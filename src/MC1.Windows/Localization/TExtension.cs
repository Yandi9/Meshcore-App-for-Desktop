using Avalonia.Markup.Xaml;

namespace MC1.Windows.Localization;

/// <summary>
/// <c>{l:T 'English text'}</c> in AXAML: the text in the app's language. Views are rebuilt when the language changes,
/// so this is read once when the view loads.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;

    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
