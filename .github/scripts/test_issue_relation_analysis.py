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
        self.assertIn("0x80070005", fields["logs"])

    def test_same_distinctive_error_identifier_is_strong_evidence(self):
        current = {"body": "### 実際の結果\n起動に失敗\n### 関連ログ・スクリーンショット・エラーメッセージ\nHRESULT: 0x80070005"}
        candidate = {"body": "### 実際の結果\n別の説明\n### 関連ログ・スクリーンショット・エラーメッセージ\nHRESULT: 0x80070005"}
        self.assertIn("0x80070005", relation.relation_evidence(current, candidate))

    def test_parses_legacy_japanese_issue_headings(self):
        fields = relation.parse_fields(
            "### 症状\nアップデート時に起動のたびUAC確認が出る\n"
            "### 期待する動作\n起動時に追加確認が出ない\n"
            "### 現象\n起動時に HRESULT 0x80070005 が表示される\n"
        )
        self.assertEqual(fields["actual"], "アップデート時に起動のたびUAC確認が出る")
        self.assertEqual(fields["expected"], "起動時に追加確認が出ない")


    def test_legacy_and_current_headings_can_match_exactly(self):
        legacy = {"body": "### 症状\nアップデート後、起動のたびにUAC確認が表示されます。"}
        current = {"body": "### 実際の結果\nアップデート後、起動のたびにUAC確認が表示されます。"}
        self.assertIn("完全一致", relation.relation_evidence(legacy, current))

    def test_different_legacy_symptoms_about_updates_do_not_match(self):
        uac_issue = {"body": "### 症状\nアップデート後、起動のたびにUAC確認が表示されます。"}
        shortcut_issue = {"body": "### 現象\nアップデート時にデスクトップのショートカットが一度削除されます。"}
        self.assertIsNone(relation.relation_evidence(uac_issue, shortcut_issue))

    def test_real_legacy_issue_examples_keep_distinct_update_symptoms_separate(self):
        # Real excerpts from Purge Issues #91 and #92, retained as regression fixtures.
        uac_issue = {
            "number": 91,
            "title": "アップデート後の「Purgeを起動する」でPurge.exeのUACが出る",
            "body": "## 症状\nアップデート時、インストーラー完了画面で「Purgeを起動する」を選ぶと、Purge.exe 起動のたびにUACの確認が出る。\n\n## 背景\n省略",
        }
        shortcut_issue = {
            "number": 92,
            "title": "アップデート時にデスクトップ/スタートメニューのショートカットが一度削除される",
            "body": "## 症状\nアップデートでMSIをインストールすると、デスクトップ／スタートメニューのショートカットが一度削除され、作り直される。\n\n## 背景\n省略",
        }
        self.assertIn("UACの確認", relation.parse_fields(uac_issue["body"])["actual"])
        self.assertIn("ショートカット", relation.parse_fields(shortcut_issue["body"])["actual"])
        self.assertIsNone(relation.relation_evidence(uac_issue, shortcut_issue))

    def test_combined_legacy_behavior_and_error_heading_extracts_signature(self):
        issue = {
            "body": "### 実際の動作・エラーメッセージ\n起動に失敗します。 HRESULT: 0x80070005"
        }
        self.assertIn("0x80070005", relation.issue_signatures(issue))

    def test_partial_distinctive_symptom_phrase_matches_reworded_report(self):
        current = {
            "body": "### 症状\nアップデート時、インストーラー完了画面で「Purgeを起動する」を選ぶと、Purge.exe 起動のたびにUACの確認が出る。"
        }
        candidate = {
            "body": "### 実際の結果\n更新後、インストーラー完了画面で「Purgeを起動する」を選ぶと、Purge.exe 起動のたびにUACの確認が表示される。"
        }
        evidence = relation.relation_evidence(current, candidate)
        self.assertIsNotNone(evidence)
        self.assertIn("24文字以上", evidence)

    def test_partial_symptom_matching_does_not_merge_issue_91_and_92(self):
        uac_issue = {
            "body": "## 症状\nアップデート時、インストーラー完了画面で「Purgeを起動する」を選ぶと、Purge.exe 起動のたびにUACの確認が出る。"
        }
        shortcut_issue = {
            "body": "## 症状\nアップデートでMSIをインストールすると、デスクトップ／スタートメニューのショートカットが一度削除され、作り直される。"
        }
        self.assertIsNone(relation.relation_evidence(uac_issue, shortcut_issue))

    def test_short_common_phrase_does_not_create_relation(self):
        current = {"body": "### 症状\nアップデート時にアプリが起動しない"}
        candidate = {"body": "### 症状\nアップデート時にショートカットが消える"}
        self.assertIsNone(relation.relation_evidence(current, candidate))

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


class StrongPRRelationTests(unittest.TestCase):
    def test_pr_with_same_distinctive_error_identifier_is_candidate(self):
        issue = {"body": "### 実際の結果\n起動に失敗\n### 関連ログ・スクリーンショット・エラーメッセージ\nHRESULT: 0x80070005"}
        pr = {"title": "fix: handle HRESULT 0x80070005 during startup", "body": "Permission error"}
        evidence = relation.pr_relation_evidence(issue, pr)
        self.assertIn("0x80070005", evidence)

    def test_pr_with_only_generic_shared_word_is_not_candidate(self):
        issue = {"body": "### 実際の結果\nアプリ起動時に設定ファイルを読み込めず終了します"}
        pr = {"title": "fix: application file handling", "body": "Improve startup behavior"}
        self.assertIsNone(relation.pr_relation_evidence(issue, pr))

    def test_search_terms_are_separate_not_joined_with_or(self):
        issue = {"body": "### 実際の結果\nインストーラー完了画面でPurgeを起動すると、起動のたびにUACの確認が出る。\n### 関連ログ・スクリーンショット・エラーメッセージ\nHRESULT: 0x80070005"}
        terms = relation.evidence_search_terms(issue)
        self.assertGreater(len(terms), 1)
        self.assertTrue(all(" OR " not in term for term in terms))
        queries = [relation.build_evidence_query("issue", term) for term in terms]
        self.assertEqual(len(queries), len(terms))
        self.assertTrue(all(query.count('"') == 2 for query in queries))


if __name__ == "__main__":
    unittest.main()
