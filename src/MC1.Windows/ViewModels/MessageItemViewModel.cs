using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MC1.Core.Models;
using MC1.Core.Utilities;
using MC1.Windows.Services;
using MeshCore;

namespace MC1.Windows.ViewModels;

public sealed record ReactionChip(string Emoji, int Count)
{
    public string Text => Count > 1 ? $"{Emoji} {Count}" : Emoji;
}

public sealed partial class MessageItemViewModel : ObservableObject
{
    private static readonly Regex ReplyRegex = new(@"^@\[([^\]]+)\]\n>(.*)\n", RegexOptions.Compiled);

    public required string Id { get; init; }
    public required ConversationViewModel Conversation { get; init; }
    public MessageRecord? Record { get; private set; }
    public RoomMessageRecord? RoomRecord { get; private set; }

    public string Text { get; private set; } = "";
    public string Body { get; private set; } = "";
    public string? QuoteName { get; private set; }
    public string? QuoteText { get; private set; }
    public bool HasQuote => QuoteName is not null;
    public bool IsOutgoing { get; private set; }
    public string? SenderName { get; private set; }
    public string SenderDisplay { get; private set; } = "";
    public string SenderColor { get; private set; } = "#2463EB";
    public string SenderKey { get; private set; } = "";
    public bool CanShowSender { get; private set; }
    public DateTimeOffset Time { get; private set; }
    /// <summary>When this PC received the message (backlog from the radio can arrive long after it was sent).</summary>
    public long ReceivedAt => Record?.CreatedAt ?? RoomRecord?.CreatedAt ?? 0;
    public string TimeText => Formatters.Clock(Time);
    public bool IsMention { get; private set; }
    public string? MapUri { get; private set; }
    public string? FirstUrl { get; private set; }
    public bool IsImageUrl { get; private set; }

    [ObservableProperty] private string? _dayHeader;
    [ObservableProperty] private bool _showSender;
    /// <summary>Shows the "New messages" line above this message (the first one not yet seen).</summary>
    [ObservableProperty] private bool _showNewDivider;
    [ObservableProperty] private string _statusIcon = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isPending;
    [ObservableProperty] private bool _isDelivered;
    [ObservableProperty] private string? _metaText;
    /// <summary>Your channel message was repeated this many times (chip with the "repeat" arrows, like iOS).</summary>
    [ObservableProperty] private int _repeatCount;
    /// <summary>Hops a received flood message took (chip with the arrow hopping off the ground, like iOS).</summary>
    [ObservableProperty] private int? _hopCount;
    /// <summary>How many times a received channel message was heard (chip with the ear, like iOS).</summary>
    [ObservableProperty] private int _heardCount;
    [ObservableProperty] private string? _linkTitle;
    [ObservableProperty] private string? _linkHost;
    [ObservableProperty] private Bitmap? _linkImage;
    [ObservableProperty] private bool _hasLinkPreview;
    [ObservableProperty] private Bitmap? _inlineImage;

    public ObservableCollection<ReactionChip> Reactions { get; } = new();
    public bool HasReactions => Reactions.Count > 0;
    public bool HasDayHeader => DayHeader is not null;
    public bool CanResend => IsOutgoing && (IsFailed || Record is { Status: (int)MessageStatus.Sent });
    public bool IsChannelIncoming => Record?.ChannelIndex is not null && !IsOutgoing;
    public bool HasMap => MapUri is not null && (AppHost.Core?.Settings.Current.ShowMapPreviews ?? true);
    public double MapLat { get; private set; }
    public double MapLon { get; private set; }
    public IReadOnlyList<MC1.Windows.Controls.MapMarker> MapMarkers { get; private set; } = [];
    public bool HasStatus => StatusText.Length > 0;
    public bool HasRepeatChip => RepeatCount > 0;
    public string RepeatTip => L.Plural(RepeatCount, "Heard {0} repeat", "Heard {0} repeats");
    public bool HasHopChip => HopCount is not null;
    public string HopTip => HopCount switch { 0 => L.T("Received directly (0 hops)"), var n => L.Plural(n ?? 0, "{0} hop", "{0} hops") };
    public bool HasHeardChip => HeardCount > 0;
    public string HeardTip => L.Plural(HeardCount, "Heard once", "Heard {0} times");
    public bool HasChips => HasRepeatChip || HasHopChip || HasHeardChip;
    public bool HasMeta => MetaText is not null;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void React(string emoji) => Conversation.ReactCommand.Execute(new ReactionRequest(this, emoji));
    public IReadOnlyList<LinkDetector.Token> Tokens { get; private set; } = [];

    partial void OnDayHeaderChanged(string? value) => OnPropertyChanged(nameof(HasDayHeader));
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnRepeatCountChanged(int value) { OnPropertyChanged(nameof(HasRepeatChip)); OnPropertyChanged(nameof(RepeatTip)); OnPropertyChanged(nameof(HasChips)); }
    partial void OnHopCountChanged(int? value) { OnPropertyChanged(nameof(HasHopChip)); OnPropertyChanged(nameof(HopTip)); OnPropertyChanged(nameof(HasChips)); }
    partial void OnHeardCountChanged(int value) { OnPropertyChanged(nameof(HasHeardChip)); OnPropertyChanged(nameof(HeardTip)); OnPropertyChanged(nameof(HasChips)); }
    partial void OnMetaTextChanged(string? value) => OnPropertyChanged(nameof(HasMeta));

    public static MessageItemViewModel From(MessageRecord m, ConversationViewModel conv)
    {
        var vm = new MessageItemViewModel { Id = m.Id, Conversation = conv };
        vm.Apply(m);
        return vm;
    }

    public static MessageItemViewModel From(RoomMessageRecord m, ConversationViewModel conv)
    {
        var vm = new MessageItemViewModel { Id = m.Id, Conversation = conv };
        vm.Apply(m);
        return vm;
    }

    public void UpdateFrom(MessageItemViewModel other)
    {
        if (other.Record is { } r) Apply(r);
        else if (other.RoomRecord is { } rr) Apply(rr);
        DayHeader = other.DayHeader;
        ShowSender = other.ShowSender;
        ShowNewDivider = other.ShowNewDivider;
    }

    private void SetText(string text)
    {
        if (text == Text && Tokens.Count > 0) return;
        Text = text;
        var match = ReplyRegex.Match(text);
        if (match.Success)
        {
            QuoteName = match.Groups[1].Value;
            QuoteText = match.Groups[2].Value;
            Body = text[match.Length..];
        }
        else Body = text;
        Tokens = LinkDetector.Tokenize(Body);
        MapUri = Tokens.FirstOrDefault(t => t.Kind == LinkDetector.TokenKind.Coordinate)?.Target;
        if (MapUri is not null && MeshCoreUrl.ParseMap(MapUri) is { } pos)
        {
            MapLat = pos.Lat;
            MapLon = pos.Lon;
            MapMarkers = [new MC1.Windows.Controls.MapMarker { Id = "pin", Latitude = pos.Lat, Longitude = pos.Lon, Label = "", Kind = MC1.Windows.Controls.MapMarkerKind.Pin }];
        }
        else MapUri = null;
        FirstUrl = Tokens.FirstOrDefault(t => t.Kind == LinkDetector.TokenKind.Url)?.Target;
        IsImageUrl = FirstUrl is not null && LinkDetector.IsImageUrl(FirstUrl);
    }

    private void Apply(MessageRecord m)
    {
        var first = Record is null;
        Record = m;
        SetText(m.Text);
        IsOutgoing = m.IsOutgoing;
        var core = AppHost.Core;
        SenderName = m.SenderName;
        SenderDisplay = m.IsOutgoing ? core.SelfName : m.SenderName ?? Conversation.Contact?.DisplayName ?? "";
        SenderKey = m.IsOutgoing ? "self" : m.SenderName ?? m.ContactId ?? "";
        SenderColor = Formatters.ColorFor(SenderKey);
        CanShowSender = m.ChannelIndex is not null && !m.IsOutgoing;
        Time = m.Date;
        IsMention = m.ContainsSelfMention && !m.IsOutgoing;
        var status = m.MessageStatus;
        IsFailed = m.IsOutgoing && status == MessageStatus.Failed;
        IsPending = m.IsOutgoing && status is MessageStatus.Pending or MessageStatus.Sending or MessageStatus.Retrying;
        IsDelivered = m.IsOutgoing && status == MessageStatus.Delivered;
        (StatusIcon, StatusText) = !m.IsOutgoing ? ("", "") : status switch
        {
            MessageStatus.Pending => ("Icon.ClockOutline", L.T("Queued")),
            MessageStatus.Sending => ("Icon.ClockOutline", L.T("Sending…")),
            MessageStatus.Retrying => ("Icon.Refresh", L.F("Retrying {0}/{1}", m.RetryAttempt + 1, Math.Max(1, m.MaxRetryAttempts))),
            MessageStatus.Sent => ("Icon.Check", L.T("Sent")),
            MessageStatus.Delivered => ("Icon.CheckAll", m.RoundTripTime is { } rtt ? L.F("Delivered · {0:0.0}s", rtt / 1000.0) : L.T("Delivered")),
            MessageStatus.Failed => ("Icon.AlertCircleOutline", L.T("Not delivered")),
            _ => ("", ""),
        };
        RepeatCount = m.IsOutgoing && m.ChannelIndex is not null && core.Settings.Current.ShowHeardRepeats ? m.HeardRepeats : 0;
        if (m.SendCount > 1 && m.IsOutgoing) StatusText += " · " + L.F("sent {0}×", m.SendCount);
        var meta = new List<string>();
        HopCount = null;
        HeardCount = 0;
        if (!m.IsOutgoing)
        {
            if (m.Snr is { } snr) meta.Add($"SNR {snr:0.#}");
            // Like the iPhone app: flood-routed messages (all channel messages) show how many hops they took,
            // and channel messages heard more than once show how many times.
            var floodRouted = m.ChannelIndex is not null || m.PathLength != 0xFF;
            if (floodRouted && PathEncoding.Decode((byte)m.PathLength) is { } h) HopCount = h.HopCount;
            if (m.RegionScope is { } region) meta.Add(region);
            if (m.HeardRepeats > 0) HeardCount = 1 + m.HeardRepeats;
        }
        MetaText = meta.Count > 0 ? string.Join(" · ", meta) : null;
        Reactions.Clear();
        foreach (var (emoji, count) in ReactionParser.ParseSummary(m.ReactionSummary)) Reactions.Add(new ReactionChip(emoji, count));
        OnPropertyChanged(nameof(HasReactions));
        OnPropertyChanged(nameof(CanResend));
        if (first) _ = LoadPreviewAsync(m);
    }

    private void Apply(RoomMessageRecord m)
    {
        RoomRecord = m;
        SetText(m.Text);
        IsOutgoing = m.IsFromSelf;
        SenderName = m.AuthorName;
        SenderDisplay = m.IsFromSelf ? AppHost.Core.SelfName : m.AuthorName;
        SenderKey = Convert.ToHexString(m.AuthorKeyPrefix);
        SenderColor = Formatters.ColorFor(SenderKey);
        CanShowSender = !m.IsFromSelf;
        Time = DateTimeOffset.FromUnixTimeSeconds(m.Timestamp);
        var status = m.MessageStatus;
        IsFailed = m.IsFromSelf && status == MessageStatus.Failed;
        IsPending = m.IsFromSelf && status is MessageStatus.Pending or MessageStatus.Sending;
        IsDelivered = m.IsFromSelf && status == MessageStatus.Delivered;
        (StatusIcon, StatusText) = !m.IsFromSelf ? ("", "") : status switch
        {
            MessageStatus.Pending => ("Icon.ClockOutline", L.T("Posting…")),
            MessageStatus.Sent => ("Icon.Check", L.T("Sent")),
            MessageStatus.Delivered => ("Icon.CheckAll", L.T("Posted")),
            MessageStatus.Failed => ("Icon.AlertCircleOutline", L.T("Not delivered")),
            _ => ("", ""),
        };
        OnPropertyChanged(nameof(CanResend));
    }

    private async Task LoadPreviewAsync(MessageRecord m)
    {
        var settings = AppHost.Core.Settings.Current;
        if (FirstUrl is null) return;
        try
        {
            if (IsImageUrl && settings.ShowInlineImages)
            {
                InlineImage = await LinkPreviewService.Instance.LoadImageAsync(LinkDetector.DirectImageUrl(FirstUrl), 360);
                return;
            }
            if (!settings.ShowLinkPreviews) return;
            var preview = await LinkPreviewService.Instance.GetPreviewAsync(m, FirstUrl);
            if (preview is null) return;
            LinkTitle = preview.Title;
            LinkHost = preview.Host;
            LinkImage = preview.Image;
            HasLinkPreview = !string.IsNullOrEmpty(preview.Title) || preview.Image is not null;
        }
        catch { /* previews are best-effort */ }
    }
}
