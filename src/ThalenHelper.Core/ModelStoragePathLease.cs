using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ThalenHelper.Core;

/// <summary>
/// Pins model-storage directory identities while the helper creates or uses them.
/// Reparse points are never accepted, and callers can prove that every path still
/// resolves to the same fixed-volume directory immediately after a mutation.
/// </summary>
internal sealed class ModelStoragePathLease : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private readonly List<PinnedDirectory> _directories;

    private ModelStoragePathLease(List<PinnedDirectory> directories)
    {
        _directories = directories;
    }

    internal static void ValidateCandidate(string path)
    {
        using var ignored = Acquire(path, createMissing: false, requireFinalDirectory: false);
    }

    internal static ModelStoragePathLease AcquireOrCreate(string path)
        => Acquire(path, createMissing: true, requireFinalDirectory: true);

    internal static ModelStoragePathLease AcquireExisting(string path)
        => Acquire(path, createMissing: false, requireFinalDirectory: true);

    private static ModelStoragePathLease Acquire(
        string path,
        bool createMissing,
        bool requireFinalDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Model-storage namespace leases require Windows.");
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full)
            ?? throw new InvalidOperationException("The model-storage path has no local drive root.");
        if (string.Equals(
                full,
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The model-storage directory cannot be a drive root.");
        }

        RequireFixedReadyDrive(root);
        var directories = new List<PinnedDirectory>();
        try
        {
            var components = EnumerateComponents(root, full).ToArray();
            foreach (var component in components)
            {
                if (!Directory.Exists(component))
                {
                    break;
                }

                directories.Add(OpenOrdinaryDirectory(component));
            }

            if (createMissing)
            {
                Directory.CreateDirectory(full);
                foreach (var component in components.Skip(directories.Count))
                {
                    directories.Add(OpenOrdinaryDirectory(component));
                }
            }

            if (requireFinalDirectory && !Directory.Exists(full))
            {
                throw new DirectoryNotFoundException("The model-storage directory does not exist.");
            }

            // Re-read the live drive after the namespace is pinned. A mount-point or
            // junction would already have failed the component checks above.
            RequireFixedReadyDrive(root);
            return new ModelStoragePathLease(directories);
        }
        catch
        {
            foreach (var directory in directories)
            {
                directory.Handle.Dispose();
            }

            throw;
        }
    }

    private static IEnumerable<string> EnumerateComponents(string root, string full)
    {
        yield return root;
        var relative = Path.GetRelativePath(root, full);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                throw new InvalidOperationException("The model-storage path is not canonical.");
            }

            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private static void RequireFixedReadyDrive(string root)
    {
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
        {
            throw new InvalidOperationException("The model-storage directory must be on a ready fixed local drive.");
        }
    }

    internal void ValidateUnchanged()
    {
        foreach (var pinned in _directories)
        {
            using var current = OpenOrdinaryDirectory(pinned.Path);
            if (current.VolumeSerialNumber != pinned.VolumeSerialNumber
                || current.FileIndex != pinned.FileIndex)
            {
                throw new InvalidOperationException(
                    "The model-storage namespace changed during a guarded operation.");
            }
        }
    }

    private static PinnedDirectory OpenOrdinaryDirectory(string path)
    {
        // Omitting delete sharing pins this namespace component against
        // deletion, rename, and replacement for the lifetime of the lease.
        var handle = CreateFileW(
            path,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException("A model-storage path component could not be pinned safely.");
        }

        try
        {
            var size = (uint)Marshal.SizeOf<FileAttributeTagInformation>();
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileInfoByHandleClass.FileAttributeTagInfo,
                    out var information,
                    size))
            {
                throw new IOException("A model-storage path component could not be inspected safely.");
            }

            var attributes = (FileAttributes)information.FileAttributes;
            if (!attributes.HasFlag(FileAttributes.Directory)
                || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    "Model storage cannot use a symbolic link, junction, mount point, or other reparse component.");
            }

            if (!GetFileInformationByHandle(handle, out var identity))
            {
                throw new IOException("A model-storage path component identity could not be read safely.");
            }

            return new PinnedDirectory(
                path,
                handle,
                identity.VolumeSerialNumber,
                ((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            directory.Handle.Dispose();
        }

        _directories.Clear();
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
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    private enum FileInfoByHandleClass
    {
        FileAttributeTagInfo = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    private sealed record PinnedDirectory(
        string Path,
        SafeFileHandle Handle,
        uint VolumeSerialNumber,
        ulong FileIndex) : IDisposable
    {
        public void Dispose() => Handle.Dispose();
    }
}
