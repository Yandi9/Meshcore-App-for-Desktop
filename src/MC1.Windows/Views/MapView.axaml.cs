using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Views;

public partial class MapView : UserControl
{
    public MapView()
    {
        InitializeComponent();
        Map.PointerLocationChanged += (lat, lon) =>
        {
            if (DataContext is MapViewModel vm) vm.PointerText = string.Create(CultureInfo.InvariantCulture, $"{lat:0.00000}, {lon:0.00000}");
        };
        Map.MapContextRequested += ShowContextMenu;
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is MapViewModel vm) vm.RunSearchCommand.Execute(null);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MapViewModel vm)
        {
            vm.VisibleBounds = () => Map.VisibleBounds();
            vm.ViewportSize = () => (Map.Bounds.Width, Map.Bounds.Height);
        }
    }

    private void ShowContextMenu(double lat, double lon)
    {
        if (DataContext is not MapViewModel vm) return;
        var coords = string.Create(CultureInfo.InvariantCulture, $"{lat:0.000000}, {lon:0.000000}");
        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                new MenuItem { Header = coords, IsEnabled = false },
                new Separator(),
                new MenuItem { Header = L.T("Line of sight from my radio to here"), Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => vm.LineOfSightTo(lat, lon)) },
                new MenuItem { Header = L.T("Set my radio's location here…"), Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => vm.SetRadioLocation(lat, lon)) },
                new MenuItem { Header = L.T("Drop a pin here"), Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => vm.CenterOn(lat, lon, vm.Zoom, L.T("Dropped pin"))) },
                new MenuItem { Header = L.T("Copy coordinates"), Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => AppHost.Dialogs.CopyToClipboard(coords)) },
            },
        };
        menu.Open(Map);
    }
}
