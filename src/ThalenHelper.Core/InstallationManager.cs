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
    Func<CancellationToken, Task>? EnsureOllamaInstalledAsync = null);

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
    private readonly Func<OllamaClient> _clientFactory;
    private readonly Func<InstallationState, bool, ResourcePressureCheck> _resourcePressureValidator;

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
        Func<InstallationState, bool, ResourcePressureCheck>? resourcePressureValidator = null)
    {
        _hardwareProvider = hardwareProvider ?? (hardwareDetector ?? new HardwareDetector()).Detect;
        _catalogService = catalogService ?? new ModelCatalogService();
        _modelSelector = modelSelector ?? new ModelSelector();
        _storageSelector = storageSelector ?? new StorageSelector();
        _codexConfig = codexConfig ?? new CodexConfigManager();
        _agentsOverride = agentsOverride ?? new AgentsOverrideManager();
        _clientFactory = clientFactory ?? (() => new OllamaClient());
        _autoStart = autoStart ?? new OllamaAutoStartManager(_clientFactory);
        var pressureGuard = new ResourcePressureGuard();
        _resourcePressureValidator = resourcePressureValidator ?? pressureGuard.Check;
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

        InstallContextStore.Save(options.Paths);

        var currentUserModelDirectory = Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User);
        var hardware = _hardwareProvider();
        var catalog = _catalogService.LoadBundled();
        var recommendation = _modelSelector.Recommend(hardware, catalog, options.AllowCpuFallback);
        var selected = SelectRequestedModel(options, catalog, recommendation);
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
        }

        var store = new StateStore(options.Paths.StateFile);
        var priorState = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var hasAgentsPreviewBinding = options.ExpectedAgentsSourceSha256 is not null;
        var installReliabilityBaseline = hasAgentsPreviewBinding
            ? options.InstallReliabilityBaseline
            : priorState?.ReliabilityBaselineInstalled ?? false;
        var priorValidatedSelection = priorState?.Availability == HelperAvailability.Enabled
            && string.Equals(priorState.SelectedModel, selected?.Tag, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                priorState.ModelStorageLocation is null ? null : Path.GetFullPath(priorState.ModelStorageLocation),
                storage?.ModelDirectory is null ? null : Path.GetFullPath(storage.ModelDirectory),
                StringComparison.OrdinalIgnoreCase);
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
            SelectedModel = selected?.Tag,
            SelectedModelDigest = selected?.ExpectedDigest,
            SelectedModelOwnedByHelper = priorValidatedSelection && priorState?.SelectedModelOwnedByHelper == true,
            ModelStorageLocation = storage?.ModelDirectory,
            ManagedCodexHome = options.Paths.CodexHome,
            HardwareTier = selected is null ? HardwareTier.NoModel : ModelSelector.GetHardwareTier(selected),
            Acceleration = priorValidatedSelection ? priorState?.Acceleration : null,
            Availability = priorValidatedSelection ? HelperAvailability.Enabled : HelperAvailability.Disabled,
            LastHealthCheckAt = priorValidatedSelection ? priorState?.LastHealthCheckAt : null,
            LastHealthCheckCode = priorValidatedSelection ? priorState?.LastHealthCheckCode : null,
            Preferences = priorState is null
                ? new HelperPreferences(
                    LowImpactMode: recommendation.LowImpactMode,
                    KeepWarm: false,
                    AutoStartOllama: options.AutoStartOllama,
                    IdleUnloadSeconds: 0)
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
                installReliabilityBaseline);
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

        var configResult = _codexConfig.InstallOrRepair(
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
                options.ExpectedAgentsPlannedSha256);
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
                "The existing local_gpu_reviewer integration was preserved unchanged. Only missing, non-conflicting managed instruction sections were considered; Ollama, models, startup, and integration activation were not changed.",
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
            Directory.CreateDirectory(state.ModelStorageLocation);
            startup = await _autoStart.ApplyConfigurationAsync(
                options.Paths,
                state,
                currentUserModelDirectory,
                options.AutoStartOllama,
                options.RestartOllamaForModelPath,
                cancellationToken).ConfigureAwait(false);
            state.StartupEntryOwnedByHelper = options.AutoStartOllama;
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
                "INSTALLED_NO_MODEL",
                "The helper was installed in disabled/no-model mode because no safe model fit was available.",
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
            || startup.Code is "OLLAMA_RESTART_REQUIRED" or "OLLAMA_RESTART_UNLOAD_FAILED" or "OLLAMA_RESTART_FAILED")
        {
            state.Availability = HelperAvailability.Disabled;
            state.LastHealthCheckAt = DateTimeOffset.UtcNow;
            state.LastHealthCheckCode = startup?.Code ?? "OLLAMA_STARTUP_UNVERIFIED";
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new InstallationOutcome(
                false,
                "OLLAMA_CONFIGURATION_FAILED",
                "Ollama did not safely inherit and verify the configured loopback model directory. The helper remains disabled.",
                hardware,
                recommendation,
                storage,
                state,
                configResult,
                agentsResult,
                startup,
                warnings);
        }

        var validation = await PullAndValidateAsync(selected, state, cancellationToken).ConfigureAwait(false);
        if (!validation.Success)
        {
            var fallback = catalog.Models
                .Where(model => model.AutomaticSelectionAllowed
                    && model.CommercialUseAllowed
                    && model.ParameterBillions < selected.ParameterBillions)
                .OrderByDescending(model => model.ParameterBillions)
                .FirstOrDefault();
            if (fallback is not null)
            {
                warnings.Add($"{selected.Tag} failed validation with {validation.Code}. One bounded downgrade to {fallback.Tag} was attempted.");
                state.SelectedModel = fallback.Tag;
                state.SelectedModelDigest = fallback.ExpectedDigest;
                validation = await PullAndValidateAsync(fallback, state, cancellationToken).ConfigureAwait(false);
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
                "The selected model and one safe fallback failed validation. The helper remains installed but disabled.",
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

        state.Availability = HelperAvailability.Enabled;
        state.LastHealthCheckAt = DateTimeOffset.UtcNow;
        state.LastHealthCheckCode = "OK";
        await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        _codexConfig.SetEnabled(options.Paths, true);
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        InstallContextStore.Save(paths);
        var store = new StateStore(paths.StateFile);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No installation state was found.");
        var hardware = _hardwareProvider();
        var catalog = _catalogService.LoadBundled();
        var recommendation = _modelSelector.Recommend(hardware, catalog, state.Acceleration?.Processor == "CPU");
        var config = _codexConfig.InstallOrRepair(
            paths,
            state.Availability == HelperAvailability.Enabled,
            codexStartupValidator);
        state.ExistingIntegrationPreserved = IsExistingIntegrationPreserved(config);
        if (state.ExistingIntegrationPreserved)
        {
            state.SelectedModel = null;
            state.SelectedModelDigest = null;
            state.SelectedModelOwnedByHelper = false;
            state.ModelStorageLocation = null;
            state.Acceleration = null;
        }

        ManagedFileResult agents;
        try
        {
            agents = _agentsOverride.InstallOrRepair(
                paths,
                state.HardwareTier,
                state.ReliabilityBaselineInstalled);
        }
        catch
        {
            _codexConfig.Rollback(config);
            throw;
        }
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
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
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
            var currentModelDirectory = Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User);
            startup = await _autoStart.ApplyConfigurationAsync(
                paths,
                state,
                currentModelDirectory,
                state.Preferences.AutoStartOllama,
                allowSafeRestart: true,
                cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(state.SelectedModel)
            && (startup is null || !ModelIntegrity.IsOperationallySafe(startup, state)))
        {
            state.Availability = HelperAvailability.Disabled;
            state.LastHealthCheckAt = DateTimeOffset.UtcNow;
            state.LastHealthCheckCode = startup?.Code ?? "OLLAMA_STARTUP_UNVERIFIED";
            TrackManagedFile(state, _codexConfig.SetEnabled(paths, false));
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            GpuCoordination.RequestCancellation();
            return new InstallationOutcome(
                false,
                "REPAIR_VERIFICATION_FAILED",
                "Managed files were repaired, but the selected model, configured model path, digest, endpoint, or loopback listener failed passive verification. The helper remains disabled.",
                hardware,
                recommendation,
                null,
                state,
                config,
                agents,
                startup,
                repairWarnings);
        }

        await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
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

    public async Task<ManagedFileResult> ConfigureReliabilityBaselineAsync(
        ProductPaths paths,
        bool install,
        string expectedAgentsSourceSha256,
        string expectedAgentsPlannedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        InstallContextStore.Save(paths);
        var store = new StateStore(paths.StateFile);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No installation state was found.");
        var result = _agentsOverride.InstallOrRepair(
            paths,
            state.HardwareTier,
            install,
            expectedAgentsSourceSha256,
            expectedAgentsPlannedSha256);
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

    public async Task<ModelValidationResult> ValidateSelectedModelAsync(
        InstallationState state,
        CancellationToken cancellationToken = default)
    {
        if (!IntegrationOwnership.IsManagedByHelper(state))
        {
            return new ModelValidationResult(
                false,
                "EXISTING_INTEGRATION_PRESERVED",
                "The existing unmarked local_gpu_reviewer integration is not controlled by this helper.",
                null,
                0,
                0);
        }

        if (string.IsNullOrWhiteSpace(state.SelectedModel))
        {
            return new ModelValidationResult(false, "NO_MODEL", "No selected model is configured.", null, 0, 0);
        }

        using var client = _clientFactory();
        var exactStart = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var models = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            var selected = ModelIntegrity.FindSelectedModel(models, state.SelectedModel);
            if (selected is null)
            {
                return new ModelValidationResult(false, "SELECTED_MODEL_UNAVAILABLE", "The selected model is not available from Ollama.", null, 0, 0);
            }

            if (!ModelIntegrity.DigestMatches(selected.Digest, state.SelectedModelDigest))
            {
                return new ModelValidationResult(false, "MODEL_DIGEST_MISMATCH", "The selected model digest does not match the audited catalog digest.", null, 0, 0);
            }

            async Task CheckResourcePressureAsync(CancellationToken token)
            {
                var runningModels = await client.GetRunningModelsAsync(token).ConfigureAwait(false);
                var selectedAlreadyLoaded = runningModels.Any(model =>
                    ModelIntegrity.NamesMatch(model.Name, state.SelectedModel));
                var pressure = _resourcePressureValidator(state, selectedAlreadyLoaded);
                if (!pressure.Allowed)
                {
                    throw new OllamaException(pressure.Code, pressure.Message, retryable: true);
                }
            }

            var exact = await client.GenerateAsync(
                state.SelectedModel,
                "Reply with exactly this text and nothing else: THALEN_HELPER_OK",
                state.HardwareTier == HardwareTier.Entry ? 2_048 : 4_096,
                64,
                TimeSpan.FromSeconds(30),
                cancellationToken,
                preGenerationCheck: CheckResourcePressureAsync).ConfigureAwait(false);
            exactStart.Stop();
            if (!string.Equals(exact.Response.Trim(), "THALEN_HELPER_OK", StringComparison.Ordinal))
            {
                await client.UnloadAsync(state.SelectedModel, cancellationToken).ConfigureAwait(false);
                return new ModelValidationResult(false, "EXACT_RESPONSE_FAILED", "The exact-response smoke test failed.", null, exactStart.ElapsedMilliseconds, 0);
            }

            var reviewStart = System.Diagnostics.Stopwatch.StartNew();
            var review = await client.GenerateAsync(
                state.SelectedModel,
                "Inspect only this loop: for (int i = 0; i <= items.Length; i++) { Use(items[i]); }. Reply with exactly: OFF_BY_ONE",
                state.HardwareTier == HardwareTier.Entry ? 2_048 : 4_096,
                64,
                TimeSpan.FromSeconds(30),
                cancellationToken,
                preGenerationCheck: CheckResourcePressureAsync).ConfigureAwait(false);
            reviewStart.Stop();
            if (!string.Equals(review.Response.Trim(), "OFF_BY_ONE", StringComparison.Ordinal))
            {
                await client.UnloadAsync(state.SelectedModel, cancellationToken).ConfigureAwait(false);
                return new ModelValidationResult(false, "CODE_REVIEW_SMOKE_FAILED", "The bounded code-review smoke test failed.", null, exactStart.ElapsedMilliseconds, reviewStart.ElapsedMilliseconds);
            }

            var running = await client.GetRunningModelsAsync(cancellationToken).ConfigureAwait(false);
            var loaded = running.FirstOrDefault(item => string.Equals(item.Name, state.SelectedModel, StringComparison.OrdinalIgnoreCase));
            var acceleration = loaded is null
                ? null
                : new AccelerationResult(
                    loaded.SizeVramBytes > 0 ? "GPU or partial GPU (verify processor split with ollama ps)" : "CPU",
                    loaded.SizeVramBytes,
                    loaded.ContextLength,
                    loaded.ExpiresAt);
            await client.UnloadAsync(state.SelectedModel, cancellationToken).ConfigureAwait(false);
            var after = await client.GetRunningModelsAsync(cancellationToken).ConfigureAwait(false);
            if (after.Any(item => string.Equals(item.Name, state.SelectedModel, StringComparison.OrdinalIgnoreCase)))
            {
                return new ModelValidationResult(false, "GPU_RELEASE_FAILED", "The selected model did not unload after validation.", acceleration, exactStart.ElapsedMilliseconds, reviewStart.ElapsedMilliseconds);
            }

            return new ModelValidationResult(true, "OK", "Exact response, bounded review, runtime inspection, and unload passed.", acceleration, exactStart.ElapsedMilliseconds, reviewStart.ElapsedMilliseconds);
        }
        catch (OllamaException exception)
        {
            return new ModelValidationResult(false, exception.Code, exception.Message, null, exactStart.ElapsedMilliseconds, 0);
        }
    }

    private async Task<ModelValidationResult> PullAndValidateAsync(
        ModelCatalogEntry model,
        InstallationState state,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _clientFactory();
            var existing = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            var alreadyPresent = existing.Any(item => string.Equals(item.Name, model.Tag, StringComparison.OrdinalIgnoreCase));
            if (!alreadyPresent)
            {
                await client.PullAsync(model.Tag, cancellationToken).ConfigureAwait(false);
                state.SelectedModelOwnedByHelper = true;
            }
            else
            {
                state.SelectedModelOwnedByHelper = false;
            }

            state.SelectedModel = model.Tag;
            state.SelectedModelDigest = model.ExpectedDigest;
            var inventory = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            var installed = ModelIntegrity.FindSelectedModel(inventory, model.Tag);
            if (installed is null)
            {
                return new ModelValidationResult(false, "SELECTED_MODEL_UNAVAILABLE", "Ollama did not return the selected model after pull.", null, 0, 0);
            }

            if (!ModelIntegrity.DigestMatches(installed.Digest, model.ExpectedDigest))
            {
                return new ModelValidationResult(false, "MODEL_DIGEST_MISMATCH", "The selected model digest does not match the audited catalog digest.", null, 0, 0);
            }

            return await ValidateSelectedModelAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (OllamaException exception)
        {
            return new ModelValidationResult(false, exception.Code, exception.Message, null, 0, 0);
        }
    }

    private static ModelCatalogEntry? SelectRequestedModel(
        InstallationOptions options,
        ModelManifest catalog,
        ModelRecommendation recommendation)
    {
        if (string.IsNullOrWhiteSpace(options.RequestedModel))
        {
            return recommendation.Model;
        }

        OllamaClient.ValidateModelIdentifier(options.RequestedModel);
        var requested = catalog.Models.FirstOrDefault(model =>
            string.Equals(model.Tag, options.RequestedModel, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The requested model is not in the audited catalog.");
        if (!requested.CommercialUseAllowed && !options.AcceptRestrictedModelLicense)
        {
            throw new InvalidOperationException("The requested model has a restrictive license and requires explicit acceptance.");
        }

        if (recommendation.Model is null || requested.ParameterBillions > recommendation.Model.ParameterBillions)
        {
            throw new InvalidOperationException("The requested model exceeds the conservative hardware recommendation.");
        }

        return requested;
    }

    private static StorageRecommendation ValidateCustomStorage(
        HardwareProfile hardware,
        ModelCatalogEntry model,
        string directory)
    {
        var full = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(full)
            ?? throw new InvalidOperationException("The custom model directory has no local drive root.");
        var volume = hardware.Volumes.FirstOrDefault(item => string.Equals(item.RootPath, root, StringComparison.OrdinalIgnoreCase));
        if (volume is null || !volume.IsSuitable || !volume.IsFixed
            || volume.MediaType is StorageMediaType.Network or StorageMediaType.Removable)
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

        return new StorageRecommendation(
            volume,
            full,
            required,
            volume.FreeBytes - required,
            "The user selected a suitable fixed local model directory.",
            volume.MediaType == StorageMediaType.Hdd ? ["The selected directory is on an HDD."] : []);
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
}
