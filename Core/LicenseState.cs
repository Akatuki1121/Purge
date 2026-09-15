using System;
using System.IO;

namespace Purge
{
    /// <summary>
    /// 無料版/有料版(Pro)の機能ゲート。
    ///
    /// 判定の優先順位:
    /// 1. 開発者専用のdev_unlock.flag(自分の開発機のみ)
    /// 2. ユーザーが入力・保存したライセンスキー(LicenseKeyVerifierで署名検証)
    /// 3. どちらもなければ無料版
    ///
    /// ライセンスキーはオフラインで完結する署名検証方式のため、購入後は
    /// インターネット接続なしで永続的に使える(サーバー側の失効機能は今のところ持たない)。
    /// </summary>
    public static class LicenseState
    {
        private const string DevUnlockFlagFileName = "dev_unlock.flag";
        private const string LicenseKeyFileName = "license.key";

        // 本番Stripeアカウントの商品・Payment Link(2026-09-13、本人確認審査中に作成)。
        public const string PurchaseUrl = "https://buy.stripe.com/bJeaERa508ig9vTcxc1Nu00";

        /// <summary>
        /// 購入ページ(既定ブラウザ)を開く。
        ///
        /// 本アプリはapp.manifestでrequireAdministratorを指定しており、常に管理者権限(高い整合性レベル)で
        /// 起動している。一方、既定のブラウザは通常のユーザー権限(中程度の整合性レベル)で動くプロセスのため、
        /// 管理者権限プロセスから直接 Process.Start(url) を呼ぶと、Windowsのプロセス整合性の仕組み
        /// (UIPI: User Interface Privilege Isolation)によりブラウザの起動がブロック・無視されることがある
        /// (例外は投げられず、単に何も起きないように見える)。
        ///
        /// 回避策として、explorer.exe を経由してURLを開く。explorer.exeは常に標準ユーザーの整合性レベルで
        /// 動作しているため、そこにURLを渡すと標準ユーザー文脈でブラウザが起動でき、この問題を回避できる。
        /// </summary>
        public static void OpenPurchasePage()
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = PurchaseUrl,
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }

        private static bool? _cachedIsProUnlocked;
        private static LicensePayload? _cachedPayload;

        /// <summary>Pro版機能(複数アプリ一括処理、スキャン結果エクスポート等)が有効かどうか。</summary>
        public static bool IsProUnlocked
        {
            get
            {
                _cachedIsProUnlocked ??= HasDevUnlockFlag() || HasValidStoredLicenseKey();
                return _cachedIsProUnlocked.Value;
            }
        }

        /// <summary>有効なライセンスキーから読み取った情報(メールアドレス等)。未認証時はnull。</summary>
        public static LicensePayload? CurrentLicense => _cachedPayload;

        /// <summary>
        /// ライセンスキーを検証し、有効であれば保存してPro機能を有効化する。
        /// UIの「ライセンスキーを入力」画面から呼ばれる想定。
        /// </summary>
        public static LicenseValidationResult ActivateLicense(string licenseKey)
        {
            var result = LicenseKeyVerifier.Validate(licenseKey, out var payload);

            if (result == LicenseValidationResult.Valid && payload != null)
            {
                try
                {
                    File.WriteAllText(GetLicenseKeyPath(), licenseKey.Trim());
                }
                catch
                {
                    // 保存に失敗しても今回のセッション中は有効として扱う(次回起動時は再入力が必要になる)
                }

                _cachedPayload = payload;
                _cachedIsProUnlocked = true;
            }

            return result;
        }

        /// <summary>保存済みのライセンスキーを削除し、無料版に戻す。</summary>
        public static void DeactivateLicense()
        {
            try
            {
                var path = GetLicenseKeyPath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 削除失敗は致命的でないため無視する
            }

            _cachedPayload = null;
            _cachedIsProUnlocked = HasDevUnlockFlag();
        }

        private static bool HasValidStoredLicenseKey()
        {
            try
            {
                var path = GetLicenseKeyPath();
                if (!File.Exists(path))
                {
                    return false;
                }

                var storedKey = File.ReadAllText(path);
                var result = LicenseKeyVerifier.Validate(storedKey, out var payload);

                if (result == LicenseValidationResult.Valid)
                {
                    _cachedPayload = payload;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static string GetLicenseKeyPath()
        {
            // exeと同じフォルダではなく、ユーザーのAppDataに保存する
            // (Program Filesは書き込み権限が無いことが多く、exeフォルダ直下だと再インストール時に消えるため)
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Purge");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, LicenseKeyFileName);
        }

        private static bool HasDevUnlockFlag()
        {
            try
            {
                var exeDir = AppContext.BaseDirectory;
                var flagPath = Path.Combine(exeDir, DevUnlockFlagFileName);
                return File.Exists(flagPath);
            }
            catch
            {
                return false;
            }
        }
    }
}
