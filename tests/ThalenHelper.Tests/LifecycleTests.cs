using System.Net;
using System.Text;
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
            hardwareProvider: () => profile,
            resourcePressureValidator: (_, _) => new ResourcePressureCheck(true, "OK", "Test pressure is safe."));

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

        var requestCountBeforeBaselineChange = runtime.RequestCount;
        var baselinePreview = new AgentsOverrideManager().PreviewInstall(paths, HardwareTier.Entry, true);
        var baselineInstalled = await manager.ConfigureReliabilityBaselineAsync(
            paths,
            true,
            baselinePreview.SourceSha256,
            baselinePreview.PlannedSha256);
        Assert.True(baselineInstalled.Changed);
        var stateAfterBaselineInstall = await new StateStore(paths.StateFile).LoadAsync();
        Assert.True(stateAfterBaselineInstall?.ReliabilityBaselineInstalled);
        Assert.Equal(requestCountBeforeBaselineChange, runtime.RequestCount);
        var nonInteractiveUpgrade = await manager.ConfigureAsync(new InstallationOptions(
            paths,
            RequestedModel: "qwen2.5-coder:1.5b",
            RequestedModelDirectory: models,
            AutoStartOllama: true,
            PullAndValidateModel: false,
            CodexStartupValidator: _ => true));
        Assert.True(nonInteractiveUpgrade.State.ReliabilityBaselineInstalled);
        Assert.Contains(
            ProductInfo.ManagedReliabilityStart,
            await File.ReadAllTextAsync(paths.AgentsOverrideFile),
            StringComparison.Ordinal);
        var requestCountAfterNonInteractiveUpgrade = runtime.RequestCount;
        baselinePreview = new AgentsOverrideManager().PreviewInstall(paths, HardwareTier.Entry, false);
        var baselineRemoved = await manager.ConfigureReliabilityBaselineAsync(
            paths,
            false,
            baselinePreview.SourceSha256,
            baselinePreview.PlannedSha256);
        Assert.True(baselineRemoved.Changed);
        Assert.False((await new StateStore(paths.StateFile).LoadAsync())?.ReliabilityBaselineInstalled);
        Assert.Equal(requestCountAfterNonInteractiveUpgrade, runtime.RequestCount);

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
        using var cancellation = new CancellationTokenSource();
        var configureTask = manager.ConfigureAsync(
            new InstallationOptions(
                paths,
                RequestedModel: "qwen2.5-coder:1.5b",
                RequestedModelDirectory: models,
                PullAndValidateModel: true,
                CodexStartupValidator: _ => true),
            cancellation.Token);
        await runtime.PullStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        var cancellationException = await Record.ExceptionAsync(() => configureTask);
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
        await store.SaveAsync(new InstallationState
        {
            HardwareTier = HardwareTier.Entry,
            Availability = HelperAvailability.Disabled,
            ManagedConfigurationSections = ["mcp_servers.local_gpu_reviewer"]
        });
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
            ManagedConfigurationSections = ["mcp_servers.local_gpu_reviewer"],
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
        var originalConfig = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("model = \"preserve-me\"\r\n\r\n"))
            .ToArray();
        var originalAgents = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("# Preserve this user instruction  \r\n"))
            .ToArray();
        File.WriteAllBytes(paths.CodexConfigFile, originalConfig);
        File.WriteAllBytes(paths.AgentsOverrideFile, originalAgents);
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
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
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
        Assert.Equal(originalConfig, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(originalAgents, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
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

    [Fact]
    public async Task ExistingUnmanagedIntegrationIsPreservedWithoutRuntimeOrControlTakeover()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models-that-must-not-be-created");
        var existingConfig = "[mcp_servers.local_gpu_reviewer]\ncommand = \"existing-reviewer.exe\"\nenabled = true\n";
        var existingAgents = "# Existing local review policy\nUse local_gpu_reviewer only for bounded work.\n";
        await File.WriteAllTextAsync(paths.CodexConfigFile, existingConfig);
        await File.WriteAllTextAsync(paths.AgentsOverrideFile, existingAgents);
        var platform = new FakeStartupPlatform
        {
            LoopbackOnly = true,
            Executable = "ollama.exe",
            RunEntry = "existing startup command"
        };
        platform.UserEnvironment["OLLAMA_MODELS"] = "existing-models";
        var runtime = new FakeOllamaRuntime(models);
        var ensureOllamaInstalledCount = 0;
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var manager = new InstallationManager(
            autoStart: autoStart,
            clientFactory: runtime.CreateClient,
            hardwareProvider: () => CreateProfile(temporary.Path));

        var outcome = await manager.ConfigureAsync(new InstallationOptions(
            paths,
            RequestedModel: "qwen2.5-coder:1.5b",
            RequestedModelDirectory: models,
            AutoStartOllama: true,
            PullAndValidateModel: true,
            CodexStartupValidator: _ => throw new InvalidOperationException("Validator must not run."),
            EnsureOllamaInstalledAsync: _ =>
            {
                ensureOllamaInstalledCount++;
                return Task.CompletedTask;
            }));

        Assert.True(outcome.Success);
        Assert.Equal("EXISTING_INTEGRATION_PRESERVED", outcome.Code);
        Assert.True(outcome.State.ExistingIntegrationPreserved);
        Assert.Equal(HelperAvailability.Disabled, outcome.State.Availability);
        Assert.Null(outcome.State.SelectedModel);
        Assert.Null(outcome.State.ModelStorageLocation);
        Assert.DoesNotContain("mcp_servers.local_gpu_reviewer", outcome.State.ManagedConfigurationSections);
        Assert.Equal(existingConfig, await File.ReadAllTextAsync(paths.CodexConfigFile));
        Assert.Equal(existingAgents, await File.ReadAllTextAsync(paths.AgentsOverrideFile));
        Assert.False(Directory.Exists(models));
        Assert.Equal(0, runtime.RequestCount);
        Assert.Equal(0, ensureOllamaInstalledCount);
        Assert.Equal(0, platform.MutationCount);
        Assert.Equal("existing startup command", platform.RunEntry);
        Assert.Equal("existing-models", platform.UserEnvironment["OLLAMA_MODELS"]);

        var store = new StateStore(paths.StateFile);
        using (var reviewerClient = runtime.CreateClient())
        {
            var reviewer = new ReviewerService(store, reviewerClient);
            Assert.Equal("EXISTING_INTEGRATION_PRESERVED", (await reviewer.GetHealthAsync()).ErrorCode);
            Assert.Equal(
                "EXISTING_INTEGRATION_PRESERVED",
                (await reviewer.ReviewAsync(new ReviewRequest("Do not run"))).ErrorCode);
        }

        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);
        Assert.Equal("EXISTING_INTEGRATION_PRESERVED", (await control.EnableAsync()).Code);
        Assert.Equal("EXISTING_INTEGRATION_PRESERVED", (await control.PauseAsync()).Code);
        Assert.Equal("EXISTING_INTEGRATION_PRESERVED", (await control.ReleaseGpuAsync()).Code);
        Assert.Equal(0, runtime.RequestCount);
        Assert.Equal(0, platform.MutationCount);

        using var cancellationEvent = GpuCoordination.OpenCancellationEvent();
        cancellationEvent.Set();
        try
        {
            var uninstall = await new UninstallManager(paths, store, autoStart: autoStart, clientFactory: runtime.CreateClient)
                .UninstallAsync(removeOwnedModel: true);
            Assert.True(uninstall.Success);
            Assert.Equal(existingConfig, await File.ReadAllTextAsync(paths.CodexConfigFile));
            Assert.Equal(existingAgents, await File.ReadAllTextAsync(paths.AgentsOverrideFile));
            Assert.Equal(0, runtime.RequestCount);
            Assert.Equal(0, platform.MutationCount);
            Assert.False(File.Exists(paths.StateFile));
            Assert.True(cancellationEvent.WaitOne(0));
        }
        finally
        {
            cancellationEvent.Reset();
        }
    }

    [Fact]
    public async Task PreviewDriftRollsBackCodexConfigBeforeAnyRuntimeAction()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models");
        var originalConfig = Encoding.UTF8.GetBytes("model = \"preserve\"\r\n");
        var originalAgents = "# Previewed instructions\r\n";
        await File.WriteAllBytesAsync(paths.CodexConfigFile, originalConfig);
        await File.WriteAllTextAsync(paths.AgentsOverrideFile, originalAgents);
        var preview = new AgentsOverrideManager().PreviewInstall(paths, HardwareTier.Entry, true);
        var ensureOllamaInstalledCount = 0;
        var manager = new InstallationManager(hardwareProvider: () => CreateProfile(temporary.Path));

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConfigureAsync(new InstallationOptions(
            paths,
            RequestedModel: "qwen2.5-coder:1.5b",
            RequestedModelDirectory: models,
            PullAndValidateModel: true,
            CodexStartupValidator: _ =>
            {
                File.AppendAllText(paths.AgentsOverrideFile, "# External edit after preview\r\n");
                return true;
            },
            InstallReliabilityBaseline: true,
            ExpectedAgentsSourceSha256: preview.SourceSha256,
            ExpectedAgentsPlannedSha256: preview.PlannedSha256,
            EnsureOllamaInstalledAsync: _ =>
            {
                ensureOllamaInstalledCount++;
                return Task.CompletedTask;
            })));

        Assert.Equal(originalConfig, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(
            originalAgents + "# External edit after preview\r\n",
            await File.ReadAllTextAsync(paths.AgentsOverrideFile));
        Assert.DoesNotContain(ProductInfo.ManagedConfigStart, await File.ReadAllTextAsync(paths.CodexConfigFile), StringComparison.Ordinal);
        Assert.False(File.Exists(paths.StateFile));
        Assert.Equal(0, ensureOllamaInstalledCount);
        Assert.False(Directory.Exists(models));
    }

    [Fact]
    public async Task ReliabilityBaselineWithoutReviewedPreviewIsRejectedBeforeAnySetupMutation()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models");
        var ensureOllamaInstalledCount = 0;
        var manager = new InstallationManager(hardwareProvider: () => CreateProfile(temporary.Path));

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConfigureAsync(new InstallationOptions(
            paths,
            RequestedModel: "qwen2.5-coder:1.5b",
            RequestedModelDirectory: models,
            PullAndValidateModel: true,
            InstallReliabilityBaseline: true,
            EnsureOllamaInstalledAsync: _ =>
            {
                ensureOllamaInstalledCount++;
                return Task.CompletedTask;
            })));

        Assert.False(File.Exists(paths.CodexConfigFile));
        Assert.False(File.Exists(paths.AgentsOverrideFile));
        Assert.False(File.Exists(paths.StateFile));
        Assert.False(Directory.Exists(models));
        Assert.Equal(0, ensureOllamaInstalledCount);
    }

    [Fact]
    public async Task MissingOwnershipStateCannotProbeOrMutateRuntimeDuringReviewOrUninstall()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "unowned-models");
        var state = new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            SelectedModelOwnedByHelper = true,
            ModelStorageLocation = models,
            ManagedCodexHome = paths.CodexHome,
            Availability = HelperAvailability.Enabled,
            StartupEntryOwnedByHelper = true
        };
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var runtime = new FakeOllamaRuntime(models);
        var platform = new FakeStartupPlatform { RunEntry = "unowned startup" };
        platform.UserEnvironment["OLLAMA_MODELS"] = "unowned environment";
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);

        using (var client = runtime.CreateClient())
        {
            var reviewer = new ReviewerService(store, client);
            Assert.Equal("EXISTING_INTEGRATION_PRESERVED", (await reviewer.GetHealthAsync()).ErrorCode);
            Assert.Equal(
                "EXISTING_INTEGRATION_PRESERVED",
                (await reviewer.ReviewAsync(new ReviewRequest("Do not run"))).ErrorCode);
        }

        var result = await new UninstallManager(paths, store, autoStart: autoStart, clientFactory: runtime.CreateClient)
            .UninstallAsync(removeOwnedModel: true);
        Assert.True(result.Success);
        Assert.Equal(0, runtime.RequestCount);
        Assert.Equal(0, platform.MutationCount);
        Assert.Equal("unowned startup", platform.RunEntry);
        Assert.Equal("unowned environment", platform.UserEnvironment["OLLAMA_MODELS"]);
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
    public TaskCompletionSource<bool> PullStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action? OnTagsRequested { get; set; }
    public int ValidationGenerationCount { get; private set; }
    public int UnloadCount { get; private set; }
    public int RequestCount { get; private set; }

    public void SeedModel()
    {
        _modelAvailable = true;
        CreateManifest();
    }

    public OllamaClient CreateClient()
    {
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            RequestCount++;
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
                PullStarted.TrySetResult(true);
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
