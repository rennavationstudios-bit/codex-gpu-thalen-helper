using System.Security.Cryptography;

namespace ThalenHelper.Core;

public sealed record ModelsMoveResult(
    bool Success,
    string Code,
    string Message,
    string Source,
    string Destination,
    int FilesVerified,
    ulong BytesVerified,
    bool RolledBack);

internal enum ModelsMoveCheckpoint
{
    DestinationVerified,
    QuarantineVerified,
    OwnedQuarantineFileDeleted
}

public sealed class ModelsMoveService
{
    private readonly ProductPaths _paths;
    private readonly StateStore _stateStore;
    private readonly ControlService _control;
    private readonly OllamaAutoStartManager _autoStart;
    private readonly Action<string, string> _destinationValidator;
    private readonly Action<ModelsMoveCheckpoint>? _checkpoint;

    public ModelsMoveService(
        ProductPaths paths,
        StateStore stateStore,
        ControlService control,
        OllamaAutoStartManager? autoStart = null,
        Action<string, string>? destinationValidator = null)
        : this(paths, stateStore, control, autoStart, destinationValidator, null)
    {
    }

    internal ModelsMoveService(
        ProductPaths paths,
        StateStore stateStore,
        ControlService control,
        OllamaAutoStartManager? autoStart,
        Action<string, string>? destinationValidator,
        Action<ModelsMoveCheckpoint>? checkpoint)
    {
        _paths = paths;
        _stateStore = stateStore;
        _control = control;
        _autoStart = autoStart ?? new OllamaAutoStartManager();
        _destinationValidator = destinationValidator ?? ValidateDestinationVolume;
        _checkpoint = checkpoint;
    }

    public async Task<ModelsMoveResult> MoveAsync(string destination, CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No installation state was found.");
        var ownership = IntegrationOwnership.Inspect(_paths, state);
        if (ownership.Status != IntegrationOwnershipStatus.ManagedValid)
        {
            return new ModelsMoveResult(
                false,
                ownership.Status == IntegrationOwnershipStatus.ExternalUnmarked
                    ? "EXISTING_INTEGRATION_PRESERVED"
                    : "INTEGRATION_OWNERSHIP_DRIFT",
                ownership.Message + " The model directory was not changed.",
                state.ModelStorageLocation ?? string.Empty,
                destination,
                0,
                0,
                false);
        }

        var source = ValidateModelDirectory(state.ModelStorageLocation, "source");
        var finalDestination = ValidateModelDirectory(destination, "destination");
        if (PathsOverlap(source, finalDestination))
        {
            throw new InvalidOperationException("Source and destination model directories cannot overlap.");
        }

        _destinationValidator(finalDestination, source);
        if (Directory.Exists(finalDestination) && Directory.EnumerateFileSystemEntries(finalDestination).Any())
        {
            throw new InvalidOperationException("The destination model directory must be empty or absent.");
        }

        var priorAvailability = state.Availability;
        var priorLocation = state.ModelStorageLocation!;
        var pause = await _control.PauseAsync(cancellationToken).ConfigureAwait(false);
        if (!pause.Success)
        {
            return new ModelsMoveResult(false, pause.Code, pause.Message, source, finalDestination, 0, 0, false);
        }

        var staging = finalDestination + ".staging-" + Guid.NewGuid().ToString("N");
        var filesVerified = 0;
        ulong bytesVerified = 0;
        var destinationCreated = false;
        var ownedFiles = new List<FileSnapshot>();
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, sourceFile);
                var destinationFile = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                File.Copy(sourceFile, destinationFile, false);
                var sourceInfo = new FileInfo(sourceFile);
                var destinationInfo = new FileInfo(destinationFile);
                if (sourceInfo.Length != destinationInfo.Length)
                {
                    throw new IOException($"Size verification failed for {relative}.");
                }

                var sourceHash = await HashFileAsync(sourceFile, cancellationToken).ConfigureAwait(false);
                var destinationHash = await HashFileAsync(destinationFile, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
                {
                    throw new IOException($"SHA-256 verification failed for {relative}.");
                }

                ownedFiles.Add(new FileSnapshot(relative, sourceInfo.Length, Convert.ToHexString(sourceHash)));
                filesVerified++;
                bytesVerified += (ulong)sourceInfo.Length;
            }

            if (filesVerified == 0)
            {
                throw new InvalidOperationException("The source model directory contains no files.");
            }

            if (Directory.Exists(finalDestination))
            {
                Directory.Delete(finalDestination, false);
            }

            Directory.Move(staging, finalDestination);
            destinationCreated = true;
            state.ModelStorageLocation = finalDestination;
            await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            _checkpoint?.Invoke(ModelsMoveCheckpoint.DestinationVerified);
            ThrowIfOwnershipInvalid(state);
            var verification = await _autoStart.ApplyConfigurationAsync(
                _paths,
                state,
                priorLocation,
                state.Preferences.AutoStartOllama,
                allowSafeRestart: true,
                cancellationToken).ConfigureAwait(false);
            if (!ModelIntegrity.IsOperationallySafe(verification, state))
            {
                throw new InvalidOperationException("Ollama did not validate the moved model directory.");
            }

            if (priorAvailability == HelperAvailability.Enabled)
            {
                ThrowIfOwnershipInvalid(state);
                var resume = await _control.ResumeAsync(cancellationToken).ConfigureAwait(false);
                if (!resume.Success)
                {
                    if (IsOwnershipFailure(resume.Code))
                    {
                        throw new ModelsMoveOwnershipException(resume.Code, resume.Message);
                    }

                    throw new InvalidOperationException($"The moved model directory could not be re-enabled safely: {resume.Code}");
                }
            }

            ThrowIfOwnershipInvalid(state);
            var sourceRemoved = await TryQuarantineAndDeleteOwnedTreeAsync(
                source,
                ownedFiles,
                finalDestination,
                cancellationToken).ConfigureAwait(false);
            return new ModelsMoveResult(
                true,
                sourceRemoved ? "MODELS_MOVED" : "MODELS_MOVED_SOURCE_PRESERVED",
                sourceRemoved
                    ? "All files were copied, SHA-256 verified, activated, runtime-checked, and then removed from the old directory."
                    : "The copied model directory was activated and verified, but the source changed during the move and was preserved for manual reconciliation.",
                source,
                finalDestination,
                filesVerified,
                bytesVerified,
                false);
        }
        catch (Exception exception)
        {
            var ownershipFailure = exception as ModelsMoveOwnershipException;
            state.ModelStorageLocation = priorLocation;
            state.Availability = priorAvailability == HelperAvailability.Enabled
                ? HelperAvailability.Paused
                : priorAvailability;
            if (ownershipFailure is null)
            {
                // This ownership check intentionally sits directly before the rollback's
                // first Ollama environment/startup/process mutation.
                ownershipFailure = GetOwnershipFailure(state);
                if (ownershipFailure is null)
                {
                    _ = await _autoStart.ApplyConfigurationAsync(
                        _paths,
                        state,
                        finalDestination,
                        state.Preferences.AutoStartOllama,
                        allowSafeRestart: true,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            await _stateStore.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
            ownershipFailure ??= GetOwnershipFailure(state);

            if (priorAvailability == HelperAvailability.Enabled && ownershipFailure is null)
            {
                ownershipFailure = GetOwnershipFailure(state);
                if (ownershipFailure is null)
                {
                    var rollbackResume = await _control.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
                    if (!rollbackResume.Success && IsOwnershipFailure(rollbackResume.Code))
                    {
                        ownershipFailure = new ModelsMoveOwnershipException(rollbackResume.Code, rollbackResume.Message);
                    }
                }
            }

            if (ownershipFailure is null)
            {
                var sourceStillVerified = await TreeMatchesAsync(source, ownedFiles, CancellationToken.None).ConfigureAwait(false);
                if (sourceStillVerified && destinationCreated && Directory.Exists(finalDestination))
                {
                    _ = await TryQuarantineAndDeleteOwnedTreeAsync(
                        finalDestination,
                        ownedFiles,
                        source,
                        CancellationToken.None).ConfigureAwait(false);
                }
                else if (sourceStillVerified && Directory.Exists(staging))
                {
                    _ = await TryQuarantineAndDeleteOwnedTreeAsync(
                        staging,
                        ownedFiles,
                        source,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (ownershipFailure is not null)
            {
                return new ModelsMoveResult(
                    false,
                    ownershipFailure.Code,
                    ownershipFailure.Message
                        + " No further Ollama environment, startup, or process changes were made. "
                        + "Any verified source, destination, or staging copies were preserved for manual reconciliation.",
                    source,
                    finalDestination,
                    filesVerified,
                    bytesVerified,
                    false);
            }

            throw;
        }
    }

    private void ThrowIfOwnershipInvalid(InstallationState state)
    {
        var failure = GetOwnershipFailure(state);
        if (failure is not null)
        {
            throw failure;
        }
    }

    private ModelsMoveOwnershipException? GetOwnershipFailure(InstallationState state)
    {
        var ownership = IntegrationOwnership.Inspect(_paths, state);
        if (ownership.Status == IntegrationOwnershipStatus.ManagedValid)
        {
            return null;
        }

        var code = ownership.Status == IntegrationOwnershipStatus.ExternalUnmarked
            ? "EXISTING_INTEGRATION_PRESERVED"
            : "INTEGRATION_OWNERSHIP_DRIFT";
        return new ModelsMoveOwnershipException(code, ownership.Message);
    }

    private static bool IsOwnershipFailure(string code)
        => string.Equals(code, "INTEGRATION_OWNERSHIP_DRIFT", StringComparison.Ordinal)
            || string.Equals(code, "EXISTING_INTEGRATION_PRESERVED", StringComparison.Ordinal);

    private static string ValidateModelDirectory(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"The {label} model directory is not configured.");
        }

        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The {label} model directory cannot be a drive root.");
        }

        return full;
    }

    private static bool PathsOverlap(string first, string second)
    {
        var a = first.TrimEnd('\\') + "\\";
        var b = second.TrimEnd('\\') + "\\";
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateDestinationVolume(string destination, string source)
    {
        var root = Path.GetPathRoot(destination)
            ?? throw new InvalidOperationException("The destination has no drive root.");
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
        {
            throw new InvalidOperationException("Models may be moved only to ready fixed local storage.");
        }

        var required = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path).Length)
            .Aggregate(0L, checked((total, length) => total + length));
        var reserve = Math.Max(10L * 1024 * 1024 * 1024, drive.TotalSize / 10);
        if (drive.AvailableFreeSpace < checked(required + reserve))
        {
            throw new InvalidOperationException("The destination does not have enough free space plus safety reserve.");
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

    private async Task<bool> TryQuarantineAndDeleteOwnedTreeAsync(
        string path,
        IReadOnlyList<FileSnapshot> expected,
        string recoverySource,
        CancellationToken cancellationToken)
    {
        var full = ValidateModelDirectory(path, "cleanup");
        if (!await TreeMatchesAsync(full, expected, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var quarantine = full + ".thalen-helper-remove-" + Guid.NewGuid().ToString("N");
        Directory.Move(full, quarantine);
        var deletedFiles = new List<FileSnapshot>();
        if (await TreeMatchesAsync(quarantine, expected, cancellationToken).ConfigureAwait(false))
        {
            _checkpoint?.Invoke(ModelsMoveCheckpoint.QuarantineVerified);
            if (await TryDeleteOwnedFilesAndEmptyDirectoriesAsync(
                    quarantine,
                    expected,
                    recoverySource,
                    deletedFiles,
                    cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        var quarantineCanBeRestored = deletedFiles.Count == 0;
        if (!quarantineCanBeRestored)
        {
            quarantineCanBeRestored = await TryRestoreDeletedFilesAsync(
                quarantine,
                recoverySource,
                deletedFiles,
                cancellationToken).ConfigureAwait(false);
        }

        if (quarantineCanBeRestored && !Directory.Exists(full))
        {
            Directory.Move(quarantine, full);
        }

        return false;
    }

    private async Task<bool> TryDeleteOwnedFilesAndEmptyDirectoriesAsync(
        string quarantine,
        IReadOnlyList<FileSnapshot> expected,
        string recoverySource,
        ICollection<FileSnapshot> deletedFiles,
        CancellationToken cancellationToken)
    {
        // Recheck the complete tree after the verification checkpoint. A writer may
        // have created content in the quarantine between verification and cleanup.
        if (!await TreeMatchesAsync(quarantine, expected, cancellationToken).ConfigureAwait(false)
            || !HasOnlyExpectedDirectories(quarantine, expected)
            || !await TreeMatchesAsync(recoverySource, expected, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        foreach (var expectedFile in expected.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await TryDeleteVerifiedFileAsync(quarantine, expectedFile, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            deletedFiles.Add(expectedFile);
            _checkpoint?.Invoke(ModelsMoveCheckpoint.OwnedQuarantineFileDeleted);
        }

        var quarantinePrefix = Path.GetFullPath(quarantine)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var ownedDirectories = expected
            .SelectMany(file => GetParentDirectories(file.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.Length);
        foreach (var relativeDirectory in ownedDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetFullPath(Path.Combine(quarantine, relativeDirectory));
            if (!directory.StartsWith(quarantinePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                Directory.Delete(directory, false);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        try
        {
            Directory.Delete(quarantine, false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> TryRestoreDeletedFilesAsync(
        string quarantine,
        string recoverySource,
        IReadOnlyCollection<FileSnapshot> deletedFiles,
        CancellationToken cancellationToken)
    {
        var quarantinePrefix = Path.GetFullPath(quarantine)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var recoveryPrefix = Path.GetFullPath(recoverySource)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        foreach (var expected in deletedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(quarantine, expected.RelativePath));
            var source = Path.GetFullPath(Path.Combine(recoverySource, expected.RelativePath));
            if (!target.StartsWith(quarantinePrefix, StringComparison.OrdinalIgnoreCase)
                || !source.StartsWith(recoveryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (File.Exists(target))
            {
                if (!await FileMatchesAsync(target, expected, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                continue;
            }

            var targetDirectory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(targetDirectory);
            var temporary = target + ".thalen-helper-restore-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var sourceStream = new FileStream(
                                 source,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 1024 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var targetStream = new FileStream(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 1024 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await sourceStream.CopyToAsync(targetStream, cancellationToken).ConfigureAwait(false);
                    await targetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!await FileMatchesAsync(temporary, expected, cancellationToken).ConfigureAwait(false))
                {
                    File.Delete(temporary);
                    return false;
                }

                File.Move(temporary, target, false);
            }
            catch (IOException)
            {
                TryDeleteOwnedTemporaryFile(temporary);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                TryDeleteOwnedTemporaryFile(temporary);
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> FileMatchesAsync(
        string path,
        FileSnapshot expected,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists
                || info.Length != expected.Length
                || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var hash = Convert.ToHexString(await HashFileAsync(path, cancellationToken).ConfigureAwait(false));
            return string.Equals(hash, expected.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteOwnedTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool HasOnlyExpectedDirectories(string root, IReadOnlyList<FileSnapshot> expected)
    {
        var expectedDirectories = expected
            .SelectMany(file => GetParentDirectories(file.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, directory);
            if (!expectedDirectories.Contains(relative)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> GetParentDirectories(string relativePath)
    {
        var current = Path.GetDirectoryName(relativePath);
        while (!string.IsNullOrEmpty(current))
        {
            yield return current;
            current = Path.GetDirectoryName(current);
        }
    }

    private static async Task<bool> TryDeleteVerifiedFileAsync(
        string root,
        FileSnapshot expected,
        CancellationToken cancellationToken)
    {
        var rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, expected.RelativePath));
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expected.Length)
            {
                return false;
            }

            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(hash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Delete this one verified path while the read handle still denies new
            // writers. Never recursively delete the containing quarantine.
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> TreeMatchesAsync(
        string path,
        IReadOnlyList<FileSnapshot> expected,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(file => new { Path = file, Relative = Path.GetRelativePath(path, file) })
            .OrderBy(item => item.Relative, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var orderedExpected = expected.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length != orderedExpected.Length)
        {
            return false;
        }

        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = files[index];
            var expectedFile = orderedExpected[index];
            var info = new FileInfo(actual.Path);
            if (!string.Equals(actual.Relative, expectedFile.RelativePath, StringComparison.OrdinalIgnoreCase)
                || info.Length != expectedFile.Length)
            {
                return false;
            }

            var hash = Convert.ToHexString(await HashFileAsync(actual.Path, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(hash, expectedFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record FileSnapshot(string RelativePath, long Length, string Sha256);

    private sealed class ModelsMoveOwnershipException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
