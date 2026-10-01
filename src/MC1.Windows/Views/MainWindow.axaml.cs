using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnGlobalKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private readonly List<ContentControl> _overlayHosts = [];

    /// <summary>
    /// Switches the language (a code, or null for Windows' language) and rebuilds the whole UI in it: new view-models
    /// and views, on the same page; the radio connection and everything in the core carry on untouched. False when
    /// the language stays the same (the choice is still saved).
    /// </summary>
    public bool SwitchLanguage(string? setting)
    {
        var core = AppHost.Core;
        core.Settings.Update(s => s.Language = string.IsNullOrEmpty(setting) ? null : setting);
        var code = L.Resolve(setting);
        if (code == L.Code) return false;
        L.SetLanguage(code);
        core.Log.Info("App", $"Language changed to {code}");
        var page = AppHost.Main?.SelectedNav?.Page ?? Page.Chats;
        AppHost.Main?.Chats.Active?.Deactivate(); // saves the draft; the new chat list reopens it
        ViewModelBase.DetachAll();
        var vm = new MainWindowViewModel();
        AppHost.Main = vm;
        vm.Navigate(page);
        RebuildShell(vm);
        AppHost.UnreadChanged?.Invoke(vm.Chats.TotalUnread);
        return true;
    }

    /// <summary>
    /// Rebuilds everything shown (after the language changed): a new shell for the new view-model, and the open
    /// overlays (the welcome guide) redrawn from their view-models, which carry on where they were.
    /// </summary>
    public void RebuildShell(MainWindowViewModel vm)
    {
        if (Content is not Panel root) return;
        var old = root.Children.OfType<ShellView>().FirstOrDefault();
        var index = old is null ? 0 : root.Children.IndexOf(old);
        if (old is not null) root.Children.Remove(old);
        DataContext = vm;
        root.Children.Insert(index, new ShellView());
        foreach (var host in _overlayHosts)
        {
            var content = host.Content;
            host.Content = null;
            host.Content = content;
        }
    }

    /// <summary>
    /// Shows a view-model as a card over the window (dimmed behind), until it calls its <c>Close</c> action.
    /// Used for the welcome guide and the radio set-up.
    /// </summary>
    public Task ShowOverlayAsync(object viewModel, double width = 620)
    {
        var done = new TaskCompletionSource();
        if (Content is not Panel root) { done.SetResult(); return done.Task; }
        var host = new ContentControl { Content = viewModel };
        _overlayHosts.Add(host);
        var card = new Border
        {
            Width = width,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(16),
            Child = new ScrollViewer { Content = host },
            Opacity = 0,
            RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(18px)"),
            Transitions =
            [
                new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) },
                new Avalonia.Animation.TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(260),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
            ],
        };
        card.Classes.Add("overlaycard");
        var backdrop = new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(150, 6, 10, 20)),
            ZIndex = 9_000,
            Child = card,
        };
        if (root is Grid grid)
        {
            Grid.SetRowSpan(backdrop, Math.Max(1, grid.RowDefinitions.Count));
            Grid.SetColumnSpan(backdrop, Math.Max(1, grid.ColumnDefinitions.Count));
        }
        var closeProp = viewModel.GetType().GetProperty("Close");
        if (closeProp?.PropertyType == typeof(Action) && closeProp.CanWrite)
            closeProp.SetValue(viewModel, new Action(() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                root.Children.Remove(backdrop);
                _overlayHosts.Remove(host);
                done.TrySetResult();
            })));
        root.Children.Add(backdrop);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            card.Opacity = 1;
            card.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(0px)");
        }, Avalonia.Threading.DispatcherPriority.Loaded);
        return done.Task;
    }

    /// <summary>
    /// Plays the start-up animation over the window, then removes it; returns it (null if it isn't shown). With
    /// <paramref name="autoStart"/> false it shows its dark first frame and waits for <c>Start()</c> (MeshCore builds
    /// its pages behind it first). Refreshes wait while it plays (see <see cref="Debouncer.Hold"/>).
    /// </summary>
    public Controls.StartupSplash? ShowStartupSplash(bool autoStart = true)
    {
        if (Content is not Panel root || root.Children.OfType<Controls.StartupSplash>().Any()) return null;
        var splash = new Controls.StartupSplash { Sound = AppHost.Core?.Settings.Current.StartupSound ?? false };
        if (root is Grid grid)
        {
            Grid.SetRowSpan(splash, Math.Max(1, grid.RowDefinitions.Count));
            Grid.SetColumnSpan(splash, Math.Max(1, grid.ColumnDefinitions.Count));
        }
        splash.ZIndex = 10_000;
        Debouncer.Hold();
        var released = false;
        void Release()
        {
            if (released) return;
            released = true;
            Debouncer.Release();
        }
        splash.Finished += () =>
        {
            root.Children.Remove(splash);
            Release();
        };
        // Never hold refreshes for long, whatever happens to the animation.
        Avalonia.Threading.DispatcherTimer.RunOnce(Release, TimeSpan.FromMilliseconds(Controls.StartupSplash.TotalDuration + 6000));
        root.Children.Add(splash);
        if (autoStart) Avalonia.Threading.Dispatcher.UIThread.Post(splash.Start, Avalonia.Threading.DispatcherPriority.Loaded);
        return splash;
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        Page? page = e.Key switch
        {
            Key.D1 => Page.Chats,
            Key.D2 => Page.Contacts,
            Key.D3 => Page.Map,
            Key.D4 => Page.Tools,
            Key.D5 => Page.Radio,
            Key.D6 => Page.Settings,
            _ => null,
        };
        if (page is { } p) { vm.Navigate(p); e.Handled = true; return; }
        if (e.Key == Key.B) { vm.ToggleNav(); e.Handled = true; return; }
        if (e.Key == Key.N) { vm.Navigate(Page.Chats); vm.Chats.NewChatCommand.Execute(null); e.Handled = true; }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        var text = e.Data.GetText();
#pragma warning restore CS0618
        if (Vm is { } vm && text?.Trim() is { } t && t.StartsWith("meshcore://", StringComparison.OrdinalIgnoreCase))
            _ = DeepLinks.HandleAsync(t, vm);
    }
}
