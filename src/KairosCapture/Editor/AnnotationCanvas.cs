using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KairosCapture.Editor;

public enum EditorTool { Select, Arrow, Rectangle, Ellipse, Pen, Text, Highlight, Redact }

/// <summary>A pixel-coordinate editor. Put this canvas in a Viewbox to scale its display.</summary>
public sealed class AnnotationCanvas : Canvas
{
    private sealed class Annotation
    {
        internal EditorTool Tool;
        internal Brush Brush = Brushes.Black;
        internal double Thickness;
        internal Geometry Geometry = Geometry.Empty;
        internal Rect Bounds;
        internal Point Offset;
        internal string Text = "";
        internal double TextWidth;
        internal double ScaleX = 1, ScaleY = 1;
        internal Point ArrowStart, ArrowEnd;
    }

    private sealed record Edit(Action Undo, Action Redo);
    private sealed record State(Geometry Geometry, Rect Bounds, Point Offset, string Text,
        double ScaleX, double ScaleY, Brush Brush, double Thickness, Point ArrowStart, Point ArrowEnd);
    [Flags]
    private enum Handle
    {
        None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8,
        TopLeft = Left | Top, TopRight = Right | Top,
        BottomLeft = Left | Bottom, BottomRight = Right | Bottom,
        ArrowStart = 16, ArrowEnd = 32
    }

    private readonly BitmapSource _image;
    private readonly List<Annotation> _items = new();
    private readonly Stack<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();
    private Annotation? _selected, _draft, _moving, _resizing, _textItem;
    private State? _beforeResize;
    private Rect _resizeBounds;
    private Rect _editingBounds;
    private Handle _resizeHandle;
    private TextBox? _textBox;
    private PolyLineSegment? _penPoints;
    private Point _start, _originalOffset;
    private bool _gestureChanged, _newText;
    private string _originalText = "";
    private EditorTool _tool;
    private Brush _stroke = new SolidColorBrush(Color.FromRgb(0xB0, 0x97, 0xE9));
    private double _thickness = 3;

    public AnnotationCanvas(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.PixelWidth < 1 || image.PixelHeight < 1)
            throw new ArgumentException("The image must contain pixels.", nameof(image));
        // Freeze a copy only when necessary: the caller's mutable image is never changed.
        _image = image.IsFrozen ? image : (BitmapSource)image.GetCurrentValueAsFrozen();
        Width = image.PixelWidth;
        Height = image.PixelHeight;
        _editingBounds = new Rect(0, 0, Width, Height);
        Background = Brushes.Transparent;
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Arrow;
        Unloaded += (_, _) => { EndGesture(); CommitText(); };
    }

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (!Enum.IsDefined(typeof(EditorTool), value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_tool == value) return;
            EndGesture();
            CommitText();
            _tool = value;
            Cursor = value == EditorTool.Select ? Cursors.Arrow :
                value == EditorTool.Text ? Cursors.IBeam : Cursors.Cross;
            InvalidateVisual();
        }
    }

    public Brush Stroke
    {
        get => _stroke;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _stroke = value.CloneCurrentValue();
            if (_stroke.CanFreeze) _stroke.Freeze();
            ChangeSelectedStyle();
        }
    }

    public double Thickness
    {
        get => _thickness;
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _thickness = Math.Clamp(value, 1, 24);
            ChangeSelectedStyle(false);
        }
    }

    private void ChangeSelectedStyle(bool colour = true)
    {
        if (_selected == null || _selected.Tool == EditorTool.Redact) return;
        EndGesture();
        CommitText();
        if (_selected is not Annotation item || item.Tool == EditorTool.Redact) return;
        State before = Snapshot(item);
        if (colour) item.Brush = _stroke;
        else item.Thickness = _thickness;
        State after = Snapshot(item);
        if (before != after) Record(() => Restore(item, before), () => Restore(item, after));
    }

    internal bool IsEditingText => _textBox != null;
    internal bool HasActiveGesture => _draft != null || _moving != null || _resizing != null;
    internal object? SelectedIdentity => _selected;
    internal bool HasAnnotationAt(Point point) => HitHandle(point) != Handle.None || Hit(point) != null;

    internal void SetEditingBounds(Int32Rect region)
    {
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            (long)region.X + region.Width > _image.PixelWidth || (long)region.Y + region.Height > _image.PixelHeight)
            throw new ArgumentOutOfRangeException(nameof(region));
        // Input constraint only: old geometry, full-raster exports and both history stacks stay intact.
        _editingBounds = new Rect(region.X, region.Y, region.Width, region.Height);
    }

    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public bool HasChanges => CanUndo || _gestureChanged ||
        (_textBox != null && _textBox.Text != _originalText);
    public event EventHandler? Changed;

    public void Undo()
    {
        EndGesture();
        CommitText();
        if (!_undo.TryPop(out Edit? edit)) return;
        edit.Undo();
        _redo.Push(edit);
        _selected = null;
        Notify();
    }

    public void Redo()
    {
        EndGesture();
        CommitText();
        if (!_redo.TryPop(out Edit? edit)) return;
        edit.Redo();
        _undo.Push(edit);
        _selected = null;
        Notify();
    }

    public void DeleteSelected()
    {
        EndGesture();
        CommitText();
        if (_selected is not Annotation item) return;
        int index = _items.IndexOf(item);
        if (index < 0) return;
        _items.RemoveAt(index);
        _selected = null;
        Record(() => _items.Insert(index, item), () => _items.Remove(item));
    }

    public void CommitText()
    {
        if (_textBox == null || _textItem == null) return;
        Annotation item = _textItem;
        string text = _textBox.Text;
        string before = _originalText;
        bool isNew = _newText;
        CloseTextBox();
        if (text == before) { Notify(); return; }
        if (string.IsNullOrWhiteSpace(text))
        {
            if (!isNew) { _selected = item; DeleteSelected(); }
            else Notify();
            return;
        }
        State oldState = Snapshot(item);
        SetText(item, text, isNew);
        item.Offset = ClampOffset(item, item.Offset);
        State newState = Snapshot(item);
        _selected = item;
        if (isNew) Add(item);
        else Record(() => Restore(item, oldState), () => Restore(item, newState));
    }

    /// <summary>Exports at 96 DPI, where one canvas unit is exactly one output pixel.</summary>
    public BitmapSource Flatten() => Flatten(new Int32Rect(0, 0, _image.PixelWidth, _image.PixelHeight));

    public BitmapSource Flatten(Int32Rect crop)
    {
        if (crop.X < 0 || crop.Y < 0 || crop.Width < 1 || crop.Height < 1 ||
            (long)crop.X + crop.Width > _image.PixelWidth || (long)crop.Y + crop.Height > _image.PixelHeight)
            throw new ArgumentOutOfRangeException(nameof(crop));
        EndGesture();
        CommitText();
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.PushClip(new RectangleGeometry(new Rect(0, 0, crop.Width, crop.Height)));
            drawing.PushTransform(new TranslateTransform(-crop.X, -crop.Y));
            DrawImageAndAnnotations(drawing);
            drawing.Pop();
            drawing.Pop();
        }
        RenderTargetBitmap bitmap = new(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        DrawImageAndAnnotations(drawing);
        if (_selected != null && _textBox == null)
        {
            Rect bounds = WorldBounds(_selected);
            double unit = DisplayUnit();
            Brush accent = TryFindResource("LavenderBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0xB0, 0x97, 0xE9));
            drawing.DrawRectangle(null, new Pen(Brushes.Black, 2 * unit) { DashStyle = DashStyles.Dash }, bounds);
            drawing.DrawRectangle(null, new Pen(accent, unit) { DashStyle = DashStyles.Dash }, bounds);
            foreach ((Handle _, Point center) in Handles(_selected))
                drawing.DrawRectangle(Brushes.White, new Pen(accent, unit),
                    new Rect(center.X - 4 * unit, center.Y - 4 * unit, 8 * unit, 8 * unit));
        }
    }

    private void DrawImageAndAnnotations(DrawingContext drawing)
    {
        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, Width, Height)));
        drawing.DrawImage(_image, new Rect(0, 0, Width, Height));
        foreach (Annotation item in _items)
            if (item != _textItem && item.Tool != EditorTool.Redact) DrawAnnotation(drawing, item);
        if (_draft != null && _draft.Tool != EditorTool.Redact) DrawAnnotation(drawing, _draft);
        // Redactions always cover later annotations too, for both display and export.
        foreach (Annotation item in _items)
            if (item.Tool == EditorTool.Redact) DrawAnnotation(drawing, item);
        if (_draft?.Tool == EditorTool.Redact) DrawAnnotation(drawing, _draft);
        drawing.Pop();
    }

    private static void DrawAnnotation(DrawingContext drawing, Annotation item)
    {
        if (item.Tool == EditorTool.Redact)
        {
            Rect bounds = WorldBounds(item);
            // Cover whole native pixels, including fractional drag/move edges. Never inherit brush alpha.
            drawing.DrawRectangle(Brushes.Black, null, new Rect(
                new Point(Math.Floor(bounds.Left), Math.Floor(bounds.Top)),
                new Point(Math.Ceiling(bounds.Right), Math.Ceiling(bounds.Bottom))));
            return;
        }
        drawing.PushTransform(new MatrixTransform(item.ScaleX, 0, 0, item.ScaleY, item.Offset.X, item.Offset.Y));
        if (item.Tool == EditorTool.Highlight)
        {
            drawing.PushOpacity(0.3);
            drawing.DrawGeometry(item.Brush, null, item.Geometry);
            drawing.Pop();
        }
        else if (item.Tool == EditorTool.Text)
            drawing.DrawGeometry(item.Brush, null, item.Geometry);
        else
            drawing.DrawGeometry(item.Tool == EditorTool.Arrow ? item.Brush : null,
                new Pen(item.Brush, item.Thickness)
                {
                    StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                }, item.Geometry);
        drawing.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_textBox?.IsMouseOver == true) return;
        Focus();
        BeginGesture(e.GetPosition(this), e.ClickCount);
        if ((_draft != null || _moving != null || _resizing != null) && !CaptureMouse()) EndGesture(true);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draft == null && _moving == null && _resizing == null)
        {
            Cursor = CursorAt(e.GetPosition(this));
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) EndGesture();
        else UpdateGesture(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_draft == null && _moving == null && _resizing == null) return;
        UpdateGesture(e.GetPosition(this));
        EndGesture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (e.OriginalSource == this) EndGesture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled) e.Handled = HandleKey(e.Key, Keyboard.Modifiers);
    }

    internal bool HandleKey(Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (!IsEnabled) return false;
        if (key == Key.Escape)
        {
            if (_textBox == null && !HasActiveGesture && _selected == null) return false;
            CloseTextBox();
            EndGesture(true);
            _selected = null;
            Notify();
            return true;
        }
        // A focused TextBox owns normal text editing, including Delete and its local undo.
        if (_textBox != null) return false;
        if (key == Key.Delete && modifiers == ModifierKeys.None) DeleteSelected();
        else if (key == Key.Z && modifiers == ModifierKeys.Control) Undo();
        else if (key == Key.Y && modifiers == ModifierKeys.Control ||
            key == Key.Z && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) Redo();
        else return false;
        Notify();
        return true;
    }

    // These gesture methods are also used by the offline STA self-check; no synthetic input is needed.
    internal void BeginGesture(Point point, int clickCount = 1)
    {
        if (!IsEnabled || !_editingBounds.Contains(point)) return;
        EndGesture();
        CommitText();
        _start = ClampPoint(point);
        Annotation? hit = Hit(_start);
        if (Tool == EditorTool.Select && clickCount >= 2 && hit?.Tool == EditorTool.Text)
        {
            BeginText(hit, _start);
            return;
        }
        _resizeHandle = HitHandle(_start);
        if (_resizeHandle != Handle.None && _selected != null)
        {
            _resizing = _selected;
            _beforeResize = Snapshot(_resizing);
            _resizeBounds = WorldBounds(_resizing);
        }
        else if (Tool == EditorTool.Text)
        {
            BeginText(hit?.Tool == EditorTool.Text ? hit : null, _start);
            return;
        }
        else if (Tool == EditorTool.Select || (_selected != null && hit == _selected))
        {
            _selected = _moving = hit;
            if (hit != null) _originalOffset = hit.Offset;
        }
        else
        {
            _selected = null;
            Brush brush = Stroke.CloneCurrentValue();
            if (brush.CanFreeze) brush.Freeze();
            _draft = new Annotation { Tool = Tool, Brush = brush, Thickness = Thickness };
            if (Tool == EditorTool.Pen)
            {
                _penPoints = new PolyLineSegment();
                PathFigure figure = new() { StartPoint = _start, IsFilled = false };
                figure.Segments.Add(_penPoints);
                PathGeometry path = new();
                path.Figures.Add(figure);
                _draft.Geometry = path;
            }
            UpdateGesture(_start);
        }
        Notify();
    }

    internal void UpdateGesture(Point point)
    {
        if (!IsEnabled) return;
        point = ClampPoint(point);
        if (_resizing != null)
        {
            if ((point - _start).Length < 2 && !_gestureChanged) return;
            Resize(point);
        }
        else if (_moving != null)
        {
            if ((point - _start).Length < 2 && !_gestureChanged) return;
            _moving.Offset = ClampOffset(_moving, _originalOffset + (point - _start));
            _gestureChanged = _moving.Offset != _originalOffset;
        }
        else if (_draft != null)
        {
            Rect rect = new(_start, point);
            switch (_draft.Tool)
            {
                case EditorTool.Pen:
                    if (_penPoints == null) return;
                    Point last = _penPoints.Points.Count == 0 ? _start : _penPoints.Points[^1];
                    if ((point - last).Length >= 0.75) _penPoints.Points.Add(point);
                    break;
                case EditorTool.Arrow:
                    _draft.ArrowStart = _start;
                    _draft.ArrowEnd = point;
                    _draft.Geometry = Arrow(_start, point, _draft.Thickness);
                    break;
                case EditorTool.Ellipse:
                    _draft.Geometry = new EllipseGeometry(rect);
                    break;
                default:
                    _draft.Geometry = new RectangleGeometry(rect);
                    break;
            }
            _draft.Bounds = _draft.Geometry.Bounds;
            _gestureChanged = IsValid(_draft);
        }
        else return;
        Notify();
    }

    internal void EndGesture(bool cancel = false)
    {
        Annotation? draft = _draft, moving = _moving, resizing = _resizing;
        State? beforeResize = _beforeResize;
        _draft = _moving = _resizing = null;
        _beforeResize = null;
        _penPoints = null;
        _gestureChanged = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (draft != null && !cancel && IsValid(draft))
        {
            if (draft.Geometry.CanFreeze) draft.Geometry.Freeze();
            _selected = draft;
            Add(draft);
        }
        else if (resizing != null && beforeResize != null)
        {
            State after = Snapshot(resizing);
            if (cancel) Restore(resizing, beforeResize);
            else if (beforeResize != after)
                Record(() => Restore(resizing, beforeResize), () => Restore(resizing, after));
        }
        else if (moving != null)
        {
            Point before = _originalOffset, after = moving.Offset;
            if (cancel) moving.Offset = before;
            else if (before != after) Record(() => moving.Offset = before, () => moving.Offset = after);
        }
        if (draft != null || moving != null || resizing != null) Notify();
    }

    private static bool IsValid(Annotation item) => !item.Bounds.IsEmpty &&
        (item.Tool == EditorTool.Pen || item.Tool == EditorTool.Arrow
            ? Math.Max(item.Bounds.Width, item.Bounds.Height) >= 2
            : item.Bounds.Width >= 2 && item.Bounds.Height >= 2);

    private Geometry Arrow(Point start, Point end, double thickness)
    {
        Vector direction = end - start;
        double distance = direction.Length;
        if (distance < 0.01) return Geometry.Empty;
        direction.Normalize();
        double length = Math.Min(distance * 0.6, Math.Max(10, thickness * 4));
        Point back = end - direction * length;
        Vector side = new(-direction.Y * length * 0.45, direction.X * length * 0.45);
        StreamGeometry geometry = new();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.LineTo(back, true, false);
            context.BeginFigure(end, true, true);
            context.LineTo(ClampPoint(back + side), true, false);
            context.LineTo(ClampPoint(back - side), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private Annotation? Hit(Point point)
    {
        for (int layer = 0; layer < 2; layer++)
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            Annotation item = _items[i];
            if ((item.Tool == EditorTool.Redact) != (layer == 0)) continue;
            Point local = new((point.X - item.Offset.X) / item.ScaleX, (point.Y - item.Offset.Y) / item.ScaleY);
            Rect bounds = item.Bounds;
            double tolerance = 5 * DisplayUnit() / Math.Min(item.ScaleX, item.ScaleY);
            bounds.Inflate(Math.Max(tolerance, item.Thickness / 2), Math.Max(tolerance, item.Thickness / 2));
            if (!bounds.Contains(local)) continue;
            if (item.Tool != EditorTool.Pen && item.Tool != EditorTool.Arrow ||
                item.Geometry.FillContains(local) ||
                item.Geometry.StrokeContains(new Pen(Brushes.Black, Math.Max(tolerance * 2, item.Thickness + 6)), local))
                return item;
        }
        return null;
    }

    private Point ClampPoint(Point point) => new(
        Math.Clamp(point.X, _editingBounds.Left, _editingBounds.Right),
        Math.Clamp(point.Y, _editingBounds.Top, _editingBounds.Bottom));

    private Point ClampOffset(Annotation item, Point offset) => new(
        Math.Clamp(offset.X, _editingBounds.Left - item.Bounds.Left * item.ScaleX,
            Math.Max(_editingBounds.Left - item.Bounds.Left * item.ScaleX, _editingBounds.Right - item.Bounds.Right * item.ScaleX)),
        Math.Clamp(offset.Y, _editingBounds.Top - item.Bounds.Top * item.ScaleY,
            Math.Max(_editingBounds.Top - item.Bounds.Top * item.ScaleY, _editingBounds.Bottom - item.Bounds.Bottom * item.ScaleY)));

    private static Rect WorldBounds(Annotation item)
    {
        return new Rect(item.Bounds.Left * item.ScaleX + item.Offset.X,
            item.Bounds.Top * item.ScaleY + item.Offset.Y,
            item.Bounds.Width * item.ScaleX, item.Bounds.Height * item.ScaleY);
    }

    // Immutable geometry plus small per-object state makes edits exact and independently reversible.
    private static State Snapshot(Annotation item) => new(item.Geometry, item.Bounds, item.Offset,
        item.Text, item.ScaleX, item.ScaleY, item.Brush, item.Thickness, item.ArrowStart, item.ArrowEnd);

    private static void Restore(Annotation item, State state)
    {
        item.Geometry = state.Geometry;
        item.Bounds = state.Bounds;
        item.Offset = state.Offset;
        item.Text = state.Text;
        item.ScaleX = state.ScaleX;
        item.ScaleY = state.ScaleY;
        item.Brush = state.Brush;
        item.Thickness = state.Thickness;
        item.ArrowStart = state.ArrowStart;
        item.ArrowEnd = state.ArrowEnd;
    }

    internal Rect SelectionBounds => _selected == null ? Rect.Empty : WorldBounds(_selected);

    private IEnumerable<(Handle Handle, Point Center)> Handles(Annotation item)
    {
        if (item.Tool == EditorTool.Arrow)
        {
            yield return (Handle.ArrowStart, ArrowPoint(item, item.ArrowStart));
            yield return (Handle.ArrowEnd, ArrowPoint(item, item.ArrowEnd));
            yield break;
        }
        Rect b = WorldBounds(item);
        if (item.Tool == EditorTool.Pen || b.Width < 0.01 || b.Height < 0.01) yield break;
        yield return (Handle.Left | Handle.Top, b.TopLeft);
        yield return (Handle.Right | Handle.Top, b.TopRight);
        yield return (Handle.Right | Handle.Bottom, b.BottomRight);
        yield return (Handle.Left | Handle.Bottom, b.BottomLeft);
        if (item.Tool == EditorTool.Text) yield break; // Text keeps its original letter proportions.
        yield return (Handle.Left, new Point(b.Left, b.Top + b.Height / 2));
        yield return (Handle.Right, new Point(b.Right, b.Top + b.Height / 2));
        yield return (Handle.Top, new Point(b.Left + b.Width / 2, b.Top));
        yield return (Handle.Bottom, new Point(b.Left + b.Width / 2, b.Bottom));
    }

    private Handle HitHandle(Point point)
    {
        if (_selected == null || _textBox != null) return Handle.None;
        double radius = 6 * DisplayUnit(), best = double.PositiveInfinity;
        Handle result = Handle.None;
        foreach ((Handle handle, Point center) in Handles(_selected))
        {
            Vector delta = point - center;
            if (Math.Abs(delta.X) <= radius && Math.Abs(delta.Y) <= radius && delta.LengthSquared < best)
            {
                best = delta.LengthSquared;
                result = handle;
            }
        }
        return result;
    }

    private double DisplayUnit()
    {
        // This canvas is commonly inside a Viewbox; handles stay eight display DIPs wide.
        if (PresentationSource.FromVisual(this)?.RootVisual is not Visual root || root == this) return 1;
        GeneralTransform transform = TransformToAncestor(root);
        Point origin = transform.Transform(new Point());
        double scale = Math.Max((transform.Transform(new Point(1, 0)) - origin).Length,
            (transform.Transform(new Point(0, 1)) - origin).Length);
        return 1 / Math.Max(0.01, scale);
    }

    private static Cursor HandleCursor(Handle handle) => handle switch
    {
        Handle.Left or Handle.Right => Cursors.SizeWE,
        Handle.Top or Handle.Bottom => Cursors.SizeNS,
        Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
        Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
        Handle.ArrowStart or Handle.ArrowEnd => Cursors.SizeAll,
        _ => Cursors.Arrow
    };

    internal Cursor CursorAt(Point point)
    {
        Handle handle = HitHandle(point);
        if (handle != Handle.None) return HandleCursor(handle);
        if (_selected != null && Hit(point) == _selected && Tool != EditorTool.Text) return Cursors.SizeAll;
        return Tool == EditorTool.Select ? Cursors.Arrow : Tool == EditorTool.Text ? Cursors.IBeam : Cursors.Cross;
    }

    private static Point ArrowPoint(Annotation item, Point point) =>
        new(point.X * item.ScaleX + item.Offset.X, point.Y * item.ScaleY + item.Offset.Y);

    private void Resize(Point point)
    {
        if (_resizing == null || _beforeResize == null) return;
        if (_resizing.Tool == EditorTool.Arrow)
        {
            Point originalStart = new(_beforeResize.ArrowStart.X * _beforeResize.ScaleX + _beforeResize.Offset.X,
                _beforeResize.ArrowStart.Y * _beforeResize.ScaleY + _beforeResize.Offset.Y);
            Point originalEnd = new(_beforeResize.ArrowEnd.X * _beforeResize.ScaleX + _beforeResize.Offset.X,
                _beforeResize.ArrowEnd.Y * _beforeResize.ScaleY + _beforeResize.Offset.Y);
            Point start = originalStart, end = originalEnd;
            Vector shift = point - _start;
            if (_resizeHandle == Handle.ArrowStart) start = ClampPoint(start + shift);
            else end = ClampPoint(end + shift);
            // Keep a real shaft; crossing the other endpoint is allowed and reverses the arrow.
            if ((end - start).Length < 2) return;
            if (start == originalStart && end == originalEnd)
            {
                Restore(_resizing, _beforeResize);
                _gestureChanged = false;
                return;
            }
            // Rebuild at world coordinates so arrowhead clipping also respects a previous move.
            _resizing.ArrowStart = start;
            _resizing.ArrowEnd = end;
            _resizing.Offset = new Point();
            _resizing.ScaleX = _resizing.ScaleY = 1;
            _resizing.Geometry = Arrow(start, end, _resizing.Thickness);
            _resizing.Bounds = _resizing.Geometry.Bounds;
            _gestureChanged = Snapshot(_resizing) != _beforeResize;
            return;
        }
        Rect b = _resizeBounds;
        bool left = _resizeHandle.HasFlag(Handle.Left), right = _resizeHandle.HasFlag(Handle.Right);
        bool top = _resizeHandle.HasFlag(Handle.Top), bottom = _resizeHandle.HasFlag(Handle.Bottom);
        // Use pointer delta so grabbing near a handle never makes the object jump.
        Vector delta = point - _start;
        double l = left ? Math.Clamp(b.Left + delta.X, _editingBounds.Left, Math.Max(_editingBounds.Left, b.Right - 2)) : b.Left;
        double r = right ? Math.Clamp(b.Right + delta.X, Math.Min(_editingBounds.Right, b.Left + 2), _editingBounds.Right) : b.Right;
        double t = top ? Math.Clamp(b.Top + delta.Y, _editingBounds.Top, Math.Max(_editingBounds.Top, b.Bottom - 2)) : b.Top;
        double d = bottom ? Math.Clamp(b.Bottom + delta.Y, Math.Min(_editingBounds.Bottom, b.Top + 2), _editingBounds.Bottom) : b.Bottom;
        double minWidth = Math.Min(2, _editingBounds.Width), minHeight = Math.Min(2, _editingBounds.Height);
        if (_resizing.Tool == EditorTool.Text)
        {
            double dx = b.Width + (left ? -delta.X : delta.X);
            double dy = b.Height + (top ? -delta.Y : delta.Y);
            double ratio = (dx * b.Width + dy * b.Height) / (b.Width * b.Width + b.Height * b.Height);
            double anchorX = left ? Math.Clamp(b.Right, _editingBounds.Left + minWidth, _editingBounds.Right)
                : Math.Clamp(b.Left, _editingBounds.Left, _editingBounds.Right - minWidth);
            double anchorY = top ? Math.Clamp(b.Bottom, _editingBounds.Top + minHeight, _editingBounds.Bottom)
                : Math.Clamp(b.Top, _editingBounds.Top, _editingBounds.Bottom - minHeight);
            double maximum = Math.Min((left ? anchorX - _editingBounds.Left : _editingBounds.Right - anchorX) / b.Width,
                (top ? anchorY - _editingBounds.Top : _editingBounds.Bottom - anchorY) / b.Height);
            double minimum = Math.Min(maximum, Math.Max(2 / b.Width, 2 / b.Height));
            ratio = Math.Clamp(ratio, minimum, maximum);
            l = left ? anchorX - b.Width * ratio : anchorX;
            r = right ? anchorX + b.Width * ratio : anchorX;
            t = top ? anchorY - b.Height * ratio : anchorY;
            d = bottom ? anchorY + b.Height * ratio : anchorY;
        }
        else
        {
            l = Math.Clamp(l, _editingBounds.Left, _editingBounds.Right - minWidth);
            r = Math.Clamp(r, l + minWidth, _editingBounds.Right);
            t = Math.Clamp(t, _editingBounds.Top, _editingBounds.Bottom - minHeight);
            d = Math.Clamp(d, t + minHeight, _editingBounds.Bottom);
        }
        _resizing.ScaleX = _beforeResize.ScaleX * (r - l) / b.Width;
        _resizing.ScaleY = _beforeResize.ScaleY * (d - t) / b.Height;
        _resizing.Offset = new Point(l - _resizing.Bounds.Left * _resizing.ScaleX,
            t - _resizing.Bounds.Top * _resizing.ScaleY);
        _gestureChanged = Snapshot(_resizing) != _beforeResize;
    }

    private void BeginText(Annotation? item, Point point)
    {
        _newText = item == null;
        item ??= new Annotation
        {
            Tool = EditorTool.Text, Brush = Stroke.CloneCurrentValue(),
            Offset = new Point(Math.Min(point.X, Math.Max(_editingBounds.Left, _editingBounds.Right - 24)),
                Math.Min(point.Y, Math.Max(_editingBounds.Top, _editingBounds.Bottom - 32)))
        };
        if (item.TextWidth == 0) item.TextWidth = Math.Max(1, Math.Min(360, _editingBounds.Right - item.Offset.X));
        _selected = _textItem = item;
        _originalText = item.Text;
        TextBox box = new()
        {
            Style = null,
            Text = item.Text, FontFamily = new FontFamily("Segoe UI"), FontSize = 24,
            Foreground = item.Brush, Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            BorderThickness = new System.Windows.Thickness(0), Padding = new System.Windows.Thickness(0),
            Width = Math.Min(item.TextWidth, Width / item.ScaleX),
            MaxHeight = Math.Max(1, ((_newText ? _editingBounds.Bottom : Height) - item.Offset.Y) / item.ScaleY),
            MinHeight = Math.Min(32, Math.Max(1, ((_newText ? _editingBounds.Bottom : Height) - item.Offset.Y) / item.ScaleY)),
            LayoutTransform = new ScaleTransform(item.ScaleX, item.ScaleY),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _textBox = box;
        SetLeft(box, item.Offset.X);
        SetTop(box, item.Offset.Y);
        box.TextChanged += (_, _) => Notify();
        box.LostKeyboardFocus += (_, _) => { if (_textBox == box && !box.IsKeyboardFocusWithin) CommitText(); };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { HandleKey(Key.Escape); e.Handled = true; Focus(); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            { CommitText(); e.Handled = true; Focus(); }
        };
        Children.Add(box);
        box.Focus();
        box.CaretIndex = box.Text.Length;
        InvalidateVisual();
    }

    private void CloseTextBox()
    {
        TextBox? box = _textBox;
        if (_newText) _selected = null;
        _textBox = null;
        _textItem = null;
        _originalText = "";
        if (box != null) Children.Remove(box);
    }

    private void SetText(Annotation item, string text, bool constrainToRegion = false)
    {
        item.Text = text;
        FormattedText formatted = new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 24, item.Brush, 1)
        {
            MaxTextWidth = Math.Min(item.TextWidth, Width / item.ScaleX),
            MaxTextHeight = (constrainToRegion ? Math.Max(1, _editingBounds.Bottom - item.Offset.Y) : Height) / item.ScaleY
        };
        item.Geometry = formatted.BuildGeometry(new Point());
        if (item.Geometry.CanFreeze) item.Geometry.Freeze();
        item.Bounds = new Rect(0, 0, Math.Max(1, formatted.WidthIncludingTrailingWhitespace), formatted.Height);
    }

    private void Add(Annotation item)
    {
        int index = _items.Count;
        _items.Add(item);
        Record(() => _items.Remove(item), () => _items.Insert(index, item));
    }

    private void Record(Action undo, Action redo)
    {
        _undo.Push(new Edit(undo, redo));
        _redo.Clear();
        Notify();
    }

    private void Notify()
    {
        InvalidateVisual();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
