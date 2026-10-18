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
        if (args[0] == "--render-preview")
        {
            RenderPreview(Path.GetFullPath(args[1]));
            return 0;
        }
        if (args[0] == "--render-overlay-preview")
        {
            RenderOverlayPreview(Path.GetFullPath(args[1]));
            return 0;
        }
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
        RunCheck(results, "handoff", CheckHandoff);
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

    private static IReadOnlyList<string> CheckHandoff()
    {
        Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Handoff checks require STA.");
        const int width = 160, height = 120;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)x;
                pixels[offset + 1] = (byte)y;
                pixels[offset + 2] = (byte)(x + y);
                pixels[offset + 3] = 255;
            }
        BitmapSource source = BitmapSource.Create(width, height, 144, 192, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();
        Int32Rect region = new(30, 20, 100, 80);
        var editor = new AnnotationCanvas(source) { Stroke = new SolidColorBrush(Color.FromRgb(211, 181, 118)), Thickness = 8 };
        byte[] original = ImageOutput.EncodePng(editor.Flatten(region));
        Draw(editor, EditorTool.Rectangle, new Point(40, 30), new Point(90, 75));
        byte[] first = ImageOutput.EncodePng(editor.Flatten(region));
        Draw(editor, EditorTool.Arrow, new Point(60, 55), new Point(100, 90));
        byte[] second = ImageOutput.EncodePng(editor.Flatten(region));
        Require(!original.SequenceEqual(first) && !first.SequenceEqual(second), "The handoff fixture must contain two distinct edits.");
        editor.Undo();
        Require(editor.CanUndo && editor.CanRedo, "The handoff fixture must contain both undo and redo history.");
        var previousHost = new Canvas();
        previousHost.Children.Add(editor);
        previousHost.Children.Remove(editor);
        var window = new MainWindow(diagnosticsMode: true);
        try
        {
            CompleteOffline(window.AcceptCaptureResultAsync(new CaptureSessionResult(CaptureCompletion.Workbench, editor, region)));
            var crop = (Canvas)window.FindName("CropCanvas");
            Require(crop.Children.Count == 1 && ReferenceEquals(crop.Children[0], editor), "Workbench handoff must adopt the exact live editor instance.");
            Require(editor.Width == width && editor.Height == height, "Handoff must keep the full physical screenshot coordinate system.");
            Require(crop.ClipToBounds && crop.Width == region.Width && crop.Height == region.Height &&
                Canvas.GetLeft(editor) == -region.X && Canvas.GetTop(editor) == -region.Y,
                "The crop host must clip to the selected physical region and offset the full editor.");
            Require(editor.Tool == EditorTool.Arrow && editor.Thickness == 8 &&
                editor.Stroke is SolidColorBrush stroke && stroke.Color == Color.FromRgb(211, 181, 118),
                "Handoff must retain the editor tool and drawing style.");
            Require(editor.CanUndo && editor.CanRedo && ImageOutput.EncodePng(editor.Flatten(region)).SequenceEqual(first),
                "Adopting the editor must preserve annotations and both history stacks.");
            editor.Redo();
            Require(ImageOutput.EncodePng(editor.Flatten(region)).SequenceEqual(second), "Redo from the overlay must work in the workbench.");
            editor.Undo();
            editor.Undo();
            Require(!editor.CanUndo && ImageOutput.EncodePng(editor.Flatten(region)).SequenceEqual(original),
                "Undo across the handoff must restore the original crop without rasterized annotations.");
            editor.Redo();
            editor.Redo();
            Require(ImageOutput.EncodePng(editor.Flatten(region)).SequenceEqual(second), "History must round-trip across handoff.");
            LayoutPreview(window, 1440, 820);
            var viewbox = (Viewbox)window.FindName("ImageViewbox");
            Require(Math.Abs(viewbox.Width / region.Width - viewbox.Height / region.Height) < 0.0001,
                "Workbench zoom must use the crop aspect ratio, not the full screenshot dimensions.");
            Require(((TextBlock)window.FindName("DimensionsText")).Text ==
                string.Format(CultureInfo.CurrentCulture, "{0:N0} × {1:N0} px", region.Width, region.Height),
                "Document dimensions must describe the selected region.");
            var rendered = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(crop);
            Require(PixelBytes(rendered).SequenceEqual(PixelBytes(editor.Flatten(region))),
                "The offscreen crop host must display exactly the exported region pixels.");

            var ocrText = (TextBox)window.FindName("OcrText");
            ocrText.Text = "Vorhandener Werkbanktext";
            string title = window.Title;
            foreach (CaptureCompletion completion in new[] { CaptureCompletion.Cancelled, CaptureCompletion.Copied, CaptureCompletion.Saved })
            {
                CompleteOffline(window.AcceptCaptureResultAsync(new CaptureSessionResult(completion)));
                Require(ReferenceEquals(crop.Children[0], editor) && window.Title == title &&
                    ocrText.Text == "Vorhandener Werkbanktext" && editor.CanUndo &&
                    ImageOutput.EncodePng(editor.Flatten(region)).SequenceEqual(second) && window.HasUnsavedDocumentChanges(),
                    completion + " must leave the existing unsaved workbench document, text and history intact.");
            }
            Require(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero,
                "Offline handoff checks must never show a window or create a native window handle.");
        }
        finally { window.Close(); }

        // Hashes must track the selected output. An edit outside the region must not dirty that output.
        var savedWindow = new MainWindow(diagnosticsMode: true);
        try
        {
            var savedEditor = new AnnotationCanvas(source) { Stroke = Brushes.Black, Thickness = 2 };
            savedWindow.LoadEditor(savedEditor, region, "Gespeichert.png", unsaved: false);
            Draw(savedEditor, EditorTool.Rectangle, new Point(2, 2), new Point(15, 15));
            Require(!savedWindow.HasUnsavedDocumentChanges(), "The saved hash must ignore edits entirely outside the crop.");
            Draw(savedEditor, EditorTool.Rectangle, new Point(60, 40), new Point(80, 70));
            Require(savedWindow.HasUnsavedDocumentChanges(), "Edits inside the crop must dirty the saved output.");
            savedEditor.Undo();
            Require(!savedWindow.HasUnsavedDocumentChanges(), "Undo must compare against the saved cropped output.");
            savedWindow.LoadImage(source, "Vollbild.png");
            var fullCrop = (Canvas)savedWindow.FindName("CropCanvas");
            Require(fullCrop.Width == width && fullCrop.Height == height && fullCrop.Children.Count == 1 &&
                Canvas.GetLeft(fullCrop.Children[0]) == 0 && Canvas.GetTop(fullCrop.Children[0]) == 0,
                "LoadImage must use the same host with the full image rectangle.");
        }
        finally { savedWindow.Close(); }

        var ocrWindow = new MainWindow(diagnosticsMode: true);
        try
        {
            var ocrEditor = new AnnotationCanvas(source);
            CompleteOffline(ocrWindow.AcceptCaptureResultAsync(new CaptureSessionResult(CaptureCompletion.Ocr, ocrEditor, region)));
            Require(ReferenceEquals(((Canvas)ocrWindow.FindName("CropCanvas")).Children[0], ocrEditor) &&
                ((TextBlock)ocrWindow.FindName("StatusText")).Text.Contains("Weiter mit ChatGPT", StringComparison.Ordinal) &&
                ((Button)ocrWindow.FindName("LoginButton")).Visibility == Visibility.Visible,
                "Explicit OCR without an account must retain the editor and invite login in the sidebar.");
            Require(!ocrWindow.IsVisible && new WindowInteropHelper(ocrWindow).Handle == IntPtr.Zero,
                "The offline OCR handoff must not show a window or start browser authentication.");
        }
        finally { ocrWindow.Close(); }
        return Array.Empty<string>();
    }

    private static void CompleteOffline(Task<bool> task)
    {
        Require(task.IsCompleted, "Offline handoff must not wait for capture, account access or user input.");
        Require(!task.GetAwaiter().GetResult(), "Offline handoff must not request a live OCR operation.");
    }

    private static void Draw(AnnotationCanvas editor, EditorTool tool, Point start, Point end)
    {
        editor.Tool = tool;
        editor.BeginGesture(start);
        editor.UpdateGesture(end);
        editor.EndGesture();
    }

    private static byte[] PixelBytes(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] bytes = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(bytes, stride, 0);
        return bytes;
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
