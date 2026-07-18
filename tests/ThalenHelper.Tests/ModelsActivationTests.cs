using System.Net;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class ModelsActivationTests
{
    [Fact]
    public async Task ExactPreCopiedStoreActivatesAndPreservesSource()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var source = Path.Combine(temporary.Path, "source-models");
        var destination = Path.Combine(temporary.Path, "destination-models");
        var runtime = new FakeOllamaRuntime(source);
        runtime.SeedModel();
        Directory.CreateDirectory(Path.Combine(source, "blobs"));
        await File.WriteAllTextAsync(Path.Combine(source, "blobs", "sha256-original"), "model bytes");
        CopyTree(source, destination);

        var platform = new FakeStartupPlatform
        {
            LoopbackOnly = true,
            Executable = Path.Combine(temporary.Path, "Ollama", "ollama.exe")
        };
        OllamaClient ClientFactory()
            => platform.ProcessRunning
                ? runtime.CreateClient()
                : new OllamaClient(
                    new Uri("http://127.0.0.1:11434"),
                    new HttpClient(new FakeHttpMessageHandler((_, _) =>
                        throw new HttpRequestException("simulated stopped provider"))));
        var autoStart = new OllamaAutoStartManager(ClientFactory, platform);
        var state = CreateState(source);
        state.PreviousUserEnvironment["OLLAMA_MODELS"] = "pre-helper-value";
        autoStart.Configure(paths, state, enabled: true);
        // Storage activation requires the shared provider to be closed by the user;
        // RequireIdle never stops a running Ollama process automatically.
        platform.ProcessRunning = false;
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        var emptyRevision = (await store.LoadWithRevisionAsync()).Revision;
        await store.SaveIfUnchangedAsync(state, emptyRevision);
        var control = new ControlService(paths, store, ClientFactory, autoStart: autoStart);

        var sourceBefore = Snapshot(source);
        var result = await new ModelsActivationService(paths, store, control, autoStart)
            .ActivateExistingAsync(destination);

        Assert.True(result.Success, $"{result.Code}: {result.Message}");
        Assert.Equal("MODELS_ACTIVATED_SOURCE_PRESERVED", result.Code);
        Assert.True(result.SourcePreserved);
        Assert.False(result.RolledBack);
        Assert.Equal(sourceBefore, Snapshot(source));
        Assert.Equal(sourceBefore, Snapshot(destination));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(temporary.Path),
            path => path.Contains(".staging-", StringComparison.OrdinalIgnoreCase)
                || path.Contains(".thalen-helper-remove-", StringComparison.OrdinalIgnoreCase));
        var saved = await store.LoadAsync();
        Assert.Equal(Path.GetFullPath(destination), saved?.ModelStorageLocation);
        Assert.Null(saved?.ModelStorageTransition);
        Assert.Equal(HelperAvailability.Enabled, saved?.Availability);
        Assert.Equal("pre-helper-value", saved?.PreviousUserEnvironment["OLLAMA_MODELS"]);
        Assert.Equal(Path.GetFullPath(destination), platform.UserEnvironment["OLLAMA_MODELS"]);
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task DestinationMismatchFailsBeforeAnyEnvironmentOrProcessMutation()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var source = Path.Combine(temporary.Path, "source-models");
        var destination = Path.Combine(temporary.Path, "destination-models");
        var runtime = new FakeOllamaRuntime(source);
        runtime.SeedModel();
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "extra"), "not the source");
        var platform = new FakeStartupPlatform { LoopbackOnly = true };
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var state = CreateState(source);
        autoStart.Configure(paths, state, enabled: true);
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);
        var mutationsBefore = platform.MutationCount;

        var result = await new ModelsActivationService(paths, store, control, autoStart)
            .ActivateExistingAsync(destination);

        Assert.False(result.Success);
        Assert.Equal("MODEL_STORAGE_DESTINATION_MISMATCH", result.Code);
        Assert.Equal(mutationsBefore, platform.MutationCount);
        Assert.Equal(0, platform.StartCount);
        Assert.Equal(0, platform.StopCount);
        Assert.Equal(Path.GetFullPath(source), (await store.LoadAsync())?.ModelStorageLocation);
        Assert.True(Directory.Exists(source));
        Assert.True(Directory.Exists(destination));
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task EnvironmentOwnershipDriftFailsBeforePauseOrMutation()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var source = Path.Combine(temporary.Path, "source-models");
        var destination = Path.Combine(temporary.Path, "destination-models");
        var runtime = new FakeOllamaRuntime(source);
        runtime.SeedModel();
        CopyTree(source, destination);
        var platform = new FakeStartupPlatform { LoopbackOnly = true };
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var state = CreateState(source);
        autoStart.Configure(paths, state, enabled: true);
        platform.UserEnvironment["OLLAMA_MODELS"] = Path.Combine(temporary.Path, "foreign");
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);
        var mutationsBefore = platform.MutationCount;

        var result = await new ModelsActivationService(paths, store, control, autoStart)
            .ActivateExistingAsync(destination);

        Assert.False(result.Success);
        Assert.Equal("MODEL_STORAGE_ENVIRONMENT_DRIFT", result.Code);
        Assert.Equal(mutationsBefore, platform.MutationCount);
        Assert.Equal(HelperAvailability.Enabled, (await store.LoadAsync())?.Availability);
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task PendingTransitionBlocksResumeAndEnable()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var state = CreateState(Path.Combine(temporary.Path, "models"));
        state.Availability = HelperAvailability.Paused;
        state.ModelStorageTransition = new ModelStorageTransition(
            "operation",
            state.ModelStorageLocation!,
            Path.Combine(temporary.Path, "new-models"),
            HelperAvailability.Enabled,
            state.ModelStorageLocation,
            DateTimeOffset.UtcNow);
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        var emptyRevision = (await store.LoadWithRevisionAsync()).Revision;
        await store.SaveIfUnchangedAsync(state, emptyRevision);
        var control = new ControlService(paths, store);

        var resume = await control.ResumeAsync();
        var enable = await control.EnableAsync();

        Assert.Equal("MODEL_STORAGE_TRANSITION_PENDING", resume.Code);
        Assert.Equal("MODEL_STORAGE_TRANSITION_PENDING", enable.Code);
        Assert.Equal(HelperAvailability.Paused, (await store.LoadAsync())?.Availability);
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task LoadedForeignModelRefusesActivationBeforePauseOrMarker()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var source = Path.Combine(temporary.Path, "source-models");
        var destination = Path.Combine(temporary.Path, "destination-models");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "manifest"), "model bytes");
        CopyTree(source, destination);
        var platform = new FakeStartupPlatform { LoopbackOnly = true, ProcessRunning = true };
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath switch
            {
                "/api/ps" => FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"foreign:latest\"}]}"),
                "/api/tags" => FakeHttpMessageHandler.Json("{\"models\":[]}"),
                _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
            }));
        var autoStart = new OllamaAutoStartManager(
            () => new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler)),
            platform);
        var state = CreateState(source);
        autoStart.Configure(paths, state, enabled: true);
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var control = new ControlService(paths, store, autoStart: autoStart);
        var mutationsBefore = platform.MutationCount;

        var result = await new ModelsActivationService(paths, store, control, autoStart)
            .ActivateExistingAsync(destination);

        Assert.False(result.Success);
        Assert.Equal("OLLAMA_RESTART_NOT_IDLE", result.Code);
        Assert.Equal(mutationsBefore, platform.MutationCount);
        Assert.Equal(0, platform.StopCount);
        Assert.Equal(0, platform.StartCount);
        var saved = await store.LoadAsync();
        Assert.Equal(Path.GetFullPath(source), saved?.ModelStorageLocation);
        Assert.Equal(HelperAvailability.Enabled, saved?.Availability);
        Assert.Null(saved?.ModelStorageTransition);
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task RecoveryRestoresSourceAndClearsDurableMarker()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var source = Path.Combine(temporary.Path, "source-models");
        var destination = Path.Combine(temporary.Path, "destination-models");
        var runtime = new FakeOllamaRuntime(source);
        runtime.SeedModel();
        CopyTree(source, destination);
        var platform = new FakeStartupPlatform
        {
            LoopbackOnly = true,
            Executable = Path.Combine(temporary.Path, "Ollama", "ollama.exe")
        };
        OllamaClient ClientFactory()
            => platform.ProcessRunning
                ? runtime.CreateClient()
                : new OllamaClient(
                    new Uri("http://127.0.0.1:11434"),
                    new HttpClient(new FakeHttpMessageHandler((_, _) =>
                        throw new HttpRequestException("simulated stopped provider"))));
        var autoStart = new OllamaAutoStartManager(ClientFactory, platform);
        var state = CreateState(destination);
        state.Availability = HelperAvailability.Paused;
        state.ModelStorageTransition = new ModelStorageTransition(
            "operation",
            source,
            destination,
            HelperAvailability.Enabled,
            source,
            DateTimeOffset.UtcNow);
        state.PreviousUserEnvironment["OLLAMA_MODELS"] = "pre-helper-value";
        autoStart.Configure(paths, state, enabled: true);
        // Recovery follows the same preservation boundary as activation.
        platform.ProcessRunning = false;
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        var emptyRevision = (await store.LoadWithRevisionAsync()).Revision;
        await store.SaveIfUnchangedAsync(state, emptyRevision);
        var control = new ControlService(paths, store, ClientFactory, autoStart: autoStart);

        var result = await new ModelsActivationService(paths, store, control, autoStart).RecoverAsync();

        Assert.True(result.Success, $"{result.Code}: {result.Message}");
        Assert.Equal("MODEL_STORAGE_RECOVERED_TO_SOURCE", result.Code);
        var saved = await store.LoadAsync();
        Assert.Equal(Path.GetFullPath(source), saved?.ModelStorageLocation);
        Assert.Null(saved?.ModelStorageTransition);
        Assert.Equal(HelperAvailability.Enabled, saved?.Availability);
        Assert.Equal("pre-helper-value", saved?.PreviousUserEnvironment["OLLAMA_MODELS"]);
        Assert.Equal(Path.GetFullPath(source), platform.UserEnvironment["OLLAMA_MODELS"]);
        Assert.True(Directory.Exists(source));
        Assert.True(Directory.Exists(destination));
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task StaleOrdinaryStateWriteCannotErasePendingTransition()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        var stale = CreateState(Path.Combine(temporary.Path, "source-models"));
        await store.SaveAsync(stale);
        var loaded = await store.LoadWithRevisionAsync();
        var transitionState = loaded.State!;
        transitionState.ModelStorageTransition = new ModelStorageTransition(
            "operation",
            transitionState.ModelStorageLocation!,
            Path.Combine(temporary.Path, "destination-models"),
            HelperAvailability.Enabled,
            transitionState.ModelStorageLocation,
            DateTimeOffset.UtcNow);
        await store.SaveIfUnchangedAsync(transitionState, loaded.Revision);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(stale));

        Assert.Contains("transition", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("operation", (await store.LoadAsync())?.ModelStorageTransition?.OperationId);
    }

    [Fact]
    public async Task ExtraEmptyDirectoryFailsExactTreeVerification()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var source = Path.Combine(temporary.Path, "source-models");
        var destination = Path.Combine(temporary.Path, "destination-models");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "manifest"), "model bytes");
        CopyTree(source, destination);
        Directory.CreateDirectory(Path.Combine(destination, "unexpected-empty"));
        var runtime = new FakeOllamaRuntime(source);
        var platform = new FakeStartupPlatform { LoopbackOnly = true };
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var state = CreateState(source);
        autoStart.Configure(paths, state, enabled: true);
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);

        var result = await new ModelsActivationService(paths, store, control, autoStart)
            .ActivateExistingAsync(destination);

        Assert.False(result.Success);
        Assert.Equal("MODEL_STORAGE_DESTINATION_MISMATCH", result.Code);
        Assert.Contains("Directory count", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null((await store.LoadAsync())?.ModelStorageTransition);
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

    private static void CopyTree(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, directory));
            File.SetAttributes(target, File.GetAttributes(directory));
            Directory.SetLastWriteTimeUtc(target, Directory.GetLastWriteTimeUtc(directory));
        }
    }

    private static string Snapshot(string root)
        => string.Join(
            "\n",
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(path => $"{Path.GetRelativePath(root, path)}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}"));
}
