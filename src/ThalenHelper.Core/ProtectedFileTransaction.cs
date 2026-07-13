using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ThalenHelper.Core;

internal sealed record ProtectedFileSnapshot(
    bool Exists,
    byte[] Bytes,
    string SourceSha256);

internal static class ProtectedFileTransaction
{
    public static ProtectedFileSnapshot Capture(string path)
    {
        var handle = OpenRegularFile(
            path,
            GenericRead,
            FileShareRead,
            allowMissing: true);
        if (handle is null)
        {
            return new ProtectedFileSnapshot(false, [], "MISSING");
        }

        using var stream = new FileStream(handle, FileAccess.Read);
        var bytes = ReadAllBytes(stream);
        return new ProtectedFileSnapshot(
            true,
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static string DecodeUtf8(ProtectedFileSnapshot snapshot)
    {
        var bytes = snapshot.Bytes.AsSpan();
        var preamble = Encoding.UTF8.Preamble;
        if (bytes.StartsWith(preamble))
        {
            bytes = bytes[preamble.Length..];
        }

        return new UTF8Encoding(false, true).GetString(bytes);
    }

    public static string CreateBackup(string path, ProtectedFileSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            throw new InvalidOperationException("A missing protected file cannot be backed up.");
        }

        var timestamp = DateTimeOffset.UtcNow.ToString(
            "yyyyMMdd-HHmmss-fff",
            System.Globalization.CultureInfo.InvariantCulture);
        var backup = $"{path}.thalen-helper.{timestamp}.{Guid.NewGuid():N}.bak";
        using var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(snapshot.Bytes);
        stream.Flush(flushToDisk: true);
        return backup;
    }

    public static void ReplaceIfUnchanged(
        string path,
        ProtectedFileSnapshot expected,
        byte[] replacement)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Protected path has no directory.");
        Directory.CreateDirectory(directory);
        if (!expected.Exists)
        {
            var temporary = $"{path}.thalen-helper.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var created = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    created.Write(replacement);
                    created.Flush(flushToDisk: true);
                }

                File.Move(temporary, path, overwrite: false);
                return;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        try
        {
            var handle = OpenRegularFile(
                path,
                GenericRead | GenericWrite,
                0,
                allowMissing: false)!;
            using var current = new FileStream(handle, FileAccess.ReadWrite);
            if (!ReadAllBytes(current).AsSpan().SequenceEqual(expected.Bytes))
            {
                throw ChangedException(path);
            }

            try
            {
                WriteLocked(current, replacement);
            }
            catch
            {
                WriteLocked(current, expected.Bytes);
                throw;
            }
        }
        catch (FileNotFoundException exception)
        {
            throw ChangedException(path, exception);
        }
    }

    public static void DeleteIfUnchanged(string path, ProtectedFileSnapshot expected)
    {
        if (!expected.Exists)
        {
            throw new InvalidOperationException("A missing protected file cannot be deleted.");
        }

        try
        {
            var handle = OpenRegularFile(
                path,
                GenericRead | GenericWrite | DeleteAccess,
                0,
                allowMissing: false)!;
            using var current = new FileStream(handle, FileAccess.ReadWrite);
            if (!ReadAllBytes(current).AsSpan().SequenceEqual(expected.Bytes))
            {
                throw ChangedException(path);
            }

            var disposition = new FileDispositionInfo { DeleteFile = true };
            if (!SetFileInformationByHandle(
                    current.SafeFileHandle,
                    FileInfoByHandleClass.FileDispositionInfo,
                    ref disposition,
                    (uint)Marshal.SizeOf<FileDispositionInfo>()))
            {
                throw new IOException(
                    $"Windows could not delete the verified {Path.GetFileName(path)} handle.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        catch (FileNotFoundException exception)
        {
            throw ChangedException(path, exception);
        }
    }

    public static bool TryRestoreIfUnchanged(
        string path,
        byte[] appliedBytes,
        ProtectedFileSnapshot original)
    {
        try
        {
            var applied = new ProtectedFileSnapshot(
                true,
                appliedBytes,
                Convert.ToHexString(SHA256.HashData(appliedBytes)));
            if (original.Exists)
            {
                ReplaceIfUnchanged(path, applied, original.Bytes);
                return true;
            }

            DeleteIfUnchanged(path, applied);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    public static bool Matches(string path, ProtectedFileSnapshot expected)
    {
        try
        {
            var current = Capture(path);
            return current.Exists == expected.Exists
                && current.Bytes.AsSpan().SequenceEqual(expected.Bytes);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void WriteLocked(FileStream stream, byte[] content)
    {
        stream.Position = 0;
        stream.Write(content);
        stream.SetLength(content.Length);
        stream.Flush(flushToDisk: true);
        stream.Position = 0;
    }

    private static SafeFileHandle? OpenRegularFile(
        string path,
        uint desiredAccess,
        uint shareMode,
        bool allowMissing)
    {
        var handle = CreateFileW(
            ToExtendedWindowsPath(path),
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (allowMissing && error is 2 or 3)
            {
                return null;
            }

            if (error is 2 or 3)
            {
                throw ChangedException(path, new Win32Exception(error));
            }

            throw new IOException(
                $"Windows could not safely open {Path.GetFileName(path)} for protected access.",
                new Win32Exception(error));
        }

        try
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileInfoByHandleClass.FileAttributeTagInfo,
                    out var attributeTag,
                    (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            {
                throw new IOException(
                    $"Windows could not inspect {Path.GetFileName(path)} before protected access.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if ((attributeTag.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)} is a reparse point. Protected-file operations refuse redirected final paths.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string ToExtendedWindowsPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return full;
        }

        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private enum FileInfoByHandleClass
    {
        FileDispositionInfo = 4,
        FileAttributeTagInfo = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    private static InvalidOperationException ChangedException(string path, Exception? inner = null)
        => new(
            $"{Path.GetFileName(path)} changed while the protected update was being prepared. No update was applied; review the current file and retry.",
            inner);
}
