using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace Purge;

/// <summary>ショートカット(.lnk)ファイルの読み書き。実体はShell、テストでは差し替える。</summary>
public interface IShortcutFileStore
{
    bool Exists(string shortcutPath);

    /// <summary>リンク先を返す。ファイルが壊れていて読めない場合は null。</summary>
    string? ReadTarget(string shortcutPath);

    void Write(string shortcutPath, string targetPath, string workingDirectory, string description);
}

public enum StartMenuRegistrationResult
{
    /// <summary>新しく作成した。</summary>
    Created,

    /// <summary>既にあったが、壊れている/別の場所を指していたため現在の実行ファイルを指すよう更新した。</summary>
    Updated,

    /// <summary>既に現在の実行ファイルを指していたため何もしなかった。</summary>
    AlreadyUpToDate,

    /// <summary>MSI版がインストール済みのため何もしなかった(ショートカットの有無はインストーラー側の選択に従う)。</summary>
    SkippedMsiInstalled,

    /// <summary>作成・更新に失敗した(失敗は操作ログに記録済み。起動は妨げない)。</summary>
    Failed,
}

/// <summary>
/// ZIP版を展開して直接起動した場合に、ユーザー単位のスタートメニュー(アプリ一覧)へ
/// Purge.lnk を自動登録する(#84)。ユーザーへの確認・通知は出さない。
///
/// MSI版がインストールされている PC では何もしない。MSIは同名のショートカットを
/// 全ユーザー用のスタートメニューに作る(作らない選択もできる、#14)ため、ここでも作ると
/// アプリ一覧に二重に並ぶうえ、インストーラーで「作らない」を選んだ意思を上書きしてしまう。
///
/// スタートへのピン留めは別機能。アプリ側から勝手にピン留めはせず、メニューから
/// ショートカットをExplorerで選択表示し、ユーザー自身が右クリックで行う。
/// </summary>
public sealed class StartMenuShortcutRegistrar
{
    private readonly OperationLog _log;
    private readonly IShortcutFileStore _store;
    private readonly string _programsFolder;
    private readonly string _commonProgramsFolder;
    private readonly Func<bool> _isMsiInstalled;

    public StartMenuShortcutRegistrar(
        OperationLog log,
        IShortcutFileStore? store = null,
        string? programsFolder = null,
        string? commonProgramsFolder = null,
        Func<bool>? isMsiInstalled = null)
    {
        _log = log;
        _store = store ?? new ShellLinkFileStore();
        _programsFolder = programsFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        _commonProgramsFolder = commonProgramsFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        _isMsiInstalled = isMsiInstalled ?? IsMsiInstalledByRegistry;
    }

    /// <summary>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Purge\Purge.lnk</summary>
    public string UserShortcutPath => Path.Combine(
        _programsFolder,
        WellKnownConstants.StartMenu.ShortcutFolderName,
        WellKnownConstants.StartMenu.ShortcutFileName);

    /// <summary>MSIが作る全ユーザー用のショートカット(Programs直下)。</summary>
    public string MsiShortcutPath => Path.Combine(
        _commonProgramsFolder,
        WellKnownConstants.StartMenu.ShortcutFileName);

    /// <summary>
    /// 起動のたびに呼ぶ。ショートカットが無い・壊れている・現在の実行ファイルと別の場所を指している
    /// 場合に、現在の実行ファイルを指す内容で作成・更新する。失敗しても例外は投げない。
    /// </summary>
    public StartMenuRegistrationResult EnsureRegistered(string exePath)
    {
        try
        {
            if (_isMsiInstalled())
            {
                _log.Info("StartMenu", "MSI版がインストール済みのため、スタートメニューへの自動登録は行いません");
                return StartMenuRegistrationResult.SkippedMsiInstalled;
            }

            return CreateOrUpdateUserShortcut(exePath);
        }
        catch (Exception ex)
        {
            _log.Warning("StartMenu", "スタートメニューへの自動登録に失敗しました(起動には影響しません)", ex.Message);
            return StartMenuRegistrationResult.Failed;
        }
    }

    /// <summary>
    /// 「スタートにピン留め」の案内用。Explorerで選択表示すべきショートカットのパスを返す。
    /// MSIの全ユーザー用ショートカットが現在の実行ファイルを指していればそれを、なければ
    /// ユーザー単位のショートカットを返す(無ければここで作る。ユーザー自身が押した操作なので、
    /// MSIでショートカットを作らない選択をしていても作ってよい)。失敗時は null。
    /// </summary>
    public string? EnsureShortcutForPinning(string exePath)
    {
        try
        {
            if (PointsTo(MsiShortcutPath, exePath))
            {
                return MsiShortcutPath;
            }

            CreateOrUpdateUserShortcut(exePath);
            return UserShortcutPath;
        }
        catch (Exception ex)
        {
            _log.Warning("StartMenu", "ピン留め用のショートカットを用意できませんでした", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// ピン留め用のショートカットをExplorerで選択表示する。ピン留めそのものはユーザーが右クリックで行う
    /// (アプリ側から無断でピン留めはしない)。成功したら true。
    /// </summary>
    public bool RevealShortcutForPinning(string exePath)
    {
        var shortcutPath = EnsureShortcutForPinning(exePath);
        if (shortcutPath == null)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(
                WellKnownConstants.StartMenu.Explorer,
                string.Format(CultureInfo.InvariantCulture, WellKnownConstants.StartMenu.ExplorerSelectArgsFormat, shortcutPath))
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning("StartMenu", "ショートカットをエクスプローラーで開けませんでした", ex.Message);
            return false;
        }
    }

    private StartMenuRegistrationResult CreateOrUpdateUserShortcut(string exePath)
    {
        var shortcutPath = UserShortcutPath;
        var existed = _store.Exists(shortcutPath);

        if (existed && PointsTo(shortcutPath, exePath))
        {
            return StartMenuRegistrationResult.AlreadyUpToDate;
        }

        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? string.Empty;
        _store.Write(shortcutPath, exePath, workingDirectory, WellKnownConstants.StartMenu.ShortcutDescription);

        var result = existed ? StartMenuRegistrationResult.Updated : StartMenuRegistrationResult.Created;
        _log.Info(
            "StartMenu",
            existed ? "スタートメニューのショートカットを更新しました" : "スタートメニューにショートカットを作成しました",
            shortcutPath);
        return result;
    }

    private bool PointsTo(string shortcutPath, string exePath)
    {
        if (!_store.Exists(shortcutPath))
        {
            return false;
        }

        var target = _store.ReadTarget(shortcutPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(target),
                Path.GetFullPath(exePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // 壊れたショートカットで不正なパス文字列が入っていた場合は「別の場所を指している」扱いにして作り直す。
            return false;
        }
    }

    private static bool IsMsiInstalledByRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WellKnownConstants.StartMenu.MsiInstallRegistryKey);
            return key?.GetValue(WellKnownConstants.StartMenu.MsiInstallRegistryValue) != null;
        }
        catch
        {
            // 読めない場合はMSI版なしとみなす(登録自体は失敗しても起動を妨げない)。
            return false;
        }
    }
}
