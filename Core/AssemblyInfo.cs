using System.Runtime.CompilerServices;

// テストプロジェクトから internal クラス(NameMatcher 等)へのアクセスを許可する
[assembly: InternalsVisibleTo("Purge.Tests")]
