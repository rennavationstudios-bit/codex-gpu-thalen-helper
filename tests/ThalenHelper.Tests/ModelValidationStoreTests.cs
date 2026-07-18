using System.Security.Cryptography;
using System.Text;
using System.Net;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class ModelValidationStoreTests
{
    [Fact]
    public async Task MissingRegistryLoadsAsEmptyAndCorruptRegistryFailsClosed()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ModelValidationStore(temporary.Path);

        Assert.Empty((await store.LoadAsync()).Entries);

        await File.WriteAllTextAsync(store.Path, "{not-json");
        var exception = await Assert.ThrowsAsync<ModelValidationStateException>(() => store.LoadAsync());
        Assert.Equal("VALIDATION_STATE_INVALID", exception.Code);
    }

    [Fact]
    public async Task ConcurrentUpsertsAreAtomicAndPersistOnlyBoundedEvidenceFields()
    {
        using var temporary = new TemporaryDirectory();
        var stores = Enumerable.Range(0, 24)
            .Select(_ => new ModelValidationStore(temporary.Path))
            .ToArray();

        await Task.WhenAll(stores.Select((store, index) => store.UpsertAsync(Entry(
            $"model-{index}:1b",
            Digest($"model-{index}")))));

        var registry = await stores[0].LoadAsync();
        Assert.Equal(24, registry.Entries.Count);
        Assert.Equal(24, registry.Entries.Select(entry => entry.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var json = await File.ReadAllTextAsync(stores[0].Path);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("response", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contextData", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(temporary.Path, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.tmp"));
    }

    [Fact]
    public async Task RemoveDeletesOnlyTheTargetTagIncludingSameTagStaleEvidence()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ModelValidationStore(temporary.Path);
        await store.UpsertAsync(Entry("one:1b", Digest("one")));
        await store.UpsertAsync(Entry("two:1b", Digest("two")));

        await store.RemoveAsync("one:1b");

        var remaining = Assert.Single((await store.LoadAsync()).Entries);
        Assert.Equal("two:1b", remaining.Tag);
    }

    [Fact]
    public async Task LmStudioValidationAttemptRemovesPriorPassBeforePathValidation()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var catalog = new ModelCatalogService().LoadBundled().Models.Single(model =>
            string.Equals(model.Provider, ModelProviders.LmStudio, StringComparison.Ordinal));
        var validationStore = new ModelValidationStore(paths.StateDirectory);
        await validationStore.UpsertAsync(Entry(catalog.Tag, catalog.ExpectedDigest!) with
        {
            Provider = ModelProviders.LmStudio
        });
        await validationStore.UpsertAsync(Entry("qwen3:8b", new string('b', 64)));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            FakeHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError)));
        using var client = new LmStudioClient(
            new Uri("http://127.0.0.1:1234"),
            new HttpClient(handler));
        var service = new LmStudioRegistrationService(paths, new StateStore(paths.StateFile), client);

        var result = await service.ValidateAndEnableAsync(
            catalog.Tag,
            Path.Combine(temporary.Path, "unrelated.gguf"));

        Assert.False(result.Success);
        Assert.Equal("LMSTUDIO_EXACT_FILE_BINDING_UNAVAILABLE", result.Code);
        var remaining = Assert.Single((await validationStore.LoadAsync()).Entries);
        Assert.Equal(ModelProviders.Ollama, remaining.Provider);
        Assert.Empty(handler.Requests);
    }

    private static ModelValidationEntry Entry(string tag, string digest)
        => new(
            tag,
            digest,
            ModelValidationStore.CurrentProtocolVersion,
            DateTimeOffset.UtcNow,
            12,
            34,
            "GPU",
            4UL * 1024 * 1024 * 1024,
            4096);

    private static string Digest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
