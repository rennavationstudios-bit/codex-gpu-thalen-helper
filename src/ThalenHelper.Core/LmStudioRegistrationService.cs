using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace ThalenHelper.Core;

public sealed record LmStudioRegistrationResult(
    bool Success,
    string Code,
    string Message,
    string Provider,
    string Model,
    string? Digest,
    long ExactMilliseconds,
    long ReviewMilliseconds,
    bool Unloaded);

public sealed class LmStudioRegistrationService
{
    private readonly ProductPaths _paths;
    private readonly StateStore _stateStore;
    private readonly ModelValidationStore _validationStore;
    private readonly LmStudioClient _client;
    private readonly ModelManifest _catalog;
    private readonly ActiveModelTracker _tracker;
    private readonly ILmStudioCliModelBinding _cliBinding;
    private readonly CodexConfigManager _configManager;
    private readonly Func<InstallationState, bool, ResourcePressureCheck> _resourcePressureValidator;
    private readonly Func<InstallationState, ProtectedFileSnapshot, CancellationToken, Task<ProtectedFileSnapshot>> _stateSaver;

    public LmStudioRegistrationService(ProductPaths paths, StateStore stateStore, LmStudioClient client)
        : this(
            paths,
            stateStore,
            client,
            new LmStudioCliModelBinding(),
            new ModelCatalogService().LoadBundled(),
            CreateDefaultPressureValidator())
    {
    }

    internal LmStudioRegistrationService(
        ProductPaths paths,
        StateStore stateStore,
        LmStudioClient client,
        ILmStudioCliModelBinding cliBinding,
        ModelManifest catalog,
        Func<InstallationState, bool, ResourcePressureCheck> resourcePressureValidator,
        CodexConfigManager? configManager = null,
        Func<InstallationState, ProtectedFileSnapshot, CancellationToken, Task<ProtectedFileSnapshot>>? stateSaver = null)
    {
        _paths = paths;
        _stateStore = stateStore;
        _validationStore = new ModelValidationStore(paths.StateDirectory);
        _client = client;
        _cliBinding = cliBinding ?? throw new ArgumentNullException(nameof(cliBinding));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _configManager = configManager ?? new CodexConfigManager();
        _resourcePressureValidator = resourcePressureValidator
            ?? throw new ArgumentNullException(nameof(resourcePressureValidator));
        _stateSaver = stateSaver ?? stateStore.SaveIfUnchangedAsync;
        _tracker = new ActiveModelTracker(paths.StateDirectory);
    }

    public async Task<LmStudioRegistrationResult> ValidateAndEnableAsync(
        string modelKey,
        string modelPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelKey)
            || modelKey.Length > 128
            || !modelKey.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/'))
        {
            return Failure("MODEL_NOT_AUDITED", "The LM Studio model is not present in the audited catalog.", modelKey);
        }

        // A removed catalog entry or any later failure must not leave the prior pass routable.
        await _validationStore.RemoveAsync(ModelProviders.LmStudio, modelKey, CancellationToken.None).ConfigureAwait(false);

        if (!LmStudioModelFileBinding.ExactLoadedFileBindingSupported)
        {
            return Failure(
                "LMSTUDIO_EXACT_FILE_BINDING_UNAVAILABLE",
                "LM Studio registration is temporarily disabled because the loopback API does not expose the absolute file behind a loaded model key. The helper will not claim that an arbitrary GGUF path is the exact file LM Studio will execute.",
                modelKey);
        }

        var entry = _catalog.Models.FirstOrDefault(model =>
            string.Equals(model.Provider, ModelProviders.LmStudio, StringComparison.OrdinalIgnoreCase)
            && string.Equals(model.Tag, modelKey, StringComparison.Ordinal));
        if (entry is null || string.IsNullOrWhiteSpace(entry.ExpectedDigest))
        {
            return Failure("MODEL_NOT_AUDITED", "The LM Studio model is not present in the audited catalog.", modelKey);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(modelPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure("MODEL_FILE_CATALOG_BINDING_MISMATCH", "The selected GGUF path is invalid.", modelKey);
        }

        if (!LmStudioModelFileBinding.IsCanonicalCatalogBinding(entry, modelKey, fullPath))
        {
            return Failure("MODEL_FILE_CATALOG_BINDING_MISMATCH", "The selected GGUF path is not the exact indexed path bound to this audited LM Studio model key.", modelKey);
        }

        if (!LmStudioModelFileBinding.TryOpen(fullPath, out var modelStream, out var auditedProof)
            || auditedProof.Length != (long)entry.ExpectedDownloadBytes)
        {
            return Failure("MODEL_FILE_IDENTITY_MISMATCH", "The selected GGUF is missing, redirected, or has an unexpected size.", modelKey);
        }

        await using var heldModelStream = modelStream;
        string digest;
        try
        {
            digest = await ComputeSha256Async(heldModelStream, cancellationToken).ConfigureAwait(false);
            if (!LmStudioModelFileBinding.TryReadProof(heldModelStream.SafeFileHandle, fullPath, out var proofAfterHash)
                || proofAfterHash != auditedProof)
            {
                return Failure("MODEL_FILE_IDENTITY_CHANGED", "The selected GGUF changed while its full digest was being validated.", modelKey);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return Failure("MODEL_FILE_READ_FAILED", "The selected GGUF could not be fully hashed while held read-only.", modelKey);
        }

        if (!string.Equals(digest, ModelValidationStore.NormalizeFullDigest(entry.ExpectedDigest), StringComparison.Ordinal))
        {
            return Failure("MODEL_DIGEST_MISMATCH", "The selected GGUF does not match the audited full SHA-256 digest.", modelKey);
        }

        var stateLoad = await _stateStore.LoadWithRevisionAsync(cancellationToken).ConfigureAwait(false);
        var state = stateLoad.State;
        if (state is null)
        {
            return Failure("NOT_CONFIGURED", "Install the helper before registering an LM Studio model.", modelKey);
        }
        if (_configManager.InspectOwnership(_paths) != CodexIntegrationOwnership.ManagedValid)
        {
            return Failure(
                "INTEGRATION_OWNERSHIP_DRIFT",
                "The managed Codex reviewer entry is missing, external, invalid, or changed. LM Studio validation did not modify or enable it.",
                modelKey,
                digest);
        }

        var exactMs = 0L;
        var reviewMs = 0L;
        var unloaded = false;
        LmStudioLoadResult? load = null;
        Exception? operationFailure = null;
        IDisposable? gpuLease = null;
        IDisposable? modelPathLease = null;
        try
        {
            gpuLease = await GpuCoordination.AcquireAsync(
                ReviewBusyBehavior.Queue,
                TimeSpan.FromSeconds(120),
                cancellationToken).ConfigureAwait(false);
            modelPathLease = _cliBinding.AcquireModelPathLease(
                entry.IndexedModelPath!,
                auditedProof);
            await _cliBinding.VerifyDownloadedAsync(
                modelKey,
                entry.IndexedModelPath!,
                auditedProof,
                cancellationToken).ConfigureAwait(false);
            var inventory = await _client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            var model = inventory.SingleOrDefault(candidate => string.Equals(candidate.Key, modelKey, StringComparison.Ordinal));
            if (model is null || model.SizeBytes != entry.ExpectedDownloadBytes || model.MaxContextLength < 65_536)
            {
                return Failure("MODEL_IDENTITY_MISMATCH", "LM Studio did not report the exact audited model identity and capabilities.", modelKey, digest);
            }
            if (inventory.SelectMany(candidate => candidate.LoadedInstances).Any())
            {
                return Failure("FOREIGN_MODEL_LOADED", "LM Studio already has a model loaded. The helper will not unload or replace a user-owned instance.", modelKey, digest);
            }

            var routedState = state with { SelectedModel = modelKey, SelectedModelDigest = digest, SelectedModelProvider = ModelProviders.LmStudio };
            var pressure = _resourcePressureValidator(routedState, false);
            if (!pressure.Allowed)
            {
                return Failure(pressure.Code, pressure.Message, modelKey, digest);
            }

            if (!LmStudioModelFileBinding.TryReadProof(
                    heldModelStream.SafeFileHandle,
                    fullPath,
                    out var proofBeforeLoad)
                || proofBeforeLoad != auditedProof)
            {
                return Failure("MODEL_FILE_IDENTITY_CHANGED", "The validated LM Studio GGUF changed before the audited model key could be loaded.", modelKey, digest);
            }

            load = await _client.LoadOwnedAsync(
                modelKey,
                _tracker,
                (instanceId, cleanupToken) => _cliBinding.VerifyUnloadedAsync(
                    instanceId,
                    entry.IndexedModelPath!,
                    cleanupToken),
                cancellationToken).ConfigureAwait(false);
            await _cliBinding.VerifyLoadedAsync(
                load.InstanceId,
                entry.IndexedModelPath!,
                auditedProof,
                cancellationToken).ConfigureAwait(false);
            var timer = Stopwatch.StartNew();
            var exact = await _client.GenerateReasoningOffAsync(modelKey, load.InstanceId,
                "Return exactly this text and nothing else: THALEN_LMSTUDIO_OK", 64, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            exactMs = timer.ElapsedMilliseconds;
            if (!string.Equals(exact.Response, "THALEN_LMSTUDIO_OK", StringComparison.Ordinal))
            {
                throw new LmStudioException(
                    "MODEL_EXACT_OUTPUT_FAILED",
                    "Qwythos did not pass the reasoning-off exact-output check.");
            }

            timer.Restart();
            var review = await _client.GenerateReasoningOffAsync(modelKey, load.InstanceId,
                "Review this code as an advisory reviewer. Return one concise risk and one test suggestion only:\npublic bool IsAllowed(string? value) => value!.Length > 0;",
                256, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            reviewMs = timer.ElapsedMilliseconds;
            if (string.IsNullOrWhiteSpace(review.Response) || review.Response.Length > 8_000)
            {
                throw new LmStudioException(
                    "MODEL_REVIEW_VALIDATION_FAILED",
                    "Qwythos did not return a bounded review response.");
            }
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        finally
        {
            try
            {
                if (load is not null)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    try
                    {
                        var inspection = _tracker.Inspect();
                        var tracked = inspection.Reference;
                        if (inspection.Status != ActiveModelTrackerStatus.Valid
                            || tracked is null
                            || !string.Equals(tracked.Provider, ModelProviders.LmStudio, StringComparison.Ordinal)
                            || !string.Equals(tracked.Model, modelKey, StringComparison.Ordinal)
                            || !string.Equals(tracked.InstanceId, load.InstanceId, StringComparison.Ordinal))
                        {
                            throw new LmStudioException(
                                "GPU_RELEASE_FAILED",
                                "The helper-created LM Studio instance no longer has exact durable ownership evidence.",
                                retryable: true);
                        }

                        await _client.UnloadAndWaitAsync(modelKey, load.InstanceId, TimeSpan.FromSeconds(30), cleanup.Token).ConfigureAwait(false);
                        await _cliBinding.VerifyUnloadedAsync(
                            load.InstanceId,
                            entry.IndexedModelPath!,
                            cleanup.Token).ConfigureAwait(false);
                        LmStudioClient.ClearExactOwnedReference(
                            _tracker,
                            modelKey,
                            load.InstanceId);
                        unloaded = true;
                    }
                    catch (Exception exception) when (exception is LmStudioException or OperationCanceledException)
                    {
                        unloaded = false;
                    }
                }
            }
            finally
            {
                modelPathLease?.Dispose();
                gpuLease?.Dispose();
            }
        }

        if (!unloaded)
        {
            if (load is not null
                || operationFailure is LmStudioException { Code: "GPU_RELEASE_FAILED" })
            {
                return Failure("GPU_RELEASE_FAILED", "LM Studio did not prove the helper-created instance was unloaded; registration was refused and durable recovery evidence was retained.", modelKey, digest, exactMs, reviewMs, false);
            }
        }

        if (operationFailure is LmStudioException lmStudioFailure)
        {
            return Failure(lmStudioFailure.Code, lmStudioFailure.Message, modelKey, digest, exactMs, reviewMs, unloaded);
        }
        if (operationFailure is OllamaException ollamaFailure)
        {
            return Failure(ollamaFailure.Code, ollamaFailure.Message, modelKey, digest, exactMs, reviewMs, unloaded);
        }
        if (operationFailure is OperationCanceledException cancellationFailure)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
        }
        if (operationFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (!LmStudioModelFileBinding.TryReadProof(
                heldModelStream.SafeFileHandle,
                fullPath,
                out var proofBeforeRegistration)
            || proofBeforeRegistration != auditedProof)
        {
            return Failure("MODEL_FILE_IDENTITY_CHANGED", "The validated LM Studio GGUF changed before its registration could be recorded.", modelKey, digest, exactMs, reviewMs, true);
        }

        IDisposable registrationPathLease;
        try
        {
            registrationPathLease = _cliBinding.AcquireModelPathLease(
                entry.IndexedModelPath!,
                auditedProof);
            await _cliBinding.VerifyDownloadedAsync(
                modelKey,
                entry.IndexedModelPath!,
                auditedProof,
                cancellationToken).ConfigureAwait(false);
        }
        catch (LmStudioException exception)
        {
            return Failure(exception.Code, exception.Message, modelKey, digest, exactMs, reviewMs, true);
        }
        using var heldRegistrationPathLease = registrationPathLease;

        var validatedAt = DateTimeOffset.UtcNow;
        var registration = new LocalModelRegistration(
            ModelProviders.LmStudio,
            modelKey,
            digest,
            fullPath,
            validatedAt,
            auditedProof.Length,
            auditedProof.LastWriteTimeUtc,
            auditedProof.FileIdentity);
        var updatedState = BuildActivatedState(state, entry, registration);
        var validation = new ModelValidationEntry(
            modelKey,
            digest,
            ModelValidationStore.CurrentProtocolVersion,
            validatedAt,
            exactMs,
            reviewMs,
            "LM Studio GPU",
            null,
            65_536,
            ModelProviders.LmStudio);

        ManagedFileResult? configChange = null;
        ProtectedFileSnapshot? savedStateRevision = null;
        try
        {
            // SetEnabled revalidates exact managed ownership and uses a hash-bound
            // compare-and-swap. No unmanaged or drifted reviewer table is touched.
            configChange = _configManager.SetEnabled(_paths, true);
            savedStateRevision = await _stateSaver(
                updatedState,
                stateLoad.Revision,
                cancellationToken).ConfigureAwait(false);
            await _validationStore.UpsertAsync(validation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var rollbackSucceeded = await RollbackActivationAsync(
                modelKey,
                state,
                savedStateRevision,
                configChange).ConfigureAwait(false);
            if (!rollbackSucceeded)
            {
                throw new InvalidOperationException(
                    "LM Studio activation was canceled and guarded rollback could not be proven. Review the protected state and config backups before retrying.");
            }

            throw;
        }
        catch (Exception)
        {
            var rollbackSucceeded = await RollbackActivationAsync(
                modelKey,
                state,
                savedStateRevision,
                configChange).ConfigureAwait(false);
            return rollbackSucceeded
                ? Failure(
                    "LMSTUDIO_ACTIVATION_FAILED",
                    "The model passed validation, but the managed Codex activation could not be committed atomically. Prior state and config were restored.",
                    modelKey,
                    digest,
                    exactMs,
                    reviewMs,
                    true)
                : Failure(
                    "LMSTUDIO_ACTIVATION_ROLLBACK_FAILED",
                    "The model passed validation, but activation and guarded rollback could not both be proven. Review the protected state and config backups before retrying.",
                    modelKey,
                    digest,
                    exactMs,
                    reviewMs,
                    true);
        }

        return new LmStudioRegistrationResult(true, "LMSTUDIO_MODEL_VALIDATED", "Qwythos is validated, enabled for automatic standard/deep routing, and unloaded.", ModelProviders.LmStudio, modelKey, digest, exactMs, reviewMs, true);
    }

    internal static InstallationState BuildActivatedState(
        InstallationState priorState,
        ModelCatalogEntry catalogEntry,
        LocalModelRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(priorState);
        ArgumentNullException.ThrowIfNull(catalogEntry);
        ArgumentNullException.ThrowIfNull(registration);
        var lmOnlyInstall = string.IsNullOrWhiteSpace(priorState.SelectedModel);
        var updatedRegistrations = priorState.RegisteredLocalModels
            .Where(item =>
                !string.Equals(item.Provider, ModelProviders.LmStudio, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.Model, registration.Model, StringComparison.Ordinal))
            .Append(registration)
            .ToList();
        return priorState with
        {
            SelectedModel = lmOnlyInstall ? registration.Model : priorState.SelectedModel,
            SelectedModelDigest = lmOnlyInstall ? registration.Digest : priorState.SelectedModelDigest,
            SelectedModelProvider = lmOnlyInstall ? ModelProviders.LmStudio : priorState.SelectedModelProvider,
            HardwareTier = priorState.HardwareTier == HardwareTier.NoModel
                ? ModelSelector.GetHardwareTier(catalogEntry)
                : priorState.HardwareTier,
            RegisteredLocalModels = updatedRegistrations,
            Preferences = priorState.Preferences with
            {
                PreferLmStudioForStandardAndDeep = true,
                ModelSelectionMode = ModelSelectionMode.Automatic
            },
            Availability = HelperAvailability.Enabled,
            ProductVersion = ProductInfo.Version
        };
    }

    private static async Task<string> ComputeSha256Async(FileStream stream, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static Func<InstallationState, bool, ResourcePressureCheck> CreateDefaultPressureValidator()
    {
        var guard = new ResourcePressureGuard();
        return guard.Check;
    }

    private async Task<bool> RollbackActivationAsync(
        string modelKey,
        InstallationState priorState,
        ProtectedFileSnapshot? savedStateRevision,
        ManagedFileResult? configChange)
    {
        var succeeded = true;
        if (savedStateRevision is not null)
        {
            try
            {
                _ = await _stateStore.SaveIfUnchangedAsync(
                    priorState,
                    savedStateRevision,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                succeeded = false;
            }
        }

        if (configChange is { Changed: true })
        {
            try
            {
                _configManager.Rollback(configChange);
            }
            catch
            {
                succeeded = false;
            }
        }

        try
        {
            await _validationStore.RemoveAsync(
                ModelProviders.LmStudio,
                modelKey,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The exact attempted key is removed by the caller before validation and
            // the atomic upsert either commits or does not. This is best-effort only.
        }

        return succeeded;
    }

    private static LmStudioRegistrationResult Failure(string code, string message, string model, string? digest = null,
        long exactMs = 0, long reviewMs = 0, bool unloaded = false)
        => new(false, code, message, ModelProviders.LmStudio, model, digest, exactMs, reviewMs, unloaded);
}

internal sealed record LmStudioModelFileProof(
    string FullPath,
    string FileIdentity,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

internal static class LmStudioModelFileBinding
{
    // The signed current-user `lms ls --json` and `lms ps --json` inventories close
    // the file-identity gap left by the loopback API. Runtime routing must still repeat
    // the CLI proof; this flag alone never makes a legacy registration eligible.
    internal static bool ExactLoadedFileBindingSupported => true;

    private const uint ReparsePointAttribute = (uint)FileAttributes.ReparsePoint;
    private const uint DirectoryAttribute = (uint)FileAttributes.Directory;

    internal static bool IsCanonicalCatalogBinding(
        ModelCatalogEntry entry,
        string modelKey,
        string modelPath)
    {
        if (!string.Equals(entry.Provider, ModelProviders.LmStudio, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(entry.Tag, modelKey, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(entry.IndexedModelPath)
            || Path.IsPathRooted(entry.IndexedModelPath))
        {
            return false;
        }

        try
        {
            var segments = entry.IndexedModelPath
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.None);
            if (segments.Length == 0
                || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
            {
                return false;
            }

            var indexedSuffix = Path.Combine(segments);
            var fullPath = Path.GetFullPath(modelPath);
            return fullPath.EndsWith(
                Path.DirectorySeparatorChar + indexedSuffix,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool TryOpen(
        string path,
        out FileStream stream,
        out LmStudioModelFileProof proof)
    {
        stream = null!;
        proof = null!;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (TryReadProof(stream.SafeFileHandle, fullPath, out proof))
            {
                return true;
            }

            stream.Dispose();
            stream = null!;
            proof = null!;
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            stream?.Dispose();
            stream = null!;
            proof = null!;
            return false;
        }
    }

    internal static bool TryReadProof(
        SafeFileHandle handle,
        string fullPath,
        out LmStudioModelFileProof proof)
    {
        proof = null!;
        if (handle.IsInvalid
            || !GetFileInformationByHandle(handle, out var information)
            || (information.FileAttributes & (ReparsePointAttribute | DirectoryAttribute)) != 0)
        {
            return false;
        }

        var length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        var creationTicks = ((long)information.CreationTimeHigh << 32) | information.CreationTimeLow;
        var lastWriteTicks = ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow;
        DateTimeOffset lastWriteTime;
        try
        {
            lastWriteTime = new DateTimeOffset(DateTime.FromFileTimeUtc(lastWriteTicks));
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        proof = new LmStudioModelFileProof(
            Path.GetFullPath(fullPath),
            $"{information.VolumeSerialNumber:x8}:{information.FileIndexHigh:x8}{information.FileIndexLow:x8}:{creationTicks:x16}",
            length,
            lastWriteTime);
        return true;
    }

    internal static bool MatchesCurrentFile(LmStudioModelFileProof expected)
    {
        if (!TryOpen(expected.FullPath, out var stream, out var current))
        {
            return false;
        }

        using (stream)
        {
            return current == expected;
        }
    }

    internal static bool MatchesRegistration(
        LocalModelRegistration registration,
        ModelCatalogEntry? catalog)
    {
        if (catalog is null
            || string.IsNullOrWhiteSpace(registration.FileIdentity)
            || !IsCanonicalCatalogBinding(catalog, registration.Model, registration.Path)
            || !TryOpen(registration.Path, out var stream, out var current))
        {
            return false;
        }

        using (stream)
        {
            return string.Equals(current.FullPath, Path.GetFullPath(registration.Path), StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.FileIdentity, registration.FileIdentity, StringComparison.Ordinal)
                && current.Length == registration.Length
                && registration.LastWriteTimeUtc.HasValue
                && current.LastWriteTimeUtc == registration.LastWriteTimeUtc.Value.ToUniversalTime();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
