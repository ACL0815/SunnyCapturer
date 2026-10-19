using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace KairosCapture.Capture;

public static class ImageOutput
{
    public static byte[] EncodePng(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(AtNativeDpi(image)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapSource Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Read dimensions before materializing the pixel buffer. OnLoad would decode
        // an oversized image before the shared raster limit could reject it.
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        if (decoder is not PngBitmapDecoder && decoder is not JpegBitmapDecoder && decoder is not BmpBitmapDecoder)
            throw new NotSupportedException("Unterstützt werden PNG-, JPEG- und BMP-Bilder.");
        // Copy while the stream is open, including already-96-DPI frames, so the
        // returned frozen bitmap never retains a lazy decoder or file dependency.
        BitmapSource image = AtNativeDpi(decoder.Frames[0], detach: true);
        image.Freeze();
        return image;
    }

    public static void Copy(BitmapSource image)
    {
        RequireSta();
        byte[] png = EncodePng(image);
        using var imageStream = new MemoryStream(png, false);
        using var bitmap = new System.Drawing.Bitmap(imageStream);
        using var pngStream = new MemoryStream(png, false);
        var data = new Forms.DataObject();
        data.SetImage(bitmap);
        data.SetData("PNG", false, pngStream);
        try
        {
            // WinForms exposes a bounded OLE clipboard retry; copy=true persists both formats after exit.
            Forms.Clipboard.SetDataObject(data, true, 5, 40);
        }
        catch (ExternalException exception)
        {
            throw new InvalidOperationException("Die Zwischenablage ist belegt. Bitte kurz warten und erneut kopieren.", exception);
        }
    }

    public static string? Save(BitmapSource image, Window owner) => Save(image, owner, new AppSettings());

    internal static string? Save(BitmapSource image, Window owner, AppSettings settings,
        Func<SaveFileDialog, Window, bool?>? showDialog = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(owner);
        owner.Dispatcher.VerifyAccess();
        RequireSta();
        string directory = settings.SaveFolder;
        Directory.CreateDirectory(directory);
        if (settings.SaveWithoutDialog) return SaveDirect(image, directory, DateTime.Now);
        var dialog = new SaveFileDialog
        {
            Title = "Aufnahme speichern",
            Filter = "PNG-Bild (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            RestoreDirectory = true,
            InitialDirectory = directory,
            FileName = FileName(DateTime.Now)
        };
        if ((showDialog ?? ((prompt, parent) => prompt.ShowDialog(parent)))(dialog, owner) != true)
            return null;

        return Write(image, dialog.FileName, overwrite: true);
    }

    internal static string FileName(DateTime now) => $"Kairos {now:yyyy-MM-dd HHmmss}.png";

    internal static string SaveDirect(BitmapSource image, string directory, DateTime now)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName(now));
        for (int suffix = 1; ; suffix++)
        {
            try { return Write(image, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            { path = Path.Combine(directory, Path.GetFileNameWithoutExtension(FileName(now)) + $" ({suffix}).png"); }
        }
    }

    private static string Write(BitmapSource image, string path, bool overwrite)
    {
        byte[] png = EncodePng(image);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".kairos-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, png);
            File.Move(temporary, path, overwrite);
            return path;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static BitmapSource AtNativeDpi(BitmapSource image, bool detach = false)
    {
        CaptureGeometry.ValidateSize(image.PixelWidth, image.PixelHeight);
        if (!detach && image.DpiX == 96 && image.DpiY == 96)
            return image;

        int stride = checked((image.PixelWidth * image.Format.BitsPerPixel + 7) / 8);
        byte[] pixels = new byte[checked(stride * image.PixelHeight)];
        image.CopyPixels(pixels, stride, 0);
        BitmapSource native = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96,
            image.Format, image.Palette, pixels, stride);
        native.Freeze();
        return native;
    }

    private static void RequireSta()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Diese Aktion muss auf dem STA-Thread des Fensters ausgeführt werden.");
    }
}
