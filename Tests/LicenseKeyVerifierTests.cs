using UninstallTool;

namespace UninstallTool.Tests;

/// <summary>
/// LicenseKeyVerifier の自動テスト。
/// LicenseKeyIssuerで実際に発行したキー形式を使い、署名検証が正しく機能することを確認する。
/// 埋め込み済みの公開鍵(Core/LicenseKeyVerifier.cs)に対応する秘密鍵で署名したキーのみ
/// Validになることを、正常系・異常系の両方で確認する。
/// </summary>
public class LicenseKeyVerifierTests
{
    // LicenseKeyIssuerで実際に発行した検証済みキー(test@example.com向け、Pro、無期限)。
    // 埋め込み済み公開鍵と対応する秘密鍵で署名されている。
    // 2026-09-11: 鍵ローテーション(2回目、確定版)に伴い、新しい鍵ペアで発行し直したサンプルに更新。
    private const string ValidSampleKey =
        "eyJFbWFpbCI6InRlc3RAZXhhbXBsZS5jb20iLCJJc3N1ZWRBdFV0YyI6IjIwMjYtMDktMTJUMDc6MDk6NDEuMDI3MjgwNloiLCJFZGl0aW9uIjoiUHJvIiwiRXhwaXJlc0F0VXRjIjpudWxsfQ==.xKoiTdctsbHWA5SlDVtimJ654x7/npFf06n7R6yWAfciAZYmkOqvaOGseS6z7/KL21MZ3+D0CqZbanpdQXyE23gjBSKf72RTxQhh8bRDxTGMKL4CdeSOEEBqAxVeHV0+xUrFK+/F7vj0f8uxAqIAPb2LWBsDTYUZrYQuI9moDOpOuupqIb9pYhVOgmD0g42Apxvx0mTsM1Oo3CxwN/I5nq/f014RyAiARL5MIhdcms8L6WFu8QtOJpkmtGx2kTSDImu2Rd0MgKrS1+2V/DBlgfwNgMzYzu1JiZemxF/93ZQgwP/YDyID2bz93UiND+j6C+8yjuAGOWmyVKDxxMio/w==";

    [Fact]
    public void 正規のキーはValidになる()
    {
        var result = LicenseKeyVerifier.Validate(ValidSampleKey, out var payload);

        Assert.Equal(LicenseValidationResult.Valid, result);
        Assert.NotNull(payload);
        Assert.Equal("test@example.com", payload!.Email);
        Assert.Equal("Pro", payload.Edition);
        Assert.Null(payload.ExpiresAtUtc);
    }

    [Fact]
    public void ドット区切りが無いキーはInvalidFormatになる()
    {
        var result = LicenseKeyVerifier.Validate("これは不正な形式です", out var payload);

        Assert.Equal(LicenseValidationResult.InvalidFormat, result);
        Assert.Null(payload);
    }

    [Fact]
    public void Base64として不正な文字列はInvalidFormatになる()
    {
        var result = LicenseKeyVerifier.Validate("不正payload.不正signature", out var payload);

        Assert.Equal(LicenseValidationResult.InvalidFormat, result);
        Assert.Null(payload);
    }

    [Fact]
    public void 署名部分を1文字改ざんするとSignatureMismatchになる()
    {
        // 末尾1文字を変更し、同じ長さのBase64のまま署名だけ壊す(改ざん耐性の確認)
        var tampered = ValidSampleKey[..^2] + (ValidSampleKey[^2] == 'A' ? 'B' : 'A') + ValidSampleKey[^1];

        var result = LicenseKeyVerifier.Validate(tampered, out var payload);

        Assert.Equal(LicenseValidationResult.SignatureMismatch, result);
        Assert.Null(payload);
    }

    [Fact]
    public void payload部分を改ざんするとSignatureMismatchになる()
    {
        var parts = ValidSampleKey.Split('.', 2);
        // ペイロード部分を別の(正規に見える)値に差し替えても、対応する署名が無いため弾かれるはず
        var tamperedPayload = System.Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("{\"Email\":\"attacker@example.com\",\"Edition\":\"Pro\"}"));
        var tampered = $"{tamperedPayload}.{parts[1]}";

        var result = LicenseKeyVerifier.Validate(tampered, out var payload);

        Assert.Equal(LicenseValidationResult.SignatureMismatch, result);
        Assert.Null(payload);
    }

    [Fact]
    public void 前後の空白は無視される()
    {
        var result = LicenseKeyVerifier.Validate($"  {ValidSampleKey}  ", out var payload);

        Assert.Equal(LicenseValidationResult.Valid, result);
        Assert.NotNull(payload);
    }
}
