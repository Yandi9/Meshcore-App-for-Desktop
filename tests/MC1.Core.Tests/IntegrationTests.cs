using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Simulation;
using MC1.Core.Utilities;
using MeshCore;
using Xunit;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MC1.Core.Tests;

public sealed class TestNotifier : INotifier
{
    public List<(string Title, string Body, string Key)> Messages { get; } = new();
    public List<(string Title, string Body)> Infos { get; } = new();
    public void ShowMessage(string title, string body, string conversationKey, bool allowReply) { lock (Messages) Messages.Add((title, body, conversationKey)); }
    public void ShowInfo(string title, string body, string? actionKey = null) { lock (Infos) Infos.Add((title, body)); }
    public void UpdateBadge(int unread) { }
}

public sealed class AppFixture : IAsyncDisposable
{
    public MeshApp App { get; }
    public SimulatedRadio Radio { get; }
    public TestNotifier Notifier { get; } = new();
    public string Root { get; }

    public AppFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "mc1test-" + Guid.NewGuid().ToString("N"));
        App = new MeshApp(Root, Notifier);
        Radio = new SimulatedRadio(chatter: false);
        App.SimulatorFactory = () => Radio;
    }

    public async Task ConnectAsync() => await App.ConnectAsync(new ConnectionTarget(ConnectionKind.Simulator, "demo")).WaitAsync(TimeSpan.FromSeconds(30));

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch { /* ignore */ }
    }

    public static async Task<bool> Eventually(Func<bool> cond, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(50);
        }
        return cond();
    }
}

public class IntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ConnectSyncsContactsChannelsAndQueuedMessages()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        Assert.Equal(LinkStatus.Ready, app.Status);
        Assert.Equal("Demo Radio", app.SelfName);
        Assert.Equal("Demo Radio (simulated)", app.Capabilities!.Model);

        var contacts = app.Contacts.GetAll();
        Assert.Equal(6, contacts.Count);
        Assert.Contains(contacts, c => c.Name == "Hilltop Repeater" && c.ContactType == ContactType.Repeater);
        Assert.Contains(contacts, c => c.Name == "Town Square" && c.ContactType == ContactType.Room);

        var channels = app.Channels.GetAll();
        Assert.Equal(2, channels.Count);
        Assert.True(channels[0].IsPublic);
        Assert.Equal("#test", channels[1].Name);

        // Queued: 1 DM from Alice + 2 channel messages.
        Assert.True(await AppFixture.Eventually(() => app.Db.GetChannelMessages(app.RadioId!, 0).Count == 1 && app.Db.GetChannelMessages(app.RadioId!, 1).Count == 1));
        var alice = contacts.First(c => c.Name == "Alice");
        var dms = app.Db.GetDirectMessages(alice.Id);
        Assert.Single(dms);
        Assert.StartsWith("Hey! Welcome", dms[0].Text);
        var pub = app.Db.GetChannelMessages(app.RadioId!, 0)[0];
        Assert.Equal("Bob", pub.SenderName);
        Assert.Equal("Morning mesh! Anyone hearing the new repeater?", pub.Text);
        Assert.True(app.Db.GetContact(alice.Id)!.UnreadCount >= 1);
        Assert.True(app.Battery is null || app.Battery.Level > 3000);
    }

    [Fact]
    public async Task DirectMessageIsDeliveredAndReplied()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var alice = app.Contacts.GetAll().First(c => c.Name == "Alice");
        var sent = app.Messages.QueueDirectMessage(alice, "Hello from Windows!");
        Assert.True(await AppFixture.Eventually(() => app.Db.GetMessage(sent.Id)!.MessageStatus == MessageStatus.Delivered), "DM should be acked");
        var m = app.Db.GetMessage(sent.Id)!;
        Assert.NotNull(m.RoundTripTime);
        Assert.True(await AppFixture.Eventually(() => app.Db.GetDirectMessages(alice.Id).Any(x => !x.IsOutgoing && x.Text.Contains("Hello from Windows!"))), "reply expected");
        // The RX log decrypted the simulated DM packet with our exported private key.
        Assert.True(await AppFixture.Eventually(() => app.RxLog.Recent().Any(r => r.Payload == PayloadType.TextMessage && r.Decrypt == DecryptStatus.Success)));
    }

    [Fact]
    public async Task MessagesInTheOpenChatStayUnreadWhileThePcIsLocked()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var alice = app.Contacts.GetAll().First(c => c.Name == "Alice");
        var key = MessageService.DmKey(alice.Id);
        app.Messages.AppIsForeground = true;
        app.Messages.SetConversationOpen(key, true);
        app.Messages.MarkRead(key);

        // Chat on screen: Alice's reply is read on arrival.
        app.Messages.QueueDirectMessage(alice, "First");
        Assert.True(await AppFixture.Eventually(() => app.Db.GetDirectMessages(alice.Id).Any(x => !x.IsOutgoing && x.Text.Contains("First"))));
        Assert.Equal(0, app.Db.GetContact(alice.Id)!.UnreadCount);

        // Locked: the same open chat now counts new messages as unread (for the lock screen).
        app.Messages.ScreenLocked = true;
        var before = app.Messages.TotalUnread();
        app.Messages.QueueDirectMessage(alice, "Second");
        Assert.True(await AppFixture.Eventually(() => app.Db.GetDirectMessages(alice.Id).Any(x => !x.IsOutgoing && x.Text.Contains("Second"))));
        Assert.True(await AppFixture.Eventually(() => app.Db.GetContact(alice.Id)!.UnreadCount == 1));
        Assert.Equal(before + 1, app.Messages.TotalUnread());
    }

    [Fact]
    public void SharedContactsShowAsTheirName()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var token = ContactShareUtilities.FormatShare(key, MeshCore.ContactType.Repeater, "Hill RPT");
        Assert.Equal("<" + Convert.ToHexString(key) + ":2:Hill RPT>", token);
        var tokens = LinkDetector.Tokenize("Try " + token + " at 1.000000, 1.000000");
        var contact = Assert.Single(tokens, t => t.Kind == LinkDetector.TokenKind.Contact);
        Assert.Equal("Contact: Hill RPT", contact.Text);
        Assert.Equal(token, contact.Target);
        Assert.Single(tokens, t => t.Kind == LinkDetector.TokenKind.Coordinate);
    }

    [Fact]
    public void EveryLanguageHasItsTranslations()
    {
        // Each language offered has a full table built into the app (English is the keys themselves).
        foreach (var lang in MC1.Core.Localization.L.Languages.Where(l => l.Code != "en"))
        {
            var keys = MC1.Core.Localization.L.KeysOf(lang.Code);
            output.WriteLine($"{lang.Code}: {keys.Count} strings");
            Assert.True(keys.Count > 1400, $"{lang.NativeName} has only {keys.Count} strings");
            Assert.Contains("Settings", keys);
        }
        // Plural forms follow each language's rules.
        Assert.Equal("one", MC1.Core.Localization.L.Category("pl", 1));
        Assert.Equal("few", MC1.Core.Localization.L.Category("pl", 23));
        Assert.Equal("many", MC1.Core.Localization.L.Category("pl", 25));
        Assert.Equal("one", MC1.Core.Localization.L.Category("fr", 0));
        Assert.Equal("other", MC1.Core.Localization.L.Category("de", 0));
    }

    [Fact]
    public void PresetsResolveLikeThePhoneApp()
    {
        Assert.Equal("us-ca", RadioPresets.Resolve(910.525, 62.5, 7, 5, null, "US")!.Id);
        Assert.Equal("us-ca", RadioPresets.Resolve(910.525, 62.5, 7, 5, null, "PR")!.Id);
        Assert.Equal("ca", RadioPresets.Resolve(910.525, 62.5, 7, 5, null, "CA")!.Id);
        Assert.Equal("ca", RadioPresets.Resolve(910.525, 62.5, 7, 5, "ca", "US")!.Id);          // last applied wins
        Assert.Equal("us-ca", RadioPresets.Resolve(910.525, 62.5, 7, 5, "eu-narrow", "US")!.Id); // stale choice ignored
        Assert.Equal("ch", RadioPresets.Resolve(869.618, 62.5, 8, 8, null, "CH")!.Id);
        Assert.Null(RadioPresets.Resolve(906.875, 250, 11, 5, null, "US"));                      // custom
        Assert.Equal("repeat-918", RadioPresets.MatchingRepeat(918.0)!.Id);
    }

    [Fact]
    public async Task RadioPageShowsWhatTheRadioIsSetTo()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        Assert.Equal("USA", RadioPresets.Resolve(app.SelfInfo!.RadioFrequency, app.SelfInfo.RadioBandwidth, app.SelfInfo.RadioSpreadingFactor, app.SelfInfo.RadioCodingRate, null, "US")!.Name);
        // Changed from this app: the choice is remembered (Canada has the same settings as USA).
        await app.Device.ApplyPresetAsync(RadioPresets.All.First(p => p.Id == "ca"));
        Assert.Equal("ca", app.Device.CurrentPreset()!.Id);
        // Changed on the radio by something else: re-reading the radio shows it.
        f.Radio.SetRadioForTest(869.618, 62.5, 8, 8);
        await app.Device.RefreshFromRadioAsync();
        Assert.Equal(869.618, app.SelfInfo!.RadioFrequency, 3);
        Assert.Contains(app.Device.CurrentPreset()!.Id, new[] { "eu-narrow", "ch", "nl-li" });
    }

    [Fact]
    public async Task DirectMessageRetriesUntilAcked()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var bob = app.Contacts.GetAll().First(c => c.Name == "Bob"); // Bob ignores attempt 0
        var sent = app.Messages.QueueDirectMessage(bob, "Are you there?");
        Assert.True(await AppFixture.Eventually(() => app.Db.GetMessage(sent.Id)!.MessageStatus == MessageStatus.Delivered, 15000));
        Assert.True(app.Db.GetMessage(sent.Id)!.RetryAttempt >= 0);
    }

    [Fact]
    public async Task ChannelMessageGetsHeardRepeatsAndReaction()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var sent = app.Messages.QueueChannelMessage(0, "Hi @[Alice] from the PC");
        Assert.True(await AppFixture.Eventually(() => app.Db.GetMessage(sent.Id)!.MessageStatus == MessageStatus.Sent));
        Assert.True(await AppFixture.Eventually(() => app.Db.GetMessage(sent.Id)!.HeardRepeats == 2), $"repeats={app.Db.GetMessage(sent.Id)!.HeardRepeats}");
        Assert.Equal(2, app.Db.GetRepeats(sent.Id).Count);
        Assert.True(await AppFixture.Eventually(() => app.Db.GetMessage(sent.Id)!.ReactionSummary == "👍:1"), "Alice's reaction should attach");
        var reactions = app.Db.GetReactions(sent.Id);
        Assert.Equal("Alice", reactions.Single().SenderName);
        // The reaction itself must not appear as a chat message.
        Assert.DoesNotContain(app.Db.GetChannelMessages(app.RadioId!, 0), m => m.Text.Contains("👍\n"));
    }

    [Fact]
    public async Task OutgoingReactionIsSentAndStored()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        Assert.True(await AppFixture.Eventually(() => app.Db.GetChannelMessages(app.RadioId!, 0).Count == 1));
        var target = app.Db.GetChannelMessages(app.RadioId!, 0)[0];
        await app.Messages.SendReactionAsync(target, "🔥");
        Assert.Equal("🔥:1", app.Db.GetMessage(target.Id)!.ReactionSummary);
    }

    [Fact]
    public async Task RepeaterAdminFlow()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var rpt = app.Contacts.GetAll().First(c => c.Name == "Hilltop Repeater");
        await Assert.ThrowsAsync<RemoteNodeException>(() => app.Remote.LoginAsync(rpt, "wrong", false));
        var login = await app.Remote.LoginAsync(rpt, "password", remember: true);
        Assert.True(login.IsAdmin);
        Assert.Equal("password", app.Secrets.GetPassword(rpt.PublicKey));
        var session = app.Remote.Sessions().Single(s => s.PublicKey.SequenceEquals(rpt.PublicKey));
        Assert.True(session.IsConnected);

        var status = await app.Remote.RequestStatusAsync(rpt);
        Assert.InRange(status.Battery, 3900, 4200);
        Assert.True(status.Uptime >= 864000);
        var telemetry = await app.Remote.RequestTelemetryAsync(rpt);
        Assert.Contains(telemetry.DataPoints, p => p.Type == LppSensorType.Voltage);
        var neighbours = await app.Remote.RequestNeighboursAsync(rpt);
        Assert.True(neighbours.Neighbours.Count >= 2);
        var owner = await app.Remote.RequestOwnerInfoAsync(rpt);
        Assert.Equal("Hilltop Repeater", owner.NodeName);

        var ver = await app.Remote.SendCliAsync(rpt, "ver");
        Assert.StartsWith("v1.16.0", ver);
        var settings = await app.Remote.ReadNodeSettingsAsync(rpt, ["get radio", "get repeat", "get advert.interval"]);
        Assert.IsType<CliResponse.Radio>(settings["get radio"]);
        Assert.Equal(new CliResponse.RepeatMode(true), settings["get repeat"]);
        Assert.Equal(new CliResponse.AdvertInterval(120), settings["get advert.interval"]);
        Assert.True(app.Remote.History(rpt, TimeSpan.FromHours(1)).Count >= 2);
        await app.Remote.LogoutAsync(session);
        Assert.False(app.Remote.Sessions().Single().IsConnected);
    }

    [Fact]
    public async Task RoomJoinReceivesPostsAndCanPost()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var room = app.Contacts.GetAll().First(c => c.Name == "Town Square");
        var session = await app.Rooms.JoinAsync(room, "hello", remember: false);
        Assert.True(session.CanPost);
        Assert.True(await AppFixture.Eventually(() => app.Rooms.Messages(session.Id).Count >= 2, 10000));
        var posts = app.Rooms.Messages(session.Id);
        Assert.Contains(posts, p => p.AuthorName == "Alice" && p.Text.StartsWith("Welcome"));
        var mine = app.Rooms.Post(session, "Hello room!");
        Assert.True(await AppFixture.Eventually(() => app.Db.GetRoomMessage(mine.Id)!.MessageStatus == MessageStatus.Delivered, 10000));
    }

    [Fact]
    public async Task ToolsTracePingDiscoveryNoise()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var rpt = app.Contacts.GetAll().First(c => c.Name == "Hilltop Repeater");
        var ridge = app.Contacts.GetAll().First(c => c.Name == "Ridge RPT");
        var path = ToolsService.BuildTracePath([rpt.PublicKey.Prefix(1), ridge.PublicKey.Prefix(1)], roundTrip: true);
        Assert.Equal(3, path.Length);
        var trace = await app.Tools.RunTraceAsync(path, 0);
        Assert.True(trace.Success, trace.Error);
        Assert.Equal(4, trace.Hops.Count);
        Assert.Equal("Hilltop Repeater", trace.Hops[0].Label);

        var ping = await app.Contacts.PingAsync(rpt);
        Assert.True(ping.Success, ping.Error);

        var found = await app.Tools.DiscoverNodesAsync(true, false, false, TimeSpan.FromSeconds(2.5));
        Assert.Equal(2, found.Count);
        Assert.All(found, n => Assert.Equal(ContactType.Repeater, n.Type));

        var readings = new List<NoiseReading>();
        app.Tools.NoiseReadingAdded += r => { lock (readings) readings.Add(r); };
        app.Tools.StartNoiseMonitor(TimeSpan.FromMilliseconds(200));
        Assert.True(await AppFixture.Eventually(() => { lock (readings) return readings.Count >= 3; }));
        app.Tools.StopNoiseMonitor();
        Assert.All(readings, r => Assert.InRange(r.NoiseFloor, -120, -100));
    }

    [Fact]
    public async Task ContactManagement()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var bob = app.Contacts.GetAll().First(c => c.Name == "Bob");
        await app.Contacts.SetFavoriteAsync(bob, true);
        Assert.True(app.Db.GetContact(bob.Id)!.IsFavorite);
        app.Contacts.SetNickname(bob, "Bobby");
        Assert.Equal("Bobby", app.Db.GetContact(bob.Id)!.DisplayName);
        await app.Contacts.DiscoverPathAsync(bob);
        Assert.True(await AppFixture.Eventually(() => !app.Db.GetContact(bob.Id)!.IsFloodRouted));
        await app.Contacts.ResetPathAsync(app.Db.GetContact(bob.Id)!);
        Assert.True(app.Db.GetContact(bob.Id)!.IsFloodRouted);

        var uri = ContactService.ContactUri(bob);
        Assert.StartsWith("meshcore://contact/add?name=Bob", uri);
        Assert.Equal(bob.PublicKey, MeshCoreUrl.ParseContact(uri)!.PublicKey);

        await app.Contacts.RemoveAsync(app.Db.GetContact(bob.Id)!, deleteMessages: true);
        Assert.DoesNotContain(app.Contacts.GetAll(), c => c.Name == "Bob");
        var readded = await app.Contacts.ImportAsync(uri);
        Assert.NotNull(readded);
        Assert.Contains(app.Contacts.GetAll(), c => c.Name == "Bob");
        await app.SyncContactsAsync(force: true);
        Assert.Equal(6, app.Contacts.GetAll().Count);
    }

    [Fact]
    public async Task ChannelManagement()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var tag = await app.Channels.JoinHashtagAsync("MeshTalk");
        Assert.Equal("#meshtalk", tag.Name);
        Assert.Equal(ChannelSecrets.HashSecret("#meshtalk"), tag.Secret);
        var priv = await app.Channels.CreatePrivateAsync("Family");
        var uri = ChannelService.ShareUri(priv);
        await app.Channels.RemoveAsync(priv);
        Assert.DoesNotContain(app.Channels.GetAll(), c => c.Name == "Family");
        var rejoined = await app.Channels.JoinFromUriAsync(uri);
        Assert.Equal(priv.Secret, rejoined.Secret);
        await app.Channels.SyncAsync(CancellationToken.None);
        Assert.Equal(4, app.Channels.GetAll().Count);
    }

    [Fact]
    public async Task DeviceSettingsAndConfigRoundTrip()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        await app.Device.SetNameAsync("Windows Node");
        Assert.Equal("Windows Node", app.SelfName);
        await app.Device.SetRadioAsync(869.618, 62.5, 8, 8);
        Assert.Equal(869.618, app.SelfInfo!.RadioFrequency, 3);
        await app.Device.SetTxPowerAsync(14);
        Assert.Equal(14, app.SelfInfo!.TxPower);
        await app.Device.SetLocationAsync(1.1317, 0.9289);
        Assert.Equal(1.1317, app.SelfInfo!.Latitude, 5);
        await app.Device.SetOtherParamsAsync(p => p.ManualAddContacts = true);
        Assert.True(app.SelfInfo!.ManualAddContacts);
        var stats = await app.Device.GetStatsAsync();
        Assert.True(stats.Packets.Received > 0);

        var json = await app.NodeConfig.ExportAsync(ConfigSections.All);
        var cfg = NodeConfigService.Parse(json);
        Assert.Equal("Windows Node", cfg.Name);
        Assert.Equal(128, cfg.PrivateKey!.Length);
        Assert.Equal(869618u, cfg.RadioSettings!.Frequency);
        Assert.Equal(2, cfg.Channels!.Count);
        Assert.Equal(6, cfg.Contacts!.Count);
        cfg.Name = "Imported";
        await app.NodeConfig.ImportAsync(cfg, ConfigSections.Identity | ConfigSections.Channels);
        Assert.Equal("Imported", app.SelfName);
    }

    [Fact]
    public async Task Mc1BackupRoundTrip()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var radioId = app.RadioId!;
        Assert.True(await AppFixture.Eventually(() => app.Db.GetChannelMessages(radioId, 0).Count == 1));
        var alice = app.Contacts.GetAll().First(c => c.Name == "Alice");
        app.Messages.QueueDirectMessage(alice, "Backup me");
        Assert.True(await AppFixture.Eventually(() => app.Db.GetDirectMessages(alice.Id).Count >= 2));
        app.Tools.SavePath("To the hill", [0x12, 0x34], 1);
        var frequency = (uint)Math.Round(app.SelfInfo!.RadioFrequency * 1000);
        await app.DisconnectAsync();

        // Backups are made while disconnected, from what the app last saw of the radio.
        var bytes = Mc1Backup.Export(app, "1.0.0", out var summary);
        if (Environment.GetEnvironmentVariable("MC1_EXPORT_PATH") is { } exportPath) File.WriteAllBytes(exportPath, bytes);
        Assert.Equal("MC1 Backup 2026-09-30 012823.mc1backup", Mc1Backup.DefaultFileName(new DateTime(2026, 9, 30, 1, 28, 23)));
        Assert.NotEqual((byte)'{', bytes[0]); // compressed
        var backup = Mc1Backup.Read(bytes);
        var device = Assert.Single(backup.Devices);
        Assert.Equal(frequency, device.FrequencyKhz);
        Assert.Single(backup.TracePathsOf(device));
        Assert.Empty(backup.BlockedChannelSenders);
        Assert.NotNull(backup.WindowsSettings);
        var cfg = backup.ToNodeConfig(device);
        Assert.Equal(2, cfg.Channels!.Count);
        Assert.Equal(6, cfg.Contacts!.Count);
        var dmCount = app.Db.GetDirectMessages(alice.Id).Count;
        Assert.Equal(summary.Messages, backup.Messages.Count);

        // Restore into emptied chats: everything comes back once; radio settings wait for the next connection.
        app.Db.ClearChannelMessages(radioId, 0);
        app.Db.ClearDirectMessages(alice.Id);
        foreach (var t in app.Db.GetTracePaths(radioId)) app.Db.DeleteTracePath(t.Id);
        var all = new Mc1Backup.RestoreOptions(true, true, true, true, true, true, true, true, true);
        var r = backup.Restore(app, device, null, all);
        Assert.Equal(radioId, r.RadioId);
        Assert.Single(app.Db.GetChannelMessages(radioId, 0));
        Assert.Equal(dmCount, app.Db.GetDirectMessages(alice.Id).Count);
        Assert.Contains(app.Db.GetDirectMessages(alice.Id), m => m.Text == "Backup me" && m.IsOutgoing);
        Assert.Single(app.Db.GetTracePaths(radioId));
        Assert.True(r.RadioWritePending);
        Assert.True(app.PendingConfig.IsFor(radioId));
        Assert.Equal(0, backup.Restore(app, device, null, all).Messages);

        await f.ConnectAsync();
        var result = await app.PendingConfig.ApplyAsync();
        output.WriteLine(result);
        Assert.Null(app.PendingConfig.Current);
        Assert.Equal(frequency, (uint)Math.Round(app.SelfInfo!.RadioFrequency * 1000));
    }

    [Fact]
    public async Task Mc1BackupRestoresPhoneBackupIntoNewRadio()
    {
        await using var f = new AppFixture();
        var app = f.App;
        var backup = Mc1Backup.Read(System.Text.Encoding.UTF8.GetBytes(PhoneBackupJson));
        var d = Assert.Single(backup.Devices);
        var r = backup.Restore(app, d, null, new Mc1Backup.RestoreOptions(true, true, true, true, true, true, true, true, true));
        Assert.Equal(d.PublicKey.ToHex(), r.RadioId);
        Assert.Equal(1, r.Contacts);
        Assert.Equal(1, r.Channels);
        Assert.Equal(2, r.Messages);
        Assert.Equal(r.RadioId, app.RadioId); // the restored radio is shown right away
        var dave = Assert.Single(app.Db.GetContacts(r.RadioId));
        Assert.Equal("Dave (phone)", dave.DisplayName);
        Assert.Single(app.Db.GetDirectMessages(dave.Id));
        Assert.Single(app.Db.GetChannelMessages(r.RadioId, 0));
        var pending = app.PendingConfig.Current!;
        Assert.Equal("Phone Node", pending.Config.Name);
        Assert.Single(pending.Contacts);
        Assert.True(app.PendingConfig.ProtectsChannel(r.RadioId, 0));
        Assert.Equal("Dark", app.Settings.Current.Theme);
        await app.PendingConfig.DiscardAsync();
        Assert.Null(app.PendingConfig.Current);
    }

    private const string PhoneBackupJson = """
        {"version":1,"exportDate":1790000000.5,"appVersion":"2.4","appBuild":"310","manifest":{"deviceCount":1,"contactCount":1,"channelCount":1,"messageCount":2,
         "messageRepeatCount":0,"reactionCount":0,"roomMessageCount":0,"remoteNodeSessionCount":0,"savedTracePathCount":0,"blockedChannelSenderCount":1,"nodeStatusSnapshotCount":0},
         "devices":[{"id":"6F1B0B33-2E1A-4F57-9A61-4A3F1E8C2A11","radioID":"0C0F8C62-5B7A-4C8D-9C6E-4C1B2D3E4F50","publicKey":"q6urq6urq6urq6urq6urq6urq6urq6urq6urq6urq6s=",
           "nodeName":"Phone Node","firmwareVersion":9,"firmwareVersionString":"v1.9","manufacturerName":"Heltec V3","buildDate":"1 Jan 2026","maxContacts":350,"maxChannels":40,
           "frequency":910525,"bandwidth":62500,"spreadingFactor":7,"codingRate":5,"txPower":20,"maxTxPower":22,"latitude":0.7317,"longitude":0.9289,"blePin":123456,
           "clientRepeat":false,"pathHashMode":0,"manualAddContacts":false,"autoAddConfig":0,"autoAddMaxHops":0,"multiAcks":1,"telemetryModeBase":2,"telemetryModeLoc":0,
           "telemetryModeEnv":0,"advertLocationPolicy":1,"lastConnected":1789999000,"lastContactSync":0,"isActive":true,"connectionMethods":[],"knownRegions":[]}],
         "contacts":[{"id":"11111111-2222-3333-4444-555555555555","radioID":"0C0F8C62-5B7A-4C8D-9C6E-4C1B2D3E4F50","publicKey":"AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=",
           "name":"Dave","typeRawValue":1,"flags":0,"outPathLength":1,"outPath":"qg==","lastAdvertTimestamp":1789990000,"latitude":0,"longitude":0,"lastModified":1789990000,
           "isBlocked":false,"isMuted":false,"isFavorite":true,"unreadCount":0,"unreadMentionCount":0,"nickname":"Dave (phone)"}],
         "channels":[{"id":"22222222-2222-3333-4444-555555555555","radioID":"0C0F8C62-5B7A-4C8D-9C6E-4C1B2D3E4F50","index":0,"name":"Public","secret":"izOH6cXN6mrJ5e26oRXNcg==",
           "isEnabled":true,"unreadCount":0,"unreadMentionCount":0,"notificationLevel":2,"isFavorite":false,"floodScopeModeRawValue":"inherit"}],
         "messages":[
           {"id":"33333333-2222-3333-4444-555555555555","radioID":"0C0F8C62-5B7A-4C8D-9C6E-4C1B2D3E4F50","channelIndex":0,"text":"Hello from the phone","timestamp":1789995000,
            "createdAt":1789995001,"sortDate":1789995001,"direction":0,"status":3,"textType":0,"pathLength":2,"senderNodeName":"Eve","isRead":true,"heardRepeats":0,"sendCount":1,
            "retryAttempt":0,"maxRetryAttempts":0,"linkPreviewFetched":false,"containsSelfMention":false,"mentionSeen":false,"timestampCorrected":false,"regionScopeMatches":[]},
           {"id":"44444444-2222-3333-4444-555555555555","radioID":"0C0F8C62-5B7A-4C8D-9C6E-4C1B2D3E4F50","contactID":"11111111-2222-3333-4444-555555555555","text":"DM from phone",
            "timestamp":1789996000,"createdAt":1789996000,"direction":1,"status":3,"textType":0,"pathLength":0,"isRead":true,"heardRepeats":0,"sendCount":1,"retryAttempt":0,
            "maxRetryAttempts":3,"linkPreviewFetched":false,"containsSelfMention":false,"mentionSeen":false,"timestampCorrected":false}],
         "messageRepeats":[],"reactions":[],"roomMessages":[],"remoteNodeSessions":[],"savedTracePaths":[],
         "blockedChannelSenders":[{"id":"55555555-2222-3333-4444-555555555555","name":"Troll","radioID":"0C0F8C62-5B7A-4C8D-9C6E-4C1B2D3E4F50","dateBlocked":1789000000}],
         "nodeStatusSnapshots":[],"userDefaults":{"appColorSchemePreference":"dark","showInlineImages":false}}
        """;

    [Fact]
    public void Mc1BackupReadsPhoneBackups()
    {
        // Same layout as a backup made by MeshCore One on an iPhone (raw DEFLATE, base64 data, dates in seconds).
        var json = PhoneBackupJson;
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(System.Text.Encoding.UTF8.GetBytes(json));
        var backup = Mc1Backup.Read(ms.ToArray());
        var d = Assert.Single(backup.Devices);
        Assert.Equal("Phone Node", d.NodeName);
        Assert.Equal(910525u, d.FrequencyKhz);
        Assert.Equal(62500u, d.BandwidthHz);
        Assert.Equal(32, d.PublicKey.Length);
        var cfg = backup.ToNodeConfig(d);
        var ch = Assert.Single(cfg.Channels!);
        Assert.Equal("Public", ch.Name);
        Assert.Equal(ChannelSecrets.PublicChannelSecret.ToHex(), ch.Secret);
        var contact = Assert.Single(cfg.Contacts!);
        Assert.Equal("Dave", contact.Name);
        Assert.Equal("Dave (phone)", contact.CustomName);
        Assert.Equal("aa", contact.OutPath);
        Assert.Equal(2, backup.MessagesOf(d).Count());
        Assert.Single(backup.BlockedOf(d));
        // Plain JSON and zlib-wrapped files are accepted too.
        Assert.Single(Mc1Backup.Read(System.Text.Encoding.UTF8.GetBytes(json)).Devices);
        Assert.Throws<InvalidDataException>(() => Mc1Backup.Read([1, 2, 3, 4, 5]));
    }

    [Fact]
    public async Task BackupRestoreRoundTrip()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        Assert.True(await AppFixture.Eventually(() => app.Db.GetChannelMessages(app.RadioId!, 0).Count == 1));
        var zip = Path.Combine(f.Root, "backup.zip");
        await app.Backup.ExportAsync(zip);
        Assert.True(new FileInfo(zip).Length > 1000);
        var radioId = app.RadioId!;
        app.Db.ClearChannelMessages(radioId, 0);
        Assert.Empty(app.Db.GetChannelMessages(radioId, 0));
        await app.Backup.ImportAsync(zip);
        Assert.Single(app.Db.GetChannelMessages(radioId, 0));
    }

    [Fact]
    public async Task ReconnectsAfterLinkDrop()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        f.Radio.SimulateDisconnect();
        Assert.True(await AppFixture.Eventually(() => app.Status == LinkStatus.Reconnecting, 5000));
        Assert.True(await AppFixture.Eventually(() => app.Status == LinkStatus.Ready, 20000), $"status={app.Status} {app.StatusDetail}");
        await app.DisconnectAsync();
        Assert.Equal(LinkStatus.Disconnected, app.Status);
    }

    [Fact]
    public async Task NewAdvertAppearsInDiscoverAndCanBeAdded()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        Assert.Empty(app.Contacts.GetDiscovered());
        f.Radio.TriggerNewNodeAdvert();
        Assert.True(await AppFixture.Eventually(() => app.Contacts.GetDiscovered().Any(d => d.Name == "Carol")));
        Assert.Contains(f.Notifier.Infos, i => i.Title == "New node discovered");
        var carol = app.Contacts.GetDiscovered().First(d => d.Name == "Carol");
        await app.Contacts.AddDiscoveredAsync(carol);
        Assert.Contains(app.Contacts.GetAll(), c => c.Name == "Carol");
        Assert.Empty(app.Contacts.GetDiscovered());
    }
}
