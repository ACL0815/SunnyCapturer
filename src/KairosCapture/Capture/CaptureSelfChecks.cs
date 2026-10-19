using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KairosCapture.Capture;

public static class CaptureSelfChecks
{
    /// <summary>Raster limits and local image I/O only. No desktop capture, windows, hotkeys or clipboard.</summary>
    public static void Run()
    {
        CaptureGeometry.ValidateSize(32768, 1);
        ExpectInvalidSize(32769, 1);
        ExpectInvalidSize(20000, 20000);
        ExpectInvalidSize(0, 10);
        CheckPhysicalCrop();

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

    private static void CheckPhysicalCrop()
    {
        const int width = 7, height = 5;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)(x * 30);
                pixels[offset + 1] = (byte)(y * 40);
                pixels[offset + 2] = (byte)(x + y * width);
                pixels[offset + 3] = 255;
            }

        BitmapSource source = BitmapSource.Create(width, height, 144, 192, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();
        var selected = new Int32Rect(1, 1, 4, 2);
        var crop = new CroppedBitmap(source, selected);
        crop.Freeze();
        byte[] expectedPixels = new byte[4 * 2 * 4];
        Buffer.BlockCopy(pixels, (1 * width + 1) * 4, expectedPixels, 0, 4 * 4);
        Buffer.BlockCopy(pixels, (2 * width + 1) * 4, expectedPixels, 4 * 4, 4 * 4);
        byte[] actual = new byte[expectedPixels.Length];
        crop.CopyPixels(actual, 4 * 4, 0);
        Check(crop.PixelWidth == 4 && crop.PixelHeight == 2 && actual.SequenceEqual(expectedPixels),
            "Crop verwendet Bildpixel statt DIPs bei 144/192 DPI");

        byte[] png = ImageOutput.EncodePng(crop);
        Check(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) == 4 &&
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) == 2, "PNG-IHDR enthält physische Cropmaße");
        string path = Path.Combine(Path.GetTempPath(), $"kairos-crop-check-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, png);
            BitmapSource loaded = ImageOutput.Load(path);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(path);
            Check(loaded.IsFrozen && loaded.PixelWidth == 4 && loaded.PixelHeight == 2 &&
                Math.Abs(loaded.DpiX - 96) < 0.01 && Math.Abs(loaded.DpiY - 96) < 0.01,
                "Crop-Datei bleibt nach Freigabe in nativen Pixelmaßen verfügbar");
            var converted = new FormatConvertedBitmap(loaded, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(actual, 4 * 4, 0);
            Check(actual.SequenceEqual(expectedPixels), "Crop-PNG erhält jeden ausgewählten Pixel");
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
            throw new SelfCheckFailure($"CaptureSelfChecks: {name} fehlgeschlagen.");
    }
}
