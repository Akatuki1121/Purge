using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace UninstallTool
{
    public sealed class OrphanCandidate
    {
        public string FolderName { get; init; } = "";
        public string FullPath { get; init; } = "";
        public string ParentLocationLabel { get; init; } = "";

        /// <summary>フォルダ内の総ファイル数(取得失敗時はnull)。</summary>
        public int? FileCount { get; init; }

        /// <summary>フォルダ内の総サイズ(バイト単位、取得失敗時はnull)。</summary>
        public long? TotalSizeBytes { get; init; }

        /// <summary>フォルダ内で最も新しいファイルの最終更新日時(取得失敗時はnull)。
        /// 「最近更新されている=まだ使われている可能性がある」という目安をユーザーに示すために使う。</summary>
        public DateTime? LastModified { get; init; }
    }

    /// <summary>
    /// 典型的なアプリ格納場所(Program Files, AppData\Roaming/Local等)の直下を棚卸しし、
    /// 現在インストール済みのどのアプリにも紐付かないフォルダを「孤児候補」として抽出する。
    /// Geek Uninstaller自身が既に削除済みのアプリの残骸
    /// (Uninstallキーがもう存在しないため通常の検索では見つからないもの)を狙う機能。
    ///
    /// 判定は InstallLocation の完全一致だけでは弱すぎる(多くのアプリがInstallLocationを
    /// 登録していない、フォルダ名とDisplayNameの表記ゆれがある等)ため、
    /// DisplayName・Publisher・InstallLocationの各階層・UninstallStringのパスの各階層を
    /// すべて「既知の名前」として集約し、候補フォルダ名がそのどれかに部分一致すれば除外する。
    ///
    /// 削除は行わない。あくまで候補の提示のみ。誤検出の危険があるカテゴリのため、
    /// 呼び出し側は必ずユーザー確認を挟むこと。
    /// </summary>
    public sealed class OrphanDetector
    {
        private readonly OperationLog _log;
        private readonly MftSearchEngine _mft;
        private readonly OrphanExclusionStore _exclusions;

        public OrphanDetector(OperationLog log, OrphanExclusionStore? exclusions = null)
        {
            _log = log;
            _mft = new MftSearchEngine(log);
            _exclusions = exclusions ?? new OrphanExclusionStore();
        }

        /// <summary>
        /// 孤児検出の走査対象とする「典型的なアプリ格納場所」。
        /// Environment.SpecialFolder経由で取得することで、Windowsのバージョン/言語設定に
        /// 依存しない実際のパスを使う(ハードコードした "C:\Program Files" 等は使わない)。
        /// </summary>
        private static IEnumerable<(string Path, string Label)> GetScanRoots()
        {
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files");
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)");
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppData\\Roaming");
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppData\\Local");
        }

        /// <summary>
        /// アプリではなく、Windows/開発環境自体が正規に使う既知のフォルダ名。
        /// 誤検出防止のため、これらは孤児候補から無条件で除外する。大文字小文字は区別しない。
        /// Steam/Unity/dotnet等の主要開発ツール名を含め、実測結果を踏まえて大幅に拡充している。
        /// </summary>
        private static readonly HashSet<string> KnownNonAppFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            // Windows自体
            "Microsoft", "Google", "Packages", "Temp", "Programs", "Common Files",
            "Windows Defender", "WindowsApps", "MicrosoftEdge", "Comms",
            "ConnectedDevicesPlatform", "CrashDumps", "D3DSCache", "ElevatedDiagnostics",
            "Application Data", "Desktop", "Documents", "Favorites", "History",
            "INetHistory", "IsolatedStorage", "Temporary Internet Files", "VirtualStore",
            "PlaceholderTileLogoFolder", "Downloads", "Appearance", "ProgramData",
            "Windows Kits", "Windows Mail", "Windows Media Player", "Windows NT",
            "Windows Photo Viewer", "Windows Sidebar", "Windows Performance Analyzer",
            "Reference Assemblies", "Uninstall Information", "Application Verifier",
            "InstallShield Installation Information", "Internet Explorer", "IIS", "DIFX",
            "Hyper-V", "Package Cache", "PackageManagement", "PeerDistRepub",
            "ServiceHub", "SourceServer", "speech", "Traces", "PC Manager Store",
            "PCHealthCheck", "ToastNotificationManagerCompat", "Publishers",
            "Microsoft SDKs", "Microsoft.NET", "Microsoft Update Health Tools",
            "Microsoft GameInput", "Windows Defender Advanced Threat Protection",
            // OneDriveはUninstallレジストリキーに登録されない per-user配布のため、
            // 名前相関では検出不可能。Microsoft標準コンポーネントとして無条件除外する。
            "OneDrive",

            // 開発環境・SDK・ランタイム(正規のツールチェーンの一部)
            "dotnet", "nodejs", "Java", "PowerShell", "WindowsPowerShell", "MSBuild",
            "Nuget", "NuGet", "npm", "npm-cache", "node-gyp", "pip", "ASP.NET",
            "Microsoft Visual Studio", "Microsoft SQL Server", "ms-playwright-go",
            "ServiceHub", "CodeMaid", "VSColorOutput64", "Visual Studio Setup",
            "github-copilot", "Codota", "TabNine", "Composer",
        };

        /// <summary>
        /// DisplayNameとフォルダ名が単語レベルで一切重ならない、既知の関連ペア。
        /// 例: Epic Games Launcher(DisplayName)がインストールするフォルダは"UnrealEngine"系だが、
        /// "Epic"と"Unreal"は文字列としてまったく重ならないため、名前相関では機械的に導出不可能。
        /// このような組み合わせだけ手動でキュレートする(誤検出防止の安全策は維持しつつ、
        /// 既知の正規パターンのみ個別救済する)。
        /// キー: DisplayName中に含まれるべき語句、値: そのアプリが作る既知の関連フォルダ名。
        /// </summary>
        private static readonly (string DisplayNameContains, string RelatedFolderName)[] KnownAppFolderAliases =
        {
            ("Epic Games Launcher", "UnrealEngine"),
            ("Epic Games Launcher", "UnrealEngineLauncher"),
            ("Epic Games Launcher", "Unreal Engine"),
        };

        /// <summary>
        /// InstalledAppから、そのアプリを表しうる「既知の名前」を洗い出す。
        /// DisplayNameそのものに加え、空白を除去した表記ゆれ版、
        /// InstallLocation/UninstallStringのパスに含まれる各階層のフォルダ名も含める。
        /// これにより「表示名はBambu Studioだがフォルダ名はBambuStudio」のようなズレを吸収する。
        /// </summary>
        private static IEnumerable<string> ExtractKnownNames(InstalledApp app)
        {
            if (!string.IsNullOrWhiteSpace(app.DisplayName))
            {
                yield return app.DisplayName;
                yield return app.DisplayName.Replace(" ", "");

                foreach (var (displayNameContains, relatedFolderName) in KnownAppFolderAliases)
                {
                    if (app.DisplayName.Contains(displayNameContains, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return relatedFolderName;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(app.Publisher))
            {
                yield return app.Publisher;
            }

            foreach (var segment in SplitPathSegments(app.InstallLocation))
            {
                yield return segment;
            }

            foreach (var segment in SplitPathSegments(app.UninstallString))
            {
                yield return segment;
            }
        }

        /// <summary>
        /// パスらしき文字列から、フォルダ名として意味のありそうなセグメントを抜き出す。
        /// UninstallStringはコマンドライン全体(引数含む)の場合があるため、単純に区切るだけの簡易実装。
        /// </summary>
        private static IEnumerable<string> SplitPathSegments(string? pathLike)
        {
            if (string.IsNullOrWhiteSpace(pathLike))
            {
                yield break;
            }

            var segments = pathLike.Split(new[] { '\\', '/', '"' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                // ドライブレター(C:等)や短すぎるセグメントはノイズなので除外
                if (segment.Length > 2 && !segment.Contains(':'))
                {
                    yield return segment;
                }
            }
        }

        /// <summary>
        /// 典型的なアプリ格納場所の直下を棚卸しし、installedApps のどの既知の名前にも
        /// 部分一致しないフォルダを孤児候補として返す。
        /// 名前ベースの相関のみを使い、最終アクセス日時など曖昧な指標は使わない
        /// (誤検出が多く危険なため意図的に採用しない)。
        ///
        /// includeExecutableMetadataCheck を有効にすると、名前相関だけでは判定できなかった
        /// 候補(例: "Epic Games Launcher"とフォルダ名"UnrealEngine"のように、DisplayNameと
        /// フォルダ名が単語レベルで一切重ならないケース)について、フォルダ内のexe/dllから
        /// FileVersionInfo(CompanyName/ProductName)を読み取り、既知アプリのDisplayName/Publisherと
        /// トークン一致するか追加チェックする。ファイルI/Oが発生し低速なため、名前ベースの
        /// 1段目で除外しきれなかった候補にのみ適用する2段構えとし、既定は無効(オプトイン)。
        /// </summary>
        public List<OrphanCandidate> DetectOrphans(string driveLetter, IReadOnlyList<InstalledApp> installedApps,
            bool includeExecutableMetadataCheck = false)
        {
            _log.Info("OrphanDetect", "孤児候補検出を開始", $"ドライブ: {driveLetter}");

            var scanRoots = GetScanRoots()
                .Where(r => !string.IsNullOrEmpty(r.Path))
                .ToList();

            var parentPaths = scanRoots.Select(r => r.Path).ToList();
            var subdirectories = _mft.ListSubdirectories(driveLetter, parentPaths);

            _log.Info("OrphanDetect", "走査対象フォルダを取得", $"{subdirectories.Count}件");

            // 全インストール済みアプリの「既知の名前」を1つの集合にまとめる。
            // 空白除去済みの表記ゆれ版も含む。大文字小文字は区別しない。
            var knownNames = installedApps
                .SelectMany(ExtractKnownNames)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _log.Info("OrphanDetect", "既知の名前を集約", $"{knownNames.Count}件");

            var candidates = new List<OrphanCandidate>();

            foreach (var (name, fullPath) in subdirectories)
            {
                if (_exclusions.Contains(fullPath))
                {
                    continue;
                }

                if (KnownNonAppFolderNames.Contains(name))
                {
                    continue;
                }

                if (IsKnownByPartialMatch(name, knownNames))
                {
                    continue;
                }

                var parentLabel = scanRoots
                    .FirstOrDefault(r => fullPath.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase))
                    .Label ?? "";

                var (fileCount, totalSize, lastModified) = SummarizeFolderContents(fullPath);

                candidates.Add(new OrphanCandidate
                {
                    FolderName = name,
                    FullPath = fullPath,
                    ParentLocationLabel = parentLabel,
                    FileCount = fileCount,
                    TotalSizeBytes = totalSize,
                    LastModified = lastModified,
                });
            }

            _log.Info("OrphanDetect", "名前ベースの孤児候補検出完了", $"{candidates.Count}件(除外・一致分を差し引き後)");

            if (includeExecutableMetadataCheck && candidates.Count > 0)
            {
                _log.Info("OrphanDetect", "exe/dllメタデータによる2段目チェックを開始", $"{candidates.Count}件が対象");
                var knownTokens = BuildKnownTokenSet(installedApps);
                candidates = ReduceByExecutableMetadata(candidates, knownTokens);
                _log.Info("OrphanDetect", "2段目チェック完了", $"{candidates.Count}件(除外後)");
            }

            return candidates;
        }

        /// <summary>
        /// 削除判断の材料として、フォルダ内のファイル数・合計サイズ・最終更新日時を集計する。
        /// 「最近更新されているなら、まだ使われているかもしれない」という目安をユーザーに提示するために使う
        /// (これ自体を自動判定には使わない — 誤検出源になりやすい指標だと以前判明したため、
        /// あくまで人間が見て判断するための参考情報として提示するに留める)。
        /// 巨大フォルダでの速度劣化を避けるため、走査ファイル数に上限を設ける。
        /// </summary>
        private (int? FileCount, long? TotalSizeBytes, DateTime? LastModified) SummarizeFolderContents(string folderPath)
        {
            const int MaxFilesToSummarize = 2000;

            try
            {
                int count = 0;
                long totalSize = 0;
                DateTime? lastModified = null;

                foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
                {
                    if (count >= MaxFilesToSummarize) break;

                    try
                    {
                        var info = new FileInfo(file);
                        totalSize += info.Length;
                        if (lastModified == null || info.LastWriteTime > lastModified)
                        {
                            lastModified = info.LastWriteTime;
                        }
                        count++;
                    }
                    catch
                    {
                        // 個別ファイルの読み取り失敗はスキップ(アクセス権限等)
                    }
                }

                return (count, totalSize, lastModified);
            }
            catch (Exception ex)
            {
                _log.Warning("OrphanDetect", "フォルダ内容の集計でエラー", $"{folderPath}: {ex.Message}");
                return (null, null, null);
            }
        }


        /// <summary>
        /// インストール済みアプリのDisplayName/Publisherから、意味のあるトークン集合を構築する。
        /// exe/dllのFileVersionInfo(CompanyName/ProductName)と突き合わせるための既知トークン集合。
        /// </summary>
        private static HashSet<string> BuildKnownTokenSet(IReadOnlyList<InstalledApp> installedApps)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in installedApps)
            {
                if (!string.IsNullOrWhiteSpace(app.DisplayName))
                {
                    foreach (var token in TokenizeName(app.DisplayName)) tokens.Add(token);
                }
                if (!string.IsNullOrWhiteSpace(app.Publisher))
                {
                    foreach (var token in TokenizeName(app.Publisher)) tokens.Add(token);
                }
            }
            return tokens;
        }

        /// <summary>
        /// 名前相関で除外しきれなかった候補について、フォルダ内のexe/dllのFileVersionInfo
        /// (CompanyName/ProductName)を既知トークン集合と突き合わせ、一致すれば孤児候補から除外する。
        /// 1フォルダあたり最大 MaxFilesToSamplePerFolder 件のexe/dllのみサンプリングする
        /// (Directory.EnumerateFilesは遅延列挙のため、Takeで打ち切れば全件走査を避けられる)。
        /// </summary>
        private List<OrphanCandidate> ReduceByExecutableMetadata(
            List<OrphanCandidate> candidates, HashSet<string> knownTokens)
        {
            const int MaxFilesToSamplePerFolder = 5;
            var remaining = new List<OrphanCandidate>();

            foreach (var candidate in candidates)
            {
                var matched = false;

                try
                {
                    var sampledFiles = Directory
                        .EnumerateFiles(candidate.FullPath, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        .Take(MaxFilesToSamplePerFolder);

                    foreach (var file in sampledFiles)
                    {
                        if (TryMatchByFileMetadata(file, knownTokens, out var matchedMeta))
                        {
                            matched = true;
                            _log.Info("OrphanDetect", "exe/dllメタデータで既知アプリと一致",
                                $"{candidate.FolderName}: {matchedMeta} ({Path.GetFileName(file)})");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning("OrphanDetect", "候補フォルダのファイル列挙でエラー",
                        $"{candidate.FullPath}: {ex.Message}");
                }

                if (!matched)
                {
                    remaining.Add(candidate);
                }
            }

            return remaining;
        }

        /// <summary>
        /// 1つのexe/dllファイルのFileVersionInfoを読み、CompanyName/ProductNameが
        /// 既知トークン集合と一致するか判定する。読み取りエラーは警告ログに留め、
        /// そのファイルは不一致(=孤児候補として残す側)として扱う。
        /// </summary>
        private bool TryMatchByFileMetadata(string filePath, HashSet<string> knownTokens, out string matchedMeta)
        {
            matchedMeta = "";
            try
            {
                var info = FileVersionInfo.GetVersionInfo(filePath);
                foreach (var meta in new[] { info.CompanyName, info.ProductName })
                {
                    if (string.IsNullOrWhiteSpace(meta)) continue;

                    foreach (var token in TokenizeName(meta))
                    {
                        if (knownTokens.Contains(token))
                        {
                            matchedMeta = meta;
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning("OrphanDetect", "exe/dllメタデータ読み取りでエラー", $"{filePath}: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// 部分一致判定のノイズを避けるための最小文字数。これ未満の名前は
        /// 誤マッチ(例: 3文字の名前が無関係な単語の一部に偶然一致する)を避けるため判定に使わない。
        /// </summary>
        private const int MinNameLengthForPartialMatch = 4;

        /// <summary>
        /// 候補フォルダ名が既知の名前集合のいずれかと部分一致するか判定する。
        /// 双方向の部分一致(フォルダ名が名前を含む、または名前がフォルダ名を含む)を見ることで、
        /// "Bambu Studio" と "BambuStudio"、"obsidian" と "obsidian-updater" のような
        /// 表記ゆれ・派生フォルダの双方を拾う。短すぎる名前同士の一致は誤検出源になるため除外する。
        /// </summary>
        private static bool IsKnownByPartialMatch(string folderName, HashSet<string> knownNames)
        {
            var normalizedFolder = folderName.Replace(" ", "");
            var folderIsShort = normalizedFolder.Length < MinNameLengthForPartialMatch;

            foreach (var known in knownNames)
            {
                var normalizedKnown = known.Replace(" ", "");
                var knownIsShort = normalizedKnown.Length < MinNameLengthForPartialMatch;

                // どちらかが短い名前(例: "Git")の場合、部分一致だと無関係な文字列への
                // 誤爆リスクが高いため、完全一致のみ許可する(安全策は維持しつつ、
                // 短い正規アプリ名が無条件で孤児候補に落ちるのを防ぐ)。
                if (folderIsShort || knownIsShort)
                {
                    if (normalizedFolder.Equals(normalizedKnown, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    continue;
                }

                if (normalizedFolder.Contains(normalizedKnown, StringComparison.OrdinalIgnoreCase) ||
                    normalizedKnown.Contains(normalizedFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 完全な包含関係にならないが、単語単位では一致するケースを拾う。
                // 例: "Arduino15" と "Arduino IDE 2.3.10" は文字列として互いを含まないが、
                // どちらも"Arduino"というトークンを共有している。
                if (ShareSignificantToken(normalizedFolder, normalizedKnown))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// トークン一致判定でノイズを避けるための最小トークン長。
        /// 短いトークン(バージョン番号の断片等)の偶然一致を避ける。
        /// </summary>
        private const int MinTokenLengthForMatch = 4;

        /// <summary>
        /// 2つの名前が、意味のあるトークン(4文字以上、英字のみ)を1つでも共有するか判定する。
        /// キャメルケース境界・数字境界・記号で分割し、バージョン番号の断片などノイズになりやすい
        /// 数字トークンや短いトークンは除外する。
        /// </summary>
        private static bool ShareSignificantToken(string a, string b)
        {
            var tokensA = TokenizeName(a).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (tokensA.Count == 0) return false;

            foreach (var token in TokenizeName(b))
            {
                if (tokensA.Contains(token))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 名前をキャメルケース境界・数字境界・記号で単語トークンに分割する。
        /// 数字のみのトークンと、MinTokenLengthForMatch未満の短いトークンは除外する
        /// (バージョン番号の断片などが無関係な一致を生むのを防ぐため)。
        /// </summary>
        private static IEnumerable<string> TokenizeName(string name)
        {
            var current = new System.Text.StringBuilder();
            var raw = new List<string>();
            for (int i = 0; i < name.Length; i++)
            {
                var c = name[i];
                var isBoundary = !char.IsLetterOrDigit(c);
                var isCamelBoundary = i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1]);
                var isDigitBoundary = i > 0 && char.IsDigit(c) != char.IsDigit(name[i - 1]);

                if (isBoundary)
                {
                    if (current.Length > 0) { raw.Add(current.ToString()); current.Clear(); }
                    continue;
                }
                if (isCamelBoundary || isDigitBoundary)
                {
                    if (current.Length > 0) { raw.Add(current.ToString()); current.Clear(); }
                }
                current.Append(c);
            }
            if (current.Length > 0) raw.Add(current.ToString());

            foreach (var token in raw)
            {
                if (token.Length < MinTokenLengthForMatch) continue;
                if (token.All(char.IsDigit)) continue; // バージョン番号断片を除外
                yield return token;
            }
        }
    }
}
