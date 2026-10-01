using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using MC1.Core.Services;
using MC1.Windows.Services;
using Windows.Management.Deployment;

namespace MC1.Windows.Platform.Windows;

/// <summary>
/// Installs the MeshCore widget package that ships in the "LockScreenWidget" folder next to MeshCore.exe.
/// Windows only accepts widgets from installed (packaged) apps, so the widget is a small signed .msix; its
/// certificate is added to the PC's Trusted People store once (that's the admin prompt).
/// </summary>
public sealed class LockScreenWidgetInstaller(MeshApp core) : ILockScreenWidget
{
    public const string PackageName = "MeshCore.LockScreenWidget";
    public const string Publisher = "CN=MeshCore";
    private const string RuntimeName = "Microsoft.WindowsAppRuntime.2";
    private const string RuntimePublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "LockScreenWidget");

    /// <summary>The package architecture to use: the PC's own (an x64 MeshCore on an Arm PC still gets the Arm widget).</summary>
    private static IEnumerable<string> Architectures => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => ["arm64", "x64"],
        _ => ["x64"],
    };

    private static (string Widget, string? Runtime, string Arch)? Files()
    {
        foreach (var arch in Architectures)
        {
            var widget = Path.Combine(Folder, $"MeshCoreWidget_{arch}.msix");
            if (!File.Exists(widget)) continue;
            var runtime = Path.Combine(Folder, $"{RuntimeName}_{arch}.msix");
            return (widget, File.Exists(runtime) ? runtime : null, arch);
        }
        return null;
    }

    private static string CertificatePath => Path.Combine(Folder, "MeshCore.cer");

    private static Version? ManifestVersion(string msix)
    {
        try
        {
            using var zip = ZipFile.OpenRead(msix);
            using var s = zip.GetEntry("AppxManifest.xml")!.Open();
            var identity = XDocument.Load(s).Root!.Elements().First(e => e.Name.LocalName == "Identity");
            return Version.Parse((string)identity.Attribute("Version")!);
        }
        catch { return null; }
    }

    private static global::Windows.ApplicationModel.Package? Installed()
    {
        try { return new PackageManager().FindPackagesForUser("", PackageName, Publisher).FirstOrDefault(); }
        catch { return null; }
    }

    private static Version VersionOf(global::Windows.ApplicationModel.Package p)
    {
        var v = p.Id.Version;
        return new Version(v.Major, v.Minor, v.Build, v.Revision);
    }

    public WidgetInstallState State
    {
        get
        {
            var files = Files();
            var installed = Installed();
            if (installed is null) return files is null ? WidgetInstallState.Unavailable : WidgetInstallState.NotInstalled;
            if (files is { } f && ManifestVersion(f.Widget) is { } bundled && bundled > VersionOf(installed)) return WidgetInstallState.UpdateAvailable;
            return WidgetInstallState.Installed;
        }
    }

    public string Description => State switch
    {
        WidgetInstallState.Installed => L.F("Installed ({0}). Add it in Windows Settings ▸ Personalization ▸ Lock screen.", VersionOf(Installed()!).ToString(3)),
        WidgetInstallState.UpdateAvailable => L.T("A newer widget is ready to install."),
        WidgetInstallState.NotInstalled => L.T("Shows new messages and the radio's battery on the lock screen and in Widgets (Win+W)."),
        _ => L.T("The LockScreenWidget folder isn't next to MeshCore.exe."),
    };

    private static bool CertificateTrusted(X509Certificate2 cert)
    {
        foreach (var (name, location) in new[] { (StoreName.TrustedPeople, StoreLocation.LocalMachine), (StoreName.Root, StoreLocation.LocalMachine) })
        {
            try
            {
                using var store = new X509Store(name, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                if (store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0) return true;
            }
            catch { /* store unreadable: try the next */ }
        }
        return false;
    }

    /// <summary>Adds the certificate to Trusted People with an elevated certutil (the one admin prompt). False if cancelled.</summary>
    private async Task<bool> TrustCertificateAsync()
    {
        var cert = X509CertificateLoader.LoadCertificateFromFile(CertificatePath);
        if (CertificateTrusted(cert)) return true;
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "certutil.exe"), $"-addstore -f TrustedPeople \"{CertificatePath}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException(L.T("certutil didn't start"));
            await p.WaitForExitAsync().ConfigureAwait(false);
            core.Log.Info("Widget", $"certutil finished with {p.ExitCode}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; } // the user said No
        if (!CertificateTrusted(cert)) throw new InvalidOperationException(L.T("Windows didn't accept the widget's certificate."));
        return true;
    }

    /// <summary>True when the Windows App Runtime the widget needs is already on the PC (same version or newer).</summary>
    private static bool RuntimeInstalled(string runtimeMsix, string arch)
    {
        var needed = ManifestVersion(runtimeMsix) ?? new Version(2, 0);
        try
        {
            var packages = new PackageManager().FindPackagesForUserWithPackageTypes("", RuntimeName, RuntimePublisher, PackageTypes.Framework);
            return packages.Any(p => p.Id.Architecture.ToString().Equals(arch, StringComparison.OrdinalIgnoreCase) && VersionOf(p) >= needed);
        }
        catch { return false; }
    }

    public async Task<string> InstallAsync(IProgress<string>? progress = null)
    {
        if (Files() is not { } files) return L.T("The widget files aren't next to MeshCore.exe (LockScreenWidget folder).");
        progress?.Report(L.T("Trusting the widget's certificate…"));
        if (!File.Exists(CertificatePath)) return L.T("MeshCore.cer is missing from the LockScreenWidget folder.");
        if (!await TrustCertificateAsync().ConfigureAwait(false)) return L.T("Cancelled — Windows needs to trust the widget's certificate before it can be installed.");

        var deps = new List<Uri>();
        if (files.Runtime is { } rt && !RuntimeInstalled(rt, files.Arch)) deps.Add(new Uri(rt));
        progress?.Report(deps.Count > 0 ? L.T("Installing the Windows App Runtime and the widget…") : L.T("Installing the widget…"));
        var pm = new PackageManager();
        var op = pm.AddPackageAsync(new Uri(files.Widget), deps, DeploymentOptions.ForceApplicationShutdown | DeploymentOptions.ForceUpdateFromAnyVersion);
        op.Progress = (_, p) => progress?.Report(L.F("Installing… {0}%", p.percentage));
        if (await DeployAsync(op.AsTask(), op.GetResults).ConfigureAwait(false) is { } error)
        {
            core.Log.Warn("Widget", "Install failed: " + error);
            return L.F("The widget wasn't installed: {0}", error);
        }
        core.Log.Info("Widget", "Lock screen widget installed");
        return L.T("Widget installed. Add it in Windows Settings ▸ Personalization ▸ Lock screen ▸ Widgets (look for MeshCore).");
    }

    public async Task<string> RemoveAsync()
    {
        if (Installed() is not { } p) return L.T("The widget isn't installed.");
        var op = new PackageManager().RemovePackageAsync(p.Id.FullName);
        if (await DeployAsync(op.AsTask(), op.GetResults).ConfigureAwait(false) is { } error) return L.F("The widget wasn't removed: {0}", error);
        return L.T("Widget removed.");
    }

    /// <summary>Waits for a deployment; null when it worked, otherwise Windows' explanation (a failed operation throws,
    /// and the reason is in its results).</summary>
    private static async Task<string?> DeployAsync(Task<DeploymentResult> task, Func<DeploymentResult> results)
    {
        DeploymentResult? result = null;
        Exception? failure = null;
        try { result = await task.ConfigureAwait(false); }
        catch (Exception ex)
        {
            failure = ex;
            try { result = results(); } catch { /* no details */ }
        }
        var code = result?.ExtendedErrorCode?.HResult ?? failure?.HResult ?? 0;
        if (code == 0 && failure is null) return null;
        var text = result?.ErrorText;
        return string.IsNullOrWhiteSpace(text) ? $"{failure?.Message ?? L.T("error")} (0x{code:X8})" : text.Trim();
    }
}
