using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MC1.Windows.Services;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Views;

/// <summary>
/// A small window showing meshpic.org. After a picture is uploaded, its share link is picked up (from the page,
/// a "copy" click, or the clipboard) and handed back so it can be inserted into the message at the caret.
/// Pictures uploaded earlier that the site still lists are never inserted on their own: they're offered as choices,
/// and only used when picked (or when their link is copied or clicked).
/// </summary>
public sealed class MeshPicWindow : Window
{
    // Where a candidate link was seen; higher wins when several turn up together.
    private const int FromCopy = 4, FromInput = 3, FromNavigation = 3, FromText = 2, FromHref = 1;

    private readonly TextBlock _status;
    private readonly Button _insert;
    private readonly Panel _content;
    /// <summary>Picture links on the page before anything was uploaded in this window: earlier uploads.</summary>
    private readonly List<(string Link, int Priority)> _earlier = new();
    private readonly HashSet<string> _earlierSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Priority, int Order)> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly WrapPanel _earlierButtons = new();
    private readonly Border _earlierBar;
    private bool _uploadStarted;
    private bool _userClicked;
    private readonly DispatcherTimer _settle;
    private readonly DispatcherTimer _clipboardPoll;
    private readonly TextBox _manual;
    private bool _pageLoaded;
    private string? _clipboardAtStart;
    private bool _clipboardPrimed;
    private string? _link;
    private bool _closing;

    public MeshPicWindow()
    {
        Title = L.T("Insert a picture · MeshPic");
        Width = 560;
        Height = 760;
        MinWidth = 380;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = this.GetResourceObservable("App.Background").ToBinding();

        var info = new TextBlock
        {
            Text = L.T("Upload a picture. As soon as MeshPic makes its link, it's added to your message where the cursor was."),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
        };
        var browser = new Button { Content = L.T("Open in browser"), Classes = { "link" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        browser.Click += (_, _) => AppHost.Dialogs.OpenUrl(MeshPicLinks.SiteUrl);
        var top = new Border
        {
            Padding = new Thickness(14, 10),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { info, Col(browser, 1) } },
        };
        top[!Border.BackgroundProperty] = this.GetResourceObservable("App.Surface").ToBinding();

        _status = new TextBlock { Text = L.T("Waiting for a picture…"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
        _status.Classes.Add("secondary");
        _insert = new Button { Content = L.T("Insert link"), IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        _insert.Classes.Add("accent");
        _insert.Click += (_, _) => Finish(_link);
        var cancel = new Button { Content = L.T("Cancel"), Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Finish(null);
        var bottom = new Border
        {
            Padding = new Thickness(14, 10),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Children = { _status, Col(_insert, 1), Col(cancel, 2) } },
        };
        bottom[!Border.BackgroundProperty] = this.GetResourceObservable("App.Surface").ToBinding();

        _manual = new TextBox { Watermark = L.T("…or paste the picture's link here"), Margin = new Thickness(0, 8, 0, 0) };
        _manual.TextChanged += (_, _) =>
        {
            var link = MeshPicLinks.Find(_manual.Text).FirstOrDefault() ?? (_manual.Text?.Trim() is { Length: > 8 } t && t.Contains("://") ? t : null);
            _link = link;
            _insert.IsEnabled = link is not null;
            if (link is not null) _status.Text = L.F("Link ready: {0}", link);
        };

        var earlierTitle = new TextBlock { Text = L.T("Already on MeshPic — pick one to use it instead of uploading:"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        earlierTitle.Classes.Add("secondary");
        _earlierBar = new Border
        {
            Padding = new Thickness(14, 8, 14, 6),
            IsVisible = false,
            Child = new StackPanel { Children = { earlierTitle, _earlierButtons } },
        };
        _earlierBar[!Border.BackgroundProperty] = this.GetResourceObservable("App.Surface").ToBinding();

        _content = new Panel();
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_earlierBar, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_earlierBar);
        root.Children.Add(_content);
        Content = root;

        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _settle.Tick += (_, _) => { _settle.Stop(); PickBest(); };
        _clipboardPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clipboardPoll.Tick += async (_, _) => await PollClipboardAsync();

        Opened += (_, _) =>
        {
            BuildBrowser();
            _clipboardPoll.Start();
        };
        Closed += (_, _) =>
        {
            _clipboardPoll.Stop();
            _settle.Stop();
        };
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Finish(null); };
    }

    private static Control Col(Control c, int column)
    {
        Grid.SetColumn(c, column);
        return c;
    }

    /// <summary>Shows the window over <paramref name="owner"/>; returns the picture link, or null if cancelled.</summary>
    public static async Task<string?> PickAsync(Window owner)
    {
        var w = new MeshPicWindow { Icon = owner.Icon };
        return await w.ShowDialog<string?>(owner);
    }

    private void BuildBrowser()
    {
#if WINDOWS
        var host = new Platform.Windows.WebView2Host();
        host.Ready += web =>
        {
            try
            {
                web.Profile.PreferredColorScheme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
                    ? Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Dark
                    : Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light;
            }
            catch { /* older runtime */ }
            web.Settings.IsStatusBarEnabled = false;
            web.WebMessageReceived += (_, e) =>
            {
                string? json = null;
                try { json = e.TryGetWebMessageAsString(); } catch { /* not a string */ }
                if (json is not null) OnPageMessage(json);
            };
            web.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                // Clicking a picture's link picks it, even one uploaded earlier.
                if (MeshPicLinks.IsPictureLink(e.Uri)) Select(e.Uri);
                else AppHost.Dialogs.OpenUrl(e.Uri);
            };
            web.SourceChanged += (_, _) => OnPageAddress(web.Source);
            web.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess && !_pageLoaded) ShowFallback(L.T("MeshPic didn't load. Check your internet connection, or open it in your browser."));
            };
            _ = web.AddScriptToExecuteOnDocumentCreatedAsync(WatchScript);
            web.Navigate(MeshPicLinks.SiteUrl);
            host.FocusBrowser();
        };
        host.Failed += reason => ShowFallback(L.F("{0} You can still upload in your browser.", reason));
        _content.Children.Clear();
        _content.Children.Add(host);
#else
        ShowFallback(L.T("The built-in browser is only available on Windows."));
#endif
    }

    /// <summary>Without the embedded browser: open the site in the browser and pick the link up from the clipboard.</summary>
    private void ShowFallback(string reason)
    {
        var open = new Button { Content = L.T("Open meshpic.org"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        open.Classes.Add("accent");
        open.Click += (_, _) => AppHost.Dialogs.OpenUrl(MeshPicLinks.SiteUrl);
        var title = new TextBlock { Text = L.T("Upload in your browser"), FontSize = 17, FontWeight = FontWeight.SemiBold };
        var why = new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        why.Classes.Add("hint");
        var how = new TextBlock
        {
            Text = L.T("Upload your picture on meshpic.org and copy its link — it's inserted into your message automatically."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var panel = new StackPanel { Margin = new Thickness(24), MaxWidth = 460, VerticalAlignment = VerticalAlignment.Center, Children = { title, why, how, open, _manual } };
        _content.Children.Clear();
        _content.Children.Add(panel);
    }

    private void OnPageMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            switch (type)
            {
                case "scan":
                    _pageLoaded = true;
                    foreach (var link in Strings(root, "input").SelectMany(MeshPicLinks.Find)) Observe(link, FromInput);
                    foreach (var link in Strings(root, "text").SelectMany(MeshPicLinks.Find)) Observe(link, FromText);
                    foreach (var link in Strings(root, "href").SelectMany(MeshPicLinks.Find)) Observe(link, FromHref);
                    if (root.TryGetProperty("page", out var page) && page.GetString() is { } address) OnPageAddress(address);
                    break;
                case "upload":
                    // A file was picked, dropped or pasted: links that show up from now on belong to it.
                    if (!_uploadStarted)
                    {
                        _uploadStarted = true;
                        if (!_closing) _status.Text = L.T("Uploading… the link is added as soon as it's ready.");
                    }
                    break;
                case "click":
                    _userClicked = true;
                    break;
                case "copy":
                    // Copying a link is picking it.
                    if (MeshPicLinks.Find(text).FirstOrDefault(MeshPicLinks.IsPictureLink) is { } copied) Select(copied);
                    break;
            }
        }
        catch (Exception ex) { AppHost.Core?.Log.Debug("MeshPic", "Page message ignored: " + ex.Message); }
    }

    /// <summary>The link that was picked (after the window closes).</summary>
    public string? PickedLink => _closing ? _link : null;

    /// <summary>Feeds a message as if the page had sent it (used by the screenshot tests, which have no browser).</summary>
    public void SimulatePageMessage(string json) => OnPageMessage(json);

    private static IEnumerable<string> Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];

    /// <summary>A picture link seen on the page: before an upload it's an earlier one; after, a new one is the upload's.</summary>
    private void Observe(string link, int priority)
    {
        if (_closing || !MeshPicLinks.IsPictureLink(link)) return;
        link = MeshPicLinks.Normalize(link);
        if (!_uploadStarted)
        {
            AddEarlier(link, priority);
            return;
        }
        if (_earlierSet.Contains(link)) return;
        if (!_candidates.ContainsKey(link)) _candidates[link] = (priority, _candidates.Count);
        else if (_candidates[link].Priority < priority) _candidates[link] = (priority, _candidates[link].Order);
        _status.Text = L.T("Getting the link…");
        if (!_settle.IsEnabled) _settle.Start();
    }

    private void OnPageAddress(string address)
    {
        if (_closing || !MeshPicLinks.IsPictureLink(address)) return;
        address = MeshPicLinks.Normalize(address);
        if (_uploadStarted && !_earlierSet.Contains(address)) Observe(address, FromNavigation);
        else if (_userClicked) Select(address);       // opened an earlier picture's page
        else AddEarlier(address, FromNavigation);     // the site showed it on its own
    }

    private void AddEarlier(string link, int priority)
    {
        link = MeshPicLinks.Normalize(link);
        if (!_earlierSet.Add(link)) return;
        _earlier.Add((link, priority));
        // Show the likeliest share links first (text boxes and visible text before plain anchors).
        _earlierButtons.Children.Clear();
        foreach (var (l, _) in _earlier.OrderByDescending(e => e.Priority).Take(5))
        {
            var label = l.Replace("https://", "").Replace("http://", "");
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 6, 6), FontSize = 12, Padding = new Thickness(10, 4) };
            ToolTip.SetTip(b, L.F("Use this picture: {0}", l));
            b.Click += (_, _) => Select(l);
            _earlierButtons.Children.Add(b);
        }
        _earlierBar.IsVisible = _earlierButtons.Children.Count > 0;
    }

    /// <summary>The user picked this link: insert it now.</summary>
    private void Select(string link)
    {
        if (_closing) return;
        link = MeshPicLinks.Normalize(link);
        _candidates.Clear();
        _candidates[link] = (int.MaxValue, 0);
        PickBest();
    }

    private void PickBest()
    {
        if (_closing || _candidates.Count == 0) return;
        var best = _candidates.OrderByDescending(c => c.Value.Priority).ThenBy(c => c.Value.Order).First().Key;
        _link = best;
        _insert.IsEnabled = true;
        _status.Text = L.F("✓ Link added to your message: {0}", best);
        _closing = true;
        // Leave the confirmation on screen for a moment, then close.
        DispatcherTimer.RunOnce(() => Close(best), TimeSpan.FromMilliseconds(1100));
    }

    private async Task PollClipboardAsync()
    {
        if (_closing || Clipboard is not { } clip) return;
        string? text;
        try { text = await clip.TryGetTextAsync(); }
        catch { return; }
        if (!_clipboardPrimed)
        {
            _clipboardPrimed = true;
            _clipboardAtStart = text;
            return;
        }
        if (text is null || text == _clipboardAtStart) return;
        _clipboardAtStart = text;
        // Copying a picture's link (here or in a browser) picks it.
        if (MeshPicLinks.Find(text).FirstOrDefault(MeshPicLinks.IsPictureLink) is { } copied) Select(copied);
    }

    private void Finish(string? link)
    {
        if (_closing && link is null) { Close(_link); return; }
        _closing = true;
        Close(link);
    }

    /// <summary>
    /// Injected into every page: reports the meshpic links in text boxes, the page text and anchors whenever they
    /// change, when an upload starts, clicks, and whatever the page copies to the clipboard.
    /// </summary>
    private const string WatchScript = """
        (() => {
          const RX = /(?:https?:\/\/)?(?:www\.)?meshpic\.org\/[^\s"'<>()\[\]{}\u2026]+/gi;
          const post = (o) => { try { window.chrome.webview.postMessage(JSON.stringify(o)); } catch (e) {} };
          try {
            if (navigator.clipboard && navigator.clipboard.writeText) {
              const orig = navigator.clipboard.writeText.bind(navigator.clipboard);
              navigator.clipboard.writeText = (t) => { post({ type: 'copy', text: String(t) }); return orig(t); };
            }
          } catch (e) {}
          document.addEventListener('copy', (ev) => {
            setTimeout(() => {
              let s = '';
              try { s = (ev.clipboardData && ev.clipboardData.getData('text')) || String(window.getSelection() || ''); } catch (e) {}
              const a = document.activeElement;
              if (!s && a && (a.value !== undefined)) s = a.value;
              if (s) post({ type: 'copy', text: s });
            }, 0);
          }, true);
          // Every full link in v (shortened ones like "meshpic.org/ab…" are skipped), always with https://.
          const grab = (v, set) => {
            if (!v) return;
            const s = String(v);
            RX.lastIndex = 0;
            let m;
            while ((m = RX.exec(s)) !== null) {
              if (s.charAt(m.index + m[0].length) === '\u2026' || /\.\.\.$/.test(m[0])) continue;
              let x = m[0].replace(/[.,;:!?]+$/, '');
              if (!/^https?:\/\//i.test(x)) x = 'https://' + x.replace(/^\/\//, '');
              set.add(x.replace(/^http:\/\//i, 'https://'));
            }
          };
          const collect = () => {
            const input = new Set(), text = new Set(), href = new Set();
            document.querySelectorAll('input, textarea').forEach(e => grab(e.value, input));
            document.querySelectorAll('[data-url],[data-link],[data-clipboard-text],[data-copy]').forEach(e =>
              ['data-url', 'data-link', 'data-clipboard-text', 'data-copy'].forEach(k => grab(e.getAttribute(k), input)));
            if (document.body) grab(document.body.innerText, text);
            document.querySelectorAll('a[href]').forEach(e => grab(e.href, href));
            return { input: [...input], text: [...text], href: [...href] };
          };
          let last = '';
          const scan = () => {
            const c = collect();
            const key = JSON.stringify(c) + location.href;
            if (key === last) return;
            last = key;
            post({ type: 'scan', input: c.input, text: c.text, href: c.href, page: location.href });
          };
          // An upload starting: a file picked, dropped or pasted, or sent to the server.
          const uploading = () => post({ type: 'upload' });
          document.addEventListener('change', (e) => { const t = e.target; if (t && t.type === 'file' && t.files && t.files.length) uploading(); }, true);
          document.addEventListener('drop', (e) => { if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length) uploading(); }, true);
          document.addEventListener('paste', (e) => { if (e.clipboardData && e.clipboardData.files && e.clipboardData.files.length) uploading(); }, true);
          document.addEventListener('click', () => post({ type: 'click' }), true);
          const isFile = (b) => { try { return b instanceof FormData || b instanceof Blob || b instanceof File; } catch (e) { return false; } };
          try {
            const f = window.fetch;
            window.fetch = function (input, init) { if (init && isFile(init.body)) uploading(); return f.apply(this, arguments); };
            const send = XMLHttpRequest.prototype.send;
            XMLHttpRequest.prototype.send = function (body) { if (isFile(body)) uploading(); return send.apply(this, arguments); };
          } catch (e) {}
          const start = () => {
            scan();
            let t = 0;
            new MutationObserver(() => { clearTimeout(t); t = setTimeout(scan, 250); })
              .observe(document.documentElement, { subtree: true, childList: true, characterData: true, attributes: true });
            setInterval(scan, 1500);
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
        })();
        """;
}
