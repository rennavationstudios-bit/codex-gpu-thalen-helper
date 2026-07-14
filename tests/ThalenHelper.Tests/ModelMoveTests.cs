using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class ModelMoveTests
{
    [Fact]
    public async Task PostCopyOwnershipDriftDoesNotMutateOllamaOrDeleteEitherVerifiedCopy()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var source = Path.Combine(temporary.Path, "source-models");
        var destination = Path.Combine(temporary.Path, "destination-models");
        var runtime = new FakeOllamaRuntime(source);
        runtime.SeedModel();
        Directory.CreateDirectory(Path.Combine(source, "blobs"));
        await File.WriteAllTextAsync(Path.Combine(source, "blobs", "sha256-original"), "model bytes");
        var platform = new FakeStartupPlatform
        {
            LoopbackOnly = true,
            Executable = Path.Combine(temporary.Path, "Ollama", "ollama.exe")
        };
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var state = CreateState(source);
        autoStart.Configure(paths, state, enabled: true);
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);
        var mutationsBeforeMove = platform.MutationCount;
        var service = new ModelsMoveService(
            paths,
            store,
            control,
            autoStart,
            (_, _) => { },
            checkpoint =>
            {
                if (checkpoint == ModelsMoveCheckpoint.DestinationVerified)
                {
                    DriftManagedConfig(paths);
                }
            });

        var result = await service.MoveAsync(destination);

        Assert.False(result.Success);
        Assert.Equal("INTEGRATION_OWNERSHIP_DRIFT", result.Code);
        Assert.Contains("manual reconciliation", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(mutationsBeforeMove, platform.MutationCount);
        Assert.Equal(0, platform.StartCount);
        Assert.Equal(0, platform.StopCount);
        Assert.True(File.Exists(Path.Combine(source, "blobs", "sha256-original")));
        Assert.True(File.Exists(Path.Combine(destination, "blobs", "sha256-original")));
        var saved = await store.LoadAsync();
        Assert.Equal(Path.GetFullPath(source), saved?.ModelStorageLocation);
        Assert.Equal(HelperAvailability.Paused, saved?.Availability);
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task OwnershipDriftDuringActivationPreservesBothCopiesAndSkipsRollbackMutation()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var source = Path.Combine(temporary.Path, "source-models");
        var destination = Path.Combine(temporary.Path, "destination-models");
        var runtime = new FakeOllamaRuntime(source);
        runtime.SeedModel();
        Directory.CreateDirectory(Path.Combine(source, "blobs"));
        await File.WriteAllTextAsync(Path.Combine(source, "blobs", "sha256-original"), "model bytes");
        var platform = new FakeStartupPlatform
        {
            LoopbackOnly = true,
            Executable = Path.Combine(temporary.Path, "Ollama", "ollama.exe")
        };
        var drifted = false;
        int? mutationsAtDrift = null;
        runtime.OnTagsRequested = () =>
        {
            if (!drifted)
            {
                drifted = true;
                DriftManagedConfig(paths);
                mutationsAtDrift = platform.MutationCount;
            }
        };
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var state = CreateState(source);
        autoStart.Configure(paths, state, enabled: true);
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);
        var mutationsBeforeMove = platform.MutationCount;
        var service = new ModelsMoveService(paths, store, control, autoStart, (_, _) => { });

        var result = await service.MoveAsync(destination);

        Assert.False(result.Success);
        Assert.Equal("INTEGRATION_OWNERSHIP_DRIFT", result.Code);
        Assert.Contains("manual reconciliation", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(platform.MutationCount > mutationsBeforeMove);
        Assert.NotNull(mutationsAtDrift);
        Assert.Equal(platform.StopCount, platform.MutationCount - mutationsAtDrift!.Value);
        Assert.Equal(0, platform.StartCount);
        Assert.Equal(1, platform.StopCount);
        Assert.True(File.Exists(Path.Combine(source, "blobs", "sha256-original")));
        Assert.True(File.Exists(Path.Combine(destination, "blobs", "sha256-original")));
        var saved = await store.LoadAsync();
        Assert.Equal(Path.GetFullPath(source), saved?.ModelStorageLocation);
        Assert.Equal(HelperAvailability.Paused, saved?.Availability);
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task SuccessfulMoveRemovesSourceOnlyAfterActivationSucceeds()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var source = Path.Combine(temporary.Path, "source-models");
        var destination = Path.Combine(temporary.Path, "destination-models");
        var runtime = new FakeOllamaRuntime(source);
        runtime.SeedModel();
        Directory.CreateDirectory(Path.Combine(source, "blobs"));
        await File.WriteAllTextAsync(Path.Combine(source, "blobs", "sha256-original"), "model bytes");
        var platform = new FakeStartupPlatform
        {
            LoopbackOnly = true,
            Executable = Path.Combine(temporary.Path, "Ollama", "ollama.exe")
        };
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var state = CreateState(source);
        autoStart.Configure(paths, state, enabled: true);
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);
        var service = new ModelsMoveService(paths, store, control, autoStart, (_, _) => { });

        var result = await service.MoveAsync(destination);

        Assert.True(result.Success);
        Assert.Equal("MODELS_MOVED", result.Code);
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(destination, "blobs", "sha256-original")));
        Assert.Equal(Path.GetFullPath(destination), (await store.LoadAsync())?.ModelStorageLocation);
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task ConcurrentSourceAdditionIsPreservedAfterSuccessfulMove()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var source = Path.Combine(temporary.Path, "source-models");
        var destination = Path.Combine(temporary.Path, "destination-models");
        var runtime = new FakeOllamaRuntime(source);
        runtime.SeedModel();
        Directory.CreateDirectory(Path.Combine(source, "blobs"));
        await File.WriteAllTextAsync(Path.Combine(source, "blobs", "sha256-original"), "model bytes");
        var concurrent = Path.Combine(source, "blobs", "concurrent-model");
        runtime.OnTagsRequested = () =>
        {
            if (!File.Exists(concurrent))
            {
                File.WriteAllText(concurrent, "concurrent bytes");
            }
        };
        var platform = new FakeStartupPlatform
        {
            LoopbackOnly = true,
            Executable = Path.Combine(temporary.Path, "Ollama", "ollama.exe")
        };
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var state = CreateState(source);
        autoStart.Configure(paths, state, enabled: true);
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);
        var service = new ModelsMoveService(paths, store, control, autoStart, (_, _) => { });

        var result = await service.MoveAsync(destination);

        Assert.True(result.Success);
        Assert.Equal("MODELS_MOVED_SOURCE_PRESERVED", result.Code);
        Assert.True(File.Exists(concurrent));
        Assert.True(Directory.Exists(source));
        Assert.True(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(destination, "blobs", "concurrent-model")));
        Assert.Equal(Path.GetFullPath(destination), (await store.LoadAsync())?.ModelStorageLocation);
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task WriterAddingContentAfterQuarantineVerificationIsPreservedWithoutRecursiveDeletion()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var source = Path.Combine(temporary.Path, "source-models");
        var destination = Path.Combine(temporary.Path, "destination-models");
        var runtime = new FakeOllamaRuntime(source);
        runtime.SeedModel();
        Directory.CreateDirectory(Path.Combine(source, "blobs"));
        await File.WriteAllTextAsync(Path.Combine(source, "blobs", "sha256-original"), "model bytes");
        var platform = new FakeStartupPlatform
        {
            LoopbackOnly = true,
            Executable = Path.Combine(temporary.Path, "Ollama", "ollama.exe")
        };
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var state = CreateState(source);
        autoStart.Configure(paths, state, enabled: true);
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);
        var concurrentRelative = Path.Combine("blobs", "writer-added");
        var service = new ModelsMoveService(
            paths,
            store,
            control,
            autoStart,
            (_, _) => { },
            checkpoint =>
            {
                if (checkpoint == ModelsMoveCheckpoint.QuarantineVerified)
                {
                    var quarantine = Assert.Single(Directory.GetDirectories(
                        temporary.Path,
                        "source-models.thalen-helper-remove-*",
                        SearchOption.TopDirectoryOnly));
                    File.WriteAllText(Path.Combine(quarantine, concurrentRelative), "writer bytes");
                }
            });

        var result = await service.MoveAsync(destination);

        Assert.True(result.Success);
        Assert.Equal("MODELS_MOVED_SOURCE_PRESERVED", result.Code);
        Assert.True(File.Exists(Path.Combine(source, concurrentRelative)));
        Assert.True(File.Exists(Path.Combine(source, "blobs", "sha256-original")));
        Assert.True(File.Exists(Path.Combine(destination, "blobs", "sha256-original")));
        Assert.Empty(Directory.GetDirectories(
            temporary.Path,
            "source-models.thalen-helper-remove-*",
            SearchOption.TopDirectoryOnly));
        Assert.Equal(Path.GetFullPath(destination), (await store.LoadAsync())?.ModelStorageLocation);
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task LateSiblingWriterAfterFirstDeletionRestoresEveryOwnedSourceFile()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var source = Path.Combine(temporary.Path, "source-models");
        var destination = Path.Combine(temporary.Path, "destination-models");
        var runtime = new FakeOllamaRuntime(source);
        runtime.SeedModel();
        Directory.CreateDirectory(Path.Combine(source, "blobs"));
        await File.WriteAllTextAsync(Path.Combine(source, "blobs", "sha256-original"), "model bytes");
        var originalFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(source, file),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);
        Assert.True(originalFiles.Count >= 2);
        var platform = new FakeStartupPlatform
        {
            LoopbackOnly = true,
            Executable = Path.Combine(temporary.Path, "Ollama", "ollama.exe")
        };
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var state = CreateState(source);
        autoStart.Configure(paths, state, enabled: true);
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);
        var injected = false;
        var concurrentRelative = Path.Combine("blobs", "writer-added-after-delete");
        var service = new ModelsMoveService(
            paths,
            store,
            control,
            autoStart,
            (_, _) => { },
            checkpoint =>
            {
                if (checkpoint == ModelsMoveCheckpoint.OwnedQuarantineFileDeleted && !injected)
                {
                    injected = true;
                    var quarantine = Assert.Single(Directory.GetDirectories(
                        temporary.Path,
                        "source-models.thalen-helper-remove-*",
                        SearchOption.TopDirectoryOnly));
                    File.WriteAllText(Path.Combine(quarantine, concurrentRelative), "late writer bytes");
                }
            });

        var result = await service.MoveAsync(destination);

        Assert.True(injected);
        Assert.True(result.Success);
        Assert.Equal("MODELS_MOVED_SOURCE_PRESERVED", result.Code);
        Assert.Equal("late writer bytes", File.ReadAllText(Path.Combine(source, concurrentRelative)));
        foreach (var original in originalFiles)
        {
            Assert.Equal(original.Value, File.ReadAllBytes(Path.Combine(source, original.Key)));
            Assert.Equal(original.Value, File.ReadAllBytes(Path.Combine(destination, original.Key)));
        }

        Assert.Empty(Directory.GetDirectories(
            temporary.Path,
            "source-models.thalen-helper-remove-*",
            SearchOption.TopDirectoryOnly));
        Assert.Equal(Path.GetFullPath(destination), (await store.LoadAsync())?.ModelStorageLocation);
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task RollbackPreservesDestinationIfAnotherWriterAddsAFile()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var source = Path.Combine(temporary.Path, "source-models");
        var destination = Path.Combine(temporary.Path, "destination-models");
        var runtime = new FakeOllamaRuntime(source);
        runtime.SeedModel();
        Directory.CreateDirectory(Path.Combine(source, "blobs"));
        await File.WriteAllTextAsync(Path.Combine(source, "blobs", "sha256-original"), "model bytes");
        var concurrentDestinationFile = Path.Combine(destination, "concurrent-owner-file");
        runtime.OnTagsRequested = () =>
        {
            if (Directory.Exists(destination) && !File.Exists(concurrentDestinationFile))
            {
                File.WriteAllText(concurrentDestinationFile, "do not delete");
            }
        };
        var platform = new FakeStartupPlatform
        {
            LoopbackOnly = true,
            Executable = Path.Combine(temporary.Path, "Ollama", "ollama.exe"),
            StopSucceeds = false
        };
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var state = CreateState(source);
        autoStart.Configure(paths, state, enabled: true);
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);
        var service = new ModelsMoveService(paths, store, control, autoStart, (_, _) => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MoveAsync(destination));

        Assert.True(File.Exists(concurrentDestinationFile));
        Assert.True(Directory.Exists(destination));
        Assert.True(Directory.Exists(source));
        Assert.Equal(Path.GetFullPath(source), (await store.LoadAsync())?.ModelStorageLocation);
        GpuCoordination.ClearCancellation();
    }

    private static InstallationState CreateState(string source)
        => new()
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            ModelStorageLocation = source,
            HardwareTier = HardwareTier.Entry,
            Availability = HelperAvailability.Enabled,
            ManagedConfigurationSections = ["mcp_servers.local_gpu_reviewer"],
            Preferences = new HelperPreferences(AutoStartOllama: true)
        };

    private static void DriftManagedConfig(ProductPaths paths)
    {
        var original = File.ReadAllText(paths.CodexConfigFile);
        var drifted = original.Replace("tool_timeout_sec = 360", "tool_timeout_sec = 361", StringComparison.Ordinal);
        Assert.NotEqual(original, drifted);
        File.WriteAllText(paths.CodexConfigFile, drifted);
    }
}
