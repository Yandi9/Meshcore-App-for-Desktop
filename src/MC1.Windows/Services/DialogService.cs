using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MC1.Core.Services;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Services;

/// <summary>Modal dialogs, file pickers and clipboard access on top of Avalonia.</summary>
public sealed class DialogService : IDialogService
{
    public Window? Owner { get; set; }

    private Window? ActiveOwner =>
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?
            .Windows.LastOrDefault(w => w.IsActive && w.IsVisible) ?? Owner;

    private static IBrush Res(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, Avalonia.Application.Current.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    private static Task<T> OnUi<T>(Func<Task<T>> f) => Dispatcher.UIThread.CheckAccess() ? f() : Dispatcher.UIThread.InvokeAsync(f);

    private Window MakeWindow(string title, double width)
    {
        return new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Res("App.Surface"),
            Icon = Owner?.Icon,
        };
    }

    private static Button MakeButton(string text, bool accent, bool destructive = false)
    {
        var b = new Button { Content = text, MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (accent) b.Classes.Add("accent");
        if (destructive) b.Classes.Add("danger");
        return b;
    }

    private async Task<string?> ShowMessage(string title, string message, string? input, string? watermark, bool password, string okText, string? cancelText, bool destructive, bool isError)
    {
        var owner = ActiveOwner;
        var win = MakeWindow(title, 440);
        string? result = null;
        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 14 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (isError && Avalonia.Application.Current!.TryGetResource("Icon.AlertCircleOutline", null, out var g) && g is Geometry geo)
            header.Children.Add(new PathIcon { Data = geo, Width = 22, Height = 22, Foreground = Res("App.Danger") });
        header.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360 });
        stack.Children.Add(header);
        if (!string.IsNullOrEmpty(message))
            stack.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Res("App.TextSecondary") });
        TextBox? box = null;
        if (input is not null)
        {
            box = new TextBox { Text = input, Watermark = watermark, AcceptsReturn = false };
            if (password) box.PasswordChar = '•';
            stack.Children.Add(box);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        if (cancelText is not null)
        {
            var cancel = MakeButton(cancelText, false);
            cancel.IsCancel = true;
            cancel.Click += (_, _) => win.Close();
            buttons.Children.Add(cancel);
        }
        var ok = MakeButton(okText, !destructive, destructive);
        ok.IsDefault = true;
        ok.Click += (_, _) => { result = box?.Text ?? "ok"; win.Close(); };
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);
        win.Content = stack;
        win.AddHandler(InputElement.KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; win.Close(); } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        if (box is not null) win.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        else win.Opened += (_, _) => ok.Focus();
        if (owner is not null && owner.IsVisible) await win.ShowDialog(owner);
        else
        {
            var tcs = new TaskCompletionSource();
            win.Closed += (_, _) => tcs.TrySetResult();
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Show();
            await tcs.Task;
        }
        return result;
    }

    public Task ShowError(string title, string message) => OnUi(() => ShowMessage(title, message, null, null, false, L.T("OK"), null, false, true));
    public Task ShowInfo(string title, string message) => OnUi(() => ShowMessage(title, message, null, null, false, L.T("OK"), null, false, false));

    /// <summary>Answers every confirmation with yes (used by the screenshot tool, which has nobody to click).</summary>
    public Func<string, bool>? AutoAnswer { get; set; }

    public async Task<bool> Confirm(string title, string message, string? confirmText = null, bool destructive = false) =>
        AutoAnswer is { } auto ? auto(title) :
        await OnUi(() => ShowMessage(title, message, null, null, false, confirmText ?? L.T("OK"), L.T("Cancel"), destructive, false)) is not null;

    public Task<string?> Prompt(string title, string message, string initial = "", string? watermark = null, bool password = false) =>
        OnUi(() => ShowMessage(title, message, initial, watermark, password, L.T("OK"), L.T("Cancel"), false, false));

    public Task<string?> PickSaveFile(string title, string suggestedName, string extension, string description, string? startFolder = null) => OnUi(async () =>
    {
        var top = ActiveOwner;
        if (top is null) return null;
        IStorageFolder? start = null;
        if (startFolder is not null && Directory.Exists(startFolder))
        {
            try { start = await top.StorageProvider.TryGetFolderFromPathAsync(startFolder); } catch { /* use the default */ }
        }
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedStartLocation = start,
            Title = title,
            SuggestedFileName = Sanitize(suggestedName),
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(description) { Patterns = [$"*.{extension}"] }],
        });
        return file?.TryGetLocalPath();
    });

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    public Task<string?> PickOpenFile(string title, string[] extensions, string description) => OnUi(async () =>
    {
        var top = ActiveOwner;
        if (top is null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            // Avalonia's own "All" filter isn't translated, so it's rebuilt here.
            FileTypeFilter = [new FilePickerFileType(description) { Patterns = extensions.Select(e => $"*.{e}").ToList() },
                new FilePickerFileType(L.T("All files")) { Patterns = ["*.*"], MimeTypes = ["*/*"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    });

    public Task CopyToClipboard(string text) => OnUi<bool>(async () =>
    {
        var clip = ActiveOwner?.Clipboard;
        if (clip is not null) await clip.SetTextAsync(text);
        return true;
    });

    public Task<string?> ReadClipboard() => OnUi(async () =>
    {
        var clip = ActiveOwner?.Clipboard;
        return clip is null ? null : await ClipboardExtensions.TryGetTextAsync(clip);
    });

    public Task ShowDialog(object viewModel, string title, double width = 520, double height = 600) => OnUi<bool>(async () =>
    {
        var owner = ActiveOwner;
        var win = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = Math.Min(width, 380),
            MinHeight = Math.Min(height, 300),
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Res("App.Background"),
            Icon = Owner?.Icon,
            Content = new ContentControl { Content = viewModel },
        };
        var closeProp = viewModel.GetType().GetProperty("Close");
        if (closeProp?.PropertyType == typeof(Action) && closeProp.CanWrite) closeProp.SetValue(viewModel, new Action(() => Dispatcher.UIThread.Post(win.Close)));
        win.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; win.Close(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        if (owner is not null && owner.IsVisible) await win.ShowDialog(owner);
        else
        {
            var tcs = new TaskCompletionSource();
            win.Closed += (_, _) => tcs.TrySetResult();
            win.Show();
            await tcs.Task;
        }
        return true;
    });

    public void OpenUrl(string url)
    {
        try
        {
            if (Directory.Exists(url))
            {
                Process.Start(new ProcessStartInfo { FileName = OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open", Arguments = $"\"{url}\"", UseShellExecute = true });
                return;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto" or "ms-settings")) return;
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception ex) { AppHost.Core?.Log.Warn("UI", "Couldn't open " + url + ": " + ex.Message); }
    }
}

/// <summary>Shows notifications as in-app toasts (used when Windows toasts are unavailable, and for info banners).</summary>
public sealed class InAppNotifier : INotifier
{
    public INotifier? System { get; set; }

    private static bool WindowActive =>
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow is { IsActive: true, IsVisible: true };

    public void ShowMessage(string title, string body, string conversationKey, bool allowReply)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (WindowActive || System is null)
                AppHost.Main?.ShowToast(title, body, () => AppHost.Main?.OpenConversation(conversationKey));
            else System.ShowMessage(title, body, conversationKey, allowReply);
        });
    }

    public void ShowInfo(string title, string body, string? actionKey = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (WindowActive || System is null)
                AppHost.Main?.ShowToast(title, body, actionKey == "discover" ? () => { AppHost.Main?.Navigate(Page.Contacts); AppHost.Main!.Contacts.ShowDiscoverCommand.Execute(null); } : null);
            else System.ShowInfo(title, body, actionKey);
        });
    }

    // The unread badge (title, tray, taskbar) is driven by UnreadBadge from the main view model.
    public void UpdateBadge(int unread) => System?.UpdateBadge(unread);
}
