using System.Diagnostics;
using System.Text.Json;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class LmStudioCliModelBindingTests
{
    [Fact]
    public async Task DownloadedAndLoadedInventoriesRequireOneExactKeyPathAndSize()
    {
        using var temporary = new TemporaryDirectory("lm-cli-binding");
        const string indexedPath = "publisher/model/model.gguf";
        const string modelKey = "audited-model";
        const string instanceId = "helper-instance";
        var modelPath = CreateCanonicalModel(temporary.Path, indexedPath, "audited bytes");
        Assert.True(LmStudioModelFileBinding.TryOpen(modelPath, out var file, out var proof));
        file.Dispose();
        var inventory = new FakeInventory(
            JsonSerializer.Serialize(new[]
            {
                new { modelKey, path = indexedPath, type = "llm", format = "gguf", sizeBytes = proof.Length }
            }),
            JsonSerializer.Serialize(new[]
            {
                new { identifier = instanceId, path = indexedPath }
            }));
        var binding = new LmStudioCliModelBinding(inventory, temporary.Path);

        using var lease = binding.AcquireModelPathLease(indexedPath, proof);
        var downloaded = await binding.VerifyDownloadedAsync(modelKey, indexedPath, proof);
        await binding.VerifyLoadedAsync(instanceId, indexedPath, proof);
        inventory.LoadedJson = "[]";
        await binding.VerifyUnloadedAsync(instanceId, indexedPath);

        Assert.Equal(modelKey, downloaded.ModelKey);
        Assert.Equal(indexedPath, downloaded.IndexedPath);
        Assert.Equal(checked((ulong)proof.Length), downloaded.SizeBytes);
    }

    [Fact]
    public async Task DuplicateKeyOrPathAndSameFileRemainingLoadedFailClosed()
    {
        using var temporary = new TemporaryDirectory("lm-cli-collision");
        const string indexedPath = "publisher/model/model.gguf";
        var modelPath = CreateCanonicalModel(temporary.Path, indexedPath, "audited bytes");
        Assert.True(LmStudioModelFileBinding.TryOpen(modelPath, out var file, out var proof));
        file.Dispose();
        var inventory = new FakeInventory(
            JsonSerializer.Serialize(new object[]
            {
                new { modelKey = "audited-model", path = indexedPath, type = "llm", format = "gguf", sizeBytes = proof.Length },
                new { modelKey = "audited-model", path = "other/model/other.gguf", type = "llm", format = "gguf", sizeBytes = proof.Length }
            }),
            JsonSerializer.Serialize(new[]
            {
                new { identifier = "foreign-instance", path = indexedPath }
            }));
        var binding = new LmStudioCliModelBinding(inventory, temporary.Path);

        using var lease = binding.AcquireModelPathLease(indexedPath, proof);
        var collision = await Assert.ThrowsAsync<LmStudioException>(() =>
            binding.VerifyDownloadedAsync("audited-model", indexedPath, proof));
        var stillLoaded = await Assert.ThrowsAsync<LmStudioException>(() =>
            binding.VerifyUnloadedAsync("helper-instance", indexedPath));

        Assert.Equal("LMSTUDIO_MODEL_BINDING_MISMATCH", collision.Code);
        Assert.Equal("LMSTUDIO_UNLOAD_UNCONFIRMED", stillLoaded.Code);
    }

    [Theory]
    [InlineData("../escape/model.gguf")]
    [InlineData("publisher/../model.gguf")]
    [InlineData("C:\\models\\model.gguf")]
    [InlineData("publisher/model/not-a-gguf.bin")]
    public void IndexedPathsRejectTraversalRootedAndNonGgufValues(string indexedPath)
    {
        var exception = Assert.Throws<LmStudioException>(() =>
            LmStudioCliModelBinding.NormalizeIndexedPath(indexedPath, requireGguf: true));

        Assert.Equal("LMSTUDIO_CLI_MALFORMED_RESPONSE", exception.Code);
    }

    [Fact]
    public void ModelNamespaceLeaseAllowsStableModelsRootJunctionAndPinsNestedDirectoriesAgainstRename()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory("lm-model-root-junction");
        const string indexedPath = "publisher/model/model.gguf";
        var lmStudioDirectory = Path.Combine(temporary.Path, ".lmstudio");
        var modelsDirectory = Path.Combine(lmStudioDirectory, "models");
        var stableTarget = Path.Combine(temporary.Path, "stable-models");
        var publisherDirectory = Path.Combine(stableTarget, "publisher");
        var movedPublisherDirectory = Path.Combine(stableTarget, "publisher-moved");
        Directory.CreateDirectory(lmStudioDirectory);
        Directory.CreateDirectory(Path.Combine(publisherDirectory, "model"));
        CreateJunction(modelsDirectory, stableTarget);

        try
        {
            using (LmStudioModelPathLease.Acquire(temporary.Path, indexedPath))
            {
                var renameFailure = Record.Exception(() =>
                    Directory.Move(publisherDirectory, movedPublisherDirectory));

                Assert.True(
                    renameFailure is IOException or UnauthorizedAccessException,
                    renameFailure is null
                        ? "A pinned nested model directory was renamed."
                        : $"Unexpected rename failure: {renameFailure.GetType().Name}: {renameFailure.Message}");
                Assert.True(Directory.Exists(publisherDirectory));
            }

            Directory.Move(publisherDirectory, movedPublisherDirectory);
            Assert.True(Directory.Exists(movedPublisherDirectory));
        }
        finally
        {
            if (Directory.Exists(modelsDirectory))
            {
                Directory.Delete(modelsDirectory);
            }
        }
    }

    [Fact]
    public void ModelNamespaceLeaseBlocksModelsRootJunctionRetargetUntilDisposed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory("lm-model-root-retarget");
        const string indexedPath = "publisher/model/model.gguf";
        var lmStudioDirectory = Path.Combine(temporary.Path, ".lmstudio");
        var modelsDirectory = Path.Combine(lmStudioDirectory, "models");
        var stableTarget = Path.Combine(temporary.Path, "stable-models");
        var retargetedTarget = Path.Combine(temporary.Path, "retargeted-models");
        Directory.CreateDirectory(lmStudioDirectory);
        Directory.CreateDirectory(Path.Combine(stableTarget, "publisher", "model"));
        Directory.CreateDirectory(Path.Combine(retargetedTarget, "publisher", "model"));
        File.WriteAllText(Path.Combine(stableTarget, "marker.txt"), "stable");
        File.WriteAllText(Path.Combine(retargetedTarget, "marker.txt"), "retargeted");
        CreateJunction(modelsDirectory, stableTarget);

        try
        {
            using (LmStudioModelPathLease.Acquire(temporary.Path, indexedPath))
            {
                var retargetFailure = Record.Exception(() =>
                {
                    Directory.Delete(modelsDirectory);
                    CreateJunction(modelsDirectory, retargetedTarget);
                });

                Assert.True(
                    retargetFailure is IOException or UnauthorizedAccessException,
                    retargetFailure is null
                        ? "The pinned models-root junction was retargeted."
                        : $"Unexpected retarget failure: {retargetFailure.GetType().Name}: {retargetFailure.Message}");
                Assert.Equal("stable", File.ReadAllText(Path.Combine(modelsDirectory, "marker.txt")));
            }

            Directory.Delete(modelsDirectory);
            CreateJunction(modelsDirectory, retargetedTarget);
            Assert.Equal("retargeted", File.ReadAllText(Path.Combine(modelsDirectory, "marker.txt")));
        }
        finally
        {
            if (Directory.Exists(modelsDirectory))
            {
                Directory.Delete(modelsDirectory);
            }
        }
    }

    [Fact]
    public void ModelNamespaceLeaseRejectsNestedJunctionComponent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory("lm-model-nested-junction");
        const string indexedPath = "publisher/model/model.gguf";
        var modelsDirectory = Path.Combine(temporary.Path, ".lmstudio", "models");
        var nestedTarget = Path.Combine(temporary.Path, "redirected-publisher");
        var nestedJunction = Path.Combine(modelsDirectory, "publisher");
        Directory.CreateDirectory(modelsDirectory);
        Directory.CreateDirectory(Path.Combine(nestedTarget, "model"));
        CreateJunction(nestedJunction, nestedTarget);

        try
        {
            var exception = Assert.Throws<LmStudioException>(() =>
                LmStudioModelPathLease.Acquire(temporary.Path, indexedPath));

            Assert.Equal("LMSTUDIO_MODEL_NAMESPACE_UNSAFE", exception.Code);
        }
        finally
        {
            Directory.Delete(nestedJunction);
        }
    }

    [Fact]
    public void TrustedCliPathRequiresCanonicalOrdinaryCurrentUserLocationAndSignature()
    {
        using var temporary = new TemporaryDirectory("lm-cli-trust");
        var canonical = Path.Combine(temporary.Path, ".lmstudio", "bin", "lms.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
        File.WriteAllText(canonical, "fixture");
        var sibling = Path.Combine(temporary.Path, "lms.exe");
        File.WriteAllText(sibling, "fixture");

        Assert.True(LmStudioCliProcessInventorySource.IsTrustedExecutablePath(
            canonical,
            temporary.Path,
            signatureValid: true));
        Assert.False(LmStudioCliProcessInventorySource.IsTrustedExecutablePath(
            canonical,
            temporary.Path,
            signatureValid: false));
        Assert.False(LmStudioCliProcessInventorySource.IsTrustedExecutablePath(
            sibling,
            temporary.Path,
            signatureValid: true));
    }

    [Fact]
    public async Task InventoryProcessGetsPrivateClosedInputInsteadOfTheMcpProtocolStream()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("set /p inherited= & exit /b 0");
        using var process = new Process { StartInfo = startInfo };

        Assert.True(LmStudioCliProcessInventorySource.StartWithClosedInput(process));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, process.ExitCode);
        var inventoryStartInfo = LmStudioCliProcessInventorySource.CreateInventoryProcessStartInfo(
            executable,
            "ls");
        Assert.False(inventoryStartInfo.UseShellExecute);
        Assert.True(inventoryStartInfo.CreateNoWindow);
        Assert.True(inventoryStartInfo.RedirectStandardInput);
        Assert.True(inventoryStartInfo.RedirectStandardOutput);
        Assert.True(inventoryStartInfo.RedirectStandardError);
        Assert.Equal(["ls", "--json"], inventoryStartInfo.ArgumentList);
    }

    [Fact]
    public async Task ExplicitOptInRealCliInventoryCompletesWithRedirectedInput()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("THALEN_HELPER_REAL_GPU_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var inventory = new LmStudioCliProcessInventorySource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var downloaded = await inventory.GetDownloadedModelsJsonAsync(timeout.Token);
        var loaded = await inventory.GetLoadedModelsJsonAsync(timeout.Token);
        using var downloadedDocument = JsonDocument.Parse(downloaded);
        using var loadedDocument = JsonDocument.Parse(loaded);

        Assert.Equal(JsonValueKind.Array, downloadedDocument.RootElement.ValueKind);
        Assert.Equal(JsonValueKind.Array, loadedDocument.RootElement.ValueKind);
    }

    [Fact]
    public void ExecutableNamespaceLeaseBlocksDirectoryRenameAndExecutableReplacement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory("lm-cli-namespace-lease");
        var binDirectory = Path.Combine(temporary.Path, ".lmstudio", "bin");
        var movedDirectory = Path.Combine(temporary.Path, ".lmstudio", "bin-moved");
        var executable = Path.Combine(binDirectory, "lms.exe");
        Directory.CreateDirectory(binDirectory);
        File.WriteAllText(executable, "signed-cli-fixture");

        var lease = LmStudioCliExecutableNamespaceLease.Acquire(executable, temporary.Path);
        try
        {
            var renameFailure = Record.Exception(() =>
                Directory.Move(binDirectory, movedDirectory));
            var replacementFailure = Record.Exception(() =>
                File.WriteAllText(executable, "replacement"));

            Assert.True(renameFailure is IOException or UnauthorizedAccessException);
            Assert.True(replacementFailure is IOException or UnauthorizedAccessException);
            Assert.True(Directory.Exists(binDirectory));
            Assert.Equal("signed-cli-fixture", File.ReadAllText(executable));
        }
        finally
        {
            lease.Dispose();
        }

        Directory.Move(binDirectory, movedDirectory);
        Assert.True(Directory.Exists(movedDirectory));
    }

    [Fact]
    public void ExecutableNamespaceLeaseRejectsSymlinkedDirectoryComponent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory("lm-cli-reparse");
        var lmStudioDirectory = Path.Combine(temporary.Path, ".lmstudio");
        var realBinDirectory = Path.Combine(temporary.Path, "real-bin");
        var linkedBinDirectory = Path.Combine(lmStudioDirectory, "bin");
        var executable = Path.Combine(linkedBinDirectory, "lms.exe");
        Directory.CreateDirectory(lmStudioDirectory);
        Directory.CreateDirectory(realBinDirectory);
        File.WriteAllText(Path.Combine(realBinDirectory, "lms.exe"), "signed-cli-fixture");
        try
        {
            Directory.CreateSymbolicLink(linkedBinDirectory, realBinDirectory);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var exception = Assert.Throws<LmStudioException>(() =>
            LmStudioCliExecutableNamespaceLease.Acquire(executable, temporary.Path));

        Assert.Equal("LMSTUDIO_CLI_UNTRUSTED", exception.Code);
    }

    [Fact]
    public async Task ExecutableNamespaceLeaseAllowsProcessStartWhilePathIsPinned()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory("lm-cli-pinned-start");
        var executable = Path.Combine(temporary.Path, ".lmstudio", "bin", "lms.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.Copy(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            executable);

        using var lease = LmStudioCliExecutableNamespaceLease.Acquire(executable, temporary.Path);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("/d");
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add("exit");
        process.StartInfo.ArgumentList.Add("0");

        Assert.True(process.Start());
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void FreshLmOnlyActivationSelectsValidatedModelButExistingOllamaSelectionIsPreserved()
    {
        var catalog = new ModelCatalogService().LoadBundled().Models.Single(model =>
            string.Equals(model.Provider, ModelProviders.LmStudio, StringComparison.Ordinal));
        var registration = new LocalModelRegistration(
            ModelProviders.LmStudio,
            catalog.Tag,
            catalog.ExpectedDigest!,
            "X:\\fixture\\model.gguf",
            DateTimeOffset.UtcNow,
            123,
            DateTimeOffset.UtcNow,
            "fixture-file-id");

        var fresh = LmStudioRegistrationService.BuildActivatedState(
            new InstallationState { HardwareTier = HardwareTier.NoModel },
            catalog,
            registration);
        var ollama = LmStudioRegistrationService.BuildActivatedState(
            new InstallationState
            {
                SelectedModel = "qwen3:8b",
                SelectedModelDigest = new string('a', 64),
                SelectedModelProvider = ModelProviders.Ollama,
                HardwareTier = HardwareTier.Mid
            },
            catalog,
            registration);

        Assert.Equal(catalog.Tag, fresh.SelectedModel);
        Assert.Equal(catalog.ExpectedDigest, fresh.SelectedModelDigest);
        Assert.Equal(ModelProviders.LmStudio, fresh.SelectedModelProvider);
        Assert.Equal(ModelSelector.GetHardwareTier(catalog), fresh.HardwareTier);
        Assert.Equal("qwen3:8b", ollama.SelectedModel);
        Assert.Equal(ModelProviders.Ollama, ollama.SelectedModelProvider);
        Assert.Equal(HardwareTier.Mid, ollama.HardwareTier);
        Assert.True(ollama.Preferences.PreferLmStudioForStandardAndDeep);
        Assert.Equal(ModelSelectionMode.Automatic, ollama.Preferences.ModelSelectionMode);
    }

    private static string CreateCanonicalModel(string userProfile, string indexedPath, string content)
    {
        var path = Path.Combine(
            userProfile,
            ".lmstudio",
            "models",
            Path.Combine(indexedPath.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void CreateJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "/d",
                "/c",
                "mklink",
                "/J",
                junction,
                target
            }
        }) ?? throw new InvalidOperationException("The junction fixture process could not start.");
        process.WaitForExit(10_000);
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, $"Junction fixture creation failed: {error}");
    }

    private sealed class FakeInventory(string downloadedJson, string loadedJson)
        : ILmStudioCliInventorySource
    {
        internal string DownloadedJson { get; set; } = downloadedJson;
        internal string LoadedJson { get; set; } = loadedJson;

        public Task<string> GetDownloadedModelsJsonAsync(CancellationToken cancellationToken)
            => Task.FromResult(DownloadedJson);

        public Task<string> GetLoadedModelsJsonAsync(CancellationToken cancellationToken)
            => Task.FromResult(LoadedJson);
    }
}
