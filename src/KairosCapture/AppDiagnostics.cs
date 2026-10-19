using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KairosCapture.Capture;
using KairosCapture.Editor;
using KairosCapture.Services;

namespace KairosCapture;

internal static class AppDiagnostics
{
    private sealed record CheckResult(string Name, string Status, long DurationMs, IReadOnlyList<string> Errors);
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Network access is disabled in diagnostics mode.");
    }

    internal static OpenAiService CreateIsolatedService() => new(new HttpClient(new OfflineHandler()),
        new OpenAiStorage(Path.Combine(Path.GetTempPath(), "KairosCapture-diagnostics-" + Guid.NewGuid().ToString("N"))),
        _ => throw new InvalidOperationException("Browser access is disabled in diagnostics mode."), true);

    internal static int Run(string[] args)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1])) return 2;
        string previewPath = Path.GetFullPath(args[1]);
        if (args[0] is "--render-preview" or "--render-settings-preview") { RenderPreview(previewPath); return 0; }
        if (args[0] == "--render-overlay-preview") { RenderOverlayPreview(previewPath); return 0; }
        if (args[0] == "--render-ocr-preview") { RenderOcrPreview(previewPath); return 0; }
        if (args[0] == "--render-tray-preview") { RenderTrayPreview(previewPath); return 0; }
        if (args[0] is not ("--self-test" or "--self-check")) return 2;
        string directory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(directory);
        var results = new List<CheckResult>();
        RunCheck(results, "capture", () => { CaptureSelfChecks.Run(); return Array.Empty<string>(); });
        RunCheck(results, "editor", () => { EditorSelfChecks.Run(); return Array.Empty<string>(); });
        RunCheck(results, "overlay", () => { OverlaySelfChecks.Run(); return Array.Empty<string>(); });
        Type? oauth = typeof(App).Assembly.GetType("KairosCapture.Services.OpenAiSelfChecks");
        MethodInfo? run = oauth?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        if (run is null)
            results.Add(new CheckResult("openai", "missing", 0, new[] { "OpenAiSelfChecks.Run is not present; OAuth was not checked." }));
        else
            RunCheck(results, "openai", () => run.Invoke(null, null) is IReadOnlyList<string> errors ? errors :
                new[] { "OpenAiSelfChecks.Run must return IReadOnlyList<string> (errors only)." });
        RunCheck(results, "shell", CheckShell);
        RunCheck(results, "settings", CheckSettings);
        RunCheck(results, "hotkey", CheckHotkey);
        RunCheck(results, "ocr-result", CheckOcrResult);
        RunCheck(results, "tray", CheckTray);
        bool passed = results.All(result => result.Status == "passed");
        WriteJson(Path.Combine(directory, "self-test.json"), new
        {
            schemaVersion = 1,
            timestampUtc = DateTimeOffset.UtcNow,
            passed,
            exitCode = passed ? 0 : 1,
            checks = results,
            limitations = new[] { "No real desktop capture, clipboard modification, browser login or live OCR request was performed.",
                "Mixed-DPI hardware, global hotkey/tray interaction and live account access require separate verification." }
        });
        return passed ? 0 : 1;
    }

    private static void RunCheck(List<CheckResult> results, string name, Func<IReadOnlyList<string>> check)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            IReadOnlyList<string> errors = check();
            results.Add(new CheckResult(name, errors.Count == 0 ? "passed" : "failed", watch.ElapsedMilliseconds, errors));
        }
        catch (Exception exception)
        {
            Exception cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            results.Add(new CheckResult(name, "failed", watch.ElapsedMilliseconds,
                new[] { cause.GetType().Name + ": " + cause.Message }));
        }
    }

    private static IReadOnlyList<string> CheckShell()
    {
        Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Shell checks require STA.");
        var window = new MainWindow(diagnosticsMode: true);
        try
        {
            FrameworkElement content = LayoutPreview(window, 560, 660);
            Require(content.ActualWidth == 560 && content.ActualHeight == 660, "Settings must arrange at compact dimensions.");
            foreach (string name in new[] { "HotkeyBox", "FolderText", "AskSave", "SaveCopy", "AccountPicker", "ModelPicker", "AutoCopy", "StartupCheck" })
                Require(window.FindName(name) is FrameworkElement, "Settings field missing: " + name);
            Require(window.FindName("CropCanvas") == null && !window.AllowDrop, "Settings must not host a workbench or image drop target.");
            Require(((TextBox)window.FindName("HotkeyBox")).Text == "Strg+Alt+A", "Default shortcut must be visible.");
            Require(((CheckBox)window.FindName("AutoCopy")).IsChecked == true && ((CheckBox)window.FindName("AskSave")).IsChecked != true,
                "Settings UI must reflect robust defaults.");
            foreach (string name in new[] { "AskSave", "SaveCopy", "AutoCopy", "StartupCheck" })
                Require(((CheckBox)window.FindName(name)).Foreground is SolidColorBrush { Color.R: > 180, Color.G: > 180, Color.B: > 180 }, "Settings toggles must retain a readable light foreground.");
            LayoutPreview(window, 500, 550);
            Require(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "Shell checks must stay offscreen.");
            return Array.Empty<string>();
        }
        finally { window.Close(); }
    }

    private static IReadOnlyList<string> CheckSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kairos-settings-check-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, "settings.json");
        try
        {
            var defaults = AppSettings.Load(file);
            Require(defaults.Hotkey == "Strg+Alt+A" && defaults.AutoCopyOcr && !defaults.AskBeforeSaving && !defaults.SaveOnCopy &&
                defaults.SaveFolder == AppSettings.DefaultSaveFolder, "Missing settings must produce expected defaults.");
            var settings = new AppSettings { SaveFolder = Path.Combine(directory, "Bilder"), Hotkey = "Strg+Umschalt+F8", AskBeforeSaving = true,
                SaveOnCopy = true, AutoCopyOcr = false, Model = "test-model", AccountKey = "test-account", WelcomeShown = true, RecentFiles = new[] { "test.png" } };
            settings.Save(file); var loaded = AppSettings.Load(file);
            Require(loaded.SaveFolder == settings.SaveFolder && loaded.Hotkey == settings.Hotkey && loaded.AskBeforeSaving && loaded.SaveOnCopy && !loaded.AutoCopyOcr &&
                loaded.Model == "test-model" && loaded.AccountKey == "test-account" && loaded.WelcomeShown && loaded.RecentFiles.SequenceEqual(settings.RecentFiles), "Settings must roundtrip every preference.");
            settings.Save(file);
            Require(Directory.GetFiles(directory, "*.tmp").Length == 0, "Atomic settings writes must leave no temporary files.");
            foreach (string corrupt in new[] { "{broken", "null", "[]", "{\"Hotkey\":null,\"SaveFolder\":null,\"Model\":null,\"RecentFiles\":null}" })
            {
                File.WriteAllText(file, corrupt); var recovered = AppSettings.Load(file);
                Require(recovered.Hotkey == "Strg+Alt+A" && recovered.AutoCopyOcr && recovered.SaveFolder == AppSettings.DefaultSaveFolder,
                    "Broken or partial settings must fall back safely.");
            }
            BitmapSource image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
            var now = new DateTime(2026, 10, 19, 14, 5, 9);
            string saved = ImageOutput.SaveDirect(image, settings.SaveFolder, now);
            Require(saved == Path.Combine(settings.SaveFolder, "Kairos 2026-10-19 140509.png") && File.Exists(saved), "Direct save must create configured directory and exact filename.");
            string second = ImageOutput.SaveDirect(image, settings.SaveFolder, now);
            Require(second != saved && File.Exists(second) && ImageOutput.Load(saved).PixelWidth == 2, "Rapid saves must preserve both images.");
            return Array.Empty<string>();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static IReadOnlyList<string> CheckHotkey()
    {
        foreach (string text in new[] { "Ctrl+Alt+A", "Strg+Alt+A", "Control+Shift+F12", "Win+7", "Alt+F24" })
        {
            Require(HotkeyGesture.TryParse(text, out var gesture) && gesture.VirtualKey > 0 && (gesture.NativeModifiers & 0x4000) != 0, "Valid shortcut rejected: " + text);
            Require(HotkeyGesture.TryParse(gesture.ToString(), out var reparsed) && gesture == reparsed, "Hotkey format must roundtrip.");
        }
        foreach (string text in new[] { "", "A", "Shift+A", "Ctrl+Ctrl+A", "Alt+", "Ctrl+Escape", "Ctrl+None", "Ctrl+12345" })
            Require(!HotkeyGesture.TryParse(text, out _), "Invalid hotkey accepted: " + text);
        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> CheckOcrResult()
    {
        var settings = new MainWindow(diagnosticsMode: true);
        var window = new OcrResultWindow(settings, CreateDemoBitmap(), default, default, diagnostics: true);
        try
        {
            Task operation = window.RecognizeAsync();
            Require(operation.IsCompletedSuccessfully && window.HasImage && window.Status.Contains("ChatGPT", StringComparison.Ordinal), "Missing account must explain next action and retain image without network.");
            var desktop = new System.Drawing.Rectangle(-1920, -1080, 4480, 2520);
            var work = new System.Drawing.Rectangle(-1920, -1080, 1920, 1040);
            foreach (double scale in new[] { 1d, 1.5, 2, 3 })
            {
                var placed = OcrResultWindow.ResultBounds(desktop, new Int32Rect(1700, 800, 200, 200), work, scale);
                Require(work.Contains(placed) && placed.Width == Math.Min(work.Width, (int)Math.Ceiling(440 * scale)), "OCR result placement must use target DPI and stay in its working area.");
            }
            LayoutPreview(window, 440, 350);
            window.SetPreviewResult();
            Require(!window.ResultText.IsReadOnly && window.ResultText.AcceptsReturn, "Recognized text must stay editable.");
            window.ResultText.Text = "Edited locally";
            Require(window.ResultText.Text == "Edited locally", "Result text must allow local editing.");
            Require(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "OCR checks must not show a native window.");
        }
        finally { window.Close(); settings.Close(); }
        Require(!window.HasImage, "Closing the result must release its image.");
        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> CheckTray()
    {
        var settings = new MainWindow(diagnosticsMode: true);
        using var tray = new TrayMenu(settings, _ => throw new InvalidOperationException("Countdown must not start during layout."));
        try
        {
            var entries = tray.Menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().ToArray();
            Require(entries.Length == 9 && entries[0].Text!.StartsWith("Bereich aufnehmen", StringComparison.Ordinal) && entries[1].Text == "Vollbild aufnehmen" &&
                entries[2].DropDownItems.Count == 3 && entries[3].Text == "Text erkennen…" && entries[4].Text == "Letzte Aufnahme fortsetzen" &&
                entries[5].Text == "Zuletzt gespeichert" && entries[6].Text!.StartsWith("OCR:", StringComparison.Ordinal) && entries[7].Text == "Einstellungen…" && entries[8].Text == "Beenden", "Tray menu must expose all requested actions in order.");
            tray.Refresh(false, false); Require(!entries[4].Enabled && entries.Take(4).All(i => i.Enabled), "Resume must be disabled without a session.");
            tray.Refresh(true, true); Require(entries.Take(5).All(i => !i.Enabled) && entries.Skip(5).All(i => i.Enabled), "Capture entries must disable during active capture.");
            tray.Refresh(false, true); Require(entries.Take(5).All(i => i.Enabled) && entries.All(i => i.Image != null), "Idle tray must expose resumed session and glyph icons.");
            using var icon = TrayMenu.RenderGlyph(settings, "Capture", 40);
            Require(icon.Width == 40 && icon.Height == 40, "Tray glyphs must render at requested DPI resolution.");
            Require(!tray.Menu.Visible && !settings.IsVisible, "Tray checks must not display menu or settings.");
            return Array.Empty<string>();
        }
        finally { settings.Close(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RenderOverlayPreview(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        BitmapSource bitmap = OverlaySelfChecks.RenderPreview(CreateDemoBitmap());
        File.WriteAllBytes(path, ImageOutput.EncodePng(bitmap));
        WriteJson(Path.ChangeExtension(path, ".json"), new { schemaVersion = 1, rendered = true,
            width = bitmap.PixelWidth, height = bitmap.PixelHeight,
            mode = "WPF live overlay / synthetic local image / offscreen only / no desktop capture" });
    }

    internal static void RenderPreview(string path)
    {
        var window = new MainWindow(diagnosticsMode: true);
        try { RenderWindow(window, path, 560, 760); }
        finally { window.Close(); }
    }
    private static void RenderOcrPreview(string path)
    {
        var settings = new MainWindow(diagnosticsMode: true);
        var window = new OcrResultWindow(settings, CreateDemoBitmap(), default, default, diagnostics: true);
        try { window.SetPreviewResult(); RenderWindow(window, path, 440, 350); }
        finally { window.Close(); settings.Close(); }
    }
    private static void RenderTrayPreview(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var settings = new MainWindow(diagnosticsMode: true);
        using var tray = new TrayMenu(settings, _ => { });
        try
        {
            tray.Refresh(false, true);
            tray.Menu.PerformLayout();
            System.Drawing.Size size = tray.Menu.GetPreferredSize(System.Drawing.Size.Empty);
            tray.Menu.Size = size;
            using var bitmap = new System.Drawing.Bitmap(size.Width, size.Height);
            tray.Menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, size));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            WriteJson(Path.ChangeExtension(path, ".json"), new { rendered = true, mode = "WinForms tray menu / offscreen / no NotifyIcon", width = size.Width, height = size.Height });
        }
        finally { settings.Close(); }
    }
    private static void RenderWindow(Window window, string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FrameworkElement content = LayoutPreview(window, width, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext dc = background.RenderOpen()) dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(content);
        File.WriteAllBytes(path, ImageOutput.EncodePng(bitmap));
        WriteJson(Path.ChangeExtension(path, ".json"), new { rendered = true, width, height, mode = "WPF / isolated offscreen diagnostics / native DWM backdrop requires visible Windows window" });
    }
    private static FrameworkElement LayoutPreview(Window window, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        return content;
    }

    private static void WriteJson(string path, object value) => File.WriteAllText(path,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    internal static BitmapSource CreateDemoBitmap()
    {
        const int width = 1280, height = 800;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            Brush ink = Brush("#252733"), muted = Brush("#737481"), purple = Brush("#7D4BE3");
            drawing.DrawRectangle(Brush("#FAF9F6"), null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(Brush("#EFEDF2"), null, new Rect(0, 0, 246, height));
            drawing.DrawRectangle(Brush("#E6E1ED"), null, new Rect(245, 0, 1, height));
            Text(drawing, "K", 40, 35, 31, purple, true);
            Text(drawing, "STUDIO NOTES", 84, 45, 15, ink, true);
            Text(drawing, "ARBEITSBEREICH", 36, 131, 12, muted, true);
            drawing.DrawRoundedRectangle(Brush("#E1D7F4"), null, new Rect(24, 164, 198, 46), 7, 7);
            Text(drawing, "Projektübersicht", 42, 176, 16, Brush("#431B96"), true);
            Text(drawing, "Notizen", 42, 236, 16, muted);
            Text(drawing, "Entwürfe", 42, 290, 16, muted);
            Text(drawing, "Archiv", 42, 344, 16, muted);
            Text(drawing, "Lokales Beispieldokument", 33, 731, 12, muted);
            Text(drawing, "PROJEKT  /  START", 306, 52, 12, purple, true);
            Text(drawing, "Klarheit im nächsten Ausschnitt", 306, 109, 36, ink, true);
            Text(drawing, "Eine gute Idee beginnt mit dem, was du siehst.", 308, 171, 20, muted);
            drawing.DrawLine(new Pen(Brush("#DDD9E2"), 1), new Point(306, 223), new Point(1208, 223));
            Text(drawing, "Festhalten. Markieren. Weitergeben.", 307, 264, 26, ink, true);
            Text(drawing, "01", 309, 331, 14, purple, true);
            Text(drawing, "Den richtigen Bereich auswählen", 352, 326, 20, ink, true);
            Text(drawing, "Genau den Inhalt aufnehmen, der für dein Gegenüber zählt.", 352, 361, 17, muted);
            Text(drawing, "02", 309, 428, 14, purple, true);
            Text(drawing, "Das Wesentliche sichtbar machen", 352, 423, 20, ink, true);
            Text(drawing, "Mit Pfeilen, Formen und kurzen Notizen direkt im Bild.", 352, 458, 17, muted);
            drawing.DrawRoundedRectangle(Brush("#F0E9DD"), null, new Rect(306, 541, 902, 133), 10, 10);
            drawing.DrawRectangle(Brush("#D3B576"), null, new Rect(306, 541, 4, 133));
            Text(drawing, "Ein Gedanke für den nächsten Schritt", 333, 565, 19, ink, true);
            Text(drawing, "Ein klarer Ausschnitt spart eine lange Erklärung.", 333, 604, 18, muted);
            Text(drawing, "BEISPIELBILD · OHNE BILDSCHIRMAUFNAHME ERZEUGT", 307, 735, 11, muted);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Brush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    private static void Text(DrawingContext drawing, string text, double x, double y, double size, Brush brush, bool bold = false) =>
        drawing.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("de-DE"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontStretches.Normal), size, brush, 1), new Point(x, y));
}
