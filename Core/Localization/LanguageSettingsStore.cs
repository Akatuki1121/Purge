using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Purge.Localization
{
    /// <summary>
    /// 表示言語の選択を %LocalAppData%\Purge\settings.json に保存・読み込みする。
    /// ライセンスキーやバックアップと同じ Purge フォルダに置く(exeフォルダは再インストールで消えるため)。
    /// 読み書きの失敗は致命的ではないので握りつぶし、読み込み失敗時は Auto にフォールバックする。
    /// 将来ほかの設定を足しても消さないよう、保存時は既存のJSONを読み込んで language だけ更新する。
    /// </summary>
    internal static class LanguageSettingsStore
    {
        private const string SettingsDirectoryName = "Purge";
        private const string SettingsFileName = "settings.json";
        private const string LanguageKey = "language";

        private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

        public static bool TryLoad(out AppLanguage language)
        {
            language = AppLanguage.Auto;
            try
            {
                var path = GetSettingsPath();
                if (!File.Exists(path))
                {
                    return false;
                }

                var text = ReadRootOrEmpty(path)[LanguageKey]?.GetValue<string>();
                if (!Enum.TryParse<AppLanguage>(text, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    return false;
                }

                language = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Save(AppLanguage language)
        {
            try
            {
                var path = GetSettingsPath();
                var root = ReadRootOrEmpty(path);
                root[LanguageKey] = language.ToString();

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, root.ToJsonString(s_writeOptions));
            }
            catch
            {
                // 保存に失敗しても今回のセッションでは切り替わる(次回起動時は元の設定に戻る)。
            }
        }

        private static JsonObject ReadRootOrEmpty(string path)
        {
            try
            {
                return File.Exists(path)
                    ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                    : new JsonObject();
            }
            catch (JsonException)
            {
                // 壊れた設定ファイルは空として扱い、上書きして復旧させる。
                return new JsonObject();
            }
        }

        /// <summary>テストが実際のユーザー設定を触らないよう、保存先を差し替えるための口。通常は null。</summary>
        internal static string? PathOverride { get; set; }

        private static string GetSettingsPath()
        {
            if (PathOverride is not null)
            {
                return PathOverride;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                SettingsDirectoryName,
                SettingsFileName);
        }
    }
}
