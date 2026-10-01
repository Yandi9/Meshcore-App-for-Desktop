using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MC1.Windows.Services;

namespace MC1.Windows.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private bool _loading;

    public SettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        Load();
        Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
        _selectedCategory = Categories[0];
        main.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainWindowViewModel.NavCollapsed)) OnPropertyChanged(nameof(CompactSidebar)); };
        OnStatus(() => Ui(RefreshRadioState));
        OnData(c => { if (c.Kind == DataKind.Radio) Ui(RefreshRadioState); });
        RefreshRadioState();
    }

    // MARK: Categories (one page at a time keeps Settings short)

    public IReadOnlyList<SettingsCategory> Categories { get; } =
    [
        new("general", L.T("General"), "Icon.CogOutline"),
        new("chats", L.T("Chats"), "Icon.ChatOutline"),
        new("looks", L.T("Chat looks"), "Icon.PaletteOutline"),
        new("notifications", L.T("Notifications"), "Icon.BellOutline"),
        new("data", L.T("Backup & storage"), "Icon.DatabaseExport"),
        new("diagnostics", L.T("Diagnostics"), "Icon.Bug"),
        new("about", L.T("About"), "Icon.InformationOutline"),
    ];

    [ObservableProperty] private SettingsCategory _selectedCategory;

    partial void OnSelectedCategoryChanged(SettingsCategory value)
    {
        foreach (var n in (string[])[nameof(IsGeneral), nameof(IsChats), nameof(IsLooks), nameof(IsNotifications), nameof(IsData), nameof(IsDiagnostics), nameof(IsAbout)])
            OnPropertyChanged(n);
        Message = null;
        if (value.Id == "notifications") RefreshWidget();
    }

    public bool IsGeneral => SelectedCategory?.Id == "general";
    public bool IsChats => SelectedCategory?.Id == "chats";
    public bool IsLooks => SelectedCategory?.Id == "looks";
    public bool IsNotifications => SelectedCategory?.Id == "notifications";
    public bool IsData => SelectedCategory?.Id == "data";
    public bool IsDiagnostics => SelectedCategory?.Id == "diagnostics";
    public bool IsAbout => SelectedCategory?.Id == "about";

    /// <summary>Opens Settings on a given page (e.g. "looks").</summary>
    public void ShowCategory(string id)
    {
        if (Categories.FirstOrDefault(c => c.Id == id) is { } c) SelectedCategory = c;
    }

    /// <summary>The navigation rail is shown as a slim icon strip.</summary>
    public bool CompactSidebar
    {
        get => _main.NavCollapsed;
        set => _main.NavCollapsed = value;
    }

    public bool NoCustomizedChats => CustomizedChats.Count == 0;

    /// <summary>"Windows default", then the 12 languages (each in its own name).</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = LanguageOption.All(withWindowsDefault: true);

    private LanguageOption? _selectedLanguage;

    /// <summary>The language picked; changing it switches the whole app right away.</summary>
    public LanguageOption? SelectedLanguage
    {
        get => _selectedLanguage ??= LanguageOptions.FirstOrDefault(o => o.Code == Core.Settings.Current.Language) ?? LanguageOptions[0];
        set
        {
            if (value is null || ReferenceEquals(value, _selectedLanguage)) return;
            _selectedLanguage = value;
            OnPropertyChanged();
            var code = value.Code;
            // After the pick has finished: the switch rebuilds every view, this page included.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AppHost.ChangeLanguage?.Invoke(code));
        }
    }

    /// <summary>The theme values stored in settings (English); <see cref="Themes"/> are the same in the app's language.</summary>
    private static readonly string[] ThemeValues = ["System", "Light", "Dark"];
    private readonly string[] _themeLabels = [L.T("System"), L.T("Light"), L.T("Dark")];

    /// <summary>The theme picker's options (shown text, same order as <see cref="ThemeValues"/>).</summary>
    public IReadOnlyList<string> Themes => _themeLabels;
    public IReadOnlyList<int> AttemptOptions { get; } = [1, 2, 3, 4, 5, 6, 8];
    public string Version { get; }
    public string DataFolder => AppPaths.Root;

    /// <summary>The theme picked, as shown (one of <see cref="Themes"/>); <see cref="ThemeValue"/> is what's stored.</summary>
    [ObservableProperty] private string _theme = "";

    /// <summary>The stored value ("System", "Light" or "Dark") of the theme shown in the picker.</summary>
    private string ThemeValue => ThemeValues[Math.Max(0, Array.IndexOf(_themeLabels, Theme))];

    private string ThemeLabel(string? value) => _themeLabels[Math.Max(0, Array.IndexOf(ThemeValues, value))];

    [ObservableProperty] private bool _compactChatList;
    [ObservableProperty] private bool _showLinkPreviews;
    [ObservableProperty] private bool _showInlineImages;
    [ObservableProperty] private bool _showMapPreviews;
    [ObservableProperty] private bool _showHeardRepeats;
    [ObservableProperty] private bool _sendOnEnter;
    [ObservableProperty] private bool _useTwentyFourHourTime;
    [ObservableProperty] private int _dmMaxAttempts;
    [ObservableProperty] private int _dmFloodAfter;
    [ObservableProperty] private bool _notificationsEnabled;
    [ObservableProperty] private bool _notifyDirectMessages;
    [ObservableProperty] private bool _notifyChannelMessages;
    [ObservableProperty] private bool _notifyRoomMessages;
    [ObservableProperty] private bool _notifyNewContacts;
    [ObservableProperty] private bool _notifyLowBattery;
    [ObservableProperty] private bool _notificationSound;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _useMetricUnits;
    [ObservableProperty] private bool _autoReconnect;
    [ObservableProperty] private bool _connectOnLaunch;
    [ObservableProperty] private string _bluetoothPin = "";
    [ObservableProperty] private bool _rxLogEnabled;
    [ObservableProperty] private int _rxLogMaxEntries;
    [ObservableProperty] private bool _debugLogging;
    [ObservableProperty] private string _tileCacheSize = "";
    [ObservableProperty] private bool _chatThemesEnabled;
    [ObservableProperty] private bool _showUnreadOnTaskbar;
    [ObservableProperty] private ChatAppearance? _defaultAppearance;
    [ObservableProperty] private bool _hasDefaultAppearance;
    [ObservableProperty] private ChatPickItem? _chatToCustomize;
    [ObservableProperty] private string _customizedSummary = "";
    public ObservableCollection<ChatThemeRow> CustomizedChats { get; } = new();
    public ObservableCollection<ChatPickItem> AllChats { get; } = new();
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _startupAnimation;
    [ObservableProperty] private bool _startupSound;
    [ObservableProperty] private bool _isRadioConnected;
    [ObservableProperty] private string? _pendingSummary;
    [ObservableProperty] private bool _canApplyPending;
    [ObservableProperty] private bool _lockScreenStatus;
    [ObservableProperty] private string _widgetDescription = "";
    [ObservableProperty] private string _widgetInstallText = L.T("Install");
    [ObservableProperty] private bool _widgetCanInstall;
    [ObservableProperty] private bool _widgetCanRemove;
    [ObservableProperty] private bool _isWidgetBusy;

    /// <summary>The lock screen widget can be installed from here (Windows, with the widget files next to the app).</summary>
    public bool HasWidget => AppHost.Widget is not null;

    partial void OnLockScreenStatusChanged(bool value)
    {
        if (_loading) return;
        Core.Settings.Update(s => s.LockScreenStatus = value);
        AppHost.LockScreenStatusChanged?.Invoke(value);
    }

    /// <summary>Re-reads whether the widget is installed (asks Windows' package manager, so off the UI thread).</summary>
    private void RefreshWidget()
    {
        if (AppHost.Widget is not { } w) return;
        _ = Task.Run(() => (w.State, w.Description)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            var (state, text) = t.Result;
            Ui(() =>
            {
                WidgetDescription = text;
                WidgetCanInstall = state is WidgetInstallState.NotInstalled or WidgetInstallState.UpdateAvailable;
                WidgetInstallText = state == WidgetInstallState.UpdateAvailable ? L.T("Update") : L.T("Install");
                WidgetCanRemove = state is WidgetInstallState.Installed or WidgetInstallState.UpdateAvailable;
            });
        });
    }

    [RelayCommand]
    private async Task InstallWidget()
    {
        if (AppHost.Widget is not { } w || IsWidgetBusy) return;
        IsWidgetBusy = true;
        try
        {
            var progress = new Progress<string>(m => WidgetDescription = m);
            Message = await Task.Run(() => w.InstallAsync(progress));
        }
        catch (Exception ex)
        {
            Core.Log.Warn("Widget", "Install failed: " + ex);
            Message = L.F("The widget wasn't installed: {0}", ex.Message);
        }
        finally
        {
            IsWidgetBusy = false;
            RefreshWidget();
        }
    }

    [RelayCommand]
    private async Task RemoveWidget()
    {
        if (AppHost.Widget is not { } w || IsWidgetBusy) return;
        if (!await AppHost.Dialogs.Confirm(L.T("Remove the lock screen widget?"), L.T("It disappears from the lock screen and the Widgets board. You can install it again any time."), L.T("Remove"), true)) return;
        IsWidgetBusy = true;
        try { Message = await Task.Run(w.RemoveAsync); }
        catch (Exception ex) { Message = L.F("The widget wasn't removed: {0}", ex.Message); }
        finally
        {
            IsWidgetBusy = false;
            RefreshWidget();
        }
    }

    [RelayCommand]
    private void OpenLockScreenSettings() => AppHost.Dialogs.OpenUrl("ms-settings:lockscreen");

    /// <summary>Backups, restores and exports need the radio disconnected.</summary>
    public bool CanTransfer => !IsRadioConnected;
    public bool HasPendingRadioWrite => PendingSummary is not null;
    public bool CanAutoStart => AppHost.GetAutoStart is not null;

    partial void OnIsRadioConnectedChanged(bool value) => OnPropertyChanged(nameof(CanTransfer));
    partial void OnPendingSummaryChanged(string? value) => OnPropertyChanged(nameof(HasPendingRadioWrite));

    private bool _startWithWindows;

    /// <summary>Windows starts the app at sign-in (the registry is the source of truth).</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;
            _startWithWindows = value;
            AppHost.SetAutoStart?.Invoke(value);
            Core.Settings.Update(s => s.StartWithWindows = value);
            OnPropertyChanged(nameof(StartWithWindows));
        }
    }

    [ObservableProperty] private string? _radioSummary;
    [ObservableProperty] private string _radioSummaryTitle = L.T("Radio");
    public bool HasRadioSummary => RadioSummary is not null;
    partial void OnRadioSummaryChanged(string? value) => OnPropertyChanged(nameof(HasRadioSummary));

    [RelayCommand]
    private void OpenRadioPage() => _main.Navigate(Page.Radio);

    [RelayCommand]
    private void PlayStartupAnimation() => AppHost.PlayStartupAnimation?.Invoke();

    private void RefreshRadioState()
    {
        if (Core.SelfInfo is { } self)
        {
            var repeating = Core.Capabilities?.ClientRepeat ?? false;
            var preset = repeating ? RadioPresets.MatchingRepeat(self.RadioFrequency) : Core.Device.CurrentPreset();
            RadioSummaryTitle = repeating ? L.F("{0} · Repeat mode preset", self.Name)
                : preset is null ? L.F("{0} · Custom preset", self.Name)
                : L.F("{0} · {1} preset", self.Name, preset.Name);
            var settings = RadioPresets.Describe(preset, self.RadioFrequency, self.RadioBandwidth, self.RadioSpreadingFactor, self.RadioCodingRate)
                           + $" · {self.TxPower} dBm";
            RadioSummary = Core.IsConnected ? L.F("On the radio now: {0}", settings) : L.F("Last seen: {0}", settings);
        }
        else RadioSummary = null;
        IsRadioConnected = Core.Status is LinkStatus.Ready or LinkStatus.Syncing or LinkStatus.Connecting or LinkStatus.Reconnecting;
        var pending = Core.PendingConfig.Current;
        PendingSummary = pending is null ? null : L.F("Waiting to write to {0}: {1}.", pending.RadioName, pending.Summary);
        CanApplyPending = pending is not null && Core.IsConnected && Core.RadioId == pending.RadioId;
    }

    private void Load()
    {
        _loading = true;
        var s = Core.Settings.Current;
        Theme = ThemeLabel(s.Theme);
        CompactChatList = s.CompactChatList;
        ShowLinkPreviews = s.ShowLinkPreviews;
        ShowInlineImages = s.ShowInlineImages;
        ShowMapPreviews = s.ShowMapPreviews;
        ShowHeardRepeats = s.ShowHeardRepeats;
        SendOnEnter = s.SendOnEnter;
        UseTwentyFourHourTime = s.UseTwentyFourHourTime;
        DmMaxAttempts = s.DmMaxAttempts;
        DmFloodAfter = s.DmFloodAfter;
        NotificationsEnabled = s.NotificationsEnabled;
        NotifyDirectMessages = s.NotifyDirectMessages;
        NotifyChannelMessages = s.NotifyChannelMessages;
        NotifyRoomMessages = s.NotifyRoomMessages;
        NotifyNewContacts = s.NotifyNewContacts;
        NotifyLowBattery = s.NotifyLowBattery;
        NotificationSound = s.NotificationSound;
        MinimizeToTray = s.MinimizeToTray;
        UseMetricUnits = s.UseMetricUnits;
        AutoReconnect = s.AutoReconnect;
        ConnectOnLaunch = s.ConnectOnLaunch;
        BluetoothPin = s.BluetoothPin;
        RxLogEnabled = s.RxLogEnabled;
        RxLogMaxEntries = s.RxLogMaxEntries;
        DebugLogging = s.DebugLogging;
        ChatThemesEnabled = s.ChatThemesEnabled;
        ShowUnreadOnTaskbar = s.ShowUnreadOnTaskbar;
        StartMinimized = s.StartMinimized;
        StartupAnimation = s.StartupAnimation;
        StartupSound = s.StartupSound;
        LockScreenStatus = s.LockScreenStatus;
        _startWithWindows = AppHost.GetAutoStart?.Invoke() ?? false;
        OnPropertyChanged(nameof(StartWithWindows));
        _loading = false;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(TileCacheSize) or nameof(Message) or nameof(DefaultAppearance) or nameof(HasDefaultAppearance)
            or nameof(ChatToCustomize) or nameof(CustomizedSummary) or nameof(SelectedCategory) or nameof(CompactSidebar) or nameof(NoCustomizedChats)
            or nameof(StartWithWindows) or nameof(CanTransfer) or nameof(PendingSummary) or nameof(HasPendingRadioWrite) or nameof(CanApplyPending)
            or nameof(LockScreenStatus) or nameof(HasWidget) or nameof(RadioSummary) or nameof(RadioSummaryTitle) or nameof(HasRadioSummary)
            || e.PropertyName?.StartsWith("Is") == true || e.PropertyName?.StartsWith("Widget") == true) return;
        if (e.PropertyName == nameof(ChatThemesEnabled)) { ChatThemes.Enabled = ChatThemesEnabled; return; }
        if (e.PropertyName == nameof(ShowUnreadOnTaskbar))
        {
            Core.Settings.Update(s => s.ShowUnreadOnTaskbar = ShowUnreadOnTaskbar);
            AppHost.UnreadChanged?.Invoke(Core.RadioId is null ? 0 : Core.Messages.TotalUnread());
            return;
        }
        Core.Settings.Update(s =>
        {
            s.Theme = ThemeValue;
            s.CompactChatList = CompactChatList;
            s.ShowLinkPreviews = ShowLinkPreviews;
            s.ShowInlineImages = ShowInlineImages;
            s.ShowMapPreviews = ShowMapPreviews;
            s.ShowHeardRepeats = ShowHeardRepeats;
            s.SendOnEnter = SendOnEnter;
            s.UseTwentyFourHourTime = UseTwentyFourHourTime;
            s.DmMaxAttempts = Math.Clamp(DmMaxAttempts, 1, 8);
            s.DmFloodAfter = Math.Clamp(DmFloodAfter, 1, 8);
            s.NotificationsEnabled = NotificationsEnabled;
            s.NotifyDirectMessages = NotifyDirectMessages;
            s.NotifyChannelMessages = NotifyChannelMessages;
            s.NotifyRoomMessages = NotifyRoomMessages;
            s.NotifyNewContacts = NotifyNewContacts;
            s.NotifyLowBattery = NotifyLowBattery;
            s.NotificationSound = NotificationSound;
            s.MinimizeToTray = MinimizeToTray;
            s.UseMetricUnits = UseMetricUnits;
            s.AutoReconnect = AutoReconnect;
            s.ConnectOnLaunch = ConnectOnLaunch;
            s.BluetoothPin = BluetoothPin.Trim();
            s.RxLogEnabled = RxLogEnabled;
            s.RxLogMaxEntries = Math.Clamp(RxLogMaxEntries, 100, 50000);
            s.DebugLogging = DebugLogging;
            s.StartMinimized = StartMinimized;
            s.StartupAnimation = StartupAnimation;
            s.StartupSound = StartupSound;
        });
        if (e.PropertyName == nameof(Theme)) AppHost.ApplyTheme?.Invoke(ThemeValue);
        if (e.PropertyName == nameof(DebugLogging)) Core.Log.DebugEnabled = DebugLogging;
        if (e.PropertyName is nameof(CompactChatList) or nameof(UseTwentyFourHourTime)) _main.Chats.Reload();
    }

    public void OnShown()
    {
        _ = Task.Run(TileService.CacheSizeBytes).ContinueWith(t => Ui(() => TileCacheSize = L.F("Using {0}", Formatters.Bytes(t.Result))));
        RefreshChatThemes();
    }

    private bool _themesHooked;

    private void RefreshChatThemes()
    {
        if (!_themesHooked)
        {
            Action onThemes = () => Ui(RefreshChatThemes);
            ChatThemes.Changed += onThemes;
            Track(() => ChatThemes.Changed -= onThemes);
            _themesHooked = true;
        }
        var names = _main.Chats.AllItems.ToDictionary(i => i.Key, i => i);
        DefaultAppearance = ChatAppearance.From(ChatThemes.GetDefault());
        HasDefaultAppearance = DefaultAppearance is not null;
        CustomizedChats.Clear();
        foreach (var (key, theme) in ChatThemes.Customized().OrderBy(c => names.TryGetValue(c.ConversationKey, out var n) ? n.Title : c.ConversationKey))
        {
            if (!names.TryGetValue(key, out var item)) continue; // chat no longer exists on this radio
            CustomizedChats.Add(new ChatThemeRow(key, item.Title, ChatAppearance.From(theme), Describe(theme)));
        }
        CustomizedSummary = CustomizedChats.Count == 0 ? "" : L.Plural(CustomizedChats.Count, "{0} chat", "{0} chats");
        OnPropertyChanged(nameof(NoCustomizedChats));
        var selected = ChatToCustomize?.Key;
        AllChats.Clear();
        foreach (var i in _main.Chats.AllItems.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)) AllChats.Add(new ChatPickItem(i.Key, i.Title));
        ChatToCustomize = AllChats.FirstOrDefault(c => c.Key == selected);
    }

    private static string Describe(ChatTheme t)
    {
        var parts = new List<string>();
        if (ChatThemes.PresetDisplayName(t.PresetName) is { } p) parts.Add(p);
        parts.Add(t.BackgroundKind switch
        {
            "Color" => L.T("solid background"),
            "Gradient" => L.T("gradient background"),
            "Image" => L.T("picture background"),
            _ => L.T("theme background"),
        });
        var colours = new[] { t.IncomingBubble, t.IncomingText, t.OutgoingBubble, t.OutgoingText }.Count(c => c is not null);
        if (colours > 0) parts.Add(L.Plural(colours, "{0} custom colour", "{0} custom colours"));
        return string.Join(" · ", parts);
    }

    [RelayCommand]
    private async Task EditDefaultAppearance() =>
        await AppHost.Dialogs.ShowDialog(new ChatAppearanceViewModel(null, L.T("Default look for all chats")), L.T("Chat appearance"), 860, 700);

    [RelayCommand]
    private void ResetDefaultAppearance() => ChatThemes.SetDefault(null);

    [RelayCommand]
    private async Task EditChatAppearance(ChatThemeRow row) =>
        await AppHost.Dialogs.ShowDialog(new ChatAppearanceViewModel(row.Key, L.F("Appearance · {0}", row.Name)), L.T("Chat appearance"), 860, 700);

    [RelayCommand]
    private void ResetChatAppearance(ChatThemeRow row) => ChatThemes.Set(row.Key, null);

    [RelayCommand]
    private async Task CustomizeSelectedChat()
    {
        if (ChatToCustomize is not { } c) { Message = L.T("Pick a chat first."); return; }
        await AppHost.Dialogs.ShowDialog(new ChatAppearanceViewModel(c.Key, L.F("Appearance · {0}", c.Name)), L.T("Chat appearance"), 860, 700);
    }

    [RelayCommand]
    private async Task ResetAllChatAppearances()
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Reset all chat looks?"), L.T("Removes the default look and every chat's own background and colours."), L.T("Reset all"), true)) return;
        ChatThemes.ResetAll();
        Message = L.T("All chats use the app theme again.");
    }

    private async Task<bool> RequireDisconnected()
    {
        if (!Core.IsConnected && Core.Status is not (LinkStatus.Connecting or LinkStatus.Syncing or LinkStatus.Reconnecting)) return true;
        await AppHost.Dialogs.ShowError(L.T("Disconnect the radio first"), L.T("Backups, restores and exports are made while the radio is disconnected. Use Disconnect at the bottom right, then try again."));
        return false;
    }

    [RelayCommand]
    private async Task DisconnectRadio()
    {
        if (Core.Status == LinkStatus.Reconnecting) { Core.CancelReconnect(); return; }
        await Guard(() => Core.DisconnectAsync(), L.T("Disconnect"));
    }

    /// <summary>Backs up messages, contacts, channels, saved paths and settings as "MC1 Backup yyyy-MM-dd HHmmss.mc1backup".</summary>
    [RelayCommand]
    private async Task BackupAppData()
    {
        if (!await RequireDisconnected()) return;
        var path = await AppHost.Dialogs.PickSaveFile(L.T("Back up"), Mc1Backup.DefaultFileName(DateTime.Now), Mc1Backup.Extension, L.T("MeshCore backup"));
        if (path is null) return;
        await Guard(async () =>
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
            Mc1Backup.ExportSummary? summary = null;
            var bytes = await Task.Run(() => Mc1Backup.Export(Core, version, out summary));
            await File.WriteAllBytesAsync(path, bytes);
            var text = L.F("Backup saved as {0}: {1}, {2}, {3}, {4} and your settings.", Path.GetFileName(path),
                L.Plural(summary!.Messages, "{0} message", "{0} messages"),
                L.Plural(summary.Contacts, "{0} contact", "{0} contacts"),
                L.Plural(summary.Channels, "{0} channel", "{0} channels"),
                L.Plural(summary.SavedPaths, "{0} saved path", "{0} saved paths"));
            Message = text;
        }, L.T("Backup"));
    }

    [RelayCommand]
    private async Task RestoreAppData()
    {
        if (!await RequireDisconnected()) return;
        var path = await AppHost.Dialogs.PickOpenFile(L.T("Restore a backup"), [Mc1Backup.Extension, "zip"], L.T("MeshCore backup (.mc1backup)"));
        if (path is null) return;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // Backups from earlier versions of this app.
            if (!await AppHost.Dialogs.Confirm(L.T("Restore backup?"), L.T("This replaces all messages, contacts and settings on this PC with the backup."), L.T("Restore"), true)) return;
            await Guard(async () =>
            {
                await Core.Backup.ImportAsync(path);
                Load();
                AppHost.ApplyTheme?.Invoke(ThemeValue);
                Core.LoadOfflineRadio();
                _main.Chats.Reload();
                Message = L.T("Backup restored.");
            }, L.T("Restore"));
            return;
        }
        Mc1Backup backup;
        try { backup = await Task.Run(async () => Mc1Backup.Read(await File.ReadAllBytesAsync(path))); }
        catch (Exception ex) { await AppHost.Dialogs.ShowError(L.T("Couldn't read the file"), ex.Message); return; }
        if (backup.Devices.Count == 0) { await AppHost.Dialogs.ShowError(L.T("Nothing to restore"), L.T("This backup doesn't contain a radio.")); return; }
        await AppHost.Dialogs.ShowDialog(new ConfigTransferViewModel(backup, Path.GetFileName(path)), L.T("Restore backup"), 540, 640);
        Load();
        AppHost.ApplyTheme?.Invoke(ThemeValue);
        _main.Chats.Reload();
        RefreshRadioState();
    }

    [RelayCommand]
    private async Task ApplyPendingNow()
    {
        await Guard(async () =>
        {
            Message = L.T("Writing to the radio…");
            Message = await Core.PendingConfig.ApplyAsync(new Progress<string>(p => Message = L.F("Writing to the radio: {0}…", p)));
        }, L.T("Restore"));
        RefreshRadioState();
    }

    [RelayCommand]
    private async Task DiscardPending()
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Discard?"), L.T("The restored radio settings, channels and contacts won't be written to the radio. Restored contacts and channels the radio doesn't have are removed the next time it syncs."), L.T("Discard"), true)) return;
        await Guard(() => Core.PendingConfig.DiscardAsync(), L.T("Restore"));
        RefreshRadioState();
    }

    [RelayCommand]
    private async Task ExportSettings()
    {
        if (!await RequireDisconnected()) return;
        var path = await AppHost.Dialogs.PickSaveFile(L.T("Export app settings"), "MeshCore-settings.json", "json", L.T("JSON file"));
        if (path is null) return;
        await File.WriteAllTextAsync(path, Core.Settings.Export());
        Message = L.T("Settings exported.");
    }

    [RelayCommand]
    private async Task ShowLog() => await AppHost.Dialogs.ShowDialog(new LogViewerViewModel(), L.T("Diagnostic log"), 900, 640);

    [RelayCommand]
    private void OpenDataFolder() => AppHost.Dialogs.OpenUrl(AppPaths.Root);

    [RelayCommand]
    private async Task ClearTileCache()
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Clear map cache?"), L.T("Deletes all cached and offline map tiles."), L.T("Clear"), true)) return;
        TileService.Instance.ClearCache();
        OnShown();
    }

    [RelayCommand]
    private async Task ClearLinkPreviews()
    {
        try { if (Directory.Exists(AppPaths.ImageCache)) Directory.Delete(AppPaths.ImageCache, true); } catch { /* in use */ }
        Message = L.T("Image cache cleared.");
        await Task.CompletedTask;
    }

    [RelayCommand] private void OpenUrl(string url) => AppHost.Dialogs.OpenUrl(url);

    [RelayCommand]
    private void TestNotification() => Core.Notifier.ShowInfo("MeshCore", L.T("Notifications are working."));
}

public sealed record ChatThemeRow(string Key, string Name, ChatAppearance? Appearance, string Summary);

public sealed record ChatPickItem(string Key, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Restoring a .mc1backup (made by this app or by MeshCore One on a phone) while the radio is disconnected.</summary>
public sealed partial class ConfigTransferViewModel : ViewModelBase
{
    private readonly Mc1Backup _backup;

    public ConfigTransferViewModel(Mc1Backup backup, string? fileName = null)
    {
        _backup = backup;
        Devices = backup.Devices.Select(d => new BackupDeviceItem(d, backup)).ToList();
        var preferred = backup.PickDevice(Core.Radio?.PublicKey);
        _selectedDevice = Devices.FirstOrDefault(d => d.Device == preferred) ?? Devices.FirstOrDefault();
        Summary = (fileName ?? L.T("Backup"))
                  + (backup.ExportDate is { } when ? " · " + L.F("made {0:g}", when.LocalDateTime) : "")
                  + (Devices.Count > 1 ? " · " + L.Plural(Devices.Count, "{0} radio", "{0} radios") : "");
        HasAppSettings = backup.WindowsSettings is not null || backup.UserDefaults is not null;
        // A backup from this app restores all its settings; a phone backup only a few shared ones, so that's opt-in.
        AppSettings = backup.WindowsSettings is not null;
        ApplyDevice();
    }

    public string Summary { get; }
    public IReadOnlyList<BackupDeviceItem> Devices { get; }
    public bool HasSeveralDevices => Devices.Count > 1;
    public IReadOnlyList<RestoreTarget> Targets { get; private set; } = [];
    public Action? Close { get; set; }

    [ObservableProperty] private BackupDeviceItem? _selectedDevice;
    [ObservableProperty] private RestoreTarget? _selectedTarget;
    partial void OnSelectedDeviceChanged(BackupDeviceItem? value) => ApplyDevice();
    partial void OnSelectedTargetChanged(RestoreTarget? value) => ApplyTargetDefaults();

    [ObservableProperty] private string _nameLabel = L.T("Name");
    [ObservableProperty] private string _radioLabel = L.T("Radio settings");
    [ObservableProperty] private string _positionLabel = L.T("Position");
    [ObservableProperty] private string _channelsLabel = L.T("Channels");
    [ObservableProperty] private string _contactsLabel = L.T("Contacts");
    [ObservableProperty] private string _messagesLabel = L.T("Messages");
    [ObservableProperty] private string _pathsLabel = L.T("Saved paths");
    [ObservableProperty] private bool _hasName;
    [ObservableProperty] private bool _hasRadio;
    [ObservableProperty] private bool _hasPosition;
    [ObservableProperty] private bool _hasChannels;
    [ObservableProperty] private bool _hasContacts;
    [ObservableProperty] private bool _hasMessages;
    [ObservableProperty] private bool _hasPaths;
    public bool HasAppSettings { get; }

    [ObservableProperty] private bool _name;
    [ObservableProperty] private bool _radio;
    [ObservableProperty] private bool _position;
    [ObservableProperty] private bool _other;
    [ObservableProperty] private bool _channels;
    [ObservableProperty] private bool _contacts;
    [ObservableProperty] private bool _messages;
    [ObservableProperty] private bool _paths;
    [ObservableProperty] private bool _appSettings;

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _progress;
    [ObservableProperty] private string? _error;

    private void ApplyDevice()
    {
        if (SelectedDevice is not { } item) return;
        var d = item.Device;
        HasName = !string.IsNullOrWhiteSpace(d.NodeName);
        NameLabel = HasName ? L.F("Name ({0})", d.NodeName) : L.T("Name");
        HasRadio = d.FrequencyKhz > 0 && d.SpreadingFactor > 0;
        RadioLabel = HasRadio
            ? L.F("Radio settings ({0:0.###} MHz · {1:0.#} kHz · SF{2} · CR{3} · {4} dBm)", d.FrequencyKhz / 1000.0, d.BandwidthHz / 1000.0, d.SpreadingFactor, d.CodingRate, d.TxPower)
            : L.T("Radio settings");
        HasPosition = d.Latitude != 0 || d.Longitude != 0;
        PositionLabel = HasPosition ? L.F("Position ({0:0.#####}, {1:0.#####})", d.Latitude, d.Longitude) : L.T("Position");
        HasChannels = item.ChannelCount > 0;
        ChannelsLabel = L.F("Channels ({0})", item.ChannelCount);
        HasContacts = item.ContactCount > 0;
        ContactsLabel = L.F("Contacts ({0})", item.ContactCount);
        HasMessages = item.MessageCount > 0;
        MessagesLabel = L.F("Messages ({0})", item.MessageCount);
        HasPaths = item.PathCount > 0;
        PathsLabel = L.F("Saved paths ({0})", item.PathCount);
        Channels = HasChannels;
        Contacts = HasContacts;
        Messages = HasMessages;
        Paths = HasPaths;
        Radio = HasRadio;
        Other = true;

        // Restore into: the radio with the same key, else the one shown now, else keep it as its own radio.
        var radios = Core.Db.GetRadios();
        var targets = radios.Select(r => new RestoreTarget(r.Id, r.Name.Length > 0 ? r.Name : r.Id[..8], r.PublicKey.AsSpan().SequenceEqual(d.PublicKey))).ToList();
        if (!targets.Any(t => t.SameRadio)) targets.Add(new RestoreTarget(null, L.F("{0} (as its own radio)", d.Describe), true));
        Targets = targets;
        OnPropertyChanged(nameof(Targets));
        SelectedTarget = targets.FirstOrDefault(t => t.SameRadio && t.Id is not null)
                         ?? targets.FirstOrDefault(t => t.Id is not null && t.Id == Core.RadioId)
                         ?? targets.FirstOrDefault();
    }

    /// <summary>Same radio: restore its name and position too. Another radio: keep that radio's own.</summary>
    private void ApplyTargetDefaults()
    {
        var same = SelectedTarget?.SameRadio ?? false;
        Name = HasName && same;
        Position = HasPosition && same;
    }

    [RelayCommand]
    private async Task Go()
    {
        Error = null;
        if (SelectedDevice is not { } item) { Error = L.T("This file has no radio in it."); return; }
        if (!(Name || Radio || Position || Other || Channels || Contacts || Messages || Paths || AppSettings)) { Error = L.T("Select at least one item."); return; }
        if (Core.IsConnected) { Error = L.T("Disconnect the radio first."); return; }
        Busy = true;
        Progress = L.T("Restoring…");
        try
        {
            var options = new Mc1Backup.RestoreOptions(Name, Radio, Position, Other, Channels, Contacts, Messages, Paths, AppSettings);
            var targetId = SelectedTarget?.Id;
            var r = await Task.Run(() => _backup.Restore(Core, item.Device, targetId, options));
            var parts = new List<string>();
            if (r.Messages > 0) parts.Add(L.Plural(r.Messages, "{0} message", "{0} messages"));
            if (r.Contacts > 0) parts.Add(L.Plural(r.Contacts, "{0} contact", "{0} contacts"));
            if (r.Channels > 0) parts.Add(L.Plural(r.Channels, "{0} channel", "{0} channels"));
            if (r.SavedPaths > 0) parts.Add(L.Plural(r.SavedPaths, "{0} saved path", "{0} saved paths"));
            if (r.SettingsRestored) parts.Add(L.T("settings"));
            var text = parts.Count == 0 ? L.T("Everything was already here.") : L.F("Restored {0}.", string.Join(", ", parts));
            if (r.RadioWritePending) text += " " + L.F("The radio part is written to {0} when you connect it.", r.RadioName);
            Close?.Invoke();
            AppHost.Main?.ShowToast(L.T("Backup restored"), text);
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    [RelayCommand] private void Cancel() => Close?.Invoke();
}

/// <summary>A radio of this app that a backup can be restored into (Id null: a new one from the backup).</summary>
public sealed record RestoreTarget(string? Id, string Name, bool SameRadio)
{
    public override string ToString() => Name;
}

/// <summary>A radio inside a .mc1backup file.</summary>
public sealed class BackupDeviceItem(Mc1Backup.Device device, Mc1Backup backup)
{
    public Mc1Backup.Device Device { get; } = device;
    public int ChannelCount { get; } = backup.ToNodeConfig(device).Channels?.Count ?? 0;
    public int ContactCount { get; } = backup.ContactsOf(device).Count();
    public int MessageCount { get; } = backup.MessagesOf(device).Count();
    public int RoomMessageCount { get; } = backup.RoomMessagesOf(device).Count();
    public int PathCount { get; } = backup.TracePathsOf(device).Count();
    public override string ToString() =>
        $"{Device.Describe} · {L.Plural(ContactCount, "{0} contact", "{0} contacts")} · {L.Plural(MessageCount, "{0} message", "{0} messages")}";
}

public sealed partial class LogViewerViewModel : ViewModelBase
{
    public LogViewerViewModel()
    {
        Refresh();
    }

    public ObservableCollection<LogEntry> Entries { get; } = new();
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _warningsOnly;

    partial void OnFilterChanged(string value) => Refresh();
    partial void OnWarningsOnlyChanged(bool value) => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        Entries.Clear();
        foreach (var e in Core.Log.Snapshot().Reverse()
                     .Where(e => !WarningsOnly || e.Level >= LogLevel.Warning)
                     .Where(e => Filter.Length == 0 || e.Message.Contains(Filter, StringComparison.OrdinalIgnoreCase) || e.Category.Contains(Filter, StringComparison.OrdinalIgnoreCase))
                     .Take(3000))
            Entries.Add(e);
    }

    [RelayCommand]
    private async Task Export()
    {
        var path = await AppHost.Dialogs.PickSaveFile(L.T("Export log"), $"meshcoreone-log-{DateTime.Now:yyyyMMdd-HHmm}.txt", "txt", L.T("Text file"));
        if (path is null) return;
        await File.WriteAllTextAsync(path, Core.Log.Export());
    }

    [RelayCommand]
    private async Task Copy() => await AppHost.Dialogs.CopyToClipboard(string.Join(Environment.NewLine, Entries.Select(e => e.ToString())));
}

public sealed record SettingsCategory(string Id, string Name, string IconKey);
