using System;
using System.IO;
using System.Text.Json;
using KairosCapture.Capture;

namespace KairosCapture;

internal sealed class AppSettings
{
    internal static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KairosCapture");
    internal static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    internal static string DefaultSaveFolder
    {
        get
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures)) pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            return Path.Combine(pictures, "Kairos");
        }
    }

    public string Hotkey { get; set; } = "Strg+Alt+A";
    public string SaveFolder { get; set; } = DefaultSaveFolder;
    public bool AskBeforeSaving { get; set; }
    public bool SaveOnCopy { get; set; }
    public bool AutoCopyOcr { get; set; } = true;
    public string Model { get; set; } = "";
    public string AccountKey { get; set; } = "";
    public bool WelcomeShown { get; set; }
    public string[] RecentFiles { get; set; } = Array.Empty<string>();

    internal static AppSettings Load(string? path = null)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path ?? FilePath)) ?? new();
            value.Hotkey = HotkeyGesture.TryParse(value.Hotkey, out var gesture) ? gesture.ToString() : "Strg+Alt+A";
            if (string.IsNullOrWhiteSpace(value.SaveFolder) || !Path.IsPathFullyQualified(value.SaveFolder)) value.SaveFolder = DefaultSaveFolder;
            value.Model ??= "";
            value.AccountKey ??= "";
            value.RecentFiles ??= Array.Empty<string>();
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return new(); }
    }

    internal void Save(string? path = null)
    {
        path ??= FilePath;
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
