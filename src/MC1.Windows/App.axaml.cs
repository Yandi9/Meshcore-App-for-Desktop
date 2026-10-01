using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using MC1.Core.Services;
using MC1.Windows.Services;
using MC1.Windows.ViewModels;
using MC1.Windows.Views;

namespace MC1.Windows;

public partial class App : Application
{
    private MainWindow? _window;
    private MeshApp? _core;
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _core = new MeshApp();
            AppHost.Core = _core;
            _core.Log.DebugEnabled = _core.Settings.Current.DebugLogging;
            _core.Log.PruneOldFiles();
            _core.Log.Info("App", $"MeshCore for Windows {typeof(App).Assembly.GetName().Version} starting ({System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
            AppHost.ApplyTheme = ApplyTheme;
            ApplyTheme(_core.Settings.Current.Theme);
            L.SetLanguage(L.Resolve(_core.Settings.Current.Language));
            _core.Log.Info("App", $"Language: {L.Code} (Windows: {L.SystemUiCulture.Name})");
            AppHost.ChangeLanguage = ChangeLanguage;
            LocalizeTray();

            var dialogs = new DialogService();
            AppHost.Dialogs = dialogs;
            var inApp = new InAppNotifier();
            _core.Notifier = inApp;
#if WINDOWS
            Platform.Windows.WindowsPlatform.Initialize(_core, inApp);
#endif
            // The window first, without its data: with the start-up animation, its dark first frame shows straight
            // away while the pages are built behind it, and the animation starts once they're ready (so it doesn't
            // stutter while MeshCore sets itself up).
            _window = new MainWindow();
            dialogs.Owner = _window;
            desktop.MainWindow = _window;
            AppHost.ActivateWindow = ActivateMainWindow;
            AppHost.PlayStartupAnimation = () => _window?.ShowStartupSplash();
            AppHost.ShowOverlay = (content, width) => _window?.ShowOverlayAsync(content, width) ?? Task.CompletedTask;
            SingleInstance.ActivationRequested += args => Dispatcher.UIThread.Post(() =>
            {
                ActivateMainWindow();
                if (args.FirstOrDefault(a => a.StartsWith("meshcore://", StringComparison.OrdinalIgnoreCase)) is { } url && AppHost.Main is { } main)
                    _ = DeepLinks.HandleAsync(url, main);
            });
            SingleInstance.Listen();
            _window.Closing += OnWindowClosing;
            AppHost.UnreadChanged = UnreadBadge.Update;
            _window.Activated += (_, _) =>
            {
                _core.Messages.AppIsForeground = true;
                AppHost.Main?.OnWindowActivated();
            };
            _window.Deactivated += (_, _) => _core.Messages.AppIsForeground = _window.WindowState != WindowState.Minimized && _window.IsVisible && _window.IsActive;
            desktop.ShutdownRequested += (_, _) => _quitting = true;
            // Started by Windows at sign-in: stay in the notification area unless the user wants the window.
            var autoStarted = desktop.Args?.Contains("--autostart", StringComparer.OrdinalIgnoreCase) == true;
            if (autoStarted && _core.Settings.Current.StartMinimized)
            {
                _core.Messages.AppIsForeground = false;
                _core.Log.Info("App", "Started with Windows (in the notification area)");
            }
            var showWindow = !(autoStarted && _core.Settings.Current.StartMinimized);
            Controls.StartupSplash? splash = null;
            if (showWindow)
            {
                _window.Show();
                // The start-up animation: once per launch (not when the window comes back from the tray). Then the
                // welcome guide / "connect your radio" when this PC hasn't used MeshCore (or a radio) before.
                if (_core.Settings.Current.StartupAnimation && (AppHost.SystemAnimationsEnabled?.Invoke() ?? true))
                    splash = _window.ShowStartupSplash(autoStart: false);
            }
            var link = desktop.Args?.FirstOrDefault(a => a.StartsWith("meshcore://", StringComparison.OrdinalIgnoreCase));

            void Build()
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                var vm = new MainWindowViewModel();
                AppHost.Main = vm;
                _window.DataContext = vm;
                _ = vm.AutoConnectOnLaunch();
                if (link is not null) Dispatcher.UIThread.Post(() => _ = DeepLinks.HandleAsync(link, vm), DispatcherPriority.Background);
                if (splash is not null)
                {
                    splash.Finished += () => _ = AppHost.Main?.StartFirstRunAsync();
                    // Once the pages have been laid out with their data.
                    Dispatcher.UIThread.Post(() =>
                    {
                        _core.Log.Info("App", $"Ready in {started.ElapsedMilliseconds} ms; start-up animation starting");
                        splash.Start();
                    }, DispatcherPriority.Background);
                }
                else if (showWindow) Dispatcher.UIThread.Post(() => _ = AppHost.Main?.StartFirstRunAsync(), DispatcherPriority.Background);
            }

            // With the animation: after its first (dark) frame is on screen.
            if (splash is not null) _window.RequestAnimationFrame(_ => Dispatcher.UIThread.Post(Build, DispatcherPriority.Background));
            else Build();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Switches the language (a code, or null for Windows' language) and rebuilds the whole UI in it: new view-models
    /// and views, on the same page; the radio connection and everything in the core carry on untouched.
    /// </summary>
    private void ChangeLanguage(string? setting)
    {
        if (_window?.SwitchLanguage(setting) == true) LocalizeTray();
    }

    private readonly Dictionary<NativeMenuItem, string> _trayEnglish = [];

    /// <summary>The tray icon's menu in the app's language (it's made once, with the English in App.axaml).</summary>
    private void LocalizeTray()
    {
        foreach (var icon in TrayIcon.GetIcons(this) ?? [])
        {
            if (icon.Menu is null) continue;
            foreach (var item in icon.Menu.Items.OfType<NativeMenuItem>())
            {
                if (!_trayEnglish.TryGetValue(item, out var english)) _trayEnglish[item] = english = item.Header ?? "";
                item.Header = L.T(english);
            }
        }
    }

    private void ApplyTheme(string theme)
    {
        RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting || _core is null) return;
        if (_core.Settings.Current.MinimizeToTray)
        {
            e.Cancel = true;
            _window?.Hide();
            _core.Messages.AppIsForeground = false;
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _core.Notifier.ShowInfo(L.T("MeshCore is still running"), L.T("It stays connected in the notification area. Right-click the tray icon to quit."));
            }
            return;
        }
        e.Cancel = true;
        _ = QuitAsync();
    }

    private bool _trayHintShown;

    public void ActivateMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        if (_core is not null) _core.Messages.AppIsForeground = true;
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        try
        {
            if (_core is not null)
            {
                var dispose = _core.DisposeAsync().AsTask();
                await Task.WhenAny(dispose, Task.Delay(3000));
            }
        }
        catch { /* shutting down anyway */ }
#if WINDOWS
        Platform.Windows.WindowsPlatform.Shutdown();
#endif
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs e) => ActivateMainWindow();
    private void TrayOpen_OnClick(object? sender, EventArgs e) => ActivateMainWindow();
    private void TrayQuit_OnClick(object? sender, EventArgs e) => _ = QuitAsync();
}
