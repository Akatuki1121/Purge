using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Purge;

/// <summary>
/// NTFS の $MFT を直接読み取り、フォルダーごとの合計サイズを引ける索引。
/// Everything と同じ原理（$MFT の逐次読み）だが、外部プロセスや外部サービスには
/// 一切依存せず、Purge 単独で動作する。
///
/// ディレクトリ走査と違い、個々のファイルの ACL に左右されないため、
/// 読み取り権限の無いサブフォルダーが1つあるだけで全体が「不明」になる問題が起きない。
/// 管理者権限と NTFS が必要で、満たせない環境では TryBuild が null を返し、
/// 呼び出し側は従来のディレクトリ走査へフォールバックする。
/// </summary>
public sealed class MftSizeIndex
{
    private readonly Dictionary<string, long> _folderSizes;

    private MftSizeIndex(Dictionary<string, long> folderSizes)
    {
        _folderSizes = folderSizes;
    }

    /// <summary>
    /// フォルダーの合計サイズ（配下のファイルの実サイズ合計）を返す。
    /// 索引に無いパス（別ボリューム、削除済み等）は null。
    /// </summary>
    public long? GetFolderSizeBytes(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return null;
        }

        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath.Trim()));
            return _folderSizes.TryGetValue(normalized, out var bytes) ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 指定ドライブ（例: "C:\"）の $MFT を読み、索引を構築する。
    /// 非NTFS・権限不足・読み取り失敗の場合は null を返し、例外は投げない。
    /// $MFT 全体の読み取りは数秒規模になり得るため、バックグラウンドスレッドから呼ぶこと。
    /// </summary>
    public static MftSizeIndex? TryBuild(string driveRoot, OperationLog log)
    {
        try
        {
            var root = Path.GetPathRoot(driveRoot);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var entries = MftVolumeReader.ReadAllEntries(root, log);
            if (entries == null)
            {
                return null;
            }

            var folderSizes = MftFolderSizeAggregator.BuildFolderSizes(entries, root);
            log.Info("MftSize", "$MFTサイズ索引を構築", $"{root} フォルダー{folderSizes.Count}件");
            return new MftSizeIndex(folderSizes);
        }
        catch (Exception ex)
        {
            log.Warning("MftSize", "$MFTサイズ索引の構築に失敗", ex.Message);
            return null;
        }
    }
}

/// <summary>
/// $MFT の1レコードから抽出した、サイズ集計に必要な最小限の情報。
/// </summary>
internal sealed class MftFileEntry
{
    public ulong ReferenceNumber { get; init; }
    public ulong ParentReferenceNumber { get; init; }
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long DataSizeBytes { get; init; }

    /// <summary>
    /// このレコードの中で無名 $DATA が見つかり、DataSizeBytes が確定しているか。
    /// 大きなファイルでは $DATA が属性リスト経由で拡張レコード側に置かれ、
    /// 基底レコードだけではサイズが分からないことがある（その場合は false）。
    /// </summary>
    public bool DataSizeResolved { get; init; }

    /// <summary>
    /// 拡張レコードから得た断片情報であることを表す。
    /// ReferenceNumber は基底レコードの番号を指す。
    /// </summary>
    public bool IsExtensionData { get; init; }

    /// <summary>この断片が $FILE_NAME（親と名前）を持つか。</summary>
    public bool HasName { get; init; }

    /// <summary>$FILE_NAME の名前空間の順位（長い名前を優先するためのスコア）。</summary>
    public int NameScore { get; init; }
}

/// <summary>
/// NTFS ファイルレコード（FILE レコード）のパーサー。
/// 更新シーケンス配列（USA）のフィックスアップを適用してから属性を走査し、
/// $FILE_NAME（親参照番号・名前）と無名 $DATA（実サイズ）を取り出す。
/// 形式は NTFS の公開仕様に基づく。合成したレコードで単体テストできるよう、
/// ボリューム読み取りとは分離してある。
/// </summary>
internal static class MftRecordParser
{
    /// <summary>ファイルレコード先頭のシグネチャ "FILE"。</summary>
    private const string RecordSignature = "FILE";

    private static class RecordHeaderOffset
    {
        public const int Signature = 0;              // char[4]
        public const int UpdateSequenceOffset = 4;   // uint16
        public const int UpdateSequenceCount = 6;    // uint16（USN 自体を含む個数）
        public const int FirstAttributeOffset = 20;  // uint16
        public const int Flags = 22;                 // uint16
    }

    /// <summary>レコードフラグ: 使用中。</summary>
    private const ushort RecordFlagInUse = 0x0001;

    /// <summary>レコードフラグ: ディレクトリ。</summary>
    private const ushort RecordFlagDirectory = 0x0002;

    private static class AttributeHeaderOffset
    {
        public const int Type = 0;                    // uint32
        public const int Length = 4;                  // uint32
        public const int NonResidentFlag = 8;         // byte
        public const int NameLength = 9;              // byte（文字数）
        public const int ResidentValueLength = 16;    // uint32
        public const int ResidentValueOffset = 20;    // uint16
        public const int NonResidentStartingVcn = 16; // uint64（このエクステントの開始VCN）
        public const int NonResidentDataSize = 48;    // uint64（実サイズ）
    }

    private static class AttributeType
    {
        public const uint FileName = 0x30;
        public const uint Data = 0x80;
        public const uint End = 0xFFFFFFFF;
    }

    private static class FileNameValueOffset
    {
        public const int ParentReference = 0;  // uint64（下位48bitが親のレコード番号）
        public const int NameLength = 64;      // byte（文字数）
        public const int Namespace = 65;       // byte
        public const int Name = 66;            // UTF-16LE
    }

    /// <summary>$FILE_NAME の名前空間。長い名前を優先するための順位付けに使う。</summary>
    private static class FileNameNamespace
    {
        public const byte Posix = 0;
        public const byte Win32 = 1;
        public const byte Dos = 2;
        public const byte Win32AndDos = 3;
    }

    /// <summary>親参照番号として意味を持つ下位ビット数（上位16bitはシーケンス番号）。</summary>
    private const ulong ReferenceNumberMask = 0x0000_FFFF_FFFF_FFFF;

    /// <summary>
    /// 1レコードをパースし、結果を results へ追加する。
    /// ハードリンクされたファイルは親の異なる $FILE_NAME を複数持ち得るため、
    /// 1レコードから複数のエントリ（親ごと）が生まれることがある。
    /// 拡張レコード（属性リストで基底レコードから溢れた属性の置き場）からは、
    /// $DATA のサイズだけを取り出し、基底レコード番号に紐づけて返す。
    /// </summary>
    public static bool TryParse(Span<byte> record, int bytesPerSector, ulong referenceNumber, List<MftFileEntry> results)
    {
        if (bytesPerSector <= 0 || record.Length < bytesPerSector || record.Length % bytesPerSector != 0)
        {
            return false;
        }

        if (Encoding.ASCII.GetString(record.Slice(RecordHeaderOffset.Signature, 4)) != RecordSignature)
        {
            return false;
        }

        var flags = BitConverter.ToUInt16(record.Slice(RecordHeaderOffset.Flags, 2));
        if ((flags & RecordFlagInUse) == 0)
        {
            // 削除済みレコードは集計対象にしない。
            return false;
        }

        if (!ApplyUpdateSequenceFixup(record, bytesPerSector))
        {
            return false;
        }

        var firstAttributeOffset = (int)BitConverter.ToUInt16(record.Slice(RecordHeaderOffset.FirstAttributeOffset, 2));

        // NTFS 3.1 以降のレコードには、基底レコードへの参照（拡張レコード判定用）が入る。
        ulong baseReference = 0;
        if (firstAttributeOffset >= ExtendedHeaderSize)
        {
            baseReference = BitConverter.ToUInt64(record.Slice(BaseRecordReferenceOffset, 8)) & ReferenceNumberMask;
        }

        var isExtensionRecord = baseReference != 0 && baseReference != referenceNumber;

        var namesByParent = new Dictionary<ulong, (int Score, string Name)>();
        long dataSizeBytes = 0;
        var dataFound = false;

        var attributeOffset = firstAttributeOffset;
        while (attributeOffset + AttributeHeaderOffset.ResidentValueOffset + 2 <= record.Length)
        {
            var attribute = record.Slice(attributeOffset);
            var type = BitConverter.ToUInt32(attribute.Slice(AttributeHeaderOffset.Type, 4));
            if (type == AttributeType.End)
            {
                break;
            }

            var length = BitConverter.ToUInt32(attribute.Slice(AttributeHeaderOffset.Length, 4));
            if (length == 0 || attributeOffset + (long)length > record.Length)
            {
                break;
            }

            var isResident = attribute[AttributeHeaderOffset.NonResidentFlag] == 0;
            var nameLength = attribute[AttributeHeaderOffset.NameLength];

            if (isResident)
            {
                var valueLength = BitConverter.ToUInt32(attribute.Slice(AttributeHeaderOffset.ResidentValueLength, 4));
                var valueOffset = BitConverter.ToUInt16(attribute.Slice(AttributeHeaderOffset.ResidentValueOffset, 2));
                if (valueOffset + (long)valueLength > length)
                {
                    attributeOffset += (int)length;
                    continue;
                }

                var value = attribute.Slice(valueOffset, (int)valueLength);

                if (type == AttributeType.FileName && valueLength >= FileNameValueOffset.Name)
                {
                    var nameChars = value[FileNameValueOffset.NameLength];
                    var nameBytes = nameChars * 2;
                    if (FileNameValueOffset.Name + nameBytes <= value.Length)
                    {
                        var parent = BitConverter.ToUInt64(value.Slice(FileNameValueOffset.ParentReference, 8)) & ReferenceNumberMask;
                        var score = NamespaceScore(value[FileNameValueOffset.Namespace]);
                        if (!namesByParent.TryGetValue(parent, out var current) || score > current.Score)
                        {
                            namesByParent[parent] = (score, Encoding.Unicode.GetString(value.Slice(FileNameValueOffset.Name, nameBytes)));
                        }
                    }
                }
                else if (type == AttributeType.Data && nameLength == 0 && !dataFound)
                {
                    // 常駐 $DATA の実サイズは値そのものの長さ。
                    dataSizeBytes = valueLength;
                    dataFound = true;
                }
            }
            else if (type == AttributeType.Data && nameLength == 0 && !dataFound
                     && length >= AttributeHeaderOffset.NonResidentDataSize + 8)
            {
                // 非常駐 $DATA の実サイズは、先頭エクステント（VCN 0）のヘッダーにだけ
                // 記録されている。2番目以降のエクステントのヘッダーは 0 を持つため、
                // 拡張レコードでは VCN 0 のものだけをサイズとして採用する。
                var startingVcn = BitConverter.ToUInt64(attribute.Slice(AttributeHeaderOffset.NonResidentStartingVcn, 8));
                if (!isExtensionRecord || startingVcn == 0)
                {
                    var rawSize = BitConverter.ToUInt64(attribute.Slice(AttributeHeaderOffset.NonResidentDataSize, 8));
                    dataSizeBytes = rawSize > long.MaxValue ? 0 : (long)rawSize;
                    dataFound = true;
                }
            }

            attributeOffset += (int)length;
        }

        var isDirectory = (flags & RecordFlagDirectory) != 0;

        if (isExtensionRecord)
        {
            // 拡張レコードからは、基底レコードのファイルの断片情報を持ち帰る。
            // $FILE_NAME が基底レコードに収まらず、こちらに置かれている場合があるため、
            // サイズだけでなく名前も対象にする。集計側で基底レコード番号ごとに統合する。
            if (!dataFound && namesByParent.Count == 0)
            {
                return false;
            }

            if (namesByParent.Count == 0)
            {
                results.Add(new MftFileEntry
                {
                    ReferenceNumber = baseReference,
                    IsExtensionData = true,
                    IsDirectory = isDirectory,
                    DataSizeResolved = dataFound,
                    DataSizeBytes = dataSizeBytes,
                });
                return true;
            }

            foreach (var (parent, nameInfo) in namesByParent)
            {
                results.Add(new MftFileEntry
                {
                    ReferenceNumber = baseReference,
                    ParentReferenceNumber = parent,
                    Name = nameInfo.Name,
                    NameScore = nameInfo.Score,
                    HasName = true,
                    IsExtensionData = true,
                    IsDirectory = isDirectory,
                    DataSizeResolved = dataFound,
                    DataSizeBytes = dataSizeBytes,
                });
            }

            return true;
        }

        if (namesByParent.Count == 0)
        {
            return false;
        }

        foreach (var (parent, nameInfo) in namesByParent)
        {
            results.Add(new MftFileEntry
            {
                ReferenceNumber = referenceNumber,
                ParentReferenceNumber = parent,
                Name = nameInfo.Name,
                NameScore = nameInfo.Score,
                HasName = true,
                IsDirectory = isDirectory,
                DataSizeBytes = dataSizeBytes,
                DataSizeResolved = dataFound,
            });
        }

        return true;
    }

    /// <summary>基底レコード参照のオフセット（NTFS 3.1 以降のレコードヘッダー内）。</summary>
    private const int BaseRecordReferenceOffset = 32;

    /// <summary>基底レコード参照を持つレコードヘッダーの最小サイズ。</summary>
    private const int ExtendedHeaderSize = 48;

    /// <summary>
    /// レコード（$MFT 自身のレコード0を想定）の非常駐 $DATA 属性から、
    /// データラン（物理配置）の並びを取り出す。$MFT は断片化していることがあり、
    /// 先頭位置からの直線読みでは途中から別ファイルのデータを読んでしまうため、
    /// 逐次読みの前にこの配置表が必要になる。
    /// 戻り値は (開始LCN, クラスタ数) の並び。スパースランは開始LCN = -1 で表す。
    /// </summary>
    public static bool TryParseDataRuns(Span<byte> record, int bytesPerSector,
        out List<(long StartLcn, long ClusterCount)> runs)
    {
        runs = new List<(long StartLcn, long ClusterCount)>();

        if (bytesPerSector <= 0 || record.Length < bytesPerSector || record.Length % bytesPerSector != 0)
        {
            return false;
        }

        if (Encoding.ASCII.GetString(record.Slice(RecordHeaderOffset.Signature, 4)) != RecordSignature)
        {
            return false;
        }

        // フィックスアップ適用のため、作業用コピー上で走査する。
        var work = record.ToArray();
        if (!ApplyUpdateSequenceFixup(work, bytesPerSector))
        {
            return false;
        }

        var attributeOffset = (int)BitConverter.ToUInt16(work.AsSpan(RecordHeaderOffset.FirstAttributeOffset, 2));
        while (attributeOffset + AttributeHeaderOffset.ResidentValueOffset + 2 <= work.Length)
        {
            var attribute = work.AsSpan(attributeOffset);
            var type = BitConverter.ToUInt32(attribute.Slice(AttributeHeaderOffset.Type, 4));
            if (type == AttributeType.End)
            {
                break;
            }

            var length = BitConverter.ToUInt32(attribute.Slice(AttributeHeaderOffset.Length, 4));
            if (length == 0 || attributeOffset + (long)length > work.Length)
            {
                break;
            }

            var isResident = attribute[AttributeHeaderOffset.NonResidentFlag] == 0;
            var nameLength = attribute[AttributeHeaderOffset.NameLength];
            if (!isResident && type == AttributeType.Data && nameLength == 0
                && length >= NonResidentHeaderSize)
            {
                var runsOffset = BitConverter.ToUInt16(attribute.Slice(NonResidentRunsOffset, 2));
                if (runsOffset < NonResidentHeaderSize || runsOffset >= length)
                {
                    return false;
                }

                return TryDecodeRuns(attribute.Slice(runsOffset, (int)length - runsOffset), runs);
            }

            attributeOffset += (int)length;
        }

        return false;
    }

    /// <summary>非常駐属性ヘッダーのサイズ（データラン開始位置までの最小長）。</summary>
    private const int NonResidentHeaderSize = 64;

    /// <summary>非常駐属性ヘッダー内のデータラン配列オフセット位置（uint16）。</summary>
    private const int NonResidentRunsOffset = 32;

    private static bool TryDecodeRuns(ReadOnlySpan<byte> runBytes, List<(long StartLcn, long ClusterCount)> runs)
    {
        long currentLcn = 0;
        var position = 0;
        while (position < runBytes.Length)
        {
            var header = runBytes[position++];
            if (header == 0)
            {
                return runs.Count > 0;
            }

            var lengthByteCount = header & 0x0F;
            var offsetByteCount = (header >> 4) & 0x0F;
            if (lengthByteCount == 0 || position + lengthByteCount + offsetByteCount > runBytes.Length)
            {
                return false;
            }

            long clusterCount = 0;
            for (var i = lengthByteCount - 1; i >= 0; i--)
            {
                clusterCount = (clusterCount << 8) | runBytes[position + i];
            }

            position += lengthByteCount;

            if (offsetByteCount == 0)
            {
                // オフセットの無いランはスパース（実データ無し）。
                runs.Add((-1, clusterCount));
                continue;
            }

            long delta = 0;
            for (var i = offsetByteCount - 1; i >= 0; i--)
            {
                delta = (delta << 8) | runBytes[position + i];
            }

            // オフセットは符号付き。最上位バイトの符号ビットで符号拡張する。
            var shift = 64 - offsetByteCount * 8;
            delta = (delta << shift) >> shift;
            position += offsetByteCount;

            currentLcn += delta;
            runs.Add((currentLcn, clusterCount));
        }

        return runs.Count > 0;
    }

    private static int NamespaceScore(byte ns)
    {
        return ns switch
        {
            FileNameNamespace.Win32 => 2,
            FileNameNamespace.Win32AndDos => 2,
            FileNameNamespace.Posix => 1,
            _ => 0, // DOS 名（8.3形式）は最後の候補
        };
    }

    /// <summary>
    /// 更新シーケンス配列によるセクター末尾の置換を元に戻す。
    /// 各セクターの末尾2バイトが USN と一致しないレコードは破損として弾く。
    /// </summary>
    private static bool ApplyUpdateSequenceFixup(Span<byte> record, int bytesPerSector)
    {
        var usaOffset = BitConverter.ToUInt16(record.Slice(RecordHeaderOffset.UpdateSequenceOffset, 2));
        var usaCount = BitConverter.ToUInt16(record.Slice(RecordHeaderOffset.UpdateSequenceCount, 2));
        var sectorCount = record.Length / bytesPerSector;

        if (usaCount != sectorCount + 1 || usaOffset + usaCount * 2 > record.Length)
        {
            return false;
        }

        var updateSequenceNumber = record.Slice(usaOffset, 2);
        for (var sector = 1; sector <= sectorCount; sector++)
        {
            var tailPosition = sector * bytesPerSector - 2;
            if (!record.Slice(tailPosition, 2).SequenceEqual(updateSequenceNumber))
            {
                return false;
            }

            var stored = record.Slice(usaOffset + sector * 2, 2);
            stored.CopyTo(record.Slice(tailPosition, 2));
        }

        return true;
    }
}

/// <summary>
/// パース済みレコード群から、フォルダーごとの合計サイズ表を作る。
/// 親参照番号を辿ってフルパスを復元し、各ファイルのサイズを祖先フォルダー全てに加算する。
/// ルートディレクトリの参照番号は NTFS の仕様で 5 と決まっている。
/// </summary>
internal static class MftFolderSizeAggregator
{
    /// <summary>NTFS ルートディレクトリのファイル参照番号。</summary>
    private const ulong RootReferenceNumber = 5;

    /// <summary>親辿りの循環・異常データに対する安全上限（パス階層数）。</summary>
    private const int MaxPathDepth = 512;

    /// <summary>
    /// 基底レコードと拡張レコードの断片を、基底レコード番号ごとに統合した1ファイル分の情報。
    /// $FILE_NAME や $DATA は、収まりきらない分が拡張レコード側に分かれて置かれるため、
    /// どちらか一方だけでは名前もサイズも欠けることがある。
    /// </summary>
    private sealed class MergedFile
    {
        public bool IsDirectory;
        public long DataSizeBytes;
        public bool DataSizeResolved;
        public Dictionary<ulong, (int Score, string Name)> NamesByParent = new();
    }

    public static Dictionary<string, long> BuildFolderSizes(IReadOnlyList<MftFileEntry> entries, string driveRoot)
    {
        // ルートだけは末尾の区切りを残す（"C:\"）。Path.TrimEndingDirectorySeparator は
        // ルートの区切りを取り除かない仕様なので、クエリ側の正規化と揃えるためこの形で統一する。
        var rootPath = Path.GetFullPath(driveRoot);

        var files = new Dictionary<ulong, MergedFile>();
        foreach (var entry in entries)
        {
            if (!files.TryGetValue(entry.ReferenceNumber, out var file))
            {
                file = new MergedFile();
                files[entry.ReferenceNumber] = file;
            }

            if (entry.IsDirectory)
            {
                file.IsDirectory = true;
            }

            if (entry.DataSizeResolved)
            {
                // サイズはストリーム全体の値で、どの断片から読んでも同じになる。
                // 0 でない値を優先し、空ファイルの正当な 0 と取り違えない。
                if (!file.DataSizeResolved || (file.DataSizeBytes == 0 && entry.DataSizeBytes != 0))
                {
                    file.DataSizeBytes = entry.DataSizeBytes;
                    file.DataSizeResolved = true;
                }
            }

            if (entry.HasName
                && (!file.NamesByParent.TryGetValue(entry.ParentReferenceNumber, out var current)
                    || entry.NameScore > current.Score))
            {
                file.NamesByParent[entry.ParentReferenceNumber] = (entry.NameScore, entry.Name);
            }
        }

        (ulong Parent, string Name)? PrimaryName(ulong reference)
        {
            if (!files.TryGetValue(reference, out var file))
            {
                return null;
            }

            foreach (var (parent, nameInfo) in file.NamesByParent)
            {
                return (parent, nameInfo.Name);
            }

            return null;
        }

        // フォルダーごとのフルパスを復元する（メモ化つき）。
        var pathByReference = new Dictionary<ulong, string?> { [RootReferenceNumber] = rootPath };
        string? ResolvePath(ulong reference)
        {
            if (pathByReference.TryGetValue(reference, out var cached))
            {
                return cached;
            }

            var primary = PrimaryName(reference);
            if (primary == null)
            {
                pathByReference[reference] = null;
                return null;
            }

            // 解決済みの祖先に届くまで、未解決の階層を集める。
            var chain = new List<(ulong Reference, string Name)>();
            var seen = new HashSet<ulong>();
            var currentReference = reference;
            var currentPrimary = primary.Value;
            string? basePath;
            while (true)
            {
                if (!seen.Add(currentReference) || chain.Count >= MaxPathDepth)
                {
                    basePath = null;
                    break;
                }

                chain.Add((currentReference, currentPrimary.Name));

                if (pathByReference.TryGetValue(currentPrimary.Parent, out var parentCached))
                {
                    basePath = parentCached;
                    break;
                }

                var parentPrimary = PrimaryName(currentPrimary.Parent);
                if (parentPrimary == null)
                {
                    basePath = null;
                    break;
                }

                currentReference = currentPrimary.Parent;
                currentPrimary = parentPrimary.Value;
            }

            // chain は対象から親方向に積まれているので、根側から順にパスを組み立てる。
            var path = basePath;
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                path = path == null ? null : JoinPath(path, chain[i].Name);
                pathByReference[chain[i].Reference] = path;
            }

            return pathByReference[reference];
        }

        var totalsByReference = new Dictionary<ulong, long>();
        foreach (var (reference, file) in files)
        {
            if (file.IsDirectory || !file.DataSizeResolved || file.DataSizeBytes <= 0)
            {
                continue;
            }

            // ハードリンクは親ごとに名前があるので、リンク先フォルダーごとに加算する。
            foreach (var parent in file.NamesByParent.Keys)
            {
                var current = parent;
                var depth = 0;
                var seen = new HashSet<ulong>();
                while (depth++ < MaxPathDepth && seen.Add(current))
                {
                    if (!files.ContainsKey(current) && current != RootReferenceNumber)
                    {
                        break;
                    }

                    totalsByReference[current] = totalsByReference.TryGetValue(current, out var total)
                        ? total + file.DataSizeBytes
                        : file.DataSizeBytes;

                    if (current == RootReferenceNumber)
                    {
                        break;
                    }

                    var parentPrimary = PrimaryName(current);
                    if (parentPrimary == null)
                    {
                        break;
                    }

                    current = parentPrimary.Value.Parent;
                }
            }
        }

        var folderSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (reference, file) in files)
        {
            if (!file.IsDirectory && reference != RootReferenceNumber)
            {
                continue;
            }

            var path = ResolvePath(reference);
            if (path == null)
            {
                continue;
            }

            folderSizes[path] = totalsByReference.TryGetValue(reference, out var total) ? total : 0;
        }

        if (!folderSizes.ContainsKey(rootPath))
        {
            folderSizes[rootPath] = totalsByReference.TryGetValue(RootReferenceNumber, out var rootTotal) ? rootTotal : 0;
        }

        return folderSizes;
    }

    private static string JoinPath(string parentPath, string name)
        => parentPath.EndsWith('\\') ? parentPath + name : parentPath + "\\" + name;
}

/// <summary>
/// ボリュームハンドルを開き、$MFT を先頭から逐次読みして全レコードをパースする。
/// FSCTL_GET_NTFS_VOLUME_DATA で $MFT の開始位置とレコードサイズを取得する。
/// </summary>
internal static class MftVolumeReader
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

    /// <summary>$MFT 読み取りの1回分のバッファサイズ（レコードサイズの倍数に保つ）。</summary>
    private const int ReadChunkBytes = 8 * 1024 * 1024;

    /// <summary>NTFS_VOLUME_DATA_BUFFER のサイズ（バイト）。</summary>
    private const int NtfsVolumeDataSize = 96;

    private static class NtfsVolumeDataOffset
    {
        public const int BytesPerSector = 40;              // uint32
        public const int BytesPerCluster = 44;             // uint32
        public const int BytesPerFileRecordSegment = 48;   // uint32
        public const int MftSize = 56;                     // int64
        public const int MftStartLcn = 64;                 // int64
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    public static List<MftFileEntry>? ReadAllEntries(string driveRoot, OperationLog log)
    {
        // driveRoot は "C:\" 形式。ボリュームデバイス名は "\\.\C:"。
        var volumePath = @"\\.\" + driveRoot.TrimEnd('\\');

        using var handle = CreateFile(volumePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            log.Warning("MftSize", "ボリュームを開けない", $"{volumePath} Win32Error={Marshal.GetLastWin32Error()}");
            return null;
        }

        var volumeData = new byte[NtfsVolumeDataSize];
        if (!DeviceIoControl(handle, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0,
                volumeData, volumeData.Length, out _, IntPtr.Zero))
        {
            // NTFS でないボリュームではここで失敗する。
            log.Warning("MftSize", "NTFSボリューム情報を取得できない", $"{volumePath} Win32Error={Marshal.GetLastWin32Error()}");
            return null;
        }

        var bytesPerSector = BitConverter.ToInt32(volumeData, NtfsVolumeDataOffset.BytesPerSector);
        var bytesPerCluster = BitConverter.ToInt32(volumeData, NtfsVolumeDataOffset.BytesPerCluster);
        var bytesPerRecord = BitConverter.ToInt32(volumeData, NtfsVolumeDataOffset.BytesPerFileRecordSegment);
        var mftSizeBytes = BitConverter.ToInt64(volumeData, NtfsVolumeDataOffset.MftSize);
        var mftStartLcn = BitConverter.ToInt64(volumeData, NtfsVolumeDataOffset.MftStartLcn);

        if (bytesPerSector <= 0 || bytesPerCluster <= 0 || bytesPerRecord <= 0
            || mftSizeBytes <= 0 || mftStartLcn < 0)
        {
            log.Warning("MftSize", "NTFSボリューム情報が想定外の値", volumePath);
            return null;
        }

        var recordCount = mftSizeBytes / bytesPerRecord;
        var entries = new List<MftFileEntry>((int)Math.Min(recordCount, int.MaxValue));
        var buffer = new byte[ReadChunkBytes];
        var recordBuffer = new byte[bytesPerRecord];
        long recordsRead = 0;

        using var stream = new FileStream(handle, FileAccess.Read, ReadChunkBytes, isAsync: false);

        // $MFT 自身のレコード0からデータラン（物理配置）を読み、断片化に追従する。
        // 取得できない場合は、全体が連続しているものとして直線読みにフォールバックする。
        var runs = ReadDataRuns(stream, mftStartLcn, bytesPerCluster, bytesPerRecord, bytesPerSector);
        if (runs == null)
        {
            runs = new List<(long StartLcn, long ClusterCount)>
            {
                (mftStartLcn, mftSizeBytes / bytesPerCluster + 1),
            };
        }

        long recordsRemaining = recordCount;
        foreach (var (runLcn, runClusters) in runs)
        {
            if (recordsRemaining <= 0)
            {
                break;
            }

            var runRecordCapacity = runClusters * bytesPerCluster / bytesPerRecord;
            if (runLcn < 0)
            {
                // スパースランに実データは無い（全てゼロで、レコードとして読めない）。
                var skipped = Math.Min(recordsRemaining, runRecordCapacity);
                recordsRead += skipped;
                recordsRemaining -= skipped;
                continue;
            }

            var runRecords = Math.Min(recordsRemaining, runRecordCapacity);
            stream.Position = runLcn * bytesPerCluster;
            long runBytesRemaining = runRecords * bytesPerRecord;
            while (runBytesRemaining > 0)
            {
                var want = (int)Math.Min(buffer.Length, runBytesRemaining);
                var filled = 0;
                while (filled < want)
                {
                    var read = stream.Read(buffer, filled, want - filled);
                    if (read <= 0)
                    {
                        break;
                    }

                    filled += read;
                }

                if (filled == 0)
                {
                    break;
                }

                var usableRecords = filled / bytesPerRecord;
                for (var i = 0; i < usableRecords; i++)
                {
                    Array.Copy(buffer, i * bytesPerRecord, recordBuffer, 0, bytesPerRecord);
                    MftRecordParser.TryParse(recordBuffer, bytesPerSector, (ulong)(recordsRead + i), entries);
                }

                recordsRead += usableRecords;
                recordsRemaining -= usableRecords;
                runBytesRemaining -= (long)usableRecords * bytesPerRecord;
                if (filled < want)
                {
                    break;
                }
            }
        }

        log.Info("MftSize", "$MFT読み取り完了", $"{driveRoot} レコード{recordsRead}件、有効{entries.Count}件");
        return entries;
    }

    /// <summary>
    /// $MFT の先頭レコード（レコード0）を読み、その $DATA のデータランを取得する。
    /// レコード0は $MFT の先頭フラグメント内に必ず存在する。
    /// </summary>
    private static List<(long StartLcn, long ClusterCount)>? ReadDataRuns(
        FileStream stream, long mftStartLcn, int bytesPerCluster, int bytesPerRecord, int bytesPerSector)
    {
        try
        {
            var firstRecord = new byte[bytesPerRecord];
            stream.Position = mftStartLcn * bytesPerCluster;
            var filled = 0;
            while (filled < firstRecord.Length)
            {
                var read = stream.Read(firstRecord, filled, firstRecord.Length - filled);
                if (read <= 0)
                {
                    return null;
                }

                filled += read;
            }

            return MftRecordParser.TryParseDataRuns(firstRecord, bytesPerSector, out var runs) ? runs : null;
        }
        catch
        {
            return null;
        }
    }
}
