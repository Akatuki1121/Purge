using System;
using System.Collections.Generic;
using System.IO;

namespace Purge;

/// <summary>
/// 既知のインストール先ディレクトリのサイズを計測する。
/// 再解析ポイントは追跡せず、権限不足や走査中の I/O エラーがあれば部分合計を返さず null とする。
/// </summary>
public static class InstallDirectorySizeCalculator
{
    public static long? CalculateBytes(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(directoryPath);
            if (!Directory.Exists(root))
            {
                return null;
            }

            var pending = new Stack<string>();
            pending.Push(root);
            long totalBytes = 0;

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // Junction / symbolic link を辿らず、循環や二重計上を防ぐ。
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    if ((attributes & FileAttributes.Device) != 0)
                    {
                        continue;
                    }

                    totalBytes = checked(totalBytes + new FileInfo(entry).Length);
                }
            }

            return totalBytes;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException
                                   or System.Security.SecurityException
                                   or OverflowException)
        {
            // 不完全なサイズを正しい値として表示しない。
            return null;
        }
    }
}
