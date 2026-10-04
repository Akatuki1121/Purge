namespace Purge.Localization
{
    /// <summary>
    /// ユーザーが選べる表示言語。Auto は OS の表示言語に従う(日本語なら日本語、それ以外は英語)。
    /// 実際に画面へ適用される言語(Loc.Current)は Japanese か English のどちらかになる。
    /// </summary>
    public enum AppLanguage
    {
        Auto,
        Japanese,
        English,
    }
}
