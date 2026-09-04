using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UninstallTool
{
    /// <summary>
    /// 「アプリ名らしき文字列」と「候補文字列」を安全に突き合わせるための共通ロジック。
    /// 元はOrphanDetectorの誤検出対策(短い名前の完全一致救済、トークン単位の部分一致)として
    /// 実装したものを、ResidueScanner等の他の名前ベース検索でも使えるよう切り出した。
    ///
    /// 単純な文字列.Contains()判定は、"Git"のような短い/一般的な名前だと
    /// 無関係な文字列("Legitimate", "Digital"等)にまで一致してしまう。
    /// このクラスはその誤検出を避けつつ、表記ゆれ(空白の有無、キャメルケース、
    /// バージョン番号付与)は拾えるようにする。
    /// </summary>
    internal static class NameMatcher
    {
        /// <summary>
        /// 部分一致判定のノイズを避けるための最小文字数。これ未満の名前は
        /// 誤マッチ(例: 3文字の名前が無関係な単語の一部に偶然一致する)を避けるため
        /// 部分一致には使わず、完全一致のみ許可する。
        /// </summary>
        private const int MinNameLengthForPartialMatch = 4;

        /// <summary>
        /// トークン一致判定でノイズを避けるための最小トークン長。
        /// 短いトークン(バージョン番号の断片等)の偶然一致を避ける。
        /// </summary>
        private const int MinTokenLengthForMatch = 4;

        /// <summary>
        /// candidate(フォルダ名・キー名・サービス名等)がappName(検索対象のアプリ名)と
        /// 安全に一致すると言えるか判定する。
        /// 双方向の部分一致(候補がappNameを含む、またはappNameが候補を含む)に加え、
        /// トークン単位の一致(例: "Arduino15" と "Arduino IDE 2.3.10")も見る。
        /// どちらかが短い名前の場合は完全一致のみ許可し、誤検出を防ぐ。
        /// </summary>
        public static bool IsSafeMatch(string candidate, string appName)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(appName))
            {
                return false;
            }

            var normalizedCandidate = candidate.Replace(" ", "");
            var normalizedAppName = appName.Replace(" ", "");

            var candidateIsShort = normalizedCandidate.Length < MinNameLengthForPartialMatch;
            var appNameIsShort = normalizedAppName.Length < MinNameLengthForPartialMatch;

            // どちらかが短い名前(例: "Git")の場合、部分一致だと無関係な文字列への
            // 誤爆リスクが高いため、完全一致のみ許可する。
            if (candidateIsShort || appNameIsShort)
            {
                return normalizedCandidate.Equals(normalizedAppName, StringComparison.OrdinalIgnoreCase);
            }

            if (normalizedCandidate.Contains(normalizedAppName, StringComparison.OrdinalIgnoreCase) ||
                normalizedAppName.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 完全な包含関係にならないが、単語単位では一致するケースを拾う。
            return ShareSignificantToken(normalizedCandidate, normalizedAppName);
        }

        /// <summary>
        /// フルパスやコマンドライン("C:\Program Files\Git\cmd"、タスクスケジューラのパス等)を
        /// パス区切り/空白で分割し、いずれかのセグメントがappNameと安全に一致するか判定する。
        /// IsSafeMatchをテキスト全体にそのまま適用すると、短いアプリ名("Git"等)が
        /// 長いパス全体と完全一致することは無いため常に不一致になってしまう
        /// (短い名前は完全一致のみ許可するルールのため)。セグメント単位で見ることで、
        /// パス中の1階層としての正当な完全一致("...\Git\...")は引き続き拾えるようにする。
        /// </summary>
        public static bool IsSafeMatchAnywhere(string candidateText, string appName)
        {
            if (string.IsNullOrWhiteSpace(candidateText) || string.IsNullOrWhiteSpace(appName))
            {
                return false;
            }

            var segments = candidateText.Split(
                new[] { '\\', '/', ' ', ';', ',', '"' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                if (IsSafeMatch(segment, appName))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 2つの名前が、意味のあるトークン(4文字以上、英数字のみ・数字のみは除く)を
        /// 1つでも共有するか判定する。
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
        /// OrphanDetectorのexe/dllメタデータ照合(トークン集合の構築)からも利用するため公開する。
        /// </summary>
        public static IEnumerable<string> TokenizeName(string name)
        {
            var current = new StringBuilder();
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
