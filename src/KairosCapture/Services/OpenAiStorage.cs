#nullable enable
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KairosCapture.Services;

internal sealed record OpenAiCredentials(string AccessToken, string RefreshToken, string IdToken,
    string Nonce, string[] Scopes, DateTimeOffset ExpiresAt, DateTimeOffset EarliestRefreshAt);
internal sealed record OpenAiRegistration(string Key, string ClientId, string Subject, string Email,
    string Label, OpenAiCredentials? Credentials);
internal sealed record OpenAiState(string HostId, OpenAiRegistration[] Registrations, string? PendingClientId = null);
internal enum OpenAiStoragePoint { TempCreated, TempWritten, TempFlushed, TempClosed, Published }

// A flushed encrypted source survives publication failures that remove the canonical name.
// ponytail: one cross-process lock for all accounts; split locks only if account throughput requires it.
internal sealed class OpenAiStorage
{
    private readonly string _directory;
    private readonly Action<string, string> _replaceFile;
    private readonly Action<string, string> _moveFile;
    private readonly Func<long> _tickCount;
    private readonly Func<byte[], byte[]> _unprotect;
    private readonly Action<OpenAiStoragePoint, string>? _checkpoint;
    private string StatePath => Path.Combine(_directory, "openai-session.bin");
    private string ResetPath => Path.Combine(_directory, "session-reset.pending");

    internal OpenAiStorage(string directory, Action<string, string>? replaceFile = null,
        Action<string, string>? moveFile = null, Func<long>? tickCount = null,
        Action<OpenAiStoragePoint, string>? checkpoint = null, Func<byte[], byte[]>? unprotect = null)
    {
        _directory = directory;
        _replaceFile = replaceFile ?? ((source, destination) => File.Replace(source, destination, null));
        _moveFile = moveFile ?? File.Move;
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _checkpoint = checkpoint;
        _unprotect = unprotect ?? OpenAiProtection.Unprotect;
    }

    internal async Task<FileStream> LockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        long deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path.Combine(_directory, "openai-session.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                if (Environment.TickCount64 >= deadline)
                    throw new OpenAiUserException(OpenAiUserError.SessionBusy);
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }

    // Caller must hold LockAsync, including readers, so publication and cleanup cannot race recovery.
    internal OpenAiState Load()
    {
        ThrowIfResetPending();
        try { return ReadState(StatePath); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }

        if (Directory.Exists(_directory))
        {
            var candidates = Directory.EnumerateFiles(_directory, "openai-*.tmp")
                .Where(path => new FileInfo(path).Length != 0)
                .Select(path => new { Path = path, Order = TempOrder(path) })
                .OrderByDescending(candidate => candidate.Order).ToArray();
            if (candidates.Length > 0)
            {
                // Neither a legacy name nor equal orders prove which non-empty snapshot is newer.
                if (candidates.Length > 1 && (candidates.Any(candidate => candidate.Order is null) ||
                    candidates[0].Order == candidates[1].Order))
                    throw new OpenAiUserException(OpenAiUserError.CredentialsUnreadable);
                // Failure to decrypt cannot distinguish damage from a temporarily inaccessible session.
                return ReadState(candidates[0].Path);
            }
        }
        return new OpenAiState("urn:uuid:" + Guid.NewGuid().ToString("D"), Array.Empty<OpenAiRegistration>());
    }

    // Caller must hold LockAsync. The directory also records an interrupted explicit reset.
    internal string Reset(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(ResetPath);
        string[] files = Directory.EnumerateFiles(_directory, "openai-*.tmp")
            .Concat(Directory.EnumerateFiles(_directory, "openai-session.bin")).ToArray();
        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            _moveFile(file, Path.Combine(ResetPath, Path.GetFileName(file)));
        }
        ct.ThrowIfCancellationRequested();
        string backup = Path.Combine(_directory, "session-backup-" +
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        Directory.Move(ResetPath, backup);
        return backup;
    }

    private void ThrowIfResetPending()
    {
        if (Directory.Exists(ResetPath))
            throw new OpenAiUserException(OpenAiUserError.CredentialsUnreadable);
    }

    private OpenAiState ReadState(string path)
    {
        byte[]? plain = null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 4 * 1024 * 1024)
                throw new InvalidDataException();
            byte[] encrypted = new byte[checked((int)file.Length)];
            file.ReadExactly(encrypted);
            plain = _unprotect(encrypted);
            OpenAiState state = JsonSerializer.Deserialize<OpenAiState>(plain) ?? throw new InvalidDataException();
            if (string.IsNullOrWhiteSpace(state.HostId) || state.Registrations is null ||
                (state.PendingClientId is not null && !OpenAiProtocol.IsIssuedClient(state.PendingClientId)) ||
                state.Registrations.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != state.Registrations.Length)
                throw new InvalidDataException();
            foreach (OpenAiRegistration r in state.Registrations)
            {
                if (!OpenAiProtocol.IsIssuedClient(r.ClientId) || string.IsNullOrWhiteSpace(r.Subject) ||
                    r.Key != OpenAiProtocol.AccountKey(r.ClientId, r.Subject) ||
                    string.IsNullOrWhiteSpace(r.Label) || r.Email is null ||
                    (r.Credentials is { } c && (string.IsNullOrWhiteSpace(c.IdToken) ||
                        c.AccessToken is null || c.RefreshToken is null || c.Scopes is null ||
                        c.Scopes.Any(s => s is null) || string.IsNullOrWhiteSpace(c.Nonce))))
                    throw new InvalidDataException();
            }
            return state;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or InvalidDataException or ArgumentException or NullReferenceException)
        {
            throw new OpenAiUserException(OpenAiUserError.CredentialsUnreadable);
        }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    // Caller must hold LockAsync. Cancellation is honored before the atomic replacement.
    internal async Task SaveAsync(OpenAiState state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfResetPending();
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(state);
        byte[] encrypted;
        try { encrypted = OpenAiProtection.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        long order = Math.Max(DateTime.UtcNow.Ticks, checked(Directory.EnumerateFiles(_directory, "openai-*.tmp")
            .Select(path => TempOrder(path) ?? File.GetLastWriteTimeUtc(path).Ticks).DefaultIfEmpty(0).Max() + 1));
        string temp = Path.Combine(_directory, "openai-" + order.ToString("D19", CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                _checkpoint?.Invoke(OpenAiStoragePoint.TempCreated, temp);
                await file.WriteAsync(encrypted, ct).ConfigureAwait(false);
                _checkpoint?.Invoke(OpenAiStoragePoint.TempWritten, temp);
                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(true);
                _checkpoint?.Invoke(OpenAiStoragePoint.TempFlushed, temp);
            }
            _checkpoint?.Invoke(OpenAiStoragePoint.TempClosed, temp);
            long deadline = _tickCount() + 5000;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(StatePath)) _replaceFile(temp, StatePath);
                    else _moveFile(temp, StatePath);
                    _checkpoint?.Invoke(OpenAiStoragePoint.Published, temp);
                    break;
                }
                // 1176/1177 can remove the target name. The durable source must remain recoverable even if Move fails.
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 1175 or 1176 or 1177 && _tickCount() < deadline)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            CleanupTemps();
        }
    }

    private static long? TempOrder(string path)
    {
        string name = Path.GetFileName(path);
        if (name.Length > 27 && name[26] == '-' &&
            long.TryParse(name.AsSpan(7, 19), NumberStyles.None, CultureInfo.InvariantCulture, out long order))
            return order;
        return null;
    }

    private void CleanupTemps()
    {
        try
        {
            // Existence alone is insufficient. A temp may still be the only decryptable snapshot.
            _ = ReadState(StatePath);
            foreach (string temp in Directory.EnumerateFiles(_directory, "openai-*.tmp"))
                File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenAiUserException) { }
    }
}

// Native current-user DPAPI avoids a package reference in the WPF project.
internal static class OpenAiProtection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    internal static byte[] Protect(byte[] data) => Transform(data, true);
    internal static byte[] Unprotect(byte[] data) => Transform(data, false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OpenAI-Kontodaten benötigen Windows DPAPI.");
        byte[] entropyBytes = Encoding.UTF8.GetBytes("KairosCapture.OpenAI.v1");
        var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        var entropy = new Blob { Length = entropyBytes.Length, Data = Marshal.AllocHGlobal(entropyBytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            Marshal.Copy(entropyBytes, 0, entropy.Data, entropyBytes.Length);
            bool success = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            byte[] result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[input.Length], 0, input.Data, input.Length);
                Marshal.FreeHGlobal(input.Data);
            }
            Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
                LocalFree(output.Data);
            }
        }
    }
}
