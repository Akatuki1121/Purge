using System;
using System.IO;

namespace Purge;

/// <summary>
/// InstallLocation が空のアプリについて、UninstallString や DisplayIcon が指す
/// 実行ファイルの置き場所から、アプリ本体のフォルダーを導出する。
/// InstallLocation の代わりにサイズ計測の対象を得るための補完であり、
/// 導出できない場合は null を返して「不明」のままにする。
/// </summary>
public static class InstallDirectoryDeriver
{
    /// <summary>Windows Installer 本体。アプリ本体ではないため導出対象から外す。</summary>
    private const string MsiExecFileName = "msiexec.exe";

    public static string? Derive(InstalledApp app)
    {
        return ToInstallDirectory(ExtractExecutablePath(app.UninstallString))
               ?? ToInstallDirectory(ExtractExecutablePath(app.DisplayIconPath));
    }

    /// <summary>
    /// "\"C:\Path\app.exe\" /args" のようなコマンド文字列や、
    /// "C:\Path\app.exe,0" のような DisplayIcon 値から、ファイルパス部分だけを取り出す。
    /// </summary>
    internal static string? ExtractExecutablePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = Environment.ExpandEnvironmentVariables(raw).Trim().Trim('"');

        // "path,index" 形式（DisplayIcon で使われる）のインデックス部分を除去する。
        var commaIndex = trimmed.LastIndexOf(',');
        if (commaIndex > 0 && int.TryParse(trimmed[(commaIndex + 1)..], out _))
        {
            trimmed = trimmed[..commaIndex];
        }

        // 引数が続く場合は拡張子までで切る。
        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
        {
            trimmed = trimmed[..(exeIndex + 4)];
        }

        return trimmed;
    }

    private static string? ToInstallDirectory(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            if (string.Equals(Path.GetFileName(filePath), MsiExecFileName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            // Windows フォルダーや System フォルダー自体はアプリのフォルダーではない。
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (string.Equals(directory, windowsDirectory, StringComparison.OrdinalIgnoreCase)
                || string.Equals(directory, systemDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return directory;
        }
        catch
        {
            return null;
        }
    }
}
