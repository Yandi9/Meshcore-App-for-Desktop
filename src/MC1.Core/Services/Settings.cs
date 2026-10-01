using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MC1.Core.Services;

public enum ConnectionKind { Serial, Tcp, Bluetooth, Simulator }

/// <summary>Where to find a radio. <see cref="Address"/> is a COM port, "host:port", a Bluetooth address or "demo".</summary>
public sealed record ConnectionTarget(ConnectionKind Kind, string Address, string? DisplayName = null, int Port = 5000)
{
    /// <summary>What to show for this connection ("USB · COM3"), in the app's language. Not stored anywhere.</summary>
    public string Describe() => Kind switch
    {
        ConnectionKind.Serial => L.F("USB · {0}", Address),
        ConnectionKind.Tcp => L.F("WiFi · {0}:{1}", Address, Port),
        ConnectionKind.Bluetooth => L.F("Bluetooth · {0}", DisplayName ?? Address),
        _ => L.T("Demo radio (simulated)"),
    };
}

/// <summary>User preferences persisted as JSON in %APPDATA%\MeshCore\settings.json.</summary>
public sealed class AppSettings
{
    // Connection
    public ConnectionTarget? LastConnection { get; set; }
    public List<ConnectionTarget> RecentConnections { get; set; } = [];
    public bool AutoReconnect { get; set; } = true;
    public bool ConnectOnLaunch { get; set; } = true;
    public string BluetoothPin { get; set; } = "123456";

    // Appearance
    /// <summary>The app's language ("es", "zh-Hans", …); null follows Windows' display language.</summary>
    public string? Language { get; set; }
    public string Theme { get; set; } = "System";
    public string AccentColor { get; set; } = "#2F80ED";
    public double FontScale { get; set; } = 1.0;
    public bool CompactChatList { get; set; }

    // Chats
    public bool ShowLinkPreviews { get; set; } = true;
    public bool ShowInlineImages { get; set; } = true;
    public bool ShowMapPreviews { get; set; } = true;
    public bool ShowHeardRepeats { get; set; } = true;
    public bool SendOnEnter { get; set; } = true;
    public bool UseTwentyFourHourTime { get; set; }
    public int DmMaxAttempts { get; set; } = 5;
    public int DmFloodAfter { get; set; } = 4;

    // Notifications
    public bool NotificationsEnabled { get; set; } = true;
    public bool NotifyDirectMessages { get; set; } = true;
    public bool NotifyChannelMessages { get; set; } = true;
    public bool NotifyRoomMessages { get; set; } = true;
    public bool NotifyNewContacts { get; set; } = true;
    public bool NotifyLowBattery { get; set; } = true;
    public bool NotificationSound { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;

    // Map
    public string MapLayer { get; set; } = "Standard";
    public double MapLatitude { get; set; } = 39.5;
    public double MapLongitude { get; set; } = -98.35;
    public double MapZoom { get; set; } = 4;
    public bool MapShowChat { get; set; } = true;
    public bool MapShowRepeaters { get; set; } = true;
    public bool MapShowRooms { get; set; } = true;
    public bool MapShowDiscovered { get; set; } = true;
    public bool UseMetricUnits { get; set; } = true;

    // Location
    public bool ShareLocationFromPc { get; set; }

    // Diagnostics
    public bool RxLogEnabled { get; set; } = true;
    public int RxLogMaxEntries { get; set; } = 3000;
    public bool DebugLogging { get; set; }

    // Line of sight defaults
    public double LosHeightA { get; set; } = 2;
    public double LosHeightB { get; set; } = 10;
    public double LosRefractionK { get; set; } = 1.333;

    // Emoji
    public List<string> RecentEmoji { get; set; } = ["👍", "❤️", "😂", "🎉", "👏", "🔥"];

    // Chat appearance (per-conversation backgrounds and colours)
    public bool ChatThemesEnabled { get; set; } = true;
    /// <summary>Keyed by "radioId|conversationKey"; "*" holds the default applied to every chat without its own.</summary>
    public Dictionary<string, ChatTheme> ChatThemes { get; set; } = new();
    public bool ShowUnreadOnTaskbar { get; set; } = true;

    // Window state
    /// <summary>"radioId|conversationKey" of the chat that was open when the app was last used.</summary>
    public string? LastConversation { get; set; }
    /// <summary>Show the navigation rail as a slim icon strip.</summary>
    public bool NavCollapsed { get; set; }

    // Windows start-up
    /// <summary>Start the app when the user signs in to Windows.</summary>
    public bool StartWithWindows { get; set; }
    /// <summary>When started with Windows, stay in the notification area instead of opening the window.</summary>
    public bool StartMinimized { get; set; } = true;

    // Lock screen
    /// <summary>While the PC is locked, show new messages and radio battery as a notification on the lock screen.</summary>
    public bool LockScreenStatus { get; set; } = true;

    /// <summary>
    /// The welcome guide was seen. Null in settings saved by versions before the guide existed — those users have used
    /// the app already, so they don't get it.
    /// </summary>
    public bool? WelcomeGuideShown { get; set; }

    /// <summary>The "Set up your radio" step after the first connection was offered.</summary>
    public bool RadioSetupShown { get; set; }

    /// <summary>Play the start-up animation when MeshCore starts (not when the window is reopened from the tray).</summary>
    public bool StartupAnimation { get; set; } = true;
    /// <summary>Sound effects with the start-up animation.</summary>
    public bool StartupSound { get; set; } = true;

    /// <summary>Radio id → the preset last applied from this app (to tell apart presets with the same settings).</summary>
    public Dictionary<string, string> AppliedRadioPresets { get; set; } = new();

    /// <summary>Radio id → radio settings before repeat mode was switched on ("freqMHz,bwKHz,sf,cr"), restored when it's switched off.</summary>
    public Dictionary<string, string> PreRepeatRadio { get; set; } = new();
}

/// <summary>Custom look of one conversation. Colours are "#RRGGBB" / "#AARRGGBB"; null means "use the app theme".</summary>
public sealed class ChatTheme
{
    /// <summary>"None", "Color", "Gradient" or "Image".</summary>
    public string BackgroundKind { get; set; } = "None";
    public string? BackgroundColor { get; set; }
    public string? BackgroundColor2 { get; set; }
    /// <summary>"Vertical", "Horizontal" or "Diagonal".</summary>
    public string GradientDirection { get; set; } = "Vertical";
    /// <summary>File name inside the app's backgrounds folder.</summary>
    public string? ImageFile { get; set; }
    /// <summary>0..0.9 — how much the image is faded towards the app background so text stays readable.</summary>
    public double ImageDim { get; set; } = 0.35;
    public string? IncomingBubble { get; set; }
    public string? IncomingText { get; set; }
    public string? OutgoingBubble { get; set; }
    public string? OutgoingText { get; set; }
    public string? PresetName { get; set; }

    [JsonIgnore]
    public bool IsEmpty => BackgroundKind == "None" && IncomingBubble is null && IncomingText is null && OutgoingBubble is null && OutgoingText is null;

    public ChatTheme Clone() => (ChatTheme)MemberwiseClone();
}

public sealed class SettingsStore
{
    public static JsonSerializerOptions JsonOptions => Options;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _lock = new();

    public SettingsStore(string path)
    {
        _path = path;
        IsFirstRun = !File.Exists(path);
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    /// <summary>True the very first time MeshCore runs on this PC (no settings were saved before).</summary>
    public bool IsFirstRun { get; }
    public event Action? Changed;

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
        }
        catch { /* corrupt: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, _path, true);
        }
        Changed?.Invoke();
    }

    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Save();
    }

    public string Export() => JsonSerializer.Serialize(Current, Options);

    public void Import(string json)
    {
        Current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        Save();
    }
}

/// <summary>Stores remote-node passwords encrypted for the current Windows user (DPAPI).</summary>
public sealed class SecretStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string> _entries;

    public SecretStore(string path)
    {
        _path = path;
        _entries = Load();
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var raw = File.ReadAllBytes(_path);
            var json = Encoding.UTF8.GetString(Unprotect(raw));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch { return new(); }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_entries))));
    }

    private static byte[] Protect(byte[] data) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser) : data;

    private static byte[] Unprotect(byte[] data) =>
        OperatingSystem.IsWindows() ? ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser) : data;

    private static readonly byte[] Entropy = "MeshCoreOne.Windows.v1"u8.ToArray();

    public string? GetPassword(byte[] nodeKey)
    {
        lock (_lock) return _entries.GetValueOrDefault(Convert.ToHexString(nodeKey));
    }

    public void SetPassword(byte[] nodeKey, string password)
    {
        lock (_lock) { _entries[Convert.ToHexString(nodeKey)] = password; Persist(); }
    }

    public void DeletePassword(byte[] nodeKey)
    {
        lock (_lock) { if (_entries.Remove(Convert.ToHexString(nodeKey))) Persist(); }
    }
}

/// <summary>Standard per-user folders.</summary>
public static class AppPaths
{
    public static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeshCore");

    /// <summary>Folder used before the app was renamed from "MeshCore One" to "MeshCore".</summary>
    public static string LegacyRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeshCoreOne");

    /// <summary>
    /// Moves the data of the old "MeshCore One" build to the new folder the first time the renamed app starts.
    /// If the old folder can't be moved (e.g. the old version is still running) it keeps using it.
    /// </summary>
    public static void MigrateLegacyFolder()
    {
        try
        {
            var legacy = LegacyRoot;
            if (Directory.Exists(Root) || !Directory.Exists(legacy)) return;
            try { Directory.Move(legacy, Root); }
            catch { Root = legacy; }
        }
        catch { /* first run or no access: start fresh in Root */ }
    }
    public static string Database => Path.Combine(Root, "meshcoreone.db");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Secrets => Path.Combine(Root, "secrets.bin");
    public static string Logs => Path.Combine(Root, "logs");
    public static string TileCache => Path.Combine(Root, "tiles");
    public static string ImageCache => Path.Combine(Root, "images");
    /// <summary>Chat background images chosen by the user.</summary>
    public static string Backgrounds => Path.Combine(Root, "backgrounds");
}
