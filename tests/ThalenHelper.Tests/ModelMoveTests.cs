using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class ModelMoveTests
{
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
            Preferences = new HelperPreferences(AutoStartOllama: true)
        };
}
