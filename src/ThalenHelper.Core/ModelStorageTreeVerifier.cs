using System.Security.Cryptography;

namespace ThalenHelper.Core;

internal sealed record ModelStorageFileSnapshot(
    string RelativePath,
    long Length,
    string Sha256,
    long LastWriteTimeUtcTicks,
    FileAttributes Attributes);

internal sealed record ModelStorageDirectorySnapshot(
    string RelativePath,
    long LastWriteTimeUtcTicks,
    FileAttributes Attributes);

internal sealed record ModelStorageTreeSnapshot(
    string Root,
    IReadOnlyList<ModelStorageDirectorySnapshot> Directories,
    IReadOnlyList<ModelStorageFileSnapshot> Files,
    ulong Bytes);

internal static class ModelStorageTreeVerifier
{
    public static async Task<ModelStorageTreeSnapshot> CaptureAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Model directory does not exist: {fullRoot}");
        }

        RejectReparsePathAndAncestors(fullRoot);
        var directories = new List<ModelStorageDirectorySnapshot>();
        var files = new List<ModelStorageFileSnapshot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        ulong bytes = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"Model storage contains a reparse point and cannot be activated safely: {entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var directoryInfo = new DirectoryInfo(entry);
                    directories.Add(new ModelStorageDirectorySnapshot(
                        Path.GetRelativePath(fullRoot, entry),
                        directoryInfo.LastWriteTimeUtc.Ticks,
                        attributes));
                    pending.Push(entry);
                    continue;
                }

                var relative = Path.GetRelativePath(fullRoot, entry);
                if (!seen.Add(relative))
                {
                    throw new InvalidOperationException($"Model storage contains a duplicate relative path: {relative}");
                }

                var info = new FileInfo(entry);
                var hash = await HashFileAsync(entry, cancellationToken).ConfigureAwait(false);
                files.Add(new ModelStorageFileSnapshot(
                    relative,
                    info.Length,
                    Convert.ToHexString(hash),
                    info.LastWriteTimeUtc.Ticks,
                    attributes));
                bytes = checked(bytes + (ulong)info.Length);
            }
        }

        directories.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        return new ModelStorageTreeSnapshot(fullRoot, directories, files, bytes);
    }

    public static bool Matches(
        ModelStorageTreeSnapshot expected,
        ModelStorageTreeSnapshot actual,
        bool requireMetadata,
        out string discrepancy)
    {
        if (expected.Directories.Count != actual.Directories.Count)
        {
            discrepancy = $"Directory count differs: source={expected.Directories.Count}, destination={actual.Directories.Count}.";
            return false;
        }

        for (var index = 0; index < expected.Directories.Count; index++)
        {
            var left = expected.Directories[index];
            var right = actual.Directories[index];
            if (!string.Equals(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                discrepancy = $"Directory path differs at {left.RelativePath}.";
                return false;
            }

            if (requireMetadata
                && (left.LastWriteTimeUtcTicks != right.LastWriteTimeUtcTicks
                    || NormalizeAttributes(left.Attributes) != NormalizeAttributes(right.Attributes)))
            {
                discrepancy = $"Directory metadata differs at {left.RelativePath}.";
                return false;
            }
        }

        if (expected.Files.Count != actual.Files.Count)
        {
            discrepancy = $"File count differs: source={expected.Files.Count}, destination={actual.Files.Count}.";
            return false;
        }

        if (expected.Bytes != actual.Bytes)
        {
            discrepancy = $"Byte count differs: source={expected.Bytes}, destination={actual.Bytes}.";
            return false;
        }

        for (var index = 0; index < expected.Files.Count; index++)
        {
            var left = expected.Files[index];
            var right = actual.Files[index];
            if (!string.Equals(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase)
                || left.Length != right.Length
                || !string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal))
            {
                discrepancy = $"Content differs at {left.RelativePath}.";
                return false;
            }

            if (requireMetadata
                && (left.LastWriteTimeUtcTicks != right.LastWriteTimeUtcTicks
                    || NormalizeAttributes(left.Attributes) != NormalizeAttributes(right.Attributes)))
            {
                discrepancy = $"Metadata differs at {left.RelativePath}.";
                return false;
            }
        }

        discrepancy = string.Empty;
        return true;
    }

    private static FileAttributes NormalizeAttributes(FileAttributes attributes)
        => attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);

    internal static void RejectReparsePathAndAncestors(string path)
    {
        var current = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(current)?
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(current)
               && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Model storage cannot use a reparse point in its path: {current}");
            }

            current = Directory.GetParent(current)?.FullName;
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }
}
