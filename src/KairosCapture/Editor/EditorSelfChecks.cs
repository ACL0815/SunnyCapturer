using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KairosCapture.Capture;

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
        CheckDrawingToolSelection(source);
        CheckDrawingToolHistoryAndStyle(source);
        CheckArrowEndpoints(source);
        CheckSelectionEscapeAndRouting(source);
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
        RequireBlackArea(redacted, new Int32Rect(10, 10, 31, 31));
        Require(!IsBlack(redacted, 5, 5) && canvas.CanUndo && canvas.HasChanges,
            "Redaction must be local and flatten must preserve undo history.");

        AnnotationCanvas layered = new(source) { Stroke = Brushes.Red };
        Draw(layered, EditorTool.Redact, new Point(10, 10), new Point(60, 60));
        Draw(layered, EditorTool.Highlight, new Point(0, 0), new Point(80, 80));
        layered.HandleKey(Key.Escape); // Start an independent stroke inside the highlight.
        Draw(layered, EditorTool.Pen, new Point(0, 30), new Point(80, 30));
        RequireBlackArea(layered.Flatten(), new Int32Rect(10, 10, 50, 50));
        layered.Undo();
        layered.Redo();
        RequireBlackArea(layered.Flatten(), new Int32Rect(10, 10, 50, 50));
        layered.Tool = EditorTool.Select;
        layered.BeginGesture(new Point(40, 40));
        layered.EndGesture();
        Require(layered.SelectionBounds == new Rect(10, 10, 50, 50),
            "Hit testing must select the visible topmost redaction rather than hidden later annotations.");

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
        small.BeginGesture(new Point(15, 15));
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

        CheckResizeHandles(source);
        CheckIndependentObjects(source);
        CheckTextResizeAndReedit(source);
        CheckGestureBoundaries(source);
        CheckSelectionRendering(source);
        CheckPenAndStacking(source);
        CheckEditingBounds(source);
    }

    private static void CheckDrawingToolSelection(BitmapSource source)
    {
        foreach (EditorTool tool in new[] { EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Highlight, EditorTool.Redact })
        {
            AnnotationCanvas canvas = new(source) { Stroke = Brushes.Black };
            Draw(canvas, tool, new Point(20, 20), new Point(60, 60));
            object? identity = canvas.SelectedIdentity;
            Require(identity != null && canvas.Tool == tool && Near(canvas.SelectionBounds, new Rect(20, 20, 40, 40)),
                $"{tool} must remain active with the new object selected.");
            byte[] exported = Bytes(canvas.Flatten());
            BitmapSource ui = Render(canvas);
            Require(!Equal(exported, Bytes(ui)) && IsWhite(ui, 20, 20),
                $"{tool} must immediately show white square handles in the drawing tool.");
            Require(Equal(exported, Bytes(canvas.Flatten())), "Drawing-tool handles must never reach Flatten.");
            Require(canvas.CursorAt(new Point(60, 60)) == Cursors.SizeNWSE &&
                canvas.CursorAt(new Point(40, 40)) == Cursors.SizeAll && canvas.CursorAt(new Point(110, 80)) == Cursors.Cross,
                "Drawing-tool hover must distinguish resize, move and a new drawing.");
            canvas.BeginGesture(new Point(61, 61)); // Grab off-center without a jump.
            canvas.UpdateGesture(new Point(71, 66));
            canvas.UpdateGesture(new Point(81, 76));
            canvas.EndGesture();
            Require(ReferenceEquals(identity, canvas.SelectedIdentity) && canvas.Tool == tool &&
                Near(canvas.SelectionBounds, new Rect(20, 20, 60, 55)), $"{tool} handle must resize the selected object.");
            canvas.Undo();
            Require(Equal(exported, Bytes(canvas.Flatten())) && canvas.SelectedIdentity == null,
                "One undo must revert the entire resize and clear selection.");
            canvas.Redo();
            canvas.Tool = EditorTool.Select;
            canvas.BeginGesture(new Point(40, 40)); canvas.EndGesture();
            canvas.Tool = tool;
            Rect beforeMove = canvas.SelectionBounds;
            canvas.BeginGesture(new Point(40, 40));
            canvas.UpdateGesture(new Point(45, 44));
            canvas.UpdateGesture(new Point(50, 48));
            canvas.EndGesture();
            Require(Near(canvas.SelectionBounds, new Rect(beforeMove.TopLeft + new Vector(10, 8), beforeMove.Size)) &&
                ReferenceEquals(identity, canvas.SelectedIdentity) && canvas.Tool == tool,
                $"{tool} must move its selected object without changing the active tool.");
            canvas.HandleKey(Key.Delete);
            Require(canvas.SelectedIdentity == null && Equal(Bytes(new AnnotationCanvas(source).Flatten()), Bytes(canvas.Flatten())),
                "Delete in a drawing tool must delete the selected object.");
        }

        AnnotationCanvas pen = new(source) { Stroke = Brushes.Black };
        Draw(pen, EditorTool.Pen, new Point(20, 20), new Point(60, 50));
        pen.BeginGesture(new Point(60, 50)); pen.UpdateGesture(new Point(70, 58)); pen.EndGesture();
        Require(pen.Tool == EditorTool.Pen && Near(pen.SelectionBounds, new Rect(30, 28, 40, 30)),
            "A selected pen stroke must move as a whole in its drawing tool.");
    }

    private static void CheckDrawingToolHistoryAndStyle(BitmapSource source)
    {
        AnnotationCanvas canvas = new(source) { Stroke = Brushes.Black };
        Draw(canvas, EditorTool.Rectangle, new Point(20, 20), new Point(60, 60));
        byte[] original = Bytes(canvas.Flatten());
        canvas.BeginGesture(new Point(40, 40));
        canvas.UpdateGesture(new Point(45, 42)); canvas.UpdateGesture(new Point(50, 45)); canvas.EndGesture();
        byte[] moved = Bytes(canvas.Flatten());
        canvas.BeginGesture(canvas.SelectionBounds.BottomRight);
        canvas.UpdateGesture(new Point(80, 80)); canvas.UpdateGesture(new Point(90, 85)); canvas.EndGesture();
        byte[] resized = Bytes(canvas.Flatten());
        canvas.Undo(); Require(Equal(moved, Bytes(canvas.Flatten())), "Resize must be one undo step after move.");
        canvas.Undo(); Require(Equal(original, Bytes(canvas.Flatten())), "Move must be exactly one undo step.");
        canvas.Undo();
        Require(!canvas.CanUndo && canvas.SelectedIdentity == null && Equal(Bytes(new AnnotationCanvas(source).Flatten()), Bytes(canvas.Flatten())),
            "Undo drawing must remove the object and its selection.");
        canvas.Redo(); canvas.Redo(); canvas.Redo();
        Require(Equal(resized, Bytes(canvas.Flatten())), "Redo must preserve add/move/resize granularity.");

        AnnotationCanvas next = new(source) { Stroke = Brushes.Black };
        Draw(next, EditorTool.Rectangle, new Point(10, 10), new Point(40, 40));
        object? first = next.SelectedIdentity;
        Draw(next, EditorTool.Rectangle, new Point(80, 50), new Point(110, 80));
        Require(next.SelectedIdentity != null && !ReferenceEquals(first, next.SelectedIdentity) &&
            Near(next.SelectionBounds, new Rect(80, 50, 30, 30)), "Dragging beside selection must immediately draw a new object.");
        // Even a hit on an unselected object begins a new draft in the drawing tool.
        next.BeginGesture(new Point(25, 25)); next.UpdateGesture(new Point(25.2, 25.2)); next.EndGesture();
        Require(next.SelectedIdentity == null, "A tiny click outside the selected object must only deselect.");
        next.Undo(); next.Undo();
        Require(!next.CanUndo && Equal(Bytes(new AnnotationCanvas(source).Flatten()), Bytes(next.Flatten())), "Tiny deselection must not introduce history.");

        AnnotationCanvas style = new(source) { Stroke = Brushes.Black };
        Draw(style, EditorTool.Rectangle, new Point(20, 20), new Point(60, 60));
        byte[] black = Bytes(style.Flatten());
        style.Stroke = Brushes.Red;
        byte[] red = Bytes(style.Flatten());
        Require(!Equal(black, red) && style.Tool == EditorTool.Rectangle, "Colour must update the selected object in its drawing tool.");
        style.Thickness = 9;
        byte[] thick = Bytes(style.Flatten());
        Require(!Equal(red, thick), "Thickness must update the selected object in its drawing tool.");
        style.Undo(); Require(Equal(red, Bytes(style.Flatten())), "Thickness must undo independently.");
        style.Undo(); Require(Equal(black, Bytes(style.Flatten())), "Colour must undo independently.");
        Require(((SolidColorBrush)style.Stroke).Color == Colors.Red && style.Thickness == 9,
            "Undoing object style must retain the next object's tool defaults.");
        Draw(style, EditorTool.Rectangle, new Point(80, 50), new Point(110, 80));
        Require(Pixel(style.Flatten(), 95, 50)[2] == 255 && Pixel(style.Flatten(), 95, 50)[1] == 0,
            "The next object must use the retained drawing colour.");
    }

    private static void CheckArrowEndpoints(BitmapSource source)
    {
        AnnotationCanvas arrow = new(source) { Stroke = Brushes.Black };
        Draw(arrow, EditorTool.Arrow, new Point(20, 30), new Point(80, 30));
        byte[] original = Bytes(arrow.Flatten());
        BitmapSource ui = Render(arrow);
        Require(arrow.Tool == EditorTool.Arrow && IsWhite(ui, 20, 30) && IsWhite(ui, 80, 30),
            "An arrow must immediately show its two endpoint handles.");
        arrow.BeginGesture(new Point(20, 30)); arrow.UpdateGesture(new Point(25, 45));
        arrow.UpdateGesture(new Point(30, 60)); arrow.EndGesture();
        byte[] changed = Bytes(arrow.Flatten());
        Require(!Equal(original, changed) && IsWhite(Render(arrow), 30, 60) && IsWhite(Render(arrow), 80, 30),
            "Dragging an arrow endpoint must move that endpoint and retain the opposite one.");
        arrow.Undo(); Require(Equal(original, Bytes(arrow.Flatten())), "Arrow endpoint resize must undo in one step.");
        arrow.Redo(); Require(Equal(changed, Bytes(arrow.Flatten())), "Arrow endpoint resize must redo exactly.");
        arrow.Tool = EditorTool.Select;
        arrow.BeginGesture(new Point(55, 45)); arrow.EndGesture();
        arrow.Tool = EditorTool.Arrow;
        arrow.BeginGesture(new Point(55, 45)); arrow.UpdateGesture(new Point(65, 50)); arrow.EndGesture();
        Require(IsWhite(Render(arrow), 40, 65) && IsWhite(Render(arrow), 90, 35),
            "Moving an arrow must translate both endpoint handles.");
        arrow.BeginGesture(new Point(90, 35)); arrow.UpdateGesture(new Point(15, 70)); arrow.EndGesture();
        Require(IsWhite(Render(arrow), 15, 70) && IsWhite(Render(arrow), 40, 65),
            "Arrow endpoint handles must remain correct after moving and reversing direction.");
    }

    private static void CheckSelectionEscapeAndRouting(BitmapSource source)
    {
        var overlay = new RegionOverlay((BitmapSource)source.GetCurrentValueAsFrozen(), new System.Drawing.Rectangle(0, 0, 128, 96));
        try
        {
            overlay.SetRegion(new Int32Rect(10, 10, 110, 80));
            Draw(overlay.Editor, EditorTool.Rectangle, new Point(10, 30), new Point(50, 70));
            byte[] before = Bytes(overlay.Editor.Flatten());
            Require(overlay.Editor.HasAnnotationAt(new Point(7, 50)) &&
                !overlay.BeginFrameGesture(new Point(10, 50)),
                "An object handle overlapping a crop handle must route to the editor in a drawing tool.");
            Require(overlay.HandleSessionKey(Key.Escape) && !overlay.IsClosed && overlay.Editor.SelectedIdentity == null &&
                Equal(before, Bytes(overlay.Editor.Flatten())), "First idle Escape must only clear the object selection.");
            Require(!overlay.Editor.HandleKey(Key.Escape), "The editor must leave idle Escape to the overlay.");
            Require(overlay.HandleSessionKey(Key.Escape) && overlay.IsClosed, "Second idle Escape must close the overlay.");
        }
        finally { if (!overlay.IsClosed) overlay.Cancel(); }
    }

    public static BitmapSource RenderPreview(BitmapSource source)
    {
        AnnotationCanvas canvas = new(source) { Stroke = Brushes.MediumPurple, Thickness = 3 };
        Draw(canvas, EditorTool.Rectangle, new Point(source.PixelWidth * .23, source.PixelHeight * .29),
            new Point(source.PixelWidth * .93, source.PixelHeight * .62));
        return Render(canvas);
    }

    private static BitmapSource Render(AnnotationCanvas canvas)
    {
        canvas.Measure(new Size(canvas.Width, canvas.Height));
        canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height));
        canvas.UpdateLayout();
        RenderTargetBitmap bitmap = new((int)canvas.Width, (int)canvas.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas); bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        byte[] pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static bool IsWhite(BitmapSource image, int x, int y) => Pixel(image, x, y).AsSpan().SequenceEqual(new byte[] { 255, 255, 255, 255 });

    private static void CheckEditingBounds(BitmapSource source)
    {
        Int32Rect crop = new(30, 20, 70, 60);
        Rect bounds = new(crop.X, crop.Y, crop.Width, crop.Height);
        AnnotationCanvas canvas = new(source) { Stroke = Brushes.Black };
        Draw(canvas, EditorTool.Redact, new Point(40, 30), new Point(60, 50));
        byte[] original = Bytes(canvas.Flatten());
        object? identity = canvas.SelectedIdentity;
        canvas.SetEditingBounds(crop);
        Require(ReferenceEquals(identity, canvas.SelectedIdentity) && Equal(original, Bytes(canvas.Flatten())),
            "Setting input bounds must preserve the full export and selected object.");
        canvas.Tool = EditorTool.Select;
        canvas.BeginGesture(new Point(50, 40));
        canvas.UpdateGesture(new Point(500, 500));
        canvas.EndGesture();
        Require(Near(canvas.SelectionBounds, new Rect(80, 60, 20, 20)), "Workbench move must stop at the visible right/bottom edges.");
        canvas.BeginGesture(new Point(90, 70));
        canvas.UpdateGesture(new Point(-500, -500));
        canvas.EndGesture();
        Require(Near(canvas.SelectionBounds, new Rect(30, 20, 20, 20)), "Workbench move must stop at the visible left/top edges.");
        canvas.Undo(); canvas.Undo();
        Require(Equal(original, Bytes(canvas.Flatten())), "Bounded moves must undo to exact full-raster pixels.");
        canvas.BeginGesture(new Point(50, 40)); canvas.EndGesture();
        canvas.BeginGesture(canvas.SelectionBounds.BottomRight);
        canvas.UpdateGesture(new Point(500, 500)); canvas.EndGesture();
        Require(Near(canvas.SelectionBounds, new Rect(40, 30, 60, 50)), "Workbench resize must clamp right/bottom to the crop.");
        canvas.BeginGesture(canvas.SelectionBounds.TopLeft);
        canvas.UpdateGesture(new Point(-500, -500)); canvas.EndGesture();
        Require(Near(canvas.SelectionBounds, bounds), "Workbench resize must clamp left/top to the crop.");
        canvas.Undo(); canvas.Undo();
        Require(Equal(original, Bytes(canvas.Flatten())), "Bounded resizes must remain independently undoable.");
        canvas.Undo();
        Require(!canvas.CanUndo && canvas.CanRedo, "Setting input bounds must not introduce history.");
        Draw(canvas, EditorTool.Rectangle, new Point(2, 2), new Point(15, 15));
        Require(!canvas.CanUndo && canvas.CanRedo && !canvas.HasActiveGesture,
            "A gesture begun outside the crop must leave both history stacks intact.");
        Draw(canvas, EditorTool.Redact, new Point(50, 40), new Point(500, 500));
        Require(bounds.Contains(canvas.SelectionBounds) && Near(canvas.SelectionBounds, new Rect(50, 40, 50, 40)),
            "New shapes must clamp to the crop without rebasing coordinates.");
        byte[] full = Bytes(canvas.Flatten());
        byte[] expectedCrop = new byte[crop.Width * crop.Height * 4];
        canvas.Flatten().CopyPixels(crop, expectedCrop, crop.Width * 4, 0);
        Require(Equal(expectedCrop, Bytes(canvas.Flatten(crop))) && canvas.Width == 128 && canvas.Height == 96,
            "Editing bounds must retain full-raster dimensions and exact cropped export.");
        canvas.Undo(); canvas.Redo();
        Require(Equal(full, Bytes(canvas.Flatten())), "A bounded draft must preserve exact redo pixels.");

        AnnotationCanvas text = new(source) { Tool = EditorTool.Text, Stroke = Brushes.Black };
        text.SetEditingBounds(crop);
        text.BeginGesture(new Point(99, 79));
        TextBox box = ActiveText(text);
        Require(Canvas.GetLeft(box) >= bounds.Left && Canvas.GetTop(box) >= bounds.Top &&
            Canvas.GetLeft(box) + box.Width <= bounds.Right && Canvas.GetTop(box) + box.MaxHeight <= bounds.Bottom,
            "New text input must fit in the visible crop, including an edge click.");
        box.Text = "A\nB\nC\nD\nE";
        text.CommitText();
        Require(bounds.Contains(text.SelectionBounds), "New multiline text must remain inside the crop after commit.");
        text.Tool = EditorTool.Select;
        text.BeginGesture(text.SelectionBounds.BottomRight);
        text.UpdateGesture(new Point(500, 500)); text.EndGesture();
        Require(bounds.Contains(text.SelectionBounds), "Text resize must remain in the visible crop.");

        // Entirely and partly outside text already exists before handoff; bounds alone must never reflow it.
        AnnotationCanvas old = new(source) { Tool = EditorTool.Text, Stroke = Brushes.Black };
        old.BeginGesture(new Point(2, 2)); ActiveText(old).Text = "X"; old.CommitText();
        byte[] outside = Bytes(old.Flatten());
        old.BeginGesture(new Point(85, 65)); ActiveText(old).Text = "AB"; old.CommitText();
        object? oldIdentity = old.SelectedIdentity;
        byte[] partly = Bytes(old.Flatten());
        old.SetEditingBounds(crop);
        Require(ReferenceEquals(oldIdentity, old.SelectedIdentity) && Equal(partly, Bytes(old.Flatten())),
            "Setting bounds must not reposition or reflow pre-existing outside/partial text.");
        old.Undo();
        Require(Equal(outside, Bytes(old.Flatten())), "Setting bounds must preserve old text undo geometry.");
        old.Redo();
        Require(Equal(partly, Bytes(old.Flatten())), "Setting bounds must preserve old text redo geometry.");
        old.Undo(); old.Undo();
        Require(!old.CanUndo && old.CanRedo, "Bounds must not add a hidden history entry for outside text.");
    }

    private static void CheckResizeHandles(BitmapSource source)
    {
        // Every corner and side is exercised through the exact mouse-gesture implementation.
        (Point Start, Point End, Rect Expected)[] handles =
        {
            (new(40, 30), new(20, 10), new(20, 10, 60, 60)),
            (new(80, 30), new(100, 10), new(40, 10, 60, 60)),
            (new(80, 70), new(100, 90), new(40, 30, 60, 60)),
            (new(40, 70), new(20, 90), new(20, 30, 60, 60)),
            (new(40, 50), new(20, 50), new(20, 30, 60, 40)),
            (new(80, 50), new(100, 50), new(40, 30, 60, 40)),
            (new(60, 30), new(60, 10), new(40, 10, 40, 60)),
            (new(60, 70), new(60, 90), new(40, 30, 40, 60))
        };
        foreach (var handle in handles)
        {
            AnnotationCanvas canvas = new(source) { Stroke = Brushes.Black };
            Draw(canvas, EditorTool.Rectangle, new Point(40, 30), new Point(80, 70));
            canvas.Tool = EditorTool.Select;
            byte[] before = Bytes(canvas.Flatten());
            canvas.BeginGesture(handle.Start);
            canvas.UpdateGesture(handle.End);
            canvas.EndGesture();
            Require(Near(canvas.SelectionBounds, handle.Expected), $"Resize handle {handle.Start} used the wrong anchor.");
            byte[] after = Bytes(canvas.Flatten());
            Require(!Equal(before, after), "Resize did not change exported geometry.");
            canvas.Undo();
            Require(Equal(before, Bytes(canvas.Flatten())), "Resize undo must be pixel-exact.");
            canvas.Redo();
            Require(Equal(after, Bytes(canvas.Flatten())), "Resize redo must be pixel-exact.");
            canvas.Undo();
            canvas.Undo();
            Require(!canvas.HasChanges && !canvas.CanUndo && canvas.CanRedo,
                "Resize and add must undo independently to the baseline.");
        }

        foreach (EditorTool tool in new[] { EditorTool.Ellipse, EditorTool.Highlight, EditorTool.Redact })
        {
            AnnotationCanvas canvas = new(source) { Stroke = Brushes.Black };
            Draw(canvas, tool, new Point(20, 20), new Point(60, 50));
            canvas.Tool = EditorTool.Select;
            Rect b = canvas.SelectionBounds;
            byte[] before = Bytes(canvas.Flatten());
            canvas.BeginGesture(b.BottomRight);
            canvas.UpdateGesture(b.BottomRight + new Vector(14, 10));
            canvas.EndGesture();
            Require(Near(canvas.SelectionBounds, new Rect(b.Left, b.Top, b.Width + 14, b.Height + 10)),
                $"{tool} must resize as its own object.");
            byte[] after = Bytes(canvas.Flatten());
            Require(!Equal(before, after), $"{tool} resize did not reach Flatten.");
            canvas.Undo();
            Require(Equal(before, Bytes(canvas.Flatten())), $"{tool} resize undo failed.");
            canvas.Redo();
            Require(Equal(after, Bytes(canvas.Flatten())), $"{tool} resize redo failed.");
        }
    }

    private static void CheckIndependentObjects(BitmapSource source)
    {
        AnnotationCanvas canvas = new(source) { Stroke = Brushes.Transparent };
        Draw(canvas, EditorTool.Redact, new Point(10, 10), new Point(35, 35));
        Draw(canvas, EditorTool.Redact, new Point(80, 60), new Point(110, 85));
        canvas.Tool = EditorTool.Select;
        canvas.BeginGesture(new Point(22, 22));
        canvas.UpdateGesture(new Point(42, 22));
        canvas.EndGesture();
        BitmapSource moved = canvas.Flatten();
        Require(!IsBlack(moved, 12, 20) && IsBlack(moved, 42, 22) && IsBlack(moved, 90, 70),
            "Selecting the lower object must move only that object.");

        byte[] before = Bytes(moved);
        canvas.BeginGesture(canvas.SelectionBounds.BottomRight);
        canvas.UpdateGesture(new Point(70.4, 50.6));
        canvas.EndGesture();
        BitmapSource resized = canvas.Flatten();
        Require(IsBlack(resized, 70, 50) && IsBlack(resized, 30, 10) && !IsBlack(resized, 71, 50)
            && IsBlack(resized, 90, 70), "Resize must retain opaque, outward-rounded redaction edges.");
        RequireBlackArea(resized, new Int32Rect(30, 10, 41, 41));
        byte[] after = Bytes(resized);
        canvas.Undo();
        Require(Equal(before, Bytes(canvas.Flatten())), "Independent resize undo changed another object.");
        canvas.Redo();
        Require(Equal(after, Bytes(canvas.Flatten())), "Independent resize redo changed another object.");

        // Re-select after redo, shrink to its minimum without flipping, then expand to the image edges.
        canvas.BeginGesture(new Point(45, 30));
        canvas.EndGesture();
        canvas.BeginGesture(canvas.SelectionBounds.TopLeft);
        canvas.UpdateGesture(new Point(120, 90));
        canvas.EndGesture();
        Require(Math.Abs(canvas.SelectionBounds.Width - 2) < 0.001 && Math.Abs(canvas.SelectionBounds.Height - 2) < 0.001,
            "Dragging a handle through its anchor must clamp to a positive minimum.");
        canvas.Undo();
        canvas.BeginGesture(new Point(45, 30));
        canvas.EndGesture();
        canvas.BeginGesture(canvas.SelectionBounds.TopLeft);
        canvas.UpdateGesture(new Point(-300, -300));
        canvas.EndGesture();
        Require(canvas.SelectionBounds.TopLeft == new Point(0, 0) && IsBlack(canvas.Flatten(), 0, 0),
            "Resize must clamp to native image edges.");
        Require(!canvas.CanRedo, "A new resize must invalidate the discarded redo branch.");

        // Delete the other object and preserve stacking/order through undo and redo.
        canvas.BeginGesture(new Point(95, 72));
        canvas.EndGesture();
        byte[] both = Bytes(canvas.Flatten());
        Require(canvas.HandleKey(Key.Delete), "Delete must be handled by the canvas.");
        Require(IsBlack(canvas.Flatten(), 0, 0) && !IsBlack(canvas.Flatten(), 95, 72),
            "Delete must affect only the selected object.");
        canvas.HandleKey(Key.Z, ModifierKeys.Control);
        Require(Equal(both, Bytes(canvas.Flatten())), "Keyboard undo delete failed.");
        canvas.HandleKey(Key.Z, ModifierKeys.Control | ModifierKeys.Shift);
        Require(!IsBlack(canvas.Flatten(), 95, 72), "Keyboard redo delete failed.");
    }

    private static void CheckTextResizeAndReedit(BitmapSource source)
    {
        AnnotationCanvas canvas = new(source) { Tool = EditorTool.Text, Stroke = Brushes.Black };
        canvas.BeginGesture(new Point(8, 8));
        ActiveText(canvas).Text = "AB";
        canvas.Tool = EditorTool.Select;
        Require(canvas.Children.Count == 0, "Tool switch must commit pending text.");
        byte[] original = Bytes(canvas.Flatten());
        Rect bounds = canvas.SelectionBounds;
        canvas.BeginGesture(bounds.BottomRight);
        canvas.UpdateGesture(bounds.BottomRight + new Vector(bounds.Width / 2, bounds.Height / 2));
        canvas.EndGesture();
        Rect resizedBounds = canvas.SelectionBounds;
        Require(Near(resizedBounds, new Rect(bounds.TopLeft, new Size(bounds.Width * 1.5, bounds.Height * 1.5))),
            "Text corner resize must scale glyphs proportionally.");
        byte[] resized = Bytes(canvas.Flatten());
        Require(!Equal(original, resized), "Text resizing did not change glyph size.");
        canvas.Undo();
        Require(Equal(original, Bytes(canvas.Flatten())), "Text resize undo failed.");
        canvas.Redo();
        Require(Equal(resized, Bytes(canvas.Flatten())), "Text resize redo failed.");

        Point center = new(resizedBounds.Left + resizedBounds.Width / 2, resizedBounds.Top + resizedBounds.Height / 2);
        canvas.BeginGesture(center, 2);
        TextBox box = ActiveText(canvas);
        Require(box.LayoutTransform is ScaleTransform scale && Math.Abs(scale.ScaleX - 1.5) < 0.001
            && Math.Abs(scale.ScaleY - 1.5) < 0.001, "Reopened text input must retain the resized scale.");
        box.Text = "CD";
        canvas.Tool = EditorTool.Arrow;
        Require(canvas.Children.Count == 0, "Tool switch must also commit edited existing text.");
        byte[] edited = Bytes(canvas.Flatten());
        Require(!Equal(resized, edited), "Double-click must edit the resized text object.");
        canvas.Undo();
        Require(Equal(resized, Bytes(canvas.Flatten())), "Undo reedit must restore exact resized glyphs.");
        canvas.Redo();
        Require(Equal(edited, Bytes(canvas.Flatten())), "Redo reedit must restore exact resized glyphs.");

        canvas.Tool = EditorTool.Select;
        canvas.BeginGesture(center, 2);
        ActiveText(canvas).Text = "discard this change";
        Require(!canvas.HandleKey(Key.Delete), "Delete during text entry belongs to the TextBox.");
        Require(canvas.HandleKey(Key.Escape) && canvas.Children.Count == 0,
            "Escape must close the in-place editor.");
        Require(Equal(edited, Bytes(canvas.Flatten())), "Escape must discard a pending text reedit.");
        canvas.BeginGesture(center, 2);
        ActiveText(canvas).Text = " ";
        canvas.CommitText();
        Require(!Equal(edited, Bytes(canvas.Flatten())), "Emptying existing text must delete its object.");
        canvas.Undo();
        Require(Equal(edited, Bytes(canvas.Flatten())), "Undo empty-text deletion must restore the resized object.");
        canvas.Redo();
        canvas.Undo(); // delete
        canvas.Undo(); // reedit
        canvas.Undo(); // resize
        canvas.Undo(); // add
        Require(!canvas.HasChanges && !canvas.CanUndo, "The entire text edit chain must undo to the baseline.");

        AnnotationCanvas empty = new(source) { Tool = EditorTool.Text };
        empty.BeginGesture(new Point(10, 10));
        ActiveText(empty).Text = "cancel";
        empty.HandleKey(Key.Escape);
        Require(!empty.HasChanges && !empty.CanUndo && empty.Children.Count == 0,
            "Canceled new text must leave neither an object nor history.");
        empty.BeginGesture(new Point(10, 10));
        ActiveText(empty).Text = "  ";
        empty.CommitText();
        Require(!empty.HasChanges && !empty.CanUndo, "Whitespace-only new text must leave no phantom history.");
    }

    private static void CheckGestureBoundaries(BitmapSource source)
    {
        AnnotationCanvas canvas = new(source) { Stroke = Brushes.Black };
        Draw(canvas, EditorTool.Redact, new Point(20, 20), new Point(60, 60));
        canvas.Tool = EditorTool.Select;
        byte[] original = Bytes(canvas.Flatten());
        canvas.BeginGesture(new Point(60, 60));
        canvas.UpdateGesture(new Point(100, 80));
        canvas.HandleKey(Key.Escape);
        Require(Equal(original, Bytes(canvas.Flatten())) && canvas.SelectionBounds.IsEmpty,
            "Escape must restore an in-progress resize and clear selection.");
        canvas.BeginGesture(new Point(40, 40));
        canvas.UpdateGesture(new Point(60, 45));
        canvas.HandleKey(Key.Escape);
        Require(Equal(original, Bytes(canvas.Flatten())), "Escape must restore an in-progress move.");

        canvas.BeginGesture(new Point(40, 40));
        canvas.EndGesture();
        canvas.BeginGesture(new Point(60, 60));
        canvas.UpdateGesture(new Point(100, 80));
        canvas.Tool = EditorTool.Rectangle;
        byte[] resized = Bytes(canvas.Flatten());
        Require(!Equal(original, resized), "Tool switch must commit an in-progress resize.");
        canvas.Undo();
        Require(Equal(original, Bytes(canvas.Flatten())), "Tool-switch resize commit was not undoable.");
        canvas.Redo();
        Require(Equal(resized, Bytes(canvas.Flatten())), "Tool-switch resize commit was not redoable.");
        canvas.BeginGesture(new Point(5, 70));
        canvas.UpdateGesture(new Point(25, 90));
        canvas.Tool = EditorTool.Select;
        byte[] withShape = Bytes(canvas.Flatten());
        Require(!Equal(resized, withShape), "Tool switch must commit a shape draft.");
        canvas.Undo();
        Require(Equal(resized, Bytes(canvas.Flatten())), "Shape-draft commit must undo independently.");
        canvas.Redo();
        Require(Equal(withShape, Bytes(canvas.Flatten())), "Shape-draft commit must redo independently.");
        canvas.Tool = EditorTool.Pen;
        canvas.BeginGesture(new Point(100, 10));
        canvas.UpdateGesture(new Point(110, 20));
        canvas.HandleKey(Key.Escape);
        Require(Equal(withShape, Bytes(canvas.Flatten())), "Escape must discard an uncommitted pen stroke.");
    }

    private static void CheckSelectionRendering(BitmapSource source)
    {
        AnnotationCanvas canvas = new(source);
        Draw(canvas, EditorTool.Redact, new Point(20, 20), new Point(60, 60));
        canvas.Tool = EditorTool.Select;
        byte[] exported = Bytes(canvas.Flatten());
        canvas.Measure(new Size(128, 96));
        canvas.Arrange(new Rect(0, 0, 128, 96));
        canvas.UpdateLayout();
        RenderTargetBitmap ui = new(128, 96, 96, 96, PixelFormats.Pbgra32);
        ui.Render(canvas);
        Require(!IsBlack(ui, 20, 20) && IsBlack(canvas.Flatten(), 20, 20),
            "Selected objects must have visible resize grips excluded from Flatten.");
        Require(Equal(exported, Bytes(canvas.Flatten())), "Rendering selection handles changed the export.");
        canvas.HandleKey(Key.Escape);
        Require(Equal(exported, Bytes(canvas.Flatten())), "Clearing selection changed exported pixels.");
    }

    private static void CheckPenAndStacking(BitmapSource source)
    {
        AnnotationCanvas pen = new(source) { Stroke = Brushes.Black };
        Draw(pen, EditorTool.Pen, new Point(20, 20), new Point(60, 50));
        pen.Tool = EditorTool.Select;
        Rect bounds = pen.SelectionBounds;
        byte[] before = Bytes(pen.Flatten());
        pen.BeginGesture(bounds.BottomRight);
        pen.UpdateGesture(bounds.BottomRight + new Vector(12, 8));
        pen.EndGesture();
        Require(Near(pen.SelectionBounds, new Rect(bounds.Left + 12, bounds.Top + 8, bounds.Width, bounds.Height)),
            "Pen endpoints must move the whole stroke without presenting meaningless resize grips.");
        byte[] moved = Bytes(pen.Flatten());
        Require(!Equal(before, moved), "Pen move did not reach the exported image.");
        pen.Undo();
        Require(Equal(before, Bytes(pen.Flatten())), "Pen move undo failed.");
        pen.Redo();
        Require(Equal(moved, Bytes(pen.Flatten())), "Pen move redo failed.");

        AnnotationCanvas overlap = new(source);
        Draw(overlap, EditorTool.Redact, new Point(10, 10), new Point(60, 60));
        overlap.HandleKey(Key.Escape); // Deselect before drawing a second overlapping object.
        Draw(overlap, EditorTool.Redact, new Point(30, 30), new Point(80, 80));
        overlap.Tool = EditorTool.Select;
        byte[] both = Bytes(overlap.Flatten());
        overlap.BeginGesture(new Point(40, 40));
        overlap.UpdateGesture(new Point(60, 40));
        overlap.EndGesture();
        Require(IsBlack(overlap.Flatten(), 90, 70) && !IsBlack(overlap.Flatten(), 35, 70)
            && IsBlack(overlap.Flatten(), 20, 20), "Overlap selection must hit the frontmost independent object.");
        overlap.Undo();
        Require(Equal(both, Bytes(overlap.Flatten())), "Undo must preserve overlapping object order.");
        overlap.BeginGesture(new Point(40, 40));
        overlap.EndGesture();
        overlap.DeleteSelected();
        Require(IsBlack(overlap.Flatten(), 40, 40) && !IsBlack(overlap.Flatten(), 70, 70),
            "Deleting a front object must reveal the intact object underneath.");
        overlap.Undo();
        Require(Equal(both, Bytes(overlap.Flatten())), "Undo delete must restore the original stacking order.");

        AnnotationCanvas noops = new(source);
        Draw(noops, EditorTool.Rectangle, new Point(20, 20), new Point(60, 60));
        noops.Tool = EditorTool.Select;
        noops.BeginGesture(new Point(60, 60));
        noops.EndGesture(); // Click on the handle without a drag.
        noops.BeginGesture(new Point(60, 60));
        noops.UpdateGesture(new Point(90, 80));
        noops.EndGesture(true);
        noops.Undo();
        Require(!noops.HasChanges && !noops.CanUndo && noops.CanRedo,
            "A handle click and a canceled resize must create no phantom history.");
    }

    private static void RequireBlackArea(BitmapSource image, Int32Rect area)
    {
        byte[] pixels = new byte[area.Width * area.Height * 4];
        image.CopyPixels(area, pixels, area.Width * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
            Require(pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 0 && pixels[i + 3] == 255,
                "Every redacted native pixel must be fully opaque black.");
    }

    private static bool Near(Rect actual, Rect expected) => !actual.IsEmpty &&
        Math.Abs(actual.Left - expected.Left) < 0.001 && Math.Abs(actual.Top - expected.Top) < 0.001 &&
        Math.Abs(actual.Width - expected.Width) < 0.001 && Math.Abs(actual.Height - expected.Height) < 0.001;

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
        if (!condition) throw new SelfCheckFailure(message);
    }
}
