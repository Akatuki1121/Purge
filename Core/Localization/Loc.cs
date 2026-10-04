using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Purge.Localization
{
    /// <summary>
    /// 表示文言の取得と、表示言語の切り替えを担う。
    ///
    /// 文言は Strings.ja.json / Strings.en.json(埋め込みリソース)にキーと対で持つ。
    /// XAMLからは {loc:Loc キー}、コードからは Loc.T("キー") / Loc.F("キー", 引数...) で参照する。
    /// 英語側にキーが無い場合は日本語、日本語にも無ければキーそのものを返す(表示が空になって
    /// 気づけなくなるのを避けるため)。キーの過不足は Tests の LocalizationTests で検出する。
    ///
    /// このクラスはWPFに依存しない。XAMLのバインディング更新は LocalizationSource が担当する。
    /// </summary>
    public static class Loc
    {
        private const string ResourcePrefix = "Purge.Strings.";
        private const string ResourceSuffix = ".json";
        private const string JapaneseCode = "ja";
        private const string EnglishCode = "en";

        private static readonly IReadOnlyDictionary<string, string> s_empty = new Dictionary<string, string>();

        private static IReadOnlyDictionary<string, string> s_japanese = s_empty;
        private static IReadOnlyDictionary<string, string> s_english = s_empty;
        private static bool s_loaded;

        /// <summary>表示言語が実際に切り替わったときに発火する(UIスレッドで呼ぶこと)。</summary>
        public static event Action? LanguageChanged;

        /// <summary>ユーザーが選んだ設定(Auto を含む)。</summary>
        public static AppLanguage Preference { get; private set; } = AppLanguage.Auto;

        /// <summary>実際に適用中の言語。Japanese か English のどちらか。</summary>
        public static AppLanguage Current { get; private set; } = AppLanguage.Japanese;

        public static bool IsEnglish => Current == AppLanguage.English;

        /// <summary>
        /// 起動時に1回呼ぶ。保存済みの設定を読み込み、最初のウィンドウを作る前に言語を確定させる。
        /// </summary>
        public static void Initialize()
        {
            EnsureLoaded();
            Preference = LanguageSettingsStore.Load();
            Current = ResolveCurrent(Preference);
        }

        /// <summary>
        /// 表示言語の設定を変更して保存する。適用中の言語が変わる場合のみ LanguageChanged を発火する
        /// (例: OSが日本語のとき Auto から 日本語 に変えても画面は変わらないため発火しない)。
        /// </summary>
        public static void SetPreference(AppLanguage preference)
        {
            if (preference == Preference)
            {
                return;
            }

            Preference = preference;
            LanguageSettingsStore.Save(preference);

            var resolved = ResolveCurrent(preference);
            if (resolved == Current)
            {
                return;
            }

            Current = resolved;
            LanguageChanged?.Invoke();
        }

        public static string T(string key)
        {
            EnsureLoaded();

            var primary = IsEnglish ? s_english : s_japanese;
            if (primary.TryGetValue(key, out var text))
            {
                return text;
            }

            return s_japanese.TryGetValue(key, out var fallback) ? fallback : key;
        }

        public static string F(string key, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, T(key), args);
        }

        /// <summary>
        /// 指定言語の文言表そのもの(キーと文言の対)を返す。キー過不足のテストなど検査用。
        /// Auto は実際の言語ではないため指定できない。
        /// </summary>
        public static IReadOnlyDictionary<string, string> GetTable(AppLanguage language)
        {
            EnsureLoaded();
            return language switch
            {
                AppLanguage.Japanese => s_japanese,
                AppLanguage.English => s_english,
                _ => throw new ArgumentException("Auto は文言表を持たない。Japanese か English を指定すること。", nameof(language)),
            };
        }

        private static AppLanguage ResolveCurrent(AppLanguage preference)
        {
            if (preference != AppLanguage.Auto)
            {
                return preference;
            }

            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == JapaneseCode
                ? AppLanguage.Japanese
                : AppLanguage.English;
        }

        private static void EnsureLoaded()
        {
            if (s_loaded)
            {
                return;
            }

            s_japanese = LoadStrings(JapaneseCode);
            s_english = LoadStrings(EnglishCode);
            s_loaded = true;
        }

        private static IReadOnlyDictionary<string, string> LoadStrings(string languageCode)
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream($"{ResourcePrefix}{languageCode}{ResourceSuffix}");
            if (stream is null)
            {
                return s_empty;
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? s_empty;
        }
    }
}
