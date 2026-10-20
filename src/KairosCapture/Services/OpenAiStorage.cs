#nullable enable
using System;
using System.ComponentModel;
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

// One encrypted snapshot makes rotation of access/refresh tokens an atomic operation.
// ponytail: one cross-process lock for all accounts; split locks only if account throughput requires it.
internal sealed class OpenAiStorage
{
    private readonly string _directory;
    private readonly Action<string, string> _replaceFile;
    private string StatePath => Path.Combine(_directory, "openai-session.bin");

    internal OpenAiStorage(string directory, Action<string, string>? replaceFile = null)
    {
        _directory = directory;
        _replaceFile = replaceFile ?? ((source, destination) => File.Replace(source, destination, null));
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

    internal OpenAiState Load()
    {
        if (!File.Exists(StatePath))
            return new OpenAiState("urn:uuid:" + Guid.NewGuid().ToString("D"), Array.Empty<OpenAiRegistration>());
        byte[]? plain = null;
        try
        {
            if (new FileInfo(StatePath).Length > 4 * 1024 * 1024)
                throw new InvalidDataException();
            plain = OpenAiProtection.Unprotect(File.ReadAllBytes(StatePath));
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
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(state);
        byte[] encrypted;
        try { encrypted = OpenAiProtection.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temp = Path.Combine(_directory, "openai-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(encrypted, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            if (File.Exists(StatePath)) _replaceFile(temp, StatePath);
            else File.Move(temp, StatePath);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
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
