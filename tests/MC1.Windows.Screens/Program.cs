using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using MC1.Core.Services;
using MC1.Windows;
using MC1.Windows.Controls;
using MC1.Windows.Services;
using MC1.Windows.Services.VectorTiles;
using MC1.Windows.ViewModels;
using MC1.Windows.Views;

// Renders every page of the app against the simulated radio and saves PNG screenshots.
var outDir = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "screens");
var theme = args.Length > 1 ? args[1] : "Light";
var only = args.Length > 2 ? args[2].Split(',') : null;
Directory.CreateDirectory(outDir);
var root = Path.Combine(Path.GetTempPath(), "mc1-screens-" + Guid.NewGuid().ToString("N"));
AppPaths.Root = root;
var seedTiles = Environment.GetEnvironmentVariable("MC1_TILE_SEED");
if (seedTiles is not null && Directory.Exists(seedTiles))
{
    foreach (var f in Directory.EnumerateFiles(seedTiles, "*", SearchOption.AllDirectories))
    {
        var dest = Path.Combine(root, "tiles", Path.GetRelativePath(seedTiles, f));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(f, dest);
    }
    TileService.Instance.OfflineOnly = true;
}
// Vector tiles for the "Dark" map (OpenFreeMap schema) seeded into the cache, drawn offline.
if (Environment.GetEnvironmentVariable("MC1_MVT_SEED") is { } mvtSeed && Directory.Exists(mvtSeed))
{
    foreach (var f in Directory.EnumerateFiles(mvtSeed, "*.pbf", SearchOption.AllDirectories))
    {
        var dest = Path.Combine(root, "tiles", "_openfreemap", "pbf", Path.GetRelativePath(mvtSeed, f));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(f, dest);
    }
    TileService.Instance.OfflineOnly = true;
}

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .With(MC1.Windows.Program.FontOptions)
    .SetupWithoutStarting();

void Pump(int ms)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(15);
    }
}

void Await(Task t, int timeoutMs = 60000)
{
    var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (!t.IsCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    if (t.IsFaulted) Console.WriteLine("Task failed: " + t.Exception?.GetBaseException().Message);
}

var core = new MeshApp(root);
AppHost.Core = core;
var dialogs = new DialogService();
AppHost.Dialogs = dialogs;
core.Notifier = new InAppNotifier();
AppHost.ApplyTheme = t => Application.Current!.RequestedThemeVariant = t == "Dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
AppHost.ApplyTheme(theme);
AppHost.Widget = new PreviewWidget();
core.Settings.Update(s => { s.ShowLinkPreviews = false; s.ShowInlineImages = false; });

// MC1_LANG=es renders everything in that language.
if (Environment.GetEnvironmentVariable("MC1_LANG") is { Length: > 0 } lang) L.SetLanguage(lang);
var vm = new MainWindowViewModel();
AppHost.Main = vm;
var win = new MainWindow { DataContext = vm, Width = 1280, Height = 820 };
AppHost.ChangeLanguage = s => win.SwitchLanguage(s);
dialogs.Owner = win;
win.Show();
Pump(300);

// Text that doesn't fit: cut off by its own box (measured wider than it was given, no wrapping or ellipsis) or
// reaching past the window's edge.
void ReportOverflow(string name)
{
    foreach (var tb in win.GetVisualDescendants().OfType<TextBlock>())
    {
        if (!tb.IsEffectivelyVisible || string.IsNullOrWhiteSpace(tb.Text) || tb.Bounds.Width < 1) continue;
        var fits = tb.TextWrapping != Avalonia.Media.TextWrapping.NoWrap || tb.TextTrimming != Avalonia.Media.TextTrimming.None;
        tb.Measure(Size.Infinity);
        var need = tb.DesiredSize.Width - tb.Margin.Left - tb.Margin.Right;
        var cut = !fits && need > tb.Bounds.Width + 1.5;
        var p = tb.TranslatePoint(new Point(tb.Bounds.Width, 0), win);
        var offWindow = p is { } q && q.X > win.Bounds.Width + 1;
        if (cut || offWindow)
            Console.WriteLine($"OVERFLOW {name}: [{tb.Text}] needs {need:0} has {tb.Bounds.Width:0}{(offWindow ? " (past window edge)" : "")}");
    }
}

void Shot(string name)
{
    if (only is not null && !only.Contains(name)) return;
    Pump(700);
    if (Environment.GetEnvironmentVariable("MC1_OVERFLOW") == "1") ReportOverflow(name);
    var frame = win.CaptureRenderedFrame();
    var path = Path.Combine(outDir, $"{name}-{theme.ToLowerInvariant()}.png");
    frame?.Save(path);
    Console.WriteLine("saved " + path);
}

// The start-up animation, frame by frame (made into a preview GIF by the build script).
if (only is not null && only.Contains("splash"))
{
    var swr = System.Diagnostics.Stopwatch.StartNew();
    _ = StartupSound.Wav;
    Console.WriteLine($"startup sound made in {swr.ElapsedMilliseconds} ms");
    var framesDir = Path.Combine(outDir, "splash-frames");
    Directory.CreateDirectory(framesDir);
    var splash = new StartupSplash { FrozenTime = 0 };
    var host = new Window { Width = 900, Height = 560, Content = splash };
    host.Show();
    Pump(300);
    // Cost of drawing one frame (layout + render, no capture), across the animation.
    var times = new List<(double Ms, double Cost)>();
    for (var ms = 0.0; ms <= StartupSplash.TotalDuration; ms += 50)
    {
        splash.FrozenTime = ms;
        splash.InvalidateVisual();
        var swf = System.Diagnostics.Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        times.Add((ms, swf.Elapsed.TotalMilliseconds));
    }
    foreach (var g in times.GroupBy(t => (int)(t.Ms / 500)))
        Console.WriteLine($"frame cost {g.Key * 500,4}-{g.Key * 500 + 500} ms: avg {g.Average(t => t.Cost):0.0} ms, max {g.Max(t => t.Cost):0.0} ms");
    var n = 0;
    for (var ms = 0.0; ms <= StartupSplash.TotalDuration + 40; ms += 1000.0 / 30)
    {
        splash.FrozenTime = ms;
        splash.InvalidateVisual();
        Pump(20);
        host.CaptureRenderedFrame()?.Save(Path.Combine(framesDir, $"f{n++:000}.png"));
    }
    Console.WriteLine($"splash frames: {n}");
    File.WriteAllBytes(Path.Combine(outDir, "startup-sound.wav"), StartupSound.Wav);
    Console.WriteLine($"startup sound: {StartupSound.Wav.Length} bytes, {StartupSound.Seconds:0.00} s");
    host.Close();
    Environment.Exit(0);
}

// Launch: the animation plays over the window, then takes itself away; a click/key skips it.
if (only is null || only.Contains("00a-startup"))
{
    win.ShowStartupSplash();
    Pump(900);
    Shot("00a-startup");
    var playing = win.GetVisualDescendants().OfType<StartupSplash>().FirstOrDefault();
    var t0 = DateTime.UtcNow;
    while (win.GetVisualDescendants().OfType<StartupSplash>().Any() && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(6)) Pump(50);
    Console.WriteLine($"startup animation: shown={playing is not null} removed after {(DateTime.UtcNow - t0).TotalMilliseconds + 900:0} ms");
    win.ShowStartupSplash();
    Pump(200);
    win.GetVisualDescendants().OfType<StartupSplash>().FirstOrDefault()?.Skip();
    t0 = DateTime.UtcNow;
    while (win.GetVisualDescendants().OfType<StartupSplash>().Any() && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(6)) Pump(20);
    Console.WriteLine($"skipped: gone after {(DateTime.UtcNow - t0).TotalMilliseconds:0} ms");
}
// The real start-up order: the window and the animation's dark first frame, then the pages are built behind it, then
// the animation starts; refreshes wait until it's over.
if (only is not null && only.Contains("00b-startup-order"))
{
    var w2 = new MainWindow { Width = 1280, Height = 820 };
    w2.Show();
    var sp = w2.ShowStartupSplash(autoStart: false)!;
    Pump(200);
    w2.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "00b-startup-first-frame.png"));
    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    var vm2 = new MainWindowViewModel();
    w2.DataContext = vm2;
    Pump(300);
    var nav = w2.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Classes.Contains("navrail"))?.ItemCount ?? -1;
    Console.WriteLine($"start-up order: pages built in {sw2.ElapsedMilliseconds} ms, nav items bound={nav}, splash waiting={sp.Parent is not null}");
    var reloadsBefore = vm2.Chats.Conversations.Count;
    sp.Start();
    core.Notify(MC1.Core.Services.DataKind.Conversations);
    Pump(400);
    var t0 = DateTime.UtcNow;
    while (sp.Parent is not null && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(6)) Pump(50);
    Pump(400);
    Console.WriteLine($"start-up order: animation finished={sp.Parent is null}, chats after release={vm2.Chats.Conversations.Count}");
    w2.Close();
}
// First run on this PC (the harness always starts with a fresh data folder): the welcome guide, then — once the
// first radio connects — "Set up your radio".
var firstRun = only is null || only.Any(o => o.StartsWith("30-"));
if (firstRun)
{
    AppHost.ShowOverlay = (content, width) => win.ShowOverlayAsync(content, width);
    Console.WriteLine($"first run: IsFirstRun={core.Settings.IsFirstRun} radios={core.Db.GetRadios().Count}");
    var flow = vm.StartFirstRunAsync();
    Pump(600);
    var guide = win.GetVisualDescendants().OfType<ContentControl>().Select(c => c.Content).OfType<WelcomeGuideViewModel>().FirstOrDefault();
    Console.WriteLine($"guide shown: {guide is not null}");
    if (guide is not null)
    {
        // Page 1 asks the language: picking one switches the whole app right away, the guide included.
        var startLang = L.Code;
        var oldMain = AppHost.Main;
        int Handlers(string name) => (typeof(MeshApp).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(core) as Delegate)?.GetInvocationList().Length ?? -1;
        var before = (Handlers("DataChanged"), Handlers("StatusChanged"));
        var pick = startLang == "de" ? "es" : "de";
        guide.SelectedLanguage = guide.Languages.First(o => o.Code == pick);
        Pump(500);
        var shells = win.GetVisualDescendants().OfType<ShellView>().Count();
        var guideNow = win.GetVisualDescendants().OfType<ContentControl>().Select(c => c.Content).OfType<WelcomeGuideViewModel>().FirstOrDefault();
        Console.WriteLine($"core handlers before={before} after={(Handlers("DataChanged"), Handlers("StatusChanged"))}");
        Console.WriteLine($"language picked: code={L.Code} saved={core.Settings.Current.Language} newMain={!ReferenceEquals(oldMain, AppHost.Main)} shells={shells} " +
                          $"guideKept={ReferenceEquals(guideNow, guide)} title=[{guide.Step.Title}] nav=[{AppHost.Main?.NavItems[0].Label}]");
        Shot("31-language-picked");
        // Back to the language the run started in (the rest of the screenshots use it).
        guide.SelectedLanguage = guide.Languages.First(o => o.Code == startLang);
        Pump(500);
        vm = AppHost.Main!;
        Console.WriteLine($"language back: code={L.Code} title=[{guide.Step.Title}]");
        var pages = guide.Dots.Count;
        for (var page = 1; page <= pages; page++)
        {
            Shot($"30-welcome-{page}");
            if (page < pages) guide.NextCommand.Execute(null);
            Pump(300);
        }
        guide.LaterCommand.Execute(null);
        Pump(400);
        Console.WriteLine($"guide closed: {flow.IsCompleted} shownFlag={core.Settings.Current.WelcomeGuideShown}");
    }
}
Shot("00-disconnected");
Await(core.ConnectAsync(new ConnectionTarget(ConnectionKind.Simulator, "demo", "Demo radio")));
Pump(2500);
if (firstRun)
{
    var setup = win.GetVisualDescendants().OfType<ContentControl>().Select(c => c.Content).OfType<RadioSetupViewModel>().FirstOrDefault();
    Console.WriteLine($"radio set-up offered after the first connection: {setup is not null}");
    Shot("30-radio-setup");
    setup?.SkipCommand.Execute(null);
    Pump(400);
    // "Apply to radio" on a radio still on the firmware defaults.
    var originalName = core.SelfInfo!.Name;
    Await(core.Device.SetRadioAsync(915.0, 250, 10, 5));
    var rs = new RadioSetupViewModel { Name = "Demo Base" };
    Console.WriteLine($"setup suggests: {rs.SelectedPreset?.Name ?? "(none)"} — {rs.RecommendedText}");
    rs.SelectedPreset = rs.Presets.First(p => p.Id == "us-ca");
    Await(rs.ApplyCommand.ExecuteAsync(null));
    var after = core.SelfInfo!;
    Console.WriteLine($"after apply: name={after.Name} radio={after.RadioFrequency:0.000}/{after.RadioBandwidth}/{after.RadioSpreadingFactor}/{after.RadioCodingRate} msg={rs.Message ?? "ok"}");
    Await(core.Device.SetNameAsync(originalName));
    // A later launch: the guide and the prompt don't come back.
    var again = new MainWindowViewModel();
    var t2 = again.StartFirstRunAsync();
    Pump(300);
    Console.WriteLine($"second launch shows nothing: {t2.IsCompleted}");
}
if (Environment.GetEnvironmentVariable("MC1_EMOJI_NAMES") is not null)
{
    // Fictional nicknames for checking emoji avatars.
    string[] names = ["💧Demo Water", "🛩Demo Mobile", "✳Demo Sun👌", "♆Demo Sea🌊", "🧑‍💻Demo Operator", "#testing"];
    var all = core.Contacts.GetAll().ToList();
    for (var i = 0; i < all.Count && i < names.Length; i++) core.Contacts.SetNickname(all[i], names[i]);
    foreach (var n in names.Append("DEMO0001").Append("Demo 3").Append("🛠 Demo Workshop🌋"))
        Console.WriteLine($"{n} -> {Formatters.AvatarEmoji(n) ?? "(generic)"}");
    Pump(500);
}
vm.Chats.Reload();
if (Environment.GetEnvironmentVariable("MC1_THEMES") is not null)
{
    // Give a couple of chats their own look (like a user would from the palette button).
    ChatThemes.Set("ch:0", ChatThemes.Presets.First(p => p.Name == "Ocean").Theme);
    var alice = core.Contacts.GetAll().First(c => c.ContactType == MeshCore.ContactType.Chat);
    ChatThemes.Set(MessageService.DmKey(alice.Id), ChatThemes.Presets.First(p => p.Name == "Midnight").Theme);
    if (Environment.GetEnvironmentVariable("MC1_BG_IMAGE") is { } img)
    {
        var importTask = ChatThemes.ImportImageAsync(img);
        Await(importTask);
        var name = importTask.Result;
        ChatThemes.Set("ch:1", new ChatTheme { BackgroundKind = "Image", ImageFile = name, ImageDim = 0.3, IncomingBubble = "#E6FFFFFF", IncomingText = "#FF1F2937" });
    }
}
Shot("01-chats-list");
var first = vm.Chats.Conversations.FirstOrDefault(c => c.Kind == ChatFilter.Channels) ?? vm.Chats.Conversations.FirstOrDefault();
if (first is not null) vm.Chats.Selected = first;
Pump(800);
Shot("02-chat-channel");
// How long a sent message takes to show up in a busy chat (more messages than one page).
if (first is not null && only is not null && only.Contains("sendlag"))
{
    var chatVm = vm.Chats.Active!;
    var rid = core.RadioId!;
    var idx = chatVm.Channel!.Idx;
    var t0ms = MC1.Core.Models.Time.Now() - 3_600_000;
    for (var k = 0; k < 260; k++)
        core.Db.SaveMessage(new MC1.Core.Models.MessageRecord
        {
            RadioId = rid, ChannelIndex = idx, Text = $"Message {k} with a link https://example.com/{k} and some words to wrap a little",
            Timestamp = (uint)((t0ms + k * 10_000) / 1000), SortDate = t0ms + k * 10_000, SenderName = k % 3 == 0 ? "Bob" : "Alice",
            Direction = 1, Status = 3,
        });
    // A long history elsewhere too, like a PC that has used MeshCore for a while.
    var others = core.Contacts.GetAll().Where(c => c.ContactType == MeshCore.ContactType.Chat).ToList();
    for (var k = 0; k < 6000; k++)
    {
        var c = others[k % others.Count];
        core.Db.SaveMessage(new MC1.Core.Models.MessageRecord
        {
            RadioId = rid, ContactId = k % 2 == 0 ? c.Id : null, ChannelIndex = k % 2 == 0 ? null : 0, Text = $"Older message {k}",
            Timestamp = (uint)((t0ms - 86_400_000 + k * 1000) / 1000), SortDate = t0ms - 86_400_000 + k * 1000, SenderName = "Carol", Direction = 1, Status = 3,
        });
    }
    var swl = System.Diagnostics.Stopwatch.StartNew();
    var lastMsgs = core.Db.GetLastMessages(rid);
    Console.WriteLine($"GetLastMessages: {swl.ElapsedMilliseconds} ms ({lastMsgs.Count})");
    swl.Restart();
    vm.Chats.Reload();
    Console.WriteLine($"Chats.Reload: {swl.ElapsedMilliseconds} ms");
    chatVm.LoadMessages();
    Pump(1500);
    Console.WriteLine($"busy chat: {chatVm.Messages.Count} messages shown");
    for (var n = 0; n < 3; n++)
    {
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        chatVm.LoadMessages();
        var load = sw0.ElapsedMilliseconds;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Console.WriteLine($"reload: {load} ms, then layout {sw0.ElapsedMilliseconds - load} ms");
    }
    {
        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        var recs = core.Db.GetChannelMessages(rid, idx, 200);
        var q = sw1.ElapsedMilliseconds;
        var made = recs.Select(m => MessageItemViewModel.From(m, chatVm)).ToList();
        Console.WriteLine($"query {q} ms, view-models {sw1.ElapsedMilliseconds - q} ms");
    }
    for (var n = 0; n < 6; n++)
    {
        var text = $"Lag test {n}";
        chatVm.Draft = text;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        chatVm.SendCommand.Execute(null);
        Console.WriteLine($"  send command returned after {sw.ElapsedMilliseconds} ms");
        while (chatVm.Messages.LastOrDefault()?.Text != text && sw.ElapsedMilliseconds < 10_000)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(2);
        }
        var shown = sw.ElapsedMilliseconds;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Console.WriteLine($"sent message shown after {shown} ms (+ layout {sw.ElapsedMilliseconds - shown} ms), {chatVm.Messages.Count} in the list");
        Pump(1500);
    }
    Environment.Exit(0);
}
// Your own channel message, repeated by the simulated repeaters (the "repeat" chip, like the iPhone app).
if (first is not null && (only is null || only.Contains("02b-chat-channel-repeats")))
{
    var chatVm = vm.Chats.Active!;
    chatVm.Draft = "Anyone copy? Testing the new repeater";
    chatVm.SendCommand.Execute(null);
    Pump(6000);
    Shot("02b-chat-channel-repeats");
}
var dm = vm.Chats.Conversations.FirstOrDefault(c => c.Kind == ChatFilter.Direct);
if (dm is not null)
{
    vm.Chats.Selected = dm;
    Pump(300);
    vm.Chats.Active!.Draft = "Hello from Windows! Meet at 47.6205, -122.3493 https://meshcore.co.uk";
    vm.Chats.Active.SendCommand.Execute(null);
    Pump(2500);
    Console.WriteLine($"active={vm.Chats.Active?.Title} msgs={vm.Chats.Active?.Messages.Count}");
}
Shot("03-chat-dm");
if (Environment.GetEnvironmentVariable("MC1_DIVIDER") is not null && vm.Chats.Active is { } act)
{
    // Window minimised while a reply arrives, then restored: the reply gets a "New Messages" line.
    core.Messages.AppIsForeground = false;
    act.Draft = "Are you still there?";
    act.SendCommand.Execute(null);
    Pump(3500);
    Console.WriteLine($"minimised: unread={core.Messages.UnreadCount(act.Key)} divider={act.HasNewDivider}");
    core.Messages.AppIsForeground = true;
    vm.OnWindowActivated();
    Pump(800);
    Console.WriteLine($"restored: unread={core.Messages.UnreadCount(act.Key)} divider={act.HasNewDivider} at={act.Messages.FirstOrDefault(m => m.ShowNewDivider)?.Text}");
    Shot("03b-chat-divider");
    foreach (var sv in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<ScrollViewer>().Where(x => x.Name == "Scroller"))
        Console.WriteLine($"scroller offset={sv.Offset.Y:0} extent={sv.Extent.Height:0} viewport={sv.Viewport.Height:0}");
    Shot("03c-chat-divider-later");
    foreach (var sv in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<ScrollViewer>().Where(x => x.Name == "Scroller"))
    {
        Console.WriteLine($"later offset={sv.Offset.Y:0} extent={sv.Extent.Height:0} viewport={sv.Viewport.Height:0} bounds={sv.Bounds}");
        var list = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(sv).OfType<ItemsControl>().First(x => x.Name == "MessageList");
        Console.WriteLine($"list bounds={list.Bounds} content={(sv.Content as Control)?.Bounds} desired={(sv.Content as Control)?.DesiredSize}");
        var last = list.ContainerFromIndex(list.ItemCount - 1);
        Console.WriteLine($"last={last?.Bounds} desired={last?.DesiredSize}");
    }
    Console.WriteLine($"list: {string.Join(", ", vm.Chats.Conversations.Select(c => c.Title + "=" + c.Unread))} total={vm.UnreadTotal}");
}
if (Environment.GetEnvironmentVariable("MC1_DEBUG_TEXT") is not null)
    foreach (var mt in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<MC1.Windows.Controls.MessageText>())
        Console.WriteLine($"MessageText fg={mt.Foreground} link={mt.LinkBrush} sel={mt.SelectionBrush} classes={string.Join(",", mt.Classes)}");
vm.Navigate(Page.Contacts);
Pump(300);
if (vm.Contacts.Contacts.Count > 0) vm.Contacts.Selected = vm.Contacts.Contacts.First(c => c.Contact.ContactType == MeshCore.ContactType.Repeater);
Shot("04-contacts");
// Filter bubbles, with Blocked as one of them.
if (only is null || only.Contains("04b-contacts-blocked"))
{
    var bob = core.Contacts.GetAll().First(c => c.Name == "Bob");
    core.Contacts.SetBlocked(bob, true);
    vm.Contacts.Reload();
    vm.Contacts.TypeFilter = ContactTypeFilter.Repeaters;
    Shot("04a-contacts-repeaters");
    vm.Contacts.TypeFilter = ContactTypeFilter.Blocked;
    Shot("04b-contacts-blocked");
    core.Contacts.SetBlocked(core.Contacts.GetAll().First(c => c.Name == "Bob"), false);
    vm.Contacts.TypeFilter = ContactTypeFilter.All;
    vm.Contacts.Reload();
}
vm.Contacts.Segment = ContactSegment.Discover;
Shot("05-contacts-discover");
vm.Navigate(Page.Map);
Pump(600);
if (core.Contacts.GetAll().FirstOrDefault(c => c.ContactType == MeshCore.ContactType.Repeater && c.HasLocation) is { } rpt)
    vm.Map.SelectedMarker = vm.Map.Markers.FirstOrDefault(m => m.Id == "c:" + rpt.Id);
Shot("06-map");
vm.Map.ShowPath([core.Contacts.GetAll().First(c => c.ContactType == MeshCore.ContactType.Repeater).PublicKey[..1]]);
Shot("06b-map-path");
if (Environment.GetEnvironmentVariable("MC1_MVT_SEED") is not null)
{
    vm.Map.LayerId = "Dark";
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var (btx, bty) = MapMath.TileXY(1.0000, 1.0000, 14);
    for (var i = 0; i < 6; i++)
    {
        var bench = OpenFreeMapSource.Dark.RenderTileAsync(14, btx + i % 2, bty + i / 2, true);
        Await(bench);
        Console.WriteLine($"render z14 tile {i}: {sw.ElapsedMilliseconds} ms, {bench.Result?.Length ?? 0} bytes");
        sw.Restart();
    }
    foreach (var (zoom, name) in new[] { (11.0, "06c-map-dark-z11"), (13.0, "06d-map-dark-z13"), (15.3, "06e-map-dark-z15"), (17.0, "06f-map-dark-z17") })
    {
        vm.Map.CenterOn(1.0000, 1.0000, zoom, "Demo location");
        Pump(4000);
        Shot(name);
    }
    vm.Map.LayerId = "Standard";
}
vm.Navigate(Page.Tools);
foreach (var r in core.Contacts.GetAll().Where(c => c.ContactType == MeshCore.ContactType.Repeater)) vm.Tools.TracePath.AddHopCommand.Execute(r);
vm.Tools.TracePath.RunCommand.Execute(null);
Pump(3000);
Shot("07-tools-trace");
vm.Tools.Selected = vm.Tools.Items[1];
Pump(200);
var los = vm.Tools.LineOfSight;
los.PickB = los.Places.LastOrDefault();
var samples = new List<MC1.Core.Utilities.ElevationSample>();
for (var i = 0; i <= 80; i++)
{
    var d = i * 5200.0 / 80;
    var h = 8 + 25 * Math.Exp(-Math.Pow((d - 2600) / 700, 2)) + 4 * Math.Sin(i / 3.0);
    samples.Add(new MC1.Core.Utilities.ElevationSample(0.9917 + i * 0.0005, 1.0089, h, d));
}
los.ApplyProfile(samples);
Shot("08-tools-los");
vm.Tools.Selected = vm.Tools.Items[2];
Shot("09-tools-rxlog");
vm.Tools.Selected = vm.Tools.Items[3];
vm.Tools.Noise.ToggleCommand.Execute(null);
Pump(4000);
Shot("10-tools-noise");
vm.Tools.Noise.ToggleCommand.Execute(null);
vm.Tools.Selected = vm.Tools.Items[4];
Shot("11-tools-discovery");
vm.Tools.Selected = vm.Tools.Items[5];
vm.Tools.Cli.Input = "help";
vm.Tools.Cli.SubmitCommand.Execute(null);
Pump(500);
Shot("12-tools-cli");
vm.Navigate(Page.Radio);
Shot("13-radio");
// A radio on the firmware defaults (915 MHz, 250 kHz, SF10, CR5 — "Custom"): picking USA must put the radio on it,
// and background refreshes mustn't undo the choice or anything being typed.
if (only is null || only.Contains("13b-radio-preset-picked"))
{
    Await(core.Device.SetRadioAsync(915.0, 250, 10, 5));
    vm.Radio.OnShown();
    Pump(1500);
    Shot("13a-radio-custom");
    var asked = new List<string>();
    dialogs.AutoAnswer = title => { asked.Add(title); return true; };
    vm.Radio.SelectedPreset = vm.Radio.Presets.First(p => p.Id == "us-ca");
    Pump(3000);
    core.Notify(DataKind.Radio);
    Pump(1500);
    var si = core.SelfInfo!;
    Console.WriteLine($"preset picked: asked=[{string.Join("|", asked)}] radio={si.RadioFrequency:0.000}/{si.RadioBandwidth}/{si.RadioSpreadingFactor}/{si.RadioCodingRate} picker={vm.Radio.SelectedPreset?.Name ?? "Custom"} header={vm.Radio.CurrentPresetName} msg={vm.Radio.Message}");
    dialogs.AutoAnswer = null;
    Shot("13b-radio-preset-picked");
    vm.Radio.EditName = "Typing a new name";
    core.Notify(DataKind.Radio);
    Pump(1200);
    Console.WriteLine($"edit kept after refresh: {vm.Radio.EditName}");
    vm.Radio.EditName = si.Name;
    vm.Navigate(Page.Settings);
    vm.Navigate(Page.Radio);
    Pump(1500);
    Console.WriteLine($"after leaving and coming back: picker={vm.Radio.SelectedPreset?.Name ?? "Custom"}");
}
vm.Navigate(Page.Settings);
Shot("14-settings");
foreach (var (id, shot) in new[] { ("chats", "14b-settings-chats"), ("looks", "14c-settings-looks"), ("notifications", "14d-settings-notifications"), ("data", "14e-settings-data"), ("about", "14i-settings-about") })
{
    vm.Settings.ShowCategory(id);
    Shot(shot);
}
// Small window (e.g. snapped to half the screen) with the slim sidebar: categories become a strip on top.
vm.NavCollapsed = true;
win.Width = 760;
vm.Settings.ShowCategory("general");
Shot("14f-settings-narrow");
vm.Settings.ShowCategory("looks");
Shot("14g-settings-narrow-looks");
vm.Navigate(Page.Chats);
Shot("14h-chats-narrow");
win.Width = 1280;
vm.NavCollapsed = false;
// "+" menu in the message box and the MeshPic window (without the Windows browser it shows the browser fallback).
if (only is null || only.Contains("15-plus-menu"))
{
    Pump(800);
    var cv0 = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<ConversationView>().FirstOrDefault();
    var plus = cv0?.FindControl<Button>("PlusButton");
    plus?.Flyout?.ShowAt(plus);
    Shot("15-plus-menu");
    void PopupShot(string name)
    {
        if (only is not null && !only.Contains(name)) return;
        Pump(500);
        if ((plus?.Flyout as Flyout)?.Content is Control content && TopLevel.GetTopLevel(content) is { } popup)
        {
            popup.CaptureRenderedFrame()?.Save(Path.Combine(outDir, $"{name}-{theme.ToLowerInvariant()}.png"));
            Console.WriteLine("saved popup " + name);
        }
    }
    Console.WriteLine($"plus={plus is not null} open={(plus?.Flyout as Flyout)?.IsOpen} content={((plus?.Flyout as Flyout)?.Content as Control)?.GetType().Name} top={(((plus?.Flyout as Flyout)?.Content as Control) is { } cc ? TopLevel.GetTopLevel(cc)?.GetType().Name : "none")}");
    PopupShot("15a-plus-menu-popup");
    // "Share contact": pick from your contacts.
    var shareContact = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<Button>()
        .Concat(TopLevel.GetTopLevel(plus!) is { } tl ? Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(tl).OfType<Button>() : [])
        .FirstOrDefault(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Share contact"));
    var cv = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<ConversationView>().FirstOrDefault();
    if (cv is not null)
    {
        typeof(ConversationView).GetMethod("ShowContactPicker_OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(cv, [null, new Avalonia.Interactivity.RoutedEventArgs()]);
        Pump(300);
        PopupShot("15b-plus-share-contact");
        plus?.Flyout?.Hide();
        // Shared location, contact card and your own info as they look in a chat.
        var chatVm = vm.Chats.Active!;
        var alice = core.Contacts.GetAll().First(c => c.Name == "Alice");
        chatVm.Draft = "Meet here: 1.000000, 1.000000 — ask " + MC1.Core.Utilities.ContactShareUtilities.FormatShare(alice.PublicKey, alice.ContactType, alice.Name);
        chatVm.SendCommand.Execute(null);
        Pump(1500);
        Shot("15c-shared-location-contact");
    }
    else plus?.Flyout?.Hide();
}
if (only is null || only.Contains("16-meshpic"))
{
    var mp = new MeshPicWindow();
    mp.Show(win);
    Pump(600);
    // The site lists a picture uploaded earlier: offered, not inserted.
    mp.SimulatePageMessage("""{"type":"scan","input":[],"text":["https://meshpic.org/OldPic1"],"href":["https://meshpic.org/privacy","https://meshpic.org/OldPic1"],"page":"https://meshpic.org/"}""");
    Pump(1500);
    Console.WriteLine($"meshpic after listing earlier upload: picked={mp.PickedLink ?? "(none)"}");
    var f = mp.CaptureRenderedFrame();
    f?.Save(Path.Combine(outDir, $"16-meshpic-{theme.ToLowerInvariant()}.png"));
    // A new upload: its link is used, not the earlier one.
    mp.SimulatePageMessage("""{"type":"click"}""");
    mp.SimulatePageMessage("""{"type":"upload"}""");
    mp.SimulatePageMessage("""{"type":"scan","input":["https://meshpic.org/NewPic2"],"text":["https://meshpic.org/OldPic1","https://meshpic.org/NewPic2"],"href":[],"page":"https://meshpic.org/"}""");
    Pump(1500);
    // (Headless test: the settle timer doesn't run here, so pick directly.)
    if (mp.PickedLink is null) typeof(MeshPicWindow).GetMethod("PickBest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(mp, null);
    Console.WriteLine($"meshpic after new upload: picked={mp.PickedLink ?? "(none)"}");
    mp.Close();
    var mp2 = new MeshPicWindow();
    mp2.Show(win);
    Pump(300);
    mp2.SimulatePageMessage("""{"type":"scan","input":[],"text":["https://meshpic.org/OldPic1"],"href":[],"page":"https://meshpic.org/"}""");
    mp2.SimulatePageMessage("""{"type":"copy","text":"https://meshpic.org/OldPic1"}""");
    Pump(300);
    Console.WriteLine($"meshpic after copying the earlier one: picked={mp2.PickedLink ?? "(none)"}");
    mp2.Close();
    // The site shows links without https:// (and shortened with "…"): the inserted link is still the full one.
    var mp3 = new MeshPicWindow();
    mp3.Show(win);
    Pump(300);
    mp3.SimulatePageMessage("""{"type":"scan","input":[],"text":["meshpic.org/OldPic1"],"href":[],"page":"https://meshpic.org/"}""");
    mp3.SimulatePageMessage("""{"type":"upload"}""");
    mp3.SimulatePageMessage("""{"type":"scan","input":[],"text":["https://meshpic.org/OldPic1","Your picture: meshpic.org/NewPic3 (meshpic.org/NewP…)"],"href":[],"page":"https://meshpic.org/"}""");
    Pump(300);
    if (mp3.PickedLink is null) typeof(MeshPicWindow).GetMethod("PickBest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(mp3, null);
    Console.WriteLine($"meshpic link without https: picked={mp3.PickedLink ?? "(none)"}");
    mp3.Close();
}

// Dialogs rendered as standalone windows
void DialogShot(string name, object dialogVm, double w, double h)
{
    if (only is not null && !only.Contains(name)) return;
    var dw = new Window { Width = w, Height = h, Content = new ContentControl { Content = dialogVm } };
    dw.Show();
    Pump(900);
    var frame = dw.CaptureRenderedFrame();
    var path = Path.Combine(outDir, $"{name}-{theme.ToLowerInvariant()}.png");
    frame?.Save(path);
    Console.WriteLine("saved " + path);
    dw.Close();
}

DialogShot("20-connect", new ConnectViewModel(), 560, 620);
var repeater = core.Contacts.GetAll().First(c => c.ContactType == MeshCore.ContactType.Repeater);
DialogShot("21-node-management", new NodeManagementViewModel(repeater), 920, 740);
DialogShot("22-new-chat", new NewChatViewModel(vm.Chats), 520, 600);
DialogShot("23-share-contact", new ShareContactViewModel(null), 440, 600);
DialogShot("24-login", new LoginViewModel(repeater), 440, 360);
if (vm.Chats.Conversations.FirstOrDefault(c => c.Kind == ChatFilter.Channels) is { } ch && core.Channels.GetAll().FirstOrDefault() is { } chan)
    DialogShot("25-channel-info", new ChannelInfoViewModel(chan), 480, 640);
DialogShot("26-restore-backup", new ConfigTransferViewModel(Mc1Backup.Read(Mc1Backup.Export(core, "1.0.0", out _)), Mc1Backup.DefaultFileName(DateTime.Now)), 540, 640);
DialogShot("27-log", new LogViewerViewModel(), 900, 640);
DialogShot("28-chat-appearance", new ChatAppearanceViewModel("ch:0", "Appearance · Public"), 860, 700);
vm.Settings.OnShown();

foreach (var (text, a, b, ins) in new[] { ("Hello world", 5, 5, "https://meshpic.org/Ab3x"), ("Look:", 5, 5, "https://meshpic.org/Ab3x"), ("", 0, 0, "😀"), ("ab", 1, 1, "😀"), ("replace ME please", 8, 10, "https://meshpic.org/q1w2") })
    Console.WriteLine($"insert [{text}] @{a}-{b} -> [{MeshPicLinks.InsertAt(text, a, b, ins).Text}] caret={MeshPicLinks.InsertAt(text, a, b, ins).Caret}");
foreach (var u in new[] { "https://meshpic.org/", "https://meshpic.org/es", "https://meshpic.org/privacy", "https://meshpic.org/Ab3xY", "meshpic.org/k9Qz", "https://meshpic.org/i/abc123.jpg", "https://meshpic.org/delete/abc123", "https://meshpic.org/static/app.js", "https://example.com/abc" })
    Console.WriteLine($"picture link? {u} -> {MeshPicLinks.IsPictureLink(u)}");
Console.WriteLine("found: " + string.Join(" | ", MeshPicLinks.Find("Your link: https://meshpic.org/Ab3xY. Share meshpic.org/k9Qz now, http://www.meshpic.org/w1w2 or meshpic.org/Trunc\u2026")));
var bubble = UnreadBadge.RenderBubblePixels(7, 32);
Console.WriteLine($"badge pixels opaque={Enumerable.Range(0, bubble.Length / 4).Count(i => bubble[i * 4 + 3] > 200)} of {bubble.Length / 4}");
File.WriteAllBytes(Path.Combine(outDir, "badge-7.bgra"), bubble);
UnreadBadge.Update(5);
Console.WriteLine("window title: " + win.Title);
Await(core.DisconnectAsync(), 5000);
try { Directory.Delete(root, true); } catch { }
Console.WriteLine("done");
Environment.Exit(0);

/// <summary>Stands in for the Windows widget installer so Settings shows the lock screen widget row.</summary>
sealed class PreviewWidget : ILockScreenWidget
{
    public WidgetInstallState State => WidgetInstallState.NotInstalled;
    public string Description => "Shows new messages and the radio's battery on the lock screen and in Widgets (Win+W).";
    public Task<string> InstallAsync(IProgress<string>? progress = null) => Task.FromResult("Widget installed.");
    public Task<string> RemoveAsync() => Task.FromResult("Widget removed.");
}
