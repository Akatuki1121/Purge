using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UninstallTool;

namespace UninstallTool.UI
{
    /// <summary>
    /// InstalledAppからアイコンを抽出し、WPFで表示可能なImageSourceに変換する。
    /// DisplayIconPath → UninstallStringのexe → InstallLocation直下のexe → InstallLocationのサブフォルダ
    /// (1階層のみ)のexe、の順で探索する。それでも見つからない場合はWindows標準の汎用アプリアイコンに
    /// フォールバックし、リストが空欄だらけにならないようにする。
    /// </summary>
    public static class AppIconResolver
    {
        // AppListItem.ResolveIcon()は複数アプリ分を並行してバックグラウンドスレッドから呼び出す想定のため、
        // Icon.ExtractAssociatedIconやshell32.dll呼び出しを同時に複数スレッドから行うと不安定になる懸念がある。
        // 単純化のため全体を1つのロックで直列化する(アイコン抽出は数十msなので、直列化しても体感差は小さい)。
        private static readonly object ResolveLock = new();

        public static ImageSource? Resolve(InstalledApp app)
        {
            lock (ResolveLock)
            {
                var candidatePath = FindExecutablePath(app);

                if (candidatePath != null && File.Exists(candidatePath))
                {
                    var icon = TryExtractIcon(candidatePath);
                    if (icon != null) return icon;
                }

                // ここまでで見つからなかった場合、Windows標準の汎用実行ファイルアイコンにフォールバックする。
                return GetGenericAppIcon();
            }
        }

        /// <summary>
        /// アイコン解決と同じ探索順序(DisplayIcon→UninstallStringのexe→InstallLocation配下)で
        /// 実行ファイルのフルパスを特定する。発行元の補完(AppPublisherResolver)でも同じロジックを使うため公開する。
        /// </summary>
        public static string? FindExecutablePath(InstalledApp app)
        {
            foreach (var path in FindExecutablePaths(app))
            {
                return path;
            }

            return null;
        }

        public static IEnumerable<string> FindExecutablePaths(InstalledApp app)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in new[]
            {
                ExtractExecutablePath(app.DisplayIconPath),
                ExtractExecutablePath(app.UninstallString),
            })
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && seen.Add(path))
                {
                    yield return path;
                }
            }

            foreach (var path in FindExesInInstallLocation(app.InstallLocation))
            {
                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }

        private static ImageSource? TryExtractIcon(string path)
        {
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;

                var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                imageSource.Freeze();
                return imageSource;
            }
            catch
            {
                // アイコン抽出はベストエフォート。失敗してもアプリ一覧表示自体は継続する。
                return null;
            }
        }

        #region Win32: shell32.dllからの汎用アイコン取得

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern int ExtractIconEx(string lpszFile, int nIconIndex,
            IntPtr[] phiconLarge, IntPtr[] phiconSmall, int nIcons);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static ImageSource? _cachedGenericIcon;

        /// <summary>
        /// shell32.dll内の「汎用実行ファイル」アイコン(インデックス2)を取得する。
        /// 毎回shell32.dllを開くコストを避けるため、初回取得分をキャッシュして使い回す。
        /// </summary>
        private static ImageSource? GetGenericAppIcon()
        {
            if (_cachedGenericIcon != null) return _cachedGenericIcon;

            var largeIcons = new IntPtr[1];
            var smallIcons = new IntPtr[1];

            try
            {
                var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var shell32Path = Path.Combine(systemDir, "shell32.dll");

                int extracted = ExtractIconEx(shell32Path, 2, largeIcons, smallIcons, 1);
                if (extracted <= 0 || smallIcons[0] == IntPtr.Zero)
                {
                    return null;
                }

                var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                    smallIcons[0], System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                imageSource.Freeze();
                _cachedGenericIcon = imageSource;
                return imageSource;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (largeIcons[0] != IntPtr.Zero) DestroyIcon(largeIcons[0]);
                if (smallIcons[0] != IntPtr.Zero) DestroyIcon(smallIcons[0]);
            }
        }

        #endregion

        /// <summary>
        /// "C:\Path\app.exe,0" のようなDisplayIcon値や、
        /// "\"C:\Path\uninstall.exe\" /args" のようなUninstallString値から、実行ファイルパスを抜き出す。
        /// </summary>
        private static string? ExtractExecutablePath(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var trimmed = raw.Trim().Trim('"');

            // "path,index" 形式(DisplayIconでよく使われる)のインデックス部分を除去
            var commaIndex = trimmed.LastIndexOf(',');
            if (commaIndex > 0 && int.TryParse(trimmed[(commaIndex + 1)..], out _))
            {
                trimmed = trimmed[..commaIndex];
            }

            // UninstallStringのように後ろに引数が続く場合、拡張子までで切る
            var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex > 0)
            {
                trimmed = trimmed[..(exeIndex + 4)];
            }

            return trimmed;
        }

        /// <summary>
        /// InstallLocation直下、それで見つからなければ直下のサブフォルダ(1階層のみ)から
        /// それらしい実行ファイルを1つ探す。深い階層まで探索すると起動時の負荷が増えるため1階層に留める。
        /// </summary>
        private static IEnumerable<string> FindExesInInstallLocation(string? installLocation)
        {
            if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
            {
                yield break;
            }

            var candidates = new List<string>();
            try
            {
                var exeFiles = Directory.GetFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly);
                candidates.AddRange(exeFiles);

                foreach (var subDir in Directory.GetDirectories(installLocation))
                {
                    var subExeFiles = Directory.GetFiles(subDir, "*.exe", SearchOption.TopDirectoryOnly);
                    candidates.AddRange(subExeFiles);
                }
            }
            catch
            {
                yield break;
            }

            foreach (var candidate in candidates)
            {
                yield return candidate;
            }
        }
    }
}
