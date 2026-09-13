using System;
using System.ComponentModel;

namespace Purge.UI
{
    /// <summary>
    /// OrphanCandidateにチェックボックス選択状態を持たせ、
    /// 判断材料(サイズ・最終更新日時・場所ラベル)を初心者にも分かりやすい表示形式に整形するラッパー。
    /// </summary>
    public sealed class SelectableOrphanCandidate : INotifyPropertyChanged
    {
        private bool _isSelected;

        public OrphanCandidate Candidate { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public string FolderName => Candidate.FolderName;
        public string FullPath => Candidate.FullPath;

        /// <summary>
        /// "AppData\Roaming" のような専門的なパス表記ではなく、
        /// 「アプリの設定・データ置き場」のような初心者にも意味が伝わる日本語ラベルに変換する。
        /// </summary>
        public string LocationDescription => Candidate.ParentLocationLabel switch
        {
            "Program Files" or "Program Files (x86)" => "アプリ本体の置き場",
            "AppData\\Roaming" => "アプリの設定・データ置き場",
            "AppData\\Local" => "アプリのキャッシュ・一時データ置き場",
            _ => Candidate.ParentLocationLabel,
        };

        /// <summary>ファイル数を「123個」のように表示。集計できなかった場合は不明表記。</summary>
        public string FileCountDisplay => Candidate.FileCount is int count ? $"{count:N0}個" : "不明";

        /// <summary>合計サイズを読みやすい単位(KB/MB/GB)で表示。</summary>
        public string SizeDisplay
        {
            get
            {
                if (Candidate.TotalSizeBytes is not long bytes) return "不明";

                return bytes switch
                {
                    < 1024 => $"{bytes} B",
                    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
                    < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
                    _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB",
                };
            }
        }

        /// <summary>
        /// 最終更新日時を「見てすぐ分かる」相対表現に変換する。
        /// 「最近更新されている=まだ使われているかもしれない」という判断のヒントを直感的に伝える。
        /// </summary>
        public string LastModifiedDisplay
        {
            get
            {
                if (Candidate.LastModified is not DateTime lastModified) return "不明";

                var age = DateTime.Now - lastModified;
                var relative = age switch
                {
                    { TotalDays: < 7 } => "1週間以内 ⚠ 最近使われた可能性あり",
                    { TotalDays: < 30 } => "1か月以内",
                    { TotalDays: < 180 } => "半年以内",
                    { TotalDays: < 365 } => "1年以内",
                    _ => "1年以上前",
                };
                return $"{lastModified:yyyy/MM/dd} ({relative})";
            }
        }

        /// <summary>直近1週間以内に更新されている場合、警告色で強調するためのフラグ。</summary>
        public bool LooksRecentlyUsed =>
            Candidate.LastModified is DateTime lastModified && (DateTime.Now - lastModified).TotalDays < 7;

        public SelectableOrphanCandidate(OrphanCandidate candidate)
        {
            Candidate = candidate;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
