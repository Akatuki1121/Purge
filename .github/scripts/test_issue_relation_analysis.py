"""Unit tests for deterministic Issue relation evidence."""
import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).with_name("issue-relation-analysis.py")
SPEC = importlib.util.spec_from_file_location("issue_relation_analysis", SCRIPT)
relation = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(relation)
relation.REPO = "Akatuki1121/Purge"


class RelationEvidenceTests(unittest.TestCase):
    def test_parses_japanese_issue_form_fields(self):
        fields = relation.parse_fields(
            "### Windows のバージョン\nWindows 11 24H2\n"
            "### 実際の結果\n起動時にエラーコード 0x80070005 が表示されます。\n"
            "### 関連ログ・スクリーンショット・エラーメッセージ\nHRESULT: 0x80070005\n"
        )
        self.assertEqual(fields["windows"], "Windows 11 24H2")
        self.assertIn("0x80070005", fields["actual"])
        self.assertIn("0x80070005", fields["logs"])

    def test_same_distinctive_error_identifier_is_strong_evidence(self):
        current = {"body": "### 実際の結果\n起動に失敗\n### 関連ログ・スクリーンショット・エラーメッセージ\nHRESULT: 0x80070005"}
        candidate = {"body": "### 実際の結果\n別の説明\n### 関連ログ・スクリーンショット・エラーメッセージ\nHRESULT: 0x80070005"}
        self.assertIn("0x80070005", relation.relation_evidence(current, candidate))

    def test_generic_exception_name_alone_is_not_enough(self):
        current = {"body": "### 実際の結果\n起動時に NullReferenceException が出ます"}
        candidate = {"body": "### 実際の結果\n設定画面で NullReferenceException が出ます"}
        self.assertIsNone(relation.relation_evidence(current, candidate))

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
        refs = relation.extract_issue_refs(
            "関連: #12 and https://github.com/Akatuki1121/Purge/issues/34, self #7",
            7,
        )
        self.assertEqual(refs, {12, 34})

    def test_foreign_repository_issue_url_is_not_treated_as_local(self):
        text = (
            "Related: https://github.com/another-owner/another-repo/issues/34 "
            "and https://github.com/Akatuki1121/OtherProject/issues/35"
        )
        self.assertEqual(relation.extract_issue_refs(text, 7), set())

    def test_foreign_issue_url_does_not_leak_number_reference(self):
        text = "See https://github.com/another-owner/another-repo/issues/34"
        self.assertNotIn(34, relation.reference_targets(text))

    def test_local_issue_url_and_shorthand_reference_are_supported(self):
        text = "See https://github.com/akatuki1121/purge/issues/34 and #35"
        self.assertEqual(relation.reference_targets(text), {34, 35})

    def test_fingerprint_changes_when_report_changes(self):
        before = {"title": "Bug", "body": "old", "labels": [{"name": "bug"}]}
        after = {"title": "Bug", "body": "new", "labels": [{"name": "bug"}]}
        self.assertNotEqual(relation.fingerprint(before), relation.fingerprint(after))


if __name__ == "__main__":
    unittest.main()
