using System.Windows;
using KairosCapture.Editor;

namespace KairosCapture.Capture;

public enum CaptureCompletion { Cancelled, Copied, Saved, Workbench, Ocr }

public sealed record CaptureSessionResult(CaptureCompletion Completion, AnnotationCanvas? Editor = null, Int32Rect Region = default);
