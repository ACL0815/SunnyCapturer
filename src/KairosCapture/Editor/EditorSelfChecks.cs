using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KairosCapture.Editor;

/// <summary>Offline checks. Call on the WPF UI thread (or another STA thread).</summary>
public static class EditorSelfChecks
{
    public static void Run()
    {
        Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Editor checks require STA.");
        byte[] pixels = new byte[128 * 96 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
            pixels[i + 3] = 128;
        }
        BitmapSource source = BitmapSource.Create(128, 96, 144, 144, PixelFormats.Bgra32, null, pixels, 128 * 4);
        AnnotationCanvas canvas = new(source);
        Require(!source.IsFrozen, "The caller's image must not be frozen by the editor.");
        Require(canvas.Width == 128 && canvas.Height == 96 && !canvas.HasChanges && !canvas.CanUndo,
            "Initial size/state must use native pixels, including 144-DPI sources.");

        // Negative drag; opaque redaction must ignore even a transparent selected brush.
        canvas.Stroke = Brushes.Transparent;
        Draw(canvas, EditorTool.Redact, new Point(40.3, 40.8), new Point(10.2, 10.1));
        BitmapSource redacted = canvas.Flatten();
        Require(redacted.IsFrozen && redacted.PixelWidth == 128 && redacted.PixelHeight == 96,
            "Flatten must return a frozen bitmap at native dimensions.");
        Require(IsBlack(redacted, 10, 10) && IsBlack(redacted, 39, 39) && IsBlack(redacted, 40, 40),
            "Redaction must cover whole pixels in opaque black, including fractional edges.");
        Require(!IsBlack(redacted, 5, 5) && canvas.CanUndo && canvas.HasChanges,
            "Redaction must be local and flatten must preserve undo history.");

        // Selection, real object move and move history, with clamping at the image edge.
        canvas.Tool = EditorTool.Select;
        canvas.BeginGesture(new Point(20, 20));
        canvas.UpdateGesture(new Point(70, 20));
        canvas.EndGesture();
        Require(!IsBlack(canvas.Flatten(), 20, 20) && IsBlack(canvas.Flatten(), 70, 20), "Select must move objects.");
        canvas.Undo();
        Require(IsBlack(canvas.Flatten(), 20, 20) && !IsBlack(canvas.Flatten(), 70, 20), "Undo move failed.");
        canvas.Redo();
        Require(IsBlack(canvas.Flatten(), 70, 20), "Redo move failed.");
        canvas.BeginGesture(new Point(70, 20));
        canvas.UpdateGesture(new Point(500, 500));
        canvas.EndGesture();
        Require(IsBlack(canvas.Flatten(), 127, 95), "Move must clamp to the native image bounds.");
        canvas.DeleteSelected();
        Require(!IsBlack(canvas.Flatten(), 127, 95), "Delete selection failed.");
        canvas.Undo();
        Require(IsBlack(canvas.Flatten(), 127, 95), "Undo delete failed.");
        canvas.Redo();
        Require(!IsBlack(canvas.Flatten(), 127, 95), "Redo delete failed.");

        // A canceled move and a sub-pixel shape must leave no phantom history.
        AnnotationCanvas small = new(source);
        Draw(small, EditorTool.Rectangle, new Point(10, 10), new Point(10.2, 10.2));
        Require(!small.CanUndo && !small.HasChanges, "A tiny drag must be discarded.");
        Draw(small, EditorTool.Redact, new Point(5, 5), new Point(25, 25));
        small.Tool = EditorTool.Select;
        small.BeginGesture(new Point(10, 10));
        small.UpdateGesture(new Point(60, 60));
        small.EndGesture(true);
        Require(IsBlack(small.Flatten(), 10, 10) && !IsBlack(small.Flatten(), 60, 60), "Escape must restore a moved object.");
        small.Undo();
        Require(!small.HasChanges && !small.CanUndo && small.CanRedo, "Undo must reach the untouched baseline.");
        small.Redo();
        Require(IsBlack(small.Flatten(), 10, 10), "Redo add failed.");

        // Enter text through the actual in-canvas TextBox; select/double-click to edit it again.
        AnnotationCanvas text = new(source) { Stroke = Brushes.Black, Tool = EditorTool.Text };
        text.BeginGesture(new Point(4, 4));
        TextBox editor = ActiveText(text);
        editor.Text = "A";
        Require(text.HasChanges, "Pending text must mark the document changed.");
        byte[] first = Bytes(text.Flatten()); // Flatten commits pending text.
        Require(text.Children.Count == 0 && text.CanUndo, "Flatten must commit and remove the input chrome.");
        text.Tool = EditorTool.Select;
        text.BeginGesture(new Point(8, 8), 2);
        ActiveText(text).Text = "B";
        text.CommitText();
        byte[] second = Bytes(text.Flatten());
        Require(!Equal(first, second), "Editing existing text must change exported glyphs.");
        text.Undo();
        Require(Equal(first, Bytes(text.Flatten())), "Undo text edit failed.");
        text.Redo();
        Require(Equal(second, Bytes(text.Flatten())), "Redo text edit failed.");
        text.Undo();
        text.Undo();
        Require(!text.HasChanges && !text.CanUndo, "Text add must undo independently from text edits.");
        text.Redo();
        Require(Equal(first, Bytes(text.Flatten())), "Redo text add failed.");

        // Export with and without selected editor chrome must be byte-identical, as must PNG dimensions.
        text.BeginGesture(new Point(8, 8));
        text.EndGesture();
        Require(Equal(first, Bytes(text.Flatten())), "Selection chrome leaked into the export.");
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(redacted));
        using MemoryStream png = new();
        encoder.Save(png);
        png.Position = 0;
        BitmapFrame decoded = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Require(decoded.PixelWidth == 128 && decoded.PixelHeight == 96, "PNG dimensions shrank with image DPI.");
        Require(Equal(pixels, Bytes(source)), "Editing must never change the base image pixels.");

        // Exercise every remaining geometry through the same gesture path and its history.
        foreach (EditorTool tool in new[] { EditorTool.Arrow, EditorTool.Rectangle, EditorTool.Ellipse,
            EditorTool.Pen, EditorTool.Highlight })
        {
            AnnotationCanvas shape = new(source) { Stroke = Brushes.Black };
            Draw(shape, tool, new Point(20, 20), new Point(60, 50));
            byte[] drawn = Bytes(shape.Flatten());
            shape.Undo();
            Require(!Equal(drawn, Bytes(shape.Flatten())) && !shape.HasChanges, $"{tool} add/undo failed.");
            shape.Redo();
            Require(Equal(drawn, Bytes(shape.Flatten())), $"{tool} redo failed.");
        }
    }

    private static void Draw(AnnotationCanvas canvas, EditorTool tool, Point start, Point end)
    {
        canvas.Tool = tool;
        canvas.BeginGesture(start);
        canvas.UpdateGesture(end);
        canvas.EndGesture();
    }

    private static TextBox ActiveText(AnnotationCanvas canvas)
    {
        Require(canvas.Children.Count == 1 && canvas.Children[0] is TextBox, "Expected an in-canvas TextBox.");
        return (TextBox)canvas.Children[0];
    }

    private static bool IsBlack(BitmapSource image, int x, int y)
    {
        byte[] pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[0] == 0 && pixel[1] == 0 && pixel[2] == 0 && pixel[3] == 255;
    }

    private static byte[] Bytes(BitmapSource image)
    {
        byte[] result = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(result, image.PixelWidth * 4, 0);
        return result;
    }

    private static bool Equal(byte[] left, byte[] right) => left.AsSpan().SequenceEqual(right);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
