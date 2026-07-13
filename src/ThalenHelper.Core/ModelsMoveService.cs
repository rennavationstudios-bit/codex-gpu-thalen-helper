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

public sealed class ModelsMoveService
{
    private readonly ProductPaths _paths;
    private readonly StateStore _stateStore;
    private readonly ControlService _control;
    private readonly OllamaAutoStartManager _autoStart;
    private readonly Action<string, string> _destinationValidator;

    public ModelsMoveService(
        ProductPaths paths,
        StateStore stateStore,
        ControlService control,
        OllamaAutoStartManager? autoStart = null,
        Action<string, string>? destinationValidator = null)
    {
        _paths = paths;
        _stateStore = stateStore;
        _control = control;
        _autoStart = autoStart ?? new OllamaAutoStartManager();
        _destinationValidator = destinationValidator ?? ValidateDestinationVolume;
    }

    public async Task<ModelsMoveResult> MoveAsync(string destination, CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No installation state was found.");
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
        _ = await _control.PauseAsync(cancellationToken).ConfigureAwait(false);
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

            var sourceRemoved = await TryQuarantineAndDeleteOwnedTreeAsync(source, ownedFiles, cancellationToken).ConfigureAwait(false);
            if (priorAvailability == HelperAvailability.Enabled)
            {
                var resume = await _control.ResumeAsync(cancellationToken).ConfigureAwait(false);
                if (!resume.Success)
                {
                    throw new InvalidOperationException($"The moved model directory could not be re-enabled safely: {resume.Code}");
                }
            }

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
        catch
        {
            state.ModelStorageLocation = priorLocation;
            state.Availability = priorAvailability == HelperAvailability.Enabled
                ? HelperAvailability.Paused
                : priorAvailability;
            _ = await _autoStart.ApplyConfigurationAsync(
                _paths,
                state,
                finalDestination,
                state.Preferences.AutoStartOllama,
                allowSafeRestart: true,
                CancellationToken.None).ConfigureAwait(false);
            await _stateStore.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
            if (destinationCreated && Directory.Exists(finalDestination))
            {
                _ = await TryDeleteOwnedTreeAsync(finalDestination, ownedFiles, CancellationToken.None).ConfigureAwait(false);
            }
            else if (Directory.Exists(staging))
            {
                _ = await TryDeleteOwnedTreeAsync(staging, ownedFiles, CancellationToken.None).ConfigureAwait(false);
            }

            if (priorAvailability == HelperAvailability.Enabled)
            {
                _ = await _control.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

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

    private static async Task<bool> TryQuarantineAndDeleteOwnedTreeAsync(
        string path,
        IReadOnlyList<FileSnapshot> expected,
        CancellationToken cancellationToken)
    {
        var full = ValidateModelDirectory(path, "cleanup");
        if (!await TreeMatchesAsync(full, expected, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var quarantine = full + ".thalen-helper-remove-" + Guid.NewGuid().ToString("N");
        Directory.Move(full, quarantine);
        if (await TreeMatchesAsync(quarantine, expected, cancellationToken).ConfigureAwait(false))
        {
            Directory.Delete(quarantine, true);
            return true;
        }

        if (!Directory.Exists(full))
        {
            Directory.Move(quarantine, full);
        }

        return false;
    }

    private static async Task<bool> TryDeleteOwnedTreeAsync(
        string path,
        IReadOnlyList<FileSnapshot> expected,
        CancellationToken cancellationToken)
    {
        var full = ValidateModelDirectory(path, "cleanup");
        if (!await TreeMatchesAsync(full, expected, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        Directory.Delete(full, true);
        return true;
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
}
