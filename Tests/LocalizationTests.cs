using System.Globalization;
using System.Text.RegularExpressions;
using Purge.Localization;

namespace Purge.Tests;

/// <summary>
/// 表示文言(Strings.ja.json / Strings.en.json)と、表示言語の決定・切り替え(Loc)の自動テスト。
/// Loc は静的な状態を持つため、このクラス内のテストは直列で動く。
/// 設定ファイルの保存先は一時フォルダに差し替え、実際のユーザー設定(%LocalAppData%\Purge)には触れない。
/// </summary>
public class LocalizationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly CultureInfo _originalUiCulture;

    public LocalizationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PurgeLocTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
        _originalUiCulture = CultureInfo.CurrentUICulture;
        LanguageSettingsStore.PathOverride = _settingsPath;
        // 実行PCのMSI版のレジストリ(InstallLanguage)に左右されないよう、既定では「無し」にする。
        Loc.InstallLanguageReader = () => null;
    }

    public void Dispose()
    {
        LanguageSettingsStore.PathOverride = null;
        Loc.ResetInstallLanguageReader();
        CultureInfo.CurrentUICulture = _originalUiCulture;
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 一時フォルダの後始末に失敗してもテスト結果には影響させない。
        }
    }

    // ─── 文言表の整合性 ─────────────────────────────────────────────────

    [Fact]
    public void 日本語と英語でキーが過不足なく揃っている()
    {
        var ja = Loc.GetTable(AppLanguage.Japanese).Keys.ToHashSet();
        var en = Loc.GetTable(AppLanguage.English).Keys.ToHashSet();

        var onlyJa = ja.Except(en).OrderBy(k => k).ToList();
        var onlyEn = en.Except(ja).OrderBy(k => k).ToList();

        Assert.True(onlyJa.Count == 0, "英語に無いキー: " + string.Join(", ", onlyJa));
        Assert.True(onlyEn.Count == 0, "日本語に無いキー: " + string.Join(", ", onlyEn));
    }

    [Fact]
    public void 文言表が空でなく値も空文字ではない()
    {
        foreach (var language in new[] { AppLanguage.Japanese, AppLanguage.English })
        {
            var table = Loc.GetTable(language);
            Assert.NotEmpty(table);

            var empty = table.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ToList();
            Assert.True(empty.Count == 0, $"{language} で値が空のキー: " + string.Join(", ", empty));
        }
    }

    [Fact]
    public void 日本語と英語で書式の引数番号が一致している()
    {
        var ja = Loc.GetTable(AppLanguage.Japanese);
        var en = Loc.GetTable(AppLanguage.English);

        var mismatched = new List<string>();
        foreach (var (key, jaText) in ja)
        {
            if (!en.TryGetValue(key, out var enText))
            {
                continue; // 過不足は別のテストで検出する
            }

            if (!PlaceholderIndexes(jaText).SetEquals(PlaceholderIndexes(enText)))
            {
                mismatched.Add(key);
            }
        }

        Assert.True(mismatched.Count == 0, "{0}などの引数番号が日英で食い違うキー: " + string.Join(", ", mismatched));
    }

    private static HashSet<int> PlaceholderIndexes(string text)
    {
        return Regex.Matches(text, @"\{(\d+)(?:[,:][^}]*)?\}")
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToHashSet();
    }

    // ─── 文言の取得 ─────────────────────────────────────────────────────

    [Fact]
    public void 存在しないキーはキーそのものを返す()
    {
        Assert.Equal("__no_such_key__", Loc.T("__no_such_key__"));
    }

    [Fact]
    public void 言語を切り替えると同じキーで違う文言が返る()
    {
        var key = Loc.GetTable(AppLanguage.Japanese).Keys.First(k => Loc.GetTable(AppLanguage.Japanese)[k] != Loc.GetTable(AppLanguage.English)[k]);

        Loc.Initialize();
        Loc.SetPreference(AppLanguage.Japanese);
        var ja = Loc.T(key);
        Loc.SetPreference(AppLanguage.English);
        var en = Loc.T(key);

        Assert.Equal(Loc.GetTable(AppLanguage.Japanese)[key], ja);
        Assert.Equal(Loc.GetTable(AppLanguage.English)[key], en);
        Assert.NotEqual(ja, en);
    }

    // ─── 起動時の言語の決定 ─────────────────────────────────────────────

    [Theory]
    [InlineData("ja-JP", AppLanguage.Japanese)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("fr-FR", AppLanguage.English)]
    public void 設定が無いときはOSの表示言語に従う(string culture, AppLanguage expected)
    {
        CultureInfo.CurrentUICulture = new CultureInfo(culture);

        Loc.Initialize();

        Assert.Equal(AppLanguage.Auto, Loc.Preference);
        Assert.Equal(expected, Loc.Current);
    }

    [Theory]
    [InlineData("ja-JP", AppLanguage.English, AppLanguage.English)]
    [InlineData("en-US", AppLanguage.Japanese, AppLanguage.Japanese)]
    public void 設定が無いときはインストール時の言語がOSの表示言語より優先される(string culture, AppLanguage installed, AppLanguage expected)
    {
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        Loc.InstallLanguageReader = () => installed;

        Loc.Initialize();

        Assert.Equal(expected, Loc.Current);
    }

    [Fact]
    public void 保存済みの言語はOSの表示言語より優先される()
    {
        File.WriteAllText(_settingsPath, "{ \"language\": \"English\" }");
        CultureInfo.CurrentUICulture = new CultureInfo("ja-JP");

        Loc.Initialize();

        Assert.Equal(AppLanguage.English, Loc.Preference);
        Assert.Equal(AppLanguage.English, Loc.Current);
    }

    [Fact]
    public void 壊れた設定ファイルはAutoとして扱う()
    {
        File.WriteAllText(_settingsPath, "{ これはJSONではない");
        CultureInfo.CurrentUICulture = new CultureInfo("ja-JP");

        Loc.Initialize();

        Assert.Equal(AppLanguage.Auto, Loc.Preference);
        Assert.Equal(AppLanguage.Japanese, Loc.Current);
    }

    // ─── 言語の切り替えと保存 ───────────────────────────────────────────

    [Fact]
    public void 適用言語が変わるときだけ通知される()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("ja-JP");
        Loc.Initialize(); // Auto → 日本語

        var count = 0;
        void OnChanged() => count++;
        Loc.LanguageChanged += OnChanged;
        try
        {
            Loc.SetPreference(AppLanguage.Japanese); // 適用言語は日本語のまま → 通知なし
            Assert.Equal(0, count);

            Loc.SetPreference(AppLanguage.English); // 日本語 → 英語 → 通知あり
            Assert.Equal(1, count);

            Loc.SetPreference(AppLanguage.English); // 同じ設定 → 通知なし
            Assert.Equal(1, count);
        }
        finally
        {
            Loc.LanguageChanged -= OnChanged;
        }
    }

    [Fact]
    public void 切り替えた言語は保存され次回起動で復元される()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("ja-JP");
        Loc.Initialize();

        Loc.SetPreference(AppLanguage.English);
        Loc.Initialize(); // 再起動相当

        Assert.Equal(AppLanguage.English, Loc.Preference);
        Assert.Equal(AppLanguage.English, Loc.Current);
    }

    [Fact]
    public void 言語を保存しても他の設定は消えない()
    {
        File.WriteAllText(_settingsPath, "{ \"other\": 42 }");
        Loc.Initialize();

        Loc.SetPreference(AppLanguage.Japanese);

        var saved = File.ReadAllText(_settingsPath);
        Assert.Contains("\"other\"", saved);
        Assert.Contains("42", saved);
        Assert.Contains("Japanese", saved);
    }

    [Fact]
    public void Autoは文言表を持たない()
    {
        Assert.Throws<ArgumentException>(() => Loc.GetTable(AppLanguage.Auto));
    }
}
