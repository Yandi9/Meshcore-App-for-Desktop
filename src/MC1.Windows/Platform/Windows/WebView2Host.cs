using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MC1.Core.Services;
using MC1.Windows.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace MC1.Windows.Platform.Windows;

/// <summary>Microsoft Edge WebView2 embedded in an Avalonia layout (a child HWND hosting a CoreWebView2Controller).</summary>
public sealed class WebView2Host : NativeControlHost
{
    private IntPtr _hwnd;
    private CoreWebView2Controller? _controller;
    private bool _destroyed;

    public CoreWebView2? WebView => _controller?.CoreWebView2;

    /// <summary>Raised once the browser is ready (on the UI thread).</summary>
    public event Action<CoreWebView2>? Ready;

    /// <summary>Raised when WebView2 can't start (e.g. the runtime isn't installed).</summary>
    public event Action<string>? Failed;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _hwnd = CreateWindowExW(0, "Static", null, WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1, parent.Handle, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        Dispatcher.UIThread.Post(() => _ = InitAsync());
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _destroyed = true;
        try { _controller?.Close(); } catch { /* closing anyway */ }
        _controller = null;
        DestroyWindow(control.Handle);
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await WebView2Support.GetEnvironmentAsync();
            var controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);
            if (_destroyed)
            {
                controller.Close();
                return;
            }
            _controller = controller;
            UpdateBounds();
            controller.IsVisible = true;
            Ready?.Invoke(controller.CoreWebView2);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Failed?.Invoke(L.T("The Microsoft Edge WebView2 Runtime isn't installed on this PC."));
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) UpdateBounds();
    }

    private void UpdateBounds()
    {
        if (_controller is null) return;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0,
            Math.Max(1, (int)Math.Round(Bounds.Width * scale)), Math.Max(1, (int)Math.Round(Bounds.Height * scale)));
    }

    public void FocusBrowser()
    {
        try { _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch { /* not ready */ }
    }

    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}

/// <summary>Shared WebView2 environment. The loader DLL ships inside the app and is unpacked next to the browser profile.</summary>
public static class WebView2Support
{
    private static Task<CoreWebView2Environment>? _env;
    private static bool _loaderReady;

    public static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        PrepareLoader();
        return _env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(DataFolder, "Profile"));
    }

    private static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCore", "WebView2");

    /// <summary>Single-file builds can't load WebView2Loader.dll from inside the bundle, so it's written to disk once.</summary>
    private static void PrepareLoader()
    {
        if (_loaderReady) return;
        _loaderReady = true;
        try
        {
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream($"WebView2Loader.{arch}.dll");
            if (res is null) return;
            var dir = Path.Combine(DataFolder, "loader-" + arch);
            var path = Path.Combine(dir, "WebView2Loader.dll");
            if (!File.Exists(path) || new FileInfo(path).Length != res.Length)
            {
                Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                using (var f = File.Create(tmp)) res.CopyTo(f);
                File.Move(tmp, path, true);
            }
            CoreWebView2Environment.SetLoaderDllFolderPath(dir);
        }
        catch (Exception ex)
        {
            AppHost.Core?.Log.Warn("WebView2", "Loader setup failed: " + ex.Message);
        }
    }
}
