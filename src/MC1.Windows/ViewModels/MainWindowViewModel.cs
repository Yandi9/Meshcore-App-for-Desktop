using Avalonia.Threading;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Services;
using MC1.Windows.Services;

namespace MC1.Windows.ViewModels;

public enum Page { Chats, Contacts, Map, Tools, Radio, Settings }

public sealed partial class NavItem(Page page, string label, string iconKey) : ObservableObject
{
    public Page Page { get; } = page;
    public string Label { get; } = label;
    public string IconKey { get; } = iconKey;
    [ObservableProperty] private int _badge;
    [ObservableProperty] private bool _alert;
}

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly Debouncer _statusRefresh;

    public MainWindowViewModel()
    {
        Chats = new ChatsViewModel(this);
        Contacts = new ContactsViewModel(this);
        Map = new MapViewModel(this);
        Tools = new ToolsViewModel(this);
        Radio = new RadioViewModel(this);
        Settings = new SettingsViewModel(this);
        NavItems =
        [
            new(Page.Chats, L.T("Chats"), "Icon.ChatOutline"),
            new(Page.Contacts, L.T("Contacts"), "Icon.AccountMultipleOutline"),
            new(Page.Map, L.T("Map"), "Icon.MapOutline"),
            new(Page.Tools, L.T("Tools"), "Icon.ToolboxOutline"),
            new(Page.Radio, L.T("Radio"), "Icon.RadioTower"),
            new(Page.Settings, L.T("Settings"), "Icon.CogOutline"),
        ];
        _selectedNav = NavItems[0];
        _navCollapsed = Core.Settings.Current.NavCollapsed;
        _currentPage = Chats;
        _statusRefresh = new Debouncer(TimeSpan.FromMilliseconds(80), RefreshStatus);
        OnStatus(_statusRefresh.Trigger);
        OnData(c => { if (c.Kind is DataKind.Radio or DataKind.Conversations) _statusRefresh.Trigger(); });
        RefreshStatus();
    }

    public ChatsViewModel Chats { get; }
    public ContactsViewModel Contacts { get; }
    public MapViewModel Map { get; }
    public ToolsViewModel Tools { get; }
    public RadioViewModel Radio { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty] private NavItem _selectedNav;

    /// <summary>The navigation rail is minimised to an icon strip.</summary>
    [ObservableProperty] private bool _navCollapsed;

    public string NavToggleIcon => NavCollapsed ? "Icon.Forwardburger" : "Icon.Backburger";
    public string NavToggleTip => NavCollapsed ? L.T("Expand the sidebar (Ctrl+B)") : L.T("Minimise the sidebar (Ctrl+B)");

    partial void OnNavCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(NavToggleIcon));
        OnPropertyChanged(nameof(NavToggleTip));
        if (Core.Settings.Current.NavCollapsed != value) Core.Settings.Update(s => s.NavCollapsed = value);
    }

    [RelayCommand]
    public void ToggleNav() => NavCollapsed = !NavCollapsed;
    [ObservableProperty] private ViewModelBase _currentPage;

    [ObservableProperty] private string _radioName = L.T("No radio");
    [ObservableProperty] private string _statusText = L.T("Not connected");
    [ObservableProperty] private string _statusIcon = "Icon.LanDisconnect";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private double _syncProgress;
    [ObservableProperty] private bool _syncIndeterminate;
    [ObservableProperty] private string? _batteryText;
    [ObservableProperty] private string _batteryIcon = "Icon.BatteryOutline";
    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private bool _contactsFull;
    [ObservableProperty] private int _unreadTotal;
    [ObservableProperty] private string _connectButtonText = L.T("Connect");
    [ObservableProperty] private bool _showReconnecting;

    public ObservableCollection<ToastItem> Toasts { get; } = new();

    partial void OnSelectedNavChanged(NavItem value)
    {
        CurrentPage = value.Page switch
        {
            Page.Chats => Chats,
            Page.Contacts => Contacts,
            Page.Map => Map,
            Page.Tools => Tools,
            Page.Radio => Radio,
            _ => Settings,
        };
        Chats.SetVisible(value.Page == Page.Chats);
        if (value.Page == Page.Map) Map.OnShown();
        if (value.Page == Page.Contacts) Contacts.Reload();
        if (value.Page == Page.Radio) Radio.OnShown();
        if (value.Page == Page.Settings) Settings.OnShown();
        if (value.Page == Page.Tools && Tools.Current is ToolPage tp) tp.OnShown();
    }

    public void Navigate(Page page) => SelectedNav = NavItems.First(n => n.Page == page);

    partial void OnUnreadTotalChanged(int value) => AppHost.UnreadChanged?.Invoke(value);

    /// <summary>Called when the window comes back to the foreground: the chat on screen counts as read.</summary>
    public void OnWindowActivated()
    {
        if (SelectedNav.Page == Page.Chats) Chats.SetVisible(true);
    }

    /// <summary>After the PC is unlocked: if MeshCore's window is the active one, its open chat is read now.</summary>
    public void OnWindowActivatedIfActive()
    {
        if (Core.Messages.AppIsForeground) OnWindowActivated();
    }

    private void RefreshStatus()
    {
        var c = Core;
        MaybeOfferRadioSetup();
        IsConnected = c.IsConnected;
        RadioName = c.SelfInfo?.Name is { Length: > 0 } n ? n : c.Radio?.Name is { Length: > 0 } rn ? L.F("{0} (offline)", rn) : L.T("No radio");
        ShowReconnecting = c.Status == LinkStatus.Reconnecting;
        (StatusText, StatusIcon) = c.Status switch
        {
            LinkStatus.Ready => (c.CurrentTarget?.Describe() ?? L.T("Connected"), c.CurrentTarget?.Kind switch
            {
                ConnectionKind.Bluetooth => "Icon.Bluetooth",
                ConnectionKind.Tcp => "Icon.Wifi",
                ConnectionKind.Serial => "Icon.Usb",
                _ => "Icon.CellphoneLink",
            }),
            LinkStatus.Connecting => (c.StatusDetail is null ? L.T("Connecting…") : L.F("Connecting… {0}", c.StatusDetail), "Icon.Sync"),
            LinkStatus.Syncing => (SyncingText(c.Sync?.Phase), "Icon.Sync"),
            LinkStatus.Reconnecting => (c.StatusDetail ?? L.T("Reconnecting…"), "Icon.SyncAlert"),
            LinkStatus.Failed => (L.F("Connection failed: {0}", c.StatusDetail ?? ""), "Icon.AlertCircleOutline"),
            _ => (L.T("Not connected"), "Icon.LanDisconnect"),
        };
        IsBusy = c.Status is LinkStatus.Connecting or LinkStatus.Syncing;
        IsSyncing = c.Sync is not null;
        SyncIndeterminate = c.Sync is not { Total: > 0 };
        SyncProgress = c.Sync is { Total: > 0 } s ? 100.0 * s.Current / s.Total : 0;
        ConnectButtonText = c.Status is LinkStatus.Ready or LinkStatus.Syncing or LinkStatus.Connecting or LinkStatus.Reconnecting ? L.T("Disconnect") : L.T("Connect");
        var pct = c.BatteryPercent;
        HasBattery = pct is not null && c.IsConnected;
        if (pct is { } p && c.Battery is { } b)
        {
            BatteryText = $"{p}% · {b.Level / 1000.0:0.00} V";
            BatteryIcon = p switch { >= 88 => "Icon.Battery", >= 63 => "Icon.Battery70", >= 38 => "Icon.Battery50", >= 13 => "Icon.Battery20", _ => "Icon.BatteryAlert" };
        }
        ContactsFull = c.ContactsFull;
        UnreadTotal = c.RadioId is null ? 0 : c.Messages.TotalUnread();
        LiveStatusHub.Publish(new LiveStatus(UnreadTotal, 0, c.IsConnected ? pct : null, c.IsConnected && c.Battery is { } bat ? bat.Level / 1000.0 : null,
            c.SelfInfo?.Name ?? c.Radio?.Name ?? "", c.IsConnected, c.Status switch
            {
                LinkStatus.Ready => L.T("Connected"),
                LinkStatus.Syncing or LinkStatus.Connecting => L.T("Connecting"),
                LinkStatus.Reconnecting => L.T("Reconnecting"),
                _ => L.T("Not connected"),
            }));
        // A backup restored while disconnected: offer to write its radio part now that the radio is here.
        if (c.Status != LinkStatus.Ready) _pendingOffered = false;
        else if (!_pendingOffered && c.PendingConfig.IsFor(c.RadioId))
        {
            _pendingOffered = true;
            Dispatcher.UIThread.Post(() => _ = OfferPendingWriteAsync(), DispatcherPriority.Background);
        }
        NavItems[0].Badge = UnreadTotal;
        NavItems[4].Alert = c.Status is LinkStatus.Failed or LinkStatus.Reconnecting || ContactsFull;
    }

    /// <summary>"Syncing contacts…": the core's phase names ("Contacts", "Channels", "Messages") stay English data.</summary>
    private static string SyncingText(string? phase) => phase switch
    {
        "Contacts" => L.T("Syncing contacts…"),
        "Channels" => L.T("Syncing channels…"),
        "Messages" => L.T("Syncing messages…"),
        null or "" => L.T("Syncing…"),
        _ => L.F("Syncing {0}…", phase.ToLowerInvariant()),
    };

    private bool _pendingOffered;

    private async Task OfferPendingWriteAsync()
    {
        var c = Core;
        if (c.PendingConfig.Current is not { } p || !c.PendingConfig.IsFor(c.RadioId)) return;
        var ok = await AppHost.Dialogs.Confirm(L.T("Finish restoring your backup?"),
            L.F("These are waiting to be written to {0}: {1}.\n\nWrite them to the radio now? (Not now keeps them for later — Settings ▸ Backup & storage.)", p.RadioName, p.Summary),
            L.T("Write to radio"));
        if (!ok) return;
        ShowToast(L.T("Restoring"), L.T("Writing the backup to the radio…"));
        try
        {
            var result = await c.PendingConfig.ApplyAsync();
            ShowToast(L.T("Backup restored"), result);
        }
        catch (Exception ex) { await AppHost.Dialogs.ShowError(L.T("Couldn't write to the radio"), ex.Message); }
    }

    [RelayCommand]
    private async Task ConnectOrDisconnect()
    {
        if (Core.Status is LinkStatus.Ready or LinkStatus.Syncing or LinkStatus.Connecting)
        {
            await Guard(() => Core.DisconnectAsync(), L.T("Disconnect"));
            return;
        }
        if (Core.Status == LinkStatus.Reconnecting)
        {
            Core.CancelReconnect();
            return;
        }
        await ShowConnectDialog();
    }

    [RelayCommand]
    public async Task ShowConnectDialog()
    {
        var vm = new ConnectViewModel();
        await AppHost.Dialogs.ShowDialog(vm, L.T("Connect to a radio"), 560, 620);
    }

    [RelayCommand]
    private void OpenRadioPage() => Navigate(Page.Radio);

    // MARK: First run

    // Per launch, not per view-model: the view-models are re-created when the language changes mid-guide.
    private static bool _radioSetupPending;
    private static bool _firstRunStarted;

    /// <summary>
    /// After the start-up animation: the welcome guide the very first time MeshCore runs on this PC, then (while no radio
    /// has ever been connected) the prompt to connect one. The first radio to connect gets "Set up your radio".
    /// </summary>
    public async Task StartFirstRunAsync()
    {
        if (_firstRunStarted || AppHost.ShowOverlay is not { } show) return;
        _firstRunStarted = true;
        var store = Core.Settings;
        var showGuide = store.IsFirstRun ? store.Current.WelcomeGuideShown != true : store.Current.WelcomeGuideShown == false;
        var knowsRadio = Core.Db.GetRadios().Count > 0 || Core.IsConnected;
        _radioSetupPending = !knowsRadio && !store.Current.RadioSetupShown;
        if (!showGuide && knowsRadio) return;
        if (showGuide) store.Update(s => s.WelcomeGuideShown ??= false); // seen from now on only once it's finished

        var guide = new WelcomeGuideViewModel(includeConnect: !knowsRadio, connectOnly: !showGuide);
        await show(guide, 620);
        if (showGuide) store.Update(s => s.WelcomeGuideShown = true);
        if (guide.Result == GuideResult.Connect && !Core.IsConnected) await (AppHost.Main ?? this).ShowConnectDialog();
    }

    /// <summary>The first radio ever connected on this PC: offer to name it and pick its preset (once).</summary>
    private void MaybeOfferRadioSetup()
    {
        if (!_radioSetupPending || Core.Status != LinkStatus.Ready || Core.SelfInfo is null || AppHost.ShowOverlay is not { } show) return;
        _radioSetupPending = false;
        Core.Settings.Update(s => s.RadioSetupShown = true);
        Ui(() => _ = show(new RadioSetupViewModel(), 580));
    }

    public void ShowToast(string title, string body, Action? onClick = null)
    {
        Ui(() =>
        {
            var t = new ToastItem(title, body, onClick);
            Toasts.Add(t);
            _ = Task.Delay(5000).ContinueWith(_ => Ui(() => Toasts.Remove(t)));
        });
    }

    /// <summary>Handles a notification activation (toast click / quick reply).</summary>
    public void OpenConversation(string key)
    {
        Ui(() =>
        {
            Navigate(Page.Chats);
            Chats.OpenByKey(key);
            AppHost.ActivateWindow?.Invoke();
        });
    }

    public async Task AutoConnectOnLaunch()
    {
        var s = Core.Settings.Current;
        Core.LoadOfflineRadio();
        Chats.Reload();
        // Reopen the chat that was on screen last time; messages that arrived since get a "New messages" line.
        if (s.LastConversation?.Split('|', 2) is [var rid, var key] && rid == Core.RadioId && SelectedNav.Page == Page.Chats)
            Chats.OpenByKey(key);
        if (s.ConnectOnLaunch && s.LastConnection is { } last)
        {
            try { await Core.ConnectAsync(last); }
            catch (Exception ex)
            {
                Core.Log.Warn("Connection", "Auto-connect failed: " + ex.Message);
                if (s.AutoReconnect && last.Kind != ConnectionKind.Simulator) { /* user can retry from the status bar */ }
            }
        }
    }
}

public sealed record ToastItem(string Title, string Body, Action? OnClick)
{
    public void Activate() => OnClick?.Invoke();
}
