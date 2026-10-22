using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
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
        CheckCopyButtons(background);
        CheckCopyFailures(background);
        CheckCopyRoutes(background);
        CheckSaveDialog(background);
        CheckPins(background);
        CheckNativePinBounds();
        CheckResume(background);
        CheckOcrMode(background);
        CheckGestureEscape(background);
        CheckRendering(background);
        CheckToolbarReachability();
        var failures = new List<string>();
        foreach (Action check in new Action[] { () => CheckFrameReleasePosition(background), CheckFrameInputRecovery,
            () => CheckRoutedFrameInput(background), () => CheckSynchronousCaptureRelease(background),
            () => CheckInterruptedFrameInput(background), () => CheckReleaseModes(background),
            () => CheckOverlayTrace(background), () => CheckDiagnosticLogGuard(background), () => CheckClosingFrameInput(background),
            () => CheckLiveOverlayTrace(background), () => CheckDiagnosticRoutes(background), () => CheckDiagnosticMeasurements(background),
            CheckDiagnosticTransforms, () => CheckDiagnosticFileStream(background), CheckDiagnosticRotation,
            () => CheckDiagnosticActivePointer(background), () => CheckDiagnosticKeyAdapter(background), () => CheckDiagnosticRetention(background) })
        {
            try { check(); }
            catch (SelfCheckFailure error) { failures.Add(error.Message); }
        }
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static RegionOverlay Create(BitmapSource background) => new(background,
        new DrawingRectangle(-1600, -900, background.PixelWidth, background.PixelHeight));

    private enum FrameInput { Release, LostCapture, Deactivate, Escape, RightClick, OutsideRelease, SecondPress, ChildLostCapture, ReleasedMove }
    private enum FrameFixture { New, Move, Resize, ResumedMove }

    private sealed class FramePointer
    {
        internal Point? Position = new Point(100, 100);
        internal MouseButtonState LeftButton = MouseButtonState.Pressed;
        internal bool Captured, CaptureSucceeds = true;
        internal int Releases;
        internal Action? OnRelease;

        internal RegionOverlay Create(BitmapSource background, bool editing = true, bool ocr = false,
            Action<string>? log = null, AnnotationCanvas? editor = null) => new(background,
            new DrawingRectangle(-1600, -900, background.PixelWidth, background.PixelHeight), editing: editing,
            ocrMode: ocr, editor: editor, logState: log, readCursor: () => Position, readLeftButton: () => LeftButton,
            captureMouse: () => Captured = CaptureSucceeds, hasCapture: () => Captured,
            releaseCapture: () => { Captured = false; Releases++; OnRelease?.Invoke(); });
    }

    private static void RaiseMove(RegionOverlay overlay) => overlay.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
        { RoutedEvent = Mouse.PreviewMouseMoveEvent, Source = overlay });

    private static void RaiseLeftButton(RegionOverlay overlay, bool down) =>
        overlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = down ? UIElement.PreviewMouseLeftButtonDownEvent : UIElement.PreviewMouseLeftButtonUpEvent, Source = overlay });

    private static void InterruptFrame(RegionOverlay overlay, bool deactivate)
    {
        if (deactivate)
            typeof(Window).GetMethod("OnDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(overlay, new object[] { EventArgs.Empty });
        else overlay.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
            { RoutedEvent = Mouse.LostMouseCaptureEvent, Source = overlay });
    }

    private static void CheckRoutedFrameInput(BitmapSource background)
    {
        foreach (bool mouseUp in new[] { false, true })
        {
            var pointer = new FramePointer();
            var overlay = pointer.Create(background);
            try
            {
                RaiseLeftButton(overlay, down: true);
                pointer.Position = new Point(400, 300);
                RaiseMove(overlay);
                Require(overlay.IsFrameGestureActive && overlay.Selection == new Int32Rect(100, 100, 300, 200) &&
                    !overlay.ToolbarVisible && !ChipVisible(overlay), "Routed pressed move must update the active frame and hide its controls.");
                pointer.Position = new Point(500, 400);
                pointer.LeftButton = MouseButtonState.Released;
                if (mouseUp) RaiseLeftButton(overlay, down: false); else RaiseMove(overlay);
                Require(!overlay.IsFrameGestureActive && overlay.Selection == new Int32Rect(100, 100, 400, 300) &&
                    FrameControlsVisible(overlay, background), mouseUp
                    ? "Routed MouseUp must finish the active gesture at the release position."
                    : "Routed released move must finish the active gesture using the injected button state.");
            }
            finally { overlay.Cancel(); }
        }

        // An unreadable cursor at release must keep the frame at its last known point instead of ending the session.
        var unreadable = new FramePointer();
        var keep = unreadable.Create(background);
        try
        {
            RaiseLeftButton(keep, down: true);
            unreadable.Position = new Point(400, 300);
            RaiseMove(keep);
            unreadable.Position = null;
            unreadable.LeftButton = MouseButtonState.Released;
            RaiseLeftButton(keep, down: false);
            Require(!keep.IsClosed && !keep.IsFrameGestureActive && keep.Selection == new Int32Rect(100, 100, 300, 200) &&
                FrameControlsVisible(keep, background), "An unreadable cursor at MouseUp must finish the frame at its last known point.");
        }
        finally { keep.Cancel(); }

        // A second press while a frame is still active is the visible symptom of a lost release and must be traced.
        var stuck = new FramePointer();
        var flushes = new List<string>();
        var again = stuck.Create(background, log: flushes.Add);
        try
        {
            RaiseLeftButton(again, down: true);
            stuck.Position = new Point(400, 300);
            RaiseMove(again);
            RaiseLeftButton(again, down: true);
            stuck.LeftButton = MouseButtonState.Released;
            RaiseLeftButton(again, down: false);
            Require(!again.IsFrameGestureActive && FrameControlsVisible(again, background), "The second click must still finish the stuck frame.");
        }
        finally { again.Cancel(); }
        Require(flushes.Count == 1, "A second press during an active frame must flush its trace exactly once.");
    }

    private static void CheckSynchronousCaptureRelease(BitmapSource background)
    {
        foreach (bool ocr in new[] { false, true })
        {
            var pointer = new FramePointer();
            var flushes = new List<string>();
            var overlay = pointer.Create(background, ocr: ocr, log: flushes.Add);
            var original = new Int32Rect(100, 100, 400, 300);
            var expected = new Int32Rect(150, 140, 400, 300);
            overlay.SetRegion(original);
            int completions = 0;
            overlay.Closed += (_, _) => { if (overlay.Result.Completion == CaptureCompletion.Ocr) completions++; };
            pointer.OnRelease = () => InterruptFrame(overlay, deactivate: false);
            try
            {
                pointer.Position = new Point(300, 250);
                RaiseLeftButton(overlay, down: true);
                pointer.Position = new Point(350, 290);
                // A synchronous capture-loss notification can still observe the previous pressed state.
                RaiseLeftButton(overlay, down: false);
                Require(overlay.Selection == expected && !overlay.IsFrameGestureActive && pointer.Releases == 1 &&
                    (ocr ? overlay.IsClosed && completions == 1 && overlay.Result.Region == expected : FrameControlsVisible(overlay, background)),
                    "Synchronous capture loss during release must preserve the final crop and complete OCR exactly once.");
                InterruptFrame(overlay, deactivate: false);
                RaiseLeftButton(overlay, down: false);
                Require(completions == (ocr ? 1 : 0) && overlay.Selection == expected,
                    "Late capture loss and MouseUp must not replace the final crop or repeat OCR.");
            }
            finally { overlay.Cancel(); }
            Require(flushes.Count == 0, "Expected synchronous capture loss after release must not flush a normal trace.");
        }
    }

    private static void CheckInterruptedFrameInput(BitmapSource background)
    {
        foreach (bool deactivate in new[] { false, true })
        foreach (MouseButtonState button in Enum.GetValues<MouseButtonState>())
        foreach (bool captured in new[] { false, true })
        foreach (bool missingCursor in new[] { false, true })
        {
            var pointer = new FramePointer();
            var overlay = pointer.Create(background);
            var original = new Int32Rect(100, 100, 400, 300);
            overlay.SetRegion(original);
            try
            {
                overlay.BeginFrameGesture(new Point(300, 250));
                overlay.UpdateFrameGesture(new Point(320, 260));
                pointer.Position = missingCursor ? null : new Point(350, 290);
                pointer.LeftButton = button;
                pointer.Captured = captured;
                InterruptFrame(overlay, deactivate);
                bool stillDragging = button == MouseButtonState.Pressed && captured;
                Int32Rect expected = button == MouseButtonState.Pressed
                    ? stillDragging ? new Int32Rect(120, 110, 400, 300) : original
                    : missingCursor ? new Int32Rect(120, 110, 400, 300) : new Int32Rect(150, 140, 400, 300);
                Require(!overlay.IsClosed && overlay.Failure == null && overlay.IsFrameGestureActive == stillDragging && overlay.Selection == expected &&
                    (stillDragging ? !overlay.ToolbarVisible && !ChipVisible(overlay) : FrameControlsVisible(overlay, background)),
                    $"Interruption semantics failed for deactivate={deactivate}, button={button}, capture={captured}, missingCursor={missingCursor}.");
                if (stillDragging)
                {
                    pointer.Position = new Point(350, 290);
                    pointer.LeftButton = MouseButtonState.Released;
                    RaiseLeftButton(overlay, down: false);
                    Require(!overlay.IsFrameGestureActive && overlay.Selection == new Int32Rect(150, 140, 400, 300) &&
                        FrameControlsVisible(overlay, background), "Retained capture must allow the later MouseUp to finish normally.");
                }
            }
            finally { overlay.Cancel(); }
        }
    }

    private static void CheckReleaseModes(BitmapSource background)
    {
        foreach (bool raw in new[] { false, true })
        foreach (string end in new[] { "releasedMove", "deactivated", "captureLost", "escape", "lostPressed", "deactivatedPressed" })
        {
            var pointer = new FramePointer();
            var overlay = pointer.Create(background, editing: !raw, ocr: !raw);
            int completions = 0;
            overlay.Closed += (_, _) => completions++;
            try
            {
                RaiseLeftButton(overlay, down: true);
                pointer.Position = new Point(400, 300);
                RaiseMove(overlay);
                pointer.Position = new Point(500, 400);
                bool cancel = end is "escape" or "lostPressed" or "deactivatedPressed";
                pointer.LeftButton = cancel ? MouseButtonState.Pressed : MouseButtonState.Released;
                if (end == "releasedMove") RaiseMove(overlay);
                else if (end == "escape") overlay.HandleSessionKey(Key.Escape);
                else
                {
                    pointer.Captured = false;
                    InterruptFrame(overlay, end.StartsWith("deactivated", StringComparison.Ordinal));
                }
                if (cancel)
                    Require(overlay.Result.Completion == CaptureCompletion.Cancelled && overlay.Selection.IsEmpty &&
                        !overlay.IsFrameGestureActive && completions == (raw && end == "escape" ? 1 : 0),
                        "Aborted frame gestures must never start OCR or accept a raw crop: " + end);
                else
                {
                    var expected = new Int32Rect(100, 100, 400, 300);
                    Require(overlay.IsClosed && completions == 1 && overlay.Selection == expected &&
                        (raw ? overlay.Result.Completion == CaptureCompletion.Cancelled : overlay.Result.Completion == CaptureCompletion.Ocr &&
                            overlay.Result.Region == expected && overlay.Result.Image?.PixelWidth == 400 && overlay.Result.Image.PixelHeight == 300),
                        "Released gestures must accept raw captures or finish OCR exactly once: " + end);
                    RaiseMove(overlay);
                    RaiseLeftButton(overlay, down: false);
                    InterruptFrame(overlay, deactivate: true);
                    Require(completions == 1, "A finished OCR/raw capture must ignore late input.");
                }
            }
            finally { overlay.Cancel(); }
        }
        foreach (string end in new[] { "escape", "captureLost", "deactivated" })
        {
            var pointer = new FramePointer();
            var overlay = pointer.Create(background, ocr: true);
            var original = new Int32Rect(100, 100, 400, 300);
            overlay.SetRegion(original);
            try
            {
                overlay.BeginFrameGesture(new Point(300, 250));
                overlay.UpdateFrameGesture(new Point(350, 290));
                pointer.Position = new Point(350, 290);
                if (end == "escape") overlay.HandleSessionKey(Key.Escape);
                else InterruptFrame(overlay, end == "deactivated");
                Require(!overlay.IsClosed && overlay.Result.Completion == CaptureCompletion.Cancelled &&
                    !overlay.IsFrameGestureActive && overlay.Selection == original && FrameControlsVisible(overlay, background),
                    "Aborting an OCR gesture with a valid previous crop must restore it without starting OCR: " + end);
            }
            finally { overlay.Cancel(); }
            Require(overlay.Result.Completion == CaptureCompletion.Cancelled, "Cancelling a retained OCR crop must never emit OCR output.");
        }
    }

    private static void CheckClosingFrameInput(BitmapSource background)
    {
        foreach (MouseButtonState button in Enum.GetValues<MouseButtonState>())
        {
            var pointer = new FramePointer { Captured = true, LeftButton = button };
            var overlay = pointer.Create(background, ocr: true);
            int completions = 0;
            overlay.Closed += (_, _) => completions++;
            try
            {
                overlay.BeginFrameGesture(new Point(100, 100));
                overlay.UpdateFrameGesture(new Point(500, 400));
                pointer.Position = new Point(500, 400);
                typeof(Window).GetMethod("OnClosing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(overlay, new object[] { new System.ComponentModel.CancelEventArgs() });
                pointer.Captured = false;
                InterruptFrame(overlay, deactivate: false);
                InterruptFrame(overlay, deactivate: true);
                RaiseLeftButton(overlay, down: false);
                RaiseMove(overlay);
                Require(!overlay.IsFrameGestureActive && overlay.Selection.IsEmpty && completions == 0 &&
                    overlay.Result.Completion == CaptureCompletion.Cancelled && overlay.Failure == null,
                    "Capture loss between Closing and Closed must never retain a cancelled crop or start OCR.");
            }
            finally { overlay.Close(); }
            Require(completions == 1 && overlay.Result.Completion == CaptureCompletion.Cancelled,
                "Closing an active frame must cancel once despite capture-loss and pointer notifications.");
        }
    }

    private static void CheckFrameReleasePosition(BitmapSource background)
    {
        var failures = new List<string>();
        foreach (FrameFixture fixture in Enum.GetValues<FrameFixture>())
        foreach (bool pressedMove in new[] { false, true })
        {
            var original = new Int32Rect(100, 100, 400, 300);
            RegionOverlay overlay = Create(background);
            if (fixture != FrameFixture.New) overlay.SetRegion(original);
            if (fixture == FrameFixture.ResumedMove)
            {
                overlay.ExecuteOutput(CaptureCompletion.Copied, _ => true);
                CaptureSessionResult session = overlay.Result;
                overlay = new RegionOverlay(session.Screenshot!, session.Bounds, editor: session.Editor);
                overlay.SetRegion(session.Region);
            }
            try
            {
                Point start = fixture switch {
                    FrameFixture.New => new Point(100, 100),
                    FrameFixture.Resize => new Point(500, 400),
                    _ => new Point(300, 250) };
                overlay.HandlePointerDown(start);
                if (pressedMove) overlay.HandlePointerMove(start + new Vector(100, 80), MouseButtonState.Pressed);
                overlay.HandlePointerMove(start + new Vector(140, 110), MouseButtonState.Released);
                Int32Rect expected = fixture switch {
                    FrameFixture.New => new Int32Rect(100, 100, 140, 110),
                    FrameFixture.Resize => new Int32Rect(100, 100, 540, 410),
                    _ => new Int32Rect(240, 210, 400, 300) };
                if (overlay.Selection != expected || overlay.IsFrameGestureActive || !FrameControlsVisible(overlay, background))
                    failures.Add($"{fixture}/pressedMove={pressedMove}: actual={overlay.Selection}, expected={expected}, toolbar={overlay.ToolbarVisible}");
                Int32Rect finished = overlay.Selection;
                overlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                overlay.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                    { RoutedEvent = Mouse.LostMouseCaptureEvent, Source = overlay });
                typeof(Window).GetMethod("OnDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(overlay, new object[] { EventArgs.Empty });
                overlay.HandlePointerMove(start + new Vector(200, 160), MouseButtonState.Released);
                overlay.EndFrameGesture();
                Require(overlay.Selection == finished,
                    "Late MouseUp, capture loss, deactivation and released moves must not finish the same gesture twice.");
            }
            finally { overlay.Cancel(); }
        }
        Require(failures.Count == 0, "Released moves must apply their position before finishing and keep all frame controls visible: " + string.Join("; ", failures));
    }

    private static void CheckFrameInputRecovery()
    {
        BitmapSource background = Pattern(1374, 859);
        var original = new Int32Rect(264, 182, 600, 300);
        var failures = new List<string>();
        foreach (FrameFixture fixture in Enum.GetValues<FrameFixture>())
        foreach (FrameInput input in Enum.GetValues<FrameInput>())
        {
            var pointer = new FramePointer();
            RegionOverlay overlay = pointer.Create(background);
            if (fixture != FrameFixture.New) overlay.SetRegion(original);
            if (fixture == FrameFixture.ResumedMove)
            {
                Require(overlay.ExecuteOutput(CaptureCompletion.Copied, _ => true), "Resume fixture must retain a completed session.");
                CaptureSessionResult session = overlay.Result;
                overlay = pointer.Create(session.Screenshot!, editor: session.Editor);
                overlay.SetRegion(session.Region);
                Require(FrameControlsVisible(overlay, background), "Resume must start with all frame controls visible.");
            }
            try
            {
                Point start = fixture switch {
                    FrameFixture.New => new Point(264, 182),
                    FrameFixture.Resize => new Point(864, 482),
                    _ => new Point(500, 350) };
                Require(overlay.HandlePointerDown(start), "Interruption fixture must begin a frame gesture.");
                Point end = fixture == FrameFixture.New || fixture == FrameFixture.Resize ? new Point(993, 593) : start + new Vector(70, 50);
                overlay.UpdateFrameGesture(end);
                Int32Rect dragged = overlay.Selection;
                Require(overlay.IsFrameGestureActive && !overlay.ToolbarVisible && !ChipVisible(overlay) &&
                    !GoldHandlesVisible(overlay, RenderScene(overlay, background)), "Active drag must hide toolbar, chip and gold handles together.");
                switch (input)
                {
                    case FrameInput.Release:
                        overlay.EndFrameGesture();
                        break;
                    case FrameInput.LostCapture:
                        overlay.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                            { RoutedEvent = Mouse.LostMouseCaptureEvent, Source = overlay });
                        break;
                    case FrameInput.Deactivate:
                        typeof(Window).GetMethod("OnDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .Invoke(overlay, new object[] { EventArgs.Empty });
                        break;
                    case FrameInput.Escape:
                        overlay.HandleSessionKey(Key.Escape);
                        break;
                    case FrameInput.RightClick:
                        overlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                            { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
                        break;
                    case FrameInput.OutsideRelease:
                        overlay.UpdateFrameGesture(new Point(5000, 5000));
                        overlay.EndFrameGesture();
                        break;
                    case FrameInput.SecondPress:
                        Require(!overlay.HandlePointerDown(end), "A duplicate press must not replace the running gesture.");
                        overlay.EndFrameGesture();
                        break;
                    case FrameInput.ChildLostCapture:
                        overlay.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                            { RoutedEvent = Mouse.LostMouseCaptureEvent, Source = overlay.Editor });
                        Require(overlay.IsFrameGestureActive, "A child's capture loss must not cancel the window's frame gesture.");
                        overlay.EndFrameGesture();
                        break;
                    case FrameInput.ReleasedMove:
                        overlay.HandlePointerMove(end, MouseButtonState.Released);
                        break;
                }
                if (overlay.IsFrameGestureActive)
                {
                    failures.Add($"{fixture}/{input}: selection={overlay.Selection.Width}x{overlay.Selection.Height}, gesture=active, toolbar=Collapsed, chip=Collapsed, handles=0");
                    continue;
                }
                if (input == FrameInput.RightClick)
                    Require(overlay.IsClosed && overlay.Selection.IsEmpty, "Right-click during a frame gesture must cancel the session.");
                else if (input is FrameInput.LostCapture or FrameInput.Deactivate or FrameInput.Escape)
                    Require(!overlay.IsClosed && overlay.Selection == (fixture == FrameFixture.New ? Int32Rect.Empty : original),
                        "Interrupted frame gestures must restore the previous selection.");
                else if (input != FrameInput.OutsideRelease)
                    Require(!overlay.IsClosed && overlay.Selection == dragged, "Release recovery must retain the last position observed while dragging.");
                if (!overlay.IsClosed && !overlay.Selection.IsEmpty)
                {
                    Require(FrameControlsVisible(overlay, background), "Finished frame gestures must show toolbar, size chip and all eight gold handles.");
                    Buttons(overlay.Toolbar).Single(b => Equals(b.Tag, EditorTool.Rectangle)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(overlay.Editor.Tool == EditorTool.Rectangle && overlay.Editor.IsHitTestVisible,
                        "Recovered frame controls and annotation input must be usable.");
                    Require(overlay.HandlePointerDown(overlay.FrameHandles()[0].Center), "Recovered frame handles must remain usable.");
                    overlay.EndFrameGesture(true);
                }
            }
            finally { overlay.Cancel(); }
        }
        Require(failures.Count == 0, "Frame input recovery failed: " + string.Join("; ", failures));
    }

    private static bool ChipVisible(RegionOverlay overlay) => ((Canvas)overlay.PreviewRoot).Children.OfType<Border>()
        .Single(border => border.Child is TextBlock).Visibility == Visibility.Visible;

    // The size chip shadow can tint a handle without hiding it.
    private static bool GoldHandlesVisible(RegionOverlay overlay, BitmapSource scene) => overlay.FrameHandles().All(handle =>
        Pixel(scene, Math.Clamp((int)handle.Center.X, 0, scene.PixelWidth - 1), Math.Clamp((int)handle.Center.Y, 0, scene.PixelHeight - 1))
            .Zip(new byte[] { 0x76, 0xB5, 0xD3, 0xFF }).All(channel => Math.Abs(channel.First - channel.Second) <= 8));

    private static bool FrameControlsVisible(RegionOverlay overlay, BitmapSource background)
    {
        BitmapSource scene = RenderScene(overlay, background);
        return overlay.ToolbarVisible && ChipVisible(overlay) && overlay.Toolbar.ActualHeight > 0 &&
            GoldHandlesVisible(overlay, scene) && overlay.ToolbarBounds.Top >= 0 && overlay.ToolbarBounds.Bottom <= background.PixelHeight;
    }

    private static void CheckOverlayTrace(BitmapSource background)
    {
        const string sensitive = "private annotation and exception message";
        var region = new Int32Rect(100, 100, 400, 300);
        foreach (string anomaly in new[] { "normal", "escape", "deactivated", "captureLost", "releasedMove", "cancelActive",
            "closeActive", "toolbarHidden", "chipHidden", "captureFailure", "cursorFailure" })
        {
            var pointer = new FramePointer();
            var flushes = new List<string>();
            var overlay = pointer.Create(background, log: flushes.Add);
            overlay.Title = sensitive;
            try
            {
                RaiseLeftButton(overlay, down: true);
                pointer.Position = new Point(500, 400);
                pointer.LeftButton = MouseButtonState.Released;
                RaiseLeftButton(overlay, down: false);
                overlay.Editor.Tool = EditorTool.Text;
                overlay.Editor.BeginGesture(new Point(180, 180));
                ((TextBox)overlay.Editor.Children[0]).Text = sensitive;
                overlay.Editor.CommitText();
                Require(!overlay.ExecuteOutput(CaptureCompletion.Copied, _ => throw new InvalidOperationException(sensitive)),
                    "Trace privacy fixture must exercise output failure.");
                overlay.Editor.Tool = EditorTool.Select;
                for (int i = 0; i < 30; i++)
                {
                    overlay.BeginFrameGesture(overlay.FrameHandles()[2].Center);
                    overlay.EndFrameGesture();
                }
                Require(flushes.Count == 0, "Normal gestures must stay in memory without log writes.");
                if (anomaly is "toolbarHidden" or "chipHidden")
                {
                    if (anomaly == "toolbarHidden") overlay.Toolbar.Visibility = Visibility.Collapsed;
                    else ((Canvas)overlay.PreviewRoot).Children.OfType<Border>().Single(b => b.Child is TextBlock).Visibility = Visibility.Collapsed;
                }
                else if (anomaly is "captureFailure" or "cursorFailure")
                {
                    pointer.CaptureSucceeds = false;
                    pointer.Position = anomaly == "cursorFailure" ? null : new Point(500, 400);
                    RaiseLeftButton(overlay, down: true);
                }
                else if (anomaly != "normal")
                {
                    overlay.BeginFrameGesture(new Point(300, 250));
                    overlay.UpdateFrameGesture(new Point(350, 290));
                    pointer.Position = new Point(350, 290);
                    if (anomaly == "escape") overlay.HandleSessionKey(Key.Escape);
                    else if (anomaly == "deactivated") InterruptFrame(overlay, deactivate: true);
                    else if (anomaly == "captureLost") InterruptFrame(overlay, deactivate: false);
                    else if (anomaly == "releasedMove") RaiseMove(overlay);
                    else if (anomaly == "cancelActive") overlay.Cancel();
                    else if (anomaly == "closeActive") overlay.Close();
                }
                if (!overlay.IsClosed) Require(flushes.Count == 0, "Anomaly traces must flush once at close, not on each event.");
            }
            finally { overlay.Cancel(); }
            Require(overlay.IsClosed && !overlay.IsFrameGestureActive && !overlay.ToolbarVisible && !ChipVisible(overlay),
                "Closing must clear the gesture and hide all frame controls.");
            if (anomaly is "normal" or "escape")
            {
                Require(flushes.Count == 0, "A normal capture or explicit gesture Escape must write zero log lines.");
                continue;
            }
            Require(flushes.Count == 1, "Each anomalous overlay must flush exactly once: " + anomaly);
            string[] entries = flushes[0].Split(Environment.NewLine);
            Require(entries.Length == 20 && entries[^1].Contains("event=Closed ", StringComparison.Ordinal) &&
                entries[^1].Contains("toolbar=Collapsed chip=Collapsed chrome=Closed handles=0", StringComparison.Ordinal),
                "The ring buffer must keep only its latest 20 lines and end with consistent closed chrome.");
            if (anomaly is "cancelActive" or "closeActive")
                Require(entries[^1].Contains("gesture=Move selection=150,140,400,300", StringComparison.Ordinal) && overlay.Selection.IsEmpty,
                    "Closed trace must retain the active gesture and selection from before reset.");
            if (anomaly is "toolbarHidden" or "chipHidden")
                Require(entries[^2].Contains("event=Closing ", StringComparison.Ordinal) && entries[^2].Contains("chrome=Ready", StringComparison.Ordinal) &&
                    entries[^2].Contains(anomaly == "toolbarHidden" ? "toolbar=Collapsed" : "chip=Collapsed", StringComparison.Ordinal),
                    "Closing trace must retain unexpectedly missing controls before hiding the closed chrome.");
            Require(entries.All(line => new[] { " gesture=", " capture=", " active=", " toolbar=", " chip=", " chrome=", " end=", " point=", " promoted=", " chipBounds=" }
                .All(field => line.Contains(field, StringComparison.Ordinal))), "Overlay trace must include gesture, capture, activation and chrome geometry.");
            Require(entries.All(line => !line.Contains(sensitive, StringComparison.Ordinal) &&
                System.Text.RegularExpressions.Regex.IsMatch(line, @"^([A-Za-z]+=[A-Za-z0-9,.\-]+)( [A-Za-z]+=[A-Za-z0-9,.\-]+)*$")),
                "Overlay trace must contain only fixed keys, enum values and numbers, never annotation, title or exception text.");
        }
    }

    private static void CheckDiagnosticLogGuard(BitmapSource background)
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kairos-overlay-log-" + Guid.NewGuid().ToString("N"));
        string path = System.IO.Path.Combine(directory, "application.log");
        const string variable = "KAIROS_DIAGNOSTIC_LOG_DIRECTORY", sentinel = "unchanged diagnostic log";
        string? previous = Environment.GetEnvironmentVariable(variable);
        System.IO.Directory.CreateDirectory(directory);
        System.IO.File.WriteAllText(path, sentinel);
        try
        {
            Environment.SetEnvironmentVariable(variable, directory);
            App.LogOverlayDiagnostic("guard-check");
            var pointer = new FramePointer();
            var overlay = pointer.Create(background);
            overlay.BeginFrameGesture(new Point(100, 100));
            overlay.UpdateFrameGesture(new Point(500, 400));
            overlay.Cancel();
            Require(System.IO.File.ReadAllText(path) == sentinel,
                "Diagnostic mode must suppress the production overlay log hook, including anomalous overlay flushes.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            System.IO.File.Delete(path);
            System.IO.Directory.Delete(directory);
        }
    }

    private static RegionOverlay.NativeOverlaySnapshot DiagnosticNative() => new(
        new(RegionOverlay.ObservationStatus.Known, -1608, -931, 1040, 807), new(RegionOverlay.ObservationStatus.Known, 0, 0, 1024, 768),
        new(RegionOverlay.ObservationStatus.Known, -1600, -900, 1024, 768), new(RegionOverlay.ObservationStatus.Known, 1),
        new(RegionOverlay.ObservationStatus.Known, 0), new(RegionOverlay.ObservationStatus.Known, 144), RegionOverlay.ObservationStatus.Known, 0,
        ImmutableArray.Create(new RegionOverlay.MonitorObservation(0, false,
            new(RegionOverlay.ObservationStatus.Known, -1600, -900, 800, 768), new(RegionOverlay.ObservationStatus.Known, -1600, -900, 800, 728),
            new(RegionOverlay.ObservationStatus.Known, 144), new(RegionOverlay.ObservationStatus.Known, 144)),
            new RegionOverlay.MonitorObservation(1, true,
                new(RegionOverlay.ObservationStatus.Known, 0, 0, 1920, 1080), new(RegionOverlay.ObservationStatus.Known, 0, 0, 1920, 1040),
                new(RegionOverlay.ObservationStatus.Known, 96), new(RegionOverlay.ObservationStatus.Known, 96))),
        new(RegionOverlay.ObservationStatus.Known, 2), new(RegionOverlay.ObservationStatus.Known, 2), new(RegionOverlay.ObservationStatus.Known, 1));

    private static RegionOverlay DiagnosticOverlay(BitmapSource background, List<string> rows, bool enabled = true,
        Action<string>? offSink = null, bool editing = true, bool ocr = false, AnnotationCanvas? editor = null,
        Func<ImmutableArray<string>, RegionOverlay.LogWriteResult>? sink = null, Func<IntPtr, RegionOverlay.NativeOverlaySnapshot>? native = null) => new(background,
            new DrawingRectangle(-1600, -900, background.PixelWidth, background.PixelHeight), editing: editing,
            settings: new AppSettings { OverlayDiagnosticsEnabled = enabled }, ocrMode: ocr, editor: editor,
            logState: offSink, readCursor: () => new Point(100, 100), readLeftButton: () => MouseButtonState.Released,
            captureMouse: () => true, hasCapture: () => false, releaseCapture: () => { },
            appendTraceSnapshot: sink ?? (batch => { rows.AddRange(batch); return new(RegionOverlay.LogWriteStatus.Written); }),
            readNativeSnapshot: native ?? (_ => DiagnosticNative()));

    private static JsonElement[] DiagnosticRows(List<string> rows, string type, string? reason = null) => rows.Select(row =>
    {
        using var json = JsonDocument.Parse(row);
        return json.RootElement.Clone();
    }).Where(row => row.GetProperty("value").GetProperty("type").GetString() == type &&
        (reason == null || row.GetProperty("event").GetString() == reason)).ToArray();

    private static void RequireCompleteSnapshots(List<string> rows)
    {
        long expected = 1;
        int offset = 0;
        while (offset < rows.Count)
        {
            using var first = JsonDocument.Parse(rows[offset]);
            JsonElement state = first.RootElement;
            long seq = state.GetProperty("seq").GetInt64();
            Require(seq == expected++ && state.GetProperty("value").GetProperty("type").GetString() == "State",
                "The live trace must retain every snapshot from Created with a monotone sequence.");
            int count = state.GetProperty("parts").GetInt32();
            Require(count > 1 && offset + count <= rows.Count, "Every live snapshot must retain all declared parts.");
            for (int part = 0; part < count; part++)
            {
                using var item = JsonDocument.Parse(rows[offset + part]);
                JsonElement value = item.RootElement;
                Require(value.GetProperty("seq").GetInt64() == seq && value.GetProperty("capture").GetInt32() == state.GetProperty("capture").GetInt32() &&
                    value.GetProperty("run").GetString() == state.GetProperty("run").GetString() && value.GetProperty("parts").GetInt32() == count &&
                    value.GetProperty("part").GetInt32() == part && value.GetProperty("writeFailures").GetInt64() == 0 &&
                    (part != count - 1 || value.GetProperty("value").GetProperty("type").GetString() == "Complete"),
                    "Every retained snapshot must have ordered, correlated parts and a Complete terminator without write loss.");
            }
            offset += count;
        }
        Require(expected > 1, "The live trace must contain snapshots.");
    }

    private static void CheckLiveOverlayTrace(BitmapSource background)
    {
        var captures = new HashSet<int>();
        foreach (string mode in new[] { "new", "raw", "ocr", "fullscreen", "resume" })
        {
            var rows = new List<string>();
            var off = new List<string>();
            var overlay = DiagnosticOverlay(background, rows, offSink: off.Add, editing: mode != "raw", ocr: mode == "ocr",
                editor: mode == "resume" ? new AnnotationCanvas(background) : null);
            try
            {
                Require(rows.Count > 0 && DiagnosticRows(rows, "State", "Created").Length == 1 && DiagnosticRows(rows, "Complete", "Created").Length == 1,
                    "On must stream a complete Created snapshot for every healthy capture before show or close.");
                captures.Add(DiagnosticRows(rows, "State")[0].GetProperty("capture").GetInt32());
                if (mode == "new")
                {
                    RenderScene(overlay, background);
                    Require(DiagnosticRows(rows, "State", "Rendered").Last().GetProperty("value").GetProperty("submittedHandles").GetInt32() == 0,
                        "An early OnRender return must report an actual zero-handle render.");
                }
                overlay.SetRegion(mode == "fullscreen" ? new(0, 0, background.PixelWidth, background.PixelHeight) : new(100, 100, 400, 300));
                RenderScene(overlay, background);
                Require(!overlay.IsClosed && DiagnosticRows(rows, "State", "Rendered").Length > 0,
                    "On must stream actual render observations before close for every capture mode.");
                RegionOverlay.DiagnosticSnapshotCommand.Execute(null, overlay.Editor);
                int stableRows = rows.Count;
                RaiseMove(overlay);
                overlay.PreviewRoot.UpdateLayout();
                Require(rows.Count == stableRows, "Unchanged idle pointer and layout callbacks must not emit full snapshots.");
                if (mode == "new")
                {
                    int before = DiagnosticRows(rows, "State", "RegionChanged").Length;
                    for (int i = 1; i <= 64; i++) overlay.SetRegion(new(100 + i, 100, 400, 300));
                    var changed = DiagnosticRows(rows, "State", "RegionChanged").Skip(before).ToArray();
                    Require(changed.Length == 64 && changed.Select(r => r.GetProperty("value").GetProperty("selectionX").GetInt32()).SequenceEqual(Enumerable.Range(101, 64)),
                        "On must stream all 64 position changes while chrome visibility remains unchanged.");
                }
                Require(off.Count == 0, "On must never write to the Off anomaly sink.");
                Require(!overlay.IsVisible && new System.Windows.Interop.WindowInteropHelper(overlay).Handle == IntPtr.Zero,
                    "Live trace tests must not create an HWND or show a window.");
            }
            finally { overlay.Cancel(); }
            RequireCompleteSnapshots(rows);
            Require(DiagnosticRows(rows, "State", "WindowClosed").Length == 1 && DiagnosticRows(rows, "State")[^1].GetProperty("event").GetString() == "WindowClosed",
                "The On stream must end once after actual close cleanup.");
            int afterClose = rows.Count;
            overlay.Cancel();
            Require(rows.Count == afterClose && off.Count == 0, "Repeated close must not duplicate either diagnostic stream.");
        }
        Require(captures.Count == 5, "Every capture must retain its own diagnostic identity.");
        int nativeReads = 0;
        var silentRows = new List<string>();
        var silent = DiagnosticOverlay(background, silentRows, enabled: false, native: _ => { nativeReads++; throw new SelfCheckFailure("Off must not read native diagnostics."); });
        silent.SetRegion(new(100, 100, 400, 300));
        silent.Cancel();
        Require(nativeReads == 0 && silentRows.Count == 0, "Off must not run the On reader or sink.");
        foreach (bool enabled in new[] { true, false })
        {
            var settings = new AppSettings { OverlayDiagnosticsEnabled = enabled };
            var rows = new List<string>();
            var overlay = new RegionOverlay(background, new DrawingRectangle(0, 0, background.PixelWidth, background.PixelHeight), settings: settings,
                hasCapture: () => false, appendTraceSnapshot: batch => { rows.AddRange(batch); return new(RegionOverlay.LogWriteStatus.Written); }, readNativeSnapshot: _ => DiagnosticNative());
            try
            {
                settings.OverlayDiagnosticsEnabled = !enabled;
                int count = rows.Count;
                overlay.SetRegion(new(100, 100, 400, 300));
                Require(enabled ? rows.Count > count : rows.Count == 0, "The diagnostic preference must be captured once per overlay, never changed during a capture.");
            }
            finally { overlay.Cancel(); }
        }
    }

    private static void CheckDiagnosticActivePointer(BitmapSource background)
    {
        var rows = new List<string>();
        Point point = new(100, 100);
        var overlay = new RegionOverlay(background, new DrawingRectangle(-1600, -900, 1024, 768),
            settings: new() { OverlayDiagnosticsEnabled = true }, readCursor: () => point,
            readLeftButton: () => MouseButtonState.Pressed, hasCapture: () => false,
            appendTraceSnapshot: batch => { rows.AddRange(batch); return new(RegionOverlay.LogWriteStatus.Written); }, readNativeSnapshot: _ => DiagnosticNative());
        try
        {
            Require(overlay.BeginFrameGesture(point), "The active diagnostic fixture must start a frame gesture.");
            point = new(400, 300);
            RaiseMove(overlay);
            RenderScene(overlay, background);
            RegionOverlay.DiagnosticSnapshotCommand.Execute(null, overlay);
            long revision = DiagnosticRows(rows, "State").Last().GetProperty("value").GetProperty("revision").GetInt64();
            int before = rows.Count;
            for (int i = 0; i < 8; i++) RaiseMove(overlay);
            Require(rows.Count == before && overlay.IsFrameGestureActive && overlay.Selection == new Int32Rect(100, 100, 300, 200),
                "Identical Pressed positions during an active gesture must not emit diagnostic snapshots.");
            RegionOverlay.DiagnosticSnapshotCommand.Execute(null, overlay);
            Require(DiagnosticRows(rows, "State").Last().GetProperty("value").GetProperty("revision").GetInt64() == revision,
                "Identical active pointer callbacks must not advance diagnostic revisions.");
            before = DiagnosticRows(rows, "State", "RegionChanged").Length;
            point = new(401, 300);
            RaiseMove(overlay);
            Require(DiagnosticRows(rows, "State", "RegionChanged").Length == before + 1, "A changed active pointer position must remain observable.");
            overlay.EndFrameGesture();
            Require(DiagnosticRows(rows, "State", "GestureEnded").Length == 1 && overlay.Selection == new Int32Rect(100, 100, 301, 200),
                "Gesture completion at the same selection must remain observable.");
            before = DiagnosticRows(rows, "State", "Rendered").Length;
            RenderScene(overlay, background);
            Require(DiagnosticRows(rows, "State", "Rendered").Length > before, "Actual renders after identical callbacks must remain observable.");
            Int32Rect selection = overlay.Selection;
            Require(overlay.BeginFrameGesture(new(200, 150)), "The unchanged-selection fixture must begin a move gesture.");
            overlay.EndFrameGesture();
            Require(overlay.Selection == selection && DiagnosticRows(rows, "State", "GestureBegan").Length == 2 &&
                DiagnosticRows(rows, "State", "GestureEnded").Length == 2, "Gesture begin and end at an unchanged selection must remain observable.");
            overlay.Toolbar.Visibility = Visibility.Collapsed;
            before = DiagnosticRows(rows, "State", "RegionChanged").Length;
            overlay.SetRegion(selection);
            Require(overlay.ToolbarVisible && DiagnosticRows(rows, "State", "RegionChanged").Length == before + 1,
                "Visibility changes during an otherwise identical refresh must remain observable.");
        }
        finally { overlay.Cancel(); }
    }

    private sealed class OffscreenKeySource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private static bool RaiseDiagnosticKey(UIElement source, Key key = Key.F12)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new OffscreenKeySource(), 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = source };
        source.RaiseEvent(args);
        return args.Handled;
    }

    private static void CheckDiagnosticKeyAdapter(BitmapSource background)
    {
        foreach (bool enabled in new[] { true, false })
        {
            var rows = new List<string>();
            ModifierKeys modifiers = ModifierKeys.Control | ModifierKeys.Shift;
            var overlay = new RegionOverlay(background, new DrawingRectangle(-1600, -900, 1024, 768),
                settings: new() { OverlayDiagnosticsEnabled = enabled }, hasCapture: () => false,
                appendTraceSnapshot: batch => { rows.AddRange(batch); return new(RegionOverlay.LogWriteStatus.Written); },
                readNativeSnapshot: _ => DiagnosticNative(), readDiagnosticModifiers: () => modifiers);
            void SnapshotKey(UIElement source, bool pending = false)
            {
                int before = DiagnosticRows(rows, "State", "SnapshotRequested").Length;
                bool handled = RaiseDiagnosticKey(source);
                Require(DiagnosticRows(rows, "State", "SnapshotRequested").Length == before + (enabled ? 1 : 0) &&
                    (pending && !enabled || handled == enabled),
                    "The production preview-key adapter must route exactly one Ctrl+Shift+F12 snapshot before text, output and selection guards when On.");
            }
            try
            {
                SnapshotKey(overlay.Editor);
                overlay.SetRegion(new(100, 100, 400, 300));
                overlay.Editor.Tool = EditorTool.Text;
                overlay.Editor.BeginGesture(new(180, 180));
                var text = overlay.Editor.Children.OfType<TextBox>().Single();
                text.Text = "PRIVATE-KEY-SENTINEL";
                SnapshotKey(text);
                modifiers = ModifierKeys.Control;
                int before = rows.Count;
                Require(!RaiseDiagnosticKey(text) && rows.Count == before, "F12 with other modifiers must remain outside the diagnostic route.");
                modifiers |= ModifierKeys.Shift;
                Require(!RaiseDiagnosticKey(text, Key.F11) && rows.Count == before, "Other keys must remain outside the diagnostic route.");
                overlay.Editor.CommitText();
                bool verifiedPending = false;
                overlay.ExecuteOutput(CaptureCompletion.Copied, _ => { SnapshotKey(overlay.Editor, pending: true); verifiedPending = true; return false; });
                Require(verifiedPending && !overlay.IsClosed && !overlay.IsVisible && new System.Windows.Interop.WindowInteropHelper(overlay).Handle == IntPtr.Zero,
                    "Key-adapter checks must exercise pending output without a visible or native window.");
                Require(!rows.Any(row => row.Contains("PRIVATE-KEY-SENTINEL", StringComparison.Ordinal)), "Key snapshots must omit text content.");
            }
            finally { overlay.Cancel(); }
        }
    }

    private static void CheckDiagnosticRetention(BitmapSource background)
    {
        string directory = DiagnosticDirectory("capture-retention");
        var history = Enumerable.Range(1, 30000).Select(seq => JsonSerializer.Serialize(new
            { capture = 0, seq, part = 0, parts = 1, value = new { type = "Complete" } })).ToImmutableArray();
        Require(App.AppendOverlayTraceSnapshot(directory, history).Status == RegionOverlay.LogWriteStatus.Written,
            "Retention fixture must seed earlier scalar history to exercise both actual segments.");
        long historyBytes = new FileInfo(Path.Combine(directory, "overlay-diagnostics.log")).Length;
        for (int capture = 0; capture < 2; capture++)
        {
            var overlay = DiagnosticOverlay(background, new List<string>(), sink: batch => App.AppendOverlayTraceSnapshot(directory, batch));
            try
            {
                Require(overlay.BeginFrameGesture(new(100, 100)), "Retention fixture must begin an actual frame gesture.");
                for (int move = 1; move <= 64; move++)
                {
                    overlay.HandlePointerMove(new(400 + move, 300), MouseButtonState.Pressed);
                    RenderScene(overlay, background);
                }
                overlay.EndFrameGesture();
                RenderScene(overlay, background);
                Require(!overlay.IsVisible && new System.Windows.Interop.WindowInteropHelper(overlay).Handle == IntPtr.Zero,
                    "Retention captures must render offscreen without creating an HWND.");
                var current = File.ReadAllLines(Path.Combine(directory, "overlay-diagnostics.log"));
                Require(!overlay.IsClosed && DiagnosticRows(current.ToList(), "State", "Rendered").Length > 0,
                    "Real capture renders must reach the writer before close.");
            }
            finally { overlay.Cancel(); }
        }
        string[] segments = new[] { Path.Combine(directory, "overlay-diagnostics.log.previous"), Path.Combine(directory, "overlay-diagnostics.log") }
            .Where(File.Exists).ToArray();
        var retained = segments.SelectMany(File.ReadLines).ToList();
        var states = DiagnosticRows(retained, "State");
        var captures = states.GroupBy(row => row.GetProperty("capture").GetInt32()).ToArray();
        var snapshots = retained.Select(row => { using var json = JsonDocument.Parse(row); return (row, json.RootElement.Clone()); })
            .Where(item => item.Item2.GetProperty("capture").GetInt32() != 0)
            .GroupBy(item => (Capture: item.Item2.GetProperty("capture").GetInt32(), Sequence: item.Item2.GetProperty("seq").GetInt64()))
            .Select(group => new { group.Key.Capture, group.Key.Sequence, bytes = group.Sum(item => Encoding.UTF8.GetByteCount(item.row + Environment.NewLine)) }).ToArray();
        File.WriteAllText(Path.Combine(directory, "retention.json"), JsonSerializer.Serialize(new
        {
            segmentBytes = segments.Select(segment => new FileInfo(segment).Length).ToArray(),
            historyBytes,
            snapshotBytesMin = snapshots.Min(snapshot => snapshot.bytes), snapshotBytesMax = snapshots.Max(snapshot => snapshot.bytes),
            captures = captures.Select(group => new { capture = group.Key, snapshots = group.Count(),
                bytes = snapshots.Where(snapshot => snapshot.Capture == group.Key).Sum(snapshot => snapshot.bytes),
                firstSequence = group.First().GetProperty("seq").GetInt64(),
                created = group.Count(row => row.GetProperty("event").GetString() == "Created"),
                closed = group.Count(row => row.GetProperty("event").GetString() == "WindowClosed"),
                moves = group.Count(row => row.GetProperty("event").GetString() == "RegionChanged"),
                renders = group.Count(row => row.GetProperty("event").GetString() == "Rendered") }).ToArray()
        }));
        Require(segments.Length == 2 && captures.Length == 2 && captures.All(group => group.Count(row => row.GetProperty("event").GetString() == "Created") == 1 &&
            group.Count(row => row.GetProperty("event").GetString() == "WindowClosed") == 1 &&
            group.Count(row => row.GetProperty("event").GetString() == "RegionChanged") == 64 &&
            group.Count(row => row.GetProperty("event").GetString() == "Rendered") >= 64),
            "Two complete 64-Move+Render captures must retain both Created and WindowClosed in the actual two files.");
        foreach (var capture in captures)
        {
            var rows = retained.Where(row => { using var json = JsonDocument.Parse(row); return json.RootElement.GetProperty("capture").GetInt32() == capture.Key; }).ToList();
            RequireCompleteSnapshots(rows);
        }
        foreach (string segment in segments)
        {
            var parts = File.ReadLines(segment).Select(row => { using var json = JsonDocument.Parse(row); return json.RootElement.Clone(); })
                .GroupBy(row => (row.GetProperty("capture").GetInt32(), row.GetProperty("seq").GetInt64()));
            Require(parts.All(group => group.Count() == group.First().GetProperty("parts").GetInt32() &&
                group.Select(row => row.GetProperty("part").GetInt32()).SequenceEqual(Enumerable.Range(0, group.Count()))),
                "Rotation must keep each snapshot entirely in one actual segment.");
        }
    }

    private static void CheckDiagnosticRoutes(BitmapSource background)
    {
        foreach (bool enabled in new[] { true, false })
        {
            var rows = new List<string>();
            var off = new List<string>();
            var overlay = DiagnosticOverlay(background, rows, enabled, off.Add);
            try
            {
                overlay.SetRegion(new(100, 100, 400, 300));
                RenderScene(overlay, background);
                overlay.Toolbar.Visibility = Visibility.Collapsed;
                int before = rows.Count;
                Int32Rect selection = overlay.Selection;
                RegionOverlay.DiagnosticSnapshotCommand.Execute(null, overlay.Editor);
                Require(enabled ? rows.Count > before : rows.Count == before,
                    "The snapshot command must route from an overlay child only when On is enabled.");
                if (enabled)
                {
                    var toolbar = DiagnosticRows(rows, "Element", "SnapshotRequested").Last(r => r.GetProperty("value").GetProperty("role").GetString() == "Toolbar").GetProperty("value");
                    Require(toolbar.GetProperty("wanted").GetBoolean() && toolbar.GetProperty("visibility").GetString() == "Collapsed",
                        "A routed snapshot must distinguish Ready chrome from an unexpectedly collapsed toolbar before close.");
                }
                overlay.Editor.Tool = EditorTool.Text;
                overlay.Editor.BeginGesture(new(180, 180));
                var text = overlay.Editor.Children.OfType<TextBox>().Single();
                text.Text = "PRIVATE-TEXT-SENTINEL";
                before = rows.Count;
                RegionOverlay.DiagnosticSnapshotCommand.Execute(null, text);
                Require(enabled ? rows.Count > before : rows.Count == before, "The snapshot command must route through a live text editor.");
                overlay.Editor.CommitText();
                overlay.Toolbar.InvalidateArrange();
                bool arranged = overlay.Toolbar.IsArrangeValid;
                RegionOverlay.DiagnosticSnapshotCommand.Execute(null, overlay.Editor);
                Require(!arranged && !overlay.Toolbar.IsArrangeValid && overlay.Selection == selection && !overlay.IsFrameGestureActive &&
                    !overlay.IsMouseCaptured && new System.Windows.Interop.WindowInteropHelper(overlay).Handle == IntPtr.Zero,
                    "Snapshot reading must not arrange, change selection, capture input or create an HWND.");
                bool verifiedPending = false;
                overlay.ExecuteOutput(CaptureCompletion.Copied, _ =>
                {
                    int count = rows.Count;
                    RegionOverlay.DiagnosticSnapshotCommand.Execute(null, overlay.Editor);
                    Require(enabled ? rows.Count > count : rows.Count == count, "The routed snapshot must run while output is pending.");
                    verifiedPending = true;
                    return false;
                });
                Require(verifiedPending, "The pending-output snapshot assertion must complete outside the output error boundary.");
                Require(!rows.Any(row => row.Contains("PRIVATE-TEXT-SENTINEL", StringComparison.Ordinal)), "Snapshots must never read inline text.");
                overlay.Toolbar.Visibility = Visibility.Visible;
            }
            finally { overlay.Cancel(); }
            Require(off.Count == 0, "Snapshot routes must not contaminate the Off anomaly buffer.");
        }
    }

    private static void CheckDiagnosticMeasurements(BitmapSource background)
    {
        const string secret = "PRIVATE-OVERLAY-SENTINEL-937";
        var rows = new List<string>();
        var overlay = DiagnosticOverlay(background, rows);
        try
        {
            overlay.Title = secret;
            overlay.SetRegion(new(100, 100, 400, 300));
            RenderScene(overlay, background);
            var rendered = DiagnosticRows(rows, "State", "Rendered").Last().GetProperty("value");
            Require(rendered.GetProperty("submittedHandles").GetInt32() == 8 && rendered.GetProperty("renderSelectionX").GetInt32() == 100,
                "Render diagnostics must observe the eight actual submitted handle rectangles.");
            overlay.Toolbar.RenderTransform = new TranslateTransform(17, 23);
            RegionOverlay.DiagnosticSnapshotCommand.Execute(null, overlay.Editor);
            var pendingTransform = DiagnosticRows(rows, "Element", "SnapshotRequested").Last(r => r.GetProperty("value").GetProperty("role").GetString() == "Toolbar").GetProperty("value");
            Require(!pendingTransform.GetProperty("arrangeValid").GetBoolean() && !overlay.Toolbar.IsArrangeValid &&
                pendingTransform.GetProperty("observedSceneX").GetDouble() == pendingTransform.GetProperty("calculatedX").GetDouble(),
                "A snapshot must retain the old visual transform until the pending WPF arrange actually occurs.");
            RenderScene(overlay, background);
            RegionOverlay.DiagnosticSnapshotCommand.Execute(null, overlay.Editor);
            var transformed = DiagnosticRows(rows, "Element", "SnapshotRequested").Last(r => r.GetProperty("value").GetProperty("role").GetString() == "Toolbar").GetProperty("value");
            Require(transformed.GetProperty("observedSceneX").GetDouble() == transformed.GetProperty("calculatedX").GetDouble() + 17 &&
                transformed.GetProperty("observedSceneY").GetDouble() == transformed.GetProperty("calculatedY").GetDouble() + 23 &&
                transformed.GetProperty("expectedScreenX").GetDouble() == transformed.GetProperty("calculatedX").GetDouble() - 1600,
                "Actual WPF element transforms must remain independent from calculated and expected raster geometry.");
            overlay.Toolbar.RenderTransform = Transform.Identity;
            long renderNumber = DiagnosticRows(rows, "State", "SnapshotRequested").Last().GetProperty("value").GetProperty("renderNumber").GetInt64();
            overlay.SetRegion(new(200, 150, 400, 300));
            RegionOverlay.DiagnosticSnapshotCommand.Execute(null, overlay.Editor);
            var state = DiagnosticRows(rows, "State", "SnapshotRequested").Last().GetProperty("value");
            var handle = DiagnosticRows(rows, "Handle", "SnapshotRequested").Last(r => r.GetProperty("value").GetProperty("handle").GetString() == "Left, Top").GetProperty("value");
            Require(state.GetProperty("selectionX").GetInt32() == 200 && state.GetProperty("renderSelectionX").GetInt32() == 100 &&
                state.GetProperty("renderNumber").GetInt64() == renderNumber && handle.GetProperty("expectedSceneX").GetInt32() == 196 &&
                handle.GetProperty("submittedSceneX").GetInt32() == 96 && state.GetProperty("renderRevision").GetInt64() < state.GetProperty("revision").GetInt64(),
                "A new selection without render must retain the old submitted geometry and render revision.");
            overlay.Toolbar.Clip = new RectangleGeometry(new Rect(0, 0, 0, 0));
            overlay.PreviewRoot.Opacity = .25;
            RegionOverlay.DiagnosticSnapshotCommand.Execute(null, overlay.Editor);
            var toolbar = DiagnosticRows(rows, "Element", "SnapshotRequested").Last(r => r.GetProperty("value").GetProperty("role").GetString() == "Toolbar").GetProperty("value");
            Require(toolbar.GetProperty("effectiveOpacity").GetDouble() == .25 &&
                (toolbar.GetProperty("clippedSceneStatus").GetString() == "Empty" || toolbar.GetProperty("clippedSceneWidth").GetDouble() == 0),
                "Snapshots must observe an empty clip and parent opacity without claiming compositor visibility.");
            Require(toolbar.GetProperty("actualWidth").GetDouble() > 0 && toolbar.GetProperty("renderWidth").GetDouble() > 0 &&
                toolbar.GetProperty("measuredScreenStatus").GetString() == "NoPresentationSource", "Offscreen layout must preserve real sizes and explicitly missing device transforms.");
            Require(!overlay.ExecuteOutput(CaptureCompletion.Copied, _ => throw new InvalidOperationException(secret)), "Privacy fixture must exercise an output error.");
            Require(!rows.Any(row => row.Contains(secret, StringComparison.Ordinal)), "Diagnostics must omit title and exception messages.");
            CheckDiagnosticSchema(rows);
        }
        finally { overlay.Cancel(); }
        var missingRows = new List<string>();
        var missing = DiagnosticOverlay(background, missingRows, native: _ => DiagnosticNative() with
        {
            Window = new(RegionOverlay.ObservationStatus.NoPresentationSource), ClientScreen = new(RegionOverlay.ObservationStatus.NativeFailure, Code: 5),
            WindowDpi = new(RegionOverlay.ObservationStatus.NativeFailure, Code: 87), MonitorStatus = RegionOverlay.ObservationStatus.NativeFailure, MonitorCode = 5,
            Monitors = ImmutableArray<RegionOverlay.MonitorObservation>.Empty
        });
        try
        {
            var native = DiagnosticRows(missingRows, "NativeWindow", "Created").Single().GetProperty("value");
            Require(native.GetProperty("windowStatus").GetString() == "NoPresentationSource" && !native.TryGetProperty("windowX", out _) &&
                native.GetProperty("clientScreenCode").GetInt32() == 5 && native.GetProperty("dpiCode").GetInt32() == 87 && !native.TryGetProperty("dpi", out _),
                "Missing native subqueries must keep independent statuses and codes without invented zero values.");
        }
        finally { missing.Cancel(); }
    }

    private static void CheckDiagnosticTransforms()
    {
        foreach (double scale in new[] { 1, 1.25, 1.5, 2 })
        {
            var local = new RegionOverlay.DiagnosticRect(RegionOverlay.ObservationStatus.Known, 10, 20, 30, 40);
            var client = new RegionOverlay.DiagnosticRect(RegionOverlay.ObservationStatus.Known, -1600, -900, 1024, 768);
            var result = RegionOverlay.MapToScreen(local, RegionOverlay.DiagnosticMatrix.Known(new Matrix(1 / scale, 0, 0, 1 / scale, 8, 12)),
                RegionOverlay.DiagnosticMatrix.Known(new Matrix(scale, 0, 0, scale, 0, 0)), client);
            Require(result.Status == RegionOverlay.ObservationStatus.Known && result.X == -1590 + 8 * scale && result.Y == -880 + 12 * scale &&
                result.Width == 30 && result.Height == 40, "Measured screen coordinates must follow actual client and device matrices independently of desktop origin.");
        }
        var rows = new List<string>();
        var background = Pattern(3360, 2560);
        var native = DiagnosticNative() with
        {
            Monitors = ImmutableArray.Create(new RegionOverlay.MonitorObservation(0, false,
                new(RegionOverlay.ObservationStatus.Known, -1440, -540, 1440, 2560), new(RegionOverlay.ObservationStatus.Known, -1440, -540, 1440, 2520),
                new(RegionOverlay.ObservationStatus.Known, 144), new(RegionOverlay.ObservationStatus.Known, 144)), DiagnosticNative().Monitors[1])
        };
        var overlay = new RegionOverlay(background, new DrawingRectangle(-1440, -540, 3360, 2560), settings: new() { OverlayDiagnosticsEnabled = true },
            hasCapture: () => false, appendTraceSnapshot: batch => { rows.AddRange(batch); return new(RegionOverlay.LogWriteStatus.Written); }, readNativeSnapshot: _ => native);
        try
        {
            overlay.SetRegion(new(1540, 540, 400, 1080));
            RenderScene(overlay, background);
            RegionOverlay.DiagnosticSnapshotCommand.Execute(null, overlay.Editor);
            var monitors = DiagnosticRows(rows, "Monitor", "SnapshotRequested").Select(r => r.GetProperty("value")).ToArray();
            var elements = DiagnosticRows(rows, "Element", "SnapshotRequested").Select(r => r.GetProperty("value"))
                .Where(v => v.GetProperty("role").GetString() is "Toolbar" or "Chip").ToArray();
            Rect Rectangle(JsonElement row, string prefix) => new(row.GetProperty(prefix + "X").GetDouble(), row.GetProperty(prefix + "Y").GetDouble(),
                row.GetProperty(prefix + "Width").GetDouble(), row.GetProperty(prefix + "Height").GetDouble());
            Require(elements.Length == 2 && elements.All(element => monitors.All(monitor =>
                Rect.Intersect(Rectangle(element, "expectedScreen"), Rectangle(monitor, "bounds")).IsEmpty &&
                Rect.Intersect(Rectangle(element, "expectedScreen"), Rectangle(monitor, "workarea")).IsEmpty)),
                "Raw screen rectangles, monitors and workareas must reveal toolbar and chip in a physical monitor gap offline.");
            Require(monitors[0].GetProperty("boundsX").GetInt32() == -1440 && monitors[0].GetProperty("dpiX").GetInt32() == 144 &&
                monitors[1].GetProperty("dpiX").GetInt32() == 96 && monitors[1].GetProperty("workareaHeight").GetInt32() == 1040,
                "Monitors must retain negative origins, independent workareas and 96/144 DPI.");
        }
        finally { overlay.Cancel(); }
    }

    private static string DiagnosticDirectory(string name)
    {
        string path = Path.Combine(Environment.GetEnvironmentVariable("KAIROS_DIAGNOSTIC_LOG_DIRECTORY") ?? Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static void CheckDiagnosticFileStream(BitmapSource background)
    {
        string directory = DiagnosticDirectory("live-stream");
        string path = Path.Combine(directory, "overlay-diagnostics.log");
        var rows = new List<string>();
        int failures = 0;
        var overlay = DiagnosticOverlay(background, rows, sink: batch =>
        {
            rows.AddRange(batch);
            if (failures++ == 0) return new(RegionOverlay.LogWriteStatus.Failed, 5);
            return App.AppendOverlayTraceSnapshot(directory, batch);
        });
        try
        {
            overlay.SetRegion(new(100, 100, 400, 300));
            string[] disk = File.ReadAllLines(path);
            Require(!overlay.IsClosed && disk.Length > 1 && !disk.Any(row => row.Contains("\"event\":\"Created\"", StringComparison.Ordinal)) &&
                disk.Any(row => row.Contains("\"event\":\"RegionChanged\"", StringComparison.Ordinal)), "The actual temp file must be readable with current snapshots before close.");
            using var recovered = JsonDocument.Parse(disk[0]);
            Require(failures == DiagnosticRows(rows, "State").Length, "Each overlay snapshot must make exactly one writer attempt.");
            Require(recovered.RootElement.GetProperty("writeFailures").GetInt64() == 1 && recovered.RootElement.GetProperty("lastWriteCode").GetInt32() == 5,
                "A failed snapshot must be entirely absent and the next batch must report its loss and numeric error code.");
            Require(disk.All(row => { using var item = JsonDocument.Parse(row); return item.RootElement.GetProperty("writeFailures").GetInt64() == 1; }) &&
                recovered.RootElement.GetProperty("seq").GetInt64() == 2, "Every row in a recovered batch must carry the same cumulative snapshot-loss count.");
            Require(App.TryLogOverlayTraceSnapshot(ImmutableArray.Create("guard-check")).Status == RegionOverlay.LogWriteStatus.Suppressed,
                "App diagnostics mode must suppress the On production sink.");
            string? previous = Environment.GetEnvironmentVariable("KAIROS_DIAGNOSTIC_LOG_DIRECTORY");
            try
            {
                Environment.SetEnvironmentVariable("KAIROS_DIAGNOSTIC_LOG_DIRECTORY", directory);
                Require(App.DiagnosticLogDirectory == directory, "The folder action and On logger must share the environment target.");
                var production = new RegionOverlay(background, new DrawingRectangle(0, 0, background.PixelWidth, background.PixelHeight), settings: new() { OverlayDiagnosticsEnabled = true });
                production.Cancel();
                Require(File.ReadAllLines(path).SequenceEqual(disk), "The guarded production On overlay must not append even with the setting enabled.");
            }
            finally { Environment.SetEnvironmentVariable("KAIROS_DIAGNOSTIC_LOG_DIRECTORY", previous); }
        }
        finally { overlay.Cancel(); }
    }

    private static void CheckDiagnosticRotation()
    {
        string directory = DiagnosticDirectory("hard-rotation"), path = Path.Combine(directory, "overlay-diagnostics.log");
        string application = Path.Combine(directory, "application.log");
        File.WriteAllText(application, "OFF-LOGGER-SENTINEL");
        string row = "{\"type\":\"Ütf8\",\"value\":\"" + new string('ä', 1024) + "\"}";
        var batch = Enumerable.Repeat(row, 32).ToImmutableArray();
        int bytes = Encoding.UTF8.GetByteCount(row + Environment.NewLine) * batch.Length, rotations = 0;
        long previousLength = 0;
        for (int i = 0; i < 270; i++)
        {
            Require(App.AppendOverlayTraceSnapshot(directory, batch).Status == RegionOverlay.LogWriteStatus.Written, "Hard rotation fixture must write every bounded UTF-8 batch.");
            long length = new FileInfo(path).Length;
            Require(length <= 8 * 1024 * 1024, "Every UTF-8 batch must keep the current segment within 8 MiB.");
            if (length < previousLength) rotations++;
            previousLength = length;
            if (File.Exists(path + ".previous")) Require(new FileInfo(path + ".previous").Length <= 8 * 1024 * 1024,
                "The previous segment must also retain the hard byte limit.");
        }
        Require(rotations >= 2 && Directory.GetFiles(directory, "overlay-diagnostics.log*").Length == 2 &&
            new FileInfo(path).Length % bytes == 0 && File.ReadAllText(application) == "OFF-LOGGER-SENTINEL",
            "Rotation must retain exactly two complete bounded segments and leave application.log unchanged.");
        File.WriteAllText(Path.Combine(directory, "rotation.json"), JsonSerializer.Serialize(new
            { rotations, batchBytes = bytes, currentBytes = new FileInfo(path).Length, previousBytes = new FileInfo(path + ".previous").Length }));
        foreach (string segment in new[] { path, path + ".previous" })
            foreach (string line in File.ReadLines(segment)) using (JsonDocument.Parse(line)) { }
        long before = new FileInfo(path).Length;
        Require(App.AppendOverlayTraceSnapshot(directory, ImmutableArray.Create(new string('x', 8 * 1024 * 1024))).Status == RegionOverlay.LogWriteStatus.Failed &&
            new FileInfo(path).Length == before && App.AppendOverlayTraceSnapshot("\0invalid", batch).Status == RegionOverlay.LogWriteStatus.Failed,
            "Oversized batches and invalid explicit directories must fail without fallback or oversized writes.");
    }

    private static void CheckDiagnosticSchema(List<string> rows)
    {
        string[] header = { "schema", "run", "build", "process", "processStartUtc", "capture", "seq", "ticks", "frequency", "utc", "event", "source", "data", "part", "parts", "writeFailures", "lastWriteCode", "value" };
        foreach (string row in rows)
        {
            using var json = JsonDocument.Parse(row);
            Require(json.RootElement.EnumerateObject().Select(p => p.Name).SequenceEqual(header), "Every diagnostic row must use the fixed envelope schema.");
            Require(Guid.TryParse(json.RootElement.GetProperty("run").GetString(), out _) && Guid.TryParse(json.RootElement.GetProperty("build").GetString(), out _),
                "Diagnostic identity must use generated run and build identifiers.");
            foreach (JsonProperty field in json.RootElement.GetProperty("value").EnumerateObject())
            {
                Require(AllowedDiagnosticFields(json.RootElement.GetProperty("value").GetProperty("type").GetString()!).Contains(field.Name),
                    "Snapshot fields must belong to the fixed row schema, including privacy-sensitive additions.");
                Require(field.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.String,
                    "Snapshot fields must be scalar numeric, boolean, enum or status values, never objects or arrays.");
                if (field.Value.ValueKind == JsonValueKind.String)
                    Require(System.Text.RegularExpressions.Regex.IsMatch(field.Value.GetString()!, @"^[A-Za-z]+(, [A-Za-z]+)*$"),
                        "Snapshot strings must be fixed enum or row-kind values, never arbitrary content.");
                Require(!new[] { "title", "text", "path", "message", "token", "image", "hwnd" }.Any(forbidden => field.Name.Equals(forbidden, StringComparison.OrdinalIgnoreCase)),
                    "The snapshot schema must exclude private content and handles.");
            }
        }
    }

    private static HashSet<string> AllowedDiagnosticFields(string type)
    {
        var fields = new HashSet<string> { "type" };
        string plain = type switch
        {
            "State" => "chrome gesture handle end tool revision pointX pointY editing ocr closed outputPending active promoted capture editorGesture editingText windowState topmost expectedHandles renderNumber renderTicks renderRevision renderChrome submittedHandles",
            "Dpi" => "windowX windowY sceneX sceneY",
            "NativeWindow" => "monitorStatus monitorCode monitors",
            "Element" => "role wanted visibility isVisible loaded hitTestVisible effectiveOpacity measureValid arrangeValid actualWidth actualHeight desiredWidth desiredHeight renderWidth renderHeight calculatedRevision calculatedCurrent",
            "Clip" => "role depth clipToBounds isVisible opacity accuracy",
            "Handle" => "handle wanted parentVisible parentOpacity renderNumber renderRevision",
            "Monitor" => "index primary",
            "Complete" => "",
            _ => throw new SelfCheckFailure("Unknown diagnostic row type.")
        };
        fields.UnionWith(plain.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string rectangles = type switch
        {
            "State" => "selection desktop renderSelection", "Dpi" => "previous current", "NativeWindow" => "window client clientScreen",
            "Element" => "calculated observedScene expectedScreen measuredScreen clippedScene", "Clip" => "bounds explicit",
            "Handle" => "expectedScene submittedScene expectedScreen measuredScreen",
            "Monitor" => "bounds workarea", _ => ""
        };
        foreach (string prefix in rectangles.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            foreach (string suffix in new[] { "Status", "Code", "X", "Y", "Width", "Height" }) fields.Add(prefix + suffix);
        string numbers = type switch { "NativeWindow" => "visible iconic dpi processAwareness threadAwareness perMonitorV2", "Element" => "canvasX canvasY", "Monitor" => "dpiX dpiY", _ => "" };
        foreach (string prefix in numbers.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            foreach (string suffix in new[] { "", "Status", "Code" }) fields.Add(prefix + suffix);
        string matrices = type switch { "Dpi" => "toDevice sceneToClient", "Element" => "toClient", _ => "" };
        foreach (string prefix in matrices.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            foreach (string suffix in new[] { "Status", "M11", "M12", "M21", "M22", "OffsetX", "OffsetY" }) fields.Add(prefix + suffix);
        return fields;
    }

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
                overlay.Result.Region == crop && !overlay.Result.ReturnToPreviousForeground && ReferenceEquals(overlay.Result.Editor, overlay.Editor) &&
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
        Require(actions.TakeLast(6).SequenceEqual(new[] { "Ocr", "Close", "Pin", "Save", "Copy", "Check" }),
            "Right action group must be Ocr, Close, Pin, Save, Copy, Check.");
        ButtonBase ocr = Buttons(overlay.Toolbar).Single(b => Equals(b.Tag, "Ocr"));
        ButtonBase close = Buttons(overlay.Toolbar).Single(b => Equals(b.Tag, "Close"));
        ButtonBase delete = Buttons(overlay.Toolbar).Single(b => Equals(b.Tag, "Delete"));
        ButtonBase copy = Buttons(overlay.Toolbar).Single(b => Equals(b.Tag, "Copy"));
        ButtonBase check = Buttons(overlay.Toolbar).Single(b => Equals(b.Tag, "Check"));
        var actionPanel = (Panel)ocr.Parent;
        Require(actionPanel.Children.Cast<FrameworkElement>().Select(child => child is Border ? "Divider" : child.Tag as string)
            .SequenceEqual(new[] { "Ocr", "Divider", "Close", "Pin", "Save", "Copy", "Check" }),
            "The action divider and adjacent Copy/Check buttons must retain their exact order.");
        Require(Equals(copy.ToolTip, "Bild kopieren und schließen · Enter / Strg+C") &&
            Equals(check.ToolTip, "Fertig · Bild kopieren und zur letzten Anwendung zurückkehren") &&
            Buttons(overlay.Toolbar).Count(button => button.ToolTip is string tip && tip.Contains("Enter / Strg+C", StringComparison.Ordinal)) == 1,
            "Only Copy must advertise Enter / Strg+C; both action tooltips must be exact.");
        Require(ReferenceEquals(copy.Style, overlay.FindResource("GlassIconButton")) &&
            ReferenceEquals(check.Style, overlay.FindResource("GlassConfirmButton")) &&
            copy.Content is System.Windows.Shapes.Path copyIcon && ReferenceEquals(copyIcon.Data, overlay.FindResource("Glyph.Copy")),
            "Copy must reuse Glyph.Copy and the secondary style; Check must remain highlighted.");
        Require(ReferenceEquals(ocr.Parent, close.Parent) && !ReferenceEquals(ocr.Parent, delete.Parent),
            "Text recognition must sit in the right action group, not beside the editing tools.");
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

    private static byte[] PrepareCopy(RegionOverlay overlay, Int32Rect crop)
    {
        overlay.SetRegion(crop);
        Draw(overlay.Editor, EditorTool.Redact, new Point(80, 70), new Point(120, 110));
        Draw(overlay.Editor, EditorTool.Ellipse, new Point(180, 170), new Point(220, 210));
        overlay.Editor.Undo();
        overlay.Editor.Tool = EditorTool.Select;
        overlay.Editor.BeginGesture(new Point(100, 90));
        overlay.Editor.EndGesture();
        return Bytes(overlay.Editor.Flatten(crop));
    }

    private static void ClickAction(RegionOverlay overlay, string action) =>
        Buttons(overlay.Toolbar).Single(button => Equals(button.Tag, action)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void CheckCopyButtons(BitmapSource background)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, $"copy-buttons-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var crop = new Int32Rect(30, 20, 700, 430);
            foreach (bool saveOnCopy in new[] { false, true })
            {
                var copies = new List<BitmapSource>();
                RegionOverlay? active = null;
                Action<BitmapSource> copyImage = image =>
                {
                    Require(active != null && !active.IsClosed && active.Result.Completion == CaptureCompletion.Cancelled &&
                        !active.Result.ReturnToPreviousForeground, "Return intent and completion must not be published before the copy succeeds.");
                    copies.Add(image);
                };
                foreach (string action in new[] { "Copy", "Check" })
                {
                    string folder = Path.Combine(directory, $"{saveOnCopy}-{action}");
                    var settings = new AppSettings { SaveOnCopy = saveOnCopy, SaveFolder = folder };
                    var overlay = new RegionOverlay(background, new DrawingRectangle(0, 0, background.PixelWidth, background.PixelHeight),
                        settings: settings, copyImage: copyImage);
                    active = overlay;
                    byte[] expected = PrepareCopy(overlay, crop);
                    ClickAction(overlay, action);
                    BitmapSource copied = copies.Last();
                    Require(copies.Count == (action == "Copy" ? 1 : 2) && overlay.IsClosed &&
                        overlay.Result.Completion == CaptureCompletion.Copied && overlay.Result.ReturnToPreviousForeground == (action == "Check") &&
                        overlay.Result.Region == crop && ReferenceEquals(overlay.Result.Editor, overlay.Editor) &&
                        copied.PixelWidth == crop.Width && copied.PixelHeight == crop.Height && expected.SequenceEqual(Bytes(copied)),
                        $"{action}/SaveOnCopy={saveOnCopy}: the real button must copy the annotated crop and publish only its own return intent.");
                    Require(Pixel(copied, 70, 70).SequenceEqual(new byte[] { 0, 0, 0, 255 }) &&
                        !Pixel(background, 100, 90).SequenceEqual(Pixel(copied, 70, 70)),
                        "The copied crop must contain the independently known black redaction pixel.");
                    if (saveOnCopy)
                        Require(overlay.Result.SavedPath != null && Directory.GetFiles(folder).Single() == overlay.Result.SavedPath &&
                            expected.SequenceEqual(Bytes(ImageOutput.Load(overlay.Result.SavedPath))),
                            "Both buttons must save the exact copied pixels through SaveOnCopy.");
                    else Require(overlay.Result.SavedPath == null && !Directory.Exists(folder), "SaveOnCopy=false must not write a file.");
                }
                Require(Bytes(copies[0]).SequenceEqual(Bytes(copies[1])), "Copy and Check must export identical annotated pixels.");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void CheckCopyFailures(BitmapSource background)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, $"copy-failures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var crop = new Int32Rect(30, 20, 700, 430);
            foreach (string action in new[] { "Copy", "Check" })
            foreach (string fault in new[] { "Clipboard", "ClipboardAfterSave", "Save" })
            {
                string folder = Path.Combine(directory, action + fault);
                if (fault == "Save") File.WriteAllText(folder, "This file blocks directory creation.");
                var settings = new AppSettings { SaveOnCopy = fault != "Clipboard", SaveFolder = folder };
                int copies = 0;
                bool failCopy = true;
                var overlay = new RegionOverlay(background, new DrawingRectangle(0, 0, background.PixelWidth, background.PixelHeight),
                    settings: settings, copyImage: _ => { copies++; if (failCopy) throw new InvalidOperationException("offline clipboard failure"); });
                byte[] expected = PrepareCopy(overlay, crop);
                AnnotationCanvas editor = overlay.Editor;
                object? identity = editor.SelectedIdentity;
                ClickAction(overlay, action);
                Require(!overlay.IsClosed && overlay.ToolbarVisible && overlay.Selection == crop && ReferenceEquals(overlay.Editor, editor) &&
                    identity != null && ReferenceEquals(editor.SelectedIdentity, identity) && editor.CanUndo && editor.CanRedo &&
                    expected.SequenceEqual(Bytes(editor.Flatten(crop))) && overlay.Result.Completion == CaptureCompletion.Cancelled &&
                    !overlay.Result.ReturnToPreviousForeground && copies == (fault == "Save" ? 0 : 1),
                    $"{action}/{fault}: failure must retain the live editor, crop, selected annotation and history without return intent.");
                if (fault == "ClipboardAfterSave")
                    Require(expected.SequenceEqual(Bytes(ImageOutput.Load(Directory.GetFiles(folder).Single()))),
                        "SaveOnCopy must finish the exact file before a clipboard failure leaves the editor open.");
                editor.Undo();
                Require(!expected.SequenceEqual(Bytes(editor.Flatten(crop))), "Undo after failed output must still remove the annotation.");
                editor.Redo();
                Require(expected.SequenceEqual(Bytes(editor.Flatten(crop))) && editor.CanRedo, "Redo after failed output must restore pixels and retain later history.");
                settings.SaveOnCopy = false;
                failCopy = false;
                ClickAction(overlay, "Copy");
                Require(overlay.IsClosed && overlay.Result.Completion == CaptureCompletion.Copied && !overlay.Result.ReturnToPreviousForeground &&
                    copies == (fault == "Save" ? 1 : 2), "A successful Copy after either failed button must not inherit return intent.");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void CheckCopyRoutes(BitmapSource background)
    {
        var crop = new Int32Rect(30, 20, 700, 430);
        foreach (string route in new[] { "Copy", "Enter", "CtrlC", "DoubleClick", "Save", "Pin", "Ocr", "Close" })
        {
            var original = new RegionOverlay(background, new DrawingRectangle(0, 0, background.PixelWidth, background.PixelHeight), copyImage: _ => { });
            byte[] expected = PrepareCopy(original, crop);
            ClickAction(original, "Check");
            CaptureService.Remember(original.Result);
            CaptureSessionResult session = CaptureService.LastSession!;
            Require(session.ReturnToPreviousForeground, "Resume fixture must remember a successful Check with return intent.");
            bool failCopy = true;
            int copies = 0;
            var resumed = new RegionOverlay(session.Screenshot!, session.Bounds, editor: session.Editor,
                copyImage: image =>
                {
                    if (failCopy) throw new InvalidOperationException("offline clipboard failure before alternate completion");
                    Require(expected.SequenceEqual(Bytes(image)), "Resumed copy must export the original annotated crop.");
                    copies++;
                }, saveImage: (image, _, _) => { Require(expected.SequenceEqual(Bytes(image)), "Resumed Save must export the same crop."); return "offline-save.png"; });
            resumed.SetRegion(session.Region);
            Require(!resumed.Result.ReturnToPreviousForeground, "A resumed overlay must start without historical return intent.");
            ClickAction(resumed, "Check");
            Require(!resumed.IsClosed && !resumed.Result.ReturnToPreviousForeground, "A failed Check must not publish return intent before another route.");
            failCopy = false;
            if (route == "Enter") Require(resumed.HandleSessionKey(Key.Enter), "Enter must remain a copy shortcut.");
            else if (route == "CtrlC") Require(resumed.HandleSessionKey(Key.C, ModifierKeys.Control), "Ctrl+C must remain a copy shortcut.");
            else if (route == "DoubleClick") PointerClick(resumed, new Point(600, 260), 2);
            else ClickAction(resumed, route);
            CaptureCompletion completion = route switch { "Save" => CaptureCompletion.Saved, "Pin" => CaptureCompletion.Pinned,
                "Ocr" => CaptureCompletion.Ocr, "Close" => CaptureCompletion.Cancelled, _ => CaptureCompletion.Copied };
            Require(resumed.IsClosed && resumed.Result.Completion == completion && !resumed.Result.ReturnToPreviousForeground &&
                copies == (completion == CaptureCompletion.Copied ? 1 : 0),
                $"{route}: completion after Resume and a failed Check must retain its route without historical return intent.");
        }
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

    private static void CheckToolbarReachability()
    {
        foreach (int width in new[] { 800, 1024, 1440 })
        {
            BitmapSource background = Pattern(width, 768);
            var overlay = Create(background);
            foreach (bool error in new[] { false, true })
            foreach (Int32Rect crop in new[] { new Int32Rect(0, 0, 2, 2), new Int32Rect(width - 2, 0, 2, 2),
                new Int32Rect(0, 766, 2, 2), new Int32Rect(width - 2, 766, 2, 2), new Int32Rect(0, 0, width, 768) })
            {
                overlay.SetRegion(crop);
                if (error) Require(!overlay.ExecuteOutput(CaptureCompletion.Copied,
                    _ => throw new InvalidOperationException("Offline output failure. The toolbar must retain every editing and output control.")),
                    "Reachability fixture must leave an output error visible.");
                RemeasureToolbar(overlay);
                Layout(overlay.PreviewRoot, width, 768);
                Rect desktop = new(0, 0, width, 768);
                Require(desktop.Contains(overlay.ToolbarBounds), $"{width}/{crop}/error={error}: measured toolbar must stay in the desktop.");
                FrameworkElement[] controls = Buttons(overlay.Toolbar).Cast<FrameworkElement>()
                    .Append(Descendant<ComboBox>(overlay.Toolbar)!).ToArray();
                var rectangles = new List<Rect>();
                foreach (FrameworkElement control in controls)
                {
                    Rect bounds = control.TransformToAncestor(overlay.PreviewRoot).TransformBounds(new Rect(control.RenderSize));
                    Require(bounds.Width >= 24 && bounds.Height >= 30 && desktop.Contains(bounds) && overlay.ToolbarBounds.Contains(bounds),
                        $"{width}/{crop}/error={error}: {control.Tag ?? control.ToolTip} must have a full visible button rectangle, actual={bounds}.");
                    Require(rectangles.All(other => { Rect overlap = Rect.Intersect(other, bounds); return overlap.IsEmpty || overlap.Width == 0 || overlap.Height == 0; }),
                        "Toolbar controls must not overlap after wrapping.");
                    rectangles.Add(bounds);
                    if (!control.IsEnabled) continue;
                    foreach (Point sample in new[] { new Point(.25, .5), new Point(.5, .25), new Point(.5, .5), new Point(.75, .5), new Point(.5, .75) })
                    {
                        Point point = new(bounds.X + bounds.Width * sample.X, bounds.Y + bounds.Height * sample.Y);
                        DependencyObject? hit = null;
                        VisualTreeHelper.HitTest(overlay.PreviewRoot,
                            candidate => candidate is UIElement element && (!element.IsHitTestVisible || element.Visibility != Visibility.Visible)
                                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
                            result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(point));
                        while (hit != null && !ReferenceEquals(hit, control)) hit = VisualTreeHelper.GetParent(hit);
                        Require(ReferenceEquals(hit, control),
                            $"{width}/{crop}/error={error}: {control.Tag ?? control.ToolTip} must be reachable by a real hit-test at {point}.");
                    }
                }
            }
            overlay.Cancel();
        }
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
        if (!condition) throw new SelfCheckFailure(message);
    }
}
