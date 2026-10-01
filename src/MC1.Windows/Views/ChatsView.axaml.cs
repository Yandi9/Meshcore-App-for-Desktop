using Avalonia.Controls;

namespace MC1.Windows.Views;

public partial class ChatsView : UserControl
{
    public ChatsView()
    {
        InitializeComponent();
        // Narrower list when the window is small (e.g. snapped to half the screen).
        Split.SizeChanged += (_, e) => Split.ColumnDefinitions[0].Width = new GridLength(e.NewSize.Width < 820 ? 272 : 340);
    }
}
