#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KairosCapture.Services;

public sealed record OpenAiAccount(string Key, string Label, string Email, bool IsSignedIn);
public sealed record OpenAiModel(string Slug, string DisplayName);

public sealed partial class OpenAiService : IDisposable
{
    private const string AuthorizeEndpoint = "https://auth.openai.com/api/accounts/authorize";
    private const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";
    private const string JwksEndpoint = "https://auth.openai.com/.well-known/jwks.json";
    private static readonly HttpRequestOptionsKey<OpenAiStep> StepOverride = new("Kairos.OpenAi.Step");
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly OpenAiStorage _storage;
    private readonly Action<Uri> _openBrowser;
    private readonly Action<string>? _diagnostic;
    private readonly TimeSpan _ocrTimeout;
    private readonly TimeSpan _refreshTimeout;
    private readonly TimeSpan _jsonTimeout;
    private readonly TimeSpan _loadTimeout;
    private readonly Func<OpenAiState> _readState;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _session = new();
    private CancellationTokenSource? _pendingSignIn;
    private OpenAiState? _state;
    private string? _activeKey;
    private JsonElement? _jwks;
    private DateTimeOffset _jwksAt;
    private bool _signingOut;
    private bool _disposed;
    private bool _storedSessionUnreadable;

    internal static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    public OpenAiService() : this(CreateHttpClient(), new OpenAiStorage(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KairosCapture")), null, true, App.LogOpenAiDiagnostic) { }

    internal OpenAiService(HttpClient http, OpenAiStorage storage, Action<Uri>? openBrowser = null, bool ownsHttp = false,
        Action<string>? diagnostic = null, TimeSpan? ocrTimeout = null, TimeSpan? refreshTimeout = null, TimeSpan? jsonTimeout = null,
        TimeSpan? loadTimeout = null, Func<OpenAiState>? readState = null)
    {
        _http = http;
        _storage = storage;
        _ownsHttp = ownsHttp;
        _diagnostic = diagnostic;
        _ocrTimeout = ocrTimeout ?? TimeSpan.FromMinutes(2);
        _refreshTimeout = refreshTimeout ?? TimeSpan.FromSeconds(30);
        _jsonTimeout = jsonTimeout ?? TimeSpan.FromSeconds(30);
        _loadTimeout = loadTimeout ?? TimeSpan.FromSeconds(35);
        _readState = readState ?? storage.Load;
        _openBrowser = openBrowser ?? (uri => { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); });
    }

    public bool IsLoaded { get { lock (_sync) return _state is not null; } }
    public bool StoredSessionUnreadable { get { lock (_sync) return _storedSessionUnreadable; } }
    internal string SessionDirectory => _storage.DirectoryPath;

    public async Task EnsureLoadedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_state is not null) return;
        }
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        attempt.CancelAfter(_loadTimeout);
        bool gateHeld = false;
        try
        {
            await _gate.WaitAsync(attempt.Token).ConfigureAwait(false);
            gateHeld = true;
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_state is not null) return;
            }
            // LockAsync can complete synchronously. Keep all initial file I/O off the caller's dispatcher.
            await Task.Run(async () =>
            {
                while (true)
                {
                    attempt.Token.ThrowIfCancellationRequested();
                    try
                    {
                        using (await LockStorageAsync(attempt.Token).ConfigureAwait(false))
                        {
                            OpenAiState state = LoadState();
                            attempt.Token.ThrowIfCancellationRequested();
                            lock (_sync)
                            {
                                ThrowIfDisposed();
                                _state = state;
                                _activeKey = (state.Registrations.FirstOrDefault(r => r.Credentials is not null) ?? state.Registrations.FirstOrDefault())?.Key;
                            }
                        }
                        return;
                    }
                    catch (OpenAiUserException ex) when (ex.Error == OpenAiUserError.SessionBusy)
                    {
                        await Task.Delay(100, attempt.Token).ConfigureAwait(false);
                    }
                }
            }, attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw SessionError(OpenAiUserError.SessionBusy);
        }
        finally { if (gateHeld) _gate.Release(); }
    }

    public async Task<IReadOnlyList<OpenAiAccount>> GetAccountsAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return Accounts;
    }

    // Only the explicit reset action may bypass initial loading of unreadable credentials.
    public async Task<string?> ResetStoredSessionAsync(CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        attempt.CancelAfter(_loadTimeout);
        lock (_sync)
        {
            ThrowIfDisposed();
            _pendingSignIn?.Cancel();
        }
        await _gate.WaitAsync(attempt.Token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                using (await LockStorageAsync(attempt.Token).ConfigureAwait(false))
                {
                    attempt.Token.ThrowIfCancellationRequested();
                    try
                    {
                        OpenAiState readable = LoadState();
                        lock (_sync)
                        {
                            ThrowIfDisposed();
                            _state = readable;
                            if (!readable.Registrations.Any(r => r.Key == _activeKey))
                                _activeKey = (readable.Registrations.FirstOrDefault(r => r.Credentials is not null) ??
                                    readable.Registrations.FirstOrDefault())?.Key;
                        }
                        return null;
                    }
                    catch (OpenAiUserException ex) when (ex.Error == OpenAiUserError.CredentialsUnreadable) { }
                    CancellationTokenSource previous;
                    lock (_sync)
                    {
                        ThrowIfDisposed();
                        _state = null;
                        _activeKey = null;
                        previous = _session;
                        _session = new CancellationTokenSource();
                    }
                    previous.Cancel();
                    previous.Dispose();
                    string backup = _storage.Reset(attempt.Token);
                    Publish(LoadState());
                    return backup;
                }
            }, attempt.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { ObserveStorageFailure(ex); OpenAiDiagnostics.Mark(ex, OpenAiStep.Persist); throw; }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<OpenAiAccount> Accounts
    {
        get { lock (_sync) return _state is { } state ? Array.AsReadOnly(state.Registrations.Select(Account).ToArray()) : Array.Empty<OpenAiAccount>(); }
    }

    public OpenAiAccount? ActiveAccount
    {
        get { lock (_sync) return _state?.Registrations.FirstOrDefault(r => r.Key == _activeKey) is { } r ? Account(r) : null; }
    }

    private static OpenAiAccount Account(OpenAiRegistration r) => new(r.Key, r.Label, r.Email, r.Credentials is not null);

    internal bool CanUsePlan
    {
        get { lock (_sync) return _state?.Registrations.FirstOrDefault(r => r.Key == _activeKey)?.Credentials is { } c && OpenAiProtocol.CanInfer(c); }
    }

    // The catalog's list entries support image input under the current sharing contract.
    // Preserve a valid choice; otherwise follow the server's preference order.
    internal static OpenAiModel? SelectOcrModel(IReadOnlyList<OpenAiModel> models, string current) =>
        models.FirstOrDefault(m => m.Slug == current) ?? models.FirstOrDefault();

    public async Task SelectAccountAsync(string key, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        CancellationTokenSource previous;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_signingOut) throw new OpenAiUserException(OpenAiUserError.SigningOut);
            if (!_state!.Registrations.Any(r => r.Key == key)) throw new ArgumentException("Unbekanntes OpenAI-Konto.", nameof(key));
            _activeKey = key;
            previous = _session;
            _session = new CancellationTokenSource();
        }
        previous.Cancel();
        previous.Dispose();
    }

    // Only call in response to the user's Continue with ChatGPT action.
    public async Task SignInAsync(string? accountKey, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        attempt.CancelAfter(TimeSpan.FromMinutes(5));
        bool gateHeld = false;
        OpenAiStep step = OpenAiStep.Session;
        try
        {
            await EnsureLoadedAsync(attempt.Token).ConfigureAwait(false);
            await _gate.WaitAsync(attempt.Token).ConfigureAwait(false);
            gateHeld = true;
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_signingOut) throw new OpenAiUserException(OpenAiUserError.SigningOut);
                _pendingSignIn = attempt;
            }
            OpenAiState state;
            OpenAiRegistration? selected;
            using (await LockStorageAsync(attempt.Token).ConfigureAwait(false))
            {
                state = LoadState();
                selected = accountKey is null ? null : state.Registrations.FirstOrDefault(r => r.Key == accountKey)
                    ?? throw new ArgumentException("Unbekanntes OpenAI-Konto.", nameof(accountKey));
                await SaveAsync(state, attempt.Token).ConfigureAwait(false); // Host ID exists before the browser opens.
            }
            string? expectedClient = selected?.ClientId ?? state.PendingClientId;
            step = OpenAiStep.Callback;
            string verifier = OpenAiProtocol.RandomValue();
            string nonce = OpenAiProtocol.RandomValue();
            string oauthState = OpenAiProtocol.RandomValue();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start(8);
            var redirect = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/auth/callback");
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = expectedClient ?? OpenAiProtocol.DynamicClient,
                ["ext_agent_host_id"] = state.HostId,
                ["response_type"] = "code", ["redirect_uri"] = redirect.AbsoluteUri,
                ["scope"] = OpenAiProtocol.Scope, ["resource"] = OpenAiProtocol.Resource,
                ["state"] = oauthState, ["nonce"] = nonce,
                ["code_challenge"] = OpenAiProtocol.Challenge(verifier), ["code_challenge_method"] = "S256"
            };
            if (expectedClient is null) parameters["agent_name_hint"] = "Kairos Capture";
            if (selected?.Credentials is { } previous) parameters["id_token_hint"] = previous.IdToken;
            if (selected is { Email.Length: > 0 }) parameters["login_hint"] = selected.Email;
            if (selected?.Credentials is { } existing && !OpenAiProtocol.CanInfer(existing)) parameters["prompt"] = "consent";
            var authorize = new Uri(AuthorizeEndpoint + "?" + string.Join("&", parameters.Select(p =>
                Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
            attempt.Token.ThrowIfCancellationRequested();
            try { _openBrowser(authorize); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { throw new InvalidOperationException("Der Systembrowser konnte für die OpenAI-Anmeldung nicht geöffnet werden."); }
            OpenAiCallback callback = await ListenAsync(listener, redirect, oauthState, expectedClient, attempt.Token).ConfigureAwait(false);
            listener.Stop();
            if (selected is null)
            {
                using (await LockStorageAsync(attempt.Token).ConfigureAwait(false))
                {
                    state = LoadState() with { PendingClientId = callback.ClientId };
                    await SaveAsync(state, attempt.Token).ConfigureAwait(false);
                }
            }
            step = OpenAiStep.Token;
            using JsonDocument token = await ExchangeAuthorizationCodeAsync(callback.ClientId, callback.Code, verifier, redirect, attempt.Token).ConfigureAwait(false);
            string idToken = OpenAiProtocol.RequiredText(token.RootElement, "id_token");
            step = OpenAiStep.Validate;
            JsonElement keys = await GetKeysAsync(idToken, attempt.Token).ConfigureAwait(false);
            OpenAiIdentity identity = OpenAiProtocol.ValidateIdToken(idToken, keys, callback.ClientId, nonce,
                DateTimeOffset.UtcNow, true, selected?.Subject);
            OpenAiCredentials credentials = Credentials(token.RootElement, nonce, null);
            string key = OpenAiProtocol.AccountKey(callback.ClientId, identity.Subject);
            using (await LockStorageAsync(attempt.Token).ConfigureAwait(false))
            {
                state = LoadState();
                OpenAiRegistration? old = state.Registrations.FirstOrDefault(r => r.Key == key);
                string label = old?.Label ?? Label(identity, key);
                var registration = new OpenAiRegistration(key, callback.ClientId, identity.Subject, identity.Email, label, credentials);
                state = state with { Registrations = state.Registrations.Where(r => r.Key != key).Append(registration).ToArray(),
                    PendingClientId = state.PendingClientId == callback.ClientId ? null : state.PendingClientId };
                await SaveAsync(state, attempt.Token).ConfigureAwait(false);
            }
            // SignOut reads the active account after it acquires the same gate. If it started
            // after the atomic save, it must also clear this newly validated login.
            ActivateAccount(key);
            if (!OpenAiProtocol.CanInfer(credentials))
                throw new OpenAiUserException(OpenAiUserError.PlanPermissionMissing);
            Report(OpenAiStep.Persist, OpenAiOutcome.Succeeded);
        }
        catch (Exception ex)
        {
            Exception failure = FinishFailure(ex, step, ct, IsSigningOut() ? attempt.Token : default,
                attempt.IsCancellationRequested, "Zeitüberschreitung bei der OpenAI-Anmeldung.");
            if (ReferenceEquals(failure, ex)) throw;
            throw failure;
        }
        finally
        {
            lock (_sync) { if (ReferenceEquals(_pendingSignIn, attempt)) _pendingSignIn = null; }
            if (gateHeld) _gate.Release();
        }
    }

    public async Task<string?> SignOutAsync(CancellationToken ct)
    {
        bool ownsSignOut = false;
        bool gateHeld = false;
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_signingOut) throw new OpenAiUserException(OpenAiUserError.SigningOut);
                _signingOut = true;
                ownsSignOut = true;
                _session.Cancel();
                _pendingSignIn?.Cancel();
            }
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            gateHeld = true;
            string? key;
            lock (_sync) key = _activeKey;
            using (await LockStorageAsync(CancellationToken.None).ConfigureAwait(false))
            {
                OpenAiState state = LoadState();
                OpenAiRegistration? selected = state.Registrations.FirstOrDefault(r => r.Key == key);
                string? warning = null;
                Exception? revocationFailure = null;
                if (selected?.Credentials is { } credentials)
                {
                    revocationFailure = credentials.RefreshToken.Length > 0 ?
                        await RevokeAsync(selected.ClientId, credentials.RefreshToken, ct).ConfigureAwait(false) :
                        new OpenAiUserException(OpenAiUserError.SessionExpired);
                    if (revocationFailure is not null) warning = "Lokal abgemeldet. Der Fernwiderruf wurde nicht bestätigt. Die App bei Bedarf zusätzlich in ChatGPT Einstellungen trennen.";
                    state = state with { Registrations = state.Registrations.Select(r => r.Key == key ? r with { Credentials = null } : r).ToArray() };
                    await SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                }
                // Local tokens are already gone here, so a caller cancel only ends the remote wait and must keep the warning.
                if (revocationFailure is OperationCanceledException && _lifetime.IsCancellationRequested) throw revocationFailure;
                _lifetime.Token.ThrowIfCancellationRequested();
                if (revocationFailure is null) Report(OpenAiStep.Revoke, OpenAiOutcome.Succeeded);
                // A revocation that already failed stays Failed even if the caller cancels afterwards.
                else FinishFailure(revocationFailure, OpenAiStep.Revoke, revocationFailure is OperationCanceledException ? ct : default);
                return warning;
            }
        }
        catch (Exception ex)
        {
            Exception failure = FinishFailure(ex, OpenAiStep.Revoke, ct);
            if (ReferenceEquals(failure, ex)) throw;
            throw failure;
        }
        finally
        {
            if (ownsSignOut) lock (_sync)
            {
                _session.Dispose();
                _session = new CancellationTokenSource();
                _signingOut = false;
            }
            if (gateHeld) _gate.Release();
        }
    }

    private bool IsSigningOut() { lock (_sync) return _signingOut; }

    private void ActivateAccount(string key)
    {
        CancellationTokenSource previous;
        lock (_sync)
        {
            _activeKey = key;
            previous = _session;
            _session = new CancellationTokenSource();
        }
        previous.Cancel();
        previous.Dispose();
    }

    public async Task<IReadOnlyList<OpenAiModel>> GetModelsAsync(CancellationToken ct)
    {
        CancellationTokenSource? operation = null;
        CancellationToken session = default;
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            using var scope = operation = Operation(ct, TimeSpan.FromSeconds(45), out string key, out session);
            string access = await AccessTokenAsync(key, operation.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiProtocol.Resource + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using JsonDocument json = await SendJsonAsync(request, operation.Token).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("OpenAI hat keinen gültigen Modellkatalog geliefert.");
            var result = Array.AsReadOnly(models.EnumerateArray().Where(m => OpenAiProtocol.Text(m, "visibility") == "list")
                .Select(m => new OpenAiModel(OpenAiProtocol.RequiredText(m, "slug"), OpenAiProtocol.RequiredText(m, "display_name"))).ToArray());
            operation.Token.ThrowIfCancellationRequested();
            Report(OpenAiStep.Models, OpenAiOutcome.Succeeded);
            return result;
        }
        catch (Exception ex)
        {
            Exception failure = FinishFailure(ex, OpenAiStep.Models, ct, session, operation?.IsCancellationRequested == true,
                "Die OpenAI-Modellliste konnte nicht rechtzeitig geladen werden.");
            if (ReferenceEquals(failure, ex)) throw;
            throw failure;
        }
    }

    public Task<string> ExtractTextAsync(byte[] png, string model, IProgress<string>? progress, CancellationToken ct, string? targetLanguage = null) =>
        ExtractTextCoreAsync(png, model, progress, ct, targetLanguage);

    internal async Task<string> ExtractTextCoreAsync(byte[] png, string model, IProgress<string>? progress, CancellationToken ct,
        string? targetLanguage, OpenAiOcrTrace? trace = null)
    {
        CancellationTokenSource? operation = null;
        CancellationToken session = default;
        int? status = null;
        try
        {
            string instruction = OpenAiOcr.Instruction(targetLanguage);
            ArgumentNullException.ThrowIfNull(png);
            if (png.Length is < 33 or > 16 * 1024 * 1024 ||
                !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                throw new ArgumentException("OCR benötigt ein PNG mit höchstens 16 MiB.", nameof(png));
            uint width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
            uint height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
            if (width == 0 || height == 0 || (ulong)width * height > 64_000_000)
                throw new ArgumentException("Der PNG-Ausschnitt darf höchstens 64 Millionen Pixel enthalten.", nameof(png));
            if (string.IsNullOrWhiteSpace(model) || model.Length > 256 || model.Any(char.IsControl))
                throw new ArgumentException("Bitte ein Modell aus der OpenAI-Modellliste wählen.", nameof(model));
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            using var scope = operation = Operation(ct, _ocrTimeout, out string key, out session);
            string access = await AccessTokenAsync(key, operation.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiProtocol.Resource + "/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model, store = false, stream = true,
                instructions = instruction,
                input = new[] { new { role = "user", content = new object[] {
                    new { type = "input_text", text = OpenAiOcr.Request(targetLanguage) },
                    new { type = "input_image", image_url = "data:image/png;base64," + Convert.ToBase64String(png) }
                } } }
            }), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await SendAsync(request, operation.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            byte[]? captured = null;
            if (trace is not null)
            {
                trace.Response(response);
                captured = await ReadBodyAsync(response, operation.Token).ConfigureAwait(false);
                trace.Body(captured, response.Content.Headers.ContentType?.MediaType);
            }
            if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, operation.Token, captured).ConfigureAwait(false);
            if (response.Content.Headers.ContentType?.MediaType is { } mediaType &&
                !mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
                throw new OpenAiOcrProtocolException(OpenAiOcrReason.ContentType);
            await using Stream stream = captured is null ? await response.Content.ReadAsStreamAsync(operation.Token).ConfigureAwait(false) : new MemoryStream(captured);
            string result = await OpenAiProtocol.ReadSseAsync(stream, progress, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            Report(OpenAiStep.Ocr, OpenAiOutcome.Succeeded, status: status);
            return result;
        }
        catch (Exception ex)
        {
            Exception failure = ex switch
            {
                IOException when status is not null => new OpenAiOcrProtocolException(OpenAiOcrReason.ConnectionInterrupted, ex),
                DecoderFallbackException => new OpenAiOcrProtocolException(OpenAiOcrReason.InvalidEncoding, ex),
                _ => ex
            };
            OpenAiStep step = OpenAiDiagnostics.Step(ex) is OpenAiStep.Unknown ? OpenAiStep.Ocr : OpenAiDiagnostics.Step(ex);
            OpenAiDiagnostics.Mark(failure, step, status);
            failure = FinishFailure(failure, step, ct, session, operation?.IsCancellationRequested == true,
                step == OpenAiStep.Session ?
                    "Die OpenAI-Anmeldung ist noch beschäftigt. Bitte die laufende Anmeldung abschließen und erneut versuchen." :
                    "Die Texterkennung hat das Zeitlimit von zwei Minuten überschritten. Bitte einen kleineren Ausschnitt versuchen.");
            if (ReferenceEquals(failure, ex)) throw;
            throw failure;
        }
    }

    private CancellationTokenSource Operation(CancellationToken ct, TimeSpan timeout, out string key, out CancellationToken session)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_signingOut) throw SessionError(OpenAiUserError.SigningOut);
            if (_activeKey is null) throw SessionError(OpenAiUserError.SignedOut);
            key = _activeKey;
            session = _session.Token;
            var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token, session);
            operation.CancelAfter(timeout);
            return operation;
        }
    }

    private async Task<string> AccessTokenAsync(string key, CancellationToken ct)
    {
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { OpenAiDiagnostics.Mark(ex, OpenAiStep.Session); throw; }
        try
        {
            using (await LockStorageAsync(ct).ConfigureAwait(false))
            {
                OpenAiState state = LoadState();
                Publish(state);
                OpenAiRegistration r = state.Registrations.FirstOrDefault(r => r.Key == key)
                    ?? throw SessionError(OpenAiUserError.SignedOut);
                OpenAiCredentials c = r.Credentials ?? throw SessionError(OpenAiUserError.SessionExpired);
                if (!OpenAiProtocol.CanInfer(c)) throw SessionError(OpenAiUserError.PlanPermissionMissing);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (c.ExpiresAt > now.AddMinutes(1) || (now < c.EarliestRefreshAt && c.ExpiresAt > now)) return c.AccessToken;
                if (now < c.EarliestRefreshAt) throw SessionError(OpenAiUserError.RefreshTooEarly);
                if (c.RefreshToken.Length == 0) throw SessionError(OpenAiUserError.SessionExpired);
                using var rotation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                rotation.CancelAfter(_refreshTimeout);
                try
                {
                    // Once rotation starts, finish reading and persisting its result even when
                    // the caller changes accounts or cancels. Never retry an ambiguous rotation.
                    using var request = Form(TokenEndpoint, new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token", ["client_id"] = r.ClientId,
                        ["refresh_token"] = c.RefreshToken, ["resource"] = OpenAiProtocol.Resource
                    });
                    using JsonDocument token = await SendJsonAsync(request, rotation.Token).ConfigureAwait(false);
                    if (OpenAiProtocol.Text(token.RootElement, "id_token") is { } id)
                        OpenAiProtocol.ValidateIdToken(id, await GetKeysAsync(id, rotation.Token).ConfigureAwait(false), r.ClientId,
                            c.Nonce, DateTimeOffset.UtcNow, false, r.Subject);
                    OpenAiCredentials replacement = Credentials(token.RootElement, c.Nonce, c);
                    state = state with { Registrations = state.Registrations.Select(item => item.Key == key ? item with { Credentials = replacement } : item).ToArray() };
                    // A successful rotation must survive cancellation after the server consumed the old token.
                    await SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                    if (!OpenAiProtocol.CanInfer(replacement)) throw new OpenAiUserException(OpenAiUserError.PlanPermissionRevoked);
                    ct.ThrowIfCancellationRequested();
                    return replacement.AccessToken;
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && rotation.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                {
                    var timeout = new TimeoutException("Die OpenAI-Anmeldung konnte nicht rechtzeitig erneuert werden. Bitte erneut versuchen oder die Anmeldung erneuern.", ex);
                    OpenAiDiagnostics.Mark(timeout, OpenAiStep.Refresh);
                    throw timeout;
                }
                catch (OpenAiRequestException ex) when (ex.ErrorCode is "invalid_grant" or "invalid_refresh_token" or "token_expired" or
                    "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused")
                {
                    await SaveAsync(state with { Registrations = state.Registrations.Select(item => item.Key == key ? item with { Credentials = null } : item).ToArray() }, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex) { OpenAiDiagnostics.Mark(ex, OpenAiStep.Refresh); throw; }
            }
        }
        finally { _gate.Release(); }
    }

    private static OpenAiCredentials Credentials(JsonElement token, string nonce, OpenAiCredentials? previous)
    {
        if (!string.Equals(OpenAiProtocol.Text(token, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OpenAI hat keinen Bearer-Token geliefert.");
        string id = TokenText(token, "id_token") ?? previous?.IdToken ?? throw new InvalidOperationException("Das OpenAI-ID-Token fehlt.");
        string scope = TokenText(token, "scope") ?? (previous is null ? throw new InvalidOperationException("Die gewährten OpenAI-Berechtigungen fehlen.") : string.Join(' ', previous.Scopes));
        string[] scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (!scopes.Contains("openid", StringComparer.Ordinal)) throw new InvalidOperationException("OpenAI hat die angeforderte Identitätsberechtigung nicht gewährt.");
        string access = TokenText(token, "access_token") ?? "";
        string refresh = TokenText(token, "refresh_token") ?? "";
        if ((scopes.Contains("resource.invoke", StringComparer.Ordinal) && scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal) && access.Length == 0) ||
            ((previous is not null || scopes.Contains("offline_access", StringComparer.Ordinal)) && refresh.Length == 0) ||
            (previous is not null && refresh == previous.RefreshToken))
            throw new OpenAiUserException(OpenAiUserError.IncompleteTokenSet);
        long expiry = token.TryGetProperty("expires_in", out JsonElement expires) && expires.TryGetInt64(out long seconds) ? seconds : 0;
        if (access.Length > 0 && expiry is <= 0 or > 604800) throw new InvalidOperationException("Die OpenAI-Token-Laufzeit ist ungültig.");
        return new OpenAiCredentials(access, refresh, id, nonce, scopes, DateTimeOffset.UtcNow.AddSeconds(expiry), OpenAiProtocol.EarliestRefresh(token));
    }

    private static string? TokenText(JsonElement token, string name)
    {
        if (!token.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > 65536 || text.Any(char.IsControl))
            throw new InvalidOperationException("OpenAI hat einen ungültigen Token-Parameter geliefert (" + name + ").");
        return text;
    }

    private async Task<JsonElement> GetKeysAsync(string idToken, CancellationToken ct)
    {
        string kid = OpenAiProtocol.JwtKeyId(idToken);
        bool hadCache = _jwks.HasValue && _jwksAt > DateTimeOffset.UtcNow.AddHours(-1);
        if (!hadCache || !OpenAiProtocol.HasKey(_jwks!.Value, kid))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, JwksEndpoint);
            using JsonDocument json = await SendJsonAsync(request, ct).ConfigureAwait(false);
            _jwks = json.RootElement.Clone();
            _jwksAt = DateTimeOffset.UtcNow;
        }
        return _jwks!.Value;
    }

    private async Task<Exception?> RevokeAsync(string clientId, string refresh, CancellationToken ct)
    {
        Exception? failure = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var discoveryRequest = new HttpRequestMessage(HttpMethod.Get, OpenAiProtocol.Issuer + "/.well-known/openid-configuration");
                using JsonDocument discovery = await SendJsonAsync(discoveryRequest, timeout.Token).ConfigureAwait(false);
                if (OpenAiProtocol.Text(discovery.RootElement, "issuer") != OpenAiProtocol.Issuer)
                    throw new InvalidOperationException("OpenAI hat einen unerwarteten Issuer geliefert.");
                string? endpoint = OpenAiProtocol.Text(discovery.RootElement, "revocation_endpoint");
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" ||
                    uri.Host != "auth.openai.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                    throw new InvalidOperationException("OpenAI hat keinen vertrauenswürdigen Widerrufsendpunkt geliefert.");
                using var request = Form(uri.AbsoluteUri, new Dictionary<string, string>
                { ["token"] = refresh, ["token_type_hint"] = "refresh_token", ["client_id"] = clientId });
                using HttpResponseMessage response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK) return null;
                failure = await ErrorAsync(response, timeout.Token).ConfigureAwait(false);
                OpenAiDiagnostics.Mark(failure, OpenAiStep.Revoke);
                if ((int)response.StatusCode < 500) return failure;
            }
            catch (OperationCanceledException ex)
            {
                if (ct.IsCancellationRequested || _lifetime.IsCancellationRequested) return ex;
                failure = new TimeoutException("Der OpenAI-Widerruf hat das Zeitlimit überschritten.", ex);
                OpenAiDiagnostics.Mark(failure, OpenAiDiagnostics.Step(ex) is OpenAiStep.Unknown ? OpenAiStep.Revoke : OpenAiDiagnostics.Step(ex));
                return failure;
            }
            catch (Exception ex) when (ex is TimeoutException or JsonException) { return ex; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OpenAiRequestException { Status: >= 500 }) { failure = ex; }
            catch (InvalidOperationException ex) { return ex; }
            if (attempt == 0)
            {
                try { await Task.Delay(400, ct).ConfigureAwait(false); }
                catch (OperationCanceledException ex) { return ex; }
            }
        }
        return failure;
    }

    private static HttpRequestMessage Form(string url, Dictionary<string, string> values)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(values) };
        if (values.TryGetValue("grant_type", out string? grant) && grant == "refresh_token") request.Options.Set(StepOverride, OpenAiStep.Refresh);
        return request;
    }

    internal async Task<JsonDocument> ExchangeAuthorizationCodeAsync(string clientId, string code, string verifier, Uri redirect, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = Form(TokenEndpoint, new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["client_id"] = clientId,
                ["code"] = code, ["code_verifier"] = verifier,
                ["redirect_uri"] = redirect.AbsoluteUri, ["resource"] = OpenAiProtocol.Resource
            });
            try { return await SendJsonAsync(request, ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) when (!ct.IsCancellationRequested && OpenAiDiagnostics.MayRetryToken(ex, attempt))
            {
                Report(OpenAiStep.Token, OpenAiOutcome.Retrying, ex);
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }

    private void Report(OpenAiStep step, OpenAiOutcome outcome, Exception? ex = null, int? status = null)
    {
        // Diagnostic sinks must never change authentication/rotation behavior.
        try { _diagnostic?.Invoke(OpenAiDiagnostics.Event(step, outcome, ex, status)); }
        catch (Exception) { }
    }

    private Exception FinishFailure(Exception ex, OpenAiStep fallback, CancellationToken caller, CancellationToken session = default,
        bool deadlineExpired = false, string? timeoutMessage = null)
    {
        CancellationToken cancelled = caller.IsCancellationRequested ? caller :
            _lifetime.IsCancellationRequested ? _lifetime.Token : session;
        Exception failure = cancelled.IsCancellationRequested ?
            ex is OperationCanceledException ? ex : new OperationCanceledException("Die OpenAI-Aktion wurde abgebrochen.", ex, cancelled) :
            ex is OperationCanceledException ? deadlineExpired ?
                new TimeoutException(timeoutMessage, ex) : new InvalidOperationException("Die OpenAI-Aktion wurde unerwartet unterbrochen.", ex) : ex;
        OpenAiStep step = OpenAiDiagnostics.Step(ex) is OpenAiStep.Unknown ? fallback : OpenAiDiagnostics.Step(ex);
        OpenAiDiagnostics.Mark(failure, step, OpenAiDiagnostics.Status(ex));
        Report(step, cancelled.IsCancellationRequested ? OpenAiOutcome.Cancelled : OpenAiOutcome.Failed, failure);
        return failure;
    }

    private static OpenAiUserException SessionError(OpenAiUserError error)
    {
        var ex = new OpenAiUserException(error);
        OpenAiDiagnostics.Mark(ex, OpenAiStep.Session);
        return ex;
    }
    private static OpenAiStep RequestStep(HttpRequestMessage request) => request.Options.TryGetValue(StepOverride, out OpenAiStep step) ? step : request.RequestUri?.AbsolutePath switch
    {
        "/api/accounts/oauth/token" => OpenAiStep.Token,
        "/.well-known/jwks.json" => OpenAiStep.Jwks,
        "/.well-known/openid-configuration" => OpenAiStep.Discovery,
        "/v1/models" => OpenAiStep.Models,
        "/v1/responses" => OpenAiStep.Ocr,
        _ => OpenAiStep.Revoke
    };
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        OpenAiStep step = RequestStep(request);
        Report(step, OpenAiOutcome.Started);
        try
        {
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            Report(step, OpenAiOutcome.Response, status: (int)response.StatusCode);
            return response;
        }
        catch (Exception ex) { OpenAiDiagnostics.Mark(ex, step); throw; }
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_jsonTimeout);
        int? status = null;
        try
        {
            using HttpResponseMessage response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, timeout.Token).ConfigureAwait(false);
            byte[] body = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
            return OpenAiProtocol.Object(body);
        }
        catch (Exception ex)
        {
            Exception failure = ex is OperationCanceledException && !ct.IsCancellationRequested && timeout.IsCancellationRequested ?
                new TimeoutException("OpenAI hat nicht rechtzeitig geantwortet.", ex) : ex;
            OpenAiDiagnostics.Mark(failure, RequestStep(request), status);
            if (ReferenceEquals(failure, ex)) throw;
            throw failure;
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) return bytes.ToArray();
            if (bytes.Length + count > 2 * 1024 * 1024) throw new InvalidOperationException("Die OpenAI-Antwort ist zu groß.");
            bytes.Write(buffer, 0, count);
        }
    }

    private static async Task<OpenAiRequestException> ErrorAsync(HttpResponseMessage response, CancellationToken ct, byte[]? captured = null)
    {
        string? code = null, parameter = null;
        string shape = "leer";
        byte[] body = captured ?? await ReadBodyAsync(response, ct).ConfigureAwait(false);
        if (body.Length > 0)
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(body);
                JsonElement root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out JsonElement error))
                {
                    shape = error.ValueKind == JsonValueKind.Object ? "error-Objekt" : "error-String";
                    code = error.ValueKind == JsonValueKind.String ? error.GetString() : OpenAiProtocol.Text(error, "code");
                    parameter = OpenAiProtocol.Text(error, "param");
                }
                else shape = OpenAiProtocol.Text(root, "detail") is not null ? "detail" : "JSON";
            }
            catch (JsonException) { shape = "kein JSON"; }
        }
        string? requestId = response.Headers.TryGetValues("x-request-id", out IEnumerable<string>? ids) ? ids.FirstOrDefault() :
            response.Headers.TryGetValues("openai-request-id", out ids) ? ids.FirstOrDefault() : null;
        return new OpenAiRequestException((int)response.StatusCode, code, parameter, requestId, shape);
    }

    private async Task SaveAsync(OpenAiState state, CancellationToken ct)
    {
        try { await _storage.SaveAsync(state, ct).ConfigureAwait(false); }
        catch (Exception ex) { ObserveStorageFailure(ex); OpenAiDiagnostics.Mark(ex, OpenAiStep.Persist); throw; }
        Publish(state);
    }
    private OpenAiState LoadState()
    {
        try
        {
            OpenAiState state = _readState();
            lock (_sync) _storedSessionUnreadable = false;
            return state;
        }
        catch (Exception ex) { ObserveStorageFailure(ex); OpenAiDiagnostics.Mark(ex, OpenAiStep.Persist); throw; }
    }
    private void ObserveStorageFailure(Exception ex)
    {
        if (_storage.ResetPending) OpenAiDiagnostics.MarkResetPending(ex, _storage.ResetPath);
        if (ex is OpenAiUserException { Error: OpenAiUserError.CredentialsUnreadable } ||
            (ex is not OpenAiUserException { Error: OpenAiUserError.SessionBusy } && _storage.ResetPending))
            lock (_sync) _storedSessionUnreadable = true;
    }
    private async Task<FileStream> LockStorageAsync(CancellationToken ct)
    {
        try { return await _storage.LockAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { OpenAiDiagnostics.Mark(ex, OpenAiStep.Persist); throw; }
    }
    private void Publish(OpenAiState state) { lock (_sync) _state = state; }
    private static string Label(OpenAiIdentity identity, string key)
    {
        string name = identity.Name.Length > 0 ? identity.Name : identity.Email.Length > 0 ? identity.Email : "ChatGPT-Konto";
        return name[..Math.Min(name.Length, 160)] + " · " + key[..8];
    }

    private static async Task<OpenAiCallback> ListenAsync(TcpListener listener, Uri redirect, string state, string? clientId, CancellationToken ct)
    {
        for (int stray = 0; stray < 8; stray++)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await using NetworkStream stream = client.GetStream();
                var header = new StringBuilder();
                byte[] buffer = new byte[1024];
                while (!header.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count == 0 || header.Length + count > 16384) throw new InvalidOperationException("Ungültiger OpenAI-HTTP-Rückruf.");
                    header.Append(Encoding.ASCII.GetString(buffer, 0, count));
                }
                string[] lines = header.ToString().Split("\r\n", StringSplitOptions.None);
                string[] first = lines[0].Split(' ');
                if (first.Length != 3 || first[0] != "GET" || !first[1].StartsWith("/auth/callback?", StringComparison.Ordinal) ||
                    !lines.Any(line => line.Equals("Host: " + redirect.Authority, StringComparison.OrdinalIgnoreCase)))
                {
                    await CallbackReplyAsync(stream, false, timeout.Token).ConfigureAwait(false);
                    continue;
                }
                OpenAiCallback callback;
                try { callback = OpenAiProtocol.ValidateCallback(new Uri(redirect.GetLeftPart(UriPartial.Authority) + first[1]), redirect, state, clientId); }
                catch (InvalidOperationException)
                {
                    await CallbackReplyAsync(stream, false, timeout.Token).ConfigureAwait(false);
                    throw;
                }
                await CallbackReplyAsync(stream, true, timeout.Token).ConfigureAwait(false);
                return callback;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                throw new TimeoutException("Der OpenAI-Browserrückruf hat das Zeitlimit überschritten.", ex);
            }
        }
        throw new InvalidOperationException("Zu viele ungültige OpenAI-Rückrufe. Bitte neu anmelden.");
    }

    private static async Task CallbackReplyAsync(Stream stream, bool accepted, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(accepted ? "Kairos Capture prüft die Anmeldung. Du kannst dieses Fenster schließen." : "Kairos Capture konnte diesen Anmelderückruf nicht bestätigen.");
        string header = "HTTP/1.1 " + (accepted ? "200 OK" : "400 Bad Request") + "\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _session.Cancel();
            _pendingSignIn?.Cancel();
        }
        if (_ownsHttp) _http.Dispose();
    }
}
