using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KairosCapture.Editor;
using DrawingRectangle = System.Drawing.Rectangle;

namespace KairosCapture.Capture;

/// <summary>STA only: real WPF editor, routed control events and offscreen rendering. Never shows a window.</summary>
public static class OverlaySelfChecks
{
    public static void Run()
    {
        Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Overlay checks require STA.");
        BitmapSource background = Pattern(1024, 768);
        CheckDoubleClick(background);
        CheckToolbarLayout(background);
        CheckFrame(background);
        CheckCropAndIdentity(background);
        CheckSelectedStyle(background);
        CheckRoutesAndText(background);
        CheckCompletion(background);
        CheckSaveDialog(background);
        CheckPins(background);
        CheckNativePinBounds();
        CheckResume(background);
        CheckOcrMode(background);
        CheckGestureEscape(background);
        CheckRendering(background);
    }

    private static RegionOverlay Create(BitmapSource background) => new(background,
        new DrawingRectangle(-1600, -900, background.PixelWidth, background.PixelHeight));

    private static void CheckDoubleClick(BitmapSource background)
    {
        var crop = new Int32Rect(320, 128, 488, 212);
        Point empty = new(600, 260);
        foreach (EditorTool tool in new[] { EditorTool.Select, EditorTool.Rectangle, EditorTool.Ellipse,
            EditorTool.Arrow, EditorTool.Pen, EditorTool.Highlight, EditorTool.Redact })
        {
            int copies = 0;
            BitmapSource? copied = null;
            var overlay = new RegionOverlay(background, new DrawingRectangle(-1600, -900, background.PixelWidth, background.PixelHeight),
                copyImage: image => { copies++; copied = image; });
            overlay.SetRegion(crop);
            Draw(overlay.Editor, EditorTool.Rectangle, new Point(360, 160), new Point(420, 210));
            overlay.Editor.Tool = tool;
            byte[] expected = Bytes(overlay.Editor.Flatten(crop));
            PointerClick(overlay, empty, 1);
            Require(!overlay.IsClosed && overlay.Selection == crop && !overlay.Editor.HasActiveGesture,
                "First empty click must leave a valid selection without a zero-sized draft.");
            PointerClick(overlay, empty, 2);
            Require(copies == 1 && overlay.IsClosed && overlay.Result.Completion == CaptureCompletion.Copied &&
                overlay.Result.Region == crop && ReferenceEquals(overlay.Result.Editor, overlay.Editor) &&
                copied != null && expected.SequenceEqual(Bytes(copied)) && !overlay.Editor.HasActiveGesture,
                "Double-click must use the copy completion with the exact annotated crop and retained editor.");
            overlay.HandlePointerDown(empty, 2);
            Require(copies == 1, "A closed session must never copy twice.");
        }
        foreach (EditorTool tool in new[] { EditorTool.Select, EditorTool.Text })
        {
            var overlay = Create(background);
            overlay.SetRegion(crop);
            overlay.Editor.Tool = EditorTool.Text;
            overlay.Editor.BeginGesture(new Point(380, 180));
            ((TextBox)overlay.Editor.Children[0]).Text = "Text";
            overlay.Editor.CommitText();
            overlay.Editor.Tool = tool;
            byte[] before = Bytes(overlay.Editor.Flatten());
            PointerClick(overlay, new Point(390, 190), 2);
            Require(!overlay.IsClosed && overlay.Editor.IsEditingText, "Text double-click must still edit the existing annotation.");
            ((TextBox)overlay.Editor.Children[0]).Text = "Bearbeitet";
            overlay.Editor.CommitText();
            overlay.Editor.Undo();
            Require(before.SequenceEqual(Bytes(overlay.Editor.Flatten())), "Text reedit must keep its undo history.");
            overlay.Cancel();
        }
        var failed = new RegionOverlay(background, new DrawingRectangle(0, 0, background.PixelWidth, background.PixelHeight),
            copyImage: _ => throw new InvalidOperationException("offline copy boundary"));
        failed.SetRegion(crop);
        PointerClick(failed, empty, 2);
        RemeasureToolbar(failed);
        Require(!failed.IsClosed && failed.Selection == crop && failed.ToolbarVisible && failed.ToolbarBounds.Height < 120 &&
            failed.ToolbarBounds.Top == crop.Y + crop.Height + 12, "Failed copy must leave a usable toolbar and the original selection.");
        failed.Cancel();
        var initial = Create(background);
        Require(initial.HandlePointerDown(empty, 2) && initial.IsFrameGestureActive && !initial.IsClosed,
            "A fast second press without a selection must still start a new frame.");
        initial.UpdateFrameGesture(new Point(empty.X + 80, empty.Y + 60));
        initial.EndFrameGesture();
        Require(initial.Selection == new Int32Rect((int)empty.X, (int)empty.Y, 80, 60) && !initial.IsClosed,
            "A frame dragged from a double-click press must become the selection.");
        initial.Cancel();
        foreach (bool spaceMove in new[] { false, true })
        {
            var handles = new RegionOverlay(background, new DrawingRectangle(0, 0, background.PixelWidth, background.PixelHeight),
                copyImage: _ => throw new InvalidOperationException("Handle and Space double-clicks must not copy."));
            handles.SetRegion(crop);
            Point press = spaceMove ? empty : new Point(crop.X + crop.Width, crop.Y + crop.Height);
            Require(handles.HandlePointerDown(press, 2, spaceMove) && handles.IsFrameGestureActive && !handles.IsClosed,
                spaceMove ? "Space double-click must keep moving the frame." : "Double-click on a frame handle must keep resizing.");
            handles.UpdateFrameGesture(new Point(press.X + 20, press.Y + 10));
            handles.EndFrameGesture();
            Require(!handles.IsClosed && handles.Selection == (spaceMove
                ? new Int32Rect(crop.X + 20, crop.Y + 10, crop.Width, crop.Height)
                : new Int32Rect(crop.X, crop.Y, crop.Width + 20, crop.Height + 10)),
                "Frame gestures started by a double-click must apply like single-click gestures.");
            handles.Cancel();
        }
    }

    private static void PointerClick(RegionOverlay overlay, Point point, int clickCount)
    {
        if (!overlay.HandlePointerDown(point, clickCount)) overlay.Editor.BeginGesture(point, clickCount);
        if (overlay.IsClosed) return;
        overlay.UpdateFrameGesture(point);
        overlay.EndFrameGesture();
        overlay.Editor.EndGesture();
    }

    private static void RemeasureToolbar(RegionOverlay overlay)
    {
        // A hidden/offscreen tree does not propagate every queued Source measure invalidation by itself.
        // Force the same parent measure pass that a live presentation source performs.
        ((Border)overlay.Toolbar).Child.InvalidateMeasure();
        overlay.Toolbar.InvalidateMeasure();
        overlay.SetRegion(overlay.Selection);
    }

    private static void CheckToolbarLayout(BitmapSource background)
    {
        foreach (BitmapSource source in new[] { background, BitmapSource.Create(background.PixelWidth, background.PixelHeight,
            96, 96, PixelFormats.Bgra32, null, Bytes(background), background.PixelWidth * 4) })
        foreach (EditorTool tool in new[] { EditorTool.Select, EditorTool.Rectangle })
        {
            var overlay = Create(source);
            var crop = new Int32Rect(320, 128, 488, 212);
            overlay.BeginFrameGesture(new Point(crop.X, crop.Y));
            overlay.UpdateFrameGesture(new Point(crop.X + crop.Width, crop.Y + crop.Height));
            overlay.EndFrameGesture();
            overlay.Editor.Tool = tool;
            double height = overlay.ToolbarBounds.Height;
            Require(height < 100, "Initial toolbar must be sized by its controls.");
            for (int pass = 0; pass < 4; pass++)
            {
                RemeasureToolbar(overlay);
                Layout(overlay.PreviewRoot, source.PixelWidth, source.PixelHeight);
                Require(overlay.ToolbarBounds.Height == height && overlay.Toolbar.ActualHeight == height &&
                    overlay.ToolbarBounds.Top == crop.Y + crop.Height + 12 && overlay.WindowState == WindowState.Normal && overlay.Selection == crop,
                    "Backdrop source remeasurement must not grow the glass or move the toolbar to the screen top.");
                Image glass = Descendant<Image>(overlay.Toolbar)!;
                Require(glass.Effect is System.Windows.Media.Effects.BlurEffect && glass.ActualHeight == height &&
                    glass.Source is BitmapSource bitmap && bitmap.PixelHeight == Math.Ceiling(height) &&
                    ((Border)overlay.Toolbar).Child.Clip is RectangleGeometry clip && clip.Rect.Size == overlay.ToolbarBounds.Size,
                    "Blur backdrop, source crop and rounded clip must follow the measured toolbar.");
            }
            Rect bounds = overlay.ToolbarBounds;
            Require(!overlay.HandlePointerDown(new Point(50, 50), 2) && !overlay.IsClosed && overlay.Selection == crop &&
                overlay.ToolbarBounds == bounds && !overlay.Editor.HasActiveGesture && !overlay.Editor.IsEditingText,
                "Outside double-click must leave the selection and layout untouched.");
            RemeasureToolbar(overlay);
            BitmapSource preview = RenderScene(overlay, source);
            Require(overlay.ToolbarBounds == bounds && Pixel(preview, 600, 260).SequenceEqual(Pixel(source, 600, 260)),
                "Outside double-click and later layout must keep unannotated selection pixels unobscured.");
            overlay.SetRegion(Int32Rect.Empty);
            Require(overlay.HandlePointerDown(new Point(50, 50), 2) && !overlay.ToolbarVisible && !overlay.Editor.HasActiveGesture,
                "Empty selection must keep the toolbar hidden after a double-click.");
            overlay.Cancel();
        }
    }
    private static void CheckResume(BitmapSource background)
    {
        foreach (CaptureCompletion completion in new[] { CaptureCompletion.Copied, CaptureCompletion.Saved, CaptureCompletion.Ocr, CaptureCompletion.Pinned })
        {
            var overlay = Create(background);
            Int32Rect crop = new(30, 20, 700, 430); overlay.SetRegion(crop);
            AnnotationCanvas editor = overlay.Editor;
            byte[] baseline = Bytes(editor.Flatten());
            Draw(editor, EditorTool.Rectangle, new Point(80, 70), new Point(120, 110));
            byte[] first = Bytes(editor.Flatten());
            Draw(editor, EditorTool.Ellipse, new Point(180, 170), new Point(220, 210));
            byte[] second = Bytes(editor.Flatten()); editor.Undo();
            editor.Tool = EditorTool.Select; editor.BeginGesture(new Point(100, 90)); editor.EndGesture();
            object? identity = editor.SelectedIdentity;
            byte[] expected = Bytes(editor.Flatten(crop));
            Require(overlay.ExecuteOutput(completion, image => Bytes(image).SequenceEqual(expected)), "Completion must export the crop.");
            CaptureSessionResult session = overlay.Result;
            CaptureService.Remember(session);
            Require(ReferenceEquals(CaptureService.LastSession, session), "Successful completion must retain the last session.");
            CaptureService.Remember(new(CaptureCompletion.Cancelled));
            Require(ReferenceEquals(CaptureService.LastSession, session), "Cancelled captures must not replace the last successful session.");
            Require(editor.Parent == null && VisualTreeHelper.GetParent(editor) == null && editor.Clip == null, "Completed editor must detach cleanly.");
            for (int round = 0; round < 2; round++)
            {
                var resumed = new RegionOverlay(session.Screenshot!, session.Bounds, editor: session.Editor);
                resumed.SetRegion(session.Region);
                Require(ReferenceEquals(resumed.Editor, editor) && ReferenceEquals(identity, editor.SelectedIdentity) && resumed.Selection == crop,
                    "Resume must keep object identity, frame and editor.");
                Require(editor.CanUndo && editor.CanRedo && expected.SequenceEqual(Bytes(editor.Flatten(crop))), "Resume must preserve both history stacks and pixels.");
                editor.Undo(); Require(baseline.SequenceEqual(Bytes(editor.Flatten())), "Resumed undo must reach original screen.");
                editor.Redo(); Require(first.SequenceEqual(Bytes(editor.Flatten())), "Resumed redo must restore the first object.");
                editor.Redo(); Require(second.SequenceEqual(Bytes(editor.Flatten())), "Resumed redo must retain later objects.");
                editor.Undo();
                editor.Tool = EditorTool.Select; editor.BeginGesture(new Point(100, 90)); editor.EndGesture();
                resumed.SetRegion(new Int32Rect(0, 0, background.PixelWidth, background.PixelHeight));
                Require(ReferenceEquals(identity, editor.SelectedIdentity) && first.SequenceEqual(Bytes(editor.Flatten())), "Expanding resumed frame must retain all original pixels and objects.");
                resumed.SetRegion(crop);
                Require(resumed.ExecuteOutput(completion, image => expected.SequenceEqual(Bytes(image))), "Resumed session must complete again.");
                session = resumed.Result;
            }
        }
    }

    private static void CheckOcrMode(BitmapSource background)
    {
        var overlay = new RegionOverlay(background, new DrawingRectangle(0, 0, background.PixelWidth, background.PixelHeight), ocrMode: true);
        Require(overlay.BeginFrameGesture(new Point(20, 30)), "OCR mode must begin a selection.");
        overlay.UpdateFrameGesture(new Point(400, 300)); overlay.EndFrameGesture();
        Require(overlay.IsClosed && overlay.Result.Completion == CaptureCompletion.Ocr && overlay.Result.Image?.PixelWidth == 380 && overlay.Result.Region == new Int32Rect(20, 30, 380, 270),
            "OCR mode must complete immediately on release using the selected image.");
        Require(!overlay.ExecuteOutput(CaptureCompletion.Ocr, _ => throw new InvalidOperationException("must not run")), "Closed OCR session must reject duplicate output.");
        var cancel = new RegionOverlay(background, new DrawingRectangle(0, 0, background.PixelWidth, background.PixelHeight), ocrMode: true);
        cancel.BeginFrameGesture(new Point(20, 30)); cancel.UpdateFrameGesture(new Point(400, 300));
        cancel.HandleSessionKey(Key.Escape);
        Require(!cancel.IsClosed && cancel.Selection.IsEmpty, "OCR selection Escape must discard only the active gesture.");
        cancel.HandleSessionKey(Key.Escape);
        Require(cancel.IsClosed && cancel.Result.Completion == CaptureCompletion.Cancelled, "OCR idle Escape must cancel without recognition.");
    }

    private static void CheckGestureEscape(BitmapSource background)
    {
        foreach (string gesture in new[] { "draft", "move", "resize", "frame move", "frame resize", "text" })
        {
            var overlay = Create(background);
            Int32Rect crop = new(30, 20, 700, 430);
            overlay.SetRegion(crop);
            AnnotationCanvas editor = overlay.Editor;
            Draw(editor, EditorTool.Redact, new Point(80, 70), new Point(120, 110));
            byte[] before = Bytes(editor.Flatten());
            editor.Tool = EditorTool.Select;
            object? identity = editor.SelectedIdentity;
            if (gesture == "draft")
            {
                editor.Tool = EditorTool.Rectangle;
                editor.BeginGesture(new Point(180, 170)); editor.UpdateGesture(new Point(250, 230));
            }
            else if (gesture == "move" || gesture == "resize")
            {
                editor.BeginGesture(gesture == "move" ? new Point(100, 90) : editor.SelectionBounds.BottomRight);
                editor.UpdateGesture(new Point(250, 230));
            }
            else if (gesture.StartsWith("frame", StringComparison.Ordinal))
            {
                Require(overlay.BeginFrameGesture(gesture == "frame move" ? new Point(300, 300) : new Point(30, 20)),
                    "Escape frame fixture must begin a gesture.");
                overlay.UpdateFrameGesture(new Point(200, 180));
            }
            else
            {
                editor.Tool = EditorTool.Text;
                editor.BeginGesture(new Point(200, 180));
                ((TextBox)editor.Children[0]).Text = "discard only this draft";
                Require(!overlay.HandleSessionKey(Key.Escape), "TextBox must retain its own Escape handler.");
                editor.HandleKey(Key.Escape);
            }
            if (gesture != "text") Require(overlay.HandleSessionKey(Key.Escape), "Gesture Escape must be handled.");
            Require(!overlay.IsClosed && !editor.HasActiveGesture && !editor.IsEditingText && !overlay.IsMouseCaptured &&
                !editor.IsMouseCaptured && overlay.Selection == crop && before.SequenceEqual(Bytes(editor.Flatten())),
                gesture + " Escape must restore the prior pixels/crop and release capture while retaining the session.");
            editor.Undo();
            Require(!editor.CanUndo && !editor.HasChanges && editor.CanRedo, gesture + " Escape must not add a history entry.");
            editor.Redo();
            editor.Tool = EditorTool.Select;
            editor.BeginGesture(new Point(100, 90)); editor.EndGesture();
            Require(ReferenceEquals(identity, editor.SelectedIdentity) && before.SequenceEqual(Bytes(editor.Flatten())),
                gesture + " Escape must retain the original object and exact redo pixels.");
            Require(overlay.HandleSessionKey(Key.Escape) && !overlay.IsClosed && editor.SelectedIdentity == null &&
                overlay.HandleSessionKey(Key.Escape) && overlay.IsClosed, "Idle Escape must deselect first, then cancel the session.");
        }
        var raw = new RegionOverlay(background, new DrawingRectangle(0, 0, background.PixelWidth, background.PixelHeight), editing: false);
        raw.BeginFrameGesture(new Point(30, 20)); raw.UpdateFrameGesture(new Point(200, 180));
        Require(raw.HandleSessionKey(Key.Escape) && raw.IsClosed && raw.Selection.IsEmpty &&
            raw.Result.Completion == CaptureCompletion.Cancelled, "Raw capture Escape must still cancel immediately during selection.");
    }

    private static void CheckFrame(BitmapSource background)
    {
        var overlay = Create(background);
        Require(overlay.BeginFrameGesture(new Point(100, 100)), "Initial down must begin selection.");
        overlay.UpdateFrameGesture(new Point(300, 260));
        overlay.EndFrameGesture();
        Require(!overlay.IsClosed && overlay.ToolbarVisible && overlay.Selection == new Int32Rect(100, 100, 200, 160),
            "Selection mouse-up must keep the same editor and overlay open, with toolbar and eight handles.");
        Require(overlay.FrameHandles().Length == 8, "Frame needs all eight handles.");
        var original = overlay.Selection;
        foreach (var handle in overlay.FrameHandles())
        {
            overlay.SetRegion(original);
            int dx = handle.Handle.HasFlag(RegionOverlay.FrameHandle.Left) ? -20 : handle.Handle.HasFlag(RegionOverlay.FrameHandle.Right) ? 20 : 0;
            int dy = handle.Handle.HasFlag(RegionOverlay.FrameHandle.Top) ? -10 : handle.Handle.HasFlag(RegionOverlay.FrameHandle.Bottom) ? 10 : 0;
            Require(overlay.BeginFrameGesture(handle.Center), "Frame grip did not start a resize.");
            overlay.UpdateFrameGesture(handle.Center + new Vector(dx, dy));
            overlay.EndFrameGesture();
            Int32Rect expected = new(original.X + Math.Min(dx, 0), original.Y + Math.Min(dy, 0), original.Width + Math.Abs(dx), original.Height + Math.Abs(dy));
            Require(overlay.Selection == expected, $"Wrong anchor for frame grip {handle.Handle}.");
        }
        overlay.SetRegion(original);
        overlay.BeginFrameGesture(new Point(100, 100));
        overlay.UpdateFrameGesture(new Point(1000, 700));
        overlay.EndFrameGesture();
        Require(overlay.Selection == new Int32Rect(298, 258, 2, 2), "Frame resize must retain a two-pixel minimum.");
        overlay.SetRegion(original);
        overlay.BeginFrameGesture(new Point(100, 100));
        overlay.UpdateFrameGesture(new Point(-2000, -2000));
        overlay.EndFrameGesture();
        Require(overlay.Selection == new Int32Rect(0, 0, 300, 260), "Frame resize must clamp to the snapshot edges.");
        overlay.SetRegion(original);
        Require(overlay.BeginFrameGesture(new Point(200, 180), true), "Space drag must move the region.");
        overlay.UpdateFrameGesture(new Point(5000, 5000));
        overlay.EndFrameGesture();
        Require(overlay.Selection == new Int32Rect(824, 608, 200, 160), "Frame movement must clamp without changing its dimensions.");
        overlay.SetRegion(original);
        Require(overlay.BeginFrameGesture(new Point(180, 180)), "Select plus empty image drag must move the region.");
        overlay.UpdateFrameGesture(new Point(210, 200));
        overlay.EndFrameGesture();
        Require(overlay.Selection == new Int32Rect(130, 120, 200, 160), "Empty Select drag used the wrong delta.");
        overlay.Editor.Tool = EditorTool.Rectangle;
        Require(!overlay.BeginFrameGesture(new Point(180, 180)), "Drawing tools must retain empty image drags.");
        Require(overlay.BeginFrameGesture(new Point(180, 180), true), "Space drag must also work in drawing tools.");
        overlay.UpdateFrameGesture(new Point(0, 0));
        overlay.EndFrameGesture(true);
        Require(overlay.Selection == new Int32Rect(130, 120, 200, 160), "Canceled frame gesture must restore the previous crop.");
        overlay.Cancel();
    }

    private static void CheckCropAndIdentity(BitmapSource background)
    {
        var overlay = Create(background);
        AnnotationCanvas editor = overlay.Editor;
        overlay.SetRegion(new Int32Rect(100, 100, 300, 260));
        Draw(editor, EditorTool.Rectangle, new Point(140, 150), new Point(230, 220));
        editor.Tool = EditorTool.Select;
        object? identity = editor.SelectedIdentity;
        byte[] annotated = Bytes(editor.Flatten());
        Require(!overlay.BeginFrameGesture(new Point(180, 180)), "An object hit in Select must not be stolen by region movement.");
        editor.BeginGesture(new Point(180, 180));
        editor.UpdateGesture(new Point(200, 190));
        editor.EndGesture();
        byte[] moved = Bytes(editor.Flatten());
        overlay.SetRegion(new Int32Rect(280, 260, 40, 40)); // Object now lies outside the temporary crop.
        Require(ReferenceEquals(editor.SelectedIdentity, identity), "Shrinking a crop must preserve object identity.");
        overlay.SetRegion(new Int32Rect(40, 30, 700, 500)); // Reveal pixels beyond the original 100..400 crop.
        Require(ReferenceEquals(editor, overlay.Editor) && ReferenceEquals(editor.SelectedIdentity, identity), "Expanding a crop replaced/rebased the live editor or its selected object.");
        BitmapSource expanded = editor.Flatten(overlay.Selection);
        Require(expanded.PixelWidth == 700 && expanded.PixelHeight == 500 && expanded.DpiX == 96 && expanded.IsFrozen,
            "Crop dimensions must be exact physical pixels at 96 DPI.");
        Require(Pixel(expanded, 10, 10).SequenceEqual(Pixel(background, 50, 40)) &&
            Pixel(expanded, 650, 450).SequenceEqual(Pixel(background, 690, 480)),
            "Expanding past the original selection must recover untouched pixels from the full desktop snapshot.");
        byte[] expected = new byte[700 * 500 * 4];
        editor.Flatten().CopyPixels(overlay.Selection, expected, 700 * 4, 0);
        Require(expected.SequenceEqual(Bytes(expanded)), "Direct crop rendering differs from the corresponding native pixels.");
        editor.Undo();
        Require(Bytes(editor.Flatten()).SequenceEqual(annotated), "Frame changes must preserve object move undo.");
        editor.Redo();
        Require(Bytes(editor.Flatten()).SequenceEqual(moved), "Frame changes must preserve object move redo.");
        editor.BeginGesture(new Point(200, 190));
        editor.EndGesture();
        Require(ReferenceEquals(editor.SelectedIdentity, identity), "Selecting after crop expansion must hit the original object.");
        Rect beforeResize = editor.SelectionBounds;
        editor.BeginGesture(beforeResize.BottomRight);
        editor.UpdateGesture(beforeResize.BottomRight + new Vector(20, 30));
        editor.EndGesture();
        Require(Math.Abs(editor.SelectionBounds.Width - beforeResize.Width - 20) < .000001 &&
            Math.Abs(editor.SelectionBounds.Height - beforeResize.Height - 30) < .000001,
            "Original object must remain resizable after frame changes.");
        editor.Undo();
        Require(Bytes(editor.Flatten()).SequenceEqual(moved), "Resize after crop expansion lost history.");
        editor.Undo();
        editor.Undo();
        Require(!editor.CanUndo && Bytes(editor.Flatten()).SequenceEqual(Bytes(new AnnotationCanvas(background).Flatten())), "History must still reach the original full snapshot.");
        foreach (Int32Rect invalid in new[] { new Int32Rect(0, 0, 0, 0), new Int32Rect(-1, 0, 1, 1), new Int32Rect(1023, 0, 2, 1) })
        {
            bool rejected = false;
            try { editor.Flatten(invalid); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "Invalid crop must be rejected before allocating a render target.");
        }
        overlay.Cancel();
    }

    private static void CheckSelectedStyle(BitmapSource background)
    {
        var editor = new AnnotationCanvas(background) { Stroke = Brushes.Red };
        Draw(editor, EditorTool.Rectangle, new Point(140, 150), new Point(230, 220));
        editor.Tool = EditorTool.Select;
        object? identity = editor.SelectedIdentity;
        byte[] original = Bytes(editor.Flatten());
        editor.Stroke = Brushes.Blue;
        byte[] blue = Bytes(editor.Flatten());
        Require(!original.SequenceEqual(blue) && ReferenceEquals(identity, editor.SelectedIdentity), "Selected colour must change the original object.");
        editor.Thickness = 12;
        byte[] thick = Bytes(editor.Flatten());
        Require(!blue.SequenceEqual(thick), "Selected thickness must change the existing object.");
        editor.Undo();
        Require(blue.SequenceEqual(Bytes(editor.Flatten())), "Thickness undo must restore the object's original brush and thickness.");
        editor.Undo();
        Require(original.SequenceEqual(Bytes(editor.Flatten())), "Colour undo must restore the object style exactly.");
        editor.Redo(); editor.Redo();
        Require(thick.SequenceEqual(Bytes(editor.Flatten())), "Style redo must restore both style edits.");
        editor.Undo(); editor.Undo(); editor.Undo();
        Require(!editor.CanUndo && !editor.HasChanges, "Style actions and object add must be independent undo steps.");
        var redact = new AnnotationCanvas(background);
        Draw(redact, EditorTool.Redact, new Point(20, 20), new Point(80, 80));
        redact.Tool = EditorTool.Select;
        byte[] black = Bytes(redact.Flatten());
        redact.Stroke = Brushes.Transparent;
        redact.Thickness = 1;
        Require(black.SequenceEqual(Bytes(redact.Flatten())) && Pixel(redact.Flatten(), 30, 30).SequenceEqual(new byte[] { 0, 0, 0, 255 }), "Style changes must never soften opaque redaction.");
        redact.Undo();
        Require(!redact.CanUndo, "Redaction style controls must not introduce ineffective undo entries.");
    }

    private static void CheckRoutesAndText(BitmapSource background)
    {
        var overlay = Create(background);
        overlay.SetRegion(new Int32Rect(80, 70, 760, 410));
        Layout(overlay.PreviewRoot, background.PixelWidth, background.PixelHeight);
        string[] actions = Buttons(overlay.Toolbar).Select(b => b.Tag).OfType<string>().ToArray();
        Require(actions.TakeLast(4).SequenceEqual(new[] { "Close", "Pin", "Save", "Check" }) && !actions.Contains("Copy"),
            "Right action group must end with Close, Pin, Save, Check and contain no Copy button.");
        ButtonBase rectangle = Buttons(overlay.Toolbar).Single(b => Equals(b.Tag, EditorTool.Rectangle));
        rectangle.ApplyTemplate();
        DependencyObject source = (DependencyObject?)Descendant<TextBlock>(rectangle) ?? rectangle;
        Require(overlay.IsControlSource(source), "Toolbar content must be recognized by source ancestry.");
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = source };
        overlay.RaiseEvent(down);
        Require(!down.Handled && overlay.Selection == new Int32Rect(80, 70, 760, 410), "Window preview must leave toolbar mouse-down unhandled.");
        rectangle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(overlay.Editor.Tool == EditorTool.Rectangle, "Actual toolbar button must activate its editor tool.");
        var colour = Buttons(overlay.Toolbar).First(b => b.ToolTip is string tip && tip.StartsWith("Farbe #7D4BE3", StringComparison.Ordinal));
        Require(colour.Background is SolidColorBrush { Color.R: 125, Color.G: 75, Color.B: 227 }, "Swatch must show its actual colour.");
        ButtonBase redo = Buttons(overlay.Toolbar).Single(b => Equals(b.Tag, "Redo"));
        Require(!redo.IsEnabled && redo.ToolTip != null, "Disabled redo must retain its tooltip.");
        foreach (ButtonBase button in Buttons(overlay.Toolbar))
        {
            Require(button.Foreground is SolidColorBrush { Color.A: 255 }, "Toolbar control foreground must be explicit and opaque.");
            Require(button.Width >= 24 && button.Height >= 30 && button.ToolTip != null, "Toolbar controls must remain usable and explained.");
        }
        overlay.Editor.Tool = EditorTool.Text;
        overlay.Editor.BeginGesture(new Point(180, 180));
        var text = (TextBox)overlay.Editor.Children[0];
        Require(overlay.IsControlSource(text), "Inline TextBox must bypass frame interception.");
        var textDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = text };
        overlay.RaiseEvent(textDown);
        Require(!textDown.Handled, "Inline TextBox mouse-down must reach the actual text control.");
        Require(!overlay.HandleSessionKey(Key.C, ModifierKeys.Control) && !overlay.HandleSessionKey(Key.Space) &&
            !overlay.HandleSessionKey(Key.Delete), "Text editor must own copy, space and delete.");
        text.Text = "Live";
        overlay.Editor.CommitText();
        overlay.Editor.Tool = EditorTool.Select;
        byte[] first = Bytes(overlay.Editor.Flatten());
        overlay.Editor.BeginGesture(new Point(185, 185), 2);
        ((TextBox)overlay.Editor.Children[0]).Text = "Live edit";
        overlay.Editor.CommitText();
        byte[] second = Bytes(overlay.Editor.Flatten());
        Require(!first.SequenceEqual(second), "Text must reedit within the same overlay.");
        overlay.Editor.Undo();
        Require(first.SequenceEqual(Bytes(overlay.Editor.Flatten())), "Overlay text reedit must undo.");
        overlay.Editor.Redo();
        Require(second.SequenceEqual(Bytes(overlay.Editor.Flatten())), "Overlay text reedit must redo.");
        overlay.Editor.BeginGesture(new Point(185, 185), 2);
        ((TextBox)overlay.Editor.Children[0]).Text = "discarded";
        overlay.Editor.HandleKey(Key.Escape);
        Require(second.SequenceEqual(Bytes(overlay.Editor.Flatten())) && !overlay.IsClosed, "Text Esc must discard only the pending text edit.");
        // Empty reedit commits as delete; a subsequent style setter must handle the cleared selection.
        overlay.Editor.BeginGesture(new Point(185, 185), 2);
        ((TextBox)overlay.Editor.Children[0]).Text = "";
        overlay.Editor.Stroke = Brushes.Gold;
        Require(overlay.Editor.SelectedIdentity == null, "Committing empty text must clear selection before applying style.");
        overlay.Editor.Undo();
        Require(second.SequenceEqual(Bytes(overlay.Editor.Flatten())), "Empty text style path must add only one delete undo step.");
        ButtonBase cancel = Buttons(overlay.Toolbar).Single(b => Equals(b.Tag, "Close"));
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(overlay.IsClosed && overlay.Result.Completion == CaptureCompletion.Cancelled, "Actual cancel button must cancel the session.");
    }

    private static void CheckCompletion(BitmapSource background)
    {
        var overlay = Create(background);
        var crop = new Int32Rect(30, 20, 700, 430);
        overlay.SetRegion(crop);
        Draw(overlay.Editor, EditorTool.Redact, new Point(80, 70), new Point(120, 110));
        overlay.Editor.Tool = EditorTool.Select;
        byte[] expected = Bytes(overlay.Editor.Flatten(crop));
        Require(!overlay.ExecuteOutput(CaptureCompletion.Saved, _ => false) && !overlay.IsClosed, "Dismissing save must leave overlay and editor open.");
        Require(!overlay.ExecuteOutput(CaptureCompletion.Copied, _ => throw new InvalidOperationException("offline boundary failure")) && !overlay.IsClosed,
            "An output failure must leave the live overlay open.");
        Require(overlay.ExecuteOutput(CaptureCompletion.Copied, image => Bytes(image).SequenceEqual(expected)) && overlay.IsClosed,
            "Copy/save must export the current crop without handles and close only after success.");
        Require(overlay.Result.Completion == CaptureCompletion.Copied && ReferenceEquals(overlay.Result.Editor, overlay.Editor),
            "Completed copy must retain the live session for resume.");
        foreach (CaptureCompletion completion in new[] { CaptureCompletion.Ocr, CaptureCompletion.Pinned })
        {
            var handoff = Create(background);
            handoff.SetRegion(crop);
            AnnotationCanvas editor = handoff.Editor;
            Draw(editor, EditorTool.Rectangle, new Point(80, 70), new Point(120, 110));
            byte[] edited = Bytes(editor.Flatten(crop));
            var button = Buttons(handoff.Toolbar).Single(b => Equals(b.Tag, completion == CaptureCompletion.Ocr ? "Ocr" : "Pin"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(handoff.IsClosed && handoff.Result.Completion == completion && ReferenceEquals(handoff.Result.Editor, editor) && handoff.Result.Region == crop,
                "Explicit OCR must return the same live editor and physical crop.");
            Require(VisualTreeHelper.GetParent(editor) == null && editor.Parent == null && editor.Clip == null,
                "The editor must be detached from the overlay before handoff.");
            Require(edited.SequenceEqual(Bytes(editor.Flatten(crop))) && editor.CanUndo, "Completed session must preserve exact output and history.");
            Require(handoff.Result.Image != null && edited.SequenceEqual(Bytes(handoff.Result.Image)), "OCR and pin must receive the exact flattened crop.");
            editor.Undo(); editor.Redo();
            Require(edited.SequenceEqual(Bytes(editor.Flatten(crop))), "Completed history must remain live.");
        }
        var escape = Create(background);
        escape.SetRegion(crop);
        Require(escape.HandleSessionKey(Key.Escape) && escape.Result.Completion == CaptureCompletion.Cancelled && escape.IsClosed,
            "Session Escape must cancel after selection too.");
        var right = Create(background);
        right.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
        Require(right.IsClosed && right.Result.Completion == CaptureCompletion.Cancelled, "Right-click route must cancel.");
    }

    private static void CheckSaveDialog(BitmapSource background)
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kairos-overlay-save-" + Guid.NewGuid().ToString("N"));
        int prompts = 0;
        bool accept = false;
        var settings = new AppSettings { SaveFolder = directory };
        var overlay = new RegionOverlay(background, new DrawingRectangle(-1600, -900, background.PixelWidth, background.PixelHeight), settings: settings,
            saveImage: (image, owner, preferences) => ImageOutput.Save(image, owner, preferences, (dialog, _) =>
            { prompts++; dialog.FileName = System.IO.Path.Combine(directory, "selection.png"); return accept; }));
        try
        {
            var region = new Int32Rect(30, 20, 700, 430);
            overlay.SetRegion(region);
            Draw(overlay.Editor, EditorTool.Rectangle, new Point(80, 70), new Point(150, 130));
            object? identity = overlay.Editor.SelectedIdentity;
            byte[] expected = Bytes(overlay.Editor.Flatten(region));
            Buttons(overlay.Toolbar).Single(b => Equals(b.Tag, "Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(prompts == 1 && !overlay.IsClosed && overlay.Selection == region && overlay.Editor.CanUndo &&
                ReferenceEquals(identity, overlay.Editor.SelectedIdentity) && expected.SequenceEqual(Bytes(overlay.Editor.Flatten(region))),
                "Save button must invoke dialog and cancellation must preserve crop, annotations, selection and history.");
            Require(overlay.HandleSessionKey(Key.S, ModifierKeys.Control) && prompts == 2 && !overlay.IsClosed,
                "Ctrl+S must use the same cancellable dialog path.");
            accept = true;
            Require(overlay.HandleSessionKey(Key.S, ModifierKeys.Control) && prompts == 3 && overlay.IsClosed &&
                overlay.Result.Completion == CaptureCompletion.Saved && expected.SequenceEqual(Bytes(ImageOutput.Load(overlay.Result.SavedPath!))),
                "Confirmed save dialog must write the flattened image and finish the session.");
        }
        finally
        {
            overlay.Cancel();
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
    }

    private static void CheckPins(BitmapSource background)
    {
        var overlay = Create(background);
        var region = new Int32Rect(30, 20, 700, 430);
        overlay.SetRegion(region);
        Require(overlay.HandleSessionKey(Key.P) && overlay.Editor.Tool == EditorTool.Pen && !overlay.IsClosed, "P must remain the pen tool.");
        Draw(overlay.Editor, EditorTool.Redact, new Point(80, 70), new Point(150, 130));
        byte[] expected = Bytes(overlay.Editor.Flatten(region));
        Require(overlay.HandleSessionKey(Key.I) && overlay.IsClosed && overlay.Result.Completion == CaptureCompletion.Pinned, "Free shortcut I must pin the crop.");
        CaptureSessionResult result = overlay.Result;
        var pin = new PinWindow(result.Image!, result.Bounds, result.Region, new AppSettings());
        var second = new PinWindow(result.Image!, result.Bounds, result.Region, new AppSettings());
        try
        {
            Require(pin.ImageBounds == new Rect(-1570, -880, 700, 430) && ReferenceEquals(pin.ImageSource, result.Image) &&
                expected.SequenceEqual(Bytes(pin.ImageSource!)), "Pin position, size and source must exactly match the selected flattened physical crop.");
            Require(pin.Topmost && !pin.ShowActivated && !pin.ShowInTaskbar && pin.WindowStyle == WindowStyle.None && pin.ResizeMode == ResizeMode.NoResize,
                "Pin must be a nonactivating topmost borderless image window.");
            Require(pin.ContextMenu.Items.OfType<MenuItem>().Select(i => i.Header).SequenceEqual(new[] { "Kopieren", "Speichern…", "Originalgröße", "Schließen" }),
                "Pin context menu must expose all four glass actions.");
            var image = ((Canvas)pin.PreviewRoot).Children.OfType<Image>().Single();
            Require(image.Effect == null && RenderOptions.GetBitmapScalingMode(image) == BitmapScalingMode.NearestNeighbor,
                "Original image pixels must bypass the shadow effect and resampling.");
            foreach (double dpi in new[] { 1d, 1.25, 1.5, 2, 3 })
            {
                var view = (FrameworkElement)pin.Content;
                double width = (region.Width + 2 * PinWindow.ShadowPixels) / dpi, height = (region.Height + 2 * PinWindow.ShadowPixels) / dpi;
                view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
                Rect placed = image.TransformToAncestor(view).TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight));
                Require(Math.Abs(placed.Width * dpi - region.Width) < .00001 && Math.Abs(placed.Height * dpi - region.Height) < .00001 &&
                    Math.Abs(placed.Left * dpi - PinWindow.ShadowPixels) < .00001, "Viewbox must retain a physical 1:1 raster and outside shadow at every tested DPI.");
            }
            Point cursor = pin.ImageBounds.TopLeft + new Vector(200, 100);
            pin.ZoomAt(cursor, 1.5);
            Require(Math.Abs(pin.ImageBounds.Width / pin.ImageBounds.Height - 700d / 430) < .00001 &&
                RenderOptions.GetBitmapScalingMode(image) == BitmapScalingMode.HighQuality && second.ImageBounds.Width == 700,
                "Zoom must preserve aspect and use high quality without changing another pin.");
            pin.ResetSize();
            Require(pin.ImageBounds.Size == new Size(700, 430), "Original size must restore physical source pixels.");
            Require(!pin.IsVisible && new System.Windows.Interop.WindowInteropHelper(pin).Handle == IntPtr.Zero, "Pin checks must never show a native window.");
        }
        finally { pin.Close(); second.Close(); }
        Require(pin.ImageSource == null && second.ImageSource == null, "Closed pins must release their display sources.");

        foreach (Rect start in new[] { new Rect(-1600, -900, 700, 430), new Rect(10, 20, 120, 600), new Rect(0, 0, 300, 300) })
        {
            double aspect = start.Width / start.Height;
            foreach (var edges in new[] { RegionOverlay.FrameHandle.Left, RegionOverlay.FrameHandle.Right, RegionOverlay.FrameHandle.Top, RegionOverlay.FrameHandle.Bottom,
                RegionOverlay.FrameHandle.Left | RegionOverlay.FrameHandle.Top, RegionOverlay.FrameHandle.Right | RegionOverlay.FrameHandle.Top,
                RegionOverlay.FrameHandle.Left | RegionOverlay.FrameHandle.Bottom, RegionOverlay.FrameHandle.Right | RegionOverlay.FrameHandle.Bottom })
            foreach (Vector delta in new[] { new Vector(45, 31), new Vector(-9000, -9000), new Vector(9000, 9000) })
            {
                Rect resized = PinGeometry.Resize(start, edges, delta);
                Require(Math.Abs(resized.Width / resized.Height - aspect) < .00001 && resized.Width >= 48 - .00001 && resized.Height >= 48 - .00001,
                    "Every edge/corner resize must retain aspect ratio and the 48px minimum.");
                if (edges.HasFlag(RegionOverlay.FrameHandle.Left)) Require(Math.Abs(resized.Right - start.Right) < .00001, "Left resize must anchor the right edge.");
                if (edges.HasFlag(RegionOverlay.FrameHandle.Right)) Require(Math.Abs(resized.Left - start.Left) < .00001, "Right resize must anchor the left edge.");
                if (edges.HasFlag(RegionOverlay.FrameHandle.Top)) Require(Math.Abs(resized.Bottom - start.Bottom) < .00001, "Top resize must anchor the bottom edge.");
                if (edges.HasFlag(RegionOverlay.FrameHandle.Bottom)) Require(Math.Abs(resized.Top - start.Top) < .00001, "Bottom resize must anchor the top edge.");
            }
            Point cursor = start.TopLeft + new Vector(start.Width * .3, start.Height * .7);
            foreach (double factor in new[] { .00001, .5, 1.5, 1000 })
            {
                Rect zoomed = PinGeometry.Zoom(start, cursor, factor);
                Require(Math.Abs(zoomed.Width / zoomed.Height - aspect) < .00001 && zoomed.Width >= 48 - .00001 && zoomed.Height >= 48 - .00001 &&
                    Math.Abs((cursor.X - zoomed.Left) / zoomed.Width - .3) < .00001 && Math.Abs((cursor.Y - zoomed.Top) / zoomed.Height - .7) < .00001,
                    "Zoom must preserve aspect, minimum size and the image pixel underneath the cursor, including clamping.");
            }
        }
    }

    private static void CheckNativePinBounds()
    {
        // EnsureHandle creates only hidden, app-owned HWNDs; no Show, focus change or desktop input.
        foreach (Size size in new[] { new Size(1000, 1600), new Size(16000, 1000) })
        {
            int width = (int)size.Width, height = (int)size.Height;
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
            source.Freeze();
            var pin = new PinWindow(source, new DrawingRectangle(-20000, -20000, width, height), new Int32Rect(0, 0, width, height), new AppSettings());
            try
            {
                IntPtr handle = new System.Windows.Interop.WindowInteropHelper(pin).EnsureHandle();
                var origin = new NativePoint();
                Require(!IsWindowVisible(handle) && GetClientRect(handle, out NativeRect client) && ClientToScreen(handle, ref origin) &&
                    client.Right == width + 2 * PinWindow.ShadowPixels && client.Bottom == height + 2 * PinWindow.ShadowPixels &&
                    origin.X == -20000 - PinWindow.ShadowPixels && origin.Y == -20000 - PinWindow.ShadowPixels,
                    "Hidden native pin bounds must match large/tall source pixels plus outside shadow without desktop MaxTrack clipping.");
                DpiScale dpi = VisualTreeHelper.GetDpi(pin);
                var view = (FrameworkElement)pin.Content;
                var layout = new Size((width + 2 * PinWindow.ShadowPixels) / dpi.DpiScaleX, (height + 2 * PinWindow.ShadowPixels) / dpi.DpiScaleY);
                pin.Measure(layout); pin.Arrange(new Rect(layout)); pin.UpdateLayout();
                view.Measure(layout); view.Arrange(new Rect(layout)); view.UpdateLayout();
                var image = ((Canvas)pin.PreviewRoot).Children.OfType<Image>().Single();
                Rect drawn = image.TransformToAncestor(view).TransformBounds(new Rect(image.RenderSize));
                Require(Math.Abs(drawn.Width * dpi.DpiScaleX - width) < .00001 && Math.Abs(drawn.Height * dpi.DpiScaleY - height) < .00001 &&
                    !pin.IsVisible && !IsWindowVisible(handle), "Native pin layout must retain 1:1 source dimensions without WPF MaxTrack clipping or showing the HWND.");
                pin.ZoomAt(pin.ImageBounds.TopLeft + new Vector(width / 2d, height / 2d), 1.12);
                Require(GetClientRect(handle, out client) && client.Right == Math.Round(pin.ImageBounds.Width) + 2 * PinWindow.ShadowPixels &&
                    client.Bottom == Math.Round(pin.ImageBounds.Height) + 2 * PinWindow.ShadowPixels,
                    "Native resized pin client bounds must remain synchronized with zoom geometry.");
            }
            finally { pin.Close(); }
        }
        var wide = new Rect(0, 0, 20000, 1000);
        Rect larger = PinGeometry.Zoom(wide, new Point(10000, 500), 1.12);
        Require(larger.Width > wide.Width && Math.Abs(larger.Width - 22400) < .00001 && Math.Abs(larger.Height - 1120) < .00001,
            "Zoom in must enlarge valid originals wider than 16384px instead of shrinking them.");
        var maximum = new Rect(0, 0, PinGeometry.MaximumPixels, 1000);
        Require(PinGeometry.Zoom(maximum, maximum.TopLeft, 1.12).Width == maximum.Width,
            "Zoom at the supported maximum may stay fixed but must never reverse direction.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    private static void CheckRendering(BitmapSource background)
    {
        var overlay = Create(background);
        overlay.SetRegion(new Int32Rect(80, 70, 760, 410));
        BitmapSource preview = RenderScene(overlay, background);
        Require(Pixel(preview, 100, 100).SequenceEqual(Pixel(background, 100, 100)), "Unannotated pixels inside the crop must remain unshaded.");
        Require(!Pixel(preview, 20, 20).SequenceEqual(Pixel(background, 20, 20)), "The area outside the crop must be masked.");
        Require(overlay.ToolbarBounds.Left >= 0 && overlay.ToolbarBounds.Top >= 0 &&
            overlay.ToolbarBounds.Right <= background.PixelWidth && overlay.ToolbarBounds.Bottom <= background.PixelHeight,
            "The context toolbar must stay inside the desktop raster.");
        foreach (Int32Rect region in new[] { new Int32Rect(1000, 740, 24, 28), new Int32Rect(0, 0, 1024, 768), new Int32Rect(0, 0, 2, 2) })
        {
            overlay.SetRegion(region);
            Require(overlay.ToolbarBounds.Left >= 0 && overlay.ToolbarBounds.Top >= 0 &&
                overlay.ToolbarBounds.Right <= 1024 && overlay.ToolbarBounds.Bottom <= 768,
                "Toolbar must clamp at every desktop edge, including tiny crops.");
        }
        overlay.Cancel();
        BitmapSource demo = RenderPreview(background);
        Require(demo.IsFrozen && demo.PixelWidth == background.PixelWidth && demo.PixelHeight == background.PixelHeight,
            "Preview must render the actual fullscreen overlay tree without showing a window.");
    }

    public static BitmapSource RenderPreview(BitmapSource background)
    {
        ArgumentNullException.ThrowIfNull(background);
        Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Overlay preview requires STA.");
        var overlay = Create(background);
        int x = (int)(background.PixelWidth * .12), y = (int)(background.PixelHeight * .12);
        int width = Math.Max(2, (int)(background.PixelWidth * .74)), height = Math.Max(2, (int)(background.PixelHeight * .56));
        width = Math.Min(width, background.PixelWidth - x); height = Math.Min(height, background.PixelHeight - y);
        overlay.SetRegion(new Int32Rect(x, y, width, height));
        if (width >= 200 && height >= 120)
        {
            Draw(overlay.Editor, EditorTool.Rectangle, new Point(x + 20, y + 20), new Point(x + width * .45, y + height * .45));
            overlay.Editor.Tool = EditorTool.Text;
            overlay.Editor.BeginGesture(new Point(x + 30, y + height * .55));
            ((TextBox)overlay.Editor.Children[0]).Text = "Direkt im aufgenommenen Bildschirm";
            overlay.Editor.CommitText();
            Buttons(overlay.Toolbar).Single(b => Equals(b.Tag, EditorTool.Select)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        BitmapSource bitmap = RenderScene(overlay, background);
        overlay.Cancel();
        return bitmap;
    }

    private static BitmapSource RenderScene(RegionOverlay overlay, BitmapSource background)
    {
        Layout(overlay.PreviewRoot, background.PixelWidth, background.PixelHeight);
        var bitmap = new RenderTargetBitmap(background.PixelWidth, background.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(overlay.PreviewRoot);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Layout(FrameworkElement root, int width, int height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static IEnumerable<ButtonBase> Buttons(DependencyObject root)
    {
        if (root is ButtonBase button) yield return button;
        if (root is Border border && border.Child != null) foreach (var b in Buttons(border.Child)) yield return b;
        else if (root is ContentControl { Content: DependencyObject content }) foreach (var b in Buttons(content)) yield return b;
        else if (root is Panel panel) foreach (UIElement child in panel.Children) foreach (var b in Buttons(child)) yield return b;
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) return result;
            if (Descendant<T>(child) is T nested) return nested;
        }
        return null;
    }

    private static void Draw(AnnotationCanvas editor, EditorTool tool, Point start, Point end)
    {
        editor.Tool = tool; editor.BeginGesture(start); editor.UpdateGesture(end); editor.EndGesture();
    }

    private static BitmapSource Pattern(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            pixels[i] = (byte)(x * 3 + y); pixels[i + 1] = (byte)(y * 5 + x); pixels[i + 2] = (byte)(x + y * 7); pixels[i + 3] = 255;
        }
        var source = BitmapSource.Create(width, height, 144, 192, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze(); return source;
    }

    private static byte[] Bytes(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }
    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        byte[] pixel = new byte[4]; image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
