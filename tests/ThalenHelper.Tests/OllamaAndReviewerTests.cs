using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class OllamaAndReviewerTests
{
    [Theory]
    [InlineData("http://192.168.1.10:11434")]
    [InlineData("https://127.0.0.1:11434")]
    [InlineData("http://user@127.0.0.1:11434")]
    [InlineData("http://127.0.0.1:11434/api")]
    [InlineData("http://127.0.0.1:11434/?token=secret")]
    [InlineData("http://127.0.0.1:11434/#fragment")]
    public void ClientRejectsUnsafeBaseUris(string value)
    {
        Assert.Throws<ArgumentException>(() => new OllamaClient(new Uri(value)));
    }

    [Theory]
    [InlineData("qwen3-coder")]
    [InlineData("QWEN:7b")]
    [InlineData("../escape:latest")]
    [InlineData("model:tag?query")]
    public void InvalidModelIdentifiersAreRejected(string value)
    {
        Assert.Throws<OllamaException>(() => OllamaClient.ValidateModelIdentifier(value));
    }

    [Fact]
    public async Task GenerationIsBoundedNonStreamingAndDoesNotKeepModelWarm()
    {
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(FakeHttpMessageHandler.Json(
            "{\"model\":\"qwen2.5-coder:1.5b\",\"response\":\"<think>hidden</think> FINDING\",\"done\":true,\"eval_count\":3}")));
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), http);

        var result = await client.GenerateAsync(
            "qwen2.5-coder:1.5b",
            "Inspect supplied text only.",
            2_048,
            128,
            TimeSpan.Zero);

        Assert.Equal("FINDING", result.Response);
        var requestBody = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.False(requestBody.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(requestBody.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal("0s", requestBody.RootElement.GetProperty("keep_alive").GetString());
        Assert.Equal(2_048, requestBody.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(128, requestBody.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public async Task NamedInferenceLockSupportsImmediateSkipAndBoundedQueue()
    {
        var first = await GpuCoordination.AcquireAsync(
            ReviewBusyBehavior.Queue,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        try
        {
            var skipped = await Assert.ThrowsAsync<OllamaException>(() =>
                GpuCoordination.AcquireAsync(
                    ReviewBusyBehavior.Skip,
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None));
            Assert.Equal("REVIEW_BUSY_SKIPPED", skipped.Code);

            var queued = GpuCoordination.AcquireAsync(
                ReviewBusyBehavior.Queue,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
            await Task.Delay(75);
            Assert.False(queued.IsCompleted);
            first.Dispose();
            using var second = await queued;
        }
        finally
        {
            first.Dispose();
        }
    }

    [Fact]
    public async Task NamedInferenceLockTimesOutInsteadOfQueuingIndefinitely()
    {
        using var first = await GpuCoordination.AcquireAsync(
            ReviewBusyBehavior.Queue,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        var timedOut = await Assert.ThrowsAsync<OllamaException>(() =>
            GpuCoordination.AcquireAsync(
                ReviewBusyBehavior.Queue,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));

        Assert.Equal("REVIEW_QUEUE_TIMEOUT", timedOut.Code);
    }

    [Fact]
    public async Task NamedInferenceLockIsSharedWithAnotherWindowsProcess()
    {
        var script = "$s=[System.Threading.Semaphore]::new(1,1,'Local\\CodexGpuThalenHelperInference');"
            + "$null=$s.WaitOne();[Console]::Out.WriteLine('READY');[Console]::Out.Flush();"
            + "$null=[Console]::In.ReadLine();$null=$s.Release();$s.Dispose()";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start cross-process semaphore fixture.");
        try
        {
            Assert.Equal("READY", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var skipped = await Assert.ThrowsAsync<OllamaException>(() =>
                GpuCoordination.AcquireAsync(
                    ReviewBusyBehavior.Skip,
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None));
            Assert.Equal("REVIEW_BUSY_SKIPPED", skipped.Code);

            await process.StandardInput.WriteLineAsync(string.Empty);
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            using var acquired = await GpuCoordination.AcquireAsync(
                ReviewBusyBehavior.Skip,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task CancelledQueueAndFailedPrecheckReleaseTheInferenceLock()
    {
        using (var first = await GpuCoordination.AcquireAsync(
            ReviewBusyBehavior.Queue,
            TimeSpan.FromSeconds(2),
            CancellationToken.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                GpuCoordination.AcquireAsync(
                    ReviewBusyBehavior.Queue,
                    TimeSpan.FromSeconds(2),
                    cancellation.Token));
        }

        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json(
            "{\"model\":\"qwen2.5-coder:1.5b\",\"response\":\"OK\",\"done\":true}")));
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), http);
        var precheck = await Assert.ThrowsAsync<OllamaException>(() => client.GenerateAsync(
            "qwen2.5-coder:1.5b",
            "Inspect.",
            2_048,
            64,
            TimeSpan.Zero,
            busyBehavior: ReviewBusyBehavior.Skip,
            preGenerationCheck: _ => throw new OllamaException("PRESSURE_TEST", "Refused.")));
        Assert.Equal("PRESSURE_TEST", precheck.Code);

        var succeeded = await client.GenerateAsync(
            "qwen2.5-coder:1.5b",
            "Inspect.",
            2_048,
            64,
            TimeSpan.Zero,
            busyBehavior: ReviewBusyBehavior.Skip);

        Assert.Equal("OK", succeeded.Response);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PressurePrecheckDoesNotRunUntilInferenceLockIsAcquired()
    {
        using var first = await GpuCoordination.AcquireAsync(
            ReviewBusyBehavior.Queue,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        var precheckRan = false;
        using var http = new HttpClient(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("{}"))));
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), http);

        var timedOut = await Assert.ThrowsAsync<OllamaException>(() => client.GenerateAsync(
            "qwen2.5-coder:1.5b",
            "Inspect.",
            2_048,
            64,
            TimeSpan.Zero,
            busyBehavior: ReviewBusyBehavior.Queue,
            queueTimeout: TimeSpan.FromMilliseconds(50),
            preGenerationCheck: _ =>
            {
                precheckRan = true;
                return Task.CompletedTask;
            }));

        Assert.Equal("REVIEW_QUEUE_TIMEOUT", timedOut.Code);
        Assert.False(precheckRan);
    }

    [Fact]
    public async Task ClientRejectsOversizedMalformedAndErrorResponses()
    {
        var oversized = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Enumerable.Repeat((byte)'x', 2 * 1024 * 1024 + 1).ToArray())
        }));
        using (var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(oversized)))
        {
            var error = await Assert.ThrowsAsync<OllamaException>(() => client.GetModelsAsync());
            Assert.Equal("OLLAMA_RESPONSE_TOO_LARGE", error.Code);
        }

        var malformed = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json("not-json")));
        using (var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(malformed)))
        {
            var error = await Assert.ThrowsAsync<OllamaException>(() => client.GetModelsAsync());
            Assert.Equal("OLLAMA_MALFORMED_RESPONSE", error.Code);
        }

        var missing = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)));
        using (var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(missing)))
        {
            var error = await Assert.ThrowsAsync<OllamaException>(() => client.GetModelsAsync());
            Assert.Equal("OLLAMA_API_NOT_FOUND", error.Code);
        }
    }

    [Fact]
    public async Task HealthIsPassiveAndNeverCallsGenerate()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Enabled
        });
        var handler = InventoryHandler();
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));

        var health = await new ReviewerService(store, client, _ => true, StorageOk).GetHealthAsync();

        Assert.True(health.EndpointReachable);
        Assert.True(health.ModelAvailable);
        Assert.False(health.ModelLoaded);
        Assert.False(health.ModelRan);
        Assert.Equal(["/api/tags", "/api/ps"], handler.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task ExposedListenerBlocksHealthWithoutAnyOllamaRequest()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Enabled
        });
        var handler = InventoryHandler();
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));

        var health = await new ReviewerService(store, client, _ => false).GetHealthAsync();

        Assert.Equal("OLLAMA_NETWORK_EXPOSURE", health.ErrorCode);
        Assert.False(health.ModelRan);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListenerDriftBeforeGenerationBlocksReview()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Enabled
        });
        var handler = InventoryHandler();
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var checks = 0;

        var result = await new ReviewerService(store, client, _ => ++checks == 1, StorageOk)
            .ReviewAsync(new ReviewRequest("Inspect."));

        Assert.Equal("OLLAMA_NETWORK_EXPOSURE", result.ErrorCode);
        Assert.False(result.ModelRan);
        Assert.Equal(["/api/tags"], handler.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task DigestMismatchBlocksReviewBeforeGeneration()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Enabled
        });
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json(
            "{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]}")));
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));

        var result = await new ReviewerService(store, client, _ => true, StorageOk)
            .ReviewAsync(new ReviewRequest("Inspect."));

        Assert.Equal("MODEL_DIGEST_MISMATCH", result.ErrorCode);
        Assert.False(result.ModelRan);
        Assert.Equal(["/api/tags"], handler.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task ModelStorageDriftBlocksReviewWithoutAnyOllamaRequest()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Enabled
        });
        var handler = InventoryHandler();
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));

        var result = await new ReviewerService(
            store,
            client,
            _ => true,
            _ => new ReviewerModelStorageVerification(false, "MODEL_PATH_NOT_CONFIGURED", "Path drift."))
            .ReviewAsync(new ReviewRequest("Inspect."));

        Assert.Equal("MODEL_PATH_NOT_CONFIGURED", result.ErrorCode);
        Assert.False(result.ModelRan);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ModelStorageDriftBeforeGenerationBlocksAfterInventory()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Enabled
        });
        var handler = InventoryHandler();
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var checks = 0;

        var result = await new ReviewerService(
            store,
            client,
            _ => true,
            _ => ++checks == 1
                ? new ReviewerModelStorageVerification(true, "OK", "Storage verified.")
                : new ReviewerModelStorageVerification(false, "MODEL_NOT_IN_CONFIGURED_PATH", "Manifest drift."))
            .ReviewAsync(new ReviewRequest("Inspect."));

        Assert.Equal("MODEL_NOT_IN_CONFIGURED_PATH", result.ErrorCode);
        Assert.False(result.ModelRan);
        Assert.Equal(["/api/tags"], handler.Requests.Select(request => request.Path));
    }

    [Fact]
    public void DefaultModelStorageValidationRequiresExactPathAndManifest()
    {
        using var temporary = new TemporaryDirectory();
        var modelDirectory = Path.Combine(temporary.Path, "Models with spaces");
        var state = new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            ModelStorageLocation = modelDirectory
        };

        Assert.Equal(
            "MODEL_PATH_NOT_CONFIGURED",
            ReviewerService.ValidateModelStorage(state, Path.Combine(temporary.Path, "Other")).Code);
        Assert.Equal(
            "MODEL_NOT_IN_CONFIGURED_PATH",
            ReviewerService.ValidateModelStorage(state, modelDirectory).Code);

        var manifest = Path.Combine(
            modelDirectory,
            "manifests",
            "registry.ollama.ai",
            "library",
            "qwen2.5-coder",
            "1.5b");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "manifest");

        Assert.True(ReviewerService.ValidateModelStorage(state, modelDirectory).Success);
    }

    [Theory]
    [InlineData(HelperAvailability.Paused, "PAUSED")]
    [InlineData(HelperAvailability.Disabled, "DISABLED")]
    public async Task PausedOrDisabledReviewDoesNotCallOllama(HelperAvailability availability, string code)
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = availability
        });
        var handler = InventoryHandler();
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));

        var result = await new ReviewerService(store, client).ReviewAsync(new ReviewRequest("Inspect."));

        Assert.False(result.ModelRan);
        Assert.Equal(code, result.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WorkloadGuardBlocksReviewWithoutInference()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Enabled
        });
        File.WriteAllText(Path.Combine(paths.StateDirectory, "gpu-blocked"), "device testing");
        var handler = InventoryHandler();
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));

        var result = await new ReviewerService(store, client).ReviewAsync(new ReviewRequest("Inspect."));

        Assert.Equal("GPU_RESOURCE_BLOCKED", result.ErrorCode);
        Assert.False(result.ModelRan);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AutomaticResourcePressureGuardBlocksBeforeGeneration()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Enabled
        });
        var handler = InventoryHandler();
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var reviewer = new ReviewerService(
            store,
            client,
            _ => true,
            StorageOk,
            (_, _) => new ResourcePressureCheck(
                false,
                "WINDOWS_COMMIT_PRESSURE",
                "Commit pressure is high."));

        var result = await reviewer.ReviewAsync(new ReviewRequest("Inspect."));

        Assert.Equal("WINDOWS_COMMIT_PRESSURE", result.ErrorCode);
        Assert.False(result.ModelRan);
        Assert.Equal(["/api/tags", "/api/ps"], handler.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task ModelValidationAlsoRefusesResourcePressureBeforeInference()
    {
        var handler = InventoryHandler();
        var manager = new InstallationManager(
            clientFactory: () => new OllamaClient(
                new Uri("http://127.0.0.1:11434"),
                new HttpClient(handler)),
            resourcePressureValidator: (_, _) => new ResourcePressureCheck(
                false,
                "GPU_MEMORY_PRESSURE",
                "VRAM pressure is high."));
        var state = new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Disabled
        };

        var result = await manager.ValidateSelectedModelForTestingAsync(state);

        Assert.False(result.Success);
        Assert.Equal("GPU_MEMORY_PRESSURE", result.Code);
        Assert.Equal(["/api/tags", "/api/ps"], handler.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task ReviewerValidatesInputBeforeCallingOllama()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Enabled
        });
        var handler = InventoryHandler();
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var reviewer = new ReviewerService(store, client, _ => true, StorageOk);

        await Assert.ThrowsAsync<ArgumentException>(() => reviewer.ReviewAsync(new ReviewRequest(new string('x', 12_001))));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AutomaticPlanIsPassiveAndReviewUsesItsTaskAwareModelInsideTheLock()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen3:8b",
            SelectedModelDigest = "500a1f067a9f",
            HardwareTier = HardwareTier.Mid,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Enabled,
            Preferences = new HelperPreferences(ModelSelectionMode: ModelSelectionMode.Automatic)
        });
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(request.RequestUri?.AbsolutePath switch
        {
            "/api/tags" => FakeHttpMessageHandler.Json("""
                {"models":[
                  {"name":"qwen3:8b","digest":"sha256:500a1f067a9f0000000000000000000000000000000000000000000000000000","details":{"quantization_level":"Q4_K_M"}},
                  {"name":"qwen3:14b","digest":"sha256:bdbd181c33f20000000000000000000000000000000000000000000000000000","details":{"quantization_level":"Q4_K_M"}}
                ]}
                """),
            "/api/ps" => FakeHttpMessageHandler.Json("{\"models\":[]}"),
            "/api/generate" => FakeHttpMessageHandler.Json("{\"model\":\"qwen3:14b\",\"response\":\"ROUTED_OK\",\"done\":true}"),
            _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
        }));
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var hardware = FixtureFactory.Create(
            FixtureFactory.LoadHardwareFixtures().Single(item => item.Name == "nvidia-rtx3090-24gb"));
        var reviewer = new ReviewerService(
            store,
            client,
            _ => true,
            StorageOk,
            (_, _) => new ResourcePressureCheck(true, "OK", "Safe."),
            router: new TaskAwareModelRouter(),
            catalogProvider: () => new ModelCatalogService().LoadBundled(),
            hardwareProvider: () => hardware);

        var request = new ReviewRequest("Review a multi-file diff.", Effort: ReviewEffort.Standard);
        var plan = await reviewer.PlanAsync(request);

        Assert.True(plan.Allowed, plan.ErrorMessage);
        Assert.True(plan.Passive);
        Assert.False(plan.ModelRan);
        Assert.Equal("qwen3:14b", plan.Model);
        Assert.Equal("Q4 required for automatic routing", plan.Tuning?.Quantization);
        Assert.DoesNotContain(handler.Requests, item => item.Path == "/api/generate");

        var result = await reviewer.ReviewAsync(request);

        Assert.True(result.ModelRan, result.ErrorMessage);
        Assert.Equal("qwen3:14b", result.Model);
        Assert.Equal(ModelSelectionMode.Automatic, result.SelectionMode);
        Assert.Equal(ReviewEffort.Standard, result.Effort);
        Assert.Equal(16_384, result.ContextTokens);
        var body = JsonDocument.Parse(handler.Requests.Single(item => item.Path == "/api/generate").Body!);
        Assert.Equal("qwen3:14b", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(16_384, body.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal("0s", body.RootElement.GetProperty("keep_alive").GetString());
    }

    [Fact]
    public async Task StateStoreRoundTripsAtomically()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        var state = new InstallationState
        {
            SelectedModel = "qwen2.5-coder:7b",
            HardwareTier = HardwareTier.Mid,
            Availability = HelperAvailability.Paused
        };

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        Assert.Equal(state.SelectedModel, loaded?.SelectedModel);
        Assert.Equal(state.Availability, loaded?.Availability);
        Assert.False(File.Exists(paths.StateFile + ".tmp"));
        Assert.DoesNotContain(Environment.UserName, await File.ReadAllTextAsync(paths.StateFile), StringComparison.OrdinalIgnoreCase);
    }

    private static FakeHttpMessageHandler InventoryHandler()
        => new((request, _) => Task.FromResult(request.RequestUri?.AbsolutePath switch
        {
            "/api/tags" => FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\",\"size\":100}]}"),
            "/api/ps" => FakeHttpMessageHandler.Json("{\"models\":[]}"),
            _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
        }));

    private static ReviewerModelStorageVerification StorageOk(InstallationState _)
        => new(true, "OK", "Storage verified.");
}
