using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LimitsWidget.Windows;

internal static class FrostedBackdrop
{
    public static bool Apply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return false;
        using var preferences = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        if (preferences?.GetValue("EnableTransparency") is int transparency && transparency == 0) return false;
        var handle = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is null || SystemParameters.HighContrast) return false;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        int enabled = 1, rounded = 2, acrylic = 3;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(handle, ref margins) != 0) return false;
        // The system backdrop can become opaque on an inactive overlay. Keep the
        // accent interop isolated; if unavailable, use the documented system material.
        int none = 1;
        _ = DwmSetWindowAttribute(handle, 38, ref none, sizeof(int));
        if (TryOverlayAcrylic(handle)) return true;
        return DwmSetWindowAttribute(handle, 38, ref acrylic, sizeof(int)) == 0;
    }

    public static void UpdateCorners(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        // A custom HRGN clips WPF, but not the separate Acrylic quad, and also
        // disables DWM's own corner mask. Let DWM round the entire composition.
        _ = SetWindowRgn(handle, 0, true);
        int rounded = 2;
        _ = DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
    }

    private static bool TryOverlayAcrylic(nint handle)
    {
        var accent = new AccentPolicy { State = 4, GradientColor = 0x802B2621 };
        nint data = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(accent, data, false);
            var attribute = new CompositionAttribute { Attribute = 19, Data = data, Size = (nuint)Marshal.SizeOf<AccentPolicy>() };
            return SetWindowCompositionAttribute(handle, ref attribute);
        }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(data); }
    }

    public static ImageBrush CreateNoise()
    {
        const int size = 96;
        var bytes = new byte[size * size * 4];
        var random = new Random(617);
        for (int i = 0; i < bytes.Length; i += 4)
        {
            byte gray = random.Next(2) == 0 ? (byte)0 : (byte)255;
            bytes[i] = bytes[i + 1] = bytes[i + 2] = gray;
            bytes[i + 3] = (byte)random.Next(0, 4);
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bytes, size * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, size, size), Stretch = Stretch.None };
        brush.Freeze();
        return brush;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int State, Flags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionAttribute { public int Attribute; public nint Data; public nuint Size; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowCompositionAttribute(nint hwnd, ref CompositionAttribute data);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
