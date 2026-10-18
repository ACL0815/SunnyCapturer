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
    }

    private sealed record Edit(Action Undo, Action Redo);

    private readonly BitmapSource _image;
    private readonly List<Annotation> _items = new();
    private readonly Stack<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();
    private Annotation? _selected, _draft, _moving, _textItem;
    private TextBox? _textBox;
    private PolyLineSegment? _penPoints;
    private Point _start, _originalOffset;
    private bool _gestureChanged, _newText;
    private string _originalText = "";
    private EditorTool _tool;
    private Brush _stroke = new SolidColorBrush(Color.FromRgb(0xC4, 0xB5, 0xFD));
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
        set => _stroke = value ?? throw new ArgumentNullException(nameof(value));
    }

    public double Thickness
    {
        get => _thickness;
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _thickness = Math.Clamp(value, 1, 24);
        }
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
        Point oldOffset = item.Offset;
        SetText(item, text);
        item.Offset = ClampOffset(item, item.Offset);
        Point newOffset = item.Offset;
        _selected = item;
        if (isNew) Add(item);
        else Record(
            () => { SetText(item, before); item.Offset = oldOffset; },
            () => { SetText(item, text); item.Offset = newOffset; });
    }

    /// <summary>Exports at 96 DPI, where one canvas unit is exactly one output pixel.</summary>
    public BitmapSource Flatten()
    {
        EndGesture();
        CommitText();
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen()) DrawImageAndAnnotations(drawing);
        RenderTargetBitmap bitmap = new(_image.PixelWidth, _image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        DrawImageAndAnnotations(drawing);
        if (_selected != null && _textBox == null && Tool == EditorTool.Select)
        {
            Rect bounds = WorldBounds(_selected);
            bounds.Inflate(3, 3);
            drawing.DrawRectangle(null, new Pen(Brushes.Black, 3), bounds);
            drawing.DrawRectangle(null, new Pen(_stroke, 1) { DashStyle = DashStyles.Dash }, bounds);
        }
    }

    private void DrawImageAndAnnotations(DrawingContext drawing)
    {
        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, Width, Height)));
        drawing.DrawImage(_image, new Rect(0, 0, Width, Height));
        foreach (Annotation item in _items)
            if (item != _textItem) DrawAnnotation(drawing, item);
        if (_draft != null) DrawAnnotation(drawing, _draft);
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
        drawing.PushTransform(new TranslateTransform(item.Offset.X, item.Offset.Y));
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
        if ((_draft != null || _moving != null) && !CaptureMouse()) EndGesture(true);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draft == null && _moving == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) EndGesture();
        else UpdateGesture(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_draft == null && _moving == null) return;
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
        if (e.Key != Key.Escape || _textBox != null) return;
        EndGesture(true);
        _selected = null;
        InvalidateVisual();
        e.Handled = true;
    }

    // These gesture methods are also used by the offline STA self-check; no synthetic input is needed.
    internal void BeginGesture(Point point, int clickCount = 1)
    {
        EndGesture();
        CommitText();
        _start = ClampPoint(point);
        Annotation? hit = Hit(_start);
        if ((Tool == EditorTool.Select && clickCount >= 2 && hit?.Tool == EditorTool.Text) || Tool == EditorTool.Text)
        {
            BeginText(hit?.Tool == EditorTool.Text ? hit : null, _start);
            return;
        }
        if (Tool == EditorTool.Select)
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
        InvalidateVisual();
    }

    internal void UpdateGesture(Point point)
    {
        point = ClampPoint(point);
        if (_moving != null)
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
        Annotation? draft = _draft, moving = _moving;
        _draft = _moving = null;
        _penPoints = null;
        _gestureChanged = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (draft != null && !cancel && IsValid(draft))
        {
            if (draft.Geometry.CanFreeze) draft.Geometry.Freeze();
            _selected = draft;
            Add(draft);
        }
        else if (moving != null)
        {
            Point before = _originalOffset, after = moving.Offset;
            if (cancel) moving.Offset = before;
            else if (before != after) Record(() => moving.Offset = before, () => moving.Offset = after);
        }
        if (draft != null || moving != null) Notify();
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
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            Annotation item = _items[i];
            Point local = new(point.X - item.Offset.X, point.Y - item.Offset.Y);
            Rect bounds = item.Bounds;
            bounds.Inflate(Math.Max(5, item.Thickness / 2), Math.Max(5, item.Thickness / 2));
            if (!bounds.Contains(local)) continue;
            if (item.Tool != EditorTool.Pen && item.Tool != EditorTool.Arrow ||
                item.Geometry.FillContains(local) ||
                item.Geometry.StrokeContains(new Pen(Brushes.Black, Math.Max(10, item.Thickness + 6)), local))
                return item;
        }
        return null;
    }

    private Point ClampPoint(Point point) => new(
        Math.Clamp(point.X, 0, Width), Math.Clamp(point.Y, 0, Height));

    private Point ClampOffset(Annotation item, Point offset) => new(
        Math.Clamp(offset.X, -item.Bounds.Left, Math.Max(-item.Bounds.Left, Width - item.Bounds.Right)),
        Math.Clamp(offset.Y, -item.Bounds.Top, Math.Max(-item.Bounds.Top, Height - item.Bounds.Bottom)));

    private static Rect WorldBounds(Annotation item)
    {
        Rect bounds = item.Bounds;
        bounds.Offset(item.Offset.X, item.Offset.Y);
        return bounds;
    }

    private void BeginText(Annotation? item, Point point)
    {
        _newText = item == null;
        item ??= new Annotation
        {
            Tool = EditorTool.Text, Brush = Stroke.CloneCurrentValue(),
            Offset = new Point(Math.Min(point.X, Math.Max(0, Width - 24)),
                Math.Min(point.Y, Math.Max(0, Height - 32)))
        };
        if (item.TextWidth == 0) item.TextWidth = Math.Max(1, Math.Min(360, Width - item.Offset.X));
        _selected = _textItem = item;
        _originalText = item.Text;
        TextBox box = new()
        {
            Text = item.Text, FontFamily = new FontFamily("Segoe UI"), FontSize = 24,
            Foreground = item.Brush, Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            BorderThickness = new System.Windows.Thickness(0), Padding = new System.Windows.Thickness(0),
            Width = item.TextWidth, MaxHeight = Math.Max(1, Height - item.Offset.Y),
            MinHeight = Math.Min(32, Math.Max(1, Height - item.Offset.Y)),
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
            if (e.Key == Key.Escape) { CloseTextBox(); Notify(); e.Handled = true; Focus(); }
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

    private void SetText(Annotation item, string text)
    {
        item.Text = text;
        FormattedText formatted = new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 24, item.Brush, 1)
        {
            MaxTextWidth = item.TextWidth,
            MaxTextHeight = Height
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
