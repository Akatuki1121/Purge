using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Purge;

namespace Purge.UI
{
    /// <summary>
    /// InstalledAppからアイコンを抽出し、WPFで表示可能なImageSourceに変換する。
    /// 候補は DisplayIcon(「,インデックス」指定も解釈する) → InstallLocation配下のexe
    /// (アプリ名に近い順) → UninstallStringのexe、の優先順に列挙し、抽出に成功するまで順に試す。
    /// すべて失敗した場合はWindows標準の汎用アプリアイコンにフォールバックし、
    /// リストが空欄だらけにならないようにする。
    /// </summary>
    public static class AppIconResolver
    {
        // AppListItem.ResolveIcon()は複数アプリ分を並行してバックグラウンドスレッドから呼び出す想定のため、
        // Icon.ExtractAssociatedIconやshell32.dll呼び出しを同時に複数スレッドから行うと不安定になる懸念がある。
        // 単純化のため全体を1つのロックで直列化する(アイコン抽出は数十msなので、直列化しても体感差は小さい)。
        private static readonly object ResolveLock = new();

        /// <summary>ExtractIconExで一度に取得するアイコンの数。常に1個だけを使う。</summary>
        private const int IconExtractionCount = 1;

        /// <summary>アイコンインデックスの先頭。指定が無い場合の既定の取得位置。</summary>
        private const int FirstIconIndex = 0;

        /// <summary>shell32.dll内にある「汎用実行ファイル」アイコンのインデックス。</summary>
        private const int GenericAppIconIndex = 2;

        /// <summary>exe名がDisplayNameと(記号・空白を除いて)完全一致した場合のスコア。</summary>
        private const int ExactNameMatchScore = 100;

        /// <summary>exe名とDisplayNameのどちらかがもう一方の接頭辞になっている場合のスコア。</summary>
        private const int PrefixNameMatchScore = 40;

        /// <summary>DisplayNameの単語がexe名に含まれる場合の、単語1つあたりのスコア。</summary>
        private const int TokenNameMatchScore = 15;

        /// <summary>単語一致の判定に使う最小の単語長。短い断片の偶然一致を避ける。</summary>
        private const int MinTokenLengthForNameMatch = 3;

        /// <summary>InstallLocation直下にあるexeへの加点。サブフォルダー内より本体らしいため。</summary>
        private const int TopLevelExecutableBonus = 10;

        /// <summary>
        /// アンインストーラーや補助プロセスらしいexeへの減点。
        /// 完全一致のスコアを大きく上回る分だけ引き、他に候補がある限り選ばれないようにする
        /// (候補がそれしか無い場合は、最終候補として引き続き使われる)。
        /// </summary>
        private const int AuxiliaryExecutablePenalty = 1000;

        /// <summary>
        /// アンインストーラー/インストーラー/更新・補助プロセスのexe名にありがちな断片。
        /// これらを含む名前のexeはアプリ本体ではないことが多く、汎用のインストーラー
        /// アイコンしか持たない場合もあるため、InstallLocation内の候補では後ろに回す。
        /// </summary>
        private static readonly string[] AuxiliaryExecutableNameFragments =
        {
            "unins", "uninstall", "setup", "install", "update", "crashpad", "helper", "service", "msiexec",
        };

        public static ImageSource? Resolve(InstalledApp app)
        {
            lock (ResolveLock)
            {
                // 候補を優先順に試し、最初に抽出できたアイコンを採用する。
                // 以前は先頭の候補1つだけで判定していたため、そのファイルにアイコンが
                // 無い・読めないだけで、後続の候補に本体のアイコンがあっても汎用に落ちていた。
                foreach (var candidate in EnumerateIconCandidates(app))
                {
                    var icon = TryExtractIcon(candidate.Path, candidate.IconIndex);
                    if (icon != null) return icon;
                }

                // ここまでで見つからなかった場合、Windows標準の汎用実行ファイルアイコンにフォールバックする。
                return GetGenericAppIcon();
            }
        }

        /// <summary>
        /// 実行ファイル探索と同じ順序(DisplayIcon→InstallLocation配下→UninstallStringのexe)で
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

            var displayIcon = ParseExecutableReference(app.DisplayIconPath);
            if (displayIcon.Path.Length > 0 && File.Exists(displayIcon.Path) && seen.Add(displayIcon.Path))
            {
                yield return displayIcon.Path;
            }

            // UninstallStringのexeはアンインストーラーであることが多く、本体のアイコンを
            // 持たない場合があるため、InstallLocation配下のexeを先に並べる。
            foreach (var path in RankExecutables(CollectInstallLocationExecutables(app.InstallLocation), app.DisplayName))
            {
                if (seen.Add(path))
                {
                    yield return path;
                }
            }

            var uninstall = ParseExecutableReference(app.UninstallString);
            if (uninstall.Path.Length > 0 && File.Exists(uninstall.Path) && seen.Add(uninstall.Path))
            {
                yield return uninstall.Path;
            }
        }

        /// <summary>
        /// アイコン抽出用の候補を優先順に列挙する。DisplayIconだけは「,インデックス」の
        /// 指定を保持したまま渡し、抽出側でそのインデックスのアイコンを取得する。
        /// </summary>
        private static IEnumerable<(string Path, int? IconIndex)> EnumerateIconCandidates(InstalledApp app)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var displayIcon = ParseExecutableReference(app.DisplayIconPath);
            if (displayIcon.Path.Length > 0 && File.Exists(displayIcon.Path) && seen.Add(displayIcon.Path))
            {
                yield return (displayIcon.Path, displayIcon.IconIndex);
            }

            foreach (var path in FindExecutablePaths(app))
            {
                if (seen.Add(path))
                {
                    yield return (path, null);
                }
            }
        }

        /// <summary>
        /// "C:\Path\app.exe,0" のようなDisplayIcon値や、
        /// "\"C:\Path\uninstall.exe\" /args" のようなUninstallString値から、
        /// 実行ファイルパスと(あれば)アイコンインデックスを抜き出す。
        /// 環境変数(%ProgramFiles%等)が含まれている場合は先に展開する。
        /// </summary>
        private static (string Path, int? IconIndex) ParseExecutableReference(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return ("", null);

            var trimmed = Environment.ExpandEnvironmentVariables(raw.Trim()).Trim();

            // "path,index" 形式(DisplayIconでよく使われる)のインデックス部分を分離する。
            // 負のインデックスはExtractIconEx側でリソースIDとして解釈されるため、そのまま保持する。
            int? iconIndex = null;
            var commaIndex = trimmed.LastIndexOf(',');
            if (commaIndex > 0 && int.TryParse(trimmed[(commaIndex + 1)..].Trim(), out var parsedIndex))
            {
                iconIndex = parsedIndex;
                trimmed = trimmed[..commaIndex];
            }

            trimmed = trimmed.Trim().Trim('"').Trim();

            // UninstallStringのように後ろに引数が続く場合、拡張子までで切る
            var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex > 0)
            {
                trimmed = trimmed[..(exeIndex + 4)];
            }

            return (trimmed.Trim().Trim('"'), iconIndex);
        }

        /// <summary>
        /// InstallLocation直下と、直下のサブフォルダ(1階層のみ)から実行ファイルを集める。
        /// 深い階層まで探索すると起動時の負荷が増えるため1階層に留める。
        /// 読めないフォルダーがあっても他のフォルダーの探索は続ける(1つのアクセス拒否で
        /// 候補全体を失わないため)。
        /// </summary>
        private static List<(string Path, bool IsTopLevel)> CollectInstallLocationExecutables(string? installLocation)
        {
            var results = new List<(string Path, bool IsTopLevel)>();
            if (string.IsNullOrWhiteSpace(installLocation))
            {
                return results;
            }

            var root = Environment.ExpandEnvironmentVariables(installLocation.Trim()).Trim().Trim('"').Trim();
            if (root.Length == 0 || !Directory.Exists(root))
            {
                return results;
            }

            AddExecutableFiles(results, root, isTopLevel: true);

            string[] subDirectories;
            try
            {
                subDirectories = Directory.GetDirectories(root);
            }
            catch
            {
                return results;
            }

            foreach (var subDirectory in subDirectories)
            {
                AddExecutableFiles(results, subDirectory, isTopLevel: false);
            }

            return results;
        }

        private static void AddExecutableFiles(
            List<(string Path, bool IsTopLevel)> results, string directory, bool isTopLevel)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                // アクセス拒否等で読めないフォルダーは飛ばす
                return;
            }

            foreach (var file in files)
            {
                results.Add((file, isTopLevel));
            }
        }

        /// <summary>
        /// InstallLocation配下のexeを「アプリ本体らしさ」の高い順に並べ替える。
        /// DisplayNameとファイル名の近さを主軸にし、アンインストーラーや補助プロセス
        /// らしい名前は後ろへ、同点なら大きいファイル(本体であることが多い)を先にする。
        /// </summary>
        private static IEnumerable<string> RankExecutables(
            List<(string Path, bool IsTopLevel)> executables, string? displayName)
        {
            return executables
                .OrderByDescending(exe => ComputeExecutablePriority(exe.Path, exe.IsTopLevel, displayName))
                .ThenByDescending(exe => GetFileLength(exe.Path))
                .ThenBy(exe => exe.Path, StringComparer.OrdinalIgnoreCase)
                .Select(exe => exe.Path);
        }

        private static int ComputeExecutablePriority(string exePath, bool isTopLevel, string? displayName)
        {
            var exeName = Path.GetFileNameWithoutExtension(exePath);
            var score = isTopLevel ? TopLevelExecutableBonus : 0;

            if (IsAuxiliaryExecutable(exeName))
            {
                score -= AuxiliaryExecutablePenalty;
            }

            score += ComputeNameSimilarityScore(exeName, displayName);
            return score;
        }

        private static bool IsAuxiliaryExecutable(string? exeName)
        {
            if (string.IsNullOrEmpty(exeName)) return false;

            foreach (var fragment in AuxiliaryExecutableNameFragments)
            {
                if (exeName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// exe名(拡張子なし)とDisplayNameの近さをスコア化する。
        /// 記号・空白を除いた完全一致を最高とし、接頭辞一致、単語単位の一致の順に加点する。
        /// </summary>
        private static int ComputeNameSimilarityScore(string? exeName, string? displayName)
        {
            if (string.IsNullOrWhiteSpace(exeName) || string.IsNullOrWhiteSpace(displayName))
            {
                return 0;
            }

            var normalizedExeName = NormalizeNameForComparison(exeName);
            var normalizedDisplayName = NormalizeNameForComparison(displayName);
            if (normalizedExeName.Length == 0 || normalizedDisplayName.Length == 0)
            {
                return 0;
            }

            if (normalizedExeName.Equals(normalizedDisplayName, StringComparison.Ordinal))
            {
                return ExactNameMatchScore;
            }

            if (normalizedDisplayName.StartsWith(normalizedExeName, StringComparison.Ordinal) ||
                normalizedExeName.StartsWith(normalizedDisplayName, StringComparison.Ordinal))
            {
                return PrefixNameMatchScore;
            }

            var score = 0;
            foreach (var token in SplitDisplayNameTokens(displayName))
            {
                if (normalizedExeName.Contains(token, StringComparison.Ordinal))
                {
                    score += TokenNameMatchScore;
                }
            }

            return score;
        }

        /// <summary>名前の比較用に、英数字だけを残して小文字化する。</summary>
        private static string NormalizeNameForComparison(string name)
        {
            var builder = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// DisplayNameを記号・空白で単語に分割し、正規化して返す。
        /// 短すぎる単語は偶然の一致を避けるため除外する。
        /// </summary>
        private static IEnumerable<string> SplitDisplayNameTokens(string displayName)
        {
            var current = new StringBuilder();
            foreach (var c in displayName)
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(char.ToLowerInvariant(c));
                    continue;
                }

                if (current.Length >= MinTokenLengthForNameMatch)
                {
                    yield return current.ToString();
                }

                current.Clear();
            }

            if (current.Length >= MinTokenLengthForNameMatch)
            {
                yield return current.ToString();
            }
        }

        private static long GetFileLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static ImageSource? TryExtractIcon(string path, int? iconIndex)
        {
            // .icoファイルはアイコンインデックスの概念が無く、ファイル自体がアイコンなので直接読み込む。
            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                return TryLoadIconFile(path);
            }

            // DisplayIconで先頭以外が指定されている場合は、そのインデックス(負数=リソースID)を先に試す。
            // ExtractAssociatedIconは常に先頭のアイコンを返すため、指定がある場合は使えない。
            if (iconIndex is int specifiedIndex && specifiedIndex != FirstIconIndex)
            {
                var specified = TryExtractIconAtIndex(path, specifiedIndex);
                if (specified != null) return specified;
            }

            return TryExtractIconAtIndex(path, FirstIconIndex) ?? TryExtractAssociatedIcon(path);
        }

        private static ImageSource? TryLoadIconFile(string path)
        {
            try
            {
                using var icon = new Icon(path);
                var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                imageSource.Freeze();
                return imageSource;
            }
            catch
            {
                // アイコン読み込みはベストエフォート。失敗してもアプリ一覧表示自体は継続する。
                return null;
            }
        }

        /// <summary>
        /// ExtractIconExで指定インデックスのアイコンを取得する。
        /// 負のインデックスはリソースIDとして解釈される(ExtractIconExの仕様)。
        /// 一覧の表示欄は小さいが、大きいアイコンを縮小表示する方が滲まないため大きい方を優先する。
        /// </summary>
        private static ImageSource? TryExtractIconAtIndex(string path, int iconIndex)
        {
            var largeIcons = new IntPtr[IconExtractionCount];
            var smallIcons = new IntPtr[IconExtractionCount];

            try
            {
                int extracted = ExtractIconEx(path, iconIndex, largeIcons, smallIcons, IconExtractionCount);
                if (extracted <= 0) return null;

                var handle = largeIcons[0] != IntPtr.Zero ? largeIcons[0] : smallIcons[0];
                if (handle == IntPtr.Zero) return null;

                var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                    handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                imageSource.Freeze();
                return imageSource;
            }
            catch
            {
                // アイコン抽出はベストエフォート。失敗してもアプリ一覧表示自体は継続する。
                return null;
            }
            finally
            {
                if (largeIcons[0] != IntPtr.Zero) DestroyIcon(largeIcons[0]);
                if (smallIcons[0] != IntPtr.Zero) DestroyIcon(smallIcons[0]);
            }
        }

        private static ImageSource? TryExtractAssociatedIcon(string path)
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
        /// shell32.dll内の「汎用実行ファイル」アイコンを取得する。
        /// 毎回shell32.dllを開くコストを避けるため、初回取得分をキャッシュして使い回す。
        /// </summary>
        private static ImageSource? GetGenericAppIcon()
        {
            if (_cachedGenericIcon != null) return _cachedGenericIcon;

            var largeIcons = new IntPtr[IconExtractionCount];
            var smallIcons = new IntPtr[IconExtractionCount];

            try
            {
                var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var shell32Path = Path.Combine(systemDir, "shell32.dll");

                int extracted = ExtractIconEx(shell32Path, GenericAppIconIndex, largeIcons, smallIcons, IconExtractionCount);
                if (extracted <= 0)
                {
                    return null;
                }

                // 他のアイコンと揃え、大きい方を優先する(縮小表示の方が滲まない)。
                var handle = largeIcons[0] != IntPtr.Zero ? largeIcons[0] : smallIcons[0];
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                    handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
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
    }
}
