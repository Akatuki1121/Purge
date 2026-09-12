using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UninstallTool
{
    /// <summary>
    /// ライセンスキーに含める情報。JSON化してから署名する。
    /// </summary>
    public sealed class LicensePayload
    {
        public string? Email { get; init; }
        public DateTime IssuedAtUtc { get; init; }
        public string Edition { get; init; } = "Pro";

        /// <summary>
        /// 買い切りライセンスのため既定はnull(無期限)。将来サブスクリプション等を扱う場合に備えて残す。
        /// </summary>
        public DateTime? ExpiresAtUtc { get; init; }
    }

    public enum LicenseValidationResult
    {
        Valid,
        InvalidFormat,
        SignatureMismatch,
        Expired,
    }

    /// <summary>
    /// ライセンスキーの署名検証。RSA公開鍵はアプリに埋め込んで配布して問題ない
    /// (公開鍵は「検証」しかできず「発行」はできないため、これを盗まれても偽キーは作れない)。
    /// 秘密鍵はLicenseKeyIssuer側(開発者の手元)にのみ存在し、絶対にこのリポジトリ/配布物に含めない。
    ///
    /// キー形式: "{Base64(JSON payload)}.{Base64(RSA署名)}"
    /// オフラインで完結する検証方式のため、サーバーやインターネット接続を必要としない。
    /// </summary>
    public static class LicenseKeyVerifier
    {
        // LicenseKeyIssuerで生成した鍵ペアのうち、公開鍵のみをここに埋め込む。
        // 公開鍵は「検証」しかできず「発行」はできないため、公開しても安全(秘密鍵とは非対称)。
        // 2026-09-11: 秘密鍵をチャット上に誤って表示してしまった事故を受け、鍵をローテーション(再生成)した。
        // 発行済みライセンスキーはまだ0件だったため、旧鍵からの移行対応は不要だった。
        // (この鍵は LicenseKeyIssuer/private_key.private.xml と対になっている実際のファイルの中身と
        //  完全に一致することを直接ファイル比較で確認済み — 過去に複数回genkeyを実行してしまい
        //  ペアがズレていた問題があったため、今後変更する際は必ずファイルの中身と突き合わせること)
        private const string PublicKeyXml = "<RSAKeyValue><Modulus>6jrkw/WFhvLYtK2srZAhRAxDfqloC0UIdG3Z3sIlt8SCJY1OKSaHCtWFeChfuIexRcP32KgZDeFqk/DW6ay7er7z6KNoRa+KFQwasaGAwz1g94EHROjyYJrfR3SzcT4H4PhEcClNHYyAlCGnG3lvPfIjpkvPTWZyVfg5hoXZtw78QNZH5naaz51ac5IKpeZgDGknjVgbtCkFHC6h/Wull0AaY3EKOw0T8vpTVG/9fkBcvXoKNIjMB9lQz/FNah/KbDjS8k96Wzscj+Gv14ZYg+ia2eSKmiLFAp6lsplEklhclK9Nagriis8rQmJxgqmHEb87nvy0K/MPuAPhgfhfDQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        public static LicenseValidationResult Validate(string licenseKey, out LicensePayload? payload)
        {
            payload = null;

            if (string.IsNullOrWhiteSpace(PublicKeyXml))
            {
                // 公開鍵が未設定の状態は「実装未完了」であり、安全側に倒してすべて拒否する。
                return LicenseValidationResult.SignatureMismatch;
            }

            var parts = licenseKey.Trim().Split('.', 2);
            if (parts.Length != 2)
            {
                return LicenseValidationResult.InvalidFormat;
            }

            byte[] payloadBytes, signatureBytes;
            try
            {
                payloadBytes = Convert.FromBase64String(parts[0]);
                signatureBytes = Convert.FromBase64String(parts[1]);
            }
            catch (FormatException)
            {
                return LicenseValidationResult.InvalidFormat;
            }

            using var rsa = RSA.Create();
            rsa.FromXmlString(PublicKeyXml);

            bool signatureValid = rsa.VerifyData(
                payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            if (!signatureValid)
            {
                return LicenseValidationResult.SignatureMismatch;
            }

            try
            {
                payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes);
            }
            catch (JsonException)
            {
                return LicenseValidationResult.InvalidFormat;
            }

            if (payload == null)
            {
                return LicenseValidationResult.InvalidFormat;
            }

            if (payload.ExpiresAtUtc is DateTime expires && expires < DateTime.UtcNow)
            {
                return LicenseValidationResult.Expired;
            }

            return LicenseValidationResult.Valid;
        }
    }
}
