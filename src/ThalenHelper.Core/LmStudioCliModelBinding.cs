using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ThalenHelper.Core;

internal sealed record LmStudioCliDownloadedModel(
    string ModelKey,
    string IndexedPath,
    string Type,
    string Format,
    ulong SizeBytes);

internal sealed record LmStudioCliLoadedModel(
    string Identifier,
    string IndexedPath);

internal interface ILmStudioCliModelBinding
{
    IDisposable AcquireModelPathLease(
        string indexedPath,
        LmStudioModelFileProof expectedFile);

    Task<LmStudioCliDownloadedModel> VerifyDownloadedAsync(
        string modelKey,
        string indexedPath,
        LmStudioModelFileProof expectedFile,
        CancellationToken cancellationToken = default);

    Task VerifyLoadedAsync(
        string instanceId,
        string indexedPath,
        LmStudioModelFileProof expectedFile,
        CancellationToken cancellationToken = default);

    Task VerifyUnloadedAsync(
        string instanceId,
        string indexedPath,
        CancellationToken cancellationToken = default);
}

internal interface ILmStudioCliInventorySource
{
    Task<string> GetDownloadedModelsJsonAsync(CancellationToken cancellationToken);

    Task<string> GetLoadedModelsJsonAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Uses LM Studio's signed, current-user CLI only as a read-only identity oracle.
/// It never imports, downloads, loads, unloads, or runs a model.
/// </summary>
internal sealed class LmStudioCliModelBinding : ILmStudioCliModelBinding
{
    private const int MaximumInventoryItems = 4_096;
    private readonly ILmStudioCliInventorySource _inventory;
    private readonly string _userProfile;

    internal LmStudioCliModelBinding()
        : this(
            new LmStudioCliProcessInventorySource(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    internal LmStudioCliModelBinding(
        ILmStudioCliInventorySource inventory,
        string userProfile)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _userProfile = string.IsNullOrWhiteSpace(userProfile)
            ? throw new ArgumentException("The current-user profile is unavailable.", nameof(userProfile))
            : Path.GetFullPath(userProfile);
    }

    public IDisposable AcquireModelPathLease(
        string indexedPath,
        LmStudioModelFileProof expectedFile)
    {
        ArgumentNullException.ThrowIfNull(expectedFile);
        var expectedPath = NormalizeIndexedPath(indexedPath, requireGguf: true);
        var lease = LmStudioModelPathLease.Acquire(_userProfile, expectedPath);
        try
        {
            VerifyCanonicalFileIdentity(expectedPath, expectedFile);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task<LmStudioCliDownloadedModel> VerifyDownloadedAsync(
        string modelKey,
        string indexedPath,
        LmStudioModelFileProof expectedFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedFile);
        var expectedPath = NormalizeIndexedPath(indexedPath, requireGguf: true);
        VerifyCanonicalFileIdentity(expectedPath, expectedFile);

        var models = ParseDownloadedModels(
            await _inventory.GetDownloadedModelsJsonAsync(cancellationToken).ConfigureAwait(false));
        var keyMatches = models
            .Where(model => string.Equals(model.ModelKey, modelKey, StringComparison.Ordinal))
            .ToArray();
        var pathMatches = models
            .Where(model => string.Equals(model.IndexedPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (keyMatches.Length != 1
            || pathMatches.Length != 1
            || keyMatches[0] != pathMatches[0]
            || !string.Equals(keyMatches[0].Type, "llm", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(keyMatches[0].Format, "gguf", StringComparison.OrdinalIgnoreCase)
            || keyMatches[0].SizeBytes != checked((ulong)expectedFile.Length))
        {
            throw new LmStudioException(
                "LMSTUDIO_MODEL_BINDING_MISMATCH",
                "The signed LM Studio inventory did not uniquely bind the audited GGUF path and size to the requested model key.");
        }

        return keyMatches[0];
    }

    public async Task VerifyLoadedAsync(
        string instanceId,
        string indexedPath,
        LmStudioModelFileProof expectedFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedFile);
        var expectedPath = NormalizeIndexedPath(indexedPath, requireGguf: true);
        VerifyCanonicalFileIdentity(expectedPath, expectedFile);

        var loaded = ParseLoadedModels(
            await _inventory.GetLoadedModelsJsonAsync(cancellationToken).ConfigureAwait(false));
        var instanceMatches = loaded
            .Where(model => string.Equals(model.Identifier, instanceId, StringComparison.Ordinal))
            .ToArray();
        var pathMatches = loaded
            .Where(model => string.Equals(model.IndexedPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (instanceMatches.Length != 1
            || pathMatches.Length != 1
            || instanceMatches[0] != pathMatches[0])
        {
            throw new LmStudioException(
                "LMSTUDIO_LOADED_FILE_MISMATCH",
                "LM Studio did not uniquely bind the helper-created model instance to the exact audited GGUF path.");
        }
    }

    public async Task VerifyUnloadedAsync(
        string instanceId,
        string indexedPath,
        CancellationToken cancellationToken = default)
    {
        var expectedPath = NormalizeIndexedPath(indexedPath, requireGguf: true);
        var loaded = ParseLoadedModels(
            await _inventory.GetLoadedModelsJsonAsync(cancellationToken).ConfigureAwait(false));
        if (loaded.Any(model =>
                string.Equals(model.Identifier, instanceId, StringComparison.Ordinal)
                || string.Equals(model.IndexedPath, expectedPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new LmStudioException(
                "LMSTUDIO_UNLOAD_UNCONFIRMED",
                "The signed LM Studio inventory still reports the helper-created model instance or its audited GGUF as loaded.",
                retryable: true);
        }
    }

    internal static IReadOnlyList<LmStudioCliDownloadedModel> ParseDownloadedModels(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, BoundedJsonOptions);
            var items = RequireBoundedArray(document.RootElement);
            var results = new List<LmStudioCliDownloadedModel>(items.GetArrayLength());
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw Malformed();
                }

                var key = RequiredBoundedString(item, "modelKey", 512);
                var path = NormalizeIndexedPath(RequiredBoundedString(item, "path", 2_048));
                var type = RequiredBoundedString(item, "type", 64);
                var format = RequiredBoundedString(item, "format", 64);
                if (!item.TryGetProperty("sizeBytes", out var size)
                    || !size.TryGetUInt64(out var sizeBytes)
                    || sizeBytes == 0)
                {
                    throw Malformed();
                }

                results.Add(new LmStudioCliDownloadedModel(key, path, type, format, sizeBytes));
            }

            return results;
        }
        catch (JsonException)
        {
            throw Malformed();
        }
    }

    internal static IReadOnlyList<LmStudioCliLoadedModel> ParseLoadedModels(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, BoundedJsonOptions);
            var items = RequireBoundedArray(document.RootElement);
            var results = new List<LmStudioCliLoadedModel>(items.GetArrayLength());
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw Malformed();
                }

                var identifier = OptionalBoundedString(item, "identifier", 512)
                    ?? OptionalBoundedString(item, "modelIdentifier", 512)
                    ?? OptionalBoundedString(item, "modelKey", 512)
                    ?? throw Malformed();
                var path = OptionalBoundedString(item, "path", 2_048)
                    ?? OptionalBoundedString(item, "modelPath", 2_048)
                    ?? throw Malformed();
                results.Add(new LmStudioCliLoadedModel(
                    identifier,
                    NormalizeIndexedPath(path)));
            }

            return results;
        }
        catch (JsonException)
        {
            throw Malformed();
        }
    }

    internal static string NormalizeIndexedPath(string path, bool requireGguf = false)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > 2_048
            || Path.IsPathRooted(path))
        {
            throw Malformed();
        }

        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.None);
        if (segments.Length is < 2 or > 64
            || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."
                || segment.Length > 255
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw Malformed();
        }

        var normalized = string.Join('/', segments);
        if (requireGguf && !normalized.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw Malformed();
        }

        return normalized;
    }

    private void VerifyCanonicalFileIdentity(
        string indexedPath,
        LmStudioModelFileProof expectedFile)
    {
        var canonicalPath = Path.Combine(
            _userProfile,
            ".lmstudio",
            "models",
            Path.Combine(indexedPath.Split('/')));
        if (!LmStudioModelFileBinding.TryOpen(canonicalPath, out var canonicalStream, out var canonicalProof))
        {
            throw new LmStudioException(
                "LMSTUDIO_MODEL_FILE_ALIAS_MISMATCH",
                "The LM Studio catalog path is not the same regular GGUF file that was audited.");
        }

        using (canonicalStream)
        {
            if (!string.Equals(canonicalProof.FileIdentity, expectedFile.FileIdentity, StringComparison.Ordinal)
                || canonicalProof.Length != expectedFile.Length
                || canonicalProof.LastWriteTimeUtc != expectedFile.LastWriteTimeUtc)
            {
                throw new LmStudioException(
                    "LMSTUDIO_MODEL_FILE_ALIAS_MISMATCH",
                    "The LM Studio catalog path is not the same regular GGUF file that was audited.");
            }
        }
    }

    private static JsonElement RequireBoundedArray(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > MaximumInventoryItems)
        {
            throw Malformed();
        }

        return root;
    }

    private static string RequiredBoundedString(JsonElement item, string name, int maximumLength)
        => OptionalBoundedString(item, name, maximumLength) ?? throw Malformed();

    private static string? OptionalBoundedString(JsonElement item, string name, int maximumLength)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Malformed();
        }

        var result = value.GetString();
        return string.IsNullOrWhiteSpace(result) || result.Length > maximumLength
            ? throw Malformed()
            : result;
    }

    private static LmStudioException Malformed()
        => new(
            "LMSTUDIO_CLI_MALFORMED_RESPONSE",
            "The signed LM Studio CLI returned malformed model inventory.");

    private static readonly JsonDocumentOptions BoundedJsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    };
}

/// <summary>
/// Pins the current-user LM Studio model namespace while a model is loaded. In
/// particular, a supported models-root junction cannot be retargeted between the
/// audited file proof and LM Studio opening the indexed path.
/// </summary>
internal sealed class LmStudioModelPathLease : IDisposable
{
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private readonly List<SafeFileHandle> _handles;

    private LmStudioModelPathLease(List<SafeFileHandle> handles)
    {
        _handles = handles;
    }

    internal static LmStudioModelPathLease Acquire(
        string userProfile,
        string indexedPath)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            var lmStudioDirectory = Path.Combine(userProfile, ".lmstudio");
            var modelsDirectory = Path.Combine(lmStudioDirectory, "models");
            var lmStudioHandle = OpenDirectory(lmStudioDirectory);
            handles.Add(lmStudioHandle);
            RequireOrdinaryDirectory(lmStudioHandle);

            var modelsHandle = OpenDirectory(modelsDirectory);
            handles.Add(modelsHandle);
            RequireSupportedModelsRoot(modelsHandle);

            var segments = indexedPath.Split('/');
            var current = modelsDirectory;
            foreach (var segment in segments[..^1])
            {
                current = Path.Combine(current, segment);
                var currentHandle = OpenDirectory(current);
                handles.Add(currentHandle);
                RequireOrdinaryDirectory(currentHandle);
            }

            return new LmStudioModelPathLease(handles);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }

            throw new LmStudioException(
                "LMSTUDIO_MODEL_NAMESPACE_UNSAFE",
                "The LM Studio model namespace could not be pinned safely for exact-file loading.");
        }
    }

    private static void RequireOrdinaryDirectory(SafeFileHandle handle)
    {
        var information = ReadAttributeTagInformation(
            handle,
            "The LM Studio model namespace identity could not be read.");
        var attributes = (FileAttributes)information.FileAttributes;
        if (!attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint)
            || information.ReparseTag != 0)
        {
            throw new IOException("A nested LM Studio model directory is redirected or is not a directory.");
        }
    }

    public void Dispose()
    {
        foreach (var handle in _handles)
        {
            handle.Dispose();
        }

        _handles.Clear();
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = CreateFileW(
            path,
            FileListDirectory | FileReadAttributes,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException("The LM Studio model namespace directory could not be opened safely.");
        }

        return handle;
    }

    private static void RequireSupportedModelsRoot(SafeFileHandle handle)
    {
        var information = ReadAttributeTagInformation(
            handle,
            "The LM Studio models-root identity could not be read.");
        var attributes = (FileAttributes)information.FileAttributes;
        var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
        if (!attributes.HasFlag(FileAttributes.Directory)
            || (isReparsePoint
                ? information.ReparseTag != IoReparseTagMountPoint
                : information.ReparseTag != 0))
        {
            throw new IOException("Only a regular directory or local junction is accepted as the LM Studio models root.");
        }
    }

    private static FileAttributeTagInformation ReadAttributeTagInformation(
        SafeFileHandle handle,
        string failureMessage)
    {
        var size = (uint)Marshal.SizeOf<FileAttributeTagInformation>();
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out var information,
                size))
        {
            throw new IOException(failureMessage);
        }

        return information;
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
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    private enum FileInfoByHandleClass
    {
        FileAttributeTagInfo = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }
}

/// <summary>
/// Executes only the two fixed, read-only inventory commands against the canonical,
/// Authenticode-verified current-user LM Studio CLI. No caller-controlled arguments
/// reach the process boundary.
/// </summary>
internal sealed class LmStudioCliProcessInventorySource : ILmStudioCliInventorySource
{
    private const int MaximumOutputCharacters = 2 * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    public Task<string> GetDownloadedModelsJsonAsync(CancellationToken cancellationToken)
        => RunAsync("ls", cancellationToken);

    public Task<string> GetLoadedModelsJsonAsync(CancellationToken cancellationToken)
        => RunAsync("ps", cancellationToken);

    internal static bool IsTrustedExecutablePath(
        string executablePath,
        string userProfile,
        bool signatureValid)
    {
        if (!signatureValid
            || string.IsNullOrWhiteSpace(executablePath)
            || string.IsNullOrWhiteSpace(userProfile))
        {
            return false;
        }

        try
        {
            using var lease = LmStudioCliExecutableNamespaceLease.Acquire(
                executablePath,
                userProfile);
            return true;
        }
        catch (Exception exception) when (exception is
            LmStudioException or
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private static async Task<string> RunAsync(
        string inventoryCommand,
        CancellationToken cancellationToken)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var executablePath = Path.Combine(userProfile, ".lmstudio", "bin", "lms.exe");
        using var executableLease = LmStudioCliExecutableNamespaceLease.Acquire(
            executablePath,
            userProfile);
        if (!AuthenticodeVerifier.Verify(executablePath, "Element Labs Inc."))
        {
            throw Untrusted();
        }

        using var timeout = new CancellationTokenSource(CommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!
            }
        };
        process.StartInfo.ArgumentList.Add(inventoryCommand);
        process.StartInfo.ArgumentList.Add("--json");

        try
        {
            if (!process.Start())
            {
                throw new LmStudioException(
                    "LMSTUDIO_CLI_FAILED",
                    "The signed LM Studio CLI did not start.");
            }

            var stdoutTask = ReadBoundedAsync(process.StandardOutput, linked.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, linked.Token);
            await Task.WhenAll(
                process.WaitForExitAsync(linked.Token),
                stdoutTask,
                stderrTask).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new LmStudioException(
                    "LMSTUDIO_CLI_FAILED",
                    "The signed LM Studio CLI returned an error without a trusted model binding.");
            }

            return stdout;
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new LmStudioException(
                "LMSTUDIO_CLI_TIMEOUT",
                "The signed LM Studio CLI inventory request timed out.",
                retryable: true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (LmStudioException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            TryKill(process);
            throw new LmStudioException(
                "LMSTUDIO_CLI_FAILED",
                "The signed LM Studio CLI could not be used safely.");
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(MaximumOutputCharacters, 64 * 1024));
        var buffer = new char[16 * 1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return builder.ToString();
            }

            if (builder.Length > MaximumOutputCharacters - read)
            {
                throw new LmStudioException(
                    "LMSTUDIO_CLI_RESPONSE_TOO_LARGE",
                    "The signed LM Studio CLI exceeded the bounded output limit.");
            }

            builder.Append(buffer, 0, read);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit();
        }
        catch
        {
            // Preserve the primary failure.
        }
    }

    private static LmStudioException Untrusted()
        => new(
            "LMSTUDIO_CLI_UNTRUSTED",
            "The signed current-user LM Studio CLI could not be verified at its canonical location.");
}

/// <summary>
/// Pins the canonical current-user LM Studio CLI namespace and executable from
/// before signature verification until after the CLI exits. Directory and file
/// handles deny write/delete sharing, so the verified path cannot be renamed,
/// redirected, replaced, or retargeted before Process.Start opens it.
/// </summary>
internal sealed class LmStudioCliExecutableNamespaceLease : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private readonly List<SafeFileHandle> _handles;

    private LmStudioCliExecutableNamespaceLease(List<SafeFileHandle> handles)
    {
        _handles = handles;
    }

    internal static LmStudioCliExecutableNamespaceLease Acquire(
        string executablePath,
        string userProfile)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            if (!OperatingSystem.IsWindows()
                || string.IsNullOrWhiteSpace(executablePath)
                || string.IsNullOrWhiteSpace(userProfile))
            {
                throw new IOException("The canonical LM Studio CLI namespace is unavailable.");
            }

            var profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userProfile));
            var expectedExecutable = Path.GetFullPath(
                Path.Combine(profile, ".lmstudio", "bin", "lms.exe"));
            var actualExecutable = Path.GetFullPath(executablePath);
            if (!string.Equals(
                    actualExecutable,
                    expectedExecutable,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The LM Studio CLI is outside its canonical current-user location.");
            }

            var lmStudioDirectory = Path.Combine(profile, ".lmstudio");
            var binDirectory = Path.Combine(lmStudioDirectory, "bin");
            foreach (var directory in new[] { profile, lmStudioDirectory, binDirectory })
            {
                var handle = OpenPinnedPath(directory, isDirectory: true);
                handles.Add(handle);
                RequireOrdinaryPath(handle, isDirectory: true);
            }

            var executableHandle = OpenPinnedPath(actualExecutable, isDirectory: false);
            handles.Add(executableHandle);
            RequireOrdinaryPath(executableHandle, isDirectory: false);
            return new LmStudioCliExecutableNamespaceLease(handles);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }

            throw new LmStudioException(
                "LMSTUDIO_CLI_UNTRUSTED",
                "The signed current-user LM Studio CLI could not be verified at its canonical location.");
        }
    }

    public void Dispose()
    {
        foreach (var handle in _handles)
        {
            handle.Dispose();
        }

        _handles.Clear();
    }

    private static SafeFileHandle OpenPinnedPath(string path, bool isDirectory)
    {
        var handle = CreateFileW(
            path,
            isDirectory ? FileReadAttributes : GenericRead,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | (isDirectory ? FileFlagBackupSemantics : 0),
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException("The LM Studio CLI namespace component could not be pinned.");
        }

        return handle;
    }

    private static void RequireOrdinaryPath(SafeFileHandle handle, bool isDirectory)
    {
        var size = (uint)Marshal.SizeOf<FileAttributeTagInformation>();
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out var information,
                size))
        {
            throw new IOException("The LM Studio CLI namespace identity could not be read.");
        }

        var attributes = (FileAttributes)information.FileAttributes;
        if (attributes.HasFlag(FileAttributes.ReparsePoint)
            || attributes.HasFlag(FileAttributes.Directory) != isDirectory)
        {
            throw new IOException("The LM Studio CLI namespace contains a redirected or unexpected component.");
        }
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
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    private enum FileInfoByHandleClass
    {
        FileAttributeTagInfo = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }
}
