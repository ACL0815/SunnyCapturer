using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingPoint = System.Drawing.Point;

namespace KairosCapture.Capture;

public static class CaptureService
{
    private static int _capturing;

    public static Task<BitmapSource?> CaptureRegionAsync(Window owner) => CaptureAsync(owner, true);

    public static Task<BitmapSource?> CaptureScreenAsync(Window owner) => CaptureAsync(owner, false);

    private static async Task<BitmapSource?> CaptureAsync(Window owner, bool region)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.Dispatcher.VerifyAccess();
        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
            return null;

        Visibility visibility = owner.Visibility;
        WindowState state = owner.WindowState;
        bool ownerClosed = false;
        void OnOwnerClosed(object? sender, EventArgs args) => ownerClosed = true;
        owner.Closed += OnOwnerClosed;
        RegionOverlay? overlay = null;
        try
        {
            owner.Hide();
            await Task.Delay(100);
            if (ownerClosed)
                return null;

            Rectangle bounds = GetVirtualBounds();
            BitmapSource screenshot = CaptureDesktop(bounds);
            if (!region)
                return screenshot;

            overlay = new RegionOverlay(screenshot, bounds) { Owner = owner };
            overlay.ShowDialog();
            if (overlay.Failure is not null)
                throw overlay.Failure;
            Int32Rect selection = overlay.Selection;
            if (selection.Width == 0 || selection.Height == 0)
                return null;

            var cropped = new CroppedBitmap(screenshot, selection);
            cropped.Freeze();
            return cropped;
        }
        finally
        {
            try
            {
                overlay?.Close();
                owner.Closed -= OnOwnerClosed;
                if (!ownerClosed)
                {
                    owner.WindowState = state;
                    if (visibility == Visibility.Visible)
                    {
                        owner.Show();
                        owner.Activate();
                    }
                    else
                    {
                        owner.Visibility = visibility;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _capturing, 0);
            }
        }
    }

    // Requires the shell's PerMonitorV2 manifest: these metrics are physical pixels.
    private static Rectangle GetVirtualBounds()
    {
        var bounds = new Rectangle(GetSystemMetrics(76), GetSystemMetrics(77),
            GetSystemMetrics(78), GetSystemMetrics(79));
        CaptureGeometry.ValidateSize(bounds.Width, bounds.Height);
        return bounds;
    }

    private static BitmapSource CaptureDesktop(Rectangle bounds)
    {
        CaptureGeometry.ValidateSize(bounds.Width, bounds.Height);
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            // Normal desktop capture only; protected content may remain black.
            graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        BitmapData bits = bitmap.LockBits(new Rectangle(0, 0, bounds.Width, bounds.Height),
            ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            BitmapSource image = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96,
                PixelFormats.Bgr32, null, bits.Scan0, checked(bits.Stride * bounds.Height), bits.Stride);
            image.Freeze();
            return image;
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }
    }

    // Deliberately separate from Run(): only a caller explicitly requesting a real capture uses this.
    internal static BitmapSource CaptureDesktopForCheck() => CaptureDesktop(GetVirtualBounds());

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

internal static class CaptureGeometry
{
    internal static void ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height > 64L * 1024 * 1024)
            throw new InvalidOperationException($"Das Bildraster ({width} × {height}) ist ungültig oder zu groß. " +
                "Unterstützt werden maximal 32768 Pixel pro Seite und insgesamt 64 Megapixel (67108864 Pixel).");
    }

    internal static Int32Rect Select(Rectangle bounds, DrawingPoint start, DrawingPoint end)
    {
        int x1 = (int)Math.Clamp((long)start.X - bounds.X, 0, bounds.Width);
        int y1 = (int)Math.Clamp((long)start.Y - bounds.Y, 0, bounds.Height);
        int x2 = (int)Math.Clamp((long)end.X - bounds.X, 0, bounds.Width);
        int y2 = (int)Math.Clamp((long)end.Y - bounds.Y, 0, bounds.Height);
        return new Int32Rect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }
}
