using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class LmStudioReviewerLifecycleTests
{
    [Fact]
    public async Task LegacyRegistrationWithoutFileIdentityIsNotPlannable()
    {
        using var fixture = await ReviewerFixture.CreateAsync();
        var state = (await fixture.Store.LoadAsync())!;
        state.RegisteredLocalModels = [state.RegisteredLocalModels.Single() with { FileIdentity = null }];
        await fixture.Store.SaveAsync(state);

        var plan = await fixture.Reviewer.PlanAsync(Request());

        Assert.False(plan.Allowed);
        Assert.False(plan.ModelRan);
        Assert.Empty(fixture.Cli.DownloadedProofs);
        Assert.DoesNotContain(fixture.LmHttp.Requests, item => item.Path == "/api/v1/models/load");
    }

    [Fact]
    public async Task StaleFileIdentityIsNotPlannableAndNeverReachesCliOrRuntime()
    {
        using var fixture = await ReviewerFixture.CreateAsync();
        var state = (await fixture.Store.LoadAsync())!;
        state.RegisteredLocalModels =
        [
            state.RegisteredLocalModels.Single() with
            {
                FileIdentity = "00000000:0000000000000000:0000000000000000"
            }
        ];
        await fixture.Store.SaveAsync(state);

        var plan = await fixture.Reviewer.PlanAsync(Request());

        Assert.False(plan.Allowed);
        Assert.False(plan.ModelRan);
        Assert.Empty(fixture.Cli.DownloadedProofs);
        Assert.DoesNotContain(fixture.LmHttp.Requests, item => item.Path == "/api/v1/models/load");
    }

    [Fact]
    public async Task ReviewBindsFullDigestAndExactCliPathThenUnloadsOnlyReturnedInstance()
    {
        using var fixture = await ReviewerFixture.CreateAsync(attemptWriteDuringGeneration: true);

        var plan = await fixture.Reviewer.PlanAsync(Request());
        var result = await fixture.Reviewer.ReviewAsync(Request());

        Assert.True(plan.Allowed, plan.ErrorMessage);
        Assert.Equal(ModelProviders.LmStudio, plan.Provider);
        Assert.True(result.ModelRan, result.ErrorMessage);
        Assert.Equal(ModelProviders.LmStudio, result.Provider);
        Assert.Equal("SAFE_REVIEW", result.Findings);
        Assert.False(fixture.Rest.WriteSucceededDuringGeneration);
        Assert.True(fixture.Rest.PathLeaseHeldDuringGeneration);
        Assert.True(fixture.Cli.DownloadedProofs.Count >= 3);
        Assert.All(fixture.Cli.DownloadedProofs, proof =>
            Assert.Equal(fixture.Model.ExpectedDownloadBytes, checked((ulong)proof.Length)));
        Assert.Single(fixture.Cli.LoadedProofs);
        Assert.Equal(fixture.Model.IndexedModelPath, fixture.Cli.LoadedProofs[0].IndexedPath);
        Assert.Equal(ReviewerFixture.InstanceId, fixture.Cli.LoadedProofs[0].InstanceId);
        Assert.Equal(
            [(ReviewerFixture.InstanceId, fixture.Model.IndexedModelPath!)],
            fixture.Cli.UnloadedInstances);
        Assert.Equal([ReviewerFixture.InstanceId], fixture.Rest.UnloadedInstances);
        Assert.False(fixture.Rest.HelperLoaded);
        Assert.False(File.Exists(new ActiveModelTracker(fixture.Paths.StateDirectory).Path));
    }

    [Fact]
    public async Task FreshLmStudioOnlyHealthPlanAndReviewDoNotRequireOllamaConfigurationOrEndpoint()
    {
        using var fixture = await ReviewerFixture.CreateAsync(
            modelSelectionMode: ModelSelectionMode.Automatic,
            ollamaUnavailable: true);
        var state = (await fixture.Store.LoadAsync())!;
        Assert.Null(state.ModelStorageLocation);
        Assert.Equal(ModelProviders.LmStudio, state.SelectedModelProvider);

        var health = await fixture.Reviewer.GetHealthAsync();
        var plan = await fixture.Reviewer.PlanAsync(Request());
        var result = await fixture.Reviewer.ReviewAsync(Request());

        Assert.Null(health.ErrorCode);
        Assert.True(health.EndpointReachable);
        Assert.True(health.ModelAvailable);
        Assert.False(health.ModelLoaded);
        Assert.Equal("Automatic (LM Studio)", health.Provider);
        Assert.Equal("http://127.0.0.1:1234", health.Endpoint);
        Assert.True(plan.Allowed, plan.ErrorMessage);
        Assert.Equal(ModelProviders.LmStudio, plan.Provider);
        Assert.True(result.ModelRan, result.ErrorMessage);
        Assert.Equal(ModelProviders.LmStudio, result.Provider);
        Assert.Equal("SAFE_REVIEW", result.Findings);
        Assert.Empty(fixture.OllamaHttp.Requests);
        Assert.Equal([ReviewerFixture.InstanceId], fixture.Rest.UnloadedInstances);
        Assert.Equal(
            [(ReviewerFixture.InstanceId, fixture.Model.IndexedModelPath!)],
            fixture.Cli.UnloadedInstances);
        Assert.False(fixture.Rest.HelperLoaded);
        Assert.False(File.Exists(new ActiveModelTracker(fixture.Paths.StateDirectory).Path));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ForeignOllamaOrLmStudioModelIsRefusedWithoutAnyUnload(
        bool foreignOllama,
        bool foreignLmStudio)
    {
        using var fixture = await ReviewerFixture.CreateAsync(
            foreignOllama: foreignOllama,
            foreignLmStudio: foreignLmStudio);

        var result = await fixture.Reviewer.ReviewAsync(Request());

        Assert.False(result.ModelRan);
        Assert.Equal("FOREIGN_MODEL_LOADED", result.ErrorCode);
        Assert.DoesNotContain(fixture.LmHttp.Requests, item => item.Path == "/api/v1/models/load");
        Assert.DoesNotContain(fixture.LmHttp.Requests, item => item.Path == "/api/v1/models/unload");
        Assert.Empty(fixture.Cli.LoadedProofs);
        Assert.Empty(fixture.Cli.UnloadedInstances);
        Assert.Empty(fixture.Rest.UnloadedInstances);
    }

    [Fact]
    public async Task FullDigestMismatchInsideLeaseFailsBeforeLoadAndUnloadsNothing()
    {
        using var fixture = await ReviewerFixture.CreateAsync(registrationDigestMatchesFile: false);

        var plan = await fixture.Reviewer.PlanAsync(Request());
        var result = await fixture.Reviewer.ReviewAsync(Request());

        Assert.True(plan.Allowed, plan.ErrorMessage);
        Assert.False(result.ModelRan);
        Assert.Equal("MODEL_DIGEST_MISMATCH", result.ErrorCode);
        Assert.DoesNotContain(fixture.LmHttp.Requests, item => item.Path == "/api/v1/models/load");
        Assert.DoesNotContain(fixture.LmHttp.Requests, item => item.Path == "/api/v1/models/unload");
        Assert.Empty(fixture.Cli.LoadedProofs);
        Assert.Empty(fixture.Cli.UnloadedInstances);
    }

    [Fact]
    public async Task CliLoadedPathMismatchUnloadsOnlyHelperCreatedInstanceAndDoesNotGenerate()
    {
        using var fixture = await ReviewerFixture.CreateAsync(cliLoadedMismatch: true);

        var result = await fixture.Reviewer.ReviewAsync(Request());

        Assert.False(result.ModelRan);
        Assert.Equal("LMSTUDIO_LOADED_FILE_MISMATCH", result.ErrorCode);
        Assert.DoesNotContain(fixture.LmHttp.Requests, item => item.Path == "/api/v1/chat");
        Assert.Equal([ReviewerFixture.InstanceId], fixture.Rest.UnloadedInstances);
        Assert.Equal(
            [(ReviewerFixture.InstanceId, fixture.Model.IndexedModelPath!)],
            fixture.Cli.UnloadedInstances);
        Assert.False(fixture.Rest.HelperLoaded);
    }

    private static ReviewRequest Request()
        => new(
            "Review a bounded non-sensitive diff.",
            TaskKind: ReviewTaskKind.DiffReview,
            Effort: ReviewEffort.Standard);

    private sealed class ReviewerFixture : IDisposable
    {
        internal const string InstanceId = "thalen-review-instance";
        private readonly TemporaryDirectory _temporary;
        private readonly OllamaClient _ollama;
        private readonly LmStudioClient _lmStudio;

        private ReviewerFixture(
            TemporaryDirectory temporary,
            ProductPaths paths,
            StateStore store,
            ModelCatalogEntry model,
            RestHarness rest,
            FakeCliBinding cli,
            FakeHttpMessageHandler ollamaHttp,
            FakeHttpMessageHandler lmHttp,
            OllamaClient ollama,
            LmStudioClient lmStudio,
            ReviewerService reviewer)
        {
            _temporary = temporary;
            Paths = paths;
            Store = store;
            Model = model;
            Rest = rest;
            Cli = cli;
            OllamaHttp = ollamaHttp;
            LmHttp = lmHttp;
            _ollama = ollama;
            _lmStudio = lmStudio;
            Reviewer = reviewer;
        }

        internal ProductPaths Paths { get; }
        internal StateStore Store { get; }
        internal ModelCatalogEntry Model { get; }
        internal RestHarness Rest { get; }
        internal FakeCliBinding Cli { get; }
        internal FakeHttpMessageHandler OllamaHttp { get; }
        internal FakeHttpMessageHandler LmHttp { get; }
        internal ReviewerService Reviewer { get; }

        internal static async Task<ReviewerFixture> CreateAsync(
            bool foreignOllama = false,
            bool foreignLmStudio = false,
            bool registrationDigestMatchesFile = true,
            bool cliLoadedMismatch = false,
            bool attemptWriteDuringGeneration = false,
            ModelSelectionMode modelSelectionMode = ModelSelectionMode.Pinned,
            bool ollamaUnavailable = false)
        {
            GpuCoordination.ClearCancellation();
            var temporary = new TemporaryDirectory("lm-reviewer-lifecycle");
            try
            {
                var paths = temporary.CreatePaths();
                var indexedPath = "fixture-publisher/fixture-model/fixture-model.gguf";
                var modelPath = Path.Combine(temporary.Path, Path.Combine(indexedPath.Split('/')));
                Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
                await File.WriteAllTextAsync(modelPath, "immutable audited LM Studio fixture", Encoding.UTF8);
                string actualDigest;
                await using (var digestStream = File.OpenRead(modelPath))
                {
                    actualDigest = Convert.ToHexString(
                        await SHA256.HashDataAsync(digestStream)).ToLowerInvariant();
                }
                var catalogDigest = registrationDigestMatchesFile
                    ? actualDigest
                    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("different audited bytes"))).ToLowerInvariant();
                Assert.True(LmStudioModelFileBinding.TryOpen(modelPath, out var file, out var proof));
                file.Dispose();

                var sourceModel = new ModelCatalogService().LoadBundled().Models.Single(item =>
                    string.Equals(item.Provider, ModelProviders.LmStudio, StringComparison.Ordinal));
                var model = sourceModel with
                {
                    Tag = "fixture-model-key",
                    ExpectedDigest = catalogDigest,
                    ExpectedDownloadBytes = checked((ulong)proof.Length),
                    MinimumDedicatedVramGiB = 1,
                    RecommendedDedicatedVramGiB = 1,
                    MinimumSystemRamGiB = 1,
                    RecommendedSystemRamGiB = 1,
                    MinimumFreeDiskGiB = 1,
                    AutomaticSelectionAllowed = true,
                    NonQ4AutomaticAllowed = true,
                    IndexedModelPath = indexedPath
                };
                var manifest = new ModelManifest(1, "lm-reviewer-fixture", DateOnly.FromDateTime(DateTime.UtcNow), [model]);
                var registration = new LocalModelRegistration(
                    ModelProviders.LmStudio,
                    model.Tag,
                    catalogDigest,
                    modelPath,
                    DateTimeOffset.UtcNow,
                    proof.Length,
                    proof.LastWriteTimeUtc,
                    proof.FileIdentity);
                var store = new StateStore(paths.StateFile);
                await store.SaveAsync(new InstallationState
                {
                    SelectedModel = model.Tag,
                    SelectedModelDigest = catalogDigest,
                    SelectedModelProvider = ModelProviders.LmStudio,
                    ModelStorageLocation = foreignOllama
                        ? Path.Combine(temporary.Path, "mocked-ollama-models")
                        : null,
                    HardwareTier = HardwareTier.High,
                    ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
                    RegisteredLocalModels = [registration],
                    Availability = HelperAvailability.Enabled,
                    Preferences = new HelperPreferences(
                        ModelSelectionMode: modelSelectionMode,
                        PreferLmStudioForStandardAndDeep: true)
                });

                var validations = new ModelValidationStore(paths.StateDirectory);
                await validations.UpsertAsync(new ModelValidationEntry(
                    model.Tag,
                    catalogDigest,
                    ModelValidationStore.CurrentProtocolVersion,
                    DateTimeOffset.UtcNow,
                    1,
                    1,
                    "GPU",
                    1,
                    65_536,
                    ModelProviders.LmStudio));

                var cli = new FakeCliBinding(cliLoadedMismatch);
                var rest = new RestHarness(
                    model,
                    modelPath,
                    foreignLmStudio,
                    attemptWriteDuringGeneration,
                    () => cli.ActivePathLeases);
                var ollamaHttp = new FakeHttpMessageHandler((request, _) =>
                {
                    if (ollamaUnavailable)
                    {
                        return Task.FromException<HttpResponseMessage>(
                            new HttpRequestException("The mocked Ollama endpoint is unavailable."));
                    }

                    return Task.FromResult(request.RequestUri?.AbsolutePath switch
                    {
                        "/api/tags" => FakeHttpMessageHandler.Json("{\"models\":[]}"),
                        "/api/ps" when foreignOllama => FakeHttpMessageHandler.Json($$"""
                            {"models":[{"name":"foreign:latest","digest":"sha256:{{new string('a', 64)}}","size":1,"size_vram":0}]}
                            """),
                        "/api/ps" => FakeHttpMessageHandler.Json("{\"models\":[]}"),
                        _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
                    });
                });
                var lmHttp = new FakeHttpMessageHandler(rest.HandleAsync);
                var ollama = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(ollamaHttp));
                var lmStudio = new LmStudioClient(new Uri("http://127.0.0.1:1234"), new HttpClient(lmHttp));
                var hardware = FixtureFactory.Create(
                    FixtureFactory.LoadHardwareFixtures().Single(item => item.Name == "nvidia-rtx3090-24gb"));
                var reviewer = new ReviewerService(
                    store,
                    ollama,
                    _ => true,
                    _ => throw new InvalidOperationException("Ollama storage validation must not run for an LM Studio-only route."),
                    (_, _) => new ResourcePressureCheck(true, "OK", "Safe."),
                    router: new TaskAwareModelRouter(),
                    catalogProvider: () => manifest,
                    hardwareProvider: () => hardware,
                    validationStore: validations,
                    lmStudio: lmStudio,
                    lmStudioCliBinding: cli);
                return new ReviewerFixture(
                    temporary,
                    paths,
                    store,
                    model,
                    rest,
                    cli,
                    ollamaHttp,
                    lmHttp,
                    ollama,
                    lmStudio,
                    reviewer);
            }
            catch
            {
                temporary.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _lmStudio.Dispose();
            _ollama.Dispose();
            GpuCoordination.ClearCancellation();
            _temporary.Dispose();
        }
    }

    private sealed class RestHarness(
        ModelCatalogEntry model,
        string modelPath,
        bool foreignLmStudio,
        bool attemptWriteDuringGeneration,
        Func<int> activePathLeases)
    {
        internal bool HelperLoaded { get; private set; }
        internal bool WriteSucceededDuringGeneration { get; private set; }
        internal bool PathLeaseHeldDuringGeneration { get; private set; }
        internal List<string> UnloadedInstances { get; } = [];

        internal Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(request.RequestUri?.AbsolutePath switch
            {
                "/api/v1/models" => FakeHttpMessageHandler.Json(Inventory()),
                "/api/v1/models/load" => Load(),
                "/api/v1/chat" => Generate(),
                "/api/v1/models/unload" => Unload(),
                _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
            });
        }

        private string Inventory()
        {
            var loadedInstances = foreignLmStudio
                ? new[] { new { id = "foreign-instance", config = new { context_length = 65_536 } } }
                : HelperLoaded
                    ? new[] { new { id = ReviewerFixture.InstanceId, config = new { context_length = 65_536 } } }
                    : [];
            return JsonSerializer.Serialize(new
            {
                models = new[]
                {
                    new
                    {
                        key = model.Tag,
                        architecture = "qwen3",
                        quantization = "BF16",
                        size_bytes = model.ExpectedDownloadBytes,
                        parameter_count = 9.0,
                        max_context_length = 65_536,
                        loaded_instances = loadedInstances
                    }
                }
            });
        }

        private HttpResponseMessage Load()
        {
            HelperLoaded = true;
            return FakeHttpMessageHandler.Json(JsonSerializer.Serialize(new
            {
                instance_id = ReviewerFixture.InstanceId,
                model_key = model.Tag,
                load_config = new
                {
                    context_length = 65_536,
                    flash_attention = true,
                    offload_kv_cache_to_gpu = true
                }
            }));
        }

        private HttpResponseMessage Generate()
        {
            PathLeaseHeldDuringGeneration = activePathLeases() > 0;
            if (attemptWriteDuringGeneration)
            {
                try
                {
                    using var write = new FileStream(modelPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    WriteSucceededDuringGeneration = true;
                }
                catch (IOException)
                {
                    WriteSucceededDuringGeneration = false;
                }
            }

            return FakeHttpMessageHandler.Json(JsonSerializer.Serialize(new
            {
                model_instance_id = ReviewerFixture.InstanceId,
                output = new[] { new { type = "message", content = "SAFE_REVIEW" } },
                stats = new
                {
                    input_tokens = 12,
                    total_output_tokens = 3,
                    reasoning_output_tokens = 0
                }
            }));
        }

        private HttpResponseMessage Unload()
        {
            HelperLoaded = false;
            UnloadedInstances.Add(ReviewerFixture.InstanceId);
            return FakeHttpMessageHandler.Json(JsonSerializer.Serialize(new
            {
                instance_id = ReviewerFixture.InstanceId
            }));
        }
    }

    private sealed class FakeCliBinding(bool loadedMismatch) : ILmStudioCliModelBinding
    {
        internal int ActivePathLeases { get; private set; }
        internal List<LmStudioModelFileProof> DownloadedProofs { get; } = [];
        internal List<(string InstanceId, string IndexedPath, LmStudioModelFileProof Proof)> LoadedProofs { get; } = [];
        internal List<(string InstanceId, string IndexedPath)> UnloadedInstances { get; } = [];

        public IDisposable AcquireModelPathLease(
            string indexedPath,
            LmStudioModelFileProof expectedFile)
        {
            ActivePathLeases++;
            return new CallbackDisposable(() => ActivePathLeases--);
        }

        public Task<LmStudioCliDownloadedModel> VerifyDownloadedAsync(
            string modelKey,
            string indexedPath,
            LmStudioModelFileProof expectedFile,
            CancellationToken cancellationToken = default)
        {
            DownloadedProofs.Add(expectedFile);
            return Task.FromResult(new LmStudioCliDownloadedModel(
                modelKey,
                indexedPath,
                "llm",
                "gguf",
                checked((ulong)expectedFile.Length)));
        }

        public Task VerifyLoadedAsync(
            string instanceId,
            string indexedPath,
            LmStudioModelFileProof expectedFile,
            CancellationToken cancellationToken = default)
        {
            LoadedProofs.Add((instanceId, indexedPath, expectedFile));
            if (loadedMismatch)
            {
                throw new LmStudioException(
                    "LMSTUDIO_LOADED_FILE_MISMATCH",
                    "Mocked loaded file mismatch.");
            }
            return Task.CompletedTask;
        }

        public Task VerifyUnloadedAsync(
            string instanceId,
            string indexedPath,
            CancellationToken cancellationToken = default)
        {
            UnloadedInstances.Add((instanceId, indexedPath));
            return Task.CompletedTask;
        }
    }

    private sealed class CallbackDisposable(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose()
            => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
