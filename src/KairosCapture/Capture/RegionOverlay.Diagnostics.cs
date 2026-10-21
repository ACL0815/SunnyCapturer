using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using KairosCapture.Editor;

namespace KairosCapture.Capture;

internal sealed partial class RegionOverlay
{
    internal enum LogWriteStatus { Written, Suppressed, Failed }
    internal readonly record struct LogWriteResult(LogWriteStatus Status, int Code = 0);
    internal enum ObservationStatus { Known, Empty, NotObserved, NoPresentationSource, TransformFailure, NativeFailure }
    internal readonly record struct DiagnosticNumber(ObservationStatus Status, double Value = 0, int Code = 0);
    internal readonly record struct DiagnosticRect(ObservationStatus Status, double X = 0, double Y = 0, double Width = 0, double Height = 0, int Code = 0)
    {
        internal static DiagnosticRect Known(Rect value) => value.IsEmpty ? new(ObservationStatus.Empty) :
            double.IsFinite(value.X + value.Y + value.Width + value.Height)
                ? new(ObservationStatus.Known, value.X, value.Y, value.Width, value.Height) : new(ObservationStatus.NotObserved);
        internal Rect Rectangle => new(X, Y, Width, Height);
    }
    internal readonly record struct DiagnosticMatrix(ObservationStatus Status, double M11 = 0, double M12 = 0, double M21 = 0,
        double M22 = 0, double OffsetX = 0, double OffsetY = 0)
    {
        internal static DiagnosticMatrix Known(Matrix m) => new(ObservationStatus.Known, m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY);
        internal Matrix Matrix => new(M11, M12, M21, M22, OffsetX, OffsetY);
    }
    internal sealed record MonitorObservation(int Index, bool Primary, DiagnosticRect Bounds, DiagnosticRect Workarea,
        DiagnosticNumber DpiX, DiagnosticNumber DpiY);
    internal sealed record NativeOverlaySnapshot(DiagnosticRect Window, DiagnosticRect Client, DiagnosticRect ClientScreen,
        DiagnosticNumber Visible, DiagnosticNumber Iconic, DiagnosticNumber WindowDpi, ObservationStatus MonitorStatus, int MonitorCode,
        ImmutableArray<MonitorObservation> Monitors, DiagnosticNumber ProcessAwareness, DiagnosticNumber ThreadAwareness, DiagnosticNumber PerMonitorV2);

    private enum ElementRole { Scene, Toolbar, Chip, FrameChrome }
    private enum ClipAccuracy { Rectangular, BoundsOnly }
    private sealed record ClipObservation(int Depth, bool ClipToBounds, bool Visible, double Opacity, DiagnosticRect Bounds,
        DiagnosticRect ExplicitClip, ClipAccuracy Accuracy);
    private sealed record ElementObservation(ElementRole Role, bool Wanted, Visibility Visibility, bool IsVisible, bool Loaded,
        bool HitTestVisible, double EffectiveOpacity, bool MeasureValid, bool ArrangeValid, double ActualWidth, double ActualHeight,
        double DesiredWidth, double DesiredHeight, double RenderWidth, double RenderHeight, DiagnosticNumber CanvasX, DiagnosticNumber CanvasY,
        DiagnosticRect Calculated, long CalculatedRevision, bool CalculatedCurrent, DiagnosticRect ObservedScene,
        DiagnosticRect ExpectedScreen, DiagnosticRect MeasuredScreen, DiagnosticMatrix ToClient, DiagnosticRect ClippedScene,
        ImmutableArray<ClipObservation> Clips);
    private readonly record struct SubmittedHandle(FrameHandle Handle, DiagnosticRect Rectangle);
    private sealed record ChromeRenderObservation(long Number, long Ticks, long Revision, FrameChromeState Chrome, DiagnosticRect Selection,
        ImmutableArray<SubmittedHandle> Handles);
    private sealed record SessionObservation(FrameChromeState Chrome, FrameGesture Gesture, FrameHandle Handle, GestureEnd End, EditorTool Tool,
        long Revision, double PointX, double PointY, bool Editing, bool Ocr, bool Closed, bool OutputPending, bool Active, bool Promoted,
        bool Capture, bool EditorGesture, bool EditingText, WindowState WindowState, bool Topmost);
    private sealed record DpiObservation(double WindowX, double WindowY, double SceneX, double SceneY, DiagnosticMatrix ToDevice,
        DiagnosticMatrix SceneToClient, DiagnosticRect Previous, DiagnosticRect Current);
    private sealed record OverlaySnapshot(SessionObservation State, DiagnosticRect Selection, DiagnosticRect Desktop,
        ImmutableArray<ElementObservation> Elements, ChromeRenderObservation Render, NativeOverlaySnapshot Native, DpiObservation Dpi);

    internal static readonly RoutedCommand DiagnosticSnapshotCommand = new();
    internal static readonly KeyGesture DiagnosticSnapshotGesture = new(Key.F12, ModifierKeys.Control | ModifierKeys.Shift);
    private OverlayTrace? _liveTrace;
    private Func<IntPtr, NativeOverlaySnapshot>? _readNativeSnapshot;
    private long _visualRevision, _calculatedRevision;
    private Int32Rect _calculatedSelection;
    private DiagnosticRect _calculatedChip = new(ObservationStatus.NotObserved);
    private DiagnosticRect _previousDpi = new(ObservationStatus.NotObserved), _currentDpi = new(ObservationStatus.NotObserved);
    private ChromeRenderObservation _lastChromeRender = new(0, 0, 0, FrameChromeState.Empty,
        new(ObservationStatus.NotObserved), ImmutableArray<SubmittedHandle>.Empty);
    private string? _lastDiagnosticValues;
    private bool _diagnosticReading;

    private void InitializeDiagnostics(Func<string, LogWriteResult>? appendRow, Func<IntPtr, NativeOverlaySnapshot>? nativeReader)
    {
        if (!_settings.OverlayDiagnosticsEnabled) return;
        _readNativeSnapshot = nativeReader ?? ReadNativeSnapshot;
        _liveTrace = new(_traceId, appendRow ?? App.TryLogOverlayTraceRow);
        CommandBindings.Add(new CommandBinding(DiagnosticSnapshotCommand,
            (_, e) => { TraceLiveOnly(OverlayEvent.SnapshotRequested); e.Handled = true; },
            (_, e) => { e.CanExecute = !_liveTrace.Terminal; e.Handled = true; }));
        InputBindings.Add(new KeyBinding(DiagnosticSnapshotCommand, DiagnosticSnapshotGesture));
        Loaded += OnDiagnosticLoaded;
        foreach (FrameworkElement element in new FrameworkElement[] { _scene, _toolbar, _chip, _chrome })
        {
            element.SizeChanged += OnDiagnosticSizeChanged;
            element.IsVisibleChanged += OnDiagnosticVisibilityChanged;
        }
        _scene.LayoutUpdated += OnDiagnosticLayoutUpdated;
    }

    private void DetachDiagnostics()
    {
        if (_liveTrace == null) return;
        Loaded -= OnDiagnosticLoaded;
        foreach (FrameworkElement element in new FrameworkElement[] { _scene, _toolbar, _chip, _chrome })
        {
            element.SizeChanged -= OnDiagnosticSizeChanged;
            element.IsVisibleChanged -= OnDiagnosticVisibilityChanged;
        }
        _scene.LayoutUpdated -= OnDiagnosticLayoutUpdated;
    }

    private void OnDiagnosticLoaded(object sender, RoutedEventArgs e) => TraceLiveOnly(OverlayEvent.Loaded);
    private void OnDiagnosticSizeChanged(object sender, SizeChangedEventArgs e) => TraceLiveOnly(OverlayEvent.ElementSizeChanged, onlyIfChanged: true);
    private void OnDiagnosticVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => TraceLiveOnly(OverlayEvent.VisibilityChanged, onlyIfChanged: true);
    private void OnDiagnosticLayoutUpdated(object? sender, EventArgs e) => TraceLiveOnly(OverlayEvent.LayoutChanged, onlyIfChanged: true);
    private void AdvanceDiagnosticRevision() { if (_liveTrace != null) _visualRevision++; }
    private void ObserveCalculatedChrome()
    {
        if (_liveTrace == null) return;
        _calculatedRevision = _visualRevision;
        _calculatedSelection = Selection;
        _calculatedChip = DiagnosticRect.Known(new(Canvas.GetLeft(_chip), Canvas.GetTop(_chip), _chip.DesiredSize.Width, _chip.DesiredSize.Height));
    }
    private void ObserveDpiChange(DpiScale before, DpiScale after)
    {
        if (_liveTrace == null) return;
        _previousDpi = DiagnosticRect.Known(new(0, 0, before.DpiScaleX, before.DpiScaleY));
        _currentDpi = DiagnosticRect.Known(new(0, 0, after.DpiScaleX, after.DpiScaleY));
        TraceLiveOnly(OverlayEvent.DpiChanged);
    }
    private void ObserveChromeRender(List<SubmittedHandle> submitted)
    {
        _lastChromeRender = new(_lastChromeRender.Number + 1, Stopwatch.GetTimestamp(), _visualRevision, ChromeState,
            SelectionRect(), submitted.ToImmutableArray());
        TraceLiveOnly(OverlayEvent.Rendered);
    }
    private bool RouteDiagnosticKey(Key key, ModifierKeys modifiers)
    {
        if (_liveTrace == null || _liveTrace.Terminal || key != DiagnosticSnapshotGesture.Key || modifiers != DiagnosticSnapshotGesture.Modifiers) return false;
        DiagnosticSnapshotCommand.Execute(null, this);
        return true;
    }

    private void TraceLiveOnly(OverlayEvent reason, DependencyObject? source = null, int data = 0, bool onlyIfChanged = false)
    {
        if (_liveTrace == null || _liveTrace.Terminal || _diagnosticReading) return;
        _diagnosticReading = true;
        try
        {
            OverlaySnapshot snapshot = ReadDiagnosticSnapshot();
            ImmutableArray<string> values = FormatSnapshot(snapshot);
            string comparison = string.Join('\n', values);
            if (onlyIfChanged && comparison == _lastDiagnosticValues) return;
            _lastDiagnosticValues = comparison;
            InputSource origin = source == null ? InputSource.None : source == this ? InputSource.Overlay :
                source == Editor ? InputSource.Editor : IsControlSource(source) ? InputSource.Control : InputSource.Other;
            _liveTrace.Record(reason, origin, data, values);
        }
        finally { _diagnosticReading = false; }
    }

    private DiagnosticRect SelectionRect() => DiagnosticRect.Known(new(Selection.X, Selection.Y, Selection.Width, Selection.Height));
    private OverlaySnapshot ReadDiagnosticSnapshot()
    {
        NativeOverlaySnapshot native = _readNativeSnapshot!(_handle);
        DpiScale windowDpi = VisualTreeHelper.GetDpi(this), sceneDpi = VisualTreeHelper.GetDpi(_scene);
        var device = PresentationSource.FromVisual(this)?.CompositionTarget is { } target
            ? DiagnosticMatrix.Known(target.TransformToDevice) : new DiagnosticMatrix(ObservationStatus.NoPresentationSource);
        var dpi = new DpiObservation(windowDpi.DpiScaleX, windowDpi.DpiScaleY, sceneDpi.DpiScaleX, sceneDpi.DpiScaleY,
            device, ReadTransform(_scene, this), _previousDpi, _currentDpi);
        bool ready = ChromeState == FrameChromeState.Ready;
        var elements = ImmutableArray.Create(
            ReadElement(_scene, ElementRole.Scene, !_closed, DiagnosticRect.Known(new(0, 0, _bounds.Width, _bounds.Height)), _visualRevision, native, dpi),
            ReadElement(_toolbar, ElementRole.Toolbar, ready, _calculatedRevision == 0 ? new(ObservationStatus.NotObserved) : DiagnosticRect.Known(ToolbarBounds), _calculatedRevision, native, dpi),
            ReadElement(_chip, ElementRole.Chip, ready, _calculatedChip, _calculatedRevision, native, dpi),
            ReadElement(_chrome, ElementRole.FrameChrome, !_closed, DiagnosticRect.Known(new(0, 0, _bounds.Width, _bounds.Height)), _visualRevision, native, dpi));
        return new(new(ChromeState, _gesture, _frameHandle, _lastGestureEnd, Editor.Tool, _visualRevision, _lastPoint.X, _lastPoint.Y,
            _editing, _ocrMode, _closed, _outputPending, IsActive, _pointerPromoted, _hasCapture(), Editor.HasActiveGesture, Editor.IsEditingText,
            WindowState, Topmost), SelectionRect(), DiagnosticRect.Known(new(_bounds.X, _bounds.Y, _bounds.Width, _bounds.Height)),
            elements, _lastChromeRender, native, dpi);
    }

    private ElementObservation ReadElement(FrameworkElement element, ElementRole role, bool wanted, DiagnosticRect calculated,
        long revision, NativeOverlaySnapshot native, DpiObservation dpi)
    {
        DiagnosticMatrix toScene = ReadTransform(element, _scene), toClient = ReadTransform(element, this);
        var local = DiagnosticRect.Known(new(0, 0, element.RenderSize.Width, element.RenderSize.Height));
        DiagnosticRect observed = TransformRect(local, toScene);
        var clips = ImmutableArray.CreateBuilder<ClipObservation>();
        DiagnosticRect clipped = observed;
        double opacity = 1;
        int depth = 0;
        for (DependencyObject? node = element; node != null; node = ParentOf(node))
        {
            if (node is not UIElement ancestor) continue;
            opacity *= ancestor.Opacity;
            var transform = ReadTransform(ancestor, _scene);
            DiagnosticRect bounds = TransformRect(DiagnosticRect.Known(new(0, 0, ancestor.RenderSize.Width, ancestor.RenderSize.Height)), transform);
            DiagnosticRect clip = ancestor.Clip == null ? new(ObservationStatus.NotObserved) : TransformRect(DiagnosticRect.Known(ancestor.Clip.Bounds), transform);
            var accuracy = ancestor.Clip is null or RectangleGeometry { RadiusX: 0, RadiusY: 0 } ? ClipAccuracy.Rectangular : ClipAccuracy.BoundsOnly;
            clips.Add(new(depth++, ancestor.ClipToBounds, ancestor.IsVisible, ancestor.Opacity, bounds, clip, accuracy));
            if (ancestor.ClipToBounds) clipped = Intersect(clipped, bounds);
            if (ancestor.Clip != null) clipped = Intersect(clipped, clip);
            if (node == this) break;
        }
        if (role == ElementRole.Toolbar && _toolbar.Child is UIElement child && child.Clip != null)
            clips.Add(new(-1, child.ClipToBounds, child.IsVisible, child.Opacity,
                TransformRect(DiagnosticRect.Known(new(0, 0, child.RenderSize.Width, child.RenderSize.Height)), ReadTransform(child, _scene)),
                TransformRect(DiagnosticRect.Known(child.Clip.Bounds), ReadTransform(child, _scene)), ClipAccuracy.BoundsOnly));
        return new(role, wanted, element.Visibility, element.IsVisible, element.IsLoaded, element.IsHitTestVisible, opacity,
            element.IsMeasureValid, element.IsArrangeValid, element.ActualWidth, element.ActualHeight, element.DesiredSize.Width, element.DesiredSize.Height,
            element.RenderSize.Width, element.RenderSize.Height, ReadNumber(Canvas.GetLeft(element)), ReadNumber(Canvas.GetTop(element)), calculated,
            revision, revision > 0 && revision == _visualRevision && (role is ElementRole.Scene or ElementRole.FrameChrome || _calculatedSelection == Selection) && wanted,
            observed, OffsetRect(calculated, _bounds.X, _bounds.Y), MapToScreen(local, toClient, dpi.ToDevice, native.ClientScreen),
            toClient, clipped, clips.ToImmutable());
    }

    private static DiagnosticNumber ReadNumber(double value) => double.IsFinite(value) ? new(ObservationStatus.Known, value) : new(ObservationStatus.NotObserved);
    private static DiagnosticMatrix ReadTransform(Visual element, Visual ancestor)
    {
        if (element == ancestor) return DiagnosticMatrix.Known(Matrix.Identity);
        try
        {
            GeneralTransform transform = element.TransformToAncestor(ancestor);
            if (!transform.TryTransform(new(0, 0), out Point origin) || !transform.TryTransform(new(1, 0), out Point x) || !transform.TryTransform(new(0, 1), out Point y))
                return new(ObservationStatus.TransformFailure);
            return DiagnosticMatrix.Known(new(x.X - origin.X, x.Y - origin.Y, y.X - origin.X, y.Y - origin.Y, origin.X, origin.Y));
        }
        catch (InvalidOperationException) { return new(ObservationStatus.TransformFailure); }
    }
    private static DiagnosticRect TransformRect(DiagnosticRect rectangle, DiagnosticMatrix transform)
    {
        if (rectangle.Status != ObservationStatus.Known) return rectangle;
        if (transform.Status != ObservationStatus.Known) return new(transform.Status);
        Matrix m = transform.Matrix;
        Point[] corners = { new(rectangle.X, rectangle.Y), new(rectangle.X + rectangle.Width, rectangle.Y),
            new(rectangle.X, rectangle.Y + rectangle.Height), new(rectangle.X + rectangle.Width, rectangle.Y + rectangle.Height) };
        Point[] points = corners.Select(m.Transform).ToArray();
        double left = points.Min(p => p.X), top = points.Min(p => p.Y);
        return DiagnosticRect.Known(new(left, top, points.Max(p => p.X) - left, points.Max(p => p.Y) - top));
    }
    internal static DiagnosticRect MapToScreen(DiagnosticRect local, DiagnosticMatrix toClient, DiagnosticMatrix toDevice, DiagnosticRect clientScreen)
    {
        if (clientScreen.Status != ObservationStatus.Known) return new(clientScreen.Status, Code: clientScreen.Code);
        if (toDevice.Status != ObservationStatus.Known) return new(toDevice.Status);
        if (toClient.Status != ObservationStatus.Known) return new(toClient.Status);
        Matrix combined = toClient.Matrix;
        combined.Append(toDevice.Matrix);
        return OffsetRect(TransformRect(local, DiagnosticMatrix.Known(combined)), clientScreen.X, clientScreen.Y);
    }
    private static DiagnosticRect OffsetRect(DiagnosticRect value, double x, double y) => value.Status == ObservationStatus.Known
        ? value with { X = value.X + x, Y = value.Y + y } : value;
    private static DiagnosticRect Intersect(DiagnosticRect a, DiagnosticRect b) => a.Status != ObservationStatus.Known ? a :
        b.Status != ObservationStatus.Known ? b : DiagnosticRect.Known(Rect.Intersect(a.Rectangle, b.Rectangle));

    private sealed class OverlayTrace(int captureId, Func<string, LogWriteResult> appendRow)
    {
        private static readonly Guid Run = Guid.NewGuid();
        private static readonly Guid Build = typeof(RegionOverlay).Module.ModuleVersionId;
        private static readonly DateTime Start = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        private long _sequence, _writeFailures;
        private int _lastWriteCode;
        internal bool Terminal { get; private set; }

        internal void Record(OverlayEvent reason, InputSource source, int data, ImmutableArray<string> values)
        {
            long sequence = ++_sequence, ticks = Stopwatch.GetTimestamp();
            DateTimeOffset utc = DateTimeOffset.UtcNow;
            for (int part = 0; part <= values.Length; part++)
            {
                using var buffer = new MemoryStream();
                using (var json = new Utf8JsonWriter(buffer))
                {
                    json.WriteStartObject();
                    json.WriteNumber("schema", 1); json.WriteString("run", Run); json.WriteString("build", Build);
                    json.WriteNumber("process", Environment.ProcessId); json.WriteString("processStartUtc", Start);
                    json.WriteNumber("capture", captureId); json.WriteNumber("seq", sequence);
                    json.WriteNumber("ticks", ticks); json.WriteNumber("frequency", Stopwatch.Frequency); json.WriteString("utc", utc);
                    json.WriteString("event", reason.ToString()); json.WriteString("source", source.ToString()); json.WriteNumber("data", data);
                    json.WriteNumber("part", part); json.WriteNumber("parts", values.Length + 1); json.WriteNumber("writeFailures", _writeFailures);
                    json.WriteNumber("lastWriteCode", _lastWriteCode);
                    json.WritePropertyName("value");
                    if (part < values.Length) json.WriteRawValue(values[part]);
                    else { json.WriteStartObject(); json.WriteString("type", "Complete"); json.WriteEndObject(); }
                    json.WriteEndObject();
                }
                LogWriteResult result = appendRow(Encoding.UTF8.GetString(buffer.ToArray()));
                if (result.Status == LogWriteStatus.Failed) { _writeFailures++; _lastWriteCode = result.Code; }
            }
            if (reason == OverlayEvent.WindowClosed) Terminal = true;
        }
    }

    private static string Row(string type, Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject(); json.WriteString("type", type); write(json); json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    private static void WriteRect(Utf8JsonWriter json, string key, DiagnosticRect value)
    {
        json.WriteString(key + "Status", value.Status.ToString()); json.WriteNumber(key + "Code", value.Code);
        if (value.Status != ObservationStatus.Known) return;
        json.WriteNumber(key + "X", value.X); json.WriteNumber(key + "Y", value.Y);
        json.WriteNumber(key + "Width", value.Width); json.WriteNumber(key + "Height", value.Height);
    }
    private static void WriteNumber(Utf8JsonWriter json, string key, DiagnosticNumber value)
    {
        json.WriteString(key + "Status", value.Status.ToString()); json.WriteNumber(key + "Code", value.Code);
        if (value.Status == ObservationStatus.Known) json.WriteNumber(key, value.Value);
    }
    private static void WriteMatrix(Utf8JsonWriter json, string key, DiagnosticMatrix value)
    {
        json.WriteString(key + "Status", value.Status.ToString());
        if (value.Status != ObservationStatus.Known) return;
        json.WriteNumber(key + "M11", value.M11); json.WriteNumber(key + "M12", value.M12);
        json.WriteNumber(key + "M21", value.M21); json.WriteNumber(key + "M22", value.M22);
        json.WriteNumber(key + "OffsetX", value.OffsetX); json.WriteNumber(key + "OffsetY", value.OffsetY);
    }

    private ImmutableArray<string> FormatSnapshot(OverlaySnapshot snapshot)
    {
        var rows = ImmutableArray.CreateBuilder<string>();
        SessionObservation s = snapshot.State;
        rows.Add(Row("State", j =>
        {
            j.WriteString("chrome", s.Chrome.ToString()); j.WriteString("gesture", s.Gesture.ToString()); j.WriteString("handle", s.Handle.ToString());
            j.WriteString("end", s.End.ToString()); j.WriteString("tool", s.Tool.ToString()); j.WriteNumber("revision", s.Revision);
            j.WriteNumber("pointX", s.PointX); j.WriteNumber("pointY", s.PointY); j.WriteBoolean("editing", s.Editing); j.WriteBoolean("ocr", s.Ocr);
            j.WriteBoolean("closed", s.Closed); j.WriteBoolean("outputPending", s.OutputPending); j.WriteBoolean("active", s.Active);
            j.WriteBoolean("promoted", s.Promoted); j.WriteBoolean("capture", s.Capture); j.WriteBoolean("editorGesture", s.EditorGesture);
            j.WriteBoolean("editingText", s.EditingText); j.WriteString("windowState", s.WindowState.ToString()); j.WriteBoolean("topmost", s.Topmost);
            WriteRect(j, "selection", snapshot.Selection); WriteRect(j, "desktop", snapshot.Desktop);
            j.WriteNumber("expectedHandles", s.Chrome == FrameChromeState.Ready ? 8 : 0);
            j.WriteNumber("renderNumber", snapshot.Render.Number); j.WriteNumber("renderTicks", snapshot.Render.Ticks);
            j.WriteNumber("renderRevision", snapshot.Render.Revision); j.WriteString("renderChrome", snapshot.Render.Chrome.ToString());
            j.WriteNumber("submittedHandles", snapshot.Render.Handles.Length); WriteRect(j, "renderSelection", snapshot.Render.Selection);
        }));
        rows.Add(Row("Dpi", j =>
        {
            j.WriteNumber("windowX", snapshot.Dpi.WindowX); j.WriteNumber("windowY", snapshot.Dpi.WindowY);
            j.WriteNumber("sceneX", snapshot.Dpi.SceneX); j.WriteNumber("sceneY", snapshot.Dpi.SceneY);
            WriteMatrix(j, "toDevice", snapshot.Dpi.ToDevice); WriteMatrix(j, "sceneToClient", snapshot.Dpi.SceneToClient);
            WriteRect(j, "previous", snapshot.Dpi.Previous); WriteRect(j, "current", snapshot.Dpi.Current);
        }));
        NativeOverlaySnapshot n = snapshot.Native;
        rows.Add(Row("NativeWindow", j =>
        {
            WriteRect(j, "window", n.Window); WriteRect(j, "client", n.Client); WriteRect(j, "clientScreen", n.ClientScreen);
            WriteNumber(j, "visible", n.Visible); WriteNumber(j, "iconic", n.Iconic); WriteNumber(j, "dpi", n.WindowDpi);
            WriteNumber(j, "processAwareness", n.ProcessAwareness); WriteNumber(j, "threadAwareness", n.ThreadAwareness); WriteNumber(j, "perMonitorV2", n.PerMonitorV2);
            j.WriteString("monitorStatus", n.MonitorStatus.ToString()); j.WriteNumber("monitorCode", n.MonitorCode); j.WriteNumber("monitors", n.Monitors.Length);
        }));
        foreach (ElementObservation element in snapshot.Elements)
        {
            rows.Add(Row("Element", j =>
            {
                j.WriteString("role", element.Role.ToString()); j.WriteBoolean("wanted", element.Wanted); j.WriteString("visibility", element.Visibility.ToString());
                j.WriteBoolean("isVisible", element.IsVisible); j.WriteBoolean("loaded", element.Loaded); j.WriteBoolean("hitTestVisible", element.HitTestVisible);
                j.WriteNumber("effectiveOpacity", element.EffectiveOpacity); j.WriteBoolean("measureValid", element.MeasureValid); j.WriteBoolean("arrangeValid", element.ArrangeValid);
                j.WriteNumber("actualWidth", element.ActualWidth); j.WriteNumber("actualHeight", element.ActualHeight);
                j.WriteNumber("desiredWidth", element.DesiredWidth); j.WriteNumber("desiredHeight", element.DesiredHeight);
                j.WriteNumber("renderWidth", element.RenderWidth); j.WriteNumber("renderHeight", element.RenderHeight);
                WriteNumber(j, "canvasX", element.CanvasX); WriteNumber(j, "canvasY", element.CanvasY);
                WriteRect(j, "calculated", element.Calculated); j.WriteNumber("calculatedRevision", element.CalculatedRevision); j.WriteBoolean("calculatedCurrent", element.CalculatedCurrent);
                WriteRect(j, "observedScene", element.ObservedScene); WriteRect(j, "expectedScreen", element.ExpectedScreen); WriteRect(j, "measuredScreen", element.MeasuredScreen);
                WriteMatrix(j, "toClient", element.ToClient); WriteRect(j, "clippedScene", element.ClippedScene);
                WriteRect(j, "clientIntersection", Intersect(element.MeasuredScreen, n.ClientScreen));
            }));
            foreach (ClipObservation clip in element.Clips)
                rows.Add(Row("Clip", j =>
                {
                    j.WriteString("role", element.Role.ToString()); j.WriteNumber("depth", clip.Depth); j.WriteBoolean("clipToBounds", clip.ClipToBounds);
                    j.WriteBoolean("isVisible", clip.Visible); j.WriteNumber("opacity", clip.Opacity); j.WriteString("accuracy", clip.Accuracy.ToString());
                    WriteRect(j, "bounds", clip.Bounds); WriteRect(j, "explicit", clip.ExplicitClip);
                }));
            foreach (MonitorObservation monitor in n.Monitors)
                rows.Add(IntersectionRow(element.Role.ToString(), FrameHandle.None, monitor, element.ExpectedScreen, element.MeasuredScreen));
        }
        ElementObservation chrome = snapshot.Elements[3];
        foreach (var handle in FrameHandles())
        {
            DiagnosticRect expected = DiagnosticRect.Known(new(handle.Center.X - 4, handle.Center.Y - 4, 8, 8));
            SubmittedHandle? submitted = snapshot.Render.Handles.Where(h => h.Handle == handle.Handle).Select(h => (SubmittedHandle?)h).FirstOrDefault();
            DiagnosticRect drawn = submitted?.Rectangle ?? new(ObservationStatus.NotObserved);
            DiagnosticRect measured = MapToScreen(drawn, snapshot.Dpi.SceneToClient, snapshot.Dpi.ToDevice, n.ClientScreen);
            DiagnosticRect expectedScreen = OffsetRect(expected, _bounds.X, _bounds.Y);
            rows.Add(Row("Handle", j =>
            {
                j.WriteString("handle", handle.Handle.ToString()); j.WriteBoolean("wanted", s.Chrome == FrameChromeState.Ready);
                j.WriteBoolean("parentVisible", chrome.IsVisible); j.WriteNumber("parentOpacity", chrome.EffectiveOpacity);
                j.WriteNumber("renderNumber", snapshot.Render.Number); j.WriteNumber("renderRevision", snapshot.Render.Revision);
                WriteRect(j, "expectedScene", expected); WriteRect(j, "submittedScene", drawn); WriteRect(j, "expectedScreen", expectedScreen);
                WriteRect(j, "measuredScreen", measured); WriteRect(j, "sceneClipIntersection", Intersect(drawn, chrome.ClippedScene));
                WriteRect(j, "clientIntersection", Intersect(measured, n.ClientScreen));
            }));
            foreach (MonitorObservation monitor in n.Monitors)
                rows.Add(IntersectionRow(ElementRole.FrameChrome.ToString(), handle.Handle, monitor, expectedScreen, measured));
        }
        foreach (MonitorObservation monitor in n.Monitors)
            rows.Add(Row("Monitor", j =>
            {
                j.WriteNumber("index", monitor.Index); j.WriteBoolean("primary", monitor.Primary);
                WriteRect(j, "bounds", monitor.Bounds); WriteRect(j, "workarea", monitor.Workarea);
                WriteNumber(j, "dpiX", monitor.DpiX); WriteNumber(j, "dpiY", monitor.DpiY);
            }));
        return rows.ToImmutable();
    }
    private static string IntersectionRow(string role, FrameHandle handle, MonitorObservation monitor, DiagnosticRect expected, DiagnosticRect measured) => Row("Intersection", j =>
    {
        j.WriteString("role", role); j.WriteString("handle", handle.ToString()); j.WriteNumber("monitor", monitor.Index);
        WriteRect(j, "expectedMonitor", Intersect(expected, monitor.Bounds)); WriteRect(j, "measuredMonitor", Intersect(measured, monitor.Bounds));
        WriteRect(j, "expectedWorkarea", Intersect(expected, monitor.Workarea)); WriteRect(j, "measuredWorkarea", Intersect(measured, monitor.Workarea));
    });

    private static NativeOverlaySnapshot ReadNativeSnapshot(IntPtr hwnd)
    {
        DiagnosticRect window = new(ObservationStatus.NoPresentationSource), client = window, screen = window;
        DiagnosticNumber visible = new(ObservationStatus.NoPresentationSource), iconic = visible, dpi = visible;
        var monitors = ImmutableArray.CreateBuilder<MonitorObservation>();
        if (hwnd != IntPtr.Zero)
        {
            window = GetWindowRect(hwnd, out NativeRect wr) ? NativeBounds(wr) : new(ObservationStatus.NativeFailure, Code: Marshal.GetLastWin32Error());
            client = GetClientRect(hwnd, out NativeRect cr) ? NativeBounds(cr) : new(ObservationStatus.NativeFailure, Code: Marshal.GetLastWin32Error());
            if (client.Status == ObservationStatus.Known)
            {
                var top = new NativePoint { X = cr.Left, Y = cr.Top };
                var bottom = new NativePoint { X = cr.Right, Y = cr.Bottom };
                screen = ClientToScreen(hwnd, ref top) && ClientToScreen(hwnd, ref bottom)
                    ? DiagnosticRect.Known(new(top.X, top.Y, bottom.X - top.X, bottom.Y - top.Y))
                    : new(ObservationStatus.NativeFailure, Code: Marshal.GetLastWin32Error());
            }
            else screen = client;
            visible = new(ObservationStatus.Known, IsWindowVisible(hwnd) ? 1 : 0);
            iconic = new(ObservationStatus.Known, IsIconic(hwnd) ? 1 : 0);
            uint nativeDpi = GetDpiForWindow(hwnd);
            dpi = nativeDpi == 0 ? new(ObservationStatus.NativeFailure, Code: Marshal.GetLastWin32Error()) : new(ObservationStatus.Known, nativeDpi);
        }
        int monitorFailure = 0;
        bool enumerated = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref NativeRect rectangle, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) { monitorFailure = Marshal.GetLastWin32Error(); return true; }
            int result = GetDpiForMonitor(monitor, 0, out uint dx, out uint dy);
            monitors.Add(new(monitors.Count, (info.Flags & 1) != 0, NativeBounds(info.Monitor), NativeBounds(info.Work),
                result == 0 ? new(ObservationStatus.Known, dx) : new(ObservationStatus.NativeFailure, Code: result),
                result == 0 ? new(ObservationStatus.Known, dy) : new(ObservationStatus.NativeFailure, Code: result)));
            return true;
        }, IntPtr.Zero);
        if (!enumerated) monitorFailure = Marshal.GetLastWin32Error();
        int processResult = GetProcessDpiAwareness(IntPtr.Zero, out int process);
        IntPtr context = GetThreadDpiAwarenessContext();
        int thread = GetAwarenessFromDpiAwarenessContext(context);
        return new(window, client, screen, visible, iconic, dpi,
            enumerated && monitorFailure == 0 ? ObservationStatus.Known : ObservationStatus.NativeFailure, monitorFailure, monitors.ToImmutable(),
            processResult == 0 ? new(ObservationStatus.Known, process) : new(ObservationStatus.NativeFailure, Code: processResult),
            thread >= 0 ? new(ObservationStatus.Known, thread) : new(ObservationStatus.NativeFailure),
            context != IntPtr.Zero ? new(ObservationStatus.Known, AreDpiAwarenessContextsEqual(context, new IntPtr(-4)) ? 1 : 0) : new(ObservationStatus.NativeFailure));
    }
    private static DiagnosticRect NativeBounds(NativeRect r) => DiagnosticRect.Known(new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect rectangle, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("shcore.dll")] private static extern int GetProcessDpiAwareness(IntPtr process, out int awareness);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
}
