using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Windows.ViewModels;

/// <summary>One page of the welcome guide.</summary>
public sealed record GuideStep(string Title, string Body, string? IconKey, bool IsLogo = false, bool IsConnect = false, bool HasToggles = false,
    bool IsLanguage = false);

/// <summary>A choice in a language picker: a language (its own name, and its name in the app's language), or Windows' language.</summary>
public sealed class LanguageOption
{
    private LanguageOption(string? code, string label, string caption)
    {
        Code = code;
        Label = label;
        Caption = caption;
    }

    /// <summary>The language code, or null for "Windows' language".</summary>
    public string? Code { get; }
    public string Label { get; }
    /// <summary>The language's name in the app's language ("Spanish" / "Español"), empty when it's the same as the label.</summary>
    public string Caption { get; }
    public bool HasCaption => Caption.Length > 0;

    public static LanguageOption For(AppLanguage lang)
    {
        var translated = L.T(lang.EnglishName);
        return new(lang.Code, lang.NativeName, translated == lang.NativeName ? "" : translated);
    }

    public static LanguageOption WindowsDefault() =>
        new(null, L.T("Windows default"), L.Languages.First(l => l.Code == L.SystemLanguageCode).NativeName);

    /// <summary>The languages, in the order the iOS app lists them; with "Windows default" first when asked for.</summary>
    public static List<LanguageOption> All(bool withWindowsDefault)
    {
        var list = new List<LanguageOption>();
        if (withWindowsDefault) list.Add(WindowsDefault());
        list.AddRange(L.Languages.Select(For));
        return list;
    }

    public override string ToString() => Label;
}

public enum GuideResult { Done, Connect, Later }

public sealed partial class GuideDot : ObservableObject
{
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>
/// The welcome guide shown the very first time MeshCore runs on a PC, ending with "Connect your radio" when no radio
/// has been connected yet. With <c>connectOnly</c> it's just that last page (later launches, still no radio).
/// </summary>
public sealed partial class WelcomeGuideViewModel : ViewModelBase
{
    private readonly List<GuideStep> _steps = [];

    private readonly bool _includeConnect, _connectOnly;

    public WelcomeGuideViewModel(bool includeConnect, bool connectOnly = false)
    {
        _includeConnect = includeConnect;
        _connectOnly = connectOnly;
        BuildSteps();
        foreach (var _ in _steps) Dots.Add(new GuideDot());
        _startWithWindows = AppHost.GetAutoStart?.Invoke() ?? false;
        _notifications = Core.Settings.Current.NotificationsEnabled;
        Languages = LanguageOption.All(withWindowsDefault: false);
        _selectedLanguage = Languages.FirstOrDefault(o => o.Code == L.Code);
        Update();
    }

    /// <summary>The pages, in the app's language (built again when the language is changed on the first page).</summary>
    private void BuildSteps()
    {
        _steps.Clear();
        if (!_connectOnly)
        {
            _steps.Add(new(L.T("Choose your language"),
                L.T("Pick the language for MeshCore's menus, buttons and messages."),
                "Icon.Translate", IsLanguage: true));
            _steps.Add(new(L.T("Welcome to MeshCore"),
                L.T("Send messages without internet or cell service. Your MeshCore radio talks to other radios over LoRa, and repeaters pass messages along so they reach further."),
                null, IsLogo: true));
            _steps.Add(new(L.T("Chats and channels"),
                L.T("Direct messages go to one person. Channels — Public, #hashtags and private ones — reach everyone who has them. The + next to the message box adds emoji, pictures, your location or a contact."),
                "Icon.ChatOutline"));
            _steps.Add(new(L.T("Contacts and the map"),
                L.T("Radios you hear show up in Contacts, and the ones that share a position appear on the Map. The Tools help you trace paths, check line of sight and manage your repeaters."),
                "Icon.MapOutline"));
            _steps.Add(new(L.T("Always within reach"),
                L.T("Close the window and MeshCore keeps running in the notification area, so messages still arrive. New messages and the radio's battery can also show on the lock screen."),
                "Icon.BellOutline", HasToggles: true));
        }
        if (_includeConnect || _connectOnly)
            _steps.Add(new(L.T("Connect your radio"),
                _connectOnly
                    ? L.T("You haven't connected a radio yet. Connect over Bluetooth, a USB cable or WiFi — then give it a name and pick the radio preset for your area.")
                    : L.T("Connect over Bluetooth, a USB cable or WiFi. Right after, you'll give your radio a name and pick the radio preset for your area."),
                "Icon.AccessPoint", IsConnect: true));
    }

    /// <summary>The languages on the first page.</summary>
    [ObservableProperty] private List<LanguageOption> _languages;
    [ObservableProperty] private LanguageOption? _selectedLanguage;

    /// <summary>Picking a language switches the whole app to it right away (and this guide with it).</summary>
    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (value?.Code is not { } code || code == L.Code) return;
        // After the click has finished: the switch rebuilds the views, this list included.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyLanguage(code));
    }

    private void ApplyLanguage(string code)
    {
        if (code == L.Code) return;
        AppHost.ChangeLanguage?.Invoke(code);
        BuildSteps();
        var selected = code;
        Languages = LanguageOption.All(withWindowsDefault: false);
        _selectedLanguage = Languages.FirstOrDefault(o => o.Code == selected);
        OnPropertyChanged(nameof(SelectedLanguage));
        Update();
    }

    public ObservableCollection<GuideDot> Dots { get; } = new();
    public Action? Close { get; set; }
    public GuideResult Result { get; private set; } = GuideResult.Done;

    [ObservableProperty] private int _index;
    [ObservableProperty] private GuideStep _step = null!;

    public bool IsFirst => Index == 0;
    public bool IsLast => Index == _steps.Count - 1;
    public bool HasBack => !IsFirst;
    public bool ShowNext => !IsLast;
    public bool ShowFinish => IsLast && !Step.IsConnect;
    public bool ShowConnect => Step.IsConnect;
    public bool ShowSkip => !IsLast;
    public bool ShowDots => _steps.Count > 1;
    public bool CanAutoStart => AppHost.SetAutoStart is not null;

    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value)) return;
            AppHost.SetAutoStart?.Invoke(value);
            Core.Settings.Update(s => s.StartWithWindows = value);
        }
    }

    private bool _notifications;
    public bool Notifications
    {
        get => _notifications;
        set
        {
            if (SetProperty(ref _notifications, value)) Core.Settings.Update(s => s.NotificationsEnabled = value);
        }
    }

    partial void OnIndexChanged(int value) => Update();

    private void Update()
    {
        Step = _steps[Index];
        for (var i = 0; i < Dots.Count; i++) Dots[i].IsCurrent = i == Index;
        foreach (var n in (string[])[nameof(IsFirst), nameof(IsLast), nameof(HasBack), nameof(ShowNext), nameof(ShowFinish), nameof(ShowConnect), nameof(ShowSkip)])
            OnPropertyChanged(n);
    }

    [RelayCommand] private void Next() { if (!IsLast) Index++; }
    [RelayCommand] private void Back() { if (!IsFirst) Index--; }

    /// <summary>Skips the tour (to the connect page when there is one).</summary>
    [RelayCommand]
    private void Skip()
    {
        var connect = _steps.FindIndex(s => s.IsConnect);
        if (connect >= 0 && Index < connect) Index = connect;
        else Finish(GuideResult.Done);
    }

    [RelayCommand] private void Done() => Finish(GuideResult.Done);
    [RelayCommand] private void Connect() => Finish(GuideResult.Connect);
    [RelayCommand] private void Later() => Finish(GuideResult.Later);

    private void Finish(GuideResult result)
    {
        Result = result;
        Close?.Invoke();
    }
}

/// <summary>
/// "Set up your radio", offered once after the very first radio connects: its name, the radio preset for the area,
/// and whether to share a position — then an advert so others see it.
/// </summary>
public sealed partial class RadioSetupViewModel : ViewModelBase
{
    private readonly RadioPreset? _current;
    private readonly string _originalName;

    public RadioSetupViewModel()
    {
        var core = Core;
        var s = core.SelfInfo;
        Presets = RadioPresets.PresetsForCurrentLocale();
        _originalName = s?.Name ?? "";
        _name = _originalName;
        _current = core.Device.CurrentPreset();
        string? country = null;
        try { country = RegionInfo.CurrentRegion.TwoLetterISORegionName; } catch { /* invariant */ }
        var recommended = RadioPresets.RecommendedFor(country) is { } id ? Presets.FirstOrDefault(p => p.Id == id) : null;
        _selectedPreset = _current is { } c ? Presets.FirstOrDefault(p => p.Id == c.Id) : recommended;
        CurrentText = s is null ? "" : L.F("On the radio now: {0}", RadioPresets.Describe(_current, s.RadioFrequency, s.RadioBandwidth, s.RadioSpreadingFactor, s.RadioCodingRate));
        RecommendedText = _current is null && recommended is not null
            ? L.F("{0} is the usual preset where you are. Everyone you talk to must use the same one.", recommended.Name)
            : L.T("Everyone you talk to must use the same preset.");
        if (s is not null && (s.Latitude != 0 || s.Longitude != 0))
        {
            _latitude = s.Latitude;
            _longitude = s.Longitude;
        }
        _sharePosition = s?.AdvertisementLocationPolicy != 0 && _latitude is not null;
        UpdateLocationText();
    }

    public IReadOnlyList<RadioPreset> Presets { get; }
    public string CurrentText { get; }
    public string RecommendedText { get; }
    public Action? Close { get; set; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private RadioPreset? _selectedPreset;
    [ObservableProperty] private bool _sharePosition;
    [ObservableProperty] private bool _sendAdvert = true;
    [ObservableProperty] private string _locationText = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;

    private double? _latitude, _longitude;

    private void UpdateLocationText() => LocationText = _latitude is { } lat && _longitude is { } lon
        ? L.F("Position: {0}", string.Create(CultureInfo.InvariantCulture, $"{lat:0.0000}, {lon:0.0000}"))
        : L.T("No position set");

    [RelayCommand]
    private async Task UsePcLocation()
    {
        if (AppHost.GetPcLocation is null) { Message = L.T("Location isn't available on this PC."); return; }
        Message = L.T("Getting this PC's location…");
        var loc = await AppHost.GetPcLocation();
        if (loc is not { } l) { Message = L.T("Couldn't get a location. Check Windows Settings → Privacy & security → Location."); return; }
        _latitude = l.Lat;
        _longitude = l.Lon;
        SharePosition = true;
        UpdateLocationText();
        Message = null;
    }

    [RelayCommand] private void Skip() => Close?.Invoke();

    [RelayCommand]
    private async Task Apply()
    {
        if (!Core.IsConnected) { Message = L.T("The radio isn't connected any more — connect it and try again from the Radio page."); return; }
        var name = (Name ?? "").Trim();
        if (name.Length == 0) { Message = L.T("Give your radio a name."); return; }
        Busy = true;
        Message = null;
        try
        {
            if (name != _originalName)
            {
                Message = L.T("Setting the name…");
                await Core.Device.SetNameAsync(name);
            }
            if (SelectedPreset is { } preset && preset.Id != _current?.Id)
            {
                Message = L.F("Switching to {0}…", preset.Name);
                await Core.Device.ApplyPresetAsync(preset);
            }
            if (SharePosition && _latitude is { } lat && _longitude is { } lon)
            {
                Message = L.T("Setting the position…");
                await Core.Device.SetLocationAsync(lat, lon);
            }
            var policy = (byte)(SharePosition && _latitude is not null ? 1 : 0);
            if (Core.SelfInfo?.AdvertisementLocationPolicy != policy)
                await Core.Device.SetOtherParamsAsync(c => c.AdvertisementLocationPolicy = policy);
            if (SendAdvert)
            {
                Message = L.T("Announcing your radio…");
                await Core.Device.SendAdvertAsync(flood: true);
            }
            AppHost.Main?.ShowToast(L.T("Your radio is ready"), SelectedPreset is { } p ? L.F("{0} is on the {1} preset.", name, p.Name) : L.F("{0} is set up.", name));
            Close?.Invoke();
        }
        catch (Exception ex)
        {
            Message = L.F("Couldn't finish: {0} You can change these on the Radio page any time.", ex.Message);
        }
        finally { Busy = false; }
    }
}
