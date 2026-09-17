using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace Purge;

/// <summary>
/// 新バージョンのMSIインストーラーをダウンロードし、ユーザーの明示的な操作で起動するための
/// ヘルパー。
///
/// 本アプリは管理者権限起動が前提のため、更新のたびに必ずUAC同意が要り、完全な
/// バックグラウンド自動更新はできない(ROADMAP Phase13で確認済み)。そのため「ダウンロード
/// はアプリ内で済ませておき、インストーラーの実行だけユーザーに委ねる」半自動方式とする。
/// </summary>
public static class UpdateDownloader
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// MSIを一時フォルダへダウンロードする。同名ファイルが既にあれば再ダウンロードせず
    /// そのまま使う(通信の無駄・二重ダウンロードを避ける)。
    /// </summary>
    /// <param name="msiUrl">ダウンロード元URL(GitHub Releasesのasset直リンク)。</param>
    /// <param name="progress">0.0〜1.0の進捗(サーバーがContent-Lengthを返さない場合はnull)。</param>
    /// <returns>ダウンロードしたMSIのローカルパス。</returns>
    public static async Task<string> DownloadAsync(string msiUrl, IProgress<double?>? progress = null)
    {
        var fileName = Path.GetFileName(new Uri(msiUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "PurgeUpdate.msi";

        var destPath = Path.Combine(Path.GetTempPath(), "PurgeUpdate", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // GitHub User-Agentヘッダーが無いとAPI/CDN側で弾かれることがあるため明示的に付与する。
        using var request = new HttpRequestMessage(HttpMethod.Get, msiUrl);
        request.Headers.UserAgent.ParseAdd("Purge-Updater");

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        // 途中失敗時に壊れた中途半端なMSIが残らないよう、一時ファイル名で書いてから
        // 成功時だけ本来のファイル名へリネームする。
        var tempPath = destPath + ".downloading";
        await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;
                progress?.Report(totalBytes.HasValue ? (double)totalRead / totalBytes.Value : null);
            }
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tempPath, destPath);

        return destPath;
    }

    /// <summary>
    /// ダウンロード済みのMSIをGUIインストーラーとして起動する。管理者権限起動中のプロセスから
    /// 直接起動するとUIPI制限でUACダイアログが正しく出ないことがあるため、explorer.exe経由で
    /// 起動する(LicenseState.OpenUrlと同じ回避策)。
    /// </summary>
    public static void LaunchInstaller(string msiPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{msiPath}\"",
            UseShellExecute = true,
        };
        Process.Start(psi);
    }
}
