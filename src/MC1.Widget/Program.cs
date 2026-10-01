using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Windows.Widgets.Providers;

namespace MC1.Widget;

/// <summary>
/// Out-of-process COM server that Windows' widget host starts ("-RegisterProcessAsComServer") to show the
/// MeshCore widget (lock screen and Widgets board). It stays running while any MeshCore widget is pinned.
/// </summary>
public static partial class Program
{
    /// <summary>Must match the class id in the package manifest (packaging/widget/AppxManifest.xml).</summary>
    public const string ProviderClsid = "6B3B5C63-4E0A-4B8A-9D0C-2C1F4A7E5B91";

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(in Guid rclsid, nint pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint dwRegister);

    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const uint REGCLS_MULTIPLEUSE = 0x1;

    [MTAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (!args.Any(a => a.Equals("-RegisterProcessAsComServer", StringComparison.OrdinalIgnoreCase)))
        {
            // Started by hand: nothing to show; the widget lives in Windows' widget surfaces.
            return 0;
        }
        var wrappers = new StrategyBasedComWrappers();
        var factory = wrappers.GetOrCreateComInterfaceForObject(new WidgetProviderFactory(), CreateComInterfaceFlags.None);
        var clsid = Guid.Parse(ProviderClsid);
        try
        {
            while (true)
            {
                var hr = CoRegisterClassObject(in clsid, factory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out var cookie);
                if (hr < 0) return hr;
                // Keep serving until the last widget is removed.
                WidgetProvider.NoWidgetsLeft.WaitOne();
                CoRevokeClassObject(cookie);
                // A widget added while we were stopping (e.g. moved from the Widgets board to the lock screen): keep going.
                if (!WidgetProvider.HasWidgets) return 0;
                WidgetProvider.NoWidgetsLeft.Reset();
            }
        }
        finally { Marshal.Release(factory); }
    }
}

[GeneratedComInterface, Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(nint pUnkOuter, in Guid riid, out nint ppvObject);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

[GeneratedComClass]
internal sealed partial class WidgetProviderFactory : IClassFactory
{
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    public int CreateInstance(nint pUnkOuter, in Guid riid, out nint ppvObject)
    {
        ppvObject = 0;
        if (pUnkOuter != 0) return CLASS_E_NOAGGREGATION;
        var provider = WinRT.MarshalInspectable<IWidgetProvider>.FromManaged(new WidgetProvider());
        var iid = riid;
        var hr = Marshal.QueryInterface(provider, in iid, out ppvObject);
        Marshal.Release(provider);
        return hr < 0 ? E_NOINTERFACE : 0;
    }

    public int LockServer(bool fLock) => 0;
}
