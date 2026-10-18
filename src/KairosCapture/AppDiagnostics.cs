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
        if (args[0] == "--render-preview")
        {
            RenderPreview(Path.GetFullPath(args[1]));
            return 0;
        }
        if (args[0] is not ("--self-test" or "--self-check")) return 2;
        string directory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(directory);
        var results = new List<CheckResult>();
        RunCheck(results, "capture", () => { CaptureSelfChecks.Run(); return Array.Empty<string>(); });
        RunCheck(results, "editor", () => { EditorSelfChecks.Run(); return Array.Empty<string>(); });
        Type? oauth = typeof(App).Assembly.GetType("KairosCapture.Services.OpenAiSelfChecks");
        MethodInfo? run = oauth?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        if (run is null)
            results.Add(new CheckResult("openai", "missing", 0, new[] { "OpenAiSelfChecks.Run is not present; OAuth was not checked." }));
        else
            RunCheck(results, "openai", () => run.Invoke(null, null) is IReadOnlyList<string> errors ? errors :
                new[] { "OpenAiSelfChecks.Run must return IReadOnlyList<string> (errors only)." });
        RunCheck(results, "shell", CheckShell);
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
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) return new[] { "Shell checks require STA." };
        var window = new MainWindow(diagnosticsMode: true);
        try
        {
            window.LoadImage(CreateDemoBitmap(), "Beispiel.png");
            FrameworkElement content = LayoutPreview(window, 1440, 820);
            if (content.ActualWidth != 1440 || content.ActualHeight != 820) return new[] { "The WPF shell did not arrange at the preview dimensions." };
            var errors = new List<string>();
            Color light = ((SolidColorBrush)window.FindResource("TextBrush")).Color;
            foreach (string name in new[] { "FileNameText", "AccountStatus" })
                if (window.FindName(name) is not TextBlock { Foreground: SolidColorBrush brush } || brush.Color != light)
                    errors.Add(name + " must inherit the light content foreground.");
            var login = (Button)window.FindName("LoginButton");
            TextBlock? loginText = Descendants<TextBlock>(login).FirstOrDefault(t => t.Text == "Weiter mit ChatGPT");
            if (loginText?.Foreground is not SolidColorBrush loginBrush || loginBrush.Color != Color.FromRgb(16, 16, 16))
                errors.Add("ChatGPT button content must inherit its own dark foreground.");
            var thickness = (ComboBox)window.FindName("ThicknessPicker");
            TextBlock? arrow = Descendants<TextBlock>(thickness).FirstOrDefault(t => t.Text == "\uE70D");
            if (arrow?.Foreground is not SolidColorBrush arrowBrush || arrowBrush.Color != light)
                errors.Add("ComboBox arrow must inherit the ComboBox foreground.");
            // Check narrow desktop layout as well; this never creates a visible window or synthesizes input.
            LayoutPreview(window, 1000, 610);
            return errors;
        }
        finally { window.Close(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    internal static void RenderPreview(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var window = new MainWindow(diagnosticsMode: true);
        try
        {
            window.LoadImage(CreateDemoBitmap(), "Beispiel.png");
            FrameworkElement content = LayoutPreview(window, 1440, 840);
            var bitmap = new RenderTargetBitmap(1440, 840, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            File.WriteAllBytes(path, ImageOutput.EncodePng(bitmap));
            WriteJson(Path.ChangeExtension(path, ".json"), new { schemaVersion = 1, rendered = true,
                width = bitmap.PixelWidth, height = bitmap.PixelHeight, mode = "WPF MainWindow / synthetic local image / isolated diagnostics" });
        }
        finally { window.Close(); }
    }

    private static FrameworkElement LayoutPreview(MainWindow window, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        window.FitDocumentForDiagnostics();
        content.UpdateLayout();
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
