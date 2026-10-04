using System.ComponentModel;
using Purge.Localization;

namespace Purge.UI.Localization;

/// <summary>
/// XAMLのバインディング用に、Loc の文言を「キーで引けるプロパティ」として公開する。
/// 表示言語が切り替わると Item[] の変更を通知し、{loc:Loc キー} で束縛されている
/// すべての文言が再起動なしでその場で書き換わる。
/// </summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    public static LocalizationSource Instance { get; } = new();

    private LocalizationSource()
    {
        Loc.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>キーに対応する、現在の表示言語の文言。</summary>
    public string this[string key] => Loc.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnLanguageChanged()
    {
        // インデクサ全体が変わったことを示す慣用の名前("Item[]")。
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
