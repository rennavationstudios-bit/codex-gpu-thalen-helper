using System.Net;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task MockedInstallValidationReinstallRepairPauseResumeReleaseAndDisableStayIsolated()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "Models with spaces ü");
        var profile = CreateProfile(temporary.Path);
        var platform = new FakeStartupPlatform { LoopbackOnly = true, Executable = "ollama.exe" };
        var runtime = new FakeOllamaRuntime(models);
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var manager = new InstallationManager(
            autoStart: autoStart,
            clientFactory: runtime.CreateClient,
            hardwareProvider: () => profile);

        var first = await manager.ConfigureAsync(new InstallationOptions(
            paths,
            RequestedModel: "qwen2.5-coder:1.5b",
            RequestedModelDirectory: models,
            AutoStartOllama: true,
            PullAndValidateModel: true,
            CodexStartupValidator: _ => true));

        Assert.True(first.Success, $"{first.Code}: {first.Message}; startup={first.OllamaStartup?.Code}; health={first.State.LastHealthCheckCode}; warnings={string.Join(" | ", first.Warnings)}");
        Assert.Equal("INSTALLED_AND_VALIDATED", first.Code);
        Assert.Equal(HelperAvailability.Enabled, first.State.Availability);
        Assert.Equal(HardwareTier.Entry, first.State.HardwareTier);
        Assert.True(first.OllamaStartup?.SelectedModelStoredInConfiguredPath);
        Assert.True(first.OllamaStartup?.SelectedModelAvailable);
        Assert.True(File.Exists(paths.CodexConfigFile));
        Assert.True(File.Exists(paths.AgentsOverrideFile));
        Assert.Contains(paths.CodexConfigFile, first.State.FilesCreated, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(paths.AgentsOverrideFile, first.State.FilesCreated, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, runtime.ValidationGenerationCount);
        Assert.True(runtime.UnloadCount >= 1);

        var installedAt = first.State.InstalledAt;
        var second = await manager.ConfigureAsync(new InstallationOptions(
            paths,
            RequestedModel: "qwen2.5-coder:1.5b",
            RequestedModelDirectory: models,
            AutoStartOllama: true,
            PullAndValidateModel: false,
            CodexStartupValidator: _ => true));
        Assert.True(second.Success);
        Assert.Equal(installedAt, second.State.InstalledAt);
        Assert.Contains(paths.AgentsOverrideFile, second.State.FilesCreated, StringComparer.OrdinalIgnoreCase);
        Assert.False(second.CodexConfig.Changed);
        Assert.False(second.AgentsOverride.Changed);

        var repair = await manager.RepairAsync(paths, _ => true);
        Assert.True(repair.Success);

        var store = new StateStore(paths.StateFile);
        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);
        var paused = await control.PauseAsync();
        Assert.Equal("PAUSED", paused.Code);
        Assert.Equal(HelperAvailability.Paused, (await store.LoadAsync())?.Availability);
        var resumed = await control.ResumeAsync();
        Assert.Equal("RESUMED", resumed.Code);
        Assert.Equal(HelperAvailability.Enabled, (await store.LoadAsync())?.Availability);
        var released = await control.ReleaseGpuAsync();
        Assert.True(released.Success);
        var disabled = await control.DisableAsync(disableCodexEntry: true);
        Assert.Equal("DISABLED", disabled.Code);
        Assert.Contains("enabled = false", await File.ReadAllTextAsync(paths.CodexConfigFile), StringComparison.Ordinal);
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task CancellationDuringMockedPullLeavesCodexEntryDisabledAndStateRecoverable()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models");
        var profile = CreateProfile(temporary.Path);
        var platform = new FakeStartupPlatform { LoopbackOnly = true, Executable = "ollama.exe" };
        var runtime = new FakeOllamaRuntime(models) { BlockPullUntilCancelled = true };
        var manager = new InstallationManager(
            autoStart: new OllamaAutoStartManager(runtime.CreateClient, platform),
            clientFactory: runtime.CreateClient,
            hardwareProvider: () => profile);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var cancellationException = await Record.ExceptionAsync(() => manager.ConfigureAsync(
            new InstallationOptions(
                paths,
                RequestedModel: "qwen2.5-coder:1.5b",
                RequestedModelDirectory: models,
                PullAndValidateModel: true,
                CodexStartupValidator: _ => true),
            cancellation.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(cancellationException);

        var state = await new StateStore(paths.StateFile).LoadAsync();
        Assert.Equal(HelperAvailability.Disabled, state?.Availability);
        Assert.Contains("enabled = false", await File.ReadAllTextAsync(paths.CodexConfigFile), StringComparison.Ordinal);
        Assert.False(File.Exists(paths.StateFile + ".tmp"));
    }

    [Fact]
    public async Task EntryTierCannotEnableKeepWarmAndNoModelCannotEnableReviewer()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        new CodexConfigManager().InstallOrRepair(paths, false);
        await store.SaveAsync(new InstallationState { HardwareTier = HardwareTier.Entry, Availability = HelperAvailability.Disabled });
        var control = new ControlService(paths, store);

        Assert.Equal("KEEP_WARM_UNSAFE", (await control.SetKeepWarmAsync(true)).Code);
        Assert.Equal("NO_MODEL", (await control.EnableAsync()).Code);
    }

    [Fact]
    public async Task EnableAndResumeRemainClosedWhenOllamaIsNetworkExposed()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models");
        var runtime = new FakeOllamaRuntime(models);
        runtime.SeedModel();
        var platform = new FakeStartupPlatform { LoopbackOnly = false };
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var state = new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            ModelStorageLocation = models,
            HardwareTier = HardwareTier.Entry,
            Availability = HelperAvailability.Paused,
            Preferences = new HelperPreferences(AutoStartOllama: false)
        };
        autoStart.Configure(paths, state, enabled: false);
        new CodexConfigManager().InstallOrRepair(paths, false);
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);

        var resumed = await control.ResumeAsync();
        Assert.False(resumed.Success);
        Assert.Equal("OLLAMA_NETWORK_EXPOSURE", resumed.Code);
        Assert.Equal(HelperAvailability.Paused, (await store.LoadAsync())?.Availability);

        state.Availability = HelperAvailability.Disabled;
        await store.SaveAsync(state);
        var enabled = await control.EnableAsync();
        Assert.False(enabled.Success);
        Assert.Equal("OLLAMA_NETWORK_EXPOSURE", enabled.Code);
        Assert.Equal(HelperAvailability.Disabled, (await store.LoadAsync())?.Availability);
        Assert.Contains("enabled = false", await File.ReadAllTextAsync(paths.CodexConfigFile), StringComparison.Ordinal);
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task UninstallRemovesOnlyOwnedSectionsAndRestoresOwnedEnvironment()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var originalConfig = "model = \"preserve-me\"" + Environment.NewLine;
        var originalAgents = "# Preserve this user instruction" + Environment.NewLine;
        File.WriteAllText(paths.CodexConfigFile, originalConfig);
        File.WriteAllText(paths.AgentsOverrideFile, originalAgents);
        var configResult = new CodexConfigManager().InstallOrRepair(paths, true);
        var agentsResult = new AgentsOverrideManager().InstallOrRepair(paths, HardwareTier.Entry);
        var state = new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelOwnedByHelper = false,
            ModelStorageLocation = Path.Combine(temporary.Path, "models"),
            ManagedCodexHome = paths.CodexHome,
            HardwareTier = HardwareTier.Entry,
            Availability = HelperAvailability.Enabled,
            FilesModified = [paths.CodexConfigFile, paths.AgentsOverrideFile],
            BackupLocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [paths.CodexConfigFile] = configResult.BackupPath!,
                [paths.AgentsOverrideFile] = agentsResult.BackupPath!
            },
            PreviousUserEnvironment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["OLLAMA_MODELS"] = "prior-models",
                ["OLLAMA_HOST"] = null
            }
        };
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var platform = new FakeStartupPlatform { RunEntry = "owned" };
        platform.UserEnvironment["OLLAMA_MODELS"] = state.ModelStorageLocation;
        platform.UserEnvironment["OLLAMA_HOST"] = "127.0.0.1:11434";
        var runtime = new FakeOllamaRuntime(state.ModelStorageLocation);
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);

        var result = await new UninstallManager(paths, store, autoStart: autoStart, clientFactory: runtime.CreateClient)
            .UninstallAsync(removeOwnedModel: false);

        Assert.True(result.Success);
        Assert.False(result.ModelRemoved);
        Assert.Equal(originalConfig, await File.ReadAllTextAsync(paths.CodexConfigFile));
        Assert.Equal(originalAgents.TrimEnd() + Environment.NewLine, await File.ReadAllTextAsync(paths.AgentsOverrideFile));
        Assert.Null(platform.RunEntry);
        Assert.Equal("prior-models", platform.UserEnvironment["OLLAMA_MODELS"]);
        Assert.Null(platform.UserEnvironment["OLLAMA_HOST"]);
        Assert.False(File.Exists(paths.StateFile));
        var report = await File.ReadAllTextAsync(result.ReportPath);
        Assert.DoesNotContain(temporary.Path, report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pre-existing models were preserved", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedManagedFilesArePreservedByteForByteForManualCleanup()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        new CodexConfigManager().InstallOrRepair(paths, false);
        new AgentsOverrideManager().InstallOrRepair(paths, HardwareTier.Entry);
        var malformedConfig = System.Text.Encoding.UTF8.GetBytes("[[broken\r\n" + ProductInfo.ManagedConfigStart + "\r\n");
        var malformedAgents = System.Text.Encoding.UTF8.GetBytes("# user text\r\n" + ProductInfo.ManagedAgentsStart + "\r\nmissing end");
        await File.WriteAllBytesAsync(paths.CodexConfigFile, malformedConfig);
        await File.WriteAllBytesAsync(paths.AgentsOverrideFile, malformedAgents);
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(new InstallationState
        {
            ManagedCodexHome = paths.CodexHome,
            Availability = HelperAvailability.Enabled,
            FilesCreated = [paths.CodexConfigFile, paths.AgentsOverrideFile]
        });
        var platform = new FakeStartupPlatform();
        var autoStart = new OllamaAutoStartManager(platform: platform);

        var result = await new UninstallManager(paths, store, autoStart: autoStart)
            .UninstallAsync(removeOwnedModel: false);

        Assert.False(result.Success);
        Assert.Equal("UNINSTALL_MANUAL_CLEANUP_REQUIRED", result.Code);
        Assert.Equal(malformedConfig, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(malformedAgents, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.True(File.Exists(paths.StateFile));
        Assert.All(result.ManagedFiles, item => Assert.Equal("manual-cleanup-required", item.Operation));
        Assert.All(result.ManagedFiles, item => Assert.True(File.Exists(item.BackupPath)));
        Assert.Equal(HelperAvailability.Disabled, (await store.LoadAsync())?.Availability);
        GpuCoordination.ClearCancellation();
    }

    private static HardwareProfile CreateProfile(string root)
    {
        var fixture = FixtureFactory.LoadHardwareFixtures().Single(item => item.Name == "nvidia-4gb");
        var profile = FixtureFactory.Create(fixture);
        var driveRoot = Path.GetPathRoot(root)!;
        return profile with
        {
            Volumes =
            [
                new StorageVolume(
                    driveRoot,
                    "NTFS",
                    1000UL * 1024 * 1024 * 1024,
                    500UL * 1024 * 1024 * 1024,
                    StorageMediaType.Ssd,
                    true,
                    false,
                    false,
                    true,
                    null)
            ]
        };
    }
}

internal sealed class FakeOllamaRuntime
{
    private readonly string _modelDirectory;
    private bool _modelAvailable;
    private bool _modelLoaded;

    public FakeOllamaRuntime(string modelDirectory)
    {
        _modelDirectory = modelDirectory;
    }

    public bool BlockPullUntilCancelled { get; set; }
    public Action? OnTagsRequested { get; set; }
    public int ValidationGenerationCount { get; private set; }
    public int UnloadCount { get; private set; }

    public void SeedModel()
    {
        _modelAvailable = true;
        CreateManifest();
    }

    public OllamaClient CreateClient()
    {
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri?.AbsolutePath;
            if (path == "/api/tags")
            {
                OnTagsRequested?.Invoke();
                return FakeHttpMessageHandler.Json(_modelAvailable
                    ? "{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\"}]}"
                    : "{\"models\":[]}");
            }

            if (path == "/api/pull")
            {
                if (BlockPullUntilCancelled)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                _modelAvailable = true;
                CreateManifest();
                return FakeHttpMessageHandler.Json("{\"status\":\"success\"}");
            }

            if (path == "/api/ps")
            {
                return FakeHttpMessageHandler.Json(_modelLoaded
                    ? "{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"size_vram\":100,\"context_length\":2048}]}"
                    : "{\"models\":[]}");
            }

            if (path == "/api/generate")
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                if (body.Contains("\"keep_alive\":0", StringComparison.Ordinal))
                {
                    _modelLoaded = false;
                    UnloadCount++;
                    return FakeHttpMessageHandler.Json("{}");
                }

                _modelLoaded = true;
                ValidationGenerationCount++;
                var response = body.Contains("THALEN_HELPER_OK", StringComparison.Ordinal)
                    ? "THALEN_HELPER_OK"
                    : "OFF_BY_ONE";
                return FakeHttpMessageHandler.Json($"{{\"model\":\"qwen2.5-coder:1.5b\",\"response\":\"{response}\",\"done\":true}}");
            }

            return FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound);
        });
        return new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
    }

    private void CreateManifest()
    {
        var path = Path.Combine(
            _modelDirectory,
            "manifests",
            "registry.ollama.ai",
            "library",
            "qwen2.5-coder",
            "1.5b");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"schemaVersion\":2}");
    }
}
