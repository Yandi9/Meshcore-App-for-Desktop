using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MC1.Windows.Services;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Views;

public partial class ConversationView : UserControl
{
    private ConversationViewModel? _vm;
    private bool _stickToBottom = true;

    public ConversationView()
    {
        InitializeComponent();
        Composer.AddHandler(KeyDownEvent, Composer_OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Scroller.ScrollChanged += (_, e) =>
        {
            var distance = Scroller.Extent.Height - Scroller.Viewport.Height - Scroller.Offset.Y;
            // Content grew (new message laid out, picture loaded) while following the end: keep following it.
            if (_stickToBottom && e.OffsetDelta.Y == 0 && (e.ExtentDelta.Y > 0 || e.ViewportDelta.Y != 0))
            {
                if (distance > 1) Scroller.ScrollToEnd();
                return;
            }
            _stickToBottom = distance < 80;
            JumpButton.IsVisible = distance > 400;
        };
        Scroller.SizeChanged += (_, _) => { if (_stickToBottom) ScrollToEnd(); };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.MessagesAppended -= OnMessagesAppended;
            _vm.FocusComposerRequested -= FocusComposer;
            _vm.DividerPlaced -= ScrollToDivider;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = DataContext as ConversationViewModel;
        ApplyAppearance();
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.MessagesAppended += OnMessagesAppended;
        _vm.FocusComposerRequested += FocusComposer;
        _vm.DividerPlaced += ScrollToDivider;
        _stickToBottom = true;
        if (_vm.HasNewDivider) ScrollToDivider();
        else ScrollToEnd();
        Dispatcher.UIThread.Post(FocusComposer, DispatcherPriority.Background);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs pe)
    {
        if (pe.PropertyName == nameof(ConversationViewModel.Appearance)) ApplyAppearance();
    }

    /// <summary>Scrolls so the "New messages" line sits near the top (or to the end if everything new fits).</summary>
    private void ScrollToDivider()
    {
        void Go()
        {
            if (_vm is null) return;
            var index = -1;
            for (var i = 0; i < _vm.Messages.Count; i++)
                if (_vm.Messages[i].ShowNewDivider) { index = i; break; }
            if (index < 0 || MessageList.ContainerFromIndex(index) is not Control container || Scroller.Content is not Visual content)
            {
                Scroller.ScrollToEnd();
                return;
            }
            var p = container.TranslatePoint(new Point(0, 0), content);
            if (p is null) { Scroller.ScrollToEnd(); return; }
            var max = Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height);
            var target = Math.Clamp(p.Value.Y - 56, 0, max);
            Scroller.Offset = new Vector(Scroller.Offset.X, target);
            _stickToBottom = target >= max - 1;
            JumpButton.IsVisible = max - target > 400;
        }
        Dispatcher.UIThread.Post(Go, DispatcherPriority.Background);
        Dispatcher.UIThread.Post(Go, DispatcherPriority.ContextIdle);
    }

    private Avalonia.Styling.Styles? _appearanceStyles;

    /// <summary>Applies the chat's bubble and text colours as view-local styles (overriding the app theme).</summary>
    private void ApplyAppearance()
    {
        if (_appearanceStyles is not null) Styles.Remove(_appearanceStyles);
        _appearanceStyles = null;
        var a = _vm?.Appearance;
        if (a is null) return;
        var st = new Avalonia.Styling.Styles();
        Avalonia.Styling.Selector Bubble(bool outgoing) => Avalonia.Styling.Selectors.Class(
            Avalonia.Styling.Selectors.Class(Avalonia.Styling.Selectors.OfType(null, typeof(Border)), "bubble"), outgoing ? "outgoing" : "incoming");
        void Add(Avalonia.Styling.Selector sel, params (AvaloniaProperty Prop, object? Value)[] setters)
        {
            var style = new Avalonia.Styling.Style { Selector = sel };
            foreach (var (prop, value) in setters) style.Setters.Add(new Avalonia.Styling.Setter(prop, value));
            st.Add(style);
        }
        if (a.IncomingBubble is { } ib)
            Add(Avalonia.Styling.Selectors.Not(Bubble(false), Avalonia.Styling.Selectors.Class(null, "mention")), (Border.BackgroundProperty, ib), (Border.BorderBrushProperty, ib));
        if (a.OutgoingBubble is { } ob)
            Add(Bubble(true), (Border.BackgroundProperty, ob));
        if (a.IncomingText is { } it)
        {
            Add(Avalonia.Styling.Selectors.Is(Avalonia.Styling.Selectors.Descendant(Bubble(false)), typeof(TextBlock)), (TextBlock.ForegroundProperty, it));
            Add(Avalonia.Styling.Selectors.Is(Avalonia.Styling.Selectors.Descendant(Bubble(false)), typeof(Controls.MessageText)), (TextBlock.ForegroundProperty, it), (Controls.MessageText.LinkBrushProperty, it));
            Add(Avalonia.Styling.Selectors.OfType(Avalonia.Styling.Selectors.Descendant(Bubble(false)), typeof(PathIcon)), (PathIcon.ForegroundProperty, it));
        }
        if (a.OutgoingText is { } ot)
        {
            Add(Avalonia.Styling.Selectors.Is(Avalonia.Styling.Selectors.Descendant(Bubble(true)), typeof(TextBlock)), (TextBlock.ForegroundProperty, ot));
            Add(Avalonia.Styling.Selectors.Is(Avalonia.Styling.Selectors.Descendant(Bubble(true)), typeof(Controls.MessageText)), (TextBlock.ForegroundProperty, ot), (Controls.MessageText.LinkBrushProperty, ot));
            Add(Avalonia.Styling.Selectors.OfType(Avalonia.Styling.Selectors.Descendant(Bubble(true)), typeof(PathIcon)), (PathIcon.ForegroundProperty, ot));
        }
        if (a.Background is not null || a.HasImage)
        {
            // Name bar, empty-chat text and header buttons sit directly on the background: pick a readable colour.
            var appBg = Application.Current?.TryGetResource("App.Background", Application.Current.ActualThemeVariant, out var bgr) == true && bgr is ISolidColorBrush abg
                ? abg.Color : Colors.White;
            var tone = a.ToneOver(appBg);
            var dark = ChatAppearance.Luminance(tone) < 0.42;
            var headText = new SolidColorBrush(dark ? Color.Parse("#F5F7FA") : Color.Parse("#101722"));
            var headSub = new SolidColorBrush(dark ? Color.Parse("#D5DBE3") : Color.Parse("#3F4A5A"));
            var chathead = Avalonia.Styling.Selectors.Class(Avalonia.Styling.Selectors.OfType(null, typeof(Border)), "chathead");
            Add(Avalonia.Styling.Selectors.OfType(Avalonia.Styling.Selectors.Descendant(chathead), typeof(TextBlock)), (TextBlock.ForegroundProperty, headText));
            Add(Avalonia.Styling.Selectors.Class(Avalonia.Styling.Selectors.OfType(Avalonia.Styling.Selectors.Descendant(chathead), typeof(TextBlock)), "headsub"), (TextBlock.ForegroundProperty, headSub));
            Add(Avalonia.Styling.Selectors.OfType(Avalonia.Styling.Selectors.Descendant(Avalonia.Styling.Selectors.Class(Avalonia.Styling.Selectors.OfType(Avalonia.Styling.Selectors.Descendant(chathead), typeof(Button)), "icon")), typeof(PathIcon)), (PathIcon.ForegroundProperty, headText));
            if (a.HasImage)
            {
                var shadow = new DropShadowEffect { BlurRadius = 8, OffsetX = 0, OffsetY = 1, Color = dark ? Colors.Black : Colors.White, Opacity = 0.7 };
                Add(Avalonia.Styling.Selectors.OfType(Avalonia.Styling.Selectors.Descendant(chathead), typeof(TextBlock)), (Visual.EffectProperty, shadow));
            }
            var empty = Avalonia.Styling.Selectors.Class(Avalonia.Styling.Selectors.OfType(null, typeof(StackPanel)), "emptystate");
            Add(Avalonia.Styling.Selectors.OfType(Avalonia.Styling.Selectors.Descendant(empty), typeof(TextBlock)), (TextBlock.ForegroundProperty, headText));
            Add(Avalonia.Styling.Selectors.OfType(Avalonia.Styling.Selectors.Descendant(empty), typeof(PathIcon)), (PathIcon.ForegroundProperty, headSub));

            // Keep sender names and delivery notes readable on top of a custom background.
            var pill = Application.Current?.TryGetResource("App.Surface", Application.Current.ActualThemeVariant, out var r) == true && r is ISolidColorBrush sb
                ? new SolidColorBrush(sb.Color, 0.88)
                : new SolidColorBrush(Colors.White, 0.88);
            Add(Avalonia.Styling.Selectors.Class(Avalonia.Styling.Selectors.OfType(null, typeof(TextBlock)), "sender"),
                (TextBlock.BackgroundProperty, pill), (TextBlock.PaddingProperty, new Thickness(7, 1)));
            Add(Avalonia.Styling.Selectors.Class(Avalonia.Styling.Selectors.OfType(null, typeof(Border)), "underpill"),
                (Border.BackgroundProperty, pill), (Border.PaddingProperty, new Thickness(7, 1)));
            // "New Messages" label on a pill so it reads on any background.
            Add(Avalonia.Styling.Selectors.Class(Avalonia.Styling.Selectors.OfType(Avalonia.Styling.Selectors.Descendant(
                    Avalonia.Styling.Selectors.Class(Avalonia.Styling.Selectors.OfType(null, typeof(Grid)), "newdivider")), typeof(Border)), "newlabel"),
                (Border.BackgroundProperty, pill));
            Add(Avalonia.Styling.Selectors.Class(Avalonia.Styling.Selectors.OfType(Avalonia.Styling.Selectors.Descendant(
                    Avalonia.Styling.Selectors.Class(Avalonia.Styling.Selectors.OfType(null, typeof(Grid)), "newdivider")), typeof(Border)), "newline"),
                (Visual.OpacityProperty, 0.9));
        }
        _appearanceStyles = st;
        Styles.Add(st);
    }

    private void OnMessagesAppended(bool outgoing)
    {
        if (outgoing || _stickToBottom) ScrollToEnd();
        else JumpButton.IsVisible = true;
    }

    private void ScrollToEnd()
    {
        Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
        Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.ContextIdle);
    }

    private void FocusComposer()
    {
        if (Composer.IsEnabled)
        {
            Composer.Focus();
            Composer.CaretIndex = Composer.Text?.Length ?? 0;
        }
    }

    private void Composer_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Enter)
        {
            var sendOnEnter = AppHost.Core.Settings.Current.SendOnEnter;
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (_vm.ShowMentions && _vm.MentionSuggestions.Count > 0 && !shift)
            {
                _vm.InsertMentionCommand.Execute(_vm.MentionSuggestions[0]);
                e.Handled = true;
                return;
            }
            if ((sendOnEnter && !shift) || (!sendOnEnter && ctrl))
            {
                _vm.SendCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Tab && _vm.ShowMentions && _vm.MentionSuggestions.Count > 0)
        {
            _vm.InsertMentionCommand.Execute(_vm.MentionSuggestions[0]);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_vm.ShowMentions) _vm.ShowMentions = false;
            else if (_vm.ReplyingTo is not null) _vm.CancelReplyCommand.Execute(null);
        }
    }

    private void JumpButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _stickToBottom = true;
        ScrollToEnd();
        JumpButton.IsVisible = false;
    }

    private static T? FindContext<T>(StyledElement? start) where T : class
    {
        for (var e = start; e is not null; e = e.Parent ?? (e as Visual)?.GetVisualParent() as StyledElement)
            if (e.DataContext is T t) return t;
        return null;
    }

    private void ReactEmoji_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => React(sender as Button);

    private void EmojiButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => React(sender as Button);

    private void React(Button? button)
    {
        if (button?.Content is not string emoji) return;
        var message = FindContext<MessageItemViewModel>(button);
        message?.ReactCommand.Execute(emoji);
        CloseFlyouts(button);
    }

    private static void CloseFlyouts(Control start)
    {
        foreach (var popup in start.GetLogicalAncestors().OfType<Avalonia.Controls.Primitives.Popup>()) popup.Close();
        for (var e = start as StyledElement; e is not null; e = e.Parent)
            if (e is Button { Flyout: { } f }) f.Hide();
    }

    // "+" menu: emoji or a picture via MeshPic. Whatever is chosen goes in where the cursor was.
    private int _selStart, _selEnd;

    private void PlusFlyout_OnOpening(object? sender, EventArgs e)
    {
        PlusMenu.IsVisible = true;
        EmojiPanel.IsVisible = false;
        ContactPanel.IsVisible = false;
        PlusStatus.IsVisible = false;
        ShareMyInfoOption.IsEnabled = MyShareToken() is not null;
        var len = Composer.Text?.Length ?? 0;
        _selStart = Math.Clamp(Composer.SelectionStart, 0, len);
        _selEnd = Math.Clamp(Composer.SelectionEnd, 0, len);
        if (_selStart == _selEnd) _selStart = _selEnd = Math.Clamp(Composer.CaretIndex, 0, len);
    }

    private void ShowEmojiPicker_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        PlusMenu.IsVisible = false;
        EmojiPanel.IsVisible = true;
    }

    private void BackToPlusMenu_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        PlusMenu.IsVisible = true;
        EmojiPanel.IsVisible = false;
        ContactPanel.IsVisible = false;
    }

    // Share options, like the iPhone app's "+" menu: location, a contact, or your own contact card.

    private void ShowPlusStatus(string text)
    {
        PlusStatus.Text = text;
        PlusStatus.IsVisible = true;
    }

    private async void ShareLocation_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowPlusStatus(L.T("Getting your location…"));
        ShareLocationOption.IsEnabled = false;
        (double Lat, double Lon)? location = null;
        try
        {
            // This PC's location first (Windows location services), then the radio's position.
            if (AppHost.GetPcLocation is { } get)
            {
                try { location = await get().WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* timed out or not allowed */ }
            }
            if (location is null && AppHost.Core.SelfInfo is { } self && (self.Latitude != 0 || self.Longitude != 0)) location = (self.Latitude, self.Longitude);
        }
        finally { ShareLocationOption.IsEnabled = true; }
        if (location is not { } l)
        {
            ShowPlusStatus(L.T("No location yet: allow location for this PC (Windows Settings → Privacy & security → Location), or set your radio's position on the Radio page."));
            return;
        }
        PlusStatus.IsVisible = false;
        PlusButton.Flyout?.Hide();
        InsertAtCaret(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{l.Lat:0.000000}, {l.Lon:0.000000}"), spaced: true);
    }

    private static string? MyShareToken()
    {
        var core = AppHost.Core;
        var key = core?.SelfInfo?.PublicKey ?? core?.Radio?.PublicKey;
        var name = core?.SelfInfo?.Name ?? core?.Radio?.Name;
        return key is { Length: 32 } && !string.IsNullOrWhiteSpace(name)
            ? MC1.Core.Utilities.ContactShareUtilities.FormatShare(key, MeshCore.ContactType.Chat, name.Trim())
            : null;
    }

    private void ShareMyInfo_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (MyShareToken() is not { } token)
        {
            ShowPlusStatus(L.T("Connect your radio first — your contact card comes from it."));
            return;
        }
        PlusButton.Flyout?.Hide();
        InsertAtCaret(token, spaced: true);
    }

    private List<ShareContactItem> _shareContacts = [];

    private void ShowContactPicker_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var core = AppHost.Core;
        var hashSize = (core.Capabilities?.PathHashMode ?? 0) + 1;
        _shareContacts = core.Contacts.GetAll()
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => ShareContactItem.From(c, hashSize))
            .ToList();
        PlusMenu.IsVisible = false;
        EmojiPanel.IsVisible = false;
        ContactPanel.IsVisible = true;
        ContactSearch.Text = "";
        FilterShareContacts();
        Dispatcher.UIThread.Post(() => ContactSearch.Focus(), DispatcherPriority.Background);
    }

    private void ContactSearch_OnTextChanged(object? sender, TextChangedEventArgs e) => FilterShareContacts();

    private void FilterShareContacts()
    {
        var q = ContactSearch.Text?.Trim().ToUpperInvariant() ?? "";
        var list = q.Length == 0 ? _shareContacts : _shareContacts.Where(i => i.Search.Contains(q, StringComparison.Ordinal)).ToList();
        ContactList.ItemsSource = list;
        ContactEmpty.Text = _shareContacts.Count == 0 ? L.T("No contacts yet.") : L.T("No contacts match.");
        ContactEmpty.IsVisible = list.Count == 0;
    }

    private void ShareContact_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ShareContactItem item }) return;
        PlusButton.Flyout?.Hide();
        InsertAtCaret(item.Token, spaced: true);
    }

    private void ComposerEmoji_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Content: string emoji }) InsertAtCaret(emoji, keepFocusInFlyout: true);
    }

    private async void OpenMeshPic_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        PlusButton.Flyout?.Hide();
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var link = await MeshPicWindow.PickAsync(owner);
        if (!string.IsNullOrWhiteSpace(link)) InsertAtCaret(Services.MeshPicLinks.Normalize(link));
        else FocusComposer();
    }

    /// <summary>Inserts text at the remembered caret/selection of the message box.</summary>
    public void InsertAtCaret(string text, bool keepFocusInFlyout = false, bool? spaced = null)
    {
        var (value, caret) = Services.MeshPicLinks.InsertAt(Composer.Text ?? "", _selStart, _selEnd, text, spaced);
        Composer.Text = value;
        _selStart = _selEnd = caret;
        Composer.CaretIndex = caret;
        if (!keepFocusInFlyout) Dispatcher.UIThread.Post(() => { Composer.Focus(); Composer.CaretIndex = caret; }, DispatcherPriority.Background);
    }

    private void LinkPreview_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: MessageItemViewModel { FirstUrl: { } url } }) AppHost.Dialogs.OpenUrl(url);
    }

    private void MapPreview_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: MessageItemViewModel { MapUri: { } uri } m }) m.Conversation.ShowOnMapCommand.Execute(uri);
    }

    private void MessageMenu_OnOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ContextMenu { DataContext: MessageItemViewModel m } menu) return;
        var react = menu.Items.OfType<MenuItem>().FirstOrDefault(i => (i.Tag as string) == "react");
        if (react is null) return;
        react.Items.Clear();
        foreach (var emoji in m.Conversation.QuickEmoji)
            react.Items.Add(new MenuItem { Header = emoji, Command = m.ReactCommand, CommandParameter = emoji, FontSize = 16 });
    }
}
