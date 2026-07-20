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

    [Fact]
    public async Task GenerationRejectsAResponseForADifferentModelIdentity()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json(
            "{\"model\":\"qwen3:8b\",\"response\":\"WRONG_MODEL\",\"done\":true}")));
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), http);

        var exception = await Assert.ThrowsAsync<OllamaException>(() => client.GenerateAsync(
            "qwen3:14b",
            "Inspect supplied text only.",
            2_048,
            128,
            TimeSpan.Zero));

        Assert.Equal("MODEL_RESPONSE_IDENTITY_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task GenerationRejectsAResponseWithoutModelIdentity()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json(
            "{\"response\":\"MISSING_MODEL\",\"done\":true}")));
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), http);

        var exception = await Assert.ThrowsAsync<OllamaException>(() => client.GenerateAsync(
            "qwen3:14b",
            "Inspect supplied text only.",
            2_048,
            128,
            TimeSpan.Zero));

        Assert.Equal("OLLAMA_MALFORMED_RESPONSE", exception.Code);
    }

    [Fact]
    public async Task ReviewerRejectsMismatchedResponseIdentityWithoutReturningAdvisoryText()
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
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(request.RequestUri?.AbsolutePath switch
        {
            "/api/tags" => FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\",\"size\":100}]}"),
            "/api/ps" => FakeHttpMessageHandler.Json("{\"models\":[]}"),
            "/api/generate" => FakeHttpMessageHandler.Json("{\"model\":\"qwen3:8b\",\"response\":\"UNTRUSTED_TEXT\",\"done\":true}"),
            _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
        }));
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var validations = new ModelValidationStore(paths.StateDirectory);
        await validations.UpsertAsync(Validation("qwen2.5-coder:1.5b", "d7372fd82851"));
        var reviewer = new ReviewerService(
            store,
            client,
            _ => true,
            StorageOk,
            (_, _) => new ResourcePressureCheck(true, "OK", "Safe."),
            hardwareProvider: ReviewerHardware,
            validationStore: validations);

        var result = await reviewer.ReviewAsync(new ReviewRequest("Inspect."));

        Assert.Equal("MODEL_RESPONSE_IDENTITY_MISMATCH", result.ErrorCode);
        Assert.False(result.ModelRan);
        Assert.True(string.IsNullOrEmpty(result.Findings));
        var generation = JsonDocument.Parse(handler.Requests.Single(item => item.Path == "/api/generate").Body!);
        Assert.Equal("0s", generation.RootElement.GetProperty("keep_alive").GetString());
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
        var validations = new ModelValidationStore(paths.StateDirectory);
        await validations.UpsertAsync(Validation("qwen2.5-coder:1.5b", "d7372fd82851"));
        var checks = 0;

        var result = await new ReviewerService(
            store,
            client,
            _ => ++checks == 1,
            StorageOk,
            hardwareProvider: ReviewerHardware,
            validationStore: validations)
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

        var result = await new ReviewerService(
            store,
            client,
            _ => true,
            StorageOk,
            hardwareProvider: ReviewerHardware)
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
            _ => new ReviewerModelStorageVerification(false, "MODEL_PATH_NOT_CONFIGURED", "Path drift."),
            hardwareProvider: ReviewerHardware)
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
        var validations = new ModelValidationStore(paths.StateDirectory);
        await validations.UpsertAsync(Validation("qwen2.5-coder:1.5b", "d7372fd82851"));
        var checks = 0;

        var result = await new ReviewerService(
            store,
            client,
            _ => true,
            _ => ++checks == 1
                ? new ReviewerModelStorageVerification(true, "OK", "Storage verified.")
                : new ReviewerModelStorageVerification(false, "MODEL_NOT_IN_CONFIGURED_PATH", "Manifest drift."),
            hardwareProvider: ReviewerHardware,
            validationStore: validations)
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
        var validations = new ModelValidationStore(paths.StateDirectory);
        await validations.UpsertAsync(Validation("qwen2.5-coder:1.5b", "d7372fd82851"));
        var reviewer = new ReviewerService(
            store,
            client,
            _ => true,
            StorageOk,
            (_, _) => new ResourcePressureCheck(
                false,
                "WINDOWS_COMMIT_PRESSURE",
                "Commit pressure is high."),
            hardwareProvider: ReviewerHardware,
            validationStore: validations);

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
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/api/generate");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(4_000_000_000L)]
    public async Task ReviewerRefusesDifferentNameCpuOrGpuForeignModelWithoutUnload(long sizeVramBytes)
        => await AssertReviewerRuntimeRefusalAsync(
            "foreign:latest",
            sizeVramBytes,
            trackSelectedModel: false,
            trackerMustRemain: false);

    [Fact]
    public async Task ReviewerRefusesSameNameUntrackedModelWithoutUnload()
        => await AssertReviewerRuntimeRefusalAsync(
            "qwen2.5-coder:1.5b",
            4_000_000_000,
            trackSelectedModel: false,
            trackerMustRemain: false);

    [Fact]
    public async Task ReviewerFailsClosedOnStaleOwnershipTrackerWithoutUnload()
        => await AssertReviewerRuntimeRefusalAsync(
            null,
            0,
            trackSelectedModel: true,
            trackerMustRemain: true);

    [Fact]
    public void RuntimeOwnershipFailsClosedOnMalformedTracker()
    {
        using var temporary = new TemporaryDirectory();
        var tracker = new ActiveModelTracker(temporary.Path);
        File.WriteAllText(tracker.Path, "{not-valid-json");

        var result = OllamaRuntimeOwnership.Inspect(
            [],
            tracker.Inspect(),
            "qwen2.5-coder:1.5b");

        Assert.False(result.Allowed);
        Assert.Equal(OllamaRuntimeOwnership.ForeignModelCode, result.Code);
        Assert.Equal(ActiveModelTrackerStatus.Invalid, tracker.Inspect().Status);
    }

    [Theory]
    [InlineData("foreign:latest", 0L)]
    [InlineData("foreign:latest", 4_000_000_000L)]
    [InlineData("qwen2.5-coder:1.5b", 4_000_000_000L)]
    public async Task ValidationRefusesCpuGpuAndSameNameUntrackedModelsWithoutUnload(
        string runningModel,
        long sizeVramBytes)
        => await AssertValidationRuntimeRefusalAsync(runningModel, sizeVramBytes, trackSelectedModel: false);

    [Fact]
    public async Task ValidationFailsClosedOnStaleOwnershipTrackerWithoutUnload()
        => await AssertValidationRuntimeRefusalAsync(null, 0, trackSelectedModel: true);

    [Fact]
    public async Task ValidationAllowsAndUnloadsOnlyTheTrackedHelperModel()
    {
        using var temporary = new TemporaryDirectory();
        var validations = new ModelValidationStore(temporary.Path);
        var tracker = new ActiveModelTracker(temporary.Path);
        tracker.Set("qwen2.5-coder:1.5b", FullDigest("d7372fd82851"));
        var loaded = true;
        var unloads = 0;
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri?.AbsolutePath;
            if (path == "/api/tags")
            {
                return FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\"}]}");
            }

            if (path == "/api/ps")
            {
                return FakeHttpMessageHandler.Json(loaded
                    ? "{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\",\"size_vram\":4000000000,\"context_length\":2048}]}"
                    : "{\"models\":[]}");
            }

            if (path == "/api/generate")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                if (!document.RootElement.TryGetProperty("prompt", out var prompt))
                {
                    return FakeHttpMessageHandler.Json("{}");
                }

                var response = prompt.GetString()!.Contains("THALEN_HELPER_OK", StringComparison.Ordinal)
                    ? "THALEN_HELPER_OK"
                    : "OFF_BY_ONE";
                if (document.RootElement.TryGetProperty("keep_alive", out var keepAlive)
                    && string.Equals(keepAlive.GetString(), "0s", StringComparison.Ordinal))
                {
                    loaded = false;
                    unloads++;
                }
                return FakeHttpMessageHandler.Json($"{{\"model\":\"qwen2.5-coder:1.5b\",\"response\":\"{response}\",\"done\":true}}");
            }

            return FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound);
        });
        var manager = new InstallationManager(
            clientFactory: () => new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler)),
            resourcePressureValidator: (_, _) => new ResourcePressureCheck(true, "OK", "Safe."),
            validationStoreProvider: _ => validations,
            activeModelTrackerProvider: _ => tracker);

        var result = await manager.ValidateSelectedModelForTestingAsync(ValidationState());

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, unloads);
        Assert.Equal(ActiveModelTrackerStatus.Absent, tracker.Inspect().Status);
        Assert.Single((await validations.LoadAsync()).Entries);
    }

    [Fact]
    public void RuntimeOwnershipRejectsTrackedSameNameWithDifferentDigest()
    {
        using var temporary = new TemporaryDirectory();
        var tracker = new ActiveModelTracker(temporary.Path);
        tracker.Set("qwen2.5-coder:1.5b", FullDigest("d7372fd82851"));

        var result = OllamaRuntimeOwnership.Inspect(
            [new OllamaRunningModel(
                "qwen2.5-coder:1.5b",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                100,
                0,
                2048,
                null)],
            tracker.Inspect(),
            "qwen2.5-coder:1.5b");

        Assert.False(result.Allowed);
        Assert.Equal(OllamaRuntimeOwnership.ForeignModelCode, result.Code);
    }

    [Fact]
    public void RuntimeOwnershipRejectsLegacyNameOnlyTracker()
    {
        using var temporary = new TemporaryDirectory();
        var tracker = new ActiveModelTracker(temporary.Path);
        tracker.Set("qwen2.5-coder:1.5b");

        var result = OllamaRuntimeOwnership.Inspect(
            [new OllamaRunningModel(
                "qwen2.5-coder:1.5b",
                "sha256:d7372fd828510000000000000000000000000000000000000000000000000000",
                100,
                4_000_000_000,
                2048,
                null)],
            tracker.Inspect(),
            "qwen2.5-coder:1.5b");

        Assert.False(result.Allowed);
        Assert.Equal(OllamaRuntimeOwnership.ForeignModelCode, result.Code);
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
    public async Task ReviewPreservesFindingsAndAddsOnlyValidatedStructuredAdvisoryFindings()
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
        var structured = """
            {"findings":[{"id":"F1","claim":"A supplied branch may miss null input.","location":"Parser.cs:42","evidence":"The supplied branch dereferences value before its null check.","confidence":"HIGH","impact":"A bounded input could fail.","verification":"Run the null-input unit test against the supplied branch.","falsePositiveCondition":"An earlier guard guarantees value is non-null."}]}
            """;
        var generationCount = 0;
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(request.RequestUri?.AbsolutePath switch
        {
            "/api/tags" => FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\",\"size\":100}]}"),
            "/api/ps" => FakeHttpMessageHandler.Json("{\"models\":[]}"),
            "/api/generate" => FakeHttpMessageHandler.Json(JsonSerializer.Serialize(new
            {
                model = "qwen2.5-coder:1.5b",
                response = ++generationCount == 1 ? structured : "prose fallback { not valid JSON",
                done = true
            })),
            _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
        }));
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var validations = new ModelValidationStore(paths.StateDirectory);
        await validations.UpsertAsync(Validation("qwen2.5-coder:1.5b", "d7372fd82851"));
        var reviewer = new ReviewerService(
            store,
            client,
            _ => true,
            StorageOk,
            (_, _) => new ResourcePressureCheck(true, "OK", "Safe."),
            hardwareProvider: ReviewerHardware,
            validationStore: validations);

        var result = await reviewer.ReviewAsync(new ReviewRequest(
            "Review the supplied diff.",
            Context: "Parser.cs:42 supplied branch",
            MaximumOutputTokens: int.MaxValue,
            TaskKind: ReviewTaskKind.DiffReview));

        Assert.True(result.ModelRan, result.ErrorMessage);
        Assert.Equal(structured, result.Findings);
        var finding = Assert.Single(result.StructuredFindings);
        Assert.Equal("parsed", result.StructuredFindingsStatus);
        Assert.Equal("F1", finding.Id);
        Assert.Equal("high", finding.Confidence);
        Assert.Equal("An earlier guard guarantees value is non-null.", finding.FalsePositiveCondition);
        Assert.Empty(result.ConfirmedObservations);
        Assert.Single(result.Hypotheses);
        var requestBody = JsonDocument.Parse(handler.Requests.Single(item => item.Path == "/api/generate").Body!);
        Assert.Equal(2_048, requestBody.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        var prompt = requestBody.RootElement.GetProperty("prompt").GetString();
        Assert.Contains("TASK RUBRIC (DiffReview)", prompt, StringComparison.Ordinal);
        Assert.Contains("falsePositiveCondition", prompt, StringComparison.Ordinal);

        var fallback = await reviewer.ReviewAsync(new ReviewRequest("Review malformed model output."));

        Assert.True(fallback.ModelRan, fallback.ErrorMessage);
        Assert.Equal("prose fallback { not valid JSON", fallback.Findings);
        Assert.Empty(fallback.StructuredFindings);
        Assert.Equal("malformed", fallback.StructuredFindingsStatus);
        Assert.Empty(fallback.ConfirmedObservations);
    }

    [Theory]
    [InlineData(ReviewTaskKind.General, "concrete, actionable issues")]
    [InlineData(ReviewTaskKind.LogTriage, "Group related symptoms")]
    [InlineData(ReviewTaskKind.TestFailure, "Connect each failure")]
    [InlineData(ReviewTaskKind.DiffReview, "Review only the supplied diff")]
    [InlineData(ReviewTaskKind.RepositoryAnalysis, "supplied repository inventory")]
    [InlineData(ReviewTaskKind.EdgeCases, "boundary and adversarial cases")]
    public void PromptInjectsTaskSpecificRubric(ReviewTaskKind taskKind, string expectedRubric)
    {
        var prompt = ReviewerService.BuildPrompt(
            new ReviewRequest("Bounded assignment.", TaskKind: taskKind),
            HardwareTier.Mid,
            taskKind);

        Assert.Contains($"TASK RUBRIC ({taskKind})", prompt, StringComparison.Ordinal);
        Assert.Contains(expectedRubric, prompt, StringComparison.Ordinal);
        Assert.Contains("Never describe a model interpretation as a confirmed observation.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredFindingParserBoundsCountAndRejectsIncompleteOrOversizedRecords()
    {
        var valid = Enumerable.Range(1, ReviewerService.MaximumStructuredFindings + 5)
            .Select(index => (object)new
            {
                id = $"F{index}",
                claim = "Claim",
                location = "File.cs:1",
                evidence = "Supplied evidence",
                confidence = "medium",
                impact = "Impact",
                verification = "Verification",
                false_positive_condition = "False-positive condition"
            })
            .ToList();
        valid.Insert(0, new
        {
            id = "OVERSIZED",
            claim = new string('x', 2_001),
            location = "File.cs:1",
            evidence = "Evidence",
            confidence = "high",
            impact = "Impact",
            verification = "Verification",
            false_positive_condition = "Condition"
        });
        valid.Insert(0, new
        {
            id = "INCOMPLETE",
            claim = "Missing required fields"
        });
        var json = JsonSerializer.Serialize(new { findings = valid });

        var findings = ReviewerService.ParseStructuredFindings(json);
        var parseResult = ReviewerService.ParseStructuredFindingsWithStatus(json);

        Assert.Equal(ReviewerService.MaximumStructuredFindings, findings.Count);
        Assert.Equal("parsed_with_ignored_items", parseResult.Status);
        Assert.Equal("F1", findings[0].Id);
        Assert.DoesNotContain(findings, item => item.Id is "OVERSIZED" or "INCOMPLETE");
    }

    [Fact]
    public void StructuredFindingParserDistinguishesValidEmptyFromMalformedOutput()
    {
        var validEmpty = ReviewerService.ParseStructuredFindingsWithStatus("{\"findings\":[]}");
        var malformed = ReviewerService.ParseStructuredFindingsWithStatus("{not-json");

        Assert.Empty(validEmpty.Findings);
        Assert.Equal("parsed", validEmpty.Status);
        Assert.Empty(malformed.Findings);
        Assert.Equal("malformed", malformed.Status);
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
        var validations = new ModelValidationStore(paths.StateDirectory);
        await validations.UpsertAsync(Validation("qwen3:8b", "500a1f067a9f"));
        await validations.UpsertAsync(Validation("qwen3:14b", "bdbd181c33f2"));
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
            hardwareProvider: () => hardware,
            validationStore: validations);

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
    public async Task AutomaticLmStudioPlanFailsClosedForLegacyRegistrationWithoutCurrentFileIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        var catalog = new ModelCatalogService().LoadBundled();
        var model = catalog.Models.Single(item => item.Provider == ModelProviders.LmStudio);
        var modelPath = Path.Combine(
            temporary.Path,
            Path.Combine(model.IndexedModelPath!.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        await File.WriteAllTextAsync(modelPath, "validated fixture identity");
        Assert.True(LmStudioModelFileBinding.TryOpen(modelPath, out var modelStream, out var modelProof));
        modelStream.Dispose();
        var registration = new LocalModelRegistration(
            ModelProviders.LmStudio,
            model.Tag,
            model.ExpectedDigest!,
            modelPath,
            DateTimeOffset.UtcNow,
            modelProof.Length,
            modelProof.LastWriteTimeUtc,
            FileIdentity: null);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen3:14b",
            SelectedModelDigest = "bdbd181c33f2",
            ModelStorageLocation = temporary.Path,
            HardwareTier = HardwareTier.High,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            RegisteredLocalModels = [registration],
            Availability = HelperAvailability.Enabled,
            Preferences = new HelperPreferences(
                ModelSelectionMode: ModelSelectionMode.Automatic,
                PreferLmStudioForStandardAndDeep: true)
        });
        var ollamaHandler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/api/tags"
                ? FakeHttpMessageHandler.Json("{\"models\":[]}")
                : FakeHttpMessageHandler.Json("{\"models\":[]}")));
        var lmHandler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json($$"""
            {"models":[{
              "key":"{{model.Tag}}",
              "architecture":"qwen3",
              "quantization":"BF16",
              "size_bytes":{{model.ExpectedDownloadBytes}},
              "parameter_count":9.0,
              "max_context_length":65536,
              "loaded_instances":[]
            }]}
            """)));
        using var ollama = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(ollamaHandler));
        using var lmStudio = new LmStudioClient(new Uri("http://127.0.0.1:1234"), new HttpClient(lmHandler));
        var validations = new ModelValidationStore(paths.StateDirectory);
        await validations.UpsertAsync(Validation(model.Tag, model.ExpectedDigest!) with { Provider = ModelProviders.LmStudio });
        var reviewer = new ReviewerService(
            store,
            ollama,
            _ => true,
            _ => throw new InvalidOperationException("Ollama storage validation must not run for an LM Studio route."),
            router: new TaskAwareModelRouter(),
            catalogProvider: () => catalog,
            hardwareProvider: ReviewerHardware,
            validationStore: validations,
            lmStudio: lmStudio);

        var plan = await reviewer.PlanAsync(new ReviewRequest(
            "Review a bounded diff.",
            TaskKind: ReviewTaskKind.DiffReview,
            Effort: ReviewEffort.Standard));

        Assert.False(plan.Allowed);
        Assert.Equal("MODEL_ROUTE_UNAVAILABLE", plan.ErrorCode);
        Assert.Contains("No installed, validated, audited", plan.ErrorMessage, StringComparison.Ordinal);
        Assert.False(plan.ModelRan);
        Assert.DoesNotContain(ollamaHandler.Requests, request => request.Path == "/api/generate");
        Assert.DoesNotContain(lmHandler.Requests, request => request.Path.Contains("chat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CorruptValidationRegistryFailsHealthAndPlanningClosedWithoutOllamaCalls()
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
        var validations = new ModelValidationStore(paths.StateDirectory);
        await File.WriteAllTextAsync(validations.Path, "{broken");
        var handler = InventoryHandler();
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var reviewer = new ReviewerService(
            store,
            client,
            _ => true,
            StorageOk,
            validationStore: validations);

        var health = await reviewer.GetHealthAsync();
        var plan = await reviewer.PlanAsync(new ReviewRequest("Review."));

        Assert.Equal("VALIDATION_STATE_INVALID", health.ErrorCode);
        Assert.Equal("VALIDATION_STATE_INVALID", plan.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SuccessfulValidationUsesOneGpuLeaseThroughBothChecksAndVerifiedUnload()
    {
        using var temporary = new TemporaryDirectory();
        var validations = new ModelValidationStore(temporary.Path);
        var psCalls = 0;
        var leaseBlockedAtEveryGenerate = 0;
        var handler = new FakeHttpMessageHandler(async (request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/tags")
            {
                return FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\"}]}");
            }

            if (request.RequestUri?.AbsolutePath == "/api/ps")
            {
                psCalls++;
                return FakeHttpMessageHandler.Json(psCalls is 2 or 3 or 4
                    ? "{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\",\"size_vram\":4000000000,\"context_length\":2048}] }"
                    : "{\"models\":[]}");
            }

            if (request.RequestUri?.AbsolutePath == "/api/generate")
            {
                var busy = await Assert.ThrowsAsync<OllamaException>(() => GpuCoordination.AcquireAsync(
                    ReviewBusyBehavior.Skip,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None));
                Assert.Equal("REVIEW_BUSY_SKIPPED", busy.Code);
                leaseBlockedAtEveryGenerate++;
                var body = await request.Content!.ReadAsStringAsync();
                if (!body.Contains("\"prompt\"", StringComparison.Ordinal))
                {
                    return FakeHttpMessageHandler.Json("{}");
                }

                return FakeHttpMessageHandler.Json(body.Contains("THALEN_HELPER_OK", StringComparison.Ordinal)
                    ? "{\"model\":\"qwen2.5-coder:1.5b\",\"response\":\"THALEN_HELPER_OK\",\"done\":true}"
                    : "{\"model\":\"qwen2.5-coder:1.5b\",\"response\":\"OFF_BY_ONE\",\"done\":true}");
            }

            return FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound);
        });
        var manager = new InstallationManager(
            clientFactory: () => new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler)),
            resourcePressureValidator: (_, _) => new ResourcePressureCheck(true, "OK", "Safe."),
            validationStoreProvider: _ => validations);
        var state = ValidationState();

        var result = await manager.ValidateSelectedModelForTestingAsync(state);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, leaseBlockedAtEveryGenerate);
        Assert.Equal(8, psCalls);
        var entry = Assert.Single((await validations.LoadAsync()).Entries);
        Assert.Equal(state.SelectedModel, entry.Tag);
        Assert.Equal("d7372fd828510000000000000000000000000000000000000000000000000000", entry.Digest);
        Assert.Equal(ModelValidationStore.CurrentProtocolVersion, entry.ProtocolVersion);
        Assert.Equal("GPU or partial GPU (verify processor split with ollama ps)", entry.Processor);
    }

    [Fact]
    public async Task SuccessfulValidationWaitsForAsynchronousOllamaUnload()
    {
        using var temporary = new TemporaryDirectory();
        var validations = new ModelValidationStore(temporary.Path);
        var psCalls = 0;
        var handler = new FakeHttpMessageHandler(async (request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/tags")
            {
                return FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\"}]}");
            }

            if (request.RequestUri?.AbsolutePath == "/api/ps")
            {
                psCalls++;
                var loaded = psCalls is >= 2 and <= 5;
                return FakeHttpMessageHandler.Json(loaded
                    ? "{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\",\"size_vram\":4000000000,\"context_length\":2048}] }"
                    : "{\"models\":[]}");
            }

            if (request.RequestUri?.AbsolutePath == "/api/generate")
            {
                var body = await request.Content!.ReadAsStringAsync();
                if (!body.Contains("\"prompt\"", StringComparison.Ordinal))
                {
                    return FakeHttpMessageHandler.Json("{}");
                }

                return FakeHttpMessageHandler.Json(body.Contains("THALEN_HELPER_OK", StringComparison.Ordinal)
                    ? "{\"model\":\"qwen2.5-coder:1.5b\",\"response\":\"THALEN_HELPER_OK\",\"done\":true}"
                    : "{\"model\":\"qwen2.5-coder:1.5b\",\"response\":\"OFF_BY_ONE\",\"done\":true}");
            }

            return FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound);
        });
        var manager = new InstallationManager(
            clientFactory: () => new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler)),
            resourcePressureValidator: (_, _) => new ResourcePressureCheck(true, "OK", "Safe."),
            validationStoreProvider: _ => validations);

        var result = await manager.ValidateSelectedModelForTestingAsync(ValidationState());

        Assert.True(result.Success, result.Message);
        Assert.Equal(9, psCalls);
        Assert.Single((await validations.LoadAsync()).Entries);
    }

    [Fact]
    public async Task FailedValidationInvalidatesSameTagEvidenceAndStillUnloads()
    {
        using var temporary = new TemporaryDirectory();
        var validations = new ModelValidationStore(temporary.Path);
        await validations.UpsertAsync(Validation(
            "qwen2.5-coder:1.5b",
            "d7372fd828510000000000000000000000000000000000000000000000000000"));
        var unloads = 0;
        var handler = new FakeHttpMessageHandler(async (request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath;
            if (path == "/api/tags")
            {
                return FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\"}]}");
            }
            if (path == "/api/ps")
            {
                return FakeHttpMessageHandler.Json("{\"models\":[]}");
            }
            if (path == "/api/generate")
            {
                var body = await request.Content!.ReadAsStringAsync();
                if (!body.Contains("\"prompt\"", StringComparison.Ordinal))
                {
                    unloads++;
                    return FakeHttpMessageHandler.Json("{}");
                }
                return FakeHttpMessageHandler.Json("{\"model\":\"qwen2.5-coder:1.5b\",\"response\":\"WRONG\",\"done\":true}");
            }
            return FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound);
        });
        var manager = new InstallationManager(
            clientFactory: () => new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler)),
            resourcePressureValidator: (_, _) => new ResourcePressureCheck(true, "OK", "Safe."),
            validationStoreProvider: _ => validations);

        var result = await manager.ValidateSelectedModelForTestingAsync(ValidationState());

        Assert.False(result.Success);
        Assert.Equal("EXACT_RESPONSE_FAILED", result.Code);
        Assert.Equal(0, unloads);
        Assert.Empty((await validations.LoadAsync()).Entries);
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

    [Fact]
    public async Task StateStoreCompareAndSwapPreservesANewerCrossInstanceWrite()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var first = new StateStore(paths.StateFile);
        var second = new StateStore(paths.StateFile);
        await first.SaveAsync(new InstallationState { ProductVersion = "original" });
        var loaded = await first.LoadWithRevisionAsync();
        await second.SaveAsync(new InstallationState { ProductVersion = "newer" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => first.SaveIfUnchangedAsync(
            loaded.State! with { ProductVersion = "stale-repair" },
            loaded.Revision));

        Assert.Equal("newer", (await second.LoadAsync())!.ProductVersion);
        Assert.Empty(Directory.GetFiles(paths.StateDirectory, "*.tmp"));
    }

    [Fact]
    public async Task StateStoreNamedMutexSerializesAnotherWindowsProcess()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        var script = "$m=[System.Threading.Mutex]::new($false,'" + store.MutexName + "');"
            + "$null=$m.WaitOne();[Console]::Out.WriteLine('READY');[Console]::Out.Flush();"
            + "$null=[Console]::In.ReadLine();$m.ReleaseMutex();$m.Dispose()";
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
        }) ?? throw new InvalidOperationException("Unable to start mutex test process.");

        Assert.Equal("READY", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
                new InstallationState { ProductVersion = "blocked" },
                timeout.Token));
        }
        finally
        {
            await process.StandardInput.WriteLineAsync();
            await process.StandardInput.FlushAsync();
        }

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
        await store.SaveAsync(new InstallationState { ProductVersion = "after-release" });
        Assert.Equal("after-release", (await store.LoadAsync())!.ProductVersion);
    }

    private static async Task AssertReviewerRuntimeRefusalAsync(
        string? runningModel,
        long sizeVramBytes,
        bool trackSelectedModel,
        bool trackerMustRemain)
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        var state = ValidationState();
        state.Availability = HelperAvailability.Enabled;
        await store.SaveAsync(state);
        var tracker = new ActiveModelTracker(paths.StateDirectory);
        if (trackSelectedModel)
        {
            tracker.Set(state.SelectedModel!, FullDigest("d7372fd82851"));
        }

        var runningJson = runningModel is null
            ? "{\"models\":[]}"
            : $"{{\"models\":[{{\"name\":\"{runningModel}\",\"size_vram\":{sizeVramBytes},\"context_length\":2048}}]}}";
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath switch
            {
                "/api/tags" => FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\",\"size\":100}]}"),
                "/api/ps" => FakeHttpMessageHandler.Json(runningJson),
                "/api/generate" => FakeHttpMessageHandler.Json("{\"model\":\"qwen2.5-coder:1.5b\",\"response\":\"UNEXPECTED\",\"done\":true}"),
                _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
            }));
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var validations = new ModelValidationStore(paths.StateDirectory);
        await validations.UpsertAsync(Validation("qwen2.5-coder:1.5b", "d7372fd82851"));
        var pressureChecks = 0;
        var reviewer = new ReviewerService(
            store,
            client,
            _ => true,
            StorageOk,
            (_, _) =>
            {
                pressureChecks++;
                return new ResourcePressureCheck(true, "OK", "Safe.");
            },
            hardwareProvider: ReviewerHardware,
            validationStore: validations);

        var result = await reviewer.ReviewAsync(new ReviewRequest("Inspect."));

        Assert.Equal(OllamaRuntimeOwnership.ForeignModelCode, result.ErrorCode);
        Assert.False(result.ModelRan);
        Assert.Equal(0, pressureChecks);
        Assert.Equal(["/api/tags", "/api/ps"], handler.Requests.Select(request => request.Path));
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/api/generate");
        Assert.Equal(
            trackerMustRemain ? ActiveModelTrackerStatus.Valid : ActiveModelTrackerStatus.Absent,
            tracker.Inspect().Status);
    }

    private static async Task AssertValidationRuntimeRefusalAsync(
        string? runningModel,
        long sizeVramBytes,
        bool trackSelectedModel)
    {
        using var temporary = new TemporaryDirectory();
        var validations = new ModelValidationStore(temporary.Path);
        var tracker = new ActiveModelTracker(temporary.Path);
        if (trackSelectedModel)
        {
            tracker.Set("qwen2.5-coder:1.5b", FullDigest("d7372fd82851"));
        }

        var runningJson = runningModel is null
            ? "{\"models\":[]}"
            : $"{{\"models\":[{{\"name\":\"{runningModel}\",\"size_vram\":{sizeVramBytes},\"context_length\":2048}}]}}";
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath switch
            {
                "/api/tags" => FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\"}]}"),
                "/api/ps" => FakeHttpMessageHandler.Json(runningJson),
                "/api/generate" => FakeHttpMessageHandler.Json("{}"),
                _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
            }));
        var pressureChecks = 0;
        var manager = new InstallationManager(
            clientFactory: () => new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler)),
            resourcePressureValidator: (_, _) =>
            {
                pressureChecks++;
                return new ResourcePressureCheck(true, "OK", "Safe.");
            },
            validationStoreProvider: _ => validations,
            activeModelTrackerProvider: _ => tracker);

        var result = await manager.ValidateSelectedModelForTestingAsync(ValidationState());

        Assert.False(result.Success);
        Assert.Equal(OllamaRuntimeOwnership.ForeignModelCode, result.Code);
        Assert.Equal(0, pressureChecks);
        Assert.Equal(["/api/tags", "/api/ps"], handler.Requests.Select(request => request.Path));
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/api/generate");
        Assert.Equal(
            trackSelectedModel ? ActiveModelTrackerStatus.Valid : ActiveModelTrackerStatus.Absent,
            tracker.Inspect().Status);
    }

    private static FakeHttpMessageHandler InventoryHandler()
        => new((request, _) => Task.FromResult(request.RequestUri?.AbsolutePath switch
        {
            "/api/tags" => FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\",\"size\":100}]}"),
            "/api/ps" => FakeHttpMessageHandler.Json("{\"models\":[]}"),
            _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
        }));

    private static ModelValidationEntry Validation(string tag, string digestPrefix)
        => new(
            tag,
            digestPrefix + new string('0', 64 - digestPrefix.Length),
            ModelValidationStore.CurrentProtocolVersion,
            DateTimeOffset.UtcNow,
            10,
            20,
            "GPU",
            1024,
            2048);

    private static string FullDigest(string prefix)
        => prefix + new string('0', 64 - prefix.Length);

    private static InstallationState ValidationState()
        => new()
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Disabled
        };

    private static ReviewerModelStorageVerification StorageOk(InstallationState _)
        => new(true, "OK", "Storage verified.");

    private static HardwareProfile ReviewerHardware()
        => FixtureFactory.Create(
            FixtureFactory.LoadHardwareFixtures().Single(item => item.Name == "nvidia-rtx3090-24gb"));
}
