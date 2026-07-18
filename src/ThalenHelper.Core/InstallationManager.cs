namespace ThalenHelper.Core;

public sealed record InstallationOptions(
    ProductPaths Paths,
    string? RequestedModel = null,
    string? RequestedModelDirectory = null,
    bool AllowCpuFallback = false,
    bool AcceptRestrictedModelLicense = false,
    bool AutoStartOllama = true,
    bool PullAndValidateModel = false,
    bool RestartOllamaForModelPath = true,
    Func<string, bool>? CodexStartupValidator = null,
    bool InstallReliabilityBaseline = false,
    string? ExpectedAgentsSourceSha256 = null,
    string? ExpectedAgentsPlannedSha256 = null,
    Func<CancellationToken, Task>? EnsureOllamaInstalledAsync = null,
    bool DeferModelSelection = false,
    bool AllowAutomaticModelFallback = true);

public sealed record InstallationOutcome(
    bool Success,
    string Code,
    string Message,
    HardwareProfile Hardware,
    ModelRecommendation Recommendation,
    StorageRecommendation? Storage,
    InstallationState State,
    ManagedFileResult CodexConfig,
    ManagedFileResult AgentsOverride,
    OllamaStartupVerification? OllamaStartup,
    IReadOnlyList<string> Warnings);

public sealed record ModelValidationResult(
    bool Success,
    string Code,
    string Message,
    AccelerationResult? Acceleration,
    long ExactResponseMilliseconds,
    long CodeReviewMilliseconds);

public sealed class InstallationManager
{
    private readonly Func<HardwareProfile> _hardwareProvider;
    private readonly ModelCatalogService _catalogService;
    private readonly ModelSelector _modelSelector;
    private readonly StorageSelector _storageSelector;
    private readonly CodexConfigManager _codexConfig;
    private readonly AgentsOverrideManager _agentsOverride;
    private readonly OllamaAutoStartManager _autoStart;
    private readonly IOllamaStartupPlatform? _startupPlatform;
    private readonly Func<string, string?> _processEnvironmentReader;
    private readonly Func<OllamaClient> _clientFactory;
    private readonly Func<InstallationState, bool, ResourcePressureCheck> _resourcePressureValidator;
    private readonly Func<StateStore, InstallationState, CancellationToken, Task>? _stateSaver;
    private readonly Action<ProductPaths> _installContextSaver;
    private readonly Func<ProductPaths?, ModelValidationStore> _validationStoreProvider;
    private readonly Func<ProductPaths?, ActiveModelTracker> _activeModelTrackerProvider;

    public InstallationManager(
        HardwareDetector? hardwareDetector = null,
        ModelCatalogService? catalogService = null,
        ModelSelector? modelSelector = null,
        StorageSelector? storageSelector = null,
        CodexConfigManager? codexConfig = null,
        AgentsOverrideManager? agentsOverride = null,
        OllamaAutoStartManager? autoStart = null,
        Func<OllamaClient>? clientFactory = null,
        Func<HardwareProfile>? hardwareProvider = null,
        Func<InstallationState, bool, ResourcePressureCheck>? resourcePressureValidator = null,
        Func<StateStore, InstallationState, CancellationToken, Task>? stateSaver = null,
        Func<ProductPaths?, ModelValidationStore>? validationStoreProvider = null,
        Action<ProductPaths>? installContextSaver = null,
        Func<ProductPaths?, ActiveModelTracker>? activeModelTrackerProvider = null,
        IOllamaStartupPlatform? startupPlatform = null,
        Func<string, string?>? processEnvironmentReader = null)
    {
        _hardwareProvider = hardwareProvider ?? (hardwareDetector ?? new HardwareDetector()).Detect;
        _catalogService = catalogService ?? new ModelCatalogService();
        _modelSelector = modelSelector ?? new ModelSelector();
        _storageSelector = storageSelector ?? new StorageSelector();
        _codexConfig = codexConfig ?? new CodexConfigManager();
        _agentsOverride = agentsOverride ?? new AgentsOverrideManager();
        _clientFactory = clientFactory ?? (() => new OllamaClient());
        if (autoStart is null)
        {
            _startupPlatform = startupPlatform ?? new WindowsOllamaStartupPlatform();
            _autoStart = new OllamaAutoStartManager(_clientFactory, _startupPlatform);
        }
        else
        {
            _autoStart = autoStart;
            _startupPlatform = startupPlatform ?? autoStart.Platform;
        }

        _processEnvironmentReader = processEnvironmentReader
            ?? (name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process));
        var pressureGuard = new ResourcePressureGuard();
        _resourcePressureValidator = resourcePressureValidator ?? pressureGuard.Check;
        _stateSaver = stateSaver;
        _installContextSaver = installContextSaver ?? InstallContextStore.Save;
        var isolatedTestDirectory = Path.Combine(
            Path.GetTempPath(),
            "CodexGpuThalenHelper",
            "validation-tests",
            Guid.NewGuid().ToString("N"));
        _validationStoreProvider = validationStoreProvider
            ?? (paths => new ModelValidationStore(paths?.StateDirectory ?? isolatedTestDirectory));
        _activeModelTrackerProvider = activeModelTrackerProvider
            ?? (paths => new ActiveModelTracker(paths?.StateDirectory ?? isolatedTestDirectory));
    }

    public async Task<RepairDryRunResult> PreviewRepairAsync(
        ProductPaths paths,
        string diffOutputPath,
        bool migrateExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(diffOutputPath))
        {
            throw new ArgumentException("An explicit --diff-out local file is required for repair dry-run.", nameof(diffOutputPath));
        }

        var diffPath = Path.GetFullPath(diffOutputPath);
        if (new Uri(diffPath).IsUnc || !IsFixedLocalPath(diffPath))
        {
            throw new InvalidOperationException(
                "Repair dry-run diff output must be on a fixed local drive, not a network or removable path.");
        }

        ValidateDiffOutputPath(diffPath);

        if (new[] { paths.CodexConfigFile, paths.AgentsOverrideFile, paths.StateFile }
            .Any(path => string.Equals(Path.GetFullPath(path), diffPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Repair dry-run diff output cannot overwrite a protected file or installation state.");
        }

        var store = new StateStore(paths.StateFile);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No installation state was found.");
        ValidateCodexHomeRoute(paths, state, "Repair dry-run");
        ValidateRepairOwnership(paths, state, allowManagedDrift: true);

        var config = _codexConfig.PreviewInstall(
            paths,
            state.Availability == HelperAvailability.Enabled,
            migrateExisting);
        var installLocalGpuGuidance = !config.ExistingIntegrationPreserved;
        var agents = _agentsOverride.PreviewInstall(
            paths,
            state.HardwareTier,
            state.ReliabilityBaselineInstalled,
            installLocalGpuGuidance,
            forceManagedLocalGpuGuidance: config.ExistingIntegrationMigrated || migrateExisting);

        var directory = Path.GetDirectoryName(diffPath)
            ?? throw new InvalidOperationException("Repair dry-run diff output has no local directory.");
        Directory.CreateDirectory(directory);
        var combinedDiff = new System.Text.StringBuilder()
            .AppendLine("# Codex GPU Thalen Helper protected repair preview")
            .AppendLine("# Apply only with all four source/planned SHA-256 values shown in the JSON summary.")
            .AppendLine()
            .Append(config.Diff)
            .AppendLine()
            .Append(agents.Diff)
            .ToString();
        var diffCreated = false;
        try
        {
            await using var diffStream = new FileStream(
                diffPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            diffCreated = true;
            await using var writer = new StreamWriter(
                diffStream,
                new System.Text.UTF8Encoding(false),
                bufferSize: 4096,
                leaveOpen: true);
            await writer.WriteAsync(combinedDiff.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            diffStream.Flush(flushToDisk: true);
        }
        catch
        {
            if (diffCreated)
            {
                File.Delete(diffPath);
            }

            throw;
        }

        return new RepairDryRunResult(
            true,
            "REPAIR_DRY_RUN_READY",
            "Protected files and state were not modified. Review the explicit diff, then apply with all four hashes.",
            new ProtectedFilePlanSummary(
                paths.CodexConfigFile,
                config.Changed,
                config.Action,
                config.SourceSha256,
                config.PlannedSha256),
            new ProtectedFilePlanSummary(
                paths.AgentsOverrideFile,
                agents.Changed,
                agents.Action,
                agents.SourceSha256,
                agents.PlannedSha256),
            diffPath);
    }

    public async Task<InstallationOutcome> ConfigureAsync(
        InstallationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.InstallReliabilityBaseline
            && (options.ExpectedAgentsSourceSha256 is null || options.ExpectedAgentsPlannedSha256 is null))
        {
            throw new InvalidOperationException(
                "The optional reliability baseline requires a reviewed before/after diff with matching source and plan hashes.");
        }

        if ((options.ExpectedAgentsSourceSha256 is null) != (options.ExpectedAgentsPlannedSha256 is null))
        {
            throw new InvalidOperationException("Both AGENTS.override.md preview hashes are required together.");
        }

        var store = new StateStore(options.Paths.StateFile);
        var priorState = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        ValidateCodexHomeRoute(options.Paths, priorState, "Configuration");
        var preservePriorSelection = options.DeferModelSelection
            && priorState?.SelectedModel is not null;

        InstallContextStore.Save(options.Paths);

        var existingIntegration = _codexConfig.InspectExistingUnmanagedIntegration(options.Paths);
        var preserveExistingIntegration = existingIntegration.ShouldPreserve;
        var currentUserModelDirectory = _startupPlatform!.GetUserEnvironmentVariable("OLLAMA_MODELS");
        var hardware = _hardwareProvider();
        var catalog = _catalogService.LoadBundled();
        var recommendation = _modelSelector.Recommend(hardware, catalog, options.AllowCpuFallback);
        var selected = preserveExistingIntegration || options.DeferModelSelection
            ? null
            : SelectRequestedModel(options, catalog, recommendation, hardware);
        StorageRecommendation? storage = null;
        if (selected is not null)
        {
            storage = options.RequestedModelDirectory is null
                ? _storageSelector.Recommend(hardware, selected)
                : ValidateCustomStorage(hardware, selected, options.RequestedModelDirectory);
            if (storage.Volume is null || string.IsNullOrWhiteSpace(storage.ModelDirectory))
            {
                throw new InvalidOperationException(storage.Explanation);
            }

            storage = ValidateLiveStorageDestination(storage, selected);
        }

        var hasAgentsPreviewBinding = options.ExpectedAgentsSourceSha256 is not null;
        var installReliabilityBaseline = hasAgentsPreviewBinding
            ? options.InstallReliabilityBaseline
            : priorState?.ReliabilityBaselineInstalled ?? false;
        var priorValidatedSelection = preservePriorSelection
            ? priorState?.Availability == HelperAvailability.Enabled
            : priorState?.Availability == HelperAvailability.Enabled
                && string.Equals(priorState.SelectedModel, selected?.Tag, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    priorState.ModelStorageLocation is null ? null : Path.GetFullPath(priorState.ModelStorageLocation),
                    storage?.ModelDirectory is null ? null : Path.GetFullPath(storage.ModelDirectory),
                    StringComparison.OrdinalIgnoreCase);
        var hardwareTier = preserveExistingIntegration && recommendation.Model is not null
            ? ModelSelector.GetHardwareTier(recommendation.Model)
            : preservePriorSelection
                ? priorState!.HardwareTier
            : selected is null
                ? HardwareTier.NoModel
                : ModelSelector.GetHardwareTier(selected);
        var state = new InstallationState
        {
            InstalledAt = priorState?.InstalledAt ?? DateTimeOffset.UtcNow,
            FilesCreated = priorState?.FilesCreated.ToList() ?? [],
            FilesModified = priorState?.FilesModified.ToList() ?? [],
            BackupLocations = priorState is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(priorState.BackupLocations, StringComparer.OrdinalIgnoreCase),
            ManagedConfigurationSections = priorState?.ManagedConfigurationSections.ToList() ?? [],
            PreviousUserEnvironment = priorState is null
                ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string?>(priorState.PreviousUserEnvironment, StringComparer.OrdinalIgnoreCase),
            ExistingIntegrationPreserved = priorState?.ExistingIntegrationPreserved ?? false,
            ReliabilityBaselineInstalled = installReliabilityBaseline,
            SelectedModel = preservePriorSelection ? priorState!.SelectedModel : selected?.Tag,
            SelectedModelProvider = preservePriorSelection
                ? priorState!.SelectedModelProvider
                : selected is null
                    ? ModelProviders.Ollama
                    : ModelProviders.Normalize(selected.Provider),
            SelectedModelDigest = preservePriorSelection ? priorState!.SelectedModelDigest : selected?.ExpectedDigest,
            RegisteredLocalModels = priorState?.RegisteredLocalModels.ToList() ?? [],
            SelectedModelOwnedByHelper = priorValidatedSelection && priorState?.SelectedModelOwnedByHelper == true,
            ModelStorageLocation = preservePriorSelection ? priorState!.ModelStorageLocation : storage?.ModelDirectory,
            ModelStorageTransition = priorState?.ModelStorageTransition,
            ManagedCodexHome = options.Paths.CodexHome,
            HardwareTier = hardwareTier,
            Acceleration = priorValidatedSelection ? priorState?.Acceleration : null,
            Availability = priorValidatedSelection ? HelperAvailability.Enabled : HelperAvailability.Disabled,
            LastHealthCheckAt = priorValidatedSelection ? priorState?.LastHealthCheckAt : null,
            LastHealthCheckCode = priorValidatedSelection ? priorState?.LastHealthCheckCode : null,
            Preferences = priorState is null
                ? new HelperPreferences(
                    LowImpactMode: recommendation.LowImpactMode,
                    KeepWarm: false,
                    AutoStartOllama: options.AutoStartOllama,
                    IdleUnloadSeconds: 0,
                    ModelSelectionMode: ModelSelectionMode.Automatic)
                : priorState.Preferences with { AutoStartOllama = options.AutoStartOllama }
        };
        if (priorState is null)
        {
            state.PreviousUserEnvironment["OLLAMA_MODELS"] = Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User);
            state.PreviousUserEnvironment["OLLAMA_HOST"] = Environment.GetEnvironmentVariable("OLLAMA_HOST", EnvironmentVariableTarget.User);
        }

        state.OllamaWasPreExisting = priorState?.OllamaWasPreExisting ?? IsOllamaPresent();
        Directory.CreateDirectory(options.Paths.StateDirectory);
        if (options.ExpectedAgentsSourceSha256 is not null || options.ExpectedAgentsPlannedSha256 is not null)
        {
            var previewCheck = _agentsOverride.PreviewInstall(
                options.Paths,
                state.HardwareTier,
                installReliabilityBaseline,
                installLocalGpuGuidance: !preserveExistingIntegration);
            if (!string.Equals(
                    previewCheck.SourceSha256,
                    options.ExpectedAgentsSourceSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    previewCheck.PlannedSha256,
                    options.ExpectedAgentsPlannedSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "AGENTS.override.md or its managed plan changed after preview. Review a fresh before/after diff before setup applies any configuration.");
            }
        }

        var configResult = preserveExistingIntegration
            ? _codexConfig.PreserveExistingUnmanagedIntegration(options.Paths, existingIntegration)
            : _codexConfig.InstallOrRepair(
                options.Paths,
                state.Availability == HelperAvailability.Enabled,
                options.CodexStartupValidator);
        state.ExistingIntegrationPreserved = IsExistingIntegrationPreserved(configResult);
        if (state.ExistingIntegrationPreserved)
        {
            state.SelectedModel = null;
            state.SelectedModelDigest = null;
            state.SelectedModelOwnedByHelper = false;
            state.ModelStorageLocation = null;
            state.Acceleration = null;
        }

        ManagedFileResult agentsResult;
        try
        {
            agentsResult = _agentsOverride.InstallOrRepair(
                options.Paths,
                state.HardwareTier,
                installReliabilityBaseline,
                options.ExpectedAgentsSourceSha256,
                options.ExpectedAgentsPlannedSha256,
                installLocalGpuGuidance: !state.ExistingIntegrationPreserved);
        }
        catch
        {
            _codexConfig.Rollback(configResult);
            throw;
        }
        TrackManagedFile(state, configResult);
        TrackManagedFile(state, agentsResult);
        SetManagedSection(state, "mcp_servers.local_gpu_reviewer", !state.ExistingIntegrationPreserved);
        var agentsContent = File.Exists(options.Paths.AgentsOverrideFile)
            ? File.ReadAllText(options.Paths.AgentsOverrideFile)
            : string.Empty;
        SetManagedSection(
            state,
            "AGENTS.override.md local GPU section",
            AgentsOverrideManager.HasManagedLocalGpuSection(agentsContent));
        state.ReliabilityBaselineInstalled = AgentsOverrideManager.HasManagedReliabilitySection(agentsContent);
        SetManagedSection(
            state,
            "AGENTS.override.md reliability baseline",
            state.ReliabilityBaselineInstalled);

        var warnings = new List<string>();
        warnings.AddRange(hardware.Warnings);
        warnings.AddRange(recommendation.Warnings);
        if (storage is not null)
        {
            warnings.AddRange(storage.Warnings);
        }

        AddWarning(warnings, configResult.Warning);
        AddWarning(warnings, agentsResult.Warning);
        if (state.ExistingIntegrationPreserved)
        {
            state.Availability = HelperAvailability.Disabled;
            state.LastHealthCheckAt = DateTimeOffset.UtcNow;
            state.LastHealthCheckCode = "EXISTING_INTEGRATION_PRESERVED";
            state.SelectedModelOwnedByHelper = false;
            state.StartupEntryOwnedByHelper = false;
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new InstallationOutcome(
                true,
                "EXISTING_INTEGRATION_PRESERVED",
                "An existing unmarked local_gpu_reviewer entry was detected and preserved unchanged. It was not inspected, tested, activated, or given helper invocation guidance. Ollama, models, startup, and environment were not changed.",
                hardware,
                recommendation,
                null,
                state,
                configResult,
                agentsResult,
                null,
                warnings);
        }

        await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        if (selected is not null && options.EnsureOllamaInstalledAsync is not null)
        {
            await options.EnsureOllamaInstalledAsync(cancellationToken).ConfigureAwait(false);
        }

        OllamaStartupVerification? startup = null;
        if (selected is not null && state.ModelStorageLocation is not null)
        {
            ThrowIfOwnershipInvalid(options.Paths, state);
            using (var storageLease = ModelStoragePathLease.AcquireOrCreate(state.ModelStorageLocation))
            {
                startup = await _autoStart.ApplyConfigurationAsync(
                    options.Paths,
                    state,
                    currentUserModelDirectory,
                    options.AutoStartOllama,
                    options.RestartOllamaForModelPath,
                    cancellationToken).ConfigureAwait(false);
                storageLease.ValidateUnchanged();
            }
        }

        if (!options.AutoStartOllama)
        {
            warnings.Add("Automatic Ollama startup was declined. Local review requires manually starting Ollama after each sign-in.");
        }

        if (selected is null)
        {
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new InstallationOutcome(
                true,
                options.DeferModelSelection ? "INSTALLED_MODEL_SETUP_REQUIRED" : "INSTALLED_NO_MODEL",
                options.DeferModelSelection
                    ? "The protected Codex integration and local GPU guidance were installed in disabled mode. Model selection was intentionally deferred; no model was downloaded or loaded."
                    : "The helper was installed in disabled/no-model mode because no safe model fit was available.",
                hardware,
                recommendation,
                storage,
                state,
                configResult,
                agentsResult,
                startup,
                warnings);
        }

        if (!options.PullAndValidateModel)
        {
            var currentSelectionVerified = priorValidatedSelection
                && startup is not null
                && ModelIntegrity.IsOperationallySafe(startup, state);
            state.Availability = currentSelectionVerified
                ? HelperAvailability.Enabled
                : HelperAvailability.Disabled;
            state.LastHealthCheckAt = DateTimeOffset.UtcNow;
            state.LastHealthCheckCode = startup?.Code ?? "OLLAMA_STARTUP_UNVERIFIED";
            if (!currentSelectionVerified)
            {
                TrackManagedFile(state, _codexConfig.SetEnabled(options.Paths, false));
                GpuCoordination.RequestCancellation();
            }

            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new InstallationOutcome(
                true,
                currentSelectionVerified ? "RECONFIGURED_VALIDATION_PRESERVED" : "CONFIGURED_VALIDATION_REQUIRED",
                currentSelectionVerified
                    ? "The helper was reconfigured idempotently and its previously validated model remains enabled."
                    : "The helper is configured but disabled until its selected model and current loopback runtime pass passive verification.",
                hardware,
                recommendation,
                storage,
                state,
                configResult,
                agentsResult,
                startup,
                warnings);
        }

        if (startup is null
            || !startup.EndpointReachable
            || !startup.ModelStorageConfigured
            || !startup.LoopbackOnly
            || startup.Code is "OLLAMA_RESTART_REQUIRED" or "OLLAMA_RESTART_UNLOAD_FAILED" or "OLLAMA_RESTART_FAILED" or "EXTERNAL_AUTOSTART_UNVERIFIED")
        {
            state.Availability = HelperAvailability.Disabled;
            state.LastHealthCheckAt = DateTimeOffset.UtcNow;
            state.LastHealthCheckCode = startup?.Code ?? "OLLAMA_STARTUP_UNVERIFIED";
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new InstallationOutcome(
                false,
                "OLLAMA_CONFIGURATION_FAILED",
                startup?.Code == "EXTERNAL_AUTOSTART_UNVERIFIED"
                    ? "An existing Ollama startup artifact was preserved to avoid creating a duplicate, but its target and next-login behavior were not verified. Review or remove it, or choose manual startup. The helper remains disabled."
                    : "Ollama did not safely inherit and verify the configured loopback model directory. The helper remains disabled.",
                hardware,
                recommendation,
                storage,
                state,
                configResult,
                agentsResult,
                startup,
                warnings);
        }

        var validation = await PullAndValidateAsync(options.Paths, selected, state, cancellationToken).ConfigureAwait(false);
        var fallbackAttempted = false;
        if (!validation.Success && options.AllowAutomaticModelFallback)
        {
            var fallback = catalog.Models
                .Where(model => model.AutomaticSelectionAllowed
                    && model.CommercialUseAllowed
                    && model.ParameterBillions < selected.ParameterBillions)
                .OrderByDescending(model => model.ParameterBillions)
                .FirstOrDefault();
            if (fallback is not null)
            {
                fallbackAttempted = true;
                warnings.Add($"{selected.Tag} failed validation with {validation.Code}. One bounded downgrade to {fallback.Tag} was attempted.");
                state.SelectedModel = fallback.Tag;
                state.SelectedModelDigest = fallback.ExpectedDigest;
                validation = await PullAndValidateAsync(options.Paths, fallback, state, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!validation.Success)
        {
            state.Availability = HelperAvailability.Disabled;
            state.LastHealthCheckAt = DateTimeOffset.UtcNow;
            state.LastHealthCheckCode = validation.Code;
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new InstallationOutcome(
                false,
                "MODEL_VALIDATION_FAILED",
                options.AllowAutomaticModelFallback
                    ? fallbackAttempted
                        ? "The selected model and one safe fallback failed validation. The helper remains installed but disabled."
                        : "The selected model failed validation and no eligible fallback was available. The helper remains installed but disabled."
                    : "The selected model failed validation. No fallback model was attempted. The helper remains installed but disabled.",
                hardware,
                recommendation,
                storage,
                state,
                configResult,
                agentsResult,
                startup,
                warnings);
        }

        state.Acceleration = validation.Acceleration;
        ThrowIfOwnershipInvalid(options.Paths, state);
        startup = await _autoStart.VerifyAsync(options.Paths, state, startup.StartedNewProcess, cancellationToken).ConfigureAwait(false);
        if (!ModelIntegrity.IsOperationallySafe(startup, state))
        {
            state.Availability = HelperAvailability.Disabled;
            state.LastHealthCheckAt = DateTimeOffset.UtcNow;
            state.LastHealthCheckCode = startup.Code;
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new InstallationOutcome(
                false,
                "MODEL_PATH_VERIFICATION_FAILED",
                "The model test passed, but the selected model could not be proven to reside in the configured loopback Ollama model directory. The helper remains disabled.",
                hardware,
                recommendation,
                storage,
                state,
                configResult,
                agentsResult,
                startup,
                warnings);
        }

        var enabledConfig = _codexConfig.SetEnabled(options.Paths, true);
        var previousAvailability = state.Availability;
        var previousHealthCheckAt = state.LastHealthCheckAt;
        var previousHealthCheckCode = state.LastHealthCheckCode;
        state.Availability = HelperAvailability.Enabled;
        state.LastHealthCheckAt = DateTimeOffset.UtcNow;
        state.LastHealthCheckCode = "OK";
        TrackManagedFile(state, enabledConfig);
        try
        {
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            state.Availability = previousAvailability;
            state.LastHealthCheckAt = previousHealthCheckAt;
            state.LastHealthCheckCode = previousHealthCheckCode;
            _codexConfig.Rollback(enabledConfig);
            throw;
        }

        GpuCoordination.ClearCancellation();
        return new InstallationOutcome(
            true,
            "INSTALLED_AND_VALIDATED",
            "The helper, selected model, loopback Ollama endpoint, and bounded reviewer tests passed.",
            hardware,
            recommendation,
            storage,
            state,
            configResult,
            agentsResult,
            startup,
            warnings);
    }

    public async Task<InstallationOutcome> RepairAsync(
        ProductPaths paths,
        Func<string, bool>? codexStartupValidator = null,
        CancellationToken cancellationToken = default,
        RepairHashBinding? binding = null,
        bool migrateExisting = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var store = new StateStore(paths.StateFile);
        var loadedState = await store.LoadWithRevisionAsync(cancellationToken).ConfigureAwait(false);
        var state = loadedState.State
            ?? throw new InvalidOperationException("No installation state was found.");
        var originalState = loadedState.Revision;
        ValidateCodexHomeRoute(paths, state, "Repair");

        if (state.ModelStorageTransition is not null)
        {
            throw new InvalidOperationException(
                "A model storage transition is pending. Run models recover --yes before ordinary repair.");
        }

        if (migrateExisting && binding is null)
        {
            throw new InvalidOperationException(
                "--migrate-existing requires a reviewed dry-run and all four source/planned hashes.");
        }


        ValidateRepairOwnership(paths, state, allowManagedDrift: binding is not null);
        var configPreview = _codexConfig.PreviewInstall(
            paths,
            state.Availability == HelperAvailability.Enabled,
            migrateExisting);
        var installLocalGpuGuidance = !configPreview.ExistingIntegrationPreserved;
        var agentsPreview = _agentsOverride.PreviewInstall(
            paths,
            state.HardwareTier,
            state.ReliabilityBaselineInstalled,
            installLocalGpuGuidance,
            forceManagedLocalGpuGuidance: configPreview.ExistingIntegrationMigrated || migrateExisting);
        ValidateRepairBinding(binding, configPreview, agentsPreview);
        if (binding is null && (configPreview.Changed || agentsPreview.Changed))
        {
            throw new InvalidOperationException(
                "Repair would change a protected file. Run repair --dry-run, review the diff, and supply all four source/planned hashes.");
        }

        if (state.ModelStorageLocation is not null && _startupPlatform is null)
        {
            throw new InvalidOperationException(
                "Repair cannot safely update Ollama startup because the injected startup manager did not expose its platform for guarded rollback.");
        }

        var hardware = _hardwareProvider();
        var catalog = _catalogService.LoadBundled();
        var recommendation = _modelSelector.Recommend(hardware, catalog, state.Acceleration?.Processor == "CPU");
        ProtectedFileSnapshot? stateWrittenByRepair = null;
        ManagedFileResult? config = null;
        ManagedFileResult? agents = null;
        StartupExternalState? startupBeforeRepair = null;
        StartupExternalState? startupWrittenByRepair = null;
        var startupMutationAttempted = false;
        try
        {
            config = _codexConfig.InstallOrRepair(
                paths,
                state.Availability == HelperAvailability.Enabled,
                codexStartupValidator,
                binding?.ConfigSourceSha256,
                binding?.ConfigPlannedSha256,
                migrateExisting);
            state.ExistingIntegrationPreserved = IsExistingIntegrationPreserved(config);
            if (state.ExistingIntegrationPreserved)
            {
                state.SelectedModel = null;
                state.SelectedModelDigest = null;
                state.SelectedModelOwnedByHelper = false;
                state.ModelStorageLocation = null;
                state.Acceleration = null;
            }

            agents = _agentsOverride.InstallOrRepair(
                paths,
                state.HardwareTier,
                state.ReliabilityBaselineInstalled,
                binding?.AgentsSourceSha256,
                binding?.AgentsPlannedSha256,
                installLocalGpuGuidance: !state.ExistingIntegrationPreserved,
                forceManagedLocalGpuGuidance: configPreview.ExistingIntegrationMigrated || migrateExisting);
            TrackManagedFile(state, config);
            TrackManagedFile(state, agents);
            SetManagedSection(state, "mcp_servers.local_gpu_reviewer", !state.ExistingIntegrationPreserved);
            var agentsContent = File.Exists(paths.AgentsOverrideFile)
                ? File.ReadAllText(paths.AgentsOverrideFile)
                : string.Empty;
            SetManagedSection(
                state,
                "AGENTS.override.md local GPU section",
                AgentsOverrideManager.HasManagedLocalGpuSection(agentsContent));
            state.ReliabilityBaselineInstalled = AgentsOverrideManager.HasManagedReliabilitySection(agentsContent);
            SetManagedSection(
                state,
                "AGENTS.override.md reliability baseline",
                state.ReliabilityBaselineInstalled);
            var repairWarnings = hardware.Warnings.ToList();
            AddWarning(repairWarnings, config.Warning);
            AddWarning(repairWarnings, agents.Warning);
            if (state.ExistingIntegrationPreserved)
            {
                state.Availability = HelperAvailability.Disabled;
                state.LastHealthCheckAt = DateTimeOffset.UtcNow;
                state.LastHealthCheckCode = "EXISTING_INTEGRATION_PRESERVED";
                state.SelectedModelOwnedByHelper = false;
                state.StartupEntryOwnedByHelper = false;
                state.ProductVersion = ProductInfo.Version;
                stateWrittenByRepair = await SaveRepairStateAsync(
                    store,
                    state,
                    originalState,
                    cancellationToken).ConfigureAwait(false);
                _installContextSaver(paths);
                return new InstallationOutcome(
                    true,
                    "EXISTING_INTEGRATION_PRESERVED",
                    "Repair preserved the existing unmarked local_gpu_reviewer integration and did not change Ollama, models, startup, or activation.",
                    hardware,
                    recommendation,
                    null,
                    state,
                    config,
                    agents,
                    null,
                    repairWarnings);
            }

            OllamaStartupVerification? startup = null;
            if (state.ModelStorageLocation is not null)
            {
                ThrowIfOwnershipInvalid(paths, state);
                startupBeforeRepair = CaptureStartupExternalState();
                var startupPlan = _autoStart.PreviewConfiguration(
                    paths,
                    state,
                    state.Preferences.AutoStartOllama);
                startupWrittenByRepair = new StartupExternalState(
                    startupPlan.ModelDirectory,
                    startupPlan.Host,
                    startupPlan.ModelDirectory,
                    startupPlan.Host,
                    startupPlan.RunEntry);
                startupMutationAttempted = true;
                _autoStart.ApplyConfiguration(startupPlan, state);
                startup = await _autoStart.VerifyAsync(
                    paths,
                    state,
                    startedNewProcess: false,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(state.SelectedModel)
                && (startup is null || !ModelIntegrity.IsOperationallySafe(startup, state)))
            {
                GpuCoordination.RequestCancellation();
                throw new InvalidOperationException(
                    "Repair verification failed; the protected files and installation state are being restored.");
            }

            state.ProductVersion = ProductInfo.Version;
            stateWrittenByRepair = await SaveRepairStateAsync(
                store,
                state,
                originalState,
                cancellationToken).ConfigureAwait(false);
            _installContextSaver(paths);
            return new InstallationOutcome(
                true,
                "REPAIRED",
                "Managed files and per-user startup settings were repaired without replacing unrelated content.",
                hardware,
                recommendation,
                null,
                state,
                config,
                agents,
                startup,
                repairWarnings);
        }
        catch (Exception exception)
        {
            var rollbackErrors = new List<Exception>();
            if (startupMutationAttempted && startupBeforeRepair is not null)
            {
                try
                {
                    RestoreStartupExternalState(startupBeforeRepair, startupWrittenByRepair!);
                }
                catch (Exception rollback)
                {
                    rollbackErrors.Add(rollback);
                }
            }

            if (agents is not null)
            {
                try
                {
                    _agentsOverride.Rollback(agents);
                }
                catch (Exception rollback)
                {
                    rollbackErrors.Add(rollback);
                }
            }

            if (config is not null)
            {
                try
                {
                    _codexConfig.Rollback(config);
                }
                catch (Exception rollback)
                {
                    rollbackErrors.Add(rollback);
                }
            }

            try
            {
                RestoreProtectedSnapshot(paths.StateFile, originalState, stateWrittenByRepair);
            }
            catch (Exception rollback)
            {
                rollbackErrors.Add(rollback);
            }

            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    "Repair failed and one or more guarded rollback steps also failed.",
                    [exception, .. rollbackErrors]);
            }

            throw;
        }
    }

    public async Task<ManagedFileResult> ConfigureReliabilityBaselineAsync(
        ProductPaths paths,
        bool install,
        string expectedAgentsSourceSha256,
        string expectedAgentsPlannedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var store = new StateStore(paths.StateFile);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No installation state was found.");
        ValidateCodexHomeRoute(paths, state, "Reliability baseline configuration");
        InstallContextStore.Save(paths);
        var result = _agentsOverride.InstallOrRepair(
            paths,
            state.HardwareTier,
            install,
            expectedAgentsSourceSha256,
            expectedAgentsPlannedSha256,
            installLocalGpuGuidance: IntegrationOwnership.Inspect(paths, state).Status == IntegrationOwnershipStatus.ManagedValid);
        TrackManagedFile(state, result);
        var content = File.Exists(paths.AgentsOverrideFile)
            ? File.ReadAllText(paths.AgentsOverrideFile)
            : string.Empty;
        SetManagedSection(
            state,
            "AGENTS.override.md local GPU section",
            AgentsOverrideManager.HasManagedLocalGpuSection(content));
        state.ReliabilityBaselineInstalled = AgentsOverrideManager.HasManagedReliabilitySection(content);
        SetManagedSection(
            state,
            "AGENTS.override.md reliability baseline",
            state.ReliabilityBaselineInstalled);
        await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<ModelValidationResult> ValidateSelectedModelAsync(
        ProductPaths paths,
        InstallationState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(state);
        var ownership = IntegrationOwnership.Inspect(paths, state);
        if (ownership.Status != IntegrationOwnershipStatus.ManagedValid)
        {
            return Task.FromResult(new ModelValidationResult(
                false,
                ownership.Status == IntegrationOwnershipStatus.ExternalUnmarked
                    ? "EXISTING_INTEGRATION_PRESERVED"
                    : "INTEGRATION_OWNERSHIP_DRIFT",
                ownership.Message + " No model was loaded or queried.",
                null,
                0,
                0));
        }

        return ValidateSelectedModelCoreAsync(paths, state, cancellationToken);
    }

    internal Task<ModelValidationResult> ValidateSelectedModelForTestingAsync(
        InstallationState state,
        CancellationToken cancellationToken = default)
    {
        if (!IntegrationOwnership.IsManagedByHelper(state))
        {
            return Task.FromResult(new ModelValidationResult(
                false,
                "EXISTING_INTEGRATION_PRESERVED",
                "The existing unmarked local_gpu_reviewer integration is not controlled by this helper.",
                null,
                0,
                0));
        }

        return ValidateSelectedModelCoreAsync(null, state, cancellationToken);
    }

    private async Task<ModelValidationResult> ValidateSelectedModelCoreAsync(
        ProductPaths? paths,
        InstallationState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.SelectedModel))
        {
            return new ModelValidationResult(false, "NO_MODEL", "No selected model is configured.", null, 0, 0);
        }

        var exactMilliseconds = 0L;
        var reviewMilliseconds = 0L;
        var validationStore = _validationStoreProvider(paths);
        var activeModelTracker = _activeModelTrackerProvider(paths);
        try
        {
            // A prior pass must never survive a new validation attempt for the same tag.
            await validationStore.RemoveAsync(state.SelectedModel, CancellationToken.None).ConfigureAwait(false);
            ThrowIfOwnershipInvalid(paths, state);
            using var client = _clientFactory();
            ThrowIfOwnershipInvalid(paths, state);
            var models = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfOwnershipInvalid(paths, state);
            var selected = ModelIntegrity.FindSelectedModel(models, state.SelectedModel);
            if (selected is null)
            {
                return new ModelValidationResult(false, "SELECTED_MODEL_UNAVAILABLE", "The selected model is not available from Ollama.", null, 0, 0);
            }

            if (!ModelIntegrity.DigestMatches(selected.Digest, state.SelectedModelDigest))
            {
                return new ModelValidationResult(false, "MODEL_DIGEST_MISMATCH", "The selected model digest does not match the audited catalog digest.", null, 0, 0);
            }

            var normalizedDigest = ModelValidationStore.NormalizeFullDigest(selected.Digest);
            if (normalizedDigest is null)
            {
                return new ModelValidationResult(false, "MODEL_DIGEST_INVALID", "Ollama did not provide a full normalized SHA-256 model digest.", null, 0, 0);
            }

            // Promote the catalog prefix to the exact installed identity for all
            // subsequent ownership, cleanup, and uninstall decisions.
            state.SelectedModelDigest = normalizedDigest;

            async Task CheckRuntimeOwnershipAndPressureAsync(CancellationToken token)
            {
                ThrowIfOwnershipInvalid(paths, state);
                var runningModels = await client.GetRunningModelsAsync(token).ConfigureAwait(false);
                ThrowIfOwnershipInvalid(paths, state);
                var runtimeOwnership = OllamaRuntimeOwnership.Inspect(
                    runningModels,
                    activeModelTracker.Inspect(),
                    state.SelectedModel);
                if (!runtimeOwnership.Allowed)
                {
                    throw new OllamaException(runtimeOwnership.Code, runtimeOwnership.Message, retryable: true);
                }

                var pressure = _resourcePressureValidator(
                    state,
                    runtimeOwnership.SelectedModelAlreadyLoaded);
                if (!pressure.Allowed)
                {
                    throw new OllamaException(pressure.Code, pressure.Message, retryable: true);
                }

                if (!runtimeOwnership.SelectedModelAlreadyLoaded)
                {
                    activeModelTracker.Set(state.SelectedModel, normalizedDigest);
                }
            }

            using var lease = await GpuCoordination.AcquireAsync(
                ReviewBusyBehavior.Queue,
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            AccelerationResult? acceleration = null;
            void ObserveAcceleration(OllamaRunningModel loaded)
            {
                acceleration = new AccelerationResult(
                    loaded.SizeVramBytes > 0 ? "GPU or partial GPU (verify processor split with ollama ps)" : "CPU",
                    loaded.SizeVramBytes,
                    loaded.ContextLength,
                    loaded.ExpiresAt);
            }
            var unloadVerified = false;
            var generationAttempted = false;
            try
            {
                await CheckRuntimeOwnershipAndPressureAsync(cancellationToken).ConfigureAwait(false);
                var exactStart = System.Diagnostics.Stopwatch.StartNew();
                generationAttempted = true;
                var exact = await client.GenerateUnderLeaseAsync(
                    new OllamaGenerationSpec(
                        state.SelectedModel,
                        "Reply with exactly this text and nothing else: THALEN_HELPER_OK",
                        state.HardwareTier == HardwareTier.Entry ? 2_048 : 4_096,
                        64,
                        TimeSpan.Zero),
                    cancellationToken).ConfigureAwait(false);
                exactStart.Stop();
                exactMilliseconds = exactStart.ElapsedMilliseconds;
                if (!await WaitForTrackedOllamaReleaseAsync(
                        client,
                        activeModelTracker,
                        state.SelectedModel,
                        ObserveAcceleration,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new OllamaException(
                        "GPU_RELEASE_FAILED",
                        "Ollama did not confirm that the zero-keep-alive exact-response model was released.");
                }
                ThrowIfOwnershipInvalid(paths, state);
                if (!string.Equals(exact.Response.Trim(), "THALEN_HELPER_OK", StringComparison.Ordinal))
                {
                    throw new OllamaException("EXACT_RESPONSE_FAILED", "The exact-response smoke test failed.");
                }

                await CheckRuntimeOwnershipAndPressureAsync(cancellationToken).ConfigureAwait(false);
                var reviewStart = System.Diagnostics.Stopwatch.StartNew();
                generationAttempted = true;
                var review = await client.GenerateUnderLeaseAsync(
                    new OllamaGenerationSpec(
                        state.SelectedModel,
                        "Inspect only this loop: for (int i = 0; i <= items.Length; i++) { Use(items[i]); }. Reply with exactly: OFF_BY_ONE",
                        state.HardwareTier == HardwareTier.Entry ? 2_048 : 4_096,
                        64,
                        TimeSpan.Zero),
                    cancellationToken).ConfigureAwait(false);
                reviewStart.Stop();
                reviewMilliseconds = reviewStart.ElapsedMilliseconds;
                if (!await WaitForTrackedOllamaReleaseAsync(
                        client,
                        activeModelTracker,
                        state.SelectedModel,
                        ObserveAcceleration,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new OllamaException(
                        "GPU_RELEASE_FAILED",
                        "Ollama did not confirm that the zero-keep-alive code-review model was released.");
                }
                ThrowIfOwnershipInvalid(paths, state);
                if (!string.Equals(review.Response.Trim(), "OFF_BY_ONE", StringComparison.Ordinal))
                {
                    throw new OllamaException("CODE_REVIEW_SMOKE_FAILED", "The bounded code-review smoke test failed.");
                }

                ThrowIfOwnershipInvalid(paths, state);
            }
            finally
            {
                using var unloadTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    unloadVerified = generationAttempted
                        && await WaitForTrackedOllamaReleaseAsync(
                            client,
                            activeModelTracker,
                            state.SelectedModel,
                            ObserveAcceleration,
                            unloadTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OllamaException or OperationCanceledException)
                {
                    unloadVerified = false;
                }
            }

            if (!unloadVerified)
            {
                return new ModelValidationResult(
                    false,
                    "GPU_RELEASE_FAILED",
                    "Ollama did not confirm that the zero-keep-alive validation model was released.",
                    acceleration,
                    exactMilliseconds,
                    reviewMilliseconds);
            }

            await validationStore.UpsertAsync(new ModelValidationEntry(
                state.SelectedModel,
                normalizedDigest,
                ModelValidationStore.CurrentProtocolVersion,
                DateTimeOffset.UtcNow,
                exactMilliseconds,
                reviewMilliseconds,
                acceleration?.Processor ?? "Unknown",
                acceleration?.SizeVramBytes,
                acceleration?.ContextLength), CancellationToken.None).ConfigureAwait(false);
            return new ModelValidationResult(true, "OK", "Exact response, bounded review, runtime inspection, and zero-keep-alive release passed.", acceleration, exactMilliseconds, reviewMilliseconds);
        }
        catch (OllamaException exception)
        {
            return new ModelValidationResult(false, exception.Code, exception.Message, null, exactMilliseconds, reviewMilliseconds);
        }
        catch (ModelValidationStateException exception)
        {
            return new ModelValidationResult(false, exception.Code, exception.Message, null, exactMilliseconds, reviewMilliseconds);
        }
    }

    private static async Task<bool> WaitForTrackedOllamaReleaseAsync(
        OllamaClient client,
        ActiveModelTracker tracker,
        string model,
        Action<OllamaRunningModel>? observe,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var running = await client.GetRunningModelsAsync(timeout.Token).ConfigureAwait(false);
                var inspection = tracker.Inspect();
                if (running.Count == 0)
                {
                    if (inspection.Status == ActiveModelTrackerStatus.Absent)
                    {
                        return true;
                    }

                    if (!OllamaRuntimeOwnership.MatchesTrackedOllamaModel(inspection, model))
                    {
                        return false;
                    }

                    tracker.Clear(model);
                    return true;
                }

                var ownership = OllamaRuntimeOwnership.Inspect(running, inspection, model);
                if (!ownership.Allowed || !ownership.SelectedModelAlreadyLoaded)
                {
                    return false;
                }
                observe?.Invoke(running[0]);

                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return false;
        }

        return false;
    }

    private async Task<ModelValidationResult> PullAndValidateAsync(
        ProductPaths paths,
        ModelCatalogEntry model,
        InstallationState state,
        CancellationToken cancellationToken)
    {
        try
        {
            ThrowIfOwnershipInvalid(paths, state);
            using var storageLease = ModelStoragePathLease.AcquireExisting(
                state.ModelStorageLocation
                ?? throw new InvalidOperationException("The Ollama model-storage directory is not configured."));
            using var client = _clientFactory();
            ThrowIfOwnershipInvalid(paths, state);
            var existing = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            storageLease.ValidateUnchanged();
            ThrowIfOwnershipInvalid(paths, state);
            var alreadyPresent = existing.Any(item => string.Equals(item.Name, model.Tag, StringComparison.OrdinalIgnoreCase));
            if (!alreadyPresent)
            {
                ThrowIfOwnershipInvalid(paths, state);
                storageLease.ValidateUnchanged();
                await client.PullAsync(model.Tag, cancellationToken).ConfigureAwait(false);
                storageLease.ValidateUnchanged();
                ThrowIfOwnershipInvalid(paths, state);
                state.SelectedModelOwnedByHelper = true;
            }
            else
            {
                state.SelectedModelOwnedByHelper = false;
            }

            state.SelectedModel = model.Tag;
            state.SelectedModelDigest = model.ExpectedDigest;
            ThrowIfOwnershipInvalid(paths, state);
            var inventory = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            storageLease.ValidateUnchanged();
            ThrowIfOwnershipInvalid(paths, state);
            var installed = ModelIntegrity.FindSelectedModel(inventory, model.Tag);
            if (installed is null)
            {
                return new ModelValidationResult(false, "SELECTED_MODEL_UNAVAILABLE", "Ollama did not return the selected model after pull.", null, 0, 0);
            }

            if (!ModelIntegrity.DigestMatches(installed.Digest, model.ExpectedDigest))
            {
                return new ModelValidationResult(false, "MODEL_DIGEST_MISMATCH", "The selected model digest does not match the audited catalog digest.", null, 0, 0);
            }

            return await ValidateSelectedModelAsync(paths, state, cancellationToken).ConfigureAwait(false);
        }
        catch (OllamaException exception)
        {
            return new ModelValidationResult(false, exception.Code, exception.Message, null, 0, 0);
        }
    }

    private static ModelCatalogEntry? SelectRequestedModel(
        InstallationOptions options,
        ModelManifest catalog,
        ModelRecommendation recommendation,
        HardwareProfile hardware)
    {
        if (string.IsNullOrWhiteSpace(options.RequestedModel))
        {
            if (recommendation.Model is not null
                && string.Equals(recommendation.Model.Provider, ModelProviders.Ollama, StringComparison.OrdinalIgnoreCase))
            {
                return recommendation.Model;
            }

            return new ModelSelector()
                .GetCompatibleModels(hardware, catalog, options.AllowCpuFallback)
                .Where(model => string.Equals(model.Provider, ModelProviders.Ollama, StringComparison.OrdinalIgnoreCase)
                    && model.AutomaticSelectionAllowed)
                .OrderByDescending(model => model.ParameterBillions)
                .FirstOrDefault();
        }

        OllamaClient.ValidateModelIdentifier(options.RequestedModel);
        var requested = catalog.Models.FirstOrDefault(model =>
            string.Equals(model.Tag, options.RequestedModel, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The requested model is not in the audited catalog.");
        if (!string.Equals(requested.Provider, ModelProviders.Ollama, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This acquisition path is for Ollama models only. Register an existing LM Studio GGUF through the dedicated LM Studio flow.");
        }
        if (!requested.CommercialUseAllowed && !options.AcceptRestrictedModelLicense)
        {
            throw new InvalidOperationException("The requested model has a restrictive license and requires explicit acceptance.");
        }

        var compatible = new ModelSelector().GetCompatibleModels(hardware, catalog, options.AllowCpuFallback);
        if (!compatible.Any(model =>
                string.Equals(model.Provider, requested.Provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(model.Tag, requested.Tag, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The requested model exceeds the conservative hardware recommendation.");
        }

        return requested;
    }

    internal static StorageRecommendation ValidateCustomStorage(
        HardwareProfile hardware,
        ModelCatalogEntry model,
        string directory)
    {
        var full = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(full)
            ?? throw new InvalidOperationException("The custom model directory has no local drive root.");
        var volume = hardware.Volumes.FirstOrDefault(item => string.Equals(item.RootPath, root, StringComparison.OrdinalIgnoreCase));
        if (volume is null || !volume.IsFixed || volume.MediaType == StorageMediaType.Network)
        {
            throw new InvalidOperationException("The custom model directory must be on suitable fixed local storage.");
        }

        var required = Math.Max(
            (ulong)Math.Ceiling(model.MinimumFreeDiskGiB) * 1024UL * 1024UL * 1024UL,
            (ulong)Math.Ceiling(model.ExpectedDownloadBytes * 2.15m));
        var reserve = volume.IsSystem ? Math.Max(20UL * 1024UL * 1024UL * 1024UL, volume.TotalBytes / 10) : 10UL * 1024UL * 1024UL * 1024UL;
        if (volume.FreeBytes < required + reserve)
        {
            throw new InvalidOperationException("The custom model directory does not leave enough free-space reserve.");
        }

        var warnings = new List<string>();
        if (volume.MediaType == StorageMediaType.Removable)
        {
            warnings.Add(
                "The explicitly selected model directory is on removable or externally attached storage. " +
                "Keep it connected, mounted at the same drive letter, and unlocked before Ollama or Codex starts.");
        }
        else if (volume.MediaType == StorageMediaType.Hdd)
        {
            warnings.Add("The selected directory is on an HDD.");
        }

        if (volume.IsEncrypted)
        {
            warnings.Add("The selected volume is encrypted. It must be unlocked before Ollama starts.");
        }

        return new StorageRecommendation(
            volume,
            full,
            required,
            volume.FreeBytes - required,
            volume.MediaType == StorageMediaType.Removable
                ? "The user explicitly selected an attached fixed-volume model directory; it will never be selected automatically."
                : "The user selected a suitable fixed local model directory.",
            warnings);
    }

    internal static StorageRecommendation ValidateLiveStorageDestination(
        StorageRecommendation recommendation,
        ModelCatalogEntry model,
        Func<string, DriveInfo>? driveInfoFactory = null,
        Action<string>? pathValidator = null)
    {
        if (recommendation.Volume is null || string.IsNullOrWhiteSpace(recommendation.ModelDirectory))
        {
            throw new InvalidOperationException("No model-storage destination is available to validate.");
        }

        var full = Path.GetFullPath(recommendation.ModelDirectory);
        (pathValidator ?? ModelStoragePathLease.ValidateCandidate)(full);
        var root = Path.GetPathRoot(full)
            ?? throw new InvalidOperationException("The model-storage destination has no local drive root.");
        var drive = (driveInfoFactory ?? (value => new DriveInfo(value)))(root);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
        {
            throw new InvalidOperationException("The model-storage destination must resolve to a ready fixed local drive.");
        }

        var required = Math.Max(
            (ulong)Math.Ceiling(model.MinimumFreeDiskGiB) * 1024UL * 1024UL * 1024UL,
            (ulong)Math.Ceiling(model.ExpectedDownloadBytes * 2.15m));
        var liveTotal = checked((ulong)drive.TotalSize);
        var liveFree = checked((ulong)drive.AvailableFreeSpace);
        var reserve = recommendation.Volume.IsSystem
            ? Math.Max(20UL * 1024UL * 1024UL * 1024UL, liveTotal / 10)
            : 10UL * 1024UL * 1024UL * 1024UL;
        if (liveFree < required + reserve)
        {
            throw new InvalidOperationException(
                "The selected model-storage destination no longer has enough free space plus the required safety reserve.");
        }

        return recommendation with
        {
            Volume = recommendation.Volume with
            {
                RootPath = root,
                TotalBytes = liveTotal,
                FreeBytes = liveFree,
                IsFixed = true
            },
            ModelDirectory = full,
            RequiredBytes = required,
            RemainingBytes = liveFree - required
        };
    }

    private static string NormalizeDirectory(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void ValidateCodexHomeRoute(
        ProductPaths paths,
        InstallationState? state,
        string operation)
    {
        var requested = NormalizeDirectory(paths.CodexHome);
        var context = InstallContextStore.Load(paths.InstallDirectory);
        var recordedHomes = new[]
        {
            state?.ManagedCodexHome,
            context?.CodexHome
        };
        if (recordedHomes.Any(recorded =>
                !string.IsNullOrWhiteSpace(recorded)
                && !string.Equals(
                    NormalizeDirectory(recorded),
                    requested,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"{operation} refused because the requested Codex home does not match the Codex home recorded by this installation. No managed files or install context were changed; state was also left untouched.");
        }
    }

    private static void ThrowIfOwnershipInvalid(ProductPaths? paths, InstallationState state)
    {
        if (paths is null)
        {
            return;
        }

        var ownership = IntegrationOwnership.Inspect(paths, state);
        if (ownership.Status != IntegrationOwnershipStatus.ManagedValid)
        {
            throw new OllamaException(
                ownership.Status == IntegrationOwnershipStatus.ExternalUnmarked
                    ? "EXISTING_INTEGRATION_PRESERVED"
                    : "INTEGRATION_OWNERSHIP_DRIFT",
                ownership.Message + " No further Ollama request was made.");
        }
    }

    private void ValidateRepairOwnership(
        ProductPaths paths,
        InstallationState state,
        bool allowManagedDrift)
    {
        var raw = _codexConfig.InspectOwnership(paths);
        var stateClaimsManaged = IntegrationOwnership.IsManagedByHelper(state);
        if (raw == CodexIntegrationOwnership.Invalid)
        {
            throw new InvalidOperationException(
                "Repair refused because the current Codex reviewer table is malformed or ambiguous. No files were changed.");
        }

        if (stateClaimsManaged)
        {
            if (raw == CodexIntegrationOwnership.ManagedValid
                || (allowManagedDrift && raw == CodexIntegrationOwnership.ManagedDrift))
            {
                return;
            }

            throw new InvalidOperationException(
                "Repair refused because the helper ownership record no longer matches the current Codex reviewer entry. Review a fresh protected-file diff before changing it.");
        }

        if (raw is CodexIntegrationOwnership.ManagedValid or CodexIntegrationOwnership.ManagedDrift)
        {
            throw new InvalidOperationException(
                "Repair refused because managed Codex markers exist without a matching helper ownership record. No files were changed.");
        }
    }

    private static void ValidateRepairBinding(
        RepairHashBinding? binding,
        CodexConfigPreview config,
        AgentsOverridePreview agents)
    {
        if (binding is null)
        {
            return;
        }

        ValidateHash(binding.ConfigSourceSha256, allowMissing: true, "config.toml source");
        ValidateHash(binding.ConfigPlannedSha256, allowMissing: false, "config.toml plan");
        ValidateHash(binding.AgentsSourceSha256, allowMissing: true, "AGENTS.override.md source");
        ValidateHash(binding.AgentsPlannedSha256, allowMissing: false, "AGENTS.override.md plan");
        if (!string.Equals(binding.ConfigSourceSha256, config.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(binding.ConfigPlannedSha256, config.PlannedSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(binding.AgentsSourceSha256, agents.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(binding.AgentsPlannedSha256, agents.PlannedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Protected repair hashes do not match the current source files and computed plans. Neither protected file was changed; run a fresh dry-run.");
        }
    }

    private static void ValidateHash(string value, bool allowMissing, string label)
    {
        if (allowMissing && string.Equals(value, "MISSING", StringComparison.Ordinal))
        {
            return;
        }

        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException($"The expected {label} SHA-256 is invalid.");
        }
    }

    private static void ValidateDiffOutputPath(string path)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException("Repair dry-run diff output must name a file, not a directory.");
        }

        var targetInfo = new FileInfo(path);
        if (targetInfo.LinkTarget is not null
            || (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new InvalidOperationException("Repair dry-run diff output cannot be a symbolic link or reparse point.");
        }

        for (var directory = Path.GetDirectoryName(path);
             !string.IsNullOrWhiteSpace(directory);
             directory = Path.GetDirectoryName(directory))
        {
            var directoryInfo = new DirectoryInfo(directory);
            if (directoryInfo.LinkTarget is not null
                || (Directory.Exists(directory)
                    && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)))
            {
                throw new InvalidOperationException("Repair dry-run diff output cannot be beneath a symbolic link, junction, or reparse point.");
            }

            var root = Path.GetPathRoot(directory);
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(directory),
                    Path.TrimEndingDirectorySeparator(root ?? string.Empty),
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }

    internal static bool IsFixedLocalPath(
        string path,
        Func<string, DriveType>? driveTypeResolver = null)
    {
        var fullPath = Path.GetFullPath(path);
        if (new Uri(fullPath).IsUnc)
        {
            return false;
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var driveType = (driveTypeResolver ?? (value => new DriveInfo(value).DriveType))(root);
            return driveType == DriveType.Fixed;
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

    private static void RestoreProtectedSnapshot(
        string path,
        ProtectedFileSnapshot original,
        ProtectedFileSnapshot? stateWrittenByRepair)
    {
        var current = ProtectedFileTransaction.Capture(path);
        if (current.Exists == original.Exists && current.Bytes.AsSpan().SequenceEqual(original.Bytes))
        {
            return;
        }

        if (stateWrittenByRepair is null
            || current.Exists != stateWrittenByRepair.Exists
            || !current.Bytes.AsSpan().SequenceEqual(stateWrittenByRepair.Bytes))
        {
            // The failed save did not complete, or another helper process wrote newer state.
            // Preserve that state rather than replacing it with this repair's older snapshot.
            return;
        }

        if (original.Exists)
        {
            ProtectedFileTransaction.ReplaceIfUnchanged(path, current, original.Bytes);
        }
        else if (current.Exists)
        {
            ProtectedFileTransaction.DeleteIfUnchanged(path, current);
        }
    }

    private StartupExternalState CaptureStartupExternalState()
    {
        var platform = _startupPlatform
            ?? throw new InvalidOperationException("The Ollama startup platform is unavailable for guarded repair rollback.");
        return new StartupExternalState(
            platform.GetUserEnvironmentVariable("OLLAMA_MODELS"),
            platform.GetUserEnvironmentVariable("OLLAMA_HOST"),
            _processEnvironmentReader("OLLAMA_MODELS"),
            _processEnvironmentReader("OLLAMA_HOST"),
            platform.GetRunEntry());
    }

    private void RestoreStartupExternalState(
        StartupExternalState original,
        StartupExternalState writtenByRepair)
    {
        var platform = _startupPlatform
            ?? throw new InvalidOperationException("The Ollama startup platform is unavailable for guarded repair rollback.");
        var current = CaptureStartupExternalState();

        ValidateStartupRollbackValue(
            "OLLAMA_MODELS",
            current.UserModels,
            writtenByRepair.UserModels,
            original.UserModels);
        ValidateStartupRollbackValue(
            "OLLAMA_HOST",
            current.UserHost,
            writtenByRepair.UserHost,
            original.UserHost);
        ValidateStartupRollbackValue(
            "OLLAMA_MODELS",
            current.ProcessModels,
            writtenByRepair.ProcessModels,
            original.ProcessModels);
        ValidateStartupRollbackValue(
            "OLLAMA_HOST",
            current.ProcessHost,
            writtenByRepair.ProcessHost,
            original.ProcessHost);
        ValidateStartupRollbackValue(
            "Ollama Run entry",
            current.RunEntry,
            writtenByRepair.RunEntry,
            original.RunEntry);

        var environmentChanged = false;
        environmentChanged |= RestoreStartupValue(
            "OLLAMA_MODELS",
            current.UserModels,
            writtenByRepair.UserModels,
            original.UserModels,
            platform.SetUserEnvironmentVariable);
        environmentChanged |= RestoreStartupValue(
            "OLLAMA_HOST",
            current.UserHost,
            writtenByRepair.UserHost,
            original.UserHost,
            platform.SetUserEnvironmentVariable);
        environmentChanged |= RestoreStartupValue(
            "OLLAMA_MODELS",
            current.ProcessModels,
            writtenByRepair.ProcessModels,
            original.ProcessModels,
            platform.SetProcessEnvironmentVariable);
        environmentChanged |= RestoreStartupValue(
            "OLLAMA_HOST",
            current.ProcessHost,
            writtenByRepair.ProcessHost,
            original.ProcessHost,
            platform.SetProcessEnvironmentVariable);

        if (!string.Equals(current.RunEntry, original.RunEntry, StringComparison.Ordinal))
        {
            if (!string.Equals(current.RunEntry, writtenByRepair.RunEntry, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The Ollama Run entry changed after repair wrote it. Guarded rollback preserved the newer value.");
            }

            platform.SetRunEntry(original.RunEntry);
        }

        if (environmentChanged)
        {
            platform.BroadcastEnvironmentChange();
        }

    }

    private static bool RestoreStartupValue(
        string name,
        string? current,
        string? writtenByRepair,
        string? original,
        Action<string, string?> restore)
    {
        if (string.Equals(current, original, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(current, writtenByRepair, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{name} changed after repair wrote it. Guarded rollback preserved the newer value.");
        }

        restore(name, original);
        return true;
    }

    private static void ValidateStartupRollbackValue(
        string name,
        string? current,
        string? writtenByRepair,
        string? original)
    {
        if (!string.Equals(current, original, StringComparison.Ordinal)
            && !string.Equals(current, writtenByRepair, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{name} changed after repair wrote it. Guarded rollback preserved the newer value.");
        }
    }

    private async Task<ProtectedFileSnapshot> SaveRepairStateAsync(
        StateStore store,
        InstallationState state,
        ProtectedFileSnapshot expected,
        CancellationToken cancellationToken)
    {
        if (_stateSaver is null)
        {
            return await store.SaveIfUnchangedAsync(state, expected, cancellationToken).ConfigureAwait(false);
        }

        await _stateSaver(store, state, cancellationToken).ConfigureAwait(false);
        return ProtectedFileTransaction.Capture(store.Path);
    }

    private static void TrackManagedFile(InstallationState state, ManagedFileResult result)
    {
        var target = result.Created ? state.FilesCreated : state.FilesModified;
        if (result.Changed && !target.Contains(result.Path, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(result.Path);
        }

        if (result.BackupPath is not null)
        {
            state.BackupLocations.TryAdd(result.Path, result.BackupPath);
        }
    }

    private static bool IsExistingIntegrationPreserved(ManagedFileResult result)
        => string.Equals(result.Operation, "preserved-existing-unmanaged", StringComparison.Ordinal);

    private static void SetManagedSection(InstallationState state, string section, bool managed)
    {
        state.ManagedConfigurationSections.RemoveAll(item => string.Equals(item, section, StringComparison.Ordinal));
        if (managed)
        {
            state.ManagedConfigurationSections.Add(section);
        }

        if (string.Equals(section, "AGENTS.override.md local GPU section", StringComparison.Ordinal))
        {
            state.ManagedConfigurationSections.RemoveAll(item =>
                string.Equals(item, "AGENTS.override.md managed section", StringComparison.Ordinal));
        }
    }

    private static void AddWarning(List<string> warnings, string? warning)
    {
        if (!string.IsNullOrWhiteSpace(warning) && !warnings.Contains(warning, StringComparer.Ordinal))
        {
            warnings.Add(warning);
        }
    }

    private static bool IsOllamaPresent()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Ollama",
            "ollama.exe");
        if (File.Exists(local))
        {
            return true;
        }

        var processes = System.Diagnostics.Process.GetProcessesByName("ollama");
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    private sealed record StartupExternalState(
        string? UserModels,
        string? UserHost,
        string? ProcessModels,
        string? ProcessHost,
        string? RunEntry);
}
