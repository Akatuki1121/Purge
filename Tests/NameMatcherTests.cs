using UninstallTool;

namespace UninstallTool.Tests;

/// <summary>
/// NameMatcher.IsSafeMatch の自動テスト。
/// 短い名前の完全一致救済・部分一致・トークン一致・誤検出防止のケースを網羅する。
/// </summary>
public class NameMatcherTests_IsSafeMatch
{
    // ─── 基本的な一致 ───────────────────────────────────────────────────

    [Fact]
    public void 完全一致は一致する()
        => Assert.True(NameMatcher.IsSafeMatch("Obsidian", "Obsidian"));

    [Fact]
    public void 大文字小文字違いは一致する()
        => Assert.True(NameMatcher.IsSafeMatch("obsidian", "Obsidian"));

    [Fact]
    public void 候補がアプリ名を部分的に含む場合は一致する()
        => Assert.True(NameMatcher.IsSafeMatch("ObsidianMarkdown", "Obsidian"));

    [Fact]
    public void アプリ名が候補を部分的に含む場合は一致する()
        => Assert.True(NameMatcher.IsSafeMatch("Visual", "Visual Studio Code"));

    // ─── 短い名前の保護 ─────────────────────────────────────────────────

    [Fact]
    public void 短い候補は完全一致のみ許可される()
        // "Git" は3文字 → 完全一致以外は不一致
        => Assert.False(NameMatcher.IsSafeMatch("Legitimate", "Git"));

    [Fact]
    public void 短い候補の完全一致は一致する()
        => Assert.True(NameMatcher.IsSafeMatch("Git", "Git"));

    [Fact]
    public void 短いアプリ名は完全一致のみ許可される()
        => Assert.False(NameMatcher.IsSafeMatch("Digital", "Git"));

    [Fact]
    public void 三文字のアプリ名の誤爆を防ぐ()
        // "VLC" が "ValveSteam" に誤爆しないこと
        => Assert.False(NameMatcher.IsSafeMatch("ValveSteam", "VLC"));

    // ─── トークン一致 ────────────────────────────────────────────────────

    [Fact]
    public void キャメルケース境界でトークン一致する()
        // "Arduino15" → tokens: ["Arduino"] / "Arduino IDE" → tokens: ["Arduino"]
        => Assert.True(NameMatcher.IsSafeMatch("Arduino15", "Arduino IDE"));

    [Fact]
    public void 数字境界でトークン一致する()
        // "Firefox123" と "Firefox Browser" はトークン "Firefox" で一致
        => Assert.True(NameMatcher.IsSafeMatch("Firefox123", "Firefox Browser"));

    [Fact]
    public void 無関係なトークンは一致しない()
        => Assert.False(NameMatcher.IsSafeMatch("Microsoft365", "Adobe Acrobat"));

    // ─── 誤検出防止 ─────────────────────────────────────────────────────

    [Fact]
    public void 空の候補は一致しない()
        => Assert.False(NameMatcher.IsSafeMatch("", "Obsidian"));

    [Fact]
    public void 空のアプリ名は一致しない()
        => Assert.False(NameMatcher.IsSafeMatch("Obsidian", ""));

    [Fact]
    public void 両方空は一致しない()
        => Assert.False(NameMatcher.IsSafeMatch("", ""));

    [Fact]
    public void OneDriveは無関係な名前に一致しない()
        // "OneDrive" (8文字) が "OneNote" に誤爆しないこと(トークン共有なし)
        => Assert.False(NameMatcher.IsSafeMatch("OneNote", "OneDrive"));

    [Fact]
    public void Gitが無関係なデジタル系ツールに一致しない()
        // "DigitalOcean" に "Git" が誤爆しないこと
        => Assert.False(NameMatcher.IsSafeMatch("DigitalOcean", "Git"));
}

/// <summary>
/// NameMatcher.IsSafeMatchAnywhere の自動テスト。
/// パス・コマンドライン等のセグメント単位での一致検出を確認する。
/// </summary>
public class NameMatcherTests_IsSafeMatchAnywhere
{
    [Fact]
    public void パスのセグメントが一致する()
        => Assert.True(NameMatcher.IsSafeMatchAnywhere(@"C:\Program Files\Git\cmd", "Git"));

    [Fact]
    public void パスに含まれないなら一致しない()
        => Assert.False(NameMatcher.IsSafeMatchAnywhere(@"C:\Program Files\Microsoft Office\root\Office16", "Git"));

    [Fact]
    public void 環境変数PATH形式のセグメントが一致する()
        => Assert.True(NameMatcher.IsSafeMatchAnywhere(@"C:\Program Files\Git\cmd;C:\Windows\System32", "Git"));

    [Fact]
    public void 長いパスでも短い名前は完全一致セグメントのみ一致する()
        // "Legitimate" というフォルダが "Git" に誤爆しないこと
        => Assert.False(NameMatcher.IsSafeMatchAnywhere(@"C:\Program Files\Legitimate\bin", "Git"));

    [Fact]
    public void 空のパスは一致しない()
        => Assert.False(NameMatcher.IsSafeMatchAnywhere("", "Obsidian"));

    [Fact]
    public void セミコロン区切りの複数パスから一致を検出できる()
        => Assert.True(NameMatcher.IsSafeMatchAnywhere(
            @"C:\Windows\System32;C:\Program Files\Obsidian;C:\Users\user\AppData\Local",
            "Obsidian"));
}

/// <summary>
/// NameMatcher.TokenizeName の自動テスト。
/// トークン分割のルールを個別に確認する。
/// </summary>
public class NameMatcherTests_TokenizeName
{
    [Fact]
    public void キャメルケースを分割する()
    {
        var tokens = NameMatcher.TokenizeName("VisualStudioCode").ToList();
        Assert.Contains("Visual", tokens, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Studio", tokens, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Code", tokens, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void 数字境界で分割する()
    {
        var tokens = NameMatcher.TokenizeName("Arduino15").ToList();
        Assert.Contains("Arduino", tokens, StringComparer.OrdinalIgnoreCase);
        // "15" は2文字なので除外されるはず
        Assert.DoesNotContain("15", tokens, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void 短いトークンは除外される()
    {
        // "ABC" は3文字 → MinTokenLengthForMatch(4)未満なので除外
        var tokens = NameMatcher.TokenizeName("ABC").ToList();
        Assert.Empty(tokens);
    }

    [Fact]
    public void 数字のみのトークンは除外される()
    {
        var tokens = NameMatcher.TokenizeName("Version2024Update").ToList();
        Assert.DoesNotContain("2024", tokens, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Version", tokens, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Update", tokens, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void スペースやハイフンで分割する()
    {
        var tokens = NameMatcher.TokenizeName("Visual-Studio Code").ToList();
        Assert.Contains("Visual", tokens, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Studio", tokens, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Code", tokens, StringComparer.OrdinalIgnoreCase);
    }
}
