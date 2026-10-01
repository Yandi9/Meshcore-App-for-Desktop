using System.Runtime.InteropServices;
using Avalonia.Controls;
using MC1.Windows.Services;

namespace MC1.Windows.Platform.Windows;

/// <summary>Draws the unread count as an overlay badge on the app's taskbar button (ITaskbarList3::SetOverlayIcon).</summary>
public static class TaskbarOverlay
{
    private static ITaskbarList3? _taskbar;

    public static void Set(Window window, int count)
    {
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        if (_taskbar is null)
        {
            _taskbar = (ITaskbarList3)new TaskbarList();
            _taskbar.HrInit();
        }
        if (count <= 0)
        {
            _taskbar.SetOverlayIcon(hwnd, IntPtr.Zero, null);
            return;
        }
        var icon = CreateIcon(UnreadBadge.RenderBubblePixels(count, 32), 32);
        try { _taskbar.SetOverlayIcon(hwnd, icon, L.Plural(count, "{0} unread message", "{0} unread messages")); }
        finally { if (icon != IntPtr.Zero) DestroyIcon(icon); }
    }

    /// <summary>Builds an HICON from premultiplied BGRA pixels.</summary>
    private static IntPtr CreateIcon(byte[] bgra, int size)
    {
        // Icons expect straight (non-premultiplied) alpha.
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a is 0 or 255) continue;
            bgra[i] = (byte)Math.Min(255, bgra[i] * 255 / a);
            bgra[i + 1] = (byte)Math.Min(255, bgra[i + 1] * 255 / a);
            bgra[i + 2] = (byte)Math.Min(255, bgra[i + 2] * 255 / a);
        }
        var color = CreateBitmap(size, size, 1, 32, bgra);
        var mask = CreateBitmap(size, size, 1, 1, new byte[size * size / 8]);
        try
        {
            var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList;

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? tip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    }
}
