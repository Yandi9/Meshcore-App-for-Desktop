using MC1.Core.Services;
using MC1.Windows.Services;
using MC1.Windows.ViewModels;
using Microsoft.Win32;
using Windows.Devices.Geolocation;

namespace MC1.Windows.Platform.Windows;

/// <summary>Wires Windows-only services (Bluetooth LE, toasts, location, COM port names, meshcore:// links) into the app.</summary>
public static class WindowsPlatform
{
    public static void Initialize(MeshApp core, InAppNotifier inApp)
    {
        core.BluetoothTransportFactory = t => new WinBleTransport(WinBleTransport.ParseAddress(t.Address), t.DisplayName, () => core.Settings.Current.BluetoothPin, core.Log);
        AppHost.Bluetooth = new WinBleScanner();
        try { inApp.System = new ToastNotifier(core); }
        catch (Exception ex) { core.Log.Warn("Toast", "Windows notifications unavailable: " + ex.Message); }
        AppHost.GetPcLocation = GetLocationAsync;
        WavPlayer.Initialize(core.Log);
        Controls.StartupSplash.PlayWav = WavPlayer.Play;
        Controls.StartupSplash.StopWav = WavPlayer.Stop;
        // Make the start-up sound and get it ready to play while the window opens.
        if (core.Settings.Current.StartupAnimation && core.Settings.Current.StartupSound)
            _ = Task.Run(() => WavPlayer.Prepare(Controls.StartupSound.Wav));
        SerialPortNames.Provider = ReadSerialPortNames;
        UnreadBadge.TaskbarOverlay = (window, count) =>
        {
            try { TaskbarOverlay.Set(window, count); }
            catch (Exception ex) { core.Log.Debug("App", "Taskbar badge unavailable: " + ex.Message); }
        };
        RegisterUrlProtocol(core);
        AppHost.SystemAnimationsEnabled = ClientAreaAnimationsEnabled;
        AppHost.GetAutoStart = AutoStart.IsEnabled;
        AppHost.SetAutoStart = on => AutoStart.Set(on, core);
        AutoStart.RefreshPath(core);
        LockScreenStatus.Initialize(core);
        LiveStatusHub.StartPipeServer(m => core.Log.Info("Widget", m));
        AppHost.LockScreenStatusChanged = on => { if (!on) LockScreenStatus.Hide(); };
        AppHost.Widget = new LockScreenWidgetInstaller(core);
    }

    public static void Shutdown() => ToastNotifier.Shutdown();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out int pvParam, uint fWinIni);

    /// <summary>Windows Settings → Accessibility → Visual effects → "Animation effects".</summary>
    private static bool ClientAreaAnimationsEnabled()
    {
        const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
        try { return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var on, 0) || on != 0; }
        catch { return true; }
    }

    private static async Task<(double Lat, double Lon)?> GetLocationAsync()
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed) return null;
            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.High };
            var pos = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(20));
            return (pos.Coordinate.Point.Position.Latitude, pos.Coordinate.Point.Position.Longitude);
        }
        catch { return null; }
    }

    /// <summary>Maps "COM5" → "USB Serial Device (COM5)" using the device registry.</summary>
    private static IReadOnlyDictionary<string, string> ReadSerialPortNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bus in new[] { @"SYSTEM\CurrentControlSet\Enum\USB", @"SYSTEM\CurrentControlSet\Enum\FTDIBUS", @"SYSTEM\CurrentControlSet\Enum\BTHENUM" })
        {
            using var root = Registry.LocalMachine.OpenSubKey(bus);
            if (root is null) continue;
            foreach (var devName in root.GetSubKeyNames())
            {
                using var dev = root.OpenSubKey(devName);
                if (dev is null) continue;
                foreach (var instName in dev.GetSubKeyNames())
                {
                    using var inst = dev.OpenSubKey(instName);
                    using var parms = inst?.OpenSubKey("Device Parameters");
                    if (parms?.GetValue("PortName") is string port && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    {
                        var friendly = inst!.GetValue("FriendlyName") as string ?? inst.GetValue("DeviceDesc") as string;
                        if (friendly is not null)
                        {
                            var semi = friendly.LastIndexOf(';');
                            if (friendly.StartsWith('@') && semi >= 0) friendly = friendly[(semi + 1)..];
                            map[port] = friendly.Contains(port, StringComparison.OrdinalIgnoreCase) ? friendly : $"{friendly} ({port})";
                        }
                    }
                }
            }
        }
        return map;
    }

    /// <summary>Registers meshcore:// links for the current user so shared channel/contact links open the app.</summary>
    private static void RegisterUrlProtocol(MeshApp core)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\meshcore");
            key.SetValue("", "URL:MeshCore link");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch (Exception ex) { core.Log.Debug("App", "Couldn't register meshcore:// links: " + ex.Message); }
    }
}
