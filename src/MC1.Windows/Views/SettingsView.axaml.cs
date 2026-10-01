using Avalonia;
using Avalonia.Controls;

namespace MC1.Windows.Views;

public partial class SettingsView : UserControl
{
    /// <summary>Below this width the category list moves from the left column to a strip across the top.</summary>
    private const double NarrowWidth = 720;
    private bool? _narrow;

    public SettingsView()
    {
        InitializeComponent();
        Root.SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        CategoryList.SelectionChanged += (_, _) => PageScroller.Offset = default;
    }

    private void ApplyLayout(double width)
    {
        var narrow = width < NarrowWidth;
        if (!narrow) NavHost.Width = width < 900 ? 200 : 230;
        if (_narrow == narrow) return;
        _narrow = narrow;
        if (narrow)
        {
            Grid.SetColumn(NavHost, 0);
            Grid.SetColumnSpan(NavHost, 2);
            Grid.SetRow(NavHost, 0);
            Grid.SetRowSpan(NavHost, 1);
            NavHost.Width = double.NaN;
            NavHost.Padding = new Thickness(16, 14, 16, 4);
            NavHost.BorderThickness = new Thickness(0, 0, 0, 1);
            NavTitle.Margin = new Thickness(2, 0, 0, 10);
            CategoryList.Classes.Add("strip");
            Grid.SetColumn(PageScroller, 0);
            Grid.SetColumnSpan(PageScroller, 2);
            PageHost.Margin = new Thickness(16, 16, 16, 24);
        }
        else
        {
            Grid.SetColumn(NavHost, 0);
            Grid.SetColumnSpan(NavHost, 1);
            Grid.SetRow(NavHost, 0);
            Grid.SetRowSpan(NavHost, 2);
            NavHost.Padding = new Thickness(14, 20, 10, 12);
            NavHost.BorderThickness = new Thickness(0, 0, 1, 0);
            NavTitle.Margin = new Thickness(10, 0, 0, 14);
            CategoryList.Classes.Remove("strip");
            Grid.SetColumn(PageScroller, 1);
            Grid.SetColumnSpan(PageScroller, 1);
            PageHost.Margin = new Thickness(28, 22, 28, 28);
        }
    }
}
