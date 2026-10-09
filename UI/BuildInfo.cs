using System.Reflection;

namespace Purge.UI;

/// <summary>
/// 実行中のビルドがインストール版(Release)か開発版(Debug)かの識別と、表示用バージョン文字列を提供する。
/// 開発版はUI/Purge.UI.csprojが「直近のリリースタグ + -dev + コミットハッシュ」でバージョンを付けるため、
/// 常に最新のタグ以上として見える(例: 1.2.1-dev+abc1234)。インストール版との取り違えを防ぐため、
/// ウィンドウタイトル・バージョン表示・アイコンを区別し、更新確認は自動では行わない。
/// </summary>
internal static class BuildInfo
{
    /// <summary>開発版(Debugビルド)なら true。</summary>
    public static bool IsDevBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>ウィンドウタイトルに使うアプリ名。開発版は「Purge (Dev)」。</summary>
    public static string AppTitle => IsDevBuild ? "Purge (Dev)" : "Purge";

    /// <summary>
    /// バージョン表示用の文字列。インストール版は従来どおりアセンブリバージョン(例: 1.2.1.0)、
    /// 開発版は情報バージョン(例: 1.2.1-dev+abc1234)を返す。
    /// </summary>
    public static string VersionText
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            if (IsDevBuild)
            {
                var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational))
                {
                    return informational;
                }
            }

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }
    }
}
