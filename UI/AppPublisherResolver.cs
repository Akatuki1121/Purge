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

            foreach (var exePath in AppIconResolver.FindExecutablePaths(app))
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(exePath);
                    if (!string.IsNullOrWhiteSpace(info.CompanyName))
                    {
                        return info.CompanyName.Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(info.ProductName))
                    {
                        return info.ProductName.Trim();
                    }
                }
                catch
                {
                    // 1つの実行ファイルで読めなくても、次の候補を試す。
                }
            }

            return null;
        }
    }
}
