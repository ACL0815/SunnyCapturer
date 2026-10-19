using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KairosCapture.Capture;
using Forms = System.Windows.Forms;
using Color = System.Drawing.Color;
using Size = System.Drawing.Size;

namespace KairosCapture;

internal sealed class TrayMenu : IDisposable
{
    internal Forms.ContextMenuStrip Menu { get; } = new();
    private readonly MainWindow _window;
    private readonly Action<int> _delay;
    private readonly List<Forms.ToolStripMenuItem> _captureItems = new();
    private readonly Forms.ToolStripMenuItem _region, _resume, _recent, _ocrStatus;
    private readonly List<Image> _images = new();

    internal TrayMenu(MainWindow window, Action<int> delay)
    {
        _window = window; _delay = delay;
        Menu.Renderer = new DarkRenderer(); Menu.ShowImageMargin = true;
        Menu.BackColor = Color.FromArgb(23, 24, 34); Menu.ForeColor = Color.FromArgb(244, 242, 250);
        Menu.Font = new Font("Segoe UI", 10); Menu.Padding = new Forms.Padding(7, 7, 7, 7);
        _region = Capture("Bereich aufnehmen", "Capture", CaptureMode.Region);
        Capture("Vollbild aufnehmen", "Fullscreen", CaptureMode.Fullscreen);
        var delayed = Item("Verzögert aufnehmen", "Timer");
        foreach (int seconds in new[] { 3, 5, 10 })
        {
            var entry = Item(seconds + " s", "Timer", delayed.DropDownItems);
            entry.Click += (_, _) => _delay(seconds);
        }
        _captureItems.Add(delayed);
        Capture("Text erkennen…", "Ocr", CaptureMode.Ocr);
        _resume = Capture("Letzte Aufnahme fortsetzen", "History", CaptureMode.Resume);
        Menu.Items.Add(new Forms.ToolStripSeparator());
        _recent = Item("Zuletzt gespeichert", "Save");
        _ocrStatus = Item("OCR: nicht verbunden", "Account");
        _ocrStatus.Click += (_, _) => _window.ShowFromTray();
        Menu.Items.Add(new Forms.ToolStripSeparator());
        Item("Einstellungen…", "Settings").Click += (_, _) => _window.ShowFromTray();
        Item("Beenden", "Exit").Click += async (_, _) => await _window.RequestExitAsync();
        Menu.Opening += (_, _) => Refresh(CaptureService.IsCapturing, CaptureService.LastSession != null);
        Refresh(false, CaptureService.LastSession != null);
    }
    private Forms.ToolStripMenuItem Item(string text, string glyph, Forms.ToolStripItemCollection? collection = null)
    {
        var item = new Forms.ToolStripMenuItem(text) { Tag = glyph, ForeColor = Menu.ForeColor, Padding = new Forms.Padding(3, 5, 8, 5) };
        (collection ?? Menu.Items).Add(item);
        item.DropDown.BackColor = Menu.BackColor; item.DropDown.ForeColor = Menu.ForeColor; item.DropDown.Renderer = Menu.Renderer;
        return item;
    }
    private Forms.ToolStripMenuItem Capture(string text, string glyph, CaptureMode mode)
    {
        var item = Item(text, glyph); _captureItems.Add(item);
        item.Click += async (_, _) => await _window.CaptureAsync(mode);
        return item;
    }
    internal void Refresh(bool capturing, bool hasSession)
    {
        _region.Text = "Bereich aufnehmen · " + _window.Settings.Hotkey;
        foreach (var item in _captureItems) item.Enabled = !capturing;
        _resume.Enabled = !capturing && hasSession;
        _ocrStatus.Text = _window.OcrStatus;
        var old = _recent.DropDownItems.Cast<Forms.ToolStripItem>().ToArray();
        _recent.DropDownItems.Clear(); foreach (var item in old) item.Dispose();
        foreach (string path in _window.Settings.RecentFiles.Where(File.Exists).Take(5))
        {
            var item = Item(Path.GetFileName(path), "History", _recent.DropDownItems);
            item.ToolTipText = path; item.Click += (_, _) => OpenFile(path);
        }
        if (_recent.DropDownItems.Count == 0)
            _recent.DropDownItems.Add(new Forms.ToolStripMenuItem("Noch keine Aufnahmen gespeichert") { Enabled = false });
        _recent.DropDownItems.Add(new Forms.ToolStripSeparator());
        Item("Ordner öffnen", "Folder", _recent.DropDownItems).Click += (_, _) => _window.OpenSaveFolder();
        foreach (var item in AllItems(Menu.Items)) item.Image = null;
        foreach (var image in _images) image.Dispose(); _images.Clear();
        int size = Math.Max(20, (int)Math.Round(20d * Menu.DeviceDpi / 96));
        Menu.ImageScalingSize = new Size(size, size);
        foreach (var item in AllItems(Menu.Items))
            if (item.Tag is string glyph)
            {
                var image = RenderGlyph(_window, glyph, size);
                _images.Add(image); item.Image = image;
                if (item.HasDropDownItems) item.DropDown.ImageScalingSize = new Size(size, size);
            }
    }
    private void OpenFile(string path)
    {
        try { GlassWindow.Open(path); }
        catch (Exception ex) { App.LogException("Open saved capture", ex); _window.SetStatus("Die gespeicherte Datei konnte nicht geöffnet werden: " + ex.Message); _window.ShowFromTray(); }
    }
    private static IEnumerable<Forms.ToolStripMenuItem> AllItems(Forms.ToolStripItemCollection items)
    {
        foreach (Forms.ToolStripItem entry in items)
            if (entry is Forms.ToolStripMenuItem item)
            { yield return item; foreach (var child in AllItems(item.DropDownItems)) yield return child; }
    }
    internal static Bitmap RenderGlyph(FrameworkElement context, string glyph, int size)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 24d, size / 24d));
            dc.DrawGeometry(null, new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 197, 241)), 1.7)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, context.TryFindResource("Glyph." + glyph) as Geometry);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        using var stream = new MemoryStream(ImageOutput.EncodePng(bitmap));
        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }
    public void Dispose()
    {
        Menu.Dispose(); foreach (var image in _images) image.Dispose(); _images.Clear();
    }

    private sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
    {
        internal DarkRenderer() : base(new DarkColors()) { RoundedEdges = true; }
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            using var fill = new SolidBrush(e.Item.Selected && e.Item.Enabled ? Color.FromArgb(69, 48, 98) : Color.FromArgb(23, 24, 34));
            var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using var shape = new GraphicsPath(); const int diameter = 12;
            shape.AddArc(r.Left, r.Top, diameter, diameter, 180, 90); shape.AddArc(r.Right - diameter, r.Top, diameter, diameter, 270, 90);
            shape.AddArc(r.Right - diameter, r.Bottom - diameter, diameter, diameter, 0, 90); shape.AddArc(r.Left, r.Bottom - diameter, diameter, diameter, 90, 90); shape.CloseFigure();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.FillPath(fill, shape);
        }
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        { e.TextColor = e.Item.Enabled ? Color.FromArgb(244, 242, 250) : Color.FromArgb(130, 126, 143); base.OnRenderItemText(e); }
        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        { e.ArrowColor = Color.FromArgb(176, 151, 233); base.OnRenderArrow(e); }
    }
    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(23, 24, 34);
        public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
        public override Color MenuBorder => Color.FromArgb(79, 65, 99);
        public override Color SeparatorDark => Color.FromArgb(58, 51, 73);
        public override Color SeparatorLight => ToolStripDropDownBackground;
    }
}
