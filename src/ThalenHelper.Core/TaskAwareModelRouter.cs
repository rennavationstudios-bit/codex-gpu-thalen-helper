namespace ThalenHelper.Core;

public sealed class TaskAwareModelRouter
{
    private const decimal GiB = 1024m * 1024m * 1024m;

    public ModelRouteDecision Plan(
        ReviewRequest request,
        InstallationState state,
        ModelManifest catalog,
        HardwareProfile hardware,
        IReadOnlyList<OllamaModelInfo> installedModels,
        ModelValidationRegistry? validations = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(installedModels);

        var taskKind = ResolveTaskKind(request);
        var effort = ResolveEffort(request, taskKind);
        var warnings = new List<string>();
        if (request.DesiredContextTokens > 65_536 && !state.Preferences.AllowExperimentalRuntimeOverrides)
        {
            warnings.Add("The requested context exceeded 64K and was capped because 128K operation has not been explicitly enabled as an experimental, measured override.");
        }
        if (request.GpuIntensiveWorkloadActive)
        {
            effort = ReviewEffort.Quick;
        }

        if (state.Preferences.ModelSelectionMode == ModelSelectionMode.Pinned)
        {
            return PlanPinned(
                request,
                state,
                catalog,
                hardware,
                installedModels,
                validations,
                taskKind,
                effort,
                warnings);
        }

        if (request.GpuIntensiveWorkloadActive)
        {
            warnings.Add("A GPU-intensive workload is active, so automatic routing was limited to the smallest safe installed model.");
        }

        var eligible = GetEligibleInstalledModels(state, catalog, hardware, installedModels, validations)
            .OrderBy(model => model.ParameterBillions)
            .ThenBy(model => model.Tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (request.GpuIntensiveWorkloadActive)
        {
            eligible = eligible
                .Where(model => ModelSelector.GetHardwareTier(model) is HardwareTier.Entry or HardwareTier.Mid)
                .ToArray();
        }

        if (eligible.Length == 0)
        {
            return new ModelRouteDecision(
                false,
                ModelSelectionMode.Automatic,
                taskKind,
                effort,
                null,
                null,
                HardwareTier.NoModel,
                0,
                "No installed, validated, audited, digest-matching model fits the current GPU, RAM, and configured reserve.",
                warnings,
                BuildTuningPlan(state.Preferences, 0));
        }

        var preferredLmStudio = state.Preferences.PreferLmStudioForStandardAndDeep
            && effort is ReviewEffort.Standard or ReviewEffort.Deep
            && !request.GpuIntensiveWorkloadActive
            ? eligible
                .Where(model => string.Equals(model.Provider, ModelProviders.LmStudio, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(model => model.AutomaticPriority)
                .ThenByDescending(model => model.ParameterBillions)
                .FirstOrDefault()
            : null;
        var selected = preferredLmStudio ?? effort switch
        {
            ReviewEffort.Quick => eligible[0],
            ReviewEffort.Standard => eligible.LastOrDefault(model => model.ParameterBillions <= 16m)
                ?? eligible[Math.Min(eligible.Length - 1, eligible.Length / 2)],
            ReviewEffort.Deep => eligible[^1],
            _ => eligible[0]
        };
        var contextTokens = ResolveContextTokens(request, state.Preferences, selected, effort);
        return new ModelRouteDecision(
            true,
            ModelSelectionMode.Automatic,
            taskKind,
            effort,
            selected.Tag,
            selected.ExpectedDigest,
            ModelSelector.GetHardwareTier(selected),
            contextTokens,
            $"Selected {selected.Tag} from {eligible.Length} installed, validated, audited candidate(s) for {taskKind} at {effort} effort. No model was downloaded or loaded while planning.",
            warnings,
            BuildTuningPlan(state.Preferences, contextTokens, selected.Provider),
            ModelProviders.Normalize(selected.Provider));
    }

    public int CountEligibleInstalledModels(
        InstallationState state,
        ModelManifest catalog,
        HardwareProfile hardware,
        IReadOnlyList<OllamaModelInfo> installedModels,
        ModelValidationRegistry? validations = null)
        => GetEligibleInstalledModels(state, catalog, hardware, installedModels, validations).Count;

    public IReadOnlyList<string> GetEligibleInstalledModelTags(
        InstallationState state,
        ModelManifest catalog,
        HardwareProfile hardware,
        IReadOnlyList<OllamaModelInfo> installedModels,
        ModelValidationRegistry? validations = null)
        => GetEligibleInstalledModels(state, catalog, hardware, installedModels, validations)
            .Select(model => model.Tag)
            .ToArray();

    internal static ReviewTaskKind ResolveTaskKind(ReviewRequest request)
    {
        if (request.TaskKind != ReviewTaskKind.Auto)
        {
            return request.TaskKind;
        }

        var suppliedCharacters = SuppliedCharacters(request);
        return suppliedCharacters switch
        {
            <= 8_000 => ReviewTaskKind.General,
            <= 48_000 => ReviewTaskKind.DiffReview,
            _ => ReviewTaskKind.RepositoryAnalysis
        };
    }

    internal static ReviewEffort ResolveEffort(ReviewRequest request, ReviewTaskKind taskKind)
    {
        if (request.Effort != ReviewEffort.Auto)
        {
            return request.Effort;
        }

        if (taskKind is ReviewTaskKind.LogTriage or ReviewTaskKind.EdgeCases)
        {
            return ReviewEffort.Quick;
        }

        if (taskKind == ReviewTaskKind.RepositoryAnalysis)
        {
            return ReviewEffort.Deep;
        }

        if (taskKind is ReviewTaskKind.TestFailure or ReviewTaskKind.DiffReview)
        {
            return ReviewEffort.Standard;
        }

        var suppliedCharacters = SuppliedCharacters(request);
        return suppliedCharacters <= 8_000 ? ReviewEffort.Quick : ReviewEffort.Standard;
    }

    private static ModelRouteDecision PlanPinned(
        ReviewRequest request,
        InstallationState state,
        ModelManifest catalog,
        HardwareProfile hardware,
        IReadOnlyList<OllamaModelInfo> installedModels,
        ModelValidationRegistry? validations,
        ReviewTaskKind taskKind,
        ReviewEffort effort,
        IReadOnlyList<string> warnings)
    {
        var selectedProvider = ModelProviders.Normalize(state.SelectedModelProvider);
        var selected = catalog.Models.FirstOrDefault(model =>
            string.Equals(ModelProviders.Normalize(model.Provider), selectedProvider, StringComparison.Ordinal)
            && ModelIntegrity.NamesMatch(model.Tag, state.SelectedModel ?? string.Empty));
        var installed = FindInstalledModel(installedModels, selectedProvider, state.SelectedModel);
        if (selected is null
            || installed is null
            || !ModelIntegrity.DigestMatches(installed.Digest, state.SelectedModelDigest)
            || !ModelIntegrity.DigestMatches(installed.Digest, selected.ExpectedDigest)
            || validations?.HasCurrentPass(selectedProvider, selected.Tag, installed.Digest) != true)
        {
            return new ModelRouteDecision(
                false,
                ModelSelectionMode.Pinned,
                taskKind,
                effort,
                state.SelectedModel,
                selected?.ExpectedDigest ?? state.SelectedModelDigest,
                state.HardwareTier,
                0,
                "The pinned model is missing from the audited catalog, unavailable, or failed current full-digest validation.",
                warnings,
                BuildTuningPlan(state.Preferences, 0, selectedProvider),
                selectedProvider);
        }

        if (!FitsHardwareCapacity(selected, state, hardware))
        {
            return new ModelRouteDecision(
                false,
                ModelSelectionMode.Pinned,
                taskKind,
                effort,
                selected.Tag,
                selected.ExpectedDigest,
                ModelSelector.GetHardwareTier(selected),
                0,
                "The pinned model cannot preserve the configured GPU and system-memory reserve on the current hardware.",
                warnings,
                BuildTuningPlan(state.Preferences, 0, selectedProvider),
                selectedProvider);
        }

        var tier = ModelSelector.GetHardwareTier(selected);
        if (request.GpuIntensiveWorkloadActive && tier is HardwareTier.High or HardwareTier.Enthusiast)
        {
            return new ModelRouteDecision(
                false,
                ModelSelectionMode.Pinned,
                taskKind,
                effort,
                selected.Tag,
                selected.ExpectedDigest,
                tier,
                0,
                "A GPU-intensive workload is active and the pinned model is too large for an optional review. Use automatic routing, pin a smaller model, or continue without local review.",
                warnings,
                BuildTuningPlan(state.Preferences, 0, selectedProvider),
                selectedProvider);
        }

        var contextTokens = ResolveContextTokens(request, state.Preferences, selected, effort);
        return new ModelRouteDecision(
            true,
            ModelSelectionMode.Pinned,
            taskKind,
            effort,
            selected.Tag,
            selected.ExpectedDigest,
            ModelSelector.GetHardwareTier(selected),
            contextTokens,
            $"Pinned mode selected {selected.Tag}. No model was downloaded or loaded while planning.",
            warnings,
            BuildTuningPlan(state.Preferences, contextTokens, selectedProvider),
            selectedProvider);
    }

    private static IReadOnlyList<ModelCatalogEntry> GetEligibleInstalledModels(
        InstallationState state,
        ModelManifest catalog,
        HardwareProfile hardware,
        IReadOnlyList<OllamaModelInfo> installedModels,
        ModelValidationRegistry? validations)
    {
        return catalog.Models
            .Where(model => ModelProviders.IsSupported(model.Provider)
                && model.AutomaticSelectionAllowed
                && model.CommercialUseAllowed
                && FitsHardwareAndReserve(model, state, hardware))
            .Where(model =>
            {
                var installed = FindInstalledModel(installedModels, model.Provider, model.Tag);
                return installed is not null
                    && ModelIntegrity.DigestMatches(installed.Digest, model.ExpectedDigest)
                    && validations?.HasCurrentPass(model.Provider, model.Tag, installed.Digest) == true
                    && (!state.Preferences.PreferQ4Quantization
                        || model.NonQ4AutomaticAllowed
                        || installed.QuantizationLevel?.StartsWith("Q4", StringComparison.OrdinalIgnoreCase) == true);
            })
            .ToArray();
    }

    private static bool FitsHardwareAndReserve(
        ModelCatalogEntry model,
        InstallationState state,
        HardwareProfile hardware)
    {
        var totalRamGiB = hardware.Memory.TotalBytes / GiB;
        var availableRamGiB = hardware.Memory.AvailableBytes / GiB;
        var gpu = hardware.Gpus
            .Where(candidate => !candidate.IsIntegrated
                && candidate.AccelerationRoute is not AccelerationRoute.None
                    and not AccelerationRoute.Unknown
                    and not AccelerationRoute.Cpu)
            .OrderByDescending(candidate => candidate.DedicatedMemoryBytes)
            .FirstOrDefault();
        if (gpu?.AvailableDedicatedMemoryBytes is not ulong availableVramBytes)
        {
            return false;
        }

        var reserveGiB = Math.Max(0.5m, state.Preferences.VramReserveMiB / 1024m);
        var dedicatedGiB = gpu.DedicatedMemoryBytes / GiB;
        var availableVramGiB = availableVramBytes / GiB;
        var usableVramGiB = Math.Max(0, Math.Min(dedicatedGiB - reserveGiB, availableVramGiB - reserveGiB));
        return model.MinimumDedicatedVramGiB <= usableVramGiB
            && model.MinimumSystemRamGiB <= totalRamGiB
            && Math.Min(model.MinimumSystemRamGiB * 0.40m, 16m) <= availableRamGiB;
    }

    private static bool FitsHardwareCapacity(
        ModelCatalogEntry model,
        InstallationState state,
        HardwareProfile hardware)
    {
        var gpu = hardware.Gpus
            .Where(candidate => !candidate.IsIntegrated
                && candidate.AccelerationRoute is not AccelerationRoute.None
                    and not AccelerationRoute.Unknown
                    and not AccelerationRoute.Cpu)
            .OrderByDescending(candidate => candidate.DedicatedMemoryBytes)
            .FirstOrDefault();
        if (gpu is null)
        {
            return false;
        }

        var reserveGiB = Math.Max(0.5m, state.Preferences.VramReserveMiB / 1024m);
        var dedicatedGiB = gpu.DedicatedMemoryBytes / GiB;
        var totalRamGiB = hardware.Memory.TotalBytes / GiB;
        return model.MinimumDedicatedVramGiB + reserveGiB <= dedicatedGiB
            && model.MinimumSystemRamGiB <= totalRamGiB;
    }

    private static int SuppliedCharacters(ReviewRequest request)
        => request.EstimatedInputCharacters
            ?? request.Assignment.Length + (request.Context?.Length ?? 0) + (request.Focus?.Length ?? 0);

    private static int ResolveContextTokens(
        ReviewRequest request,
        HelperPreferences preferences,
        ModelCatalogEntry model,
        ReviewEffort effort)
    {
        var preferred = request.DesiredContextTokens ?? effort switch
        {
            ReviewEffort.Quick => preferences.QuickContextTokens,
            ReviewEffort.Standard => preferences.StandardContextTokens,
            ReviewEffort.Deep => preferences.DeepContextTokens,
            _ => preferences.StandardContextTokens
        };
        var policyMaximum = preferences.AllowExperimentalRuntimeOverrides
            ? preferences.MaximumContextTokens
            : Math.Min(preferences.MaximumContextTokens, 65_536);
        var maximum = Math.Min(policyMaximum, model.MaximumContextTokens);
        return Math.Clamp(preferred, 512, maximum);
    }

    private static OllamaModelInfo? FindInstalledModel(
        IReadOnlyList<OllamaModelInfo> installedModels,
        string provider,
        string? model)
        => installedModels.FirstOrDefault(item =>
            string.Equals(ModelProviders.Normalize(item.Provider), ModelProviders.Normalize(provider), StringComparison.Ordinal)
            && ModelIntegrity.NamesMatch(item.Name, model ?? string.Empty));

    private static ReviewerTuningPlan BuildTuningPlan(HelperPreferences preferences, int contextTokens, string provider = ModelProviders.Ollama)
        => new(
            preferences.PreferQ4Quantization ? "Q4 required for automatic routing" : "catalog quantization",
            contextTokens,
            preferences.PreferFlashAttention,
            preferences.KeyCacheQuantization,
            preferences.ValueCacheQuantization,
            preferences.VramReserveMiB,
            "Maximize cautiously while preserving the configured VRAM reserve.",
            preferences.AllowCpuMoeOffloadWhenSupported
                ? "Move some MoE experts to system RAM when the provider safely supports it."
                : "Keep provider defaults.",
            preferences.PreferGpuKvCache ? "Prefer GPU" : "Provider default",
            preferences.PreferModelProvidedChatTemplate ? "Use the model-provided Jinja template" : "Provider default",
            string.Equals(ModelProviders.Normalize(provider), ModelProviders.LmStudio, StringComparison.Ordinal)
                ? "The helper explicitly requests LM Studio Flash Attention, GPU K/V cache, model-provided template, reasoning off, bounded context, and verified unload. LM Studio remains authoritative for the accepted load configuration."
                : "The helper enforces model, context, Q4 eligibility, locking, and pressure policy. Ollama manages Flash Attention, K/V cache format, tensor placement, and chat-template internals; verify those runtime settings instead of assuming they were applied per request.",
            preferences.AllowExperimentalRuntimeOverrides
                ? ["Experimental overrides are enabled by explicit preference; validate each result."]
                : ["128K context, no-mmap, manual tensor routing, TurboQuant, Q3-versus-Q4 speed claims, and unverified tokens-per-second claims remain disabled experiments."]);
}
