using System.Net;
using System.Text.Json;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class LmStudioClientTests
{
    [Theory]
    [InlineData("http://192.168.1.10:1234")]
    [InlineData("https://127.0.0.1:1234")]
    [InlineData("http://user@127.0.0.1:1234")]
    [InlineData("http://127.0.0.1:1235")]
    [InlineData("http://127.0.0.1:1234/api")]
    [InlineData("http://127.0.0.1:1234/?token=secret")]
    [InlineData("http://127.0.0.1:1234/#fragment")]
    [InlineData("http://[::1]:1234")]
    public void ClientRejectsAnythingExceptTheFixedIpv4LoopbackEndpoint(string value)
    {
        Assert.Throws<ArgumentException>(() => LmStudioClient.ValidateBaseUri(new Uri(value)));
    }

    [Fact]
    public void LocalhostIsCanonicalizedToIpv4Loopback()
    {
        var normalized = LmStudioClient.ValidateBaseUri(new Uri("http://localhost:1234"));

        Assert.Equal("http://127.0.0.1:1234/", normalized.AbsoluteUri);
    }

    [Fact]
    public async Task InventoryParsesNativeV1MetadataAndLoadedInstances()
    {
        const string body = """
            {
              "models": [{
                "type": "llm",
                "publisher": "example",
                "key": "model-9b",
                "display_name": "Model 9B",
                "architecture": "qwen35",
                "quantization": { "name": "BF16", "bits_per_weight": 16 },
                "size_bytes": 17920696992,
                "params_string": "9.0B",
                "max_context_length": 1048576,
                "capabilities": { "trained_for_tool_use": true },
                "loaded_instances": [{ "id": "model-9b:1", "config": { "context_length": 65536 } }]
              }]
            }
            """;
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/api/v1/models"
                ? FakeHttpMessageHandler.Json(body)
                : FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)));
        using var client = CreateClient(handler);

        var model = Assert.Single(await client.GetModelsAsync());

        Assert.Equal("model-9b", model.Key);
        Assert.Equal("BF16", model.Quantization);
        Assert.Equal(16, model.BitsPerWeight);
        Assert.Equal(9, model.ParameterBillions);
        Assert.True(model.TrainedForToolUse);
        Assert.Equal(65_536, Assert.Single(model.LoadedInstances).ContextLength);
    }

    [Fact]
    public async Task LoadUsesTheAuditedSafeStartingConfiguration()
    {
        using var temporary = new TemporaryDirectory();
        var tracker = new ActiveModelTracker(temporary.Path);
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath switch
            {
                "/api/v1/models/load" => FakeHttpMessageHandler.Json(
                    "{\"model\":\"model-9b\",\"instance_id\":\"model-9b:1\",\"load_config\":{\"context_length\":65536,\"flash_attention\":true,\"offload_kv_cache_to_gpu\":true}}"),
                "/api/v1/models" => FakeHttpMessageHandler.Json(
                    "{\"models\":[{\"key\":\"model-9b\",\"loaded_instances\":[{\"id\":\"model-9b:1\"}]}]}"),
                _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
            }));
        using var client = CreateClient(handler);

        var result = await client.LoadOwnedAsync(
            "model-9b",
            tracker,
            static (_, _) => Task.CompletedTask);

        Assert.Equal(LmStudioClient.ProviderName, result.Provider);
        Assert.Equal("model-9b", result.ModelKey);
        Assert.Equal("model-9b:1", result.InstanceId);
        var tracked = Assert.IsType<ActiveModelReference>(tracker.ReadReference());
        Assert.Equal(ModelProviders.LmStudio, tracked.Provider);
        Assert.Equal("model-9b", tracked.Model);
        Assert.Equal("model-9b:1", tracked.InstanceId);
        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/models/load", request.Path);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("model-9b", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(65_536, body.RootElement.GetProperty("context_length").GetInt32());
        Assert.True(body.RootElement.GetProperty("flash_attention").GetBoolean());
        Assert.True(body.RootElement.GetProperty("offload_kv_cache_to_gpu").GetBoolean());
        Assert.True(body.RootElement.GetProperty("echo_load_config").GetBoolean());
        Assert.Equal("/api/v1/models", handler.Requests[1].Path);
    }

    [Fact]
    public async Task LoadRejectsAResponseForADifferentModelIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var tracker = new ActiveModelTracker(temporary.Path);
        var cliAbsenceProofs = 0;
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath switch
            {
                "/api/v1/models/load" => FakeHttpMessageHandler.Json(
                    "{\"model\":\"other-9b\",\"instance_id\":\"other-9b:1\"}"),
                "/api/v1/models/unload" => FakeHttpMessageHandler.Json(
                    "{\"instance_id\":\"other-9b:1\"}"),
                "/api/v1/models" => FakeHttpMessageHandler.Json("{\"models\":[]}"),
                _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
            }));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<LmStudioException>(() =>
            client.LoadOwnedAsync(
                "model-9b",
                tracker,
                (_, _) =>
                {
                    cliAbsenceProofs++;
                    return Task.CompletedTask;
                }));

        Assert.Equal("MODEL_RESPONSE_IDENTITY_MISMATCH", exception.Code);
        Assert.Equal(1, cliAbsenceProofs);
        Assert.Equal(ActiveModelTrackerStatus.Absent, tracker.Inspect().Status);
        Assert.Equal(
            new[] { "/api/v1/models/load", "/api/v1/models/unload", "/api/v1/models" },
            handler.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task LoadConfigMismatchRequiresExactRestAndCliAbsenceBeforeClearingOwnership()
    {
        using var temporary = new TemporaryDirectory();
        var tracker = new ActiveModelTracker(temporary.Path);
        var cliAbsenceProofs = 0;
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath switch
            {
                "/api/v1/models/load" => FakeHttpMessageHandler.Json(
                    "{\"instance_id\":\"model-9b:1\",\"load_config\":{\"context_length\":32768,\"flash_attention\":true,\"offload_kv_cache_to_gpu\":true}}"),
                "/api/v1/models/unload" => FakeHttpMessageHandler.Json(
                    "{\"instance_id\":\"model-9b:1\"}"),
                "/api/v1/models" => FakeHttpMessageHandler.Json("{\"models\":[]}"),
                _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
            }));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<LmStudioException>(() =>
            client.LoadOwnedAsync(
                "model-9b",
                tracker,
                (_, _) =>
                {
                    cliAbsenceProofs++;
                    return Task.CompletedTask;
                }));

        Assert.Equal("LMSTUDIO_LOAD_CONFIG_MISMATCH", exception.Code);
        Assert.Equal(1, cliAbsenceProofs);
        Assert.Equal(ActiveModelTrackerStatus.Absent, tracker.Inspect().Status);
        Assert.Equal(
            new[] { "/api/v1/models/load", "/api/v1/models/unload", "/api/v1/models" },
            handler.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task CanceledPostLoadVerificationUsesAnIndependentCleanupToken()
    {
        using var temporary = new TemporaryDirectory();
        var tracker = new ActiveModelTracker(temporary.Path);
        using var callerCancellation = new CancellationTokenSource();
        var cleanupTokenWasCanceled = true;
        var cliCleanupTokenWasCanceled = true;
        var cliAbsenceProofs = 0;
        var inventoryRequests = 0;
        var handler = new FakeHttpMessageHandler(async (request, token) =>
        {
            switch (request.RequestUri?.AbsolutePath)
            {
                case "/api/v1/models/load":
                    return FakeHttpMessageHandler.Json(
                        "{\"model\":\"model-9b\",\"instance_id\":\"model-9b:1\",\"load_config\":{\"context_length\":65536,\"flash_attention\":true,\"offload_kv_cache_to_gpu\":true}}");
                case "/api/v1/models":
                    if (++inventoryRequests == 1)
                    {
                        callerCancellation.Cancel();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        throw new InvalidOperationException("The canceled verification request unexpectedly completed.");
                    }
                    return FakeHttpMessageHandler.Json("{\"models\":[]}");
                case "/api/v1/models/unload":
                    cleanupTokenWasCanceled = token.IsCancellationRequested;
                    return FakeHttpMessageHandler.Json("{\"instance_id\":\"model-9b:1\"}");
                default:
                    return FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound);
            }
        });
        using var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.LoadOwnedAsync(
                "model-9b",
                tracker,
                (_, token) =>
                {
                    cliAbsenceProofs++;
                    cliCleanupTokenWasCanceled = token.IsCancellationRequested;
                    return Task.CompletedTask;
                },
                callerCancellation.Token));

        Assert.False(cleanupTokenWasCanceled);
        Assert.False(cliCleanupTokenWasCanceled);
        Assert.Equal(1, cliAbsenceProofs);
        Assert.Equal(ActiveModelTrackerStatus.Absent, tracker.Inspect().Status);
        Assert.Equal(
            new[] { "/api/v1/models/load", "/api/v1/models", "/api/v1/models/unload", "/api/v1/models" },
            handler.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task FailedPostLoadCleanupReportsGpuReleaseFailureAndRetainsExactOwnership()
    {
        using var temporary = new TemporaryDirectory();
        var tracker = new ActiveModelTracker(temporary.Path);
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath switch
            {
                "/api/v1/models/load" => FakeHttpMessageHandler.Json(
                    "{\"model\":\"model-9b\",\"instance_id\":\"model-9b:1\",\"load_config\":{\"context_length\":32768,\"flash_attention\":true,\"offload_kv_cache_to_gpu\":true}}"),
                "/api/v1/models/unload" => FakeHttpMessageHandler.Json(
                    "{}",
                    HttpStatusCode.InternalServerError),
                _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
            }));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<LmStudioException>(() =>
            client.LoadOwnedAsync(
                "model-9b",
                tracker,
                static (_, _) => Task.CompletedTask));

        Assert.Equal("GPU_RELEASE_FAILED", exception.Code);
        var tracked = Assert.IsType<ActiveModelReference>(tracker.ReadReference());
        Assert.Equal(ModelProviders.LmStudio, tracked.Provider);
        Assert.Equal("model-9b", tracked.Model);
        Assert.Equal("model-9b:1", tracked.InstanceId);
        Assert.Equal(
            new[] { "/api/v1/models/load", "/api/v1/models/unload" },
            handler.Requests.Select(request => request.Path));
    }

    [Fact]
    public void CatalogBindingRequiresTheExactIndexedModelPathForTheKey()
    {
        using var temporary = new TemporaryDirectory();
        var catalog = new ModelCatalogService().LoadBundled().Models.Single(model =>
            string.Equals(model.Provider, ModelProviders.LmStudio, StringComparison.Ordinal));
        var exactPath = Path.Combine(
            temporary.Path,
            Path.Combine(catalog.IndexedModelPath!.Split('/')));
        var unrelatedPath = Path.Combine(temporary.Path, Path.GetFileName(exactPath));

        Assert.True(LmStudioModelFileBinding.IsCanonicalCatalogBinding(catalog, catalog.Tag, exactPath));
        Assert.False(LmStudioModelFileBinding.IsCanonicalCatalogBinding(catalog, "unrelated-key", exactPath));
        Assert.False(LmStudioModelFileBinding.IsCanonicalCatalogBinding(catalog, catalog.Tag, unrelatedPath));
    }

    [Fact]
    public void AutomaticEligibilityAcceptsCurrentProofAndRejectsReplacementAndLegacyEvidence()
    {
        using var temporary = new TemporaryDirectory();
        var catalog = new ModelCatalogService().LoadBundled().Models.Single(model =>
            string.Equals(model.Provider, ModelProviders.LmStudio, StringComparison.Ordinal));
        var modelPath = Path.Combine(
            temporary.Path,
            Path.Combine(catalog.IndexedModelPath!.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        File.WriteAllText(modelPath, "AAAA");
        Assert.True(LmStudioModelFileBinding.TryOpen(modelPath, out var stream, out var proof));
        stream.Dispose();
        var registration = new LocalModelRegistration(
            ModelProviders.LmStudio,
            catalog.Tag,
            new string('a', 64),
            modelPath,
            DateTimeOffset.UtcNow,
            proof.Length,
            proof.LastWriteTimeUtc,
            proof.FileIdentity);

        Assert.True(LmStudioModelFileBinding.MatchesRegistration(registration, catalog));
        Assert.True(ReviewerService.RegistrationFileIsCurrent(registration, catalog));
        Assert.False(ReviewerService.RegistrationFileIsCurrent(registration with { FileIdentity = null }, catalog));

        var replacement = modelPath + ".replacement";
        File.WriteAllText(replacement, "BBBB");
        File.SetLastWriteTimeUtc(replacement, proof.LastWriteTimeUtc.UtcDateTime);
        File.Move(replacement, modelPath, overwrite: true);

        var replacementInfo = new FileInfo(modelPath);
        Assert.Equal(proof.Length, replacementInfo.Length);
        Assert.Equal(proof.LastWriteTimeUtc.UtcDateTime, replacementInfo.LastWriteTimeUtc);
        Assert.False(ReviewerService.RegistrationFileIsCurrent(registration, catalog));
    }

    [Fact]
    public async Task GenerationIsBoundedNonStreamingAndRequiresExactModelIdentity()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            FakeHttpMessageHandler.Json("""
                {
                  "model": "model-9b:1",
                  "choices": [{
                    "message": { "role": "assistant", "content": "ADVISORY" },
                    "finish_reason": "stop"
                  }],
                  "usage": { "prompt_tokens": 12, "completion_tokens": 4 }
                }
                """)));
        using var client = CreateClient(handler);

        var result = await client.GenerateAsync(
            "model-9b",
            "model-9b:1",
            "Inspect supplied text only.",
            256);

        Assert.Equal(LmStudioClient.ProviderName, result.Provider);
        Assert.Equal("ADVISORY", result.Response);
        Assert.Equal(12, result.PromptTokens);
        Assert.Equal(4, result.CompletionTokens);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("model-9b:1", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(256, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("user", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task GenerationRejectsAResponseForADifferentModelIdentity()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            FakeHttpMessageHandler.Json("""
                {
                  "model": "other-model:1",
                  "choices": [{ "message": { "content": "UNTRUSTED" }, "finish_reason": "stop" }]
                }
                """)));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<LmStudioException>(() => client.GenerateAsync(
            "model-9b",
            "model-9b:1",
            "Inspect.",
            128));

        Assert.Equal("MODEL_RESPONSE_IDENTITY_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task NativeGenerationExplicitlyDisablesReasoningAndRequiresExactInstance()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            FakeHttpMessageHandler.Json("""
                {
                  "model_instance_id": "model-9b:1",
                  "output": [{ "type": "message", "content": "ADVISORY" }],
                  "stats": {
                    "input_tokens": 12,
                    "total_output_tokens": 4,
                    "reasoning_output_tokens": 0
                  }
                }
                """)));
        using var client = CreateClient(handler);

        var result = await client.GenerateReasoningOffAsync(
            "model-9b",
            "model-9b:1",
            "Inspect supplied text only.",
            256);

        Assert.Equal("ADVISORY", result.Response);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("/api/v1/chat", Assert.Single(handler.Requests).Path);
        Assert.Equal("model-9b:1", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("off", body.RootElement.GetProperty("reasoning").GetString());
        Assert.Equal(65_536, body.RootElement.GetProperty("context_length").GetInt32());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
    }

    [Fact]
    public async Task NativeGenerationFailsClosedIfReasoningIsReturned()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            FakeHttpMessageHandler.Json("""
                {
                  "model_instance_id": "model-9b:1",
                  "output": [
                    { "type": "reasoning", "content": "hidden" },
                    { "type": "message", "content": "ADVISORY" }
                  ],
                  "stats": { "reasoning_output_tokens": 3 }
                }
                """)));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<LmStudioException>(() => client.GenerateReasoningOffAsync(
            "model-9b",
            "model-9b:1",
            "Inspect.",
            128));

        Assert.Equal("LMSTUDIO_REASONING_NOT_DISABLED", exception.Code);
    }

    [Fact]
    public async Task UnloadAcknowledgesExactInstanceAndPollsUntilAbsent()
    {
        var inventoryCalls = 0;
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath switch
            {
                "/api/v1/models/unload" => FakeHttpMessageHandler.Json(
                    "{\"instance_id\":\"model-9b:1\"}"),
                "/api/v1/models" when ++inventoryCalls == 1 => FakeHttpMessageHandler.Json(
                    "{\"models\":[{\"key\":\"model-9b\",\"loaded_instances\":[]}]}"),
                _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
            }));
        using var client = CreateClient(handler);

        await client.UnloadAndWaitAsync("model-9b", "model-9b:1", TimeSpan.FromSeconds(1));

        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal("/api/v1/models/unload", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal("model-9b:1", body.RootElement.GetProperty("instance_id").GetString());
            },
            request => Assert.Equal("/api/v1/models", request.Path));
    }

    [Theory]
    [InlineData(true, "C:\\Program Files\\LM Studio\\LM Studio.exe", "S-1-5-21-1", "S-1-5-21-1", true)]
    [InlineData(false, "C:\\Program Files\\LM Studio\\LM Studio.exe", "S-1-5-21-1", "S-1-5-21-1", false)]
    [InlineData(false, "C:\\Temp\\LM Studio.exe", "S-1-5-21-2", "S-1-5-21-1", true)]
    [InlineData(false, "C:\\Temp\\node.exe", "S-1-5-21-1", "S-1-5-21-1", true)]
    public void PeerTrustRequiresSignedExpectedExecutableAndCurrentUser(
        bool expected,
        string executable,
        string processSid,
        string currentSid,
        bool signatureValid)
    {
        Assert.Equal(
            expected,
            LmStudioPeerVerifier.IsTrustedIdentity(executable, processSid, currentSid, signatureValid));
    }

    [Fact]
    public async Task ExplicitOptInRealEndpointAcceptsVerifiedPeerWithoutRunningInference()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("THALEN_HELPER_REAL_GPU_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var client = new LmStudioClient();

        Assert.NotNull(await client.GetModelsAsync());
    }

    private static LmStudioClient CreateClient(FakeHttpMessageHandler handler)
        => new(new Uri("http://127.0.0.1:1234"), new HttpClient(handler));
}
