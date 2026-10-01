using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MC1.Windows;

/// <summary>Maps <c>FooViewModel</c> to <c>FooView</c> for ContentControls and dialogs.</summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Type?> Map = new();

    public Control? Build(object? data)
    {
        if (data is null) return null;
        var vmType = data.GetType();
        if (!Map.TryGetValue(vmType, out var viewType))
        {
            var name = vmType.FullName!.Replace(".ViewModels.", ".Views.");
            name = name[..^"ViewModel".Length] + "View";
            viewType = vmType.Assembly.GetType(name);
            Map[vmType] = viewType;
        }
        return viewType is null ? new TextBlock { Text = L.F("View not found: {0}", vmType.Name) } : (Control)Activator.CreateInstance(viewType)!;
    }

    public bool Match(object? data) => data is ObservableObject && data.GetType().Name.EndsWith("ViewModel", StringComparison.Ordinal);
}
