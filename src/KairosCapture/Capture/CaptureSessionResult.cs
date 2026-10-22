using System.Drawing;
using System.Windows;
using System.Windows.Media.Imaging;
using KairosCapture.Editor;

namespace KairosCapture.Capture;

public enum CaptureCompletion { Cancelled, Copied, Saved, Ocr, Pinned }
internal enum CaptureMode { Region, Fullscreen, Ocr, Resume }

public sealed record CaptureSessionResult(CaptureCompletion Completion, AnnotationCanvas? Editor = null, Int32Rect Region = default)
{
    internal BitmapSource? Screenshot { get; init; }
    internal BitmapSource? Image { get; init; }
    internal Rectangle Bounds { get; init; }
    internal string? SavedPath { get; init; }
    internal bool ReturnToPreviousForeground { get; init; }
}
