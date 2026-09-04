using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace UninstallTool
{
    /// <summary>
    /// USN Journal (FSCTL_ENUM_USN_DATA) を使い、NTFSボリューム上の全ファイル名を
    /// MFTから直接高速取得する。Everightingと同じ原理を自前実装したもの。
    /// 管理者権限が必要。USN Journalが無効なボリューム(非NTFS、無効化されたドライブ等)は
    /// スキップしてログに警告を残す。
    /// </summary>
    public sealed class MftSearchEngine
    {
        private readonly OperationLog _log;

        public MftSearchEngine(OperationLog log)
        {
            _log = log;
        }

        /// <summary>
        /// 1件のMFTレコード。フルパス復元のため親フォルダの参照番号を保持する。
        /// </summary>
        private sealed class MftRecord
        {
            public ulong FileReferenceNumber;
            public ulong ParentFileReferenceNumber;
            public string FileName = "";
            public bool IsDirectory;
        }

        #region Win32 P/Invoke 定数・構造体

        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint GENERIC_READ = 0x80000000;
        private const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
        private const int FILE_ATTRIBUTE_DIRECTORY = 0x10;

        /// <summary>DeviceIoControlがEOF(列挙完了)を示すWin32エラーコード。</summary>
        private const int ERROR_HANDLE_EOF = 38;

        /// <summary>1回のDeviceIoControl呼び出しで使うバッファサイズ(1MB)。</summary>
        private const int UsnEnumBufferSize = 1024 * 1024;

        /// <summary>
        /// USN_RECORD_V2構造体のフィールドオフセット(バイト単位)。
        /// Microsoft公式ドキュメントのUSN_RECORD_V2レイアウトに準拠。
        /// P/Invokeでマーシャリングせず手動パースしているため、オフセットを明示的に定数化する。
        /// </summary>
        private static class UsnRecordV2Offset
        {
            public const int RecordLength = 0;      // uint32
            public const int MajorVersion = 4;       // uint16
            public const int MinorVersion = 6;        // uint16
            public const int FileReferenceNumber = 8;        // uint64
            public const int ParentFileReferenceNumber = 16; // uint64
            public const int Usn = 24;                        // int64
            public const int TimeStamp = 32;                  // int64
            public const int Reason = 40;                     // uint32
            public const int SourceInfo = 44;                 // uint32
            public const int SecurityId = 48;                 // uint32
            public const int FileAttributes = 52;              // uint32
            public const int FileNameLength = 56;               // uint16
            public const int FileNameOffset = 58;                // uint16
        }

        /// <summary>ファイル名として許容する最大バイト長(異常なレコードを弾くための安全上限)。</summary>
        private const int MaxFileNameLengthBytes = 1024;

        [StructLayout(LayoutKind.Sequential)]
        private struct MFT_ENUM_DATA_V0
        {
            public ulong StartFileReferenceNumber;
            public long LowUsn;
            public long HighUsn;
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
            ref MFT_ENUM_DATA_V0 lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        #endregion

        /// <summary>
        /// 指定ドライブ(例: "C:")のMFTを読み、検索語を含むファイル/フォルダ名のフルパス一覧を返す。
        /// 大文字小文字を無視した部分一致検索。
        /// </summary>
        public List<string> Search(string driveLetter, string searchTerm, CancellationToken cancellationToken = default,
            IProgress<int>? progress = null, IReadOnlyCollection<string>? pathPrefixes = null)
        {
            _log.Info("MftSearch", "MFT高速検索を開始", $"検索語: {searchTerm}, ドライブ: {driveLetter}");

            var records = ReadAllRecords(driveLetter, cancellationToken, progress);
            if (records == null)
            {
                return new List<string>();
            }

            var recordsById = records.ToDictionary(r => r.FileReferenceNumber);

            var matches = records
                .Where(r => r.FileName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                .ToList();

            _log.Info("MftSearch", "名前一致レコードを抽出", $"{matches.Count}件");

            var results = new List<string>();
            var pathCache = new Dictionary<ulong, string?>();
            foreach (var match in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = BuildFullPath(match, recordsById, driveLetter, pathCache);
                if (fullPath != null && IsUnderPathPrefix(fullPath, pathPrefixes))
                {
                    results.Add(fullPath);
                }
            }

            _log.Info("MftSearch", "検索完了", $"{results.Count}件のパスを解決");
            return results;
        }

        private static bool IsUnderPathPrefix(string path, IReadOnlyCollection<string>? pathPrefixes)
        {
            if (pathPrefixes == null || pathPrefixes.Count == 0)
            {
                return true;
            }

            return pathPrefixes.Any(prefix =>
            {
                var normalized = prefix.TrimEnd('\\') + "\\";
                return path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                    || path.Equals(prefix.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>
        /// ParentFileReferenceNumberを辿ってルートまでのフルパスを組み立てる。
        /// 循環参照や未知の親IDに当たった場合は安全に打ち切る。
        /// </summary>
        private string? BuildFullPath(MftRecord record, Dictionary<ulong, MftRecord> recordsById,
            string driveLetter, Dictionary<ulong, string?>? pathCache = null)
        {
            if (pathCache?.TryGetValue(record.FileReferenceNumber, out var cachedPath) == true)
            {
                return cachedPath;
            }

            var parts = new List<string> { record.FileName };
            var current = record;
            var visited = new HashSet<ulong> { record.FileReferenceNumber };

            while (recordsById.TryGetValue(current.ParentFileReferenceNumber, out var parent))
            {
                if (!visited.Add(parent.FileReferenceNumber))
                {
                    // 循環参照を検知した場合は壊れたパスとして破棄
                    pathCache?[record.FileReferenceNumber] = null;
                    return null;
                }

                if (string.IsNullOrEmpty(parent.FileName))
                {
                    break;
                }

                parts.Add(parent.FileName);
                current = parent;
            }

            parts.Reverse();
            var fullPath = driveLetter.TrimEnd('\\', ':') + ":\\" + string.Join('\\', parts);
            pathCache?[record.FileReferenceNumber] = fullPath;
            return fullPath;
        }

        /// <summary>
        /// 指定ドライブのMFTを読み、指定した親フォルダ(複数可)の直下にあるフォルダ名一覧を返す。
        /// 全ドライブ検索と違い、対象を絞ることで孤児検出のような「特定フォルダ配下の棚卸し」を軽量に行える。
        /// 各要素は (フォルダ名, フルパス) のタプル。
        /// </summary>
        public List<(string Name, string FullPath)> ListSubdirectories(string driveLetter, IEnumerable<string> parentFullPaths)
        {
            var records = ReadAllRecords(driveLetter, default, null);
            if (records == null)
            {
                return new List<(string, string)>();
            }

            var recordsById = records.ToDictionary(r => r.FileReferenceNumber);

            // フルパス文字列からMftRecordを引けるように、正規化したパスでインデックスを作る
            var pathToRecord = new Dictionary<string, MftRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records.Where(r => r.IsDirectory))
            {
                var fullPath = BuildFullPath(record, recordsById, driveLetter);
                if (fullPath != null)
                {
                    pathToRecord[fullPath] = record;
                }
            }

            var results = new List<(string, string)>();
            foreach (var parentPath in parentFullPaths)
            {
                var normalizedParent = parentPath.TrimEnd('\\');
                if (!pathToRecord.TryGetValue(normalizedParent, out var parentRecord))
                {
                    continue;
                }

                var children = records
                    .Where(r => r.IsDirectory && r.ParentFileReferenceNumber == parentRecord.FileReferenceNumber);

                foreach (var child in children)
                {
                    results.Add((child.FileName, $@"{normalizedParent}\{child.FileName}"));
                }
            }

            return results;
        }

        private List<MftRecord>? ReadAllRecords(string driveLetter, CancellationToken cancellationToken, IProgress<int>? progress)
        {
            var volumePath = $@"\\.\{driveLetter.TrimEnd('\\', ':')}:";
            _log.Info("MftSearch", "ボリュームをオープン", volumePath);

            using var volumeHandle = CreateFile(
                volumePath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (volumeHandle.IsInvalid)
            {
                var err = Marshal.GetLastWin32Error();
                _log.Warning("MftSearch", "ボリュームのオープンに失敗(管理者権限またはNTFS非対応の可能性)",
                    $"{volumePath}: Win32Error={err}");
                return null;
            }

            var records = new List<MftRecord>();
            var medv0 = new MFT_ENUM_DATA_V0
            {
                StartFileReferenceNumber = 0,
                LowUsn = 0,
                HighUsn = long.MaxValue,
            };

            var buffer = Marshal.AllocHGlobal(UsnEnumBufferSize);

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool ok = DeviceIoControl(
                        volumeHandle,
                        FSCTL_ENUM_USN_DATA,
                        ref medv0,
                        Marshal.SizeOf<MFT_ENUM_DATA_V0>(),
                        buffer,
                        UsnEnumBufferSize,
                        out int bytesReturned,
                        IntPtr.Zero);

                    if (!ok)
                    {
                        var err = Marshal.GetLastWin32Error();
                        if (err != ERROR_HANDLE_EOF)
                        {
                            _log.Warning("MftSearch", "USN Journal読み取りでエラー(途中まで結果を使用)",
                                $"Win32Error={err}");
                        }
                        break;
                    }

                    if (bytesReturned <= sizeof(ulong))
                    {
                        break;
                    }

                    // バッファ先頭8バイトは次回開始位置(NextStartFileReferenceNumber)
                    medv0.StartFileReferenceNumber = (ulong)Marshal.ReadInt64(buffer, 0);
                    progress?.Report(records.Count);

                    int offset = sizeof(ulong);
                    while (offset < bytesReturned)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int recordLength = Marshal.ReadInt32(buffer, offset + UsnRecordV2Offset.RecordLength);
                        if (recordLength <= 0)
                        {
                            break;
                        }

                        var record = ParseUsnRecord(buffer, offset);
                        if (record != null)
                        {
                            records.Add(record);
                        }

                        offset += recordLength;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            _log.Info("MftSearch", "MFTレコード読み取り完了", $"{records.Count}件");
            return records;
        }

        /// <summary>
        /// USN_RECORD_V2構造体をバッファから手動でパースする。
        /// オフセット定義は UsnRecordV2Offset を参照(マジックナンバー排除)。
        /// </summary>
        private MftRecord? ParseUsnRecord(IntPtr buffer, int offset)
        {
            try
            {
                ulong fileRefNumber = (ulong)Marshal.ReadInt64(buffer, offset + UsnRecordV2Offset.FileReferenceNumber);
                ulong parentRefNumber = (ulong)Marshal.ReadInt64(buffer, offset + UsnRecordV2Offset.ParentFileReferenceNumber);
                uint fileAttributes = (uint)Marshal.ReadInt32(buffer, offset + UsnRecordV2Offset.FileAttributes);
                ushort fileNameLength = (ushort)Marshal.ReadInt16(buffer, offset + UsnRecordV2Offset.FileNameLength);
                ushort fileNameOffset = (ushort)Marshal.ReadInt16(buffer, offset + UsnRecordV2Offset.FileNameOffset);

                if (fileNameLength <= 0 || fileNameLength > MaxFileNameLengthBytes)
                {
                    return null;
                }

                var namePtr = IntPtr.Add(buffer, offset + fileNameOffset);
                var fileName = Marshal.PtrToStringUni(namePtr, fileNameLength / 2);

                if (string.IsNullOrEmpty(fileName))
                {
                    return null;
                }

                return new MftRecord
                {
                    FileReferenceNumber = fileRefNumber,
                    ParentFileReferenceNumber = parentRefNumber,
                    FileName = fileName,
                    IsDirectory = (fileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0,
                };
            }
            catch
            {
                // 個別レコードのパース失敗は無視して続行(壊れたレコード1件で全体を止めない)
                return null;
            }
        }
    }
}
