using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class TaskAwareModelRouterTests
{
    [Theory]
    [InlineData(ReviewEffort.Quick, "qwen3:8b")]
    [InlineData(ReviewEffort.Standard, "qwen3:14b")]
    [InlineData(ReviewEffort.Deep, "qwen3-coder:30b")]
    public void AutomaticRoutingChoosesInstalledAuditedModelForEffort(
        ReviewEffort effort,
        string expectedModel)
    {
        var route = new TaskAwareModelRouter().Plan(
            new ReviewRequest("Review supplied text.", Effort: effort),
            AutomaticState(),
            Catalog(),
            Rtx3090(),
            InstalledQwenModels(),
            ValidationRegistry(InstalledQwenModels()));

        Assert.True(route.Allowed, route.Reason);
        Assert.Equal(ModelSelectionMode.Automatic, route.SelectionMode);
        Assert.Equal(expectedModel, route.Model);
        Assert.Contains("No model was downloaded or loaded", route.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReviewTaskKind.LogTriage, ReviewEffort.Quick, "qwen3:8b")]
    [InlineData(ReviewTaskKind.EdgeCases, ReviewEffort.Quick, "qwen3:8b")]
    [InlineData(ReviewTaskKind.TestFailure, ReviewEffort.Standard, "qwen3:14b")]
    [InlineData(ReviewTaskKind.DiffReview, ReviewEffort.Standard, "qwen3:14b")]
    [InlineData(ReviewTaskKind.RepositoryAnalysis, ReviewEffort.Deep, "qwen3-coder:30b")]
    public void AutomaticTaskKindMapsToConservativeEffortAndModel(
        ReviewTaskKind taskKind,
        ReviewEffort expectedEffort,
        string expectedModel)
    {
        var route = new TaskAwareModelRouter().Plan(
            new ReviewRequest("Review.", TaskKind: taskKind),
            AutomaticState(),
            Catalog(),
            Rtx3090(),
            InstalledQwenModels(),
            ValidationRegistry(InstalledQwenModels()));

        Assert.True(route.Allowed, route.Reason);
        Assert.Equal(expectedEffort, route.Effort);
        Assert.Equal(expectedModel, route.Model);
    }

    [Fact]
    public void PassiveRoutingCapsDeepContextToAuditedModelLimit()
    {
        var route = new TaskAwareModelRouter().Plan(
            new ReviewRequest(
                "Review a repository inventory.",
                TaskKind: ReviewTaskKind.RepositoryAnalysis,
                Effort: ReviewEffort.Deep,
                DesiredContextTokens: 131_072),
            AutomaticState(),
            Catalog(),
            Rtx3090(),
            InstalledQwenModels(),
            ValidationRegistry(InstalledQwenModels()));

        Assert.True(route.Allowed);
        Assert.Equal(32_768, route.ContextTokens);
    }

    [Fact]
    public void ContextAbove64KRequiresExplicitExperimentalPreferenceEvenWhenModelAllowsIt()
    {
        var entry = Catalog().Models.Single(model => model.Tag == "qwen3:14b") with
        {
            MaximumContextTokens = 131_072
        };
        var catalog = Catalog() with { Models = [entry] };
        var request = new ReviewRequest(
            "Review a long supplied context.",
            Effort: ReviewEffort.Deep,
            DesiredContextTokens: 131_072);

        var safe = new TaskAwareModelRouter().Plan(
            request,
            AutomaticState(),
            catalog,
            Rtx3090(),
            [Installed("qwen3:14b")],
            ValidationRegistry([Installed("qwen3:14b")]));

        Assert.True(safe.Allowed);
        Assert.Equal(65_536, safe.ContextTokens);
        Assert.Contains(safe.Warnings, warning => warning.Contains("capped", StringComparison.Ordinal));

        var experimentalState = AutomaticState() with
        {
            Preferences = AutomaticState().Preferences with { AllowExperimentalRuntimeOverrides = true }
        };
        var experimental = new TaskAwareModelRouter().Plan(
            request,
            experimentalState,
            catalog,
            Rtx3090(),
            [Installed("qwen3:14b")],
            ValidationRegistry([Installed("qwen3:14b")]));

        Assert.True(experimental.Allowed);
        Assert.Equal(131_072, experimental.ContextTokens);
    }

    [Fact]
    public void ActiveGpuWorkloadForcesSmallestSafeModelAndQuickEffort()
    {
        var route = new TaskAwareModelRouter().Plan(
            new ReviewRequest(
                "Review a large log.",
                Effort: ReviewEffort.Deep,
                GpuIntensiveWorkloadActive: true),
            AutomaticState(),
            Catalog(),
            Rtx3090(),
            InstalledQwenModels(),
            ValidationRegistry(InstalledQwenModels()));

        Assert.True(route.Allowed);
        Assert.Equal("qwen3:8b", route.Model);
        Assert.Equal(ReviewEffort.Quick, route.Effort);
        Assert.Single(route.Warnings);
    }

    [Fact]
    public void AutomaticRoutingRejectsDigestMismatchAndNonQ4Model()
    {
        var installed = new[]
        {
            new OllamaModelInfo("qwen3:8b", "sha256:wrong", null, "qwen3", "8.2B", "Q4_K_M"),
            new OllamaModelInfo("qwen3:14b", Digest("qwen3:14b"), null, "qwen3", "14.8B", "Q8_0")
        };

        var route = new TaskAwareModelRouter().Plan(
            new ReviewRequest("Review."),
            AutomaticState(),
            Catalog(),
            Rtx3090(),
            installed);

        Assert.False(route.Allowed);
        Assert.Null(route.Model);
        Assert.Contains("digest-matching", route.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void VramReserveCanRefuseAllAutomaticCandidates()
    {
        var constrained = Rtx3090() with
        {
            Gpus =
            [
                Rtx3090().Gpus[0] with
                {
                    AvailableDedicatedMemoryBytes = FixtureFactory.Bytes(7.5)
                }
            ]
        };

        var route = new TaskAwareModelRouter().Plan(
            new ReviewRequest("Review."),
            AutomaticState(),
            Catalog(),
            constrained,
            InstalledQwenModels(),
            ValidationRegistry(InstalledQwenModels()));

        Assert.False(route.Allowed);
    }

    [Fact]
    public void PinnedModeDoesNotSilentlySwitchModels()
    {
        var state = AutomaticState() with
        {
            SelectedModel = "qwen3:14b",
            SelectedModelDigest = Digest("qwen3:14b"),
            Preferences = AutomaticState().Preferences with { ModelSelectionMode = ModelSelectionMode.Pinned }
        };

        var route = new TaskAwareModelRouter().Plan(
            new ReviewRequest("Small review.", Effort: ReviewEffort.Quick),
            state,
            Catalog(),
            Rtx3090(),
            InstalledQwenModels(),
            ValidationRegistry(InstalledQwenModels()));

        Assert.True(route.Allowed);
        Assert.Equal("qwen3:14b", route.Model);
        Assert.Equal(ModelSelectionMode.Pinned, route.SelectionMode);
    }

    [Fact]
    public void PinnedModeRequiresCurrentValidationForTheExactInstalledDigest()
    {
        var state = AutomaticState() with
        {
            SelectedModel = "qwen3:14b",
            SelectedModelDigest = Digest("qwen3:14b"),
            Preferences = AutomaticState().Preferences with { ModelSelectionMode = ModelSelectionMode.Pinned }
        };

        var route = new TaskAwareModelRouter().Plan(
            new ReviewRequest("Review."),
            state,
            Catalog(),
            Rtx3090(),
            InstalledQwenModels(),
            ModelValidationRegistry.Empty);

        Assert.False(route.Allowed);
        Assert.Contains("current full-digest validation", route.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedModeRejectsWrongFullDigestStaleProtocolAndWrongTagValidation()
    {
        var state = AutomaticState() with
        {
            SelectedModel = "qwen3:14b",
            SelectedModelDigest = Digest("qwen3:14b"),
            Preferences = AutomaticState().Preferences with { ModelSelectionMode = ModelSelectionMode.Pinned }
        };
        var installed = InstalledQwenModels();
        var fullDigest = installed.Single(model => model.Name == "qwen3:14b").Digest!;
        var wrongSuffix = fullDigest[..^1] + (fullDigest[^1] == '0' ? "1" : "0");
        var staleProtocol = Validation("qwen3:14b", fullDigest) with
        {
            ProtocolVersion = ModelValidationStore.CurrentProtocolVersion + 1
        };
        var registries = new[]
        {
            new ModelValidationRegistry(ModelValidationStore.SchemaVersion, [Validation("qwen3:14b", wrongSuffix)]),
            new ModelValidationRegistry(ModelValidationStore.SchemaVersion, [staleProtocol]),
            new ModelValidationRegistry(ModelValidationStore.SchemaVersion, [Validation("qwen3:8b", fullDigest)])
        };

        foreach (var registry in registries)
        {
            var route = new TaskAwareModelRouter().Plan(
                new ReviewRequest("Review."),
                state,
                Catalog(),
                Rtx3090(),
                installed,
                registry);

            Assert.False(route.Allowed);
            Assert.Contains("current full-digest validation", route.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PinnedModeStillRequiresTheCatalogDigestWhenStateAndInventoryAgreeOnWrongDigest()
    {
        var state = AutomaticState() with
        {
            SelectedModel = "qwen3:14b",
            SelectedModelDigest = "aaaaaaaaaaaa",
            Preferences = AutomaticState().Preferences with { ModelSelectionMode = ModelSelectionMode.Pinned }
        };
        var installed = new[]
        {
            new OllamaModelInfo(
                "qwen3:14b",
                "sha256:" + new string('a', 64),
                null,
                "qwen3",
                "14.8B",
                "Q4_K_M")
        };

        var route = new TaskAwareModelRouter().Plan(
            new ReviewRequest("Review."),
            state,
            Catalog(),
            Rtx3090(),
            installed,
            new ModelValidationRegistry(
                ModelValidationStore.SchemaVersion,
                [Validation("qwen3:14b", installed[0].Digest!)]));

        Assert.False(route.Allowed);
        Assert.Equal(
            Catalog().Models.Single(model => model.Tag == "qwen3:14b").ExpectedDigest,
            route.ExpectedDigest);
    }

    [Fact]
    public void GpuIntensiveWorkRefusesLargePinnedModelWithoutSilentlySwitching()
    {
        var state = AutomaticState() with
        {
            SelectedModel = "qwen3:14b",
            SelectedModelDigest = Digest("qwen3:14b"),
            Preferences = AutomaticState().Preferences with { ModelSelectionMode = ModelSelectionMode.Pinned }
        };

        var route = new TaskAwareModelRouter().Plan(
            new ReviewRequest("Review.", GpuIntensiveWorkloadActive: true),
            state,
            Catalog(),
            Rtx3090(),
            InstalledQwenModels(),
            ValidationRegistry(InstalledQwenModels()));

        Assert.False(route.Allowed);
        Assert.Equal("qwen3:14b", route.Model);
        Assert.Contains("pinned model is too large", route.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(route.Warnings, warning => warning.Contains("automatic routing", StringComparison.Ordinal));
    }

    [Fact]
    public void AutomaticRoutingRequiresCurrentValidationForTheExactInstalledDigest()
    {
        var installed = InstalledQwenModels();
        var current = Validation("qwen3:14b", installed.Single(model => model.Name == "qwen3:14b").Digest!);
        var staleDigest = Validation("qwen3:8b", "sha256:" + new string('a', 64));
        var staleProtocol = Validation("qwen3-coder:30b", installed.Single(model => model.Name == "qwen3-coder:30b").Digest!) with
        {
            ProtocolVersion = ModelValidationStore.CurrentProtocolVersion + 1
        };
        var registry = new ModelValidationRegistry(ModelValidationStore.SchemaVersion, [current, staleDigest, staleProtocol]);

        var route = new TaskAwareModelRouter().Plan(
            new ReviewRequest("Review.", Effort: ReviewEffort.Deep),
            AutomaticState(),
            Catalog(),
            Rtx3090(),
            installed,
            registry);

        Assert.True(route.Allowed, route.Reason);
        Assert.Equal("qwen3:14b", route.Model);
        Assert.Equal(1, new TaskAwareModelRouter().CountEligibleInstalledModels(
            AutomaticState(), Catalog(), Rtx3090(), installed, registry));
    }

    [Fact]
    public void AutomaticRoutingWithValidationRegistryHandlesZeroAndMultiplePasses()
    {
        var installed = InstalledQwenModels();
        var router = new TaskAwareModelRouter();
        var none = router.Plan(
            new ReviewRequest("Review."), AutomaticState(), Catalog(), Rtx3090(), installed, ModelValidationRegistry.Empty);
        var multiple = new ModelValidationRegistry(ModelValidationStore.SchemaVersion,
        [
            Validation("qwen3:8b", installed[0].Digest!),
            Validation("qwen3:14b", installed[1].Digest!)
        ]);
        var standard = router.Plan(
            new ReviewRequest("Review.", Effort: ReviewEffort.Standard),
            AutomaticState(), Catalog(), Rtx3090(), installed, multiple);

        Assert.False(none.Allowed);
        Assert.True(standard.Allowed, standard.Reason);
        Assert.Equal("qwen3:14b", standard.Model);
    }

    [Theory]
    [InlineData(ReviewEffort.Quick, false, "Ollama", "qwen3:8b")]
    [InlineData(ReviewEffort.Standard, false, "LM Studio", "qwythos-9b-claude-mythos-5-1m")]
    [InlineData(ReviewEffort.Deep, false, "LM Studio", "qwythos-9b-claude-mythos-5-1m")]
    [InlineData(ReviewEffort.Deep, true, "Ollama", "qwen3:8b")]
    public void AutomaticRoutingUsesExplicitCrossProviderPolicy(
        ReviewEffort effort,
        bool gpuBusy,
        string expectedProvider,
        string expectedModel)
    {
        var q = Catalog().Models.Single(model => model.Provider == ModelProviders.LmStudio);
        var installed = InstalledQwenModels().Append(new OllamaModelInfo(
            q.Tag, q.ExpectedDigest, q.ExpectedDownloadBytes, q.Family, "9B", "BF16",
            ModelProviders.LmStudio, q.IndexedModelPath)).ToArray();
        var validations = ValidationRegistry(installed);
        var state = AutomaticState() with
        {
            Preferences = AutomaticState().Preferences with { PreferLmStudioForStandardAndDeep = true }
        };

        var route = new TaskAwareModelRouter().Plan(
            new ReviewRequest("Review.", Effort: effort, GpuIntensiveWorkloadActive: gpuBusy),
            state, Catalog(), Rtx3090(), installed, validations);

        Assert.True(route.Allowed, route.Reason);
        Assert.Equal(expectedProvider, route.Provider);
        Assert.Equal(expectedModel, route.Model);
    }

    [Fact]
    public void ProviderSpecificValidationCannotAuthorizeSameNamedModelInAnotherProvider()
    {
        var digest = new string('a', 64);
        var registry = new ModelValidationRegistry(ModelValidationStore.SchemaVersion,
            [Validation("collision:model", digest)]);

        Assert.True(registry.HasCurrentPass(ModelProviders.Ollama, "collision:model", digest));
        Assert.False(registry.HasCurrentPass(ModelProviders.LmStudio, "collision:model", digest));
    }

    private static InstallationState AutomaticState() => new()
    {
        Availability = HelperAvailability.Enabled,
        Preferences = new HelperPreferences(ModelSelectionMode: ModelSelectionMode.Automatic)
    };

    private static ModelManifest Catalog() => new ModelCatalogService().LoadBundled();

    private static HardwareProfile Rtx3090()
        => FixtureFactory.Create(FixtureFactory.LoadHardwareFixtures().Single(item => item.Name == "nvidia-rtx3090-24gb"));

    private static IReadOnlyList<OllamaModelInfo> InstalledQwenModels()
        => [
            Installed("qwen3:8b"),
            Installed("qwen3:14b"),
            Installed("qwen3-coder:30b")
        ];

    private static OllamaModelInfo Installed(string tag)
        => new(tag, Digest(tag), null, "qwen3", null, "Q4_K_M");

    private static string Digest(string tag)
        => "sha256:" + Catalog().Models.Single(model => model.Tag == tag).ExpectedDigest + new string('0', 52);

    private static ModelValidationEntry Validation(string tag, string digest)
        => new(
            tag,
            digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : digest,
            ModelValidationStore.CurrentProtocolVersion,
            DateTimeOffset.UtcNow,
            1,
            1,
            "GPU",
            1024,
            2048);

    private static ModelValidationRegistry ValidationRegistry(IReadOnlyList<OllamaModelInfo> installed)
        => new(
            ModelValidationStore.SchemaVersion,
            installed.Select(model => Validation(model.Name, model.Digest!) with { Provider = model.Provider }).ToArray());
}
