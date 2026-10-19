#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace KairosCapture.Services;

internal sealed record OpenAiIdentity(string Subject, string Email, string Name);
internal sealed record OpenAiCallback(string Code, string ClientId);

internal static class OpenAiProtocol
{
    internal const string Issuer = "https://auth.openai.com";
    internal const string Resource = "https://api.openai.com/v1";
    internal const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    internal const string DynamicClient = "dynamic_agent_client";

    internal static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string RandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));
    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    internal static string AccountKey(string clientId, string subject) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Issuer + "\0" + clientId + "\0" + subject))).ToLowerInvariant();
    internal static bool IsIssuedClient(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value != DynamicClient && value.Length <= 1024 && !value.Any(char.IsControl);

    internal static bool SameSecret(string? actual, string expected) => actual is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

    internal static OpenAiCallback ValidateCallback(Uri callback, Uri redirect, string state, string? expectedClient)
    {
        if (callback.Scheme != redirect.Scheme || callback.Authority != redirect.Authority ||
            callback.AbsolutePath != redirect.AbsolutePath || callback.UserInfo.Length != 0 ||
            callback.Fragment.Length != 0 || callback.Query.Length > 16000)
            throw new InvalidOperationException("Ungültige OpenAI-Rückrufadresse.");
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in callback.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            string value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            if (!query.TryAdd(key, value)) throw new InvalidOperationException("Doppelte OpenAI-Rückrufparameter.");
        }
        if (!query.TryGetValue("state", out string? returnedState) || !SameSecret(returnedState, state))
            throw new InvalidOperationException("OpenAI-Anmeldung konnte nicht zugeordnet werden (state). Bitte neu anmelden.");
        if (query.ContainsKey("error"))
            throw new InvalidOperationException("Die OpenAI-Anmeldung wurde abgebrochen oder nicht genehmigt.");
        query.TryGetValue("client_id", out string? returnedClient);
        string? client = returnedClient ?? expectedClient;
        if (!IsIssuedClient(client) || (expectedClient is not null && client != expectedClient))
            throw new InvalidOperationException("Die ausgegebene OpenAI-Client-ID fehlt oder passt nicht zum gewählten Konto.");
        if (!query.TryGetValue("code", out string? code) || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("OpenAI hat keinen Autorisierungscode geliefert.");
        return new OpenAiCallback(code, client!);
    }

    private static byte[] Decode(string value)
    {
        if (value.Length == 0 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new FormatException();
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
    }

    internal static JsonDocument Object(byte[] json)
    {
        JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            document.Dispose();
            throw new FormatException();
        }
        return document;
    }

    internal static string JwtKeyId(string jwt)
    {
        try
        {
            string[] parts = jwt.Split('.');
            if (jwt.Length > 65536 || parts.Length != 3) throw new FormatException();
            using JsonDocument header = Object(Decode(parts[0]));
            if (Text(header.RootElement, "alg") != "RS256" || header.RootElement.TryGetProperty("crit", out _))
                throw new FormatException();
            return RequiredText(header.RootElement, "kid");
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("Das OpenAI-ID-Token hat keinen unterstützten RS256-Signaturkopf.", ex);
        }
    }

    internal static bool HasKey(JsonElement jwks, string kid) => jwks.ValueKind == JsonValueKind.Object && jwks.TryGetProperty("keys", out JsonElement keys) &&
        keys.ValueKind == JsonValueKind.Array && keys.EnumerateArray().Any(k => Text(k, "kid") == kid);

    internal static OpenAiIdentity ValidateIdToken(string jwt, JsonElement jwks, string clientId, string nonce,
        DateTimeOffset now, bool requireNonce = true, string? expectedSubject = null)
    {
        try
        {
            if (!IsIssuedClient(clientId)) throw new FormatException();
            string kid = JwtKeyId(jwt);
            string[] parts = jwt.Split('.');
            // Reject ambiguous duplicate claims rather than allowing parser-specific last-value wins.
            using JsonDocument payload = Object(Decode(parts[1]));
            JsonElement[] matches = jwks.GetProperty("keys").EnumerateArray().Where(k => Text(k, "kid") == kid).ToArray();
            if (matches.Length != 1) throw new FormatException();
            JsonElement key = matches[0];
            if (Text(key, "kty") != "RSA" || (Text(key, "use") is { } use && use != "sig") ||
                (Text(key, "alg") is { } alg && alg != "RS256") ||
                (key.TryGetProperty("key_ops", out JsonElement ops) &&
                    (ops.ValueKind != JsonValueKind.Array || !ops.EnumerateArray().Any(o => o.ValueKind == JsonValueKind.String && o.GetString() == "verify"))))
                throw new FormatException();
            var signingKey = new JsonWebKey(key.GetRawText());
            if (signingKey.KeySize is < 2048 or > 8192) throw new FormatException();
            var validation = new TokenValidationParameters
            {
                RequireSignedTokens = true, RequireExpirationTime = true,
                ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey,
                ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 }, TryAllIssuerSigningKeys = false,
                ValidateIssuer = true, ValidIssuer = Issuer,
                ValidateAudience = true, ValidAudience = clientId, IgnoreTrailingSlashWhenValidatingAudience = false,
                ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(5),
                IncludeTokenOnFailedValidation = false, LogTokenId = false, LogValidationExceptions = false
            };
            var handler = new JsonWebTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 65536 };
            TokenValidationResult result = handler.ValidateTokenAsync(jwt, validation).ConfigureAwait(false).GetAwaiter().GetResult();
            if (!result.IsValid) throw new InvalidOperationException("ID token validation failed.", result.Exception);

            // Consume identity claims only after the maintained library verifies the signature and issuer.
            JsonElement claims = payload.RootElement;
            JsonElement audience = claims.GetProperty("aud");
            bool multiple = audience.ValueKind == JsonValueKind.Array && audience.GetArrayLength() > 1;
            bool audienceMatches = audience.ValueKind == JsonValueKind.String ? audience.GetString() == clientId :
                audience.ValueKind == JsonValueKind.Array && audience.EnumerateArray().All(a => a.ValueKind == JsonValueKind.String) &&
                audience.EnumerateArray().Any(a => a.GetString() == clientId);
            string? azp = Text(claims, "azp");
            if (!audienceMatches || (multiple && azp != clientId) || (claims.TryGetProperty("azp", out _) && azp != clientId))
                throw new FormatException();
            long current = now.ToUnixTimeSeconds();
            long expiry = claims.GetProperty("exp").GetInt64();
            long issued = claims.GetProperty("iat").GetInt64();
            if (issued <= 0 || issued > current + 5 || expiry <= current - 5 || expiry <= issued ||
                (claims.TryGetProperty("nbf", out JsonElement nbf) && (nbf.GetInt64() > current + 5 || nbf.GetInt64() > expiry)))
                throw new FormatException();
            bool hasNonce = claims.TryGetProperty("nonce", out _);
            if ((requireNonce || hasNonce) && !SameSecret(Text(claims, "nonce"), nonce)) throw new FormatException();
            string subject = RequiredText(claims, "sub");
            if (subject.Length > 1024 || (expectedSubject is not null && expectedSubject != subject)) throw new FormatException();
            return new OpenAiIdentity(subject, Text(claims, "email") ?? "", Text(claims, "name") ?? "");
        }
        catch (Exception ex) when (ex is SecurityTokenException or FormatException or JsonException or CryptographicException or
            InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        {
            throw new InvalidOperationException("Das OpenAI-ID-Token konnte nicht sicher bestätigt werden (Signatur, Konto, Zeit, Audience oder Nonce).", ex);
        }
    }

    internal static string? Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static string RequiredText(JsonElement item, string name) =>
        Text(item, name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value) ? value :
            throw new InvalidOperationException("OpenAI hat eine unvollständige Antwort geliefert (" + name + ").");

    internal static bool CanInfer(OpenAiCredentials c) => !string.IsNullOrWhiteSpace(c.AccessToken) &&
        c.Scopes.Contains("resource.invoke", StringComparer.Ordinal) && c.Scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal);

    internal static DateTimeOffset EarliestRefresh(JsonElement token)
    {
        if (!token.TryGetProperty("earliest_refresh_at", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return DateTimeOffset.MinValue;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset date)) return date;
        throw new InvalidOperationException("OpenAI hat einen ungültigen frühesten Refresh-Zeitpunkt geliefert.");
    }

    internal static async Task<string> ReadSseAsync(Stream stream, IProgress<string>? progress, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, true);
        var output = new StringBuilder();
        var data = new StringBuilder();
        string? eventName = null;
        while (true)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) throw new InvalidOperationException("Die OCR-Verbindung endete vor response.completed. Der Teiltext ist unvollständig.");
            if (line.Length + data.Length > 1024 * 1024) throw new InvalidOperationException("Das OCR-Stream-Ereignis ist zu groß.");
            if (line.Length != 0)
            {
                if (line.StartsWith(':')) continue;
                int colon = line.IndexOf(':');
                string field = colon < 0 ? line : line[..colon];
                string value = colon < 0 ? "" : line[(colon + 1)..];
                if (value.StartsWith(' ')) value = value[1..];
                if (field == "data") data.Append(value).Append('\n');
                else if (field == "event") eventName = value;
                continue;
            }
            if (data.Length == 0) { eventName = null; continue; }
            string json = data.ToString();
            data.Clear();
            if (json.Trim() == "[DONE]") throw new InvalidOperationException("Der OCR-Stream enthält keinen bestätigten Abschluss.");
            JsonDocument document;
            try { document = JsonDocument.Parse(json); }
            catch (JsonException) { throw new InvalidOperationException("OpenAI hat ein ungültiges OCR-Stream-Ereignis geliefert."); }
            using (document)
            {
                JsonElement item = document.RootElement;
                string type = RequiredText(item, "type");
                if (eventName is { Length: > 0 } && eventName != type)
                    throw new InvalidOperationException("Widersprüchliche OCR-Stream-Ereignisse.");
                eventName = null;
                switch (type)
                {
                    case "response.output_text.delta":
                        string delta = Text(item, "delta") ?? throw new InvalidOperationException("OpenAI hat einen ungültigen Textabschnitt geliefert.");
                        if (output.Length + delta.Length > 1_000_000) throw new InvalidOperationException("Der erkannte Text ist zu groß.");
                        output.Append(delta);
                        progress?.Report(delta);
                        break;
                    case "response.completed":
                        if (!item.TryGetProperty("response", out JsonElement completed) || Text(completed, "status") != "completed")
                            throw new InvalidOperationException("OpenAI hat keinen vollständigen OCR-Abschluss bestätigt.");
                        return output.ToString();
                    case "response.failed":
                    case "error":
                        JsonElement source = item.TryGetProperty("response", out JsonElement response) ? response : item;
                        JsonElement error = source.TryGetProperty("error", out JsonElement nested) ? nested : source;
                        throw new OpenAiRequestException(null, Text(error, "code"), Text(error, "param"), Text(item, "request_id"), "SSE error");
                    case "response.incomplete":
                        throw new InvalidOperationException("OpenAI hat die Texterkennung unvollständig beendet. Bitte einen kleineren Ausschnitt versuchen.");
                    case "response.cancelled":
                    case "response.canceled":
                        throw new InvalidOperationException("OpenAI hat die Texterkennung abgebrochen. Der Teiltext ist unvollständig.");
                    case "response.refusal.delta":
                    case "response.refusal.done":
                        throw new InvalidOperationException("Das gewählte Modell hat die Texterkennung abgelehnt.");
                }
            }
        }
    }
}

internal sealed class OpenAiRequestException : InvalidOperationException
{
    internal string? ErrorCode { get; }
    internal int? Status { get; }
    internal OpenAiRequestException(int? status, string? code, string? parameter, string? requestId, string shape)
        : base(MessageFor(status, Safe(code), Safe(parameter), Safe(requestId), shape))
    { ErrorCode = Safe(code); Status = status; }

    private static string? Safe(string? value) => value is { Length: > 0 and <= 160 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '[' or ']') ? value : null;

    private static string MessageFor(int? status, string? code, string? parameter, string? requestId, string shape)
    {
        string help = code switch
        {
            "insufficient_quota" or "billing_hard_limit_reached" => "Das verfügbare Guthaben oder Abrechnungslimit reicht für diese Anfrage nicht aus. Nutzung und Guthaben in ChatGPT prüfen.",
            "rate_limit_exceeded" => "Die zulässige Anfragerate wurde überschritten. Bitte später erneut versuchen.",
            "insufficient_scope" or "chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context" =>
                "Die erteilte Berechtigung erlaubt diesen Zugriff nicht. Gewähltes Konto und Freigabe zur Nutzung des ChatGPT-Plans prüfen.",
            "subscription_sharing_usage_limit_exceeded" => "Das Nutzungslimit für die App oder den ChatGPT-Plan wurde erreicht. ChatGPT Einstellungen → Nutzung prüfen.",
            "subscription_sharing_usage_unavailable" => "OpenAI konnte die verfügbare Nutzung vorübergehend nicht prüfen. Bitte später erneut versuchen; die Sitzung bleibt erhalten.",
            "subscription_sharing_user_unavailable" => "Die Konto- oder Workspace-Information ist vorübergehend nicht verfügbar. Bitte später erneut versuchen.",
            "subscription_sharing_route_not_supported" => "Dieser Endpunkt oder diese HTTP-Methode wird für die Verbindung nicht unterstützt.",
            "subscription_sharing_invalid_user" => "OpenAI konnte den Kontokontext nicht bestätigen. Gewähltes Konto und Freigabe prüfen; bei bestätigter Trennung erneut anmelden.",
            "subscription_sharing_user_not_eligible" => "Dieses Konto oder dieser Workspace darf den ChatGPT-Plan hier nicht verwenden.",
            "subscription_sharing_unsupported_capability" => "Das gewählte Modell oder der Bildinput wird für diese Verbindung nicht unterstützt.",
            "invalid_grant" or "invalid_refresh_token" or "token_expired" or "refresh_token_expired" or
                "refresh_token_invalidated" or "refresh_token_reused" => "Die OpenAI-Sitzung ist nicht mehr gültig. Bitte erneut anmelden.",
            "invalid_client" => "OpenAI hat die ausgegebene Client-Registrierung nicht akzeptiert.",
            _ => status switch
            {
                401 => "OpenAI hat die Anmeldung oder die erforderliche Freigabe nicht akzeptiert.",
                403 => "OpenAI verweigert diesen Zugriff aufgrund einer Konto-, Regions- oder Berechtigungsregel.",
                429 => "OpenAI hat ein Nutzungslimit gemeldet. Bitte später erneut versuchen.",
                >= 500 => "OpenAI ist vorübergehend nicht verfügbar. Die Kontodaten bleiben erhalten.",
                _ => "OpenAI konnte die Anfrage nicht abschließen."
            }
        };
        return help + (status.HasValue ? " HTTP " + status.Value.ToString(CultureInfo.InvariantCulture) + "." : "") +
            (code is null ? "" : " Code: " + code + ".") + (parameter is null ? "" : " Parameter: " + parameter + ".") +
            (requestId is null ? "" : " Request-ID: " + requestId + ".") + " Antwortform: " + shape + ".";
    }
}
