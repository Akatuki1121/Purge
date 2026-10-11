"""Unit tests for deterministic Issue relation evidence."""
import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).with_name("issue-relation-analysis.py")
SPEC = importlib.util.spec_from_file_location("issue_relation_analysis", SCRIPT)
relation = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(relation)


class RelationEvidenceTests(unittest.TestCase):
    def test_parses_japanese_issue_form_fields(self):
        fields = relation.parse_fields(
            "### Windows のバージョン\nWindows 11 24H2\n"
            "### 実際の結果\n起動時に NullReferenceException が表示されます。\n"
            "### 関連ログ・スクリーンショット・エラーメッセージ\nNullReferenceException at MainWindow\n"
        )
        self.assertEqual(fields["windows"], "Windows 11 24H2")
        self.assertIn("NullReferenceException", fields["actual"])
        self.assertIn("NullReferenceException", fields["logs"])

    def test_exact_error_signature_is_strong_evidence(self):
        current = {"body": "### 実際の結果\n起動に失敗\n### 関連ログ・スクリーンショット・エラーメッセージ\nSystem.NullReferenceException"}
        candidate = {"body": "### 実際の結果\n別の説明\n### 関連ログ・スクリーンショット・エラーメッセージ\nSystem.NullReferenceException"}
        self.assertIn("NullReferenceException", relation.relation_evidence(current, candidate))

    def test_same_actual_result_requires_exact_normalized_match(self):
        current = {"body": "### 実際の結果\nアプリ起動時に設定ファイルを読み込めず終了します"}
        candidate = {"body": "### 実際の結果\nアプリ起動時に設定ファイルを読み込めず終了します"}
        self.assertIn("完全一致", relation.relation_evidence(current, candidate))

    def test_similar_words_alone_do_not_create_relation(self):
        current = {"body": "### 実際の結果\nアプリ起動時に設定ファイルを読み込めず終了します"}
        candidate = {"body": "### 実際の結果\n設定画面でファイルを選択するとアプリが起動します"}
        self.assertIsNone(relation.relation_evidence(current, candidate))

    def test_short_generic_actual_result_is_not_enough(self):
        current = {"body": "### 実際の結果\nエラーが出ます"}
        candidate = {"body": "### 実際の結果\nエラーが出ます"}
        self.assertIsNone(relation.relation_evidence(current, candidate))

    def test_explicit_issue_references_are_extracted_without_self_reference(self):
        refs = relation.extract_issue_refs("関連: #12 and https://github.com/org/repo/issues/34, self #7", 7)
        self.assertEqual(refs, {12, 34})

    def test_fingerprint_changes_when_report_changes(self):
        before = {"title": "Bug", "body": "old", "labels": [{"name": "bug"}]}
        after = {"title": "Bug", "body": "new", "labels": [{"name": "bug"}]}
        self.assertNotEqual(relation.fingerprint(before), relation.fingerprint(after))


if __name__ == "__main__":
    unittest.main()
