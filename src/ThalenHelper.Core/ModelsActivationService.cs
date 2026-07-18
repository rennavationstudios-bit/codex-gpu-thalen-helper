namespace ThalenHelper.Core;

public sealed record ModelsActivationResult(
    bool Success,
    string Code,
    string Message,
    string Source,
    string Destination,
    int FilesVerified,
    ulong BytesVerified,
    bool SourcePreserved,
    bool RolledBack,
    string? VerificationCode);

public sealed class ModelsActivationService
{
    private readonly ProductPaths _paths;
    private readonly StateStore _stateStore;
    private readonly OllamaAutoStartManager _autoStart;
    private readonly Action<string> _destinationValidator;

    public ModelsActivationService(
        ProductPaths paths,
        StateStore stateStore,
        ControlService control,
        OllamaAutoStartManager? autoStart = null,
        Action<string>? destinationValidator = null)
    {
        _paths = paths;
        _stateStore = stateStore;
        _ = control;
        _autoStart = autoStart ?? new OllamaAutoStartManager();
        _destinationValidator = destinationValidator ?? ValidateFixedLocalDirectory;
    }

    public async Task<ModelsActivationResult> ActivateExistingAsync(
        string destination,
        CancellationToken cancellationToken = default)
    {
        using var operationLease = await ModelStorageOperationLease.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var loaded = await _stateStore.LoadWithRevisionAsync(cancellationToken).ConfigureAwait(false);
        var state = loaded.State ?? throw new InvalidOperationException("No installation state was found.");
        var source = ValidateModelDirectory(state.ModelStorageLocation, "source");
        var target = ValidateModelDirectory(destination, "destination");
        var ownership = IntegrationOwnership.Inspect(_paths, state);
        if (ownership.Status != IntegrationOwnershipStatus.ManagedValid)
        {
            return Failure(
                ownership.Status == IntegrationOwnershipStatus.ExternalUnmarked
                    ? "EXISTING_INTEGRATION_PRESERVED"
                    : "INTEGRATION_OWNERSHIP_DRIFT",
                ownership.Message + " Model storage was not changed.",
                source,
                target);
        }

        if (state.ModelStorageTransition is not null)
        {
            return Failure(
                "MODEL_STORAGE_TRANSITION_PENDING",
                "A previous model storage transition requires models recover --yes before another activation can start.",
                source,
                target);
        }

        if (PathsOverlap(source, target))
        {
            return Failure(
                "MODEL_STORAGE_PATHS_OVERLAP",
                "Source and destination model directories cannot be the same or overlap.",
                source,
                target);
        }

        _destinationValidator(target);
        if (!Directory.Exists(source) || !Directory.Exists(target))
        {
            return Failure(
                "MODEL_STORAGE_DIRECTORY_MISSING",
                "Both the existing source and pre-copied destination must already exist.",
                source,
                target);
        }

        var configuredUserPath = CanonicalizeOptional(_autoStart.GetConfiguredUserModelDirectory());
        if (!string.Equals(configuredUserPath, source, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "MODEL_STORAGE_ENVIRONMENT_DRIFT",
                "The current user's OLLAMA_MODELS value does not match helper state. Nothing was changed.",
                source,
                target);
        }

        ModelStorageTreeSnapshot sourceSnapshot;
        ModelStorageTreeSnapshot targetSnapshot;
        try
        {
            sourceSnapshot = await ModelStorageTreeVerifier.CaptureAsync(source, cancellationToken).ConfigureAwait(false);
            targetSnapshot = await ModelStorageTreeVerifier.CaptureAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failure("MODEL_STORAGE_VERIFICATION_FAILED", exception.Message, source, target);
        }

        if (sourceSnapshot.Files.Count == 0)
        {
            return Failure(
                "MODEL_STORAGE_SOURCE_EMPTY",
                "The configured source contains no model files.",
                source,
                target);
        }

        if (!ModelStorageTreeVerifier.Matches(sourceSnapshot, targetSnapshot, requireMetadata: true, out var discrepancy))
        {
            return Failure(
                "MODEL_STORAGE_DESTINATION_MISMATCH",
                "The pre-copied destination is not an exact path, size, metadata, and SHA-256 match. " + discrepancy,
                source,
                target,
                sourceSnapshot);
        }

        var sourceRecheck = await ModelStorageTreeVerifier.CaptureAsync(source, cancellationToken).ConfigureAwait(false);
        if (!ModelStorageTreeVerifier.Matches(sourceSnapshot, sourceRecheck, requireMetadata: true, out discrepancy))
        {
            return Failure(
                "MODEL_STORAGE_SOURCE_CHANGED",
                "The source changed during verification. Nothing was activated. " + discrepancy,
                source,
                target,
                sourceSnapshot);
        }

        var idle = await _autoStart.VerifyIdleForStorageChangeAsync(cancellationToken).ConfigureAwait(false);
        if (!idle.Idle)
        {
            return Failure(idle.Code, idle.Message, source, target, sourceSnapshot);
        }

        var priorAvailability = state.Availability;
        loaded = await _stateStore.LoadWithRevisionAsync(cancellationToken).ConfigureAwait(false);
        state = loaded.State ?? throw new InvalidOperationException("Installation state disappeared during activation.");
        if (IntegrationOwnership.Inspect(_paths, state).Status != IntegrationOwnershipStatus.ManagedValid
            || !string.Equals(CanonicalizeOptional(state.ModelStorageLocation), source, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(CanonicalizeOptional(_autoStart.GetConfiguredUserModelDirectory()), source, StringComparison.OrdinalIgnoreCase)
            || state.Availability != priorAvailability
            || state.ModelStorageTransition is not null)
        {
            return Failure(
                "MODEL_STORAGE_ACTIVATION_PRECONDITION_CHANGED",
                "Ownership, state, availability, or environment changed during verification. Activation stopped before rebinding.",
                source,
                target,
                sourceSnapshot);
        }

        var transition = new ModelStorageTransition(
            Guid.NewGuid().ToString("N"),
            source,
            target,
            priorAvailability,
            configuredUserPath,
            DateTimeOffset.UtcNow);
        state.ModelStorageTransition = transition;
        state.Availability = HelperAvailability.Paused;
        var transitionRevision = await _stateStore.SaveIfUnchangedAsync(
            state,
            loaded.Revision,
            cancellationToken).ConfigureAwait(false);
        GpuCoordination.RequestCancellation();

        state.ModelStorageLocation = target;
        var targetRevision = await _stateStore.SaveIfUnchangedAsync(
            state,
            transitionRevision,
            cancellationToken).ConfigureAwait(false);

        OllamaStartupVerification? verification = null;
        try
        {
            ThrowIfOwnershipInvalid(state);
            if (!string.Equals(
                    CanonicalizeOptional(_autoStart.GetConfiguredUserModelDirectory()),
                    source,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "OLLAMA_MODELS changed after the activation checkpoint; it was not overwritten.");
            }

            verification = await _autoStart.ApplyConfigurationAsync(
                _paths,
                state,
                source,
                state.Preferences.AutoStartOllama,
                allowSafeRestart: true,
                OllamaRestartModelPolicy.RequireIdle,
                cancellationToken).ConfigureAwait(false);
            if (!ModelIntegrity.IsOperationallySafe(verification, state))
            {
                throw new InvalidOperationException(
                    $"Ollama rejected the activated model directory: {verification.Code}.");
            }

            var finalSource = await ModelStorageTreeVerifier.CaptureAsync(source, cancellationToken).ConfigureAwait(false);
            var finalTarget = await ModelStorageTreeVerifier.CaptureAsync(target, cancellationToken).ConfigureAwait(false);
            if (!ModelStorageTreeVerifier.Matches(sourceSnapshot, finalSource, requireMetadata: true, out discrepancy)
                || !ModelStorageTreeVerifier.Matches(targetSnapshot, finalTarget, requireMetadata: true, out discrepancy))
            {
                throw new InvalidOperationException("A model tree changed during activation. " + discrepancy);
            }

            ThrowIfOwnershipInvalid(state);
            state.ModelStorageTransition = null;
            state.LastHealthCheckAt = DateTimeOffset.UtcNow;
            state.LastHealthCheckCode = verification.Code;
            state.ProductVersion = ProductInfo.Version;
            state.Availability = priorAvailability;
            _ = await _stateStore.SaveIfUnchangedAsync(state, targetRevision, cancellationToken).ConfigureAwait(false);

            if (priorAvailability == HelperAvailability.Enabled)
            {
                try
                {
                    GpuCoordination.ClearCancellation();
                }
                catch (Exception exception)
                {
                    return new ModelsActivationResult(
                        false,
                        "MODELS_ACTIVATED_GPU_SIGNAL_UNCONFIRMED",
                        "The destination was activated and the transition committed, but this process could not clear the cancellation signal: " + exception.Message,
                        source,
                        target,
                        sourceSnapshot.Files.Count,
                        sourceSnapshot.Bytes,
                        true,
                        false,
                        verification.Code);
                }
            }

            return new ModelsActivationResult(
                true,
                "MODELS_ACTIVATED_SOURCE_PRESERVED",
                "The existing destination was SHA-256 verified, activated, runtime-checked, and the source was preserved unchanged.",
                source,
                target,
                sourceSnapshot.Files.Count,
                sourceSnapshot.Bytes,
                true,
                false,
                verification.Code);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return await RollBackAsync(
                state,
                transition,
                targetRevision,
                sourceSnapshot,
                verification?.Code,
                exception.Message).ConfigureAwait(false);
        }
    }

    public async Task<ModelsActivationResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        using var operationLease = await ModelStorageOperationLease.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var loaded = await _stateStore.LoadWithRevisionAsync(cancellationToken).ConfigureAwait(false);
        var state = loaded.State ?? throw new InvalidOperationException("No installation state was found.");
        var transition = state.ModelStorageTransition;
        if (transition is null)
        {
            return Failure(
                "MODEL_STORAGE_TRANSITION_NOT_FOUND",
                "No pending model storage transition requires recovery.",
                state.ModelStorageLocation ?? string.Empty,
                state.ModelStorageLocation ?? string.Empty);
        }

        if (IntegrationOwnership.Inspect(_paths, state).Status != IntegrationOwnershipStatus.ManagedValid)
        {
            return Failure(
                "INTEGRATION_OWNERSHIP_DRIFT",
                "Integration ownership changed. Recovery made no environment or process changes.",
                transition.Source,
                transition.Destination);
        }

        ModelStorageTreeSnapshot sourceSnapshot;
        ModelStorageTreeSnapshot destinationSnapshot;
        try
        {
            sourceSnapshot = await ModelStorageTreeVerifier.CaptureAsync(
                transition.Source,
                cancellationToken).ConfigureAwait(false);
            destinationSnapshot = await ModelStorageTreeVerifier.CaptureAsync(
                transition.Destination,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failure(
                "MODEL_STORAGE_RECOVERY_VERIFICATION_FAILED",
                exception.Message,
                transition.Source,
                transition.Destination);
        }

        if (!ModelStorageTreeVerifier.Matches(
                sourceSnapshot,
                destinationSnapshot,
                requireMetadata: true,
                out var discrepancy))
        {
            return Failure(
                "MODEL_STORAGE_RECOVERY_MISMATCH",
                "Recovery refused because the source and destination no longer match exactly. " + discrepancy,
                transition.Source,
                transition.Destination,
                sourceSnapshot);
        }

        var configured = CanonicalizeOptional(_autoStart.GetConfiguredUserModelDirectory());
        if (!string.Equals(configured, transition.Source, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(configured, transition.Destination, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "MODEL_STORAGE_ENVIRONMENT_DRIFT",
                "OLLAMA_MODELS points to neither side of the pending transition. Recovery made no changes.",
                transition.Source,
                transition.Destination,
                sourceSnapshot);
        }

        var idle = await _autoStart.VerifyIdleForStorageChangeAsync(cancellationToken).ConfigureAwait(false);
        if (!idle.Idle)
        {
            return Failure(idle.Code, idle.Message, transition.Source, transition.Destination, sourceSnapshot);
        }

        try
        {
            loaded = await _stateStore.LoadWithRevisionAsync(cancellationToken).ConfigureAwait(false);
            state = loaded.State ?? throw new InvalidOperationException("Installation state disappeared during recovery.");
            if (state.ModelStorageTransition is null
                || !string.Equals(state.ModelStorageTransition.OperationId, transition.OperationId, StringComparison.Ordinal)
                || IntegrationOwnership.Inspect(_paths, state).Status != IntegrationOwnershipStatus.ManagedValid)
            {
                return Failure(
                    "MODEL_STORAGE_RECOVERY_PRECONDITION_CHANGED",
                    "The pending transition or integration ownership changed during verification.",
                    transition.Source,
                    transition.Destination,
                    sourceSnapshot);
            }

            configured = CanonicalizeOptional(_autoStart.GetConfiguredUserModelDirectory());
            if (!string.Equals(configured, transition.Source, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(configured, transition.Destination, StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    "MODEL_STORAGE_ENVIRONMENT_DRIFT",
                    "OLLAMA_MODELS changed during recovery verification. No mutation was attempted.",
                    transition.Source,
                    transition.Destination,
                    sourceSnapshot);
            }

            idle = await _autoStart.VerifyIdleForStorageChangeAsync(cancellationToken).ConfigureAwait(false);
            if (!idle.Idle)
            {
                return Failure(idle.Code, idle.Message, transition.Source, transition.Destination, sourceSnapshot);
            }

            ThrowIfOwnershipInvalid(state);
            state.ModelStorageLocation = transition.Source;
            state.Availability = HelperAvailability.Paused;
            var recoveryRevision = await _stateStore.SaveIfUnchangedAsync(
                state,
                loaded.Revision,
                cancellationToken).ConfigureAwait(false);
            ThrowIfOwnershipInvalid(state);
            if (!string.Equals(
                    CanonicalizeOptional(_autoStart.GetConfiguredUserModelDirectory()),
                    configured,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new ModelsActivationResult(
                    false,
                    "MODEL_STORAGE_RECOVERY_ENVIRONMENT_CHANGED",
                    "OLLAMA_MODELS changed after the recovery checkpoint. It was not overwritten; the marker remains.",
                    transition.Source,
                    transition.Destination,
                    sourceSnapshot.Files.Count,
                    sourceSnapshot.Bytes,
                    true,
                    false,
                    null);
            }

            var verification = await _autoStart.ApplyConfigurationAsync(
                _paths,
                state,
                configured,
                state.Preferences.AutoStartOllama,
                allowSafeRestart: true,
                OllamaRestartModelPolicy.RequireIdle,
                cancellationToken).ConfigureAwait(false);
            if (!ModelIntegrity.IsOperationallySafe(verification, state))
            {
                return new ModelsActivationResult(
                    false,
                    "MODEL_STORAGE_RECOVERY_RUNTIME_FAILED",
                    "The original path was selected but runtime verification failed. The recovery marker remains and local review stays paused.",
                    transition.Source,
                    transition.Destination,
                    sourceSnapshot.Files.Count,
                    sourceSnapshot.Bytes,
                    true,
                    false,
                    verification.Code);
            }

            var finalSource = await ModelStorageTreeVerifier.CaptureAsync(
                transition.Source,
                cancellationToken).ConfigureAwait(false);
            if (!ModelStorageTreeVerifier.Matches(sourceSnapshot, finalSource, requireMetadata: true, out discrepancy))
            {
                return new ModelsActivationResult(
                    false,
                    "MODEL_STORAGE_RECOVERY_SOURCE_CHANGED",
                    "The source changed during recovery. The recovery marker remains and local review stays paused. " + discrepancy,
                    transition.Source,
                    transition.Destination,
                    sourceSnapshot.Files.Count,
                    sourceSnapshot.Bytes,
                    true,
                    false,
                    verification.Code);
            }

            ThrowIfOwnershipInvalid(state);
            if (!string.Equals(
                    CanonicalizeOptional(_autoStart.GetConfiguredUserModelDirectory()),
                    transition.Source,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new ModelsActivationResult(
                    false,
                    "MODEL_STORAGE_RECOVERY_ENVIRONMENT_UNVERIFIED",
                    "The original path was not retained in OLLAMA_MODELS. The recovery marker remains and local review stays paused.",
                    transition.Source,
                    transition.Destination,
                    sourceSnapshot.Files.Count,
                    sourceSnapshot.Bytes,
                    true,
                    false,
                    verification.Code);
            }

            state.ModelStorageTransition = null;
            state.Availability = transition.PriorAvailability;
            state.LastHealthCheckAt = DateTimeOffset.UtcNow;
            state.LastHealthCheckCode = verification.Code;
            _ = await _stateStore.SaveIfUnchangedAsync(
                state,
                recoveryRevision,
                cancellationToken).ConfigureAwait(false);
            if (transition.PriorAvailability == HelperAvailability.Enabled)
            {
                try
                {
                    GpuCoordination.ClearCancellation();
                }
                catch (Exception exception)
                {
                    return new ModelsActivationResult(
                        false,
                        "MODEL_STORAGE_RECOVERED_GPU_SIGNAL_UNCONFIRMED",
                        "Recovery committed the original path, but this process could not clear the cancellation signal: " + exception.Message,
                        transition.Source,
                        transition.Destination,
                        sourceSnapshot.Files.Count,
                        sourceSnapshot.Bytes,
                        true,
                        true,
                        verification.Code);
                }
            }

            return new ModelsActivationResult(
                true,
                "MODEL_STORAGE_RECOVERED_TO_SOURCE",
                "The pending transition was recovered to the original model path. Both trees were preserved.",
                transition.Source,
                transition.Destination,
                sourceSnapshot.Files.Count,
                sourceSnapshot.Bytes,
                true,
                true,
                verification.Code);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new ModelsActivationResult(
                false,
                "MODEL_STORAGE_RECOVERY_INCOMPLETE",
                "Recovery did not reach a verified checkpoint. The marker remains and local review stays paused. " + exception.Message,
                transition.Source,
                transition.Destination,
                sourceSnapshot.Files.Count,
                sourceSnapshot.Bytes,
                true,
                false,
                null);
        }
    }

    private async Task<ModelsActivationResult> RollBackAsync(
        InstallationState state,
        ModelStorageTransition transition,
        ProtectedFileSnapshot targetRevision,
        ModelStorageTreeSnapshot sourceSnapshot,
        string? verificationCode,
        string failureMessage)
    {
        try
        {
            if (IntegrationOwnership.Inspect(_paths, state).Status != IntegrationOwnershipStatus.ManagedValid)
            {
                return RollbackIncomplete("Integration ownership changed during activation; no further mutation was attempted.");
            }

            var configured = CanonicalizeOptional(_autoStart.GetConfiguredUserModelDirectory());
            if (!string.Equals(configured, transition.Source, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(configured, transition.Destination, StringComparison.OrdinalIgnoreCase))
            {
                return RollbackIncomplete("OLLAMA_MODELS points to neither side of the transition.");
            }

            state.ModelStorageLocation = transition.Source;
            state.Availability = HelperAvailability.Paused;
            var rollbackRevision = await _stateStore.SaveIfUnchangedAsync(
                state,
                targetRevision,
                CancellationToken.None).ConfigureAwait(false);
            ThrowIfOwnershipInvalid(state);
            if (!string.Equals(
                    CanonicalizeOptional(_autoStart.GetConfiguredUserModelDirectory()),
                    configured,
                    StringComparison.OrdinalIgnoreCase))
            {
                return RollbackIncomplete("OLLAMA_MODELS changed after the rollback checkpoint; it was not overwritten.");
            }

            var rollbackVerification = await _autoStart.ApplyConfigurationAsync(
                _paths,
                state,
                configured,
                state.Preferences.AutoStartOllama,
                allowSafeRestart: true,
                OllamaRestartModelPolicy.RequireIdle,
                CancellationToken.None).ConfigureAwait(false);
            if (!ModelIntegrity.IsOperationallySafe(rollbackVerification, state))
            {
                return RollbackIncomplete(
                    $"The original path could not be runtime-verified: {rollbackVerification.Code}.");
            }

            var finalSource = await ModelStorageTreeVerifier.CaptureAsync(
                transition.Source,
                CancellationToken.None).ConfigureAwait(false);
            if (!ModelStorageTreeVerifier.Matches(sourceSnapshot, finalSource, requireMetadata: true, out var discrepancy))
            {
                return RollbackIncomplete("The source changed during rollback verification. " + discrepancy);
            }

            ThrowIfOwnershipInvalid(state);
            state.ModelStorageTransition = null;
            state.Availability = transition.PriorAvailability;
            _ = await _stateStore.SaveIfUnchangedAsync(
                state,
                rollbackRevision,
                CancellationToken.None).ConfigureAwait(false);
            if (transition.PriorAvailability == HelperAvailability.Enabled)
            {
                try
                {
                    GpuCoordination.ClearCancellation();
                }
                catch (Exception exception)
                {
                    return new ModelsActivationResult(
                        false,
                        "ACTIVATION_ROLLED_BACK_GPU_SIGNAL_UNCONFIRMED",
                        "Activation rolled back and cleared its marker, but this process could not clear the cancellation signal: " + exception.Message,
                        transition.Source,
                        transition.Destination,
                        sourceSnapshot.Files.Count,
                        sourceSnapshot.Bytes,
                        true,
                        true,
                        verificationCode);
                }
            }

            return new ModelsActivationResult(
                false,
                "ACTIVATION_ROLLED_BACK",
                "Activation failed and the original model path was restored. " + failureMessage,
                transition.Source,
                transition.Destination,
                sourceSnapshot.Files.Count,
                sourceSnapshot.Bytes,
                true,
                true,
                verificationCode);
        }
        catch (Exception rollbackException) when (rollbackException is not OutOfMemoryException and not StackOverflowException)
        {
            return RollbackIncomplete(rollbackException.Message);
        }

        ModelsActivationResult RollbackIncomplete(string reason)
            => new(
                false,
                "ACTIVATION_ROLLBACK_INCOMPLETE",
                "Activation failed and automatic rollback could not be proven complete. The helper remains paused with a recovery marker. " + reason,
                transition.Source,
                transition.Destination,
                sourceSnapshot.Files.Count,
                sourceSnapshot.Bytes,
                true,
                false,
                verificationCode);
    }

    private void ThrowIfOwnershipInvalid(InstallationState state)
    {
        if (IntegrationOwnership.Inspect(_paths, state).Status != IntegrationOwnershipStatus.ManagedValid)
        {
            throw new InvalidOperationException("Integration ownership changed during model storage activation.");
        }
    }

    private static ModelsActivationResult Failure(
        string code,
        string message,
        string source,
        string destination,
        ModelStorageTreeSnapshot? snapshot = null)
        => new(
            false,
            code,
            message,
            source,
            destination,
            snapshot?.Files.Count ?? 0,
            snapshot?.Bytes ?? 0,
            true,
            false,
            null);

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

    private static void ValidateFixedLocalDirectory(string path)
    {
        var root = Path.GetPathRoot(path)
            ?? throw new InvalidOperationException("The destination has no drive root.");
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
        {
            throw new InvalidOperationException("Existing model storage may be activated only on ready fixed local storage.");
        }
    }

    private static bool PathsOverlap(string first, string second)
    {
        var a = first.TrimEnd('\\') + "\\";
        var b = second.TrimEnd('\\') + "\\";
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string? CanonicalizeOptional(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
