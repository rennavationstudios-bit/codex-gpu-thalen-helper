using System.Text;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class SafeRepairTests
{
    [Theory]
    [InlineData("mcp_servers.local_gpu_reviewer")]
    [InlineData("\"mcp_servers\".\"local_gpu_reviewer\"")]
    public void ExplicitMigrationReplacesOneContiguousReviewerFamilyAndPreservesUnrelatedToml(
        string reviewerKey)
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var prefix = "# keep prefix exactly\r\nmodel = \"cloud\"\r\n\r\n";
        var suffix = "[mcp_servers.unrelated]\r\n# keep following comment\r\ncommand = \"other.exe\"\r\n";
        var original = prefix
            + $"[{reviewerKey}]\r\n# preserve root comment\r\ncommand = \"docker.exe\" # preserve command comment\r\ncustom_option = \"keep\"\r\n\r\n"
            + $"[{reviewerKey}.env]\r\n# preserve env comment\r\nOLLAMA_HOST = \"http://127.0.0.1:11434\"\r\n\r\n"
            + $"[{reviewerKey}.tools.local_gpu_review]\r\napproval_mode = \"prompt\"\r\n\r\n"
            + suffix;
        File.WriteAllText(paths.CodexConfigFile, original, new UTF8Encoding(false));
        var manager = new CodexConfigManager();

        var preserved = manager.PreviewInstall(paths, enabled: false);
        Assert.False(preserved.Changed);
        Assert.True(preserved.ExistingIntegrationPreserved);
        Assert.Equal(original, File.ReadAllText(paths.CodexConfigFile));

        var preview = manager.PreviewInstall(paths, enabled: false, migrateExisting: true);
        var result = manager.InstallOrRepair(
            paths,
            enabled: false,
            expectedSourceSha256: preview.SourceSha256,
            expectedPlannedSha256: preview.PlannedSha256,
            migrateExisting: true);
        var migrated = File.ReadAllText(paths.CodexConfigFile);

        Assert.True(result.Changed);
        Assert.Equal("migrated-existing", result.Operation);
        Assert.StartsWith(prefix, migrated, StringComparison.Ordinal);
        Assert.EndsWith(suffix, migrated, StringComparison.Ordinal);
        Assert.DoesNotContain("docker.exe", migrated, StringComparison.Ordinal);
        Assert.Contains("custom_option = \"keep\"", migrated, StringComparison.Ordinal);
        Assert.Contains("# preserve root comment", migrated, StringComparison.Ordinal);
        Assert.Contains("# preserve env comment", migrated, StringComparison.Ordinal);
        Assert.Contains("# preserve command comment", migrated, StringComparison.Ordinal);
        Assert.Contains(paths.McpExecutable.Replace("\\", "\\\\", StringComparison.Ordinal), migrated, StringComparison.Ordinal);
        Assert.Contains("env_vars = [\"OLLAMA_MODELS\"]", migrated, StringComparison.Ordinal);
        Assert.Equal(1, Count(migrated, ProductInfo.ManagedConfigStart));
        Assert.Equal(CodexIntegrationOwnership.ManagedValid, manager.InspectOwnership(paths));

        var secondPreview = manager.PreviewInstall(paths, enabled: false, migrateExisting: true);
        var second = manager.InstallOrRepair(
            paths,
            enabled: false,
            expectedSourceSha256: secondPreview.SourceSha256,
            expectedPlannedSha256: secondPreview.PlannedSha256,
            migrateExisting: true);
        Assert.False(second.Changed);
        Assert.Equal(migrated, File.ReadAllText(paths.CodexConfigFile));
        Assert.Equal(1, Count(migrated, ProductInfo.ManagedConfigStart));

        var uninstall = manager.Uninstall(paths, result.BackupPath);
        Assert.Equal("restored-exact-original", uninstall.Operation);
        Assert.Equal(Encoding.UTF8.GetBytes(original), File.ReadAllBytes(paths.CodexConfigFile));
    }

    [Theory]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("CORECLR_PROFILER_PATH_64")]
    public void ExplicitMigrationRefusesRuntimeInjectionEnvironmentWithoutMutation(string name)
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = $$"""
            [mcp_servers.local_gpu_reviewer]
            command = "docker.exe"

            [mcp_servers.local_gpu_reviewer.env]
            CUSTOM_ENV = "keep"
            {{name}} = "injected"
            """;
        File.WriteAllText(paths.CodexConfigFile, original, new UTF8Encoding(false));
        var before = File.ReadAllBytes(paths.CodexConfigFile);

        var manager = new CodexConfigManager();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            manager.PreviewInstall(paths, enabled: false, migrateExisting: true));

        Assert.Contains("No automatic migration or repair was applied", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(paths.CodexConfigFile));
        Assert.Empty(Directory.GetFiles(paths.CodexHome, "config.toml.thalen-helper.*.bak"));
    }

    [Fact]
    public void MigrationRefusesInterleavedOrDisplacedReviewerSubtablesWithoutMutation()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = """
            [mcp_servers.local_gpu_reviewer]
            command = "docker.exe"

            [mcp_servers.unrelated]
            command = "other.exe"

            [mcp_servers.local_gpu_reviewer.env]
            OLLAMA_HOST = "http://127.0.0.1:11434"
            """;
        File.WriteAllText(paths.CodexConfigFile, original, new UTF8Encoding(false));
        var before = File.ReadAllBytes(paths.CodexConfigFile);

        Assert.Throws<InvalidOperationException>(() =>
            new CodexConfigManager().PreviewInstall(paths, enabled: false, migrateExisting: true));
        Assert.Equal(before, File.ReadAllBytes(paths.CodexConfigFile));
        Assert.Empty(Directory.GetFiles(paths.CodexHome, "config.toml.thalen-helper.*.bak"));
    }

    [Fact]
    public async Task DryRunWritesOnlyExplicitDiffAndHashBoundMigrationIsIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var config = """
            # personal config
            model = "cloud"

            [mcp_servers.local_gpu_reviewer]
            command = "docker.exe"

            [mcp_servers.local_gpu_reviewer.env]
            OLLAMA_HOST = "http://127.0.0.1:11434"
            """;
        var agents = "# personal instructions  \r\nUse local_gpu_reviewer only when asked.\r\n\r\n";
        await File.WriteAllTextAsync(paths.CodexConfigFile, config, new UTF8Encoding(false));
        await File.WriteAllTextAsync(paths.AgentsOverrideFile, agents, new UTF8Encoding(false));
        await new StateStore(paths.StateFile).SaveAsync(new InstallationState
        {
            ProductVersion = "0.1.0-beta.4",
            ManagedCodexHome = paths.CodexHome,
            ExistingIntegrationPreserved = true,
            HardwareTier = HardwareTier.High,
            Availability = HelperAvailability.Disabled
        });
        var configBefore = await File.ReadAllBytesAsync(paths.CodexConfigFile);
        var agentsBefore = await File.ReadAllBytesAsync(paths.AgentsOverrideFile);
        var stateBefore = await File.ReadAllBytesAsync(paths.StateFile);
        var contextPath = InstallContextStore.GetPath(paths.InstallDirectory);
        var diffPath = Path.Combine(temporary.Path, "review", "protected-repair.diff");
        var manager = new InstallationManager(
            hardwareProvider: () => throw new InvalidOperationException("Dry-run must not inspect runtime hardware."));

        var preview = await manager.PreviewRepairAsync(paths, diffPath, migrateExisting: true);

        Assert.True(File.Exists(diffPath));
        Assert.Contains("docker.exe", await File.ReadAllTextAsync(diffPath), StringComparison.Ordinal);
        Assert.Equal(configBefore, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(agentsBefore, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(paths.StateFile));
        Assert.False(File.Exists(contextPath));
        Assert.Empty(Directory.GetFiles(paths.CodexHome, "*.thalen-helper.*.bak"));

        var binding = new RepairHashBinding(
            preview.CodexConfig.SourceSha256,
            preview.CodexConfig.PlannedSha256,
            preview.AgentsOverride.SourceSha256,
            preview.AgentsOverride.PlannedSha256);
        manager = new InstallationManager(hardwareProvider: ReviewerHardware);
        var applied = await manager.RepairAsync(
            paths,
            codexStartupValidator: _ => true,
            binding: binding,
            migrateExisting: true);

        Assert.True(applied.Success);
        Assert.False(applied.State.ExistingIntegrationPreserved);
        Assert.Equal(ProductInfo.Version, (await new StateStore(paths.StateFile).LoadAsync())!.ProductVersion);
        var updatedAgents = await File.ReadAllTextAsync(paths.AgentsOverrideFile);
        Assert.StartsWith(agents, updatedAgents, StringComparison.Ordinal);
        Assert.Equal(1, Count(updatedAgents, ProductInfo.ManagedAgentsStart));
        Assert.Contains("Use local_gpu_reviewer only when asked.", updatedAgents, StringComparison.Ordinal);

        var secondDiffPath = Path.Combine(temporary.Path, "review", "protected-repair-second.diff");
        var secondPreview = await manager.PreviewRepairAsync(paths, secondDiffPath, migrateExisting: true);
        var second = await manager.RepairAsync(
            paths,
            codexStartupValidator: _ => true,
            binding: new RepairHashBinding(
                secondPreview.CodexConfig.SourceSha256,
                secondPreview.CodexConfig.PlannedSha256,
                secondPreview.AgentsOverride.SourceSha256,
                secondPreview.AgentsOverride.PlannedSha256),
            migrateExisting: true);
        Assert.True(second.Success);
        Assert.False(second.CodexConfig.Changed);
        Assert.False(second.AgentsOverride.Changed);
        Assert.Equal(1, Count(await File.ReadAllTextAsync(paths.CodexConfigFile), ProductInfo.ManagedConfigStart));
        Assert.Equal(1, Count(await File.ReadAllTextAsync(paths.AgentsOverrideFile), ProductInfo.ManagedAgentsStart));
    }

    [Fact]
    public async Task DryRunRefusesToOverwriteAnExistingDiffFileWithoutProtectedMutation()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await File.WriteAllTextAsync(
            paths.CodexConfigFile,
            "[mcp_servers.local_gpu_reviewer]\ncommand = \"docker.exe\"\n",
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(paths.AgentsOverrideFile, "# personal local_gpu_reviewer guidance\n");
        await new StateStore(paths.StateFile).SaveAsync(new InstallationState
        {
            ManagedCodexHome = paths.CodexHome,
            ExistingIntegrationPreserved = true,
            HardwareTier = HardwareTier.High,
            Availability = HelperAvailability.Disabled
        });
        var diffPath = Path.Combine(temporary.Path, "existing.diff");
        var existingDiff = Encoding.UTF8.GetBytes("keep this file unchanged");
        await File.WriteAllBytesAsync(diffPath, existingDiff);
        var configBefore = await File.ReadAllBytesAsync(paths.CodexConfigFile);
        var agentsBefore = await File.ReadAllBytesAsync(paths.AgentsOverrideFile);
        var stateBefore = await File.ReadAllBytesAsync(paths.StateFile);

        await Assert.ThrowsAsync<IOException>(() => new InstallationManager(
                hardwareProvider: () => throw new InvalidOperationException("Dry-run must not inspect runtime hardware."))
            .PreviewRepairAsync(paths, diffPath, migrateExisting: true));

        Assert.Equal(existingDiff, await File.ReadAllBytesAsync(diffPath));
        Assert.Equal(configBefore, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(agentsBefore, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(paths.StateFile));
        Assert.Empty(Directory.GetFiles(paths.CodexHome, "*.thalen-helper.*.bak"));
    }

    [Fact]
    public async Task HashDriftRefusesBeforeEitherProtectedFileOrStateIsWritten()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await File.WriteAllTextAsync(
            paths.CodexConfigFile,
            "[mcp_servers.local_gpu_reviewer]\ncommand = \"docker.exe\"\n",
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(paths.AgentsOverrideFile, "# personal local_gpu_reviewer guidance\n");
        await new StateStore(paths.StateFile).SaveAsync(new InstallationState
        {
            ProductVersion = "0.1.0-beta.4",
            ManagedCodexHome = paths.CodexHome,
            ExistingIntegrationPreserved = true,
            HardwareTier = HardwareTier.High,
            Availability = HelperAvailability.Disabled
        });
        var manager = new InstallationManager(hardwareProvider: ReviewerHardware);
        var diffPath = Path.Combine(temporary.Path, "repair.diff");
        var preview = await manager.PreviewRepairAsync(paths, diffPath, migrateExisting: true);
        var binding = new RepairHashBinding(
            preview.CodexConfig.SourceSha256,
            preview.CodexConfig.PlannedSha256,
            preview.AgentsOverride.SourceSha256,
            preview.AgentsOverride.PlannedSha256);
        var configBefore = await File.ReadAllBytesAsync(paths.CodexConfigFile);
        var stateBefore = await File.ReadAllBytesAsync(paths.StateFile);
        await File.AppendAllTextAsync(paths.AgentsOverrideFile, "# steered after preview\n");
        var driftedAgents = await File.ReadAllBytesAsync(paths.AgentsOverrideFile);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RepairAsync(
            paths,
            codexStartupValidator: _ => true,
            binding: binding,
            migrateExisting: true));

        Assert.Equal(configBefore, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(driftedAgents, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(paths.StateFile));
        Assert.False(File.Exists(InstallContextStore.GetPath(paths.InstallDirectory)));
        Assert.Empty(Directory.GetFiles(paths.CodexHome, "*.thalen-helper.*.bak"));
    }

    [Fact]
    public async Task ChangedRepairRequiresAllFourHashesAndDoesNotInspectHardwareOrWrite()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await File.WriteAllTextAsync(paths.CodexConfigFile, "model = \"cloud\"\n");
        await File.WriteAllTextAsync(paths.AgentsOverrideFile, "# personal\n");
        await new StateStore(paths.StateFile).SaveAsync(new InstallationState
        {
            ProductVersion = "0.1.0-beta.4",
            ManagedCodexHome = paths.CodexHome,
            HardwareTier = HardwareTier.High,
            Availability = HelperAvailability.Disabled
        });
        var config = await File.ReadAllBytesAsync(paths.CodexConfigFile);
        var agents = await File.ReadAllBytesAsync(paths.AgentsOverrideFile);
        var state = await File.ReadAllBytesAsync(paths.StateFile);
        var manager = new InstallationManager(hardwareProvider: () =>
            throw new InvalidOperationException("Hardware must not run before hash authorization."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RepairAsync(paths));

        Assert.Equal(config, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(agents, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.Equal(state, await File.ReadAllBytesAsync(paths.StateFile));
    }

    [Fact]
    public async Task StateSaveFailureRollsBackBothProtectedFilesAndOriginalVersion()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await File.WriteAllTextAsync(
            paths.CodexConfigFile,
            "[mcp_servers.local_gpu_reviewer]\ncommand = \"docker.exe\"\n");
        await File.WriteAllTextAsync(paths.AgentsOverrideFile, "# unrelated personal instructions\n");
        await new StateStore(paths.StateFile).SaveAsync(new InstallationState
        {
            ProductVersion = "0.1.0-beta.4",
            ManagedCodexHome = paths.CodexHome,
            ExistingIntegrationPreserved = true,
            HardwareTier = HardwareTier.High,
            Availability = HelperAvailability.Disabled
        });
        var beforeConfig = await File.ReadAllBytesAsync(paths.CodexConfigFile);
        var beforeAgents = await File.ReadAllBytesAsync(paths.AgentsOverrideFile);
        var beforeState = await File.ReadAllBytesAsync(paths.StateFile);
        var manager = new InstallationManager(hardwareProvider: ReviewerHardware);
        var preview = await manager.PreviewRepairAsync(
            paths,
            Path.Combine(temporary.Path, "rollback.diff"),
            migrateExisting: true);
        manager = new InstallationManager(
            hardwareProvider: ReviewerHardware,
            stateSaver: (_, _, _) => throw new IOException("simulated state save failure"));

        await Assert.ThrowsAsync<IOException>(() => manager.RepairAsync(
            paths,
            codexStartupValidator: _ => true,
            binding: new RepairHashBinding(
                preview.CodexConfig.SourceSha256,
                preview.CodexConfig.PlannedSha256,
                preview.AgentsOverride.SourceSha256,
                preview.AgentsOverride.PlannedSha256),
            migrateExisting: true));

        Assert.Equal(beforeConfig, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(beforeAgents, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.Equal(beforeState, await File.ReadAllBytesAsync(paths.StateFile));
    }

    [Fact]
    public async Task FailedStartupVerificationRollsBackFilesEnvironmentAndRunEntryWithoutProcessMutation()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await File.WriteAllTextAsync(
            paths.CodexConfigFile,
            "[mcp_servers.local_gpu_reviewer]\ncommand = \"docker.exe\"\n");
        await File.WriteAllTextAsync(paths.AgentsOverrideFile, "# unrelated personal instructions\n");
        var modelDirectory = Path.Combine(temporary.Path, "models");
        Directory.CreateDirectory(modelDirectory);
        await new StateStore(paths.StateFile).SaveAsync(new InstallationState
        {
            ProductVersion = "0.1.0-beta.4",
            ManagedCodexHome = paths.CodexHome,
            ExistingIntegrationPreserved = true,
            HardwareTier = HardwareTier.High,
            Availability = HelperAvailability.Enabled,
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "sha256:d7372fd828510000000000000000000000000000000000000000000000000000",
            ModelStorageLocation = modelDirectory,
            Preferences = new HelperPreferences(AutoStartOllama: true)
        });
        var beforeConfig = await File.ReadAllBytesAsync(paths.CodexConfigFile);
        var beforeAgents = await File.ReadAllBytesAsync(paths.AgentsOverrideFile);
        var beforeState = await File.ReadAllBytesAsync(paths.StateFile);
        var platform = new FakeStartupPlatform
        {
            Executable = "ollama.exe",
            LoopbackOnly = true,
            ProcessRunning = false,
            RunEntry = "custom-startup-command"
        };
        platform.UserEnvironment["OLLAMA_MODELS"] = "user-models-before";
        platform.UserEnvironment["OLLAMA_HOST"] = "user-host-before";
        platform.ProcessEnvironment["OLLAMA_MODELS"] = "process-models-before";
        platform.ProcessEnvironment["OLLAMA_HOST"] = "process-host-before";
        OllamaClient OfflineClient()
        {
            var handler = new FakeHttpMessageHandler((_, _) =>
                throw new HttpRequestException("simulated loopback outage"));
            return new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
        }

        var previewManager = new InstallationManager(hardwareProvider: ReviewerHardware);
        var preview = await previewManager.PreviewRepairAsync(
            paths,
            Path.Combine(temporary.Path, "startup-rollback.diff"),
            migrateExisting: true);
        var autoStart = new OllamaAutoStartManager(OfflineClient, platform);
        var manager = new InstallationManager(
            autoStart: autoStart,
            clientFactory: OfflineClient,
            hardwareProvider: ReviewerHardware,
            startupPlatform: platform,
            processEnvironmentReader: name => platform.ProcessEnvironment.GetValueOrDefault(name));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RepairAsync(
            paths,
            codexStartupValidator: _ => true,
            binding: new RepairHashBinding(
                preview.CodexConfig.SourceSha256,
                preview.CodexConfig.PlannedSha256,
                preview.AgentsOverride.SourceSha256,
                preview.AgentsOverride.PlannedSha256),
            migrateExisting: true));

        Assert.Contains("Repair verification failed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(beforeConfig, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(beforeAgents, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.Equal(beforeState, await File.ReadAllBytesAsync(paths.StateFile));
        Assert.Equal("user-models-before", platform.UserEnvironment["OLLAMA_MODELS"]);
        Assert.Equal("user-host-before", platform.UserEnvironment["OLLAMA_HOST"]);
        Assert.Equal("process-models-before", platform.ProcessEnvironment["OLLAMA_MODELS"]);
        Assert.Equal("process-host-before", platform.ProcessEnvironment["OLLAMA_HOST"]);
        Assert.Equal("custom-startup-command", platform.RunEntry);
        Assert.False(platform.ProcessRunning);
        Assert.Equal(0, platform.StartCount);
        Assert.Equal(0, platform.StopCount);
        Assert.False(File.Exists(InstallContextStore.GetPath(paths.InstallDirectory)));
    }

    [Fact]
    public async Task FailedRepairPreservesAConcurrentNewerStateWrite()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await File.WriteAllTextAsync(
            paths.CodexConfigFile,
            "[mcp_servers.local_gpu_reviewer]\ncommand = \"docker.exe\"\n");
        await File.WriteAllTextAsync(paths.AgentsOverrideFile, "# unrelated personal instructions\n");
        await new StateStore(paths.StateFile).SaveAsync(new InstallationState
        {
            ProductVersion = "0.1.0-beta.4",
            ManagedCodexHome = paths.CodexHome,
            ExistingIntegrationPreserved = true,
            HardwareTier = HardwareTier.High,
            Availability = HelperAvailability.Disabled
        });
        var beforeConfig = await File.ReadAllBytesAsync(paths.CodexConfigFile);
        var beforeAgents = await File.ReadAllBytesAsync(paths.AgentsOverrideFile);
        var preview = await new InstallationManager(hardwareProvider: ReviewerHardware).PreviewRepairAsync(
            paths,
            Path.Combine(temporary.Path, "concurrent-state.diff"),
            migrateExisting: true);
        var concurrentState = new InstallationState
        {
            ProductVersion = "concurrent-newer-state",
            ManagedCodexHome = paths.CodexHome,
            HardwareTier = HardwareTier.Enthusiast,
            Availability = HelperAvailability.Disabled
        };
        var manager = new InstallationManager(
            hardwareProvider: ReviewerHardware,
            stateSaver: async (_, _, cancellationToken) =>
            {
                await new StateStore(paths.StateFile).SaveAsync(concurrentState, cancellationToken);
                throw new IOException("simulated failure after a concurrent state write");
            });

        await Assert.ThrowsAsync<IOException>(() => manager.RepairAsync(
            paths,
            codexStartupValidator: _ => true,
            binding: new RepairHashBinding(
                preview.CodexConfig.SourceSha256,
                preview.CodexConfig.PlannedSha256,
                preview.AgentsOverride.SourceSha256,
                preview.AgentsOverride.PlannedSha256),
            migrateExisting: true));

        Assert.Equal(beforeConfig, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(beforeAgents, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.Equal("concurrent-newer-state", (await new StateStore(paths.StateFile).LoadAsync())!.ProductVersion);
    }

    [Fact]
    public async Task RollbackDoesNotOverwriteAStateWrittenAfterTheRepairSaveCompletes()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await File.WriteAllTextAsync(
            paths.CodexConfigFile,
            "[mcp_servers.local_gpu_reviewer]\ncommand = \"docker.exe\"\n");
        await File.WriteAllTextAsync(paths.AgentsOverrideFile, "# unrelated personal instructions\n");
        await new StateStore(paths.StateFile).SaveAsync(new InstallationState
        {
            ProductVersion = "0.1.0-beta.4",
            ManagedCodexHome = paths.CodexHome,
            ExistingIntegrationPreserved = true,
            HardwareTier = HardwareTier.High,
            Availability = HelperAvailability.Disabled
        });
        var preview = await new InstallationManager(hardwareProvider: ReviewerHardware).PreviewRepairAsync(
            paths,
            Path.Combine(temporary.Path, "post-save-race.diff"),
            migrateExisting: true);
        var concurrentState = new InstallationState
        {
            ProductVersion = "post-repair-newer-state",
            ManagedCodexHome = paths.CodexHome,
            HardwareTier = HardwareTier.Enthusiast,
            Availability = HelperAvailability.Disabled
        };
        var manager = new InstallationManager(
            hardwareProvider: ReviewerHardware,
            installContextSaver: _ =>
            {
                new StateStore(paths.StateFile).SaveAsync(concurrentState).GetAwaiter().GetResult();
                throw new IOException("simulated context failure after a newer state write");
            });

        await Assert.ThrowsAsync<IOException>(() => manager.RepairAsync(
            paths,
            codexStartupValidator: _ => true,
            binding: new RepairHashBinding(
                preview.CodexConfig.SourceSha256,
                preview.CodexConfig.PlannedSha256,
                preview.AgentsOverride.SourceSha256,
                preview.AgentsOverride.PlannedSha256),
            migrateExisting: true));

        Assert.Equal("post-repair-newer-state", (await new StateStore(paths.StateFile).LoadAsync())!.ProductVersion);
    }

    [Theory]
    [InlineData(DriveType.Fixed, true)]
    [InlineData(DriveType.Network, false)]
    [InlineData(DriveType.Removable, false)]
    [InlineData(DriveType.Unknown, false)]
    public void RepairDiffOutputRequiresAFixedLocalDrive(DriveType driveType, bool expected)
    {
        Assert.Equal(
            expected,
            InstallationManager.IsFixedLocalPath(@"Z:\private\repair.diff", _ => driveType));
    }

    [Fact]
    public void RepairDiffOutputRejectsUncAndResolverFailures()
    {
        Assert.False(InstallationManager.IsFixedLocalPath(@"\\server\share\repair.diff", _ => DriveType.Fixed));
        Assert.False(InstallationManager.IsFixedLocalPath(
            @"Z:\private\repair.diff",
            _ => throw new IOException("drive lookup failed")));
        Assert.False(InstallationManager.IsFixedLocalPath(
            @"Z:\private\repair.diff",
            _ => throw new UnauthorizedAccessException("drive lookup denied")));
    }

    [Fact]
    public void ScatteredGuidanceCanRemainUserOwnedButNearDuplicateManagedGuidanceIsRefused()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var scattered = "Use local_gpu_reviewer only when useful.\nAnother unrelated rule.\n";
        File.WriteAllText(paths.AgentsOverrideFile, scattered);
        var allowed = new AgentsOverrideManager().PreviewInstall(
            paths,
            HardwareTier.High,
            installReliabilityBaseline: false,
            installLocalGpuGuidance: true,
            forceManagedLocalGpuGuidance: true);
        Assert.True(allowed.Changed);
        Assert.DoesNotContain("-Use local_gpu_reviewer", allowed.Diff, StringComparison.Ordinal);
        Assert.Equal(scattered, File.ReadAllText(paths.AgentsOverrideFile));

        var duplicate = "## Optional local GPU reviewer\nUse local_gpu_health, local_gpu_plan, then local_gpu_review.\n";
        File.WriteAllText(paths.AgentsOverrideFile, duplicate);
        Assert.Throws<InvalidOperationException>(() => new AgentsOverrideManager().PreviewInstall(
            paths,
            HardwareTier.High,
            installReliabilityBaseline: false,
            installLocalGpuGuidance: true,
            forceManagedLocalGpuGuidance: true));
        Assert.Equal(duplicate, File.ReadAllText(paths.AgentsOverrideFile));
    }

    [Fact]
    public async Task DryRunRejectsReparsePointParentBeforeWritingDiff()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await new StateStore(paths.StateFile).SaveAsync(new InstallationState
        {
            ManagedCodexHome = paths.CodexHome,
            HardwareTier = HardwareTier.High,
            Availability = HelperAvailability.Disabled
        });
        var realDirectory = Path.Combine(temporary.Path, "real-diff-directory");
        var linkedDirectory = Path.Combine(temporary.Path, "linked-diff-directory");
        Directory.CreateDirectory(realDirectory);
        try
        {
            Directory.CreateSymbolicLink(linkedDirectory, realDirectory);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var diff = Path.Combine(linkedDirectory, "repair.diff");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new InstallationManager().PreviewRepairAsync(paths, diff));
        Assert.False(File.Exists(diff));
    }

    private static HardwareProfile ReviewerHardware()
        => FixtureFactory.Create(FixtureFactory.LoadHardwareFixtures()
            .Single(item => item.Name == "nvidia-rtx3090-24gb"));

    private static int Count(string content, string value)
    {
        var count = 0;
        for (var index = 0; (index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
        {
            count++;
        }

        return count;
    }
}
