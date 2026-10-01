namespace MC1.Windows.Services;

public enum WidgetInstallState
{
    /// <summary>Not on this PC (the widget files aren't next to MeshCore, or this isn't Windows).</summary>
    Unavailable,
    NotInstalled,
    Installed,
    /// <summary>An older widget is installed; the one next to MeshCore is newer.</summary>
    UpdateAvailable,
}

/// <summary>The MeshCore lock screen widget (a small packaged widget provider installed next to the app).</summary>
public interface ILockScreenWidget
{
    WidgetInstallState State { get; }

    /// <summary>One line for Settings, e.g. "Installed (1.0.0)".</summary>
    string Description { get; }

    /// <summary>Trusts the widget's certificate (one Windows admin prompt) and installs the widget. Returns what happened.</summary>
    Task<string> InstallAsync(IProgress<string>? progress = null);

    Task<string> RemoveAsync();
}
