using System.Text;
using Purge;

namespace Purge.Tests;

/// <summary>
/// $MFT レコードのパースとフォルダー集計のテスト。
/// レコードは NTFS の公開仕様に沿って合成し、実ボリュームには依存しない。
/// セクター長 512 バイト、レコード長 1024 バイト（USA は3要素）で固定する。
/// </summary>
public class MftSizeIndexTests
{
    private const int RecordSize = 1024;
    private const int SectorSize = 512;
    private const ushort UpdateSequenceNumber = 0xAAAA;

    private const byte NamespacePosix = 0;
    private const byte NamespaceWin32 = 1;
    private const byte NamespaceDos = 2;

    // ---- レコード合成 ----

    private sealed record FileNameSpec(ulong ParentReference, string Name, byte Namespace);

    private static byte[] BuildRecord(
        IReadOnlyList<FileNameSpec> fileNames,
        bool isDirectory,
        bool inUse = true,
        long? nonResidentDataSize = null,
        int? residentDataSize = null,
        byte[]? dataRuns = null,
        ulong baseReference = 0,
        ulong dataStartingVcn = 0)
    {
        var record = new byte[RecordSize];
        var usaOffset = 48;
        var attributeOffset = 56;

        Encoding.ASCII.GetBytes("FILE").CopyTo(record, 0);
        BitConverter.GetBytes((ushort)usaOffset).CopyTo(record, 4);
        BitConverter.GetBytes((ushort)3).CopyTo(record, 6); // USN + セクター2つ分
        BitConverter.GetBytes((ushort)attributeOffset).CopyTo(record, 20);

        var flags = (ushort)((inUse ? 0x0001 : 0) | (isDirectory ? 0x0002 : 0));
        BitConverter.GetBytes(flags).CopyTo(record, 22);
        BitConverter.GetBytes(RecordSize).CopyTo(record, 24);
        BitConverter.GetBytes(baseReference).CopyTo(record, 32);

        foreach (var spec in fileNames)
        {
            var nameBytes = Encoding.Unicode.GetBytes(spec.Name);
            var valueLength = 66 + nameBytes.Length;
            var attributeLength = Align8(24 + valueLength);

            BitConverter.GetBytes(0x30u).CopyTo(record, attributeOffset); // $FILE_NAME
            BitConverter.GetBytes((uint)attributeLength).CopyTo(record, attributeOffset + 4);
            record[attributeOffset + 8] = 0; // 常駐
            BitConverter.GetBytes((uint)valueLength).CopyTo(record, attributeOffset + 16);
            BitConverter.GetBytes((ushort)24).CopyTo(record, attributeOffset + 20);

            var value = attributeOffset + 24;
            BitConverter.GetBytes(spec.ParentReference).CopyTo(record, value);
            BitConverter.GetBytes(isDirectory ? 0x10000000u : 0x20u).CopyTo(record, value + 56);
            record[value + 64] = (byte)spec.Name.Length;
            record[value + 65] = spec.Namespace;
            nameBytes.CopyTo(record, value + 66);

            attributeOffset += attributeLength;
        }

        if (nonResidentDataSize.HasValue)
        {
            var dataAttributeLength = 64 + (dataRuns?.Length ?? 0);
            BitConverter.GetBytes(0x80u).CopyTo(record, attributeOffset); // $DATA
            BitConverter.GetBytes((uint)dataAttributeLength).CopyTo(record, attributeOffset + 4);
            record[attributeOffset + 8] = 1; // 非常駐
            BitConverter.GetBytes(dataStartingVcn).CopyTo(record, attributeOffset + 16);
            BitConverter.GetBytes((ulong)nonResidentDataSize.Value).CopyTo(record, attributeOffset + 48);
            if (dataRuns != null)
            {
                BitConverter.GetBytes((ushort)64).CopyTo(record, attributeOffset + 32);
                dataRuns.CopyTo(record, attributeOffset + 64);
            }

            attributeOffset += dataAttributeLength;
        }
        else if (residentDataSize.HasValue)
        {
            var attributeLength = Align8(24 + residentDataSize.Value);
            BitConverter.GetBytes(0x80u).CopyTo(record, attributeOffset); // $DATA
            BitConverter.GetBytes((uint)attributeLength).CopyTo(record, attributeOffset + 4);
            record[attributeOffset + 8] = 0; // 常駐
            BitConverter.GetBytes((uint)residentDataSize.Value).CopyTo(record, attributeOffset + 16);
            BitConverter.GetBytes((ushort)24).CopyTo(record, attributeOffset + 20);
            attributeOffset += attributeLength;
        }

        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(record, attributeOffset); // 終端マーカー

        ApplyFixup(record, usaOffset);
        return record;
    }

    private static int Align8(int value) => (value + 7) & ~7;

    /// <summary>
    /// 実レコードと同じ形にするため、セクター末尾の元の値を USA に退避し、
    /// 末尾を USN で上書きする（パーサー側はこの逆を行う）。
    /// </summary>
    private static void ApplyFixup(byte[] record, int usaOffset)
    {
        BitConverter.GetBytes(UpdateSequenceNumber).CopyTo(record, usaOffset);
        for (var sector = 1; sector <= RecordSize / SectorSize; sector++)
        {
            var tailPosition = sector * SectorSize - 2;
            Array.Copy(record, tailPosition, record, usaOffset + sector * 2, 2);
            BitConverter.GetBytes(UpdateSequenceNumber).CopyTo(record, tailPosition);
        }
    }

    private static List<MftFileEntry> Parse(byte[] record, ulong referenceNumber)
    {
        var results = new List<MftFileEntry>();
        MftRecordParser.TryParse(record, SectorSize, referenceNumber, results);
        return results;
    }

    // ---- パース ----

    [Fact]
    public void Parse_FileRecord_ReturnsNameParentAndDataSize()
    {
        var record = BuildRecord(
            new[] { new FileNameSpec(5, "app.exe", NamespaceWin32) },
            isDirectory: false,
            nonResidentDataSize: 123_456);

        var entries = Parse(record, 100);
        var entry = Assert.Single(entries);
        Assert.Equal("app.exe", entry.Name);
        Assert.Equal(5ul, entry.ParentReferenceNumber);
        Assert.False(entry.IsDirectory);
        Assert.Equal(123_456L, entry.DataSizeBytes);
        Assert.True(entry.DataSizeResolved);
    }

    [Fact]
    public void Parse_ResidentData_UsesValueLengthAsSize()
    {
        var record = BuildRecord(
            new[] { new FileNameSpec(5, "memo.txt", NamespaceWin32) },
            isDirectory: false,
            residentDataSize: 42);

        var entry = Assert.Single(Parse(record, 101));
        Assert.Equal(42L, entry.DataSizeBytes);
    }

    [Fact]
    public void Parse_DirectoryRecord_SetsDirectoryFlag()
    {
        var record = BuildRecord(
            new[] { new FileNameSpec(5, "MyApp", NamespaceWin32) },
            isDirectory: true);

        var entry = Assert.Single(Parse(record, 102));
        Assert.True(entry.IsDirectory);
        Assert.Equal(0L, entry.DataSizeBytes);
    }

    [Fact]
    public void Parse_Win32NamePreferredOverDosName()
    {
        var record = BuildRecord(
            new[]
            {
                new FileNameSpec(5, "PROGRA~1", NamespaceDos),
                new FileNameSpec(5, "Program Files", NamespaceWin32),
            },
            isDirectory: true);

        var entry = Assert.Single(Parse(record, 103));
        Assert.Equal("Program Files", entry.Name);
    }

    [Fact]
    public void Parse_DosNameOnly_StillParses()
    {
        var record = BuildRecord(
            new[] { new FileNameSpec(5, "OLDAPP~1", NamespaceDos) },
            isDirectory: true);

        var entry = Assert.Single(Parse(record, 104));
        Assert.Equal("OLDAPP~1", entry.Name);
    }

    [Fact]
    public void Parse_DeletedRecord_ReturnsFalse()
    {
        var record = BuildRecord(
            new[] { new FileNameSpec(5, "gone.exe", NamespaceWin32) },
            isDirectory: false,
            inUse: false,
            nonResidentDataSize: 10);

        Assert.Empty(Parse(record, 105));
    }

    [Fact]
    public void Parse_BadSignature_ReturnsFalse()
    {
        var record = BuildRecord(
            new[] { new FileNameSpec(5, "app.exe", NamespaceWin32) },
            isDirectory: false,
            nonResidentDataSize: 10);
        record[0] = (byte)'X';

        Assert.Empty(Parse(record, 106));
    }

    [Fact]
    public void Parse_BrokenUpdateSequence_ReturnsFalse()
    {
        var record = BuildRecord(
            new[] { new FileNameSpec(5, "app.exe", NamespaceWin32) },
            isDirectory: false,
            nonResidentDataSize: 10);
        // セクター末尾の USN を壊すとフィックスアップ検証で弾かれる。
        record[SectorSize - 2] ^= 0xFF;

        Assert.Empty(Parse(record, 107));
    }

    [Fact]
    public void Parse_HardlinkedFile_ProducesOneEntryPerParent()
    {
        var record = BuildRecord(
            new[]
            {
                new FileNameSpec(100, "shared.dll", NamespaceWin32),
                new FileNameSpec(200, "shared.dll", NamespaceWin32),
            },
            isDirectory: false,
            nonResidentDataSize: 70);

        var entries = Parse(record, 300);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.ParentReferenceNumber == 100ul);
        Assert.Contains(entries, e => e.ParentReferenceNumber == 200ul);
        Assert.All(entries, e => Assert.Equal(70L, e.DataSizeBytes));
    }

    [Fact]
    public void Parse_ExtensionRecord_ReturnsDataSizeForBaseRecord()
    {
        // 大きなファイルの $DATA が拡張レコード側にある場合の形。
        var record = BuildRecord(
            Array.Empty<FileNameSpec>(),
            isDirectory: false,
            nonResidentDataSize: 70_235_430,
            baseReference: 500);

        var entry = Assert.Single(Parse(record, 900));
        Assert.True(entry.IsExtensionData);
        Assert.Equal(500ul, entry.ReferenceNumber);
        Assert.Equal(70_235_430L, entry.DataSizeBytes);
    }

    [Fact]
    public void Parse_ExtensionRecordWithLaterExtent_ReturnsNoSize()
    {
        // 2番目以降のエクステントのヘッダーはサイズ 0 を持ち、先頭エクステントの
        // サイズを上書きしてはいけないため、サイズ情報として採用しない。
        var record = BuildRecord(
            Array.Empty<FileNameSpec>(),
            isDirectory: false,
            nonResidentDataSize: 0,
            baseReference: 500,
            dataStartingVcn: 11365);

        Assert.Empty(Parse(record, 902));
    }

    [Fact]
    public void Parse_ExtensionRecordWithFileName_ReturnsNameFragment()
    {
        // $FILE_NAME が基底レコードに収まらず拡張レコード側にある場合の形。
        var record = BuildRecord(
            new[] { new FileNameSpec(100, "longname.cat", NamespaceWin32) },
            isDirectory: false,
            baseReference: 500);

        var entry = Assert.Single(Parse(record, 903));
        Assert.True(entry.IsExtensionData);
        Assert.Equal(500ul, entry.ReferenceNumber);
        Assert.True(entry.HasName);
        Assert.Equal(100ul, entry.ParentReferenceNumber);
        Assert.Equal("longname.cat", entry.Name);
    }

    [Fact]
    public void Parse_BaseRecordWithoutData_SizeIsUnresolved()
    {
        var record = BuildRecord(
            new[] { new FileNameSpec(5, "big.bin", NamespaceWin32) },
            isDirectory: false);

        var entry = Assert.Single(Parse(record, 901));
        Assert.False(entry.DataSizeResolved);
        Assert.Equal(0L, entry.DataSizeBytes);
    }

    // ---- データラン ----

    [Fact]
    public void ParseDataRuns_DecodesLengthAndDelta()
    {
        // ラン1: 8クラスタ、LCN は +32 / ラン2: 4クラスタ、LCN はさらに +16 / 終端 0
        var runs = new byte[] { 0x11, 0x08, 0x20, 0x11, 0x04, 0x10, 0x00 };
        var record = BuildRecord(
            new[] { new FileNameSpec(5, "$MFT", NamespaceWin32) },
            isDirectory: false,
            nonResidentDataSize: 1024,
            dataRuns: runs);

        Assert.True(MftRecordParser.TryParseDataRuns(record, SectorSize, out var parsed));
        Assert.Equal(2, parsed.Count);
        Assert.Equal((32L, 8L), parsed[0]);
        Assert.Equal((48L, 4L), parsed[1]);
    }

    [Fact]
    public void ParseDataRuns_NegativeDeltaMovesBackwards()
    {
        // ラン2 のデルタは -8（0xF8）。符号拡張できていないと大きな正値になってしまう。
        var runs = new byte[] { 0x11, 0x08, 0x20, 0x11, 0x04, 0xF8, 0x00 };
        var record = BuildRecord(
            new[] { new FileNameSpec(5, "$MFT", NamespaceWin32) },
            isDirectory: false,
            nonResidentDataSize: 1024,
            dataRuns: runs);

        Assert.True(MftRecordParser.TryParseDataRuns(record, SectorSize, out var parsed));
        Assert.Equal((24L, 4L), parsed[1]);
    }

    // ---- 集計 ----

    private static MftFileEntry Entry(ulong reference, ulong parent, string name, bool isDirectory, long size = 0)
        => new()
        {
            ReferenceNumber = reference,
            ParentReferenceNumber = parent,
            Name = name,
            IsDirectory = isDirectory,
            DataSizeBytes = size,
            DataSizeResolved = true,
            HasName = true,
            NameScore = 2,
        };

    [Fact]
    public void Aggregate_NestedFolders_SumIntoAllAncestors()
    {
        var entries = new[]
        {
            Entry(5, 5, ".", isDirectory: true),
            Entry(100, 5, "AppA", isDirectory: true),
            Entry(101, 100, "plugins", isDirectory: true),
            Entry(102, 5, "AppB", isDirectory: true),
            Entry(200, 100, "a.bin", isDirectory: false, size: 100),
            Entry(201, 101, "b.bin", isDirectory: false, size: 25),
            Entry(202, 102, "c.bin", isDirectory: false, size: 50),
            Entry(203, 999, "orphan.bin", isDirectory: false, size: 999),
        };

        var sizes = MftFolderSizeAggregator.BuildFolderSizes(entries, @"C:\");

        Assert.Equal(175L, sizes[@"C:\"]);
        Assert.Equal(125L, sizes[@"C:\AppA"]);
        Assert.Equal(25L, sizes[@"C:\AppA\plugins"]);
        Assert.Equal(50L, sizes[@"C:\AppB"]);
    }

    [Fact]
    public void Aggregate_HardlinkedFile_CountsInEachFolder()
    {
        var entries = new[]
        {
            Entry(5, 5, ".", isDirectory: true),
            Entry(100, 5, "AppA", isDirectory: true),
            Entry(102, 5, "AppB", isDirectory: true),
            Entry(200, 100, "shared.dll", isDirectory: false, size: 70),
            Entry(200, 102, "shared.dll", isDirectory: false, size: 70),
        };

        var sizes = MftFolderSizeAggregator.BuildFolderSizes(entries, @"C:\");

        Assert.Equal(70L, sizes[@"C:\AppA"]);
        Assert.Equal(70L, sizes[@"C:\AppB"]);
        Assert.Equal(140L, sizes[@"C:\"]);
    }

    [Fact]
    public void Aggregate_ExtensionRecordData_FillsBaseRecordSize()
    {
        var entries = new[]
        {
            Entry(5, 5, ".", isDirectory: true),
            Entry(100, 5, "AppA", isDirectory: true),
            // 基底レコードでは $DATA が見つからず未解決。拡張レコード側にサイズがある。
            new MftFileEntry
            {
                ReferenceNumber = 200,
                ParentReferenceNumber = 100,
                Name = "big.bin",
                HasName = true,
                NameScore = 2,
                IsDirectory = false,
                DataSizeBytes = 0,
                DataSizeResolved = false,
            },
            new MftFileEntry
            {
                ReferenceNumber = 200,
                IsExtensionData = true,
                DataSizeResolved = true,
                DataSizeBytes = 55,
            },
        };

        var sizes = MftFolderSizeAggregator.BuildFolderSizes(entries, @"C:\");

        Assert.Equal(55L, sizes[@"C:\AppA"]);
    }

    [Fact]
    public void Aggregate_FileKnownOnlyFromExtensionFragments_IsCounted()
    {
        var entries = new[]
        {
            Entry(5, 5, ".", isDirectory: true),
            Entry(100, 5, "AppA", isDirectory: true),
            // 名前だけを持つ拡張レコードの断片
            new MftFileEntry
            {
                ReferenceNumber = 200,
                ParentReferenceNumber = 100,
                Name = "big.bin",
                HasName = true,
                NameScore = 2,
                IsExtensionData = true,
                DataSizeResolved = false,
            },
            // サイズだけを持つ拡張レコードの断片
            new MftFileEntry
            {
                ReferenceNumber = 200,
                IsExtensionData = true,
                DataSizeResolved = true,
                DataSizeBytes = 55,
            },
        };

        var sizes = MftFolderSizeAggregator.BuildFolderSizes(entries, @"C:\");

        Assert.Equal(55L, sizes[@"C:\AppA"]);
    }

    [Fact]
    public void Aggregate_EmptyDirectory_ReportsZero()
    {
        var entries = new[]
        {
            Entry(5, 5, ".", isDirectory: true),
            Entry(100, 5, "Empty", isDirectory: true),
        };

        var sizes = MftFolderSizeAggregator.BuildFolderSizes(entries, @"C:\");

        Assert.Equal(0L, sizes[@"C:\Empty"]);
    }
}
