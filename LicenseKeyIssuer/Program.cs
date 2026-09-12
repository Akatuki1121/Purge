// LicenseKeyIssuer: 開発者(奏星さん)専用のライセンスキー発行ツール。
//
// 【重要】このプロジェクトで生成される秘密鍵(*.private.xml)は絶対にGitリポジトリにコミットしないこと。
// .gitignoreで "LicenseKeyIssuer/*.private.xml" を除外済み(このファイル作成と合わせて確認する)。
//
// 使い方:
//   1. 初回のみ: dotnet run -- genkey
//      → private_key.private.xml(秘密鍵、絶対に公開しない)と public_key.xml(公開鍵)を生成
//      → public_key.xml の中身を Core/LicenseKeyVerifier.cs の PublicKeyXml に貼り付ける
//   2. キー発行: dotnet run -- issue customer@example.com
//      → そのメールアドレス向けのライセンスキーを標準出力に表示(コピーしてメールで送る)

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length == 0)
{
    PrintUsage();
    return;
}

switch (args[0])
{
    case "genkey":
        GenerateKeyPair();
        break;
    case "issue":
        if (args.Length < 2)
        {
            Console.WriteLine("エラー: メールアドレスを指定してください。例: dotnet run -- issue foo@example.com");
            return;
        }
        IssueKey(args[1]);
        break;
    default:
        PrintUsage();
        break;
}

static void PrintUsage()
{
    Console.WriteLine("使い方:");
    Console.WriteLine("  dotnet run -- genkey                    鍵ペアを新規生成(初回のみ)");
    Console.WriteLine("  dotnet run -- issue <メールアドレス>      ライセンスキーを発行");
}

static void GenerateKeyPair()
{
    const string privateKeyPath = "private_key.private.xml";

    if (File.Exists(privateKeyPath))
    {
        Console.WriteLine($"エラー: {privateKeyPath} は既に存在します。上書きすると既存の全キーが無効になるため中止しました。");
        return;
    }

    using var rsa = RSA.Create(2048);
    File.WriteAllText(privateKeyPath, rsa.ToXmlString(includePrivateParameters: true));
    File.WriteAllText("public_key.xml", rsa.ToXmlString(includePrivateParameters: false));

    Console.WriteLine("鍵ペアを生成しました。");
    Console.WriteLine($"  秘密鍵: {privateKeyPath} (絶対にコミット・共有しないこと)");
    Console.WriteLine("  公開鍵: public_key.xml");
    Console.WriteLine();
    Console.WriteLine("public_key.xml の中身を Core/LicenseKeyVerifier.cs の PublicKeyXml 定数に貼り付けてください。");
}

static void IssueKey(string email)
{
    const string privateKeyPath = "private_key.private.xml";

    if (!File.Exists(privateKeyPath))
    {
        Console.WriteLine($"エラー: {privateKeyPath} が見つかりません。先に 'dotnet run -- genkey' を実行してください。");
        return;
    }

    using var rsa = RSA.Create();
    rsa.FromXmlString(File.ReadAllText(privateKeyPath));

    var payload = new
    {
        Email = email,
        IssuedAtUtc = DateTime.UtcNow,
        Edition = "Pro",
        ExpiresAtUtc = (DateTime?)null, // 買い切りライセンスのため無期限
    };

    var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
    var signature = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    var licenseKey = $"{Convert.ToBase64String(payloadBytes)}.{Convert.ToBase64String(signature)}";

    Console.WriteLine("ライセンスキーを発行しました:");
    Console.WriteLine();
    Console.WriteLine(licenseKey);
    Console.WriteLine();
    Console.WriteLine($"({email} 向け、Pro版、無期限)");
}
