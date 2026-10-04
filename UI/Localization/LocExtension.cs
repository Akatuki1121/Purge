using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace Purge.UI.Localization;

/// <summary>
/// XAML用の文言参照。<c>Content="{loc:Loc Common_Close}"</c> のように書く。
/// 内部では LocalizationSource への一方向バインディングを返すため、
/// 表示言語を切り替えると画面上の文言がその場で更新される。
/// 使う側のXAMLには <c>xmlns:loc="clr-namespace:Purge.UI.Localization"</c> を宣言すること。
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationSource.Instance,
            Mode = BindingMode.OneWay,
        };

        return binding.ProvideValue(serviceProvider);
    }
}
