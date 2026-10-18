using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        CheckFrame(background);
        CheckCropAndIdentity(background);
        CheckSelectedStyle(background);
        CheckRoutesAndText(background);
        CheckCompletion(background);
        CheckHandoffPermission(background);
        CheckGestureEscape(background);
        CheckRendering(background);
    }

    private static RegionOverlay Create(BitmapSource background, Func<Window, Task<bool>>? confirmHandoff = null) => new(background,
        new DrawingRectangle(-1600, -900, background.PixelWidth, background.PixelHeight), confirmHandoff: confirmHandoff);

    private static void CheckHandoffPermission(BitmapSource background)
    {
        // Complete controlled delayed callbacks on this STA, just as a modal WPF owner does.
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            foreach (CaptureCompletion completion in new[] { CaptureCompletion.Workbench, CaptureCompletion.Ocr })
            {
                Task<bool> permission = Task.FromResult(false);
                int calls = 0;
                Window? dialogOwner = null;
                var overlay = Create(background, owner => { calls++; dialogOwner = owner; return permission; });
                Int32Rect crop = new(30, 20, 700, 430);
                overlay.SetRegion(crop);
                AnnotationCanvas editor = overlay.Editor;
                byte[] baseline = Bytes(editor.Flatten());
                Draw(editor, EditorTool.Rectangle, new Point(80, 70), new Point(120, 110));
                byte[] first = Bytes(editor.Flatten());
                Draw(editor, EditorTool.Ellipse, new Point(180, 170), new Point(220, 210));
                byte[] second = Bytes(editor.Flatten());
                editor.Undo();
                editor.Tool = EditorTool.Select;
                editor.BeginGesture(new Point(100, 90)); editor.EndGesture();
                object? identity = editor.SelectedIdentity;
                byte[] cropped = Bytes(editor.Flatten(crop));
                Require(!overlay.CompleteAsync(completion).GetAwaiter().GetResult() && calls == 1 && ReferenceEquals(dialogOwner, overlay),
                    "Denied handoff must use the live overlay as dialog owner.");
                Require(!overlay.IsClosed && ReferenceEquals(editor.Parent, overlay.PreviewRoot) && editor.Clip != null &&
                    overlay.Selection == crop && ReferenceEquals(identity, editor.SelectedIdentity) && editor.CanUndo && editor.CanRedo &&
                    first.SequenceEqual(Bytes(editor.Flatten())) && cropped.SequenceEqual(Bytes(editor.Flatten(crop))),
                    "Denied handoff must retain editor/object identity, crop, exact pixels and both history stacks.");

                permission = Task.FromException<bool>(new InvalidOperationException("offline approval failure"));
                Require(!overlay.CompleteAsync(completion).GetAwaiter().GetResult() && !overlay.IsClosed && editor.IsEnabled,
                    "Approval exceptions must be observed and release the gate without closing the session.");
                var pending = new TaskCompletionSource<bool>();
                permission = pending.Task;
                Task<bool> handoff = overlay.CompleteAsync(completion);
                Require(!handoff.IsCompleted && !editor.IsEnabled && calls == 3, "Delayed approval must hold an input gate.");
                Require(!overlay.CompleteAsync(completion).GetAwaiter().GetResult() &&
                    !overlay.CompleteAsync(CaptureCompletion.Copied).GetAwaiter().GetResult() &&
                    !overlay.CompleteAsync(CaptureCompletion.Saved).GetAwaiter().GetResult() && calls == 3,
                    "Pending approval must reject duplicate handoffs, copy and save before any side effects.");
                Require(!overlay.ExecuteOutput(CaptureCompletion.Saved, _ => throw new InvalidOperationException("must not write")) &&
                    !overlay.BeginFrameGesture(new Point(30, 20)), "Pending approval must block output and frame gestures.");
                overlay.SetRegion(new Int32Rect(0, 0, 50, 50));
                editor.BeginGesture(new Point(100, 90)); editor.UpdateGesture(new Point(600, 400)); editor.EndGesture();
                foreach (Button button in Buttons(overlay.Toolbar)) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                overlay.HandleSessionKey(Key.Escape);
                overlay.HandleSessionKey(Key.Delete);
                overlay.HandleSessionKey(Key.Z, ModifierKeys.Control);
                overlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
                Require(!overlay.IsClosed && calls == 3 && overlay.Selection == crop && ReferenceEquals(identity, editor.SelectedIdentity) &&
                    editor.CanUndo && editor.CanRedo && first.SequenceEqual(Bytes(editor.Flatten())),
                    "Pending approval must ignore cancellation, object/frame gestures and all toolbar/shortcut edits.");
                pending.SetResult(false);
                Require(handoff.IsCompletedSuccessfully && !handoff.Result && editor.IsEnabled && !overlay.IsClosed,
                    "Delayed denial must keep the session and permit another attempt.");
                pending = new TaskCompletionSource<bool>(); permission = pending.Task;
                handoff = overlay.CompleteAsync(completion);
                pending.SetResult(true);
                Require(handoff.IsCompletedSuccessfully && handoff.Result && overlay.IsClosed && calls == 4 &&
                    overlay.Result.ReplacementConfirmed && overlay.Result.Completion == completion && overlay.Result.Region == crop &&
                    ReferenceEquals(overlay.Result.Editor, editor) && ReferenceEquals(identity, editor.SelectedIdentity) && editor.Parent == null,
                    "Retry approval must detach exactly once and retain the approved editor/object/crop.");
                Require(cropped.SequenceEqual(Bytes(editor.Flatten(crop))) && editor.CanUndo && editor.CanRedo,
                    "Approved handoff must preserve pixels and both history stacks.");
                editor.Undo();
                Require(baseline.SequenceEqual(Bytes(editor.Flatten())) && !editor.CanUndo, "Denied/failed/pending approvals must add no undo entries.");
                editor.Redo();
                Require(first.SequenceEqual(Bytes(editor.Flatten())), "The original object must survive approval retry history.");
                editor.Redo();
                Require(second.SequenceEqual(Bytes(editor.Flatten())), "Existing redo must survive all approval attempts.");
                Require(!overlay.IsVisible && new System.Windows.Interop.WindowInteropHelper(overlay).Handle == IntPtr.Zero,
                    "Approval checks must never show a window or allocate a native handle.");
            }
            foreach (bool externalClose in new[] { false, true })
            {
                var pending = new TaskCompletionSource<bool>();
                var overlay = Create(background, _ => pending.Task);
                overlay.SetRegion(new Int32Rect(30, 20, 700, 430));
                Task<bool> handoff = overlay.CompleteAsync(CaptureCompletion.Workbench);
                if (externalClose) overlay.Close(); else overlay.Cancel(); // Owner/lifetime cancellation uses Cancel.
                pending.SetResult(true);
                Require(handoff.IsCompletedSuccessfully && !handoff.Result && overlay.IsClosed &&
                    overlay.Result.Completion == CaptureCompletion.Cancelled && overlay.Result.Editor == null &&
                    !overlay.Result.ReplacementConfirmed && ReferenceEquals(overlay.Editor.Parent, overlay.PreviewRoot),
                    "A late approval after external close or owner cancellation must never detach/activate the editor.");
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
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
            Require(overlay.HandleSessionKey(Key.Escape) && overlay.IsClosed, "The next idle Escape must cancel the session.");
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
        Button rectangle = Buttons(overlay.Toolbar).Single(b => Equals(b.Content, "Rechteck"));
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
        Require(Descendant<TextBlock>(colour)?.Foreground is SolidColorBrush { Color.R: 125, Color.G: 75, Color.B: 227 },
            "Button content must inherit its own foreground instead of a global TextBlock colour.");
        Button redo = Buttons(overlay.Toolbar).Single(b => Equals(b.Content, "Wiederholen"));
        Require(!redo.IsEnabled && Descendant<Border>(redo)?.Background is SolidColorBrush { Color.R: 9, Color.G: 14, Color.B: 19 },
            "Disabled toolbar controls must retain a dark readable background.");
        foreach (Button button in Buttons(overlay.Toolbar))
        {
            Require(button.Foreground is SolidColorBrush { Color.A: 255 }, "Toolbar control foreground must be explicit and opaque.");
            Require(button.FontSize >= 13 && button.MinHeight >= 30, "Toolbar controls must remain readable and usable.");
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
        Button cancel = Buttons(overlay.Toolbar).Single(b => Equals(b.Content, "Abbrechen"));
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
        Require(overlay.Result.Completion == CaptureCompletion.Copied && overlay.Result.Editor == null,
            "Completed copy should not request the workbench.");
        foreach (CaptureCompletion completion in new[] { CaptureCompletion.Ocr, CaptureCompletion.Workbench })
        {
            var handoff = Create(background);
            handoff.SetRegion(crop);
            AnnotationCanvas editor = handoff.Editor;
            Draw(editor, EditorTool.Rectangle, new Point(80, 70), new Point(120, 110));
            byte[] edited = Bytes(editor.Flatten(crop));
            var button = Buttons(handoff.Toolbar).Single(b => Equals(b.Content, completion == CaptureCompletion.Ocr ? "OCR" : "Werkbank"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(handoff.IsClosed && handoff.Result.Completion == completion && ReferenceEquals(handoff.Result.Editor, editor) && handoff.Result.Region == crop,
                "Explicit OCR/workbench must return the same live editor and physical crop.");
            Require(VisualTreeHelper.GetParent(editor) == null && editor.Parent == null && editor.Clip == null,
                "The editor must be detached from the overlay before handoff.");
            Require(edited.SequenceEqual(Bytes(editor.Flatten(crop))) && editor.CanUndo, "Handoff must preserve exact output and history.");
            editor.Undo(); editor.Redo();
            Require(edited.SequenceEqual(Bytes(editor.Flatten(crop))), "Handoff history must remain live.");
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
            Buttons(overlay.Toolbar).Single(b => Equals(b.Content, "Auswahl")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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

    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        if (root is Button button) yield return button;
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
