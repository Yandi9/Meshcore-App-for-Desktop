using CommunityToolkit.Mvvm.ComponentModel;
using MC1.Core.Models;

namespace MC1.Windows.ViewModels;

public sealed record ToolItem(string Id, string Title, string Subtitle, string IconKey, bool RequiresRadio);

public sealed partial class ToolsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public ToolsViewModel(MainWindowViewModel main)
    {
        _main = main;
        TracePath = new TracePathViewModel(main);
        LineOfSight = new LineOfSightViewModel(main);
        RxLog = new RxLogViewModel(main);
        Noise = new NoiseFloorViewModel(main);
        Discovery = new NodeDiscoveryViewModel(main);
        Cli = new CliViewModel(main);
        Items =
        [
            new("trace", L.T("Trace Path"), L.T("Measure SNR hop by hop through repeaters"), "Icon.TransitConnectionVariant", true),
            new("los", L.T("Line of Sight"), L.T("Terrain profile, Fresnel zone and path loss"), "Icon.Terrain", false),
            new("rxlog", L.T("RX Log"), L.T("Every packet your radio hears, decoded"), "Icon.FormatListBulleted", true),
            new("noise", L.T("Noise Floor"), L.T("Live noise floor, RSSI and SNR"), "Icon.Waveform", true),
            new("discover", L.T("Node Discovery"), L.T("Find repeaters and rooms in direct range"), "Icon.Radar", true),
            new("cli", L.T("CLI Terminal"), L.T("Command line for your radio and remote nodes"), "Icon.Console", true),
        ];
        _selected = Items[0];
        _current = TracePath;
    }

    public IReadOnlyList<ToolItem> Items { get; }
    public TracePathViewModel TracePath { get; }
    public LineOfSightViewModel LineOfSight { get; }
    public RxLogViewModel RxLog { get; }
    public NoiseFloorViewModel Noise { get; }
    public NodeDiscoveryViewModel Discovery { get; }
    public CliViewModel Cli { get; }

    [ObservableProperty] private ToolItem _selected;
    [ObservableProperty] private ViewModelBase _current;

    partial void OnSelectedChanged(ToolItem value)
    {
        Current = value.Id switch
        {
            "trace" => TracePath,
            "los" => LineOfSight,
            "rxlog" => RxLog,
            "noise" => Noise,
            "discover" => Discovery,
            _ => Cli,
        };
        if (Current is ToolPage page) page.OnShown();
    }

    private void Show(string id) => Selected = Items.First(i => i.Id == id);

    public void OpenLineOfSight((double Lat, double Lon)? from, (double Lat, double Lon) to, string toLabel)
    {
        Show("los");
        LineOfSight.SetPoints(from, to, toLabel);
    }

    public void OpenTraceFor(ContactRecord contact)
    {
        Show("trace");
        TracePath.StartWith(contact);
    }

    public void OpenCliFor(ContactRecord contact)
    {
        Show("cli");
        Cli.SelectTarget(contact);
    }
}

/// <summary>Base for tool pages that want to know when they become visible.</summary>
public abstract class ToolPage : ViewModelBase
{
    public virtual void OnShown() { }
}
