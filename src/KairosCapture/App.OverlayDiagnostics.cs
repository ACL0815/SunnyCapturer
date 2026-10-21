using System;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using KairosCapture.Capture;

namespace KairosCapture;

public partial class App
{
    internal const int OverlayLogLimit = 8 * 1024 * 1024;
    internal static string DiagnosticLogDirectory => Environment.GetEnvironmentVariable("KAIROS_DIAGNOSTIC_LOG_DIRECTORY") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KairosCapture", "logs");

    internal static RegionOverlay.LogWriteResult TryLogOverlayTraceSnapshot(ImmutableArray<string> rows)
    {
        if (Current is App { _diagnostics: true }) return new(RegionOverlay.LogWriteStatus.Suppressed);
        return AppendOverlayTraceSnapshot(DiagnosticLogDirectory, rows);
    }

    internal static RegionOverlay.LogWriteResult AppendOverlayTraceSnapshot(string directory, ImmutableArray<string> rows)
    {
        try
        {
            if (rows.IsDefaultOrEmpty) return new(RegionOverlay.LogWriteStatus.Failed, 87);
            var batch = new StringBuilder();
            long byteCount = 0;
            foreach (string row in rows)
            {
                byteCount += Encoding.UTF8.GetByteCount(row) + Environment.NewLine.Length;
                if (byteCount > OverlayLogLimit || row.Contains('\r') || row.Contains('\n'))
                    return new(RegionOverlay.LogWriteStatus.Failed, 87);
                batch.AppendLine(row);
            }
            byte[] bytes = Encoding.UTF8.GetBytes(batch.ToString());
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
