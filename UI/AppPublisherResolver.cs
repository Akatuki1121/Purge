using System.Diagnostics;
using System.IO;
using UninstallTool;

namespace UninstallTool.UI
{
    /// <summary>
    /// InstalledApp.Publisherがレジストリに登録されておらず空欄の場合に、
    /// 実行ファイルのFileVersionInfo(CompanyName)から発行元を補完する。
    /// アイコン解決(AppIconResolver)と同じ実行ファイル探索ロジックを再利用する。
    /// あくまで補完(ベストエフォート)であり、取得できなければ引き続き空欄のまま扱う
    /// (「不明」のような固定文字列は表示しない — 誤情報を断定的に見せないため)。
    /// </summary>
    public static class AppPublisherResolver
    {
        public static string? Resolve(InstalledApp app)
        {
            if (!string.IsNullOrWhiteSpace(app.Publisher))
            {
                return app.Publisher;
            }

            var exePath = AppIconResolver.FindExecutablePath(app);
            if (exePath == null || !File.Exists(exePath))
            {
                return null;
            }

            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                return string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName;
            }
            catch
            {
                // 読み取り失敗はベストエフォートとして無視し、空欄のまま扱う。
                return null;
            }
        }
    }
}
