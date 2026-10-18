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

    internal static bool IsCapturing => Volatile.Read(ref _capturing) != 0;
    internal static event EventHandler? CaptureStateChanged;

    public static Task<BitmapSource?> CaptureRegionAsync(Window owner) => CaptureRegionAsync(owner, CancellationToken.None);

    public static Task<BitmapSource?> CaptureScreenAsync(Window owner) => CaptureScreenAsync(owner, CancellationToken.None);

    public static async Task<BitmapSource?> CaptureRegionAsync(Window owner, CancellationToken cancellationToken) =>
        (await CaptureAsync(owner, true, false, cancellationToken)).Raw;

    public static async Task<BitmapSource?> CaptureScreenAsync(Window owner, CancellationToken cancellationToken) =>
        (await CaptureAsync(owner, false, false, cancellationToken)).Raw;

    public static async Task<CaptureSessionResult> CaptureEditedRegionAsync(Window owner, CancellationToken cancellationToken = default) =>
        (await CaptureAsync(owner, true, true, cancellationToken)).Edited;

    public static async Task<CaptureSessionResult> CaptureEditedRegionAsync(Window owner, CancellationToken cancellationToken,
        Func<Window, Task<bool>> confirmHandoff)
    {
        ArgumentNullException.ThrowIfNull(confirmHandoff);
        return (await CaptureAsync(owner, true, true, cancellationToken, confirmHandoff)).Edited;
    }

    private static async Task<(BitmapSource? Raw, CaptureSessionResult Edited)> CaptureAsync(Window owner, bool region, bool editing,
        CancellationToken cancellationToken, Func<Window, Task<bool>>? confirmHandoff = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
            return (null, new(CaptureCompletion.Cancelled));

        Visibility visibility = owner.Visibility;
        WindowState state = owner.WindowState;
        bool ownerClosed = false;
        RegionOverlay? overlay = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnOwnerClosed(object? sender, EventArgs args)
        {
            ownerClosed = true;
            lifetime.Cancel();
            overlay?.Cancel();
        }
        owner.Closed += OnOwnerClosed;
        try
        {
            CaptureStateChanged?.Invoke(null, EventArgs.Empty);
            owner.Hide();
            if (visibility == Visibility.Visible) await Task.Delay(100, lifetime.Token);
            if (ownerClosed)
                return (null, new(CaptureCompletion.Cancelled));

            lifetime.Token.ThrowIfCancellationRequested();
            // A tray action may have shown the owner during the delay. Hide again without
            // yielding, then wait for this application's queued compositor work to finish.
            owner.Hide();
            Marshal.ThrowExceptionForHR(DwmFlush());
            Rectangle bounds = GetVirtualBounds();
            BitmapSource screenshot = CaptureDesktop(bounds);
            lifetime.Token.ThrowIfCancellationRequested();
            if (!region)
                return (screenshot, new(CaptureCompletion.Cancelled));

            overlay = new RegionOverlay(screenshot, bounds, editing, confirmHandoff) { Owner = owner };
            using CancellationTokenRegistration cancelSelection = lifetime.Token.Register(() =>
            {
                if (!owner.Dispatcher.HasShutdownStarted)
                    owner.Dispatcher.BeginInvoke(new Action(() => overlay.Cancel()));
            });
            lifetime.Token.ThrowIfCancellationRequested();
            overlay.ShowDialog();
            lifetime.Token.ThrowIfCancellationRequested();
            if (ownerClosed)
                return (null, new(CaptureCompletion.Cancelled));
            if (overlay.Failure is not null)
                throw overlay.Failure;
            if (editing)
                return (null, overlay.Result);
            Int32Rect selection = overlay.Selection;
            if (selection.Width == 0 || selection.Height == 0)
                return (null, new(CaptureCompletion.Cancelled));

            var cropped = new CroppedBitmap(screenshot, selection);
            cropped.Freeze();
            return (cropped, new(CaptureCompletion.Cancelled));
        }
        catch (OperationCanceledException) when (ownerClosed && !cancellationToken.IsCancellationRequested)
        {
            return (null, new(CaptureCompletion.Cancelled));
        }
        finally
        {
            try
            {
                overlay?.Cancel();
                owner.Closed -= OnOwnerClosed;
                if (!ownerClosed)
                {
                    owner.WindowState = state;
                    if (editing && overlay?.Result.Completion is CaptureCompletion.Copied or CaptureCompletion.Saved or CaptureCompletion.Workbench or CaptureCompletion.Ocr)
                    {
                        owner.Hide();
                    }
                    else if (visibility == Visibility.Visible)
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
                CaptureStateChanged?.Invoke(null, EventArgs.Empty);
            }
        }
    }

    // Requires the shell's PerMonitorV2 manifest: these metrics are physical pixels.
    private static Rectangle GetVirtualBounds()
    {
        if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new IntPtr(-4)))
            throw new InvalidOperationException("Die Aufnahme benötigt die PerMonitorV2-DPI-Einstellung des Anwendungsmanifests.");
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
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
