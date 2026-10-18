using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace KairosCapture.Capture;

public static class CaptureSelfChecks
{
    /// <summary>Pure geometry and local image I/O only. No desktop capture, windows, hotkeys or clipboard.</summary>
    public static void Run()
    {
        var bounds = new DrawingRectangle(-1920, -1080, 4480, 2520);
        var start = new DrawingPoint(-1800, -900);
        var end = new DrawingPoint(120, 100);
        var expected = new Int32Rect(120, 180, 1920, 1000);
        Check(CaptureGeometry.Select(bounds, start, end) == expected, "Negative Desktopkoordinaten");
        Check(CaptureGeometry.Select(bounds, end, start) == expected, "Rückwärtsauswahl");
        Check(CaptureGeometry.Select(bounds, new DrawingPoint(int.MinValue, int.MaxValue),
            new DrawingPoint(int.MaxValue, int.MinValue)) == new Int32Rect(0, 0, 4480, 2520), "Clamping/Überlauf");
        Int32Rect empty = CaptureGeometry.Select(bounds, start, start);
        Check(empty.Width == 0 && empty.Height == 0, "Leere Auswahl");
        Check(CaptureGeometry.Select(bounds, new DrawingPoint(-3000, -2000), new DrawingPoint(-2500, -1500)).Width == 0,
            "Auswahl außerhalb des Desktops");
        CaptureGeometry.ValidateSize(32768, 1);
        ExpectInvalidSize(32769, 1);
        ExpectInvalidSize(20000, 20000);
        ExpectInvalidSize(0, 10);

        byte[] pixels =
        {
            0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255,
            255, 255, 255, 255, 0, 0, 0, 255, 30, 60, 90, 255
        };
        BitmapSource image = BitmapSource.Create(3, 2, 144, 192, PixelFormats.Bgra32, null, pixels, 12);
        image.Freeze();
        byte[] png = ImageOutput.EncodePng(image);
        Check(png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG-Signatur");
        string path = Path.Combine(Path.GetTempPath(), $"kairos-check-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, png);
            BitmapSource loaded = ImageOutput.Load(path);
            // Exclusive reopening and deletion prove that Load released the source file handle.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(path);
            Check(loaded.IsFrozen && loaded.PixelWidth == 3 && loaded.PixelHeight == 2 &&
                Math.Abs(loaded.DpiX - 96) < 0.01 && Math.Abs(loaded.DpiY - 96) < 0.01, "Native Pixel/96 DPI/frozen");
            var converted = new FormatConvertedBitmap(loaded, PixelFormats.Bgra32, null, 0);
            byte[] actual = new byte[pixels.Length];
            converted.CopyPixels(actual, 12, 0);
            Check(actual.SequenceEqual(pixels), "PNG-Pixelroundtrip nach Freigabe der Datei");

            BitmapEncoder[] encoders = { new BmpBitmapEncoder(), new JpegBitmapEncoder() };
            foreach (BitmapEncoder encoder in encoders)
            {
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                    encoder.Save(stream);
                BitmapSource decoded = ImageOutput.Load(path);
                File.Delete(path);
                Check(decoded.IsFrozen && decoded.PixelWidth == 3 && decoded.PixelHeight == 2 &&
                    Math.Abs(decoded.DpiX - 96) < 0.01 && Math.Abs(decoded.DpiY - 96) < 0.01, "BMP/JPEG laden");
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void ExpectInvalidSize(int width, int height)
    {
        try
        {
            CaptureGeometry.ValidateSize(width, height);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("CaptureSelfChecks: Rasterlimit wurde nicht geprüft.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"CaptureSelfChecks: {name} fehlgeschlagen.");
    }
}
