using System.Net;
using System.Text;
using System.Text.Json;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task DeferredProviderSetupPreservesAnExistingValidatedSelectionAndRegistrations()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models");
        var platform = new FakeStartupPlatform { LoopbackOnly = true, Executable = "ollama.exe" };
        var runtime = new FakeOllamaRuntime(models);
        var manager = new InstallationManager(
            autoStart: new OllamaAutoStartManager(runtime.CreateClient, platform),
            clientFactory: runtime.CreateClient,
            hardwareProvider: () => CreateProfile(temporary.Path),
            startupPlatform: platform,
            processEnvironmentReader: name => platform.ProcessEnvironment.GetValueOrDefault(name));

        var first = await manager.ConfigureAsync(new InstallationOptions(
            paths,
            RequestedModel: "qwen2.5-coder:1.5b",
            RequestedModelDirectory: models,
            AutoStartOllama: false,
            PullAndValidateModel: false,
            CodexStartupValidator: _ => true));
        var prior = first.State;
        prior.Availability = HelperAvailability.Enabled;
        prior.SelectedModelProvider = ModelProviders.Ollama;
        prior.RegisteredLocalModels.Add(new LocalModelRegistration(
            ModelProviders.LmStudio,
            "fixture:model",
            new string('a', 64),
            Path.Combine(temporary.Path, "fixture.gguf"),
            DateTimeOffset.UtcNow,
            123,
            DateTimeOffset.UtcNow,
            "fixture-identity"));
        await new StateStore(paths.StateFile).SaveAsync(prior);
        _ = new CodexConfigManager().SetEnabled(paths, true);

        var deferred = await manager.ConfigureAsync(new InstallationOptions(
            paths,
            AutoStartOllama: prior.Preferences.AutoStartOllama,
            DeferModelSelection: true,
            CodexStartupValidator: _ => true));

        Assert.True(deferred.Success);
        Assert.Equal("INSTALLED_MODEL_SETUP_REQUIRED", deferred.Code);
        Assert.Equal(prior.SelectedModel, deferred.State.SelectedModel);
        Assert.Equal(prior.SelectedModelDigest, deferred.State.SelectedModelDigest);
        Assert.Equal(prior.SelectedModelProvider, deferred.State.SelectedModelProvider);
        Assert.Equal(prior.ModelStorageLocation, deferred.State.ModelStorageLocation);
        Assert.Equal(prior.HardwareTier, deferred.State.HardwareTier);
        Assert.Equal(HelperAvailability.Enabled, deferred.State.Availability);
        Assert.Single(deferred.State.RegisteredLocalModels);
        Assert.Equal(0, runtime.PullCount);
        Assert.Equal(0, runtime.ValidationGenerationCount);
    }

    [Fact]
    public async Task ConfigureRejectsCodexHomeMismatchBeforeAnyTargetStateOrContextMutation()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var originalConfig = Encoding.UTF8.GetBytes("model = \"preserve-original\"\r\n");
        var originalAgents = Encoding.UTF8.GetBytes("# Preserve original instructions\r\n");
        await File.WriteAllBytesAsync(paths.CodexConfigFile, originalConfig);
        await File.WriteAllBytesAsync(paths.AgentsOverrideFile, originalAgents);
        await new StateStore(paths.StateFile).SaveAsync(new InstallationState
        {
            HardwareTier = HardwareTier.NoModel,
            Availability = HelperAvailability.Disabled
        });
        InstallContextStore.Save(paths);

        var differentCodexHome = Path.Combine(temporary.Path, "Different configure Codex home");
        Directory.CreateDirectory(differentCodexHome);
        var mismatchedPaths = ProductPaths.Resolve(paths.InstallDirectory, paths.StateDirectory, differentCodexHome);
        var differentConfig = Encoding.UTF8.GetBytes("model = \"preserve-different\"\r\n");
        var differentAgents = Encoding.UTF8.GetBytes("# Preserve different instructions\r\n");
        await File.WriteAllBytesAsync(mismatchedPaths.CodexConfigFile, differentConfig);
        await File.WriteAllBytesAsync(mismatchedPaths.AgentsOverrideFile, differentAgents);
        var contextPath = InstallContextStore.GetPath(paths.InstallDirectory);
        var contextBefore = await File.ReadAllBytesAsync(contextPath);
        var stateBefore = await File.ReadAllBytesAsync(paths.StateFile);
        var manager = new InstallationManager(hardwareProvider: () =>
            throw new InvalidOperationException("Hardware inspection must not run after a route mismatch."));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConfigureAsync(
            new InstallationOptions(mismatchedPaths, DeferModelSelection: true)));

        Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(contextBefore, await File.ReadAllBytesAsync(contextPath));
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(paths.StateFile));
        Assert.Equal(originalConfig, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(originalAgents, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.Equal(differentConfig, await File.ReadAllBytesAsync(mismatchedPaths.CodexConfigFile));
        Assert.Equal(differentAgents, await File.ReadAllBytesAsync(mismatchedPaths.AgentsOverrideFile));
        Assert.False(File.Exists(contextPath + ".tmp"));
        Assert.False(File.Exists(paths.StateFile + ".tmp"));
    }

    [Fact]
    public async Task ReliabilityBaselineRejectsCodexHomeMismatchBeforeAnyTargetStateOrContextMutation()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var originalConfig = Encoding.UTF8.GetBytes("model = \"preserve-original\"\r\n");
        var originalAgents = Encoding.UTF8.GetBytes("# Preserve original instructions\r\n");
        await File.WriteAllBytesAsync(paths.CodexConfigFile, originalConfig);
        await File.WriteAllBytesAsync(paths.AgentsOverrideFile, originalAgents);
        await new StateStore(paths.StateFile).SaveAsync(new InstallationState
        {
            ManagedCodexHome = paths.CodexHome,
            HardwareTier = HardwareTier.NoModel,
            Availability = HelperAvailability.Disabled
        });
        InstallContextStore.Save(paths);

        var differentCodexHome = Path.Combine(temporary.Path, "Different baseline Codex home");
        Directory.CreateDirectory(differentCodexHome);
        var mismatchedPaths = ProductPaths.Resolve(paths.InstallDirectory, paths.StateDirectory, differentCodexHome);
        var differentConfig = Encoding.UTF8.GetBytes("model = \"preserve-different\"\r\n");
        var differentAgents = Encoding.UTF8.GetBytes("# Preserve different instructions\r\n");
        await File.WriteAllBytesAsync(mismatchedPaths.CodexConfigFile, differentConfig);
        await File.WriteAllBytesAsync(mismatchedPaths.AgentsOverrideFile, differentAgents);
        var contextPath = InstallContextStore.GetPath(paths.InstallDirectory);
        var contextBefore = await File.ReadAllBytesAsync(contextPath);
        var stateBefore = await File.ReadAllBytesAsync(paths.StateFile);
        var manager = new InstallationManager();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ConfigureReliabilityBaselineAsync(mismatchedPaths, true, "unused", "unused"));

        Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(contextBefore, await File.ReadAllBytesAsync(contextPath));
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(paths.StateFile));
        Assert.Equal(originalConfig, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(originalAgents, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.Equal(differentConfig, await File.ReadAllBytesAsync(mismatchedPaths.CodexConfigFile));
        Assert.Equal(differentAgents, await File.ReadAllBytesAsync(mismatchedPaths.AgentsOverrideFile));
        Assert.False(File.Exists(contextPath + ".tmp"));
        Assert.False(File.Exists(paths.StateFile + ".tmp"));
    }

    [Fact]
    public async Task RepairRejectsCodexHomeMismatchBeforeAnyTargetOrContextMutation()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var originalConfig = Encoding.UTF8.GetBytes("model = \"preserve-original\"\r\n");
        var originalAgents = Encoding.UTF8.GetBytes("# Preserve original instructions\r\n");
        await File.WriteAllBytesAsync(paths.CodexConfigFile, originalConfig);
        await File.WriteAllBytesAsync(paths.AgentsOverrideFile, originalAgents);
        await new StateStore(paths.StateFile).SaveAsync(new InstallationState
        {
            ManagedCodexHome = paths.CodexHome,
            HardwareTier = HardwareTier.NoModel,
            Availability = HelperAvailability.Disabled
        });
        InstallContextStore.Save(paths);

        var differentCodexHome = Path.Combine(temporary.Path, "Different Codex home");
        Directory.CreateDirectory(differentCodexHome);
        var mismatchedPaths = ProductPaths.Resolve(
            paths.InstallDirectory,
            paths.StateDirectory,
            differentCodexHome);
        var differentConfig = Encoding.UTF8.GetBytes("model = \"preserve-different\"\r\n");
        var differentAgents = Encoding.UTF8.GetBytes("# Preserve different instructions\r\n");
        await File.WriteAllBytesAsync(mismatchedPaths.CodexConfigFile, differentConfig);
        await File.WriteAllBytesAsync(mismatchedPaths.AgentsOverrideFile, differentAgents);

        var contextPath = InstallContextStore.GetPath(paths.InstallDirectory);
        var contextBefore = await File.ReadAllBytesAsync(contextPath);
        var stateBefore = await File.ReadAllBytesAsync(paths.StateFile);
        var manager = new InstallationManager(hardwareProvider: () =>
            throw new InvalidOperationException("Hardware inspection must not run after a route mismatch."));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.RepairAsync(mismatchedPaths, _ =>
                throw new InvalidOperationException("Codex validation must not run after a route mismatch.")));

        Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No managed files or install context were changed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(contextBefore, await File.ReadAllBytesAsync(contextPath));
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(paths.StateFile));
        Assert.Equal(originalConfig, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(originalAgents, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.Equal(differentConfig, await File.ReadAllBytesAsync(mismatchedPaths.CodexConfigFile));
        Assert.Equal(differentAgents, await File.ReadAllBytesAsync(mismatchedPaths.AgentsOverrideFile));
        Assert.False(File.Exists(contextPath + ".tmp"));
        Assert.False(File.Exists(paths.StateFile + ".tmp"));
    }

    [Fact]
    public async Task MatchingRepairRouteRemainsIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await new StateStore(paths.StateFile).SaveAsync(new InstallationState
        {
            ManagedCodexHome = paths.CodexHome,
            HardwareTier = HardwareTier.NoModel,
            Availability = HelperAvailability.Disabled
        });
        InstallContextStore.Save(paths);
        var manager = new InstallationManager(hardwareProvider: () => CreateProfile(temporary.Path));

        var preview = await manager.PreviewRepairAsync(paths, Path.Combine(temporary.Path, "repair.diff"));
        var first = await manager.RepairAsync(
            paths,
            _ => true,
            binding: new RepairHashBinding(
                preview.CodexConfig.SourceSha256,
                preview.CodexConfig.PlannedSha256,
                preview.AgentsOverride.SourceSha256,
                preview.AgentsOverride.PlannedSha256));
        var configAfterFirst = await File.ReadAllBytesAsync(paths.CodexConfigFile);
        var agentsAfterFirst = await File.ReadAllBytesAsync(paths.AgentsOverrideFile);
        var second = await manager.RepairAsync(paths, _ => true);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.False(second.CodexConfig.Changed);
        Assert.False(second.AgentsOverride.Changed);
        Assert.Equal(configAfterFirst, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(agentsAfterFirst, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
    }

    [Fact]
    public async Task GuidedNoFallbackPolicyNeverPullsASecondModelAfterValidationFailure()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models");
        var runtime = new FakeOllamaRuntime(models) { FailValidation = true };
        var platform = new FakeStartupPlatform { LoopbackOnly = true, Executable = "ollama.exe" };
        runtime.EndpointAvailable = () => platform.ProcessRunning;
        var autoStart = new OllamaAutoStartManager(
            runtime.CreateClient,
            platform);
        var manager = new InstallationManager(
            autoStart: autoStart,
            clientFactory: runtime.CreateClient,
            hardwareProvider: () => CreateProfile(temporary.Path),
            resourcePressureValidator: (_, _) => new ResourcePressureCheck(true, "OK", "Test pressure is safe."));

        var outcome = await manager.ConfigureAsync(new InstallationOptions(
            paths,
            RequestedModel: "qwen2.5-coder:1.5b",
            RequestedModelDirectory: models,
            PullAndValidateModel: true,
            CodexStartupValidator: _ => true,
            AllowAutomaticModelFallback: false));

        Assert.False(outcome.Success);
        Assert.Equal("MODEL_VALIDATION_FAILED", outcome.Code);
        Assert.Equal("qwen2.5-coder:1.5b", outcome.State.SelectedModel);
        Assert.Equal(1, runtime.PullCount);
        Assert.Contains("No fallback model was attempted", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(outcome.Warnings, warning =>
            warning.Contains("downgrade", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PullRefusesOwnershipDriftDetectedAfterInventoryWithoutPullOrGeneration()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models");
        var runtime = new FakeOllamaRuntime(models);
        var platform = new FakeStartupPlatform { LoopbackOnly = true, Executable = "ollama.exe" };
        runtime.EndpointAvailable = () => platform.ProcessRunning;
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var manager = new InstallationManager(
            autoStart: autoStart,
            clientFactory: runtime.CreateClient,
            hardwareProvider: () => CreateProfile(temporary.Path),
            resourcePressureValidator: (_, _) => new ResourcePressureCheck(true, "OK", "Test pressure is safe."));
        var tagsRequested = 0;
        runtime.OnTagsRequested = () =>
        {
            tagsRequested++;
            if (tagsRequested == 2)
            {
                File.WriteAllText(
                    paths.CodexConfigFile,
                    "[mcp_servers.local_gpu_reviewer]\r\ncommand = \"external-reviewer.exe\"\r\nenabled = true\r\n");
            }
        };

        var outcome = await manager.ConfigureAsync(new InstallationOptions(
            paths,
            RequestedModel: "qwen2.5-coder:1.5b",
            RequestedModelDirectory: models,
            PullAndValidateModel: true,
            CodexStartupValidator: _ => true,
            AllowAutomaticModelFallback: false));

        Assert.False(outcome.Success);
        Assert.Equal("MODEL_VALIDATION_FAILED", outcome.Code);
        Assert.Equal("INTEGRATION_OWNERSHIP_DRIFT", outcome.State.LastHealthCheckCode);
        Assert.Equal(0, runtime.PullCount);
        Assert.Equal(0, runtime.ValidationGenerationCount);
    }

    [Fact]
    public async Task PublicValidationRefusesOwnershipDriftAfterInventoryBeforeGeneration()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models");
        _ = new CodexConfigManager().InstallOrRepair(paths, enabled: false, startupValidator: _ => true);
        var state = new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            ModelStorageLocation = models,
            ManagedCodexHome = paths.CodexHome,
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Disabled
        };
        var runtime = new FakeOllamaRuntime(models);
        runtime.SeedModel();
        runtime.OnTagsRequested = () => File.WriteAllText(
            paths.CodexConfigFile,
            "[mcp_servers.local_gpu_reviewer]\r\ncommand = \"external-reviewer.exe\"\r\nenabled = true\r\n");
        var manager = new InstallationManager(
            clientFactory: runtime.CreateClient,
            resourcePressureValidator: (_, _) => new ResourcePressureCheck(true, "OK", "Test pressure is safe."));

        var result = await manager.ValidateSelectedModelAsync(paths, state);

        Assert.False(result.Success);
        Assert.Equal("INTEGRATION_OWNERSHIP_DRIFT", result.Code);
        Assert.Equal(0, runtime.PullCount);
        Assert.Equal(0, runtime.ValidationGenerationCount);
    }

    [Fact]
    public async Task PublicValidationRechecksOwnershipBeforeSecondGenerationBoundary()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models");
        _ = new CodexConfigManager().InstallOrRepair(paths, enabled: false, startupValidator: _ => true);
        var state = new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            ModelStorageLocation = models,
            ManagedCodexHome = paths.CodexHome,
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Disabled
        };
        var runtime = new FakeOllamaRuntime(models);
        runtime.SeedModel();
        runtime.OnValidationGenerationCompleted = count =>
        {
            if (count == 1)
            {
                File.WriteAllText(
                    paths.CodexConfigFile,
                    "[mcp_servers.local_gpu_reviewer]\r\ncommand = \"external-reviewer.exe\"\r\nenabled = true\r\n");
            }
        };
        var manager = new InstallationManager(
            clientFactory: runtime.CreateClient,
            resourcePressureValidator: (_, _) => new ResourcePressureCheck(true, "OK", "Test pressure is safe."));

        var result = await manager.ValidateSelectedModelAsync(paths, state);

        Assert.False(result.Success);
        Assert.Equal("INTEGRATION_OWNERSHIP_DRIFT", result.Code);
        Assert.Equal(1, runtime.ValidationGenerationCount);
    }

    [Fact]
    public async Task FinalEnableRefusalLeavesPersistedAvailabilityDisabled()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models");
        var runtime = new FakeOllamaRuntime(models);
        var platform = new FakeStartupPlatform { LoopbackOnly = true, Executable = "ollama.exe" };
        runtime.EndpointAvailable = () => platform.ProcessRunning;
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var manager = new InstallationManager(
            autoStart: autoStart,
            clientFactory: runtime.CreateClient,
            hardwareProvider: () => CreateProfile(temporary.Path),
            resourcePressureValidator: (_, _) => new ResourcePressureCheck(true, "OK", "Test pressure is safe."));
        var tagsRequested = 0;
        runtime.OnTagsRequested = () =>
        {
            tagsRequested++;
            if (tagsRequested == 5)
            {
                File.WriteAllText(
                    paths.CodexConfigFile,
                    "[mcp_servers.local_gpu_reviewer]\r\ncommand = \"external-reviewer.exe\"\r\nenabled = true\r\n");
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConfigureAsync(new InstallationOptions(
            paths,
            RequestedModel: "qwen2.5-coder:1.5b",
            RequestedModelDirectory: models,
            PullAndValidateModel: true,
            CodexStartupValidator: _ => true,
            AllowAutomaticModelFallback: false)));

        var persisted = await new StateStore(paths.StateFile).LoadAsync();
        Assert.NotNull(persisted);
        Assert.Equal(HelperAvailability.Disabled, persisted.Availability);
        Assert.Equal(2, runtime.ValidationGenerationCount);
        Assert.Contains("external-reviewer.exe", await File.ReadAllTextAsync(paths.CodexConfigFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalAutoStartIsPreservedButBlocksAutomaticCertificationWithoutInference()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models");
        var runtime = new FakeOllamaRuntime(models);
        runtime.SeedModel();
        var platform = new FakeStartupPlatform
        {
            ExternalAutoStartArtifact = "Ollama telemetry bootstrap",
            LoopbackOnly = true,
            Executable = "ollama.exe",
            ProcessRunning = true
        };
        platform.UserEnvironment["OLLAMA_MODELS"] = models;
        platform.ProcessEnvironment["OLLAMA_MODELS"] = models;
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
            PullAndValidateModel: false,
            CodexStartupValidator: _ => true));

        Assert.True(outcome.Success);
        Assert.Equal("CONFIGURED_VALIDATION_REQUIRED", outcome.Code);
        Assert.Equal("EXTERNAL_AUTOSTART_UNVERIFIED", outcome.OllamaStartup?.Code);
        Assert.False(outcome.OllamaStartup!.AutoStartConfigured);
        Assert.False(outcome.State.StartupEntryOwnedByHelper);
        Assert.Equal(HelperAvailability.Disabled, outcome.State.Availability);
        Assert.Null(platform.RunEntry);
        Assert.Equal("Ollama telemetry bootstrap", platform.ExternalAutoStartArtifact);
        Assert.Equal(0, runtime.PullCount);
        Assert.Equal(0, runtime.ValidationGenerationCount);
    }

    [Fact]
    public async Task MockedInstallValidationReinstallRepairPauseResumeReleaseAndDisableStayIsolated()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "Models with spaces ü");
        var profile = CreateProfile(temporary.Path);
        var platform = new FakeStartupPlatform { LoopbackOnly = true, Executable = "ollama.exe" };
        var runtime = new FakeOllamaRuntime(models);
        runtime.EndpointAvailable = () => platform.ProcessRunning;
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);
        var manager = new InstallationManager(
            autoStart: autoStart,
            clientFactory: runtime.CreateClient,
            hardwareProvider: () => profile,
            resourcePressureValidator: (_, _) => new ResourcePressureCheck(true, "OK", "Test pressure is safe."),
            startupPlatform: platform,
            processEnvironmentReader: name => platform.ProcessEnvironment.GetValueOrDefault(name));

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
        runtime.EndpointAvailable = () => platform.ProcessRunning;
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
    public async Task ModelRoutingPreferenceIsPersistentIdempotentAndDoesNotRewriteCodexFiles()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        new CodexConfigManager().InstallOrRepair(paths, false);
        var configBefore = await File.ReadAllBytesAsync(paths.CodexConfigFile);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen3:8b",
            SelectedModelDigest = "500a1f067a9f",
            HardwareTier = HardwareTier.Mid,
            Availability = HelperAvailability.Disabled,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection]
        });
        var control = new ControlService(paths, store);

        Assert.Equal("MODEL_ROUTING_AUTOMATIC", (await control.SetModelSelectionModeAsync(ModelSelectionMode.Automatic)).Code);
        Assert.Equal("MODEL_ROUTING_AUTOMATIC", (await control.SetModelSelectionModeAsync(ModelSelectionMode.Automatic)).Code);
        Assert.Equal(ModelSelectionMode.Automatic, (await store.LoadAsync())?.Preferences.ModelSelectionMode);
        Assert.Equal("KEEP_WARM_AUTOMATIC_UNSAFE", (await control.SetKeepWarmAsync(true)).Code);
        Assert.Equal(configBefore, await File.ReadAllBytesAsync(paths.CodexConfigFile));

        Assert.Equal("MODEL_ROUTING_PINNED", (await control.SetModelSelectionModeAsync(ModelSelectionMode.Pinned)).Code);
        Assert.Equal(ModelSelectionMode.Pinned, (await store.LoadAsync())?.Preferences.ModelSelectionMode);
        Assert.Equal(configBefore, await File.ReadAllBytesAsync(paths.CodexConfigFile));
    }

    [Fact]
    public async Task ReleaseGpuObservesTrackedAutomaticRouteDisappearWithoutNameBasedUnload()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models");
        var store = new StateStore(paths.StateFile);
        new CodexConfigManager().InstallOrRepair(paths, false);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            ModelStorageLocation = models,
            HardwareTier = HardwareTier.Entry,
            Availability = HelperAvailability.Enabled,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Preferences = new HelperPreferences(ModelSelectionMode: ModelSelectionMode.Automatic)
        });
        var tracker = new ActiveModelTracker(paths.StateDirectory);
        tracker.Set("qwen3:14b", FullDigest("bdbd181c33f2"));
        var psCalls = 0;
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/ps")
            {
                psCalls++;
                return FakeHttpMessageHandler.Json(psCalls == 1
                    ? "{\"models\":[{\"name\":\"qwen3:14b\",\"digest\":\"sha256:bdbd181c33f20000000000000000000000000000000000000000000000000000\",\"size_vram\":100,\"context_length\":2048}]}"
                    : "{\"models\":[]}");
            }

            if (request.RequestUri?.AbsolutePath == "/api/generate")
            {
                _ = await request.Content!.ReadAsStringAsync(cancellationToken);
                return FakeHttpMessageHandler.Json("{}");
            }

            return FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound);
        });
        OllamaClient ClientFactory()
            => new(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var control = new ControlService(paths, store, ClientFactory);

        var result = await control.ReleaseGpuAsync();

        Assert.True(result.Success, result.Message);
        Assert.Null(tracker.Read());
        Assert.Equal(2, psCalls);
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/api/generate");
    }

    [Fact]
    public async Task PauseDisableAndReleaseNeverUnloadAnUntrackedSelectedModel()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        new CodexConfigManager().InstallOrRepair(paths, false);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen3:14b",
            HardwareTier = HardwareTier.High,
            Availability = HelperAvailability.Enabled,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection]
        });
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/api/ps"
                ? FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen3:14b\",\"size_vram\":100,\"context_length\":2048}]}")
                : FakeHttpMessageHandler.Json("{}")));
        OllamaClient ClientFactory()
            => new(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var control = new ControlService(paths, store, ClientFactory);
        using var displayOnlyActivity = new ReviewActivityTracker(paths.StateDirectory).TryBegin(
            ModelProviders.Ollama,
            "qwen3:14b",
            ReviewActivityPhase.Reviewing);

        try
        {
            Assert.Equal("PAUSED", (await control.PauseAsync()).Code);
            Assert.Equal("GPU_RELEASED", (await control.ReleaseGpuAsync()).Code);
            Assert.Equal("DISABLED", (await control.DisableAsync(disableCodexEntry: false)).Code);
            Assert.Empty(handler.Requests);
            Assert.Equal(ActiveModelTrackerStatus.Absent, new ActiveModelTracker(paths.StateDirectory).Inspect().Status);
            Assert.NotNull(new ReviewActivityTracker(paths.StateDirectory).ReadCurrent());
        }
        finally
        {
            GpuCoordination.ClearCancellation();
        }
    }

    [Fact]
    public async Task StaleTrackedModelFailsClosedWithoutUnloadOrMarkerDeletion()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        new CodexConfigManager().InstallOrRepair(paths, false);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen3:14b",
            HardwareTier = HardwareTier.High,
            Availability = HelperAvailability.Enabled,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection]
        });
        var tracker = new ActiveModelTracker(paths.StateDirectory);
        tracker.Set("qwen3:14b", FullDigest("bdbd181c33f2"));
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/api/ps"
                ? FakeHttpMessageHandler.Json("{\"models\":[]}")
                : FakeHttpMessageHandler.Json("{}")));
        OllamaClient ClientFactory()
            => new(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var control = new ControlService(paths, store, ClientFactory);

        var result = await control.ReleaseGpuAsync();

        Assert.False(result.Success);
        Assert.Equal("GPU_RELEASE_UNCONFIRMED", result.Code);
        Assert.Equal(["/api/ps"], handler.Requests.Select(request => request.Path));
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/api/generate");
        Assert.Equal("qwen3:14b", tracker.Read());
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task PauseAndDisableReportUnconfirmedForStaleTrackedModelWithoutUnloading()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        new CodexConfigManager().InstallOrRepair(paths, false);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen3:14b",
            HardwareTier = HardwareTier.High,
            Availability = HelperAvailability.Enabled,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection]
        });
        var tracker = new ActiveModelTracker(paths.StateDirectory);
        tracker.Set("qwen3:14b", FullDigest("bdbd181c33f2"));
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/api/ps"
                ? FakeHttpMessageHandler.Json("{\"models\":[]}")
                : FakeHttpMessageHandler.Json("{}")));
        OllamaClient ClientFactory()
            => new(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var control = new ControlService(paths, store, ClientFactory);

        var pause = await control.PauseAsync();
        var disable = await control.DisableAsync(disableCodexEntry: false);

        Assert.Equal("PAUSED_UNLOAD_UNCONFIRMED", pause.Code);
        Assert.Equal("DISABLED_UNLOAD_UNCONFIRMED", disable.Code);
        Assert.Equal(["/api/ps", "/api/ps"], handler.Requests.Select(request => request.Path));
        Assert.Equal("qwen3:14b", tracker.Read());
        GpuCoordination.ClearCancellation();
    }

    [Fact]
    public async Task FailedTrackedModelUnloadDoesNotEraseRecoveryMarker()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        new CodexConfigManager().InstallOrRepair(paths, false);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen3:8b",
            HardwareTier = HardwareTier.Mid,
            Availability = HelperAvailability.Enabled,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Preferences = new HelperPreferences(ModelSelectionMode: ModelSelectionMode.Automatic)
        });
        var tracker = new ActiveModelTracker(paths.StateDirectory);
        tracker.Set("qwen3:14b", FullDigest("bdbd181c33f2"));
        OllamaClient FailingClient()
        {
            var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
                FakeHttpMessageHandler.Json("{\"error\":\"offline\"}", HttpStatusCode.ServiceUnavailable)));
            return new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        }
        var control = new ControlService(paths, store, FailingClient);

        var result = await control.ReleaseGpuAsync();

        Assert.False(result.Success);
        Assert.Equal("qwen3:14b", tracker.Read());
    }

    [Fact]
    public async Task ControlRefusesSameNameRuntimeWhenTrackedDigestWasReplaced()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var store = new StateStore(paths.StateFile);
        new CodexConfigManager().InstallOrRepair(paths, false);
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            HardwareTier = HardwareTier.Entry,
            Availability = HelperAvailability.Enabled,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection]
        });
        var tracker = new ActiveModelTracker(paths.StateDirectory);
        tracker.Set("qwen2.5-coder:1.5b", FullDigest("d7372fd82851"));
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/api/ps"
                ? FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"size_vram\":100,\"context_length\":2048}]}")
                : FakeHttpMessageHandler.Json("{}")));
        OllamaClient ClientFactory()
            => new(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var control = new ControlService(paths, store, ClientFactory);

        var result = await control.ReleaseGpuAsync();

        Assert.False(result.Success);
        Assert.Equal("GPU_RELEASE_UNCONFIRMED", result.Code);
        Assert.Equal(["/api/ps"], handler.Requests.Select(request => request.Path));
        Assert.Equal(FullDigest("d7372fd82851"), tracker.ReadReference()?.Digest);
        GpuCoordination.ClearCancellation();
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
        var reviewActivity = new ReviewActivityTracker(paths.StateDirectory);
        using var activity = reviewActivity.TryBegin(
            ModelProviders.Ollama,
            "qwen2.5-coder:1.5b",
            ReviewActivityPhase.Reviewing);
        Assert.True(File.Exists(reviewActivity.Path));
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
        Assert.False(File.Exists(reviewActivity.Path));
        var report = await File.ReadAllTextAsync(result.ReportPath);
        Assert.DoesNotContain(temporary.Path, report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("all model data were preserved", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UninstallAlwaysPreservesModelDataBecauseOllamaDeleteIsNameBased()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        new CodexConfigManager().InstallOrRepair(paths, false);
        var state = new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            SelectedModelOwnedByHelper = true,
            ManagedCodexHome = paths.CodexHome,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Enabled
        };
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        await new ModelValidationStore(paths.StateDirectory).UpsertAsync(new ModelValidationEntry(
            state.SelectedModel,
            "d7372fd828510000000000000000000000000000000000000000000000000000",
            ModelValidationStore.CurrentProtocolVersion,
            DateTimeOffset.UtcNow,
            1,
            1,
            "GPU",
            1,
            2_048));
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath switch
            {
                "/api/tags" => FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\"}]}"),
                "/api/delete" => FakeHttpMessageHandler.Json("{}"),
                _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
            }));
        OllamaClient ClientFactory()
            => new(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        var platform = new FakeStartupPlatform();
        var autoStart = new OllamaAutoStartManager(ClientFactory, platform);

        var result = await new UninstallManager(
            paths,
            store,
            autoStart: autoStart,
            clientFactory: ClientFactory)
            .UninstallAsync(removeOwnedModel: true);

        Assert.True(result.Success);
        Assert.False(result.ModelRemoved);
        Assert.DoesNotContain(handler.Requests, request => request.Path is "/api/tags" or "/api/delete");
        Assert.Contains("model data was preserved", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UninstallReportsUseDistinctCollisionSafePaths()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var manager = new UninstallManager(paths, new StateStore(paths.StateFile));

        var first = await manager.UninstallAsync(removeOwnedModel: false);
        var second = await manager.UninstallAsync(removeOwnedModel: false);
        try
        {
            Assert.NotEqual(first.ReportPath, second.ReportPath);
            Assert.True(File.Exists(first.ReportPath));
            Assert.True(File.Exists(second.ReportPath));
        }
        finally
        {
            File.Delete(first.ReportPath);
            File.Delete(second.ReportPath);
        }
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
        Assert.Equal(HelperAvailability.Enabled, (await store.LoadAsync())?.Availability);
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
            RequestedModelDirectory: @"Z:\models-that-must-not-be-validated",
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
    public async Task ExistingIntegrationDriftFailsBeforeAnyProtectedFileWrite()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var existingConfig = Encoding.UTF8.GetBytes(
            "[mcp_servers.local_gpu_reviewer]\r\ncommand = \"existing-reviewer.exe\"\r\n");
        var concurrentConfig = Encoding.UTF8.GetBytes(
            "model = \"concurrent-user-edit\"\r\n# preserve exactly\r\n");
        var existingAgents = Encoding.UTF8.GetBytes("# Existing instructions\r\n");
        await File.WriteAllBytesAsync(paths.CodexConfigFile, existingConfig);
        await File.WriteAllBytesAsync(paths.AgentsOverrideFile, existingAgents);
        var manager = new InstallationManager(hardwareProvider: () =>
        {
            File.WriteAllBytes(paths.CodexConfigFile, concurrentConfig);
            return CreateProfile(temporary.Path);
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConfigureAsync(
            new InstallationOptions(
                paths,
                RequestedModel: "model-that-must-not-be-validated",
                RequestedModelDirectory: @"Z:\models-that-must-not-be-validated")));

        Assert.Contains("changed after", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(concurrentConfig, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(existingAgents, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.False(File.Exists(paths.StateFile));
    }

    [Fact]
    public async Task ExistingIntegrationAddsNoInvocationGuidanceAndUninstallsExactly()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var preamble = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble();
        var originalConfig = preamble.Concat(Encoding.UTF8.GetBytes(
            "[mcp_servers.local_gpu_reviewer]\r\ncommand = \"existing-reviewer.exe\"\r\nenabled = true\r\n\r\n"))
            .ToArray();
        var originalAgents = preamble.Concat(Encoding.UTF8.GetBytes(
            "# Existing unrelated instructions\r\nKeep comments and whitespace.\r\n\r\n"))
            .ToArray();
        await File.WriteAllBytesAsync(paths.CodexConfigFile, originalConfig);
        await File.WriteAllBytesAsync(paths.AgentsOverrideFile, originalAgents);
        var manager = new InstallationManager(hardwareProvider: () => CreateProfile(temporary.Path));
        var options = new InstallationOptions(
            paths,
            RequestedModel: "model-that-must-not-be-validated",
            RequestedModelDirectory: @"Z:\models-that-must-not-be-validated",
            AutoStartOllama: true,
            PullAndValidateModel: true);

        var first = await manager.ConfigureAsync(options);
        var agentsAfterFirst = await File.ReadAllBytesAsync(paths.AgentsOverrideFile);
        var second = await manager.ConfigureAsync(options);

        Assert.Equal("EXISTING_INTEGRATION_PRESERVED", first.Code);
        Assert.Equal("EXISTING_INTEGRATION_PRESERVED", second.Code);
        Assert.Equal(originalConfig, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(originalAgents, agentsAfterFirst);
        Assert.Equal(originalAgents, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        var installedAgents = Encoding.UTF8.GetString(agentsAfterFirst);
        Assert.Equal(0, Count(installedAgents, ProductInfo.ManagedAgentsStart));
        Assert.DoesNotContain(ProductInfo.ManagedReliabilityStart, installedAgents, StringComparison.Ordinal);
        Assert.Null(second.State.SelectedModel);
        Assert.Null(second.State.ModelStorageLocation);
        Assert.False(second.State.StartupEntryOwnedByHelper);

        var uninstall = await new UninstallManager(paths, new StateStore(paths.StateFile))
            .UninstallAsync(removeOwnedModel: true);

        Assert.True(uninstall.Success);
        Assert.Equal(originalConfig, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(originalAgents, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.False(File.Exists(paths.StateFile));
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

    [Fact]
    public async Task ManagedStateWithExternalConfigDriftCannotMutateRuntime()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var models = Path.Combine(temporary.Path, "models");
        var externalConfig = "[mcp_servers.local_gpu_reviewer]\r\ncommand = \"external-reviewer.exe\"\r\nenabled = true\r\n";
        await File.WriteAllTextAsync(paths.CodexConfigFile, externalConfig);
        var state = new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            SelectedModelOwnedByHelper = true,
            ModelStorageLocation = models,
            ManagedCodexHome = paths.CodexHome,
            Availability = HelperAvailability.Enabled,
            StartupEntryOwnedByHelper = true,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection]
        };
        var store = new StateStore(paths.StateFile);
        await store.SaveAsync(state);
        var runtime = new FakeOllamaRuntime(models);
        var platform = new FakeStartupPlatform { RunEntry = "helper-owned-looking startup" };
        platform.UserEnvironment["OLLAMA_MODELS"] = models;
        var autoStart = new OllamaAutoStartManager(runtime.CreateClient, platform);

        using (var client = runtime.CreateClient())
        {
            var reviewer = new ReviewerService(paths, store, client);
            Assert.Equal("INTEGRATION_OWNERSHIP_DRIFT", (await reviewer.GetHealthAsync()).ErrorCode);
            Assert.Equal(
                "INTEGRATION_OWNERSHIP_DRIFT",
                (await reviewer.ReviewAsync(new ReviewRequest("Do not run"))).ErrorCode);
        }

        var control = new ControlService(paths, store, runtime.CreateClient, autoStart: autoStart);
        Assert.Equal("INTEGRATION_OWNERSHIP_DRIFT", (await control.PauseAsync()).Code);
        Assert.Equal(HelperAvailability.Enabled, (await store.LoadAsync())?.Availability);
        Assert.Equal(0, runtime.RequestCount);
        Assert.Equal(0, platform.MutationCount);

        using var cancellationEvent = GpuCoordination.OpenCancellationEvent();
        cancellationEvent.Reset();
        var uninstall = await new UninstallManager(paths, store, autoStart: autoStart, clientFactory: runtime.CreateClient)
            .UninstallAsync(removeOwnedModel: true);

        Assert.False(uninstall.Success);
        Assert.Equal("UNINSTALL_MANUAL_CLEANUP_REQUIRED", uninstall.Code);
        Assert.Equal(externalConfig, await File.ReadAllTextAsync(paths.CodexConfigFile));
        Assert.True(File.Exists(paths.StateFile));
        Assert.Equal(0, runtime.RequestCount);
        Assert.Equal(0, platform.MutationCount);
        Assert.False(cancellationEvent.WaitOne(0));
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FullDigest(string prefix)
        => prefix + new string('0', 64 - prefix.Length);

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
    public bool FailValidation { get; set; }
    public Func<bool>? EndpointAvailable { get; set; }
    public TaskCompletionSource<bool> PullStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action? OnTagsRequested { get; set; }
    public Action<int>? OnValidationGenerationCompleted { get; set; }
    public int PullCount { get; private set; }
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
            if (EndpointAvailable is not null && !EndpointAvailable())
            {
                throw new HttpRequestException("simulated stopped provider");
            }

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
                PullCount++;
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
                    ? "{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\",\"size_vram\":100,\"context_length\":2048}]}"
                    : "{\"models\":[]}");
            }

            if (path == "/api/generate")
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                var keepAliveZero = document.RootElement.TryGetProperty("keep_alive", out var keepAlive)
                    && string.Equals(keepAlive.GetString(), "0s", StringComparison.Ordinal);
                if (!document.RootElement.TryGetProperty("prompt", out var prompt))
                {
                    return FakeHttpMessageHandler.Json("{}");
                }

                _modelLoaded = true;
                ValidationGenerationCount++;
                OnValidationGenerationCompleted?.Invoke(ValidationGenerationCount);
                var response = FailValidation
                    ? "VALIDATION_FAILED"
                    : prompt.GetString()!.Contains("THALEN_HELPER_OK", StringComparison.Ordinal)
                        ? "THALEN_HELPER_OK"
                        : "OFF_BY_ONE";
                if (keepAliveZero)
                {
                    _modelLoaded = false;
                    UnloadCount++;
                }
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
