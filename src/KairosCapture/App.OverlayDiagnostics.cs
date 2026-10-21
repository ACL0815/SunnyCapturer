using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using KairosCapture.Capture;

namespace KairosCapture;

public partial class App
{
    internal const int OverlayLogLimit = 1024 * 1024;
    internal static string DiagnosticLogDirectory => Environment.GetEnvironmentVariable("KAIROS_DIAGNOSTIC_LOG_DIRECTORY") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KairosCapture", "logs");

    internal static RegionOverlay.LogWriteResult TryLogOverlayTraceRow(string row)
    {
        if (Current is App { _diagnostics: true }) return new(RegionOverlay.LogWriteStatus.Suppressed);
        return AppendOverlayTraceRow(DiagnosticLogDirectory, row);
    }

    internal static RegionOverlay.LogWriteResult AppendOverlayTraceRow(string directory, string row)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(row + Environment.NewLine);
            if (bytes.Length > OverlayLogLimit || row.Contains('\r') || row.Contains('\n'))
                return new(RegionOverlay.LogWriteStatus.Failed, 87);
            if (string.IsNullOrWhiteSpace(directory)) return new(RegionOverlay.LogWriteStatus.Failed, 87);
            string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())));
            using var gate = new Mutex(false, @"Global\KairosOverlayLog-" + key);
            bool acquired;
            try { acquired = gate.WaitOne(100); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return new(RegionOverlay.LogWriteStatus.Failed, 1460);
            try
            {
                Directory.CreateDirectory(target);
                string path = Path.Combine(target, "overlay-diagnostics.log");
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > OverlayLogLimit)
                    File.Move(path, path + ".previous", true);
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                stream.Seek(0, SeekOrigin.End);
                stream.Write(bytes);
                stream.Flush();
                return new(RegionOverlay.LogWriteStatus.Written);
            }
            finally { gate.ReleaseMutex(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
            System.Security.SecurityException or WaitHandleCannotBeOpenedException or System.ComponentModel.Win32Exception)
        { return new(RegionOverlay.LogWriteStatus.Failed, error.HResult); }
    }
}
