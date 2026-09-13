using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Purge
{
    /// <summary>
    /// 1件の操作ステップを表す。「何を」「いつ」「どういう状態で」実行したかを記録する。
    /// </summary>
    public sealed class OperationStep
    {
        public DateTime Timestamp { get; }
        public string Category { get; }      // 例: "RegistryScan", "MftSearch", "AppUninstall"
        public string Description { get; }   // 例: "アプリXの残存レジストリキーを検索中"
        public string? Detail { get; }       // 補足情報(対象パスなど)
        public OperationStepLevel Level { get; }

        public OperationStep(string category, string description, string? detail, OperationStepLevel level)
        {
            Timestamp = DateTime.Now;
            Category = category;
            Description = description;
            Detail = detail;
            Level = level;
        }

        public override string ToString()
        {
            var detailPart = string.IsNullOrEmpty(Detail) ? "" : $" ({Detail})";
            return $"[{Timestamp:HH:mm:ss.fff}] [{Category}] {Description}{detailPart}";
        }
    }

    public enum OperationStepLevel
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// アプリ全体で共有する操作ログ。リングバッファでメモリ使用量を抑えつつ、
    /// エラー発生時に直前の文脈をまとめて取得できるようにする。
    /// 常駐前提ではなく、1回の実行(スキャン〜アンインストール)内で完結する用途を想定。
    /// </summary>
    public sealed class OperationLog
    {
        private readonly LinkedList<OperationStep> _steps = new();
        private readonly int _capacity;
        private readonly object _lock = new();

        public OperationLog(int capacity = 500)
        {
            _capacity = capacity;
        }

        public void Info(string category, string description, string? detail = null)
            => Add(category, description, detail, OperationStepLevel.Info);

        public void Warning(string category, string description, string? detail = null)
            => Add(category, description, detail, OperationStepLevel.Warning);

        public void Error(string category, string description, string? detail = null)
            => Add(category, description, detail, OperationStepLevel.Error);

        private void Add(string category, string description, string? detail, OperationStepLevel level)
        {
            lock (_lock)
            {
                _steps.AddLast(new OperationStep(category, description, detail, level));
                while (_steps.Count > _capacity)
                {
                    _steps.RemoveFirst();
                }
            }
        }

        /// <summary>
        /// 直近N件のログを時系列で取得する(エラー報告の文脈として使う)。
        /// </summary>
        public IReadOnlyList<OperationStep> GetRecent(int count = 30)
        {
            lock (_lock)
            {
                return _steps.Reverse().Take(count).Reverse().ToList();
            }
        }

        /// <summary>
        /// 例外発生時、直近の操作履歴と例外情報を合わせて
        /// 「何をしようとして、どこで、何が起きたか」を時系列の文章として組み立てる。
        /// テンプレート穴埋めではなく、実際のログをそのまま文脈として使う。
        /// </summary>
        public string BuildErrorReport(Exception ex, int contextSteps = 15)
        {
            var recent = GetRecent(contextSteps);
            var sb = new StringBuilder();

            sb.AppendLine("## エラー報告");
            sb.AppendLine();
            sb.AppendLine($"発生日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($".NETバージョン: {Environment.Version}");
            sb.AppendLine();

            sb.AppendLine("### 直前の操作の流れ");
            if (recent.Count == 0)
            {
                sb.AppendLine("(記録された操作ログがありません)");
            }
            else
            {
                foreach (var step in recent)
                {
                    sb.AppendLine($"- {step}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("### 発生したエラー");
            sb.AppendLine($"種類: {ex.GetType().FullName}");
            sb.AppendLine($"メッセージ: {ex.Message}");
            if (ex.InnerException != null)
            {
                sb.AppendLine($"内部例外: {ex.InnerException.Message}");
            }
            sb.AppendLine();
            sb.AppendLine("### スタックトレース");
            sb.AppendLine("```");
            sb.AppendLine(ex.StackTrace ?? "(なし)");
            sb.AppendLine("```");

            return sb.ToString();
        }

        public void Clear()
        {
            lock (_lock)
            {
                _steps.Clear();
            }
        }
    }
}
