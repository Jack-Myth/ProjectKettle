using Avalonia.Markup.Xaml;
using Kettle.Core;

namespace Kettle;

public sealed class TrExtension : MarkupExtension
{
    public TextId Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => L.Text(Key);
}
