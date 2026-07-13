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
            Availability = HelperAvailability.Enabled
        });
        var handler = InventoryHandler();
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));

        var health = await new ReviewerService(store, client, null, StorageOk).GetHealthAsync();

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
            Availability = HelperAvailability.Enabled
        });
        var handler = InventoryHandler();
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var reviewer = new ReviewerService(store, client, null, StorageOk);

        await Assert.ThrowsAsync<ArgumentException>(() => reviewer.ReviewAsync(new ReviewRequest(new string('x', 12_001))));
        Assert.Empty(handler.Requests);
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
