using System.Text;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class ManagedFilesTests
{
    [Fact]
    public void CodexOwnershipInspectionRequiresTheExactManagedContract()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var manager = new CodexConfigManager();

        Assert.Equal(CodexIntegrationOwnership.NotConfigured, manager.InspectOwnership(paths));

        File.WriteAllText(
            paths.CodexConfigFile,
            "[mcp_servers.local_gpu_reviewer]\r\ncommand = \"external.exe\"\r\n");
        Assert.Equal(CodexIntegrationOwnership.ExternalUnmarked, manager.InspectOwnership(paths));

        File.Delete(paths.CodexConfigFile);
        manager.InstallOrRepair(paths, enabled: false);
        Assert.Equal(CodexIntegrationOwnership.ManagedValid, manager.InspectOwnership(paths));

        var managed = File.ReadAllText(paths.CodexConfigFile);
        File.WriteAllText(
            paths.CodexConfigFile,
            managed.Replace("tool_timeout_sec = 360", "tool_timeout_sec = 361", StringComparison.Ordinal));
        Assert.Equal(CodexIntegrationOwnership.ManagedDrift, manager.InspectOwnership(paths));
        Assert.Throws<InvalidOperationException>(() => manager.SetEnabled(paths, true));

        File.WriteAllText(paths.CodexConfigFile, ProductInfo.ManagedConfigStart + "\r\n[[broken");
        Assert.Equal(CodexIntegrationOwnership.Invalid, manager.InspectOwnership(paths));
    }

    [Fact]
    public void CodexConfigRejectsDisplacedMarkersWithoutMutatingOrRemovingUnrelatedToml()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var manager = new CodexConfigManager();
        manager.InstallOrRepair(paths, enabled: false);

        var generated = File.ReadAllText(paths.CodexConfigFile);
        var exactReviewerOutsideMarkers = generated
            .Replace(ProductInfo.ManagedConfigStart, string.Empty, StringComparison.Ordinal)
            .Replace(ProductInfo.ManagedConfigEnd, string.Empty, StringComparison.Ordinal)
            .TrimStart('\r', '\n');
        var displaced =
            $"{ProductInfo.ManagedConfigStart}\n" +
            "[mcp_servers.unrelated]\n" +
            "enabled = false\n" +
            $"{ProductInfo.ManagedConfigEnd}\n\n" +
            exactReviewerOutsideMarkers;
        File.WriteAllText(paths.CodexConfigFile, displaced, new UTF8Encoding(false));
        CodexConfigManager.ValidateToml(displaced, allowEmpty: false);
        var expectedBytes = File.ReadAllBytes(paths.CodexConfigFile);

        Assert.Equal(CodexIntegrationOwnership.Invalid, manager.InspectOwnership(paths));

        Assert.Throws<InvalidOperationException>(() => manager.SetEnabled(paths, enabled: true));
        Assert.Equal(expectedBytes, File.ReadAllBytes(paths.CodexConfigFile));

        Assert.Throws<InvalidOperationException>(() => manager.InstallOrRepair(paths, enabled: true));
        Assert.Equal(expectedBytes, File.ReadAllBytes(paths.CodexConfigFile));

        Assert.Throws<InvalidDataException>(() => manager.Uninstall(paths));
        Assert.Equal(expectedBytes, File.ReadAllBytes(paths.CodexConfigFile));
        Assert.Contains("[mcp_servers.unrelated]\nenabled = false", displaced, StringComparison.Ordinal);
        Assert.Equal(1, Count(displaced, "[mcp_servers.local_gpu_reviewer]"));
        Assert.Empty(Directory.GetFiles(paths.CodexHome, "config.toml.thalen-helper.*.bak"));
    }

    [Fact]
    public void CodexConfigInstallRepairAndUninstallAreSurgicalAndIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = "# user comment\nmodel = \"gpt-test\"\n\n[mcp_servers.existing]\ncommand = \"existing.exe\"\n";
        File.WriteAllText(paths.CodexConfigFile, original, new UTF8Encoding(false));
        var manager = new CodexConfigManager();

        var installed = manager.InstallOrRepair(paths, enabled: false, _ => true);
        var content = File.ReadAllText(paths.CodexConfigFile);
        CodexConfigManager.ValidateToml(content, allowEmpty: false);
        Assert.True(installed.Changed);
        Assert.NotNull(installed.BackupPath);
        Assert.Equal(original, File.ReadAllText(installed.BackupPath!));
        Assert.Contains(original.TrimEnd(), content, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.local_gpu_reviewer]", content, StringComparison.Ordinal);
        Assert.Contains("enabled = false", content, StringComparison.Ordinal);
        Assert.Contains("default_tools_approval_mode = \"prompt\"", content, StringComparison.Ordinal);
        Assert.Contains("env_vars = [\"OLLAMA_MODELS\"]", content, StringComparison.Ordinal);
        Assert.Contains("approval_mode = \"auto\"", content, StringComparison.Ordinal);
        Assert.Contains("approval_mode = \"prompt\"", content, StringComparison.Ordinal);
        Assert.Equal(1, Count(content, "[mcp_servers.local_gpu_reviewer]"));

        var repaired = manager.InstallOrRepair(paths, enabled: false, _ => true);
        Assert.False(repaired.Changed);
        Assert.Equal(content, File.ReadAllText(paths.CodexConfigFile));
        Assert.Equal(1, Count(content, ProductInfo.ManagedConfigStart));

        var alreadyDisabled = manager.SetEnabled(paths, false);
        Assert.False(alreadyDisabled.Changed);
        Assert.Null(alreadyDisabled.BackupPath);
        Assert.Equal("unchanged", alreadyDisabled.Operation);
        Assert.Equal(content, File.ReadAllText(paths.CodexConfigFile));

        manager.SetEnabled(paths, true);
        Assert.Contains("enabled = true", File.ReadAllText(paths.CodexConfigFile), StringComparison.Ordinal);
        manager.Uninstall(paths, installed.BackupPath);
        Assert.Equal(Encoding.UTF8.GetBytes(original), File.ReadAllBytes(paths.CodexConfigFile));
    }

    [Fact]
    public void CodexConfigRepairRestoresTheOllamaModelsEnvironmentWhitelist()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var manager = new CodexConfigManager();
        manager.InstallOrRepair(paths, enabled: true);
        var current = File.ReadAllText(paths.CodexConfigFile);
        var legacy = current.Replace("env_vars = [\"OLLAMA_MODELS\"]", string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(current, legacy);
        File.WriteAllText(paths.CodexConfigFile, legacy, new UTF8Encoding(false));

        Assert.Equal(CodexIntegrationOwnership.ManagedDrift, manager.InspectOwnership(paths));
        var preview = manager.PreviewInstall(paths, enabled: true);
        Assert.True(preview.Changed);
        Assert.Contains("env_vars = [\"OLLAMA_MODELS\"]", preview.Diff, StringComparison.Ordinal);

        var repaired = manager.InstallOrRepair(
            paths,
            enabled: true,
            expectedSourceSha256: preview.SourceSha256,
            expectedPlannedSha256: preview.PlannedSha256);
        Assert.True(repaired.Changed);
        Assert.Equal(CodexIntegrationOwnership.ManagedValid, manager.InspectOwnership(paths));
        Assert.Equal(1, Count(File.ReadAllText(paths.CodexConfigFile), "env_vars = [\"OLLAMA_MODELS\"]"));
        Assert.False(manager.PreviewInstall(paths, enabled: true).Changed);
    }

    [Theory]
    [InlineData("env_vars = [\"OTHER\"]")]
    [InlineData("env_vars = [\"OLLAMA_MODELS\", \"OTHER\"]")]
    [InlineData("env_vars = [\"OLLAMA_MODELS\", \"OLLAMA_MODELS\"]")]
    public void CodexConfigPreservesAndRefusesAnIncorrectOllamaModelsEnvironmentWhitelist(string replacement)
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var manager = new CodexConfigManager();
        manager.InstallOrRepair(paths, enabled: true);
        var current = File.ReadAllText(paths.CodexConfigFile);
        File.WriteAllText(
            paths.CodexConfigFile,
            current.Replace("env_vars = [\"OLLAMA_MODELS\"]", replacement, StringComparison.Ordinal),
            new UTF8Encoding(false));

        var before = File.ReadAllBytes(paths.CodexConfigFile);
        Assert.Equal(CodexIntegrationOwnership.ManagedDrift, manager.InspectOwnership(paths));
        Assert.Throws<InvalidOperationException>(() => manager.PreviewInstall(paths, enabled: true));
        Assert.Equal(before, File.ReadAllBytes(paths.CodexConfigFile));
    }

    [Fact]
    public void ManagedReviewerPreservesButRefusesUnknownEnvironmentEntries()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var manager = new CodexConfigManager();
        manager.InstallOrRepair(paths, enabled: true);
        var withCustomEnvironment = File.ReadAllText(paths.CodexConfigFile).Replace(
            "OLLAMA_HOST = \"http://127.0.0.1:11434\"",
            "OLLAMA_HOST = \"http://127.0.0.1:11434\", CUSTOM_ENV = \"keep\"",
            StringComparison.Ordinal);
        File.WriteAllText(paths.CodexConfigFile, withCustomEnvironment, new UTF8Encoding(false));

        var before = File.ReadAllBytes(paths.CodexConfigFile);
        Assert.Equal(CodexIntegrationOwnership.ManagedDrift, manager.InspectOwnership(paths));
        var exception = Assert.Throws<InvalidOperationException>(() => manager.PreviewInstall(paths, enabled: true));
        Assert.Contains("unapproved environment entry", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(paths.CodexConfigFile));
        Assert.Contains("CUSTOM_ENV = \"keep\"", File.ReadAllText(paths.CodexConfigFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("COR_ENABLE_PROFILING")]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("COR_PROFILER")]
    [InlineData("COR_PROFILER_PATH_64")]
    [InlineData("CORECLR_PROFILER")]
    [InlineData("CORECLR_PROFILER_PATH_32")]
    [InlineData("DOTNET_ENABLE_PROFILING")]
    [InlineData("DOTNET_PROFILER_PATH_ARM64")]
    [InlineData("DOTNET_DiagnosticPorts")]
    [InlineData("DOTNET_GCPath")]
    [InlineData("DOTNET_GCName")]
    [InlineData("DOTNET_JitName")]
    public void ManagedReviewerRefusesRuntimeInjectionEnvironmentEntries(string name)
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var manager = new CodexConfigManager();
        manager.InstallOrRepair(paths, enabled: true);
        var dangerous = File.ReadAllText(paths.CodexConfigFile).Replace(
            "OLLAMA_HOST = \"http://127.0.0.1:11434\"",
            $"OLLAMA_HOST = \"http://127.0.0.1:11434\", {name} = \"injected\"",
            StringComparison.Ordinal);
        File.WriteAllText(paths.CodexConfigFile, dangerous, new UTF8Encoding(false));
        var before = File.ReadAllBytes(paths.CodexConfigFile);

        Assert.Equal(CodexIntegrationOwnership.ManagedDrift, manager.InspectOwnership(paths));
        var exception = Assert.Throws<InvalidOperationException>(() => manager.PreviewInstall(paths, enabled: true));
        Assert.Contains("unapproved environment entry", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(paths.CodexConfigFile));
    }

    [Fact]
    public void ManagedReviewerRefusesRuntimeInjectionEnvironmentImport()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var manager = new CodexConfigManager();
        manager.InstallOrRepair(paths, enabled: true);
        var dangerous = File.ReadAllText(paths.CodexConfigFile).Replace(
            "env_vars = [\"OLLAMA_MODELS\"]",
            "env_vars = [\"OLLAMA_MODELS\", \"DOTNET_STARTUP_HOOKS\"]",
            StringComparison.Ordinal);
        File.WriteAllText(paths.CodexConfigFile, dangerous, new UTF8Encoding(false));

        Assert.Equal(CodexIntegrationOwnership.ManagedDrift, manager.InspectOwnership(paths));
        Assert.Throws<InvalidOperationException>(() => manager.PreviewInstall(paths, enabled: true));
        Assert.Equal(dangerous, File.ReadAllText(paths.CodexConfigFile));
    }

    [Fact]
    public void CodexConfigValidatorFailureRollsBackExactly()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = "model = \"unchanged\"\n";
        File.WriteAllText(paths.CodexConfigFile, original, new UTF8Encoding(false));

        Assert.Throws<InvalidOperationException>(() =>
            new CodexConfigManager().InstallOrRepair(paths, enabled: true, _ => false));
        Assert.Equal(original, File.ReadAllText(paths.CodexConfigFile));
    }

    [Fact]
    public void CodexConfigValidatorFailureRestoresUtf8BomBytesExactly()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("model = \"unchanged\"\r\n"))
            .ToArray();
        File.WriteAllBytes(paths.CodexConfigFile, original);

        Assert.Throws<InvalidOperationException>(() =>
            new CodexConfigManager().InstallOrRepair(paths, enabled: true, _ => false));

        Assert.Equal(original, File.ReadAllBytes(paths.CodexConfigFile));
        var backup = Directory.GetFiles(paths.CodexHome, "config.toml.thalen-helper.*.bak").Single();
        Assert.Equal(original, File.ReadAllBytes(backup));
    }

    [Fact]
    public void CodexConfigConcurrentPostWriteEditIsNeverOverwrittenByRollback()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = Encoding.UTF8.GetBytes("model = \"before\"\r\n");
        var concurrent = Encoding.UTF8.GetBytes("model = \"concurrent-user-edit\"\r\n# keep this\r\n");
        File.WriteAllBytes(paths.CodexConfigFile, original);

        var exception = Assert.Throws<IOException>(() =>
            new CodexConfigManager().InstallOrRepair(paths, enabled: true, _ =>
            {
                File.WriteAllBytes(paths.CodexConfigFile, concurrent);
                return false;
            }));

        Assert.Contains("newer bytes were preserved", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(concurrent, File.ReadAllBytes(paths.CodexConfigFile));
        var backup = Directory.GetFiles(paths.CodexHome, "config.toml.thalen-helper.*.bak").Single();
        Assert.Equal(original, File.ReadAllBytes(backup));
    }

    [Fact]
    public void CodexConfigRefusesMalformedAndUnmanagedCollisions()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        File.WriteAllText(paths.CodexConfigFile, "[[broken");
        Assert.Throws<InvalidDataException>(() => new CodexConfigManager().InstallOrRepair(paths, false));

        var existing = "[mcp_servers.local_gpu_reviewer]\ncommand = \"other.exe\"\n";
        File.WriteAllText(paths.CodexConfigFile, existing);
        var preserved = new CodexConfigManager().InstallOrRepair(paths, false);
        Assert.False(preserved.Changed);
        Assert.Null(preserved.BackupPath);
        Assert.Equal("preserved-existing-unmanaged", preserved.Operation);
        Assert.Equal(existing, File.ReadAllText(paths.CodexConfigFile));
        Assert.DoesNotContain(ProductInfo.ManagedConfigStart, File.ReadAllText(paths.CodexConfigFile), StringComparison.Ordinal);
        Assert.Equal(1, Count(existing, "[mcp_servers.local_gpu_reviewer]"));
    }

    [Theory]
    [InlineData("[mcp_servers.\"local_gpu_reviewer\"] # existing integration\ncommand = \"other.exe\"\n")]
    [InlineData("mcp_servers.local_gpu_reviewer = { command = \"other.exe\" }\n")]
    public void CodexConfigPreservesSemanticReviewerTableForms(string existing)
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        File.WriteAllText(paths.CodexConfigFile, existing);

        var preserved = new CodexConfigManager().InstallOrRepair(paths, false);

        Assert.False(preserved.Changed);
        Assert.Equal("preserved-existing-unmanaged", preserved.Operation);
        Assert.Equal(existing, File.ReadAllText(paths.CodexConfigFile));
        Assert.DoesNotContain(ProductInfo.ManagedConfigStart, existing, StringComparison.Ordinal);
    }

    [Fact]
    public void CodexConfigUninstallDoesNotRestoreStaleBackupOverNewUserContent()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = "model = \"original\"\r\n";
        File.WriteAllText(paths.CodexConfigFile, original, new UTF8Encoding(false));
        var manager = new CodexConfigManager();
        var installed = manager.InstallOrRepair(paths, false);
        File.AppendAllText(paths.CodexConfigFile, "# added after install\r\n");

        var removed = manager.Uninstall(paths, installed.BackupPath);
        var content = File.ReadAllText(paths.CodexConfigFile);

        Assert.Equal("removed", removed.Operation);
        Assert.Contains("# added after install", content, StringComparison.Ordinal);
        Assert.DoesNotContain(ProductInfo.ManagedConfigStart, content, StringComparison.Ordinal);
        Assert.NotEqual(original, content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodexConfigUninstallPreservesByteOnlyUserChangesInsteadOfRestoringStaleBackup(bool addBom)
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = "model = \"original\"\r\n";
        File.WriteAllText(paths.CodexConfigFile, original, new UTF8Encoding(false));
        var manager = new CodexConfigManager();
        var installed = manager.InstallOrRepair(paths, false);
        var installedBytes = File.ReadAllBytes(paths.CodexConfigFile);
        var changed = addBom
            ? Encoding.UTF8.GetPreamble().Concat(installedBytes).ToArray()
            : Encoding.UTF8.GetBytes(
                File.ReadAllText(paths.CodexConfigFile).Replace(
                    ProductInfo.ManagedConfigStart,
                    "  " + Environment.NewLine + ProductInfo.ManagedConfigStart,
                    StringComparison.Ordinal));
        File.WriteAllBytes(paths.CodexConfigFile, changed);

        var removed = manager.Uninstall(paths, installed.BackupPath);
        var result = File.ReadAllBytes(paths.CodexConfigFile);

        Assert.Equal("removed", removed.Operation);
        Assert.NotEqual(Encoding.UTF8.GetBytes(original), result);
        Assert.Equal(addBom, result.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.DoesNotContain(ProductInfo.ManagedConfigStart, File.ReadAllText(paths.CodexConfigFile), StringComparison.Ordinal);
        if (!addBom)
        {
            Assert.Contains("  ", File.ReadAllText(paths.CodexConfigFile), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AgentsOverridePreservesUserTextAndRemovesOnlyManagedSection()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = "# Personal instructions\n\nKeep this exact sentence.\n";
        File.WriteAllText(paths.AgentsOverrideFile, original, new UTF8Encoding(false));
        var manager = new AgentsOverrideManager();

        var installed = manager.InstallOrRepair(paths, HardwareTier.Entry);
        var content = File.ReadAllText(paths.AgentsOverrideFile);
        Assert.True(installed.Changed);
        Assert.NotNull(installed.BackupPath);
        Assert.Contains(original.TrimEnd(), content, StringComparison.Ordinal);
        Assert.Contains("Entry-tier models", content, StringComparison.Ordinal);
        Assert.DoesNotContain(ProductInfo.ManagedReliabilityStart, content, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, content, StringComparison.OrdinalIgnoreCase);
        Assert.False(manager.InstallOrRepair(paths, HardwareTier.Entry).Changed);

        manager.Uninstall(paths, fileWasCreatedByProduct: false, installed.BackupPath);
        Assert.Equal(Encoding.UTF8.GetBytes(original), File.ReadAllBytes(paths.AgentsOverrideFile));
    }

    [Fact]
    public void ReliabilityBaselineIsOptInPreviewedBackedUpIdempotentAndSurgicallyReversible()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = "# User instructions\n\nPreserve this sentence.\n";
        File.WriteAllText(paths.AgentsOverrideFile, original, new UTF8Encoding(false));
        var manager = new AgentsOverrideManager();

        var preview = manager.PreviewInstall(paths, HardwareTier.Mid, installReliabilityBaseline: true);
        Assert.True(preview.Changed);
        Assert.True(preview.ReliabilityBaselineSelected);
        Assert.Contains("--- AGENTS.override.md (before)", preview.Diff, StringComparison.Ordinal);
        Assert.Contains("+++ AGENTS.override.md (after)", preview.Diff, StringComparison.Ordinal);
        Assert.Contains("Goal, context, constraints, and done", preview.Diff, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(paths.AgentsOverrideFile));

        var installed = manager.InstallOrRepair(paths, HardwareTier.Mid, installReliabilityBaseline: true);
        var content = File.ReadAllText(paths.AgentsOverrideFile);
        Assert.True(installed.Changed);
        Assert.NotNull(installed.BackupPath);
        Assert.Equal(original, File.ReadAllText(installed.BackupPath!));
        Assert.Equal(1, Count(content, ProductInfo.ManagedAgentsStart));
        Assert.Equal(1, Count(content, ProductInfo.ManagedReliabilityStart));
        Assert.False(manager.InstallOrRepair(paths, HardwareTier.Mid, installReliabilityBaseline: true).Changed);

        var removedOptional = manager.InstallOrRepair(paths, HardwareTier.Mid, installReliabilityBaseline: false);
        Assert.True(removedOptional.Changed);
        content = File.ReadAllText(paths.AgentsOverrideFile);
        Assert.Contains(ProductInfo.ManagedAgentsStart, content, StringComparison.Ordinal);
        Assert.DoesNotContain(ProductInfo.ManagedReliabilityStart, content, StringComparison.Ordinal);
        manager.Uninstall(paths, fileWasCreatedByProduct: false, installed.BackupPath);
        Assert.Equal(Encoding.UTF8.GetBytes(original), File.ReadAllBytes(paths.AgentsOverrideFile));
    }

    [Fact]
    public void ExistingUnmarkedLocalGpuGuidanceIsPreservedWithoutDuplicateSection()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = "# Existing reviewer instructions\nUse local_gpu_reviewer only when appropriate.\n";
        File.WriteAllText(paths.AgentsOverrideFile, original, new UTF8Encoding(false));

        var result = new AgentsOverrideManager().InstallOrRepair(paths, HardwareTier.High);

        Assert.False(result.Changed);
        Assert.Null(result.BackupPath);
        Assert.Equal("preserved-existing-unmanaged", result.Operation);
        Assert.Equal(original, File.ReadAllText(paths.AgentsOverrideFile));
        Assert.DoesNotContain(ProductInfo.ManagedAgentsStart, original, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentsUninstallDoesNotRestoreStaleBackupOverNewUserContent()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = "# Original instruction\r\n";
        File.WriteAllText(paths.AgentsOverrideFile, original, new UTF8Encoding(false));
        var manager = new AgentsOverrideManager();
        var installed = manager.InstallOrRepair(paths, HardwareTier.Entry);
        File.AppendAllText(paths.AgentsOverrideFile, "# Added after install\r\n");

        var removed = manager.Uninstall(paths, fileWasCreatedByProduct: false, installed.BackupPath);
        var content = File.ReadAllText(paths.AgentsOverrideFile);

        Assert.Equal("removed-managed-sections", removed.Operation);
        Assert.Contains("# Added after install", content, StringComparison.Ordinal);
        Assert.DoesNotContain(ProductInfo.ManagedAgentsStart, content, StringComparison.Ordinal);
        Assert.NotEqual(original, content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AgentsUninstallPreservesByteOnlyUserChangesInsteadOfRestoringStaleBackup(bool addBom)
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = "# Original instruction\r\n";
        File.WriteAllText(paths.AgentsOverrideFile, original, new UTF8Encoding(false));
        var manager = new AgentsOverrideManager();
        var installed = manager.InstallOrRepair(paths, HardwareTier.Entry);
        var installedBytes = File.ReadAllBytes(paths.AgentsOverrideFile);
        var changed = addBom
            ? Encoding.UTF8.GetPreamble().Concat(installedBytes).ToArray()
            : Encoding.UTF8.GetBytes(
                File.ReadAllText(paths.AgentsOverrideFile).Replace(
                    ProductInfo.ManagedAgentsStart,
                    "  " + Environment.NewLine + ProductInfo.ManagedAgentsStart,
                    StringComparison.Ordinal));
        File.WriteAllBytes(paths.AgentsOverrideFile, changed);

        var removed = manager.Uninstall(paths, fileWasCreatedByProduct: false, installed.BackupPath);
        var result = File.ReadAllBytes(paths.AgentsOverrideFile);

        Assert.Equal("removed-managed-sections", removed.Operation);
        Assert.NotEqual(Encoding.UTF8.GetBytes(original), result);
        Assert.Equal(addBom, result.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.DoesNotContain(ProductInfo.ManagedAgentsStart, File.ReadAllText(paths.AgentsOverrideFile), StringComparison.Ordinal);
        if (!addBom)
        {
            Assert.Contains("  ", File.ReadAllText(paths.AgentsOverrideFile), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ManagedLocalGpuSectionSurvivesSeparateUserMentionDuringRepair()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var manager = new AgentsOverrideManager();
        manager.InstallOrRepair(paths, HardwareTier.Entry);
        File.AppendAllText(paths.AgentsOverrideFile, "\n# User note\nlocal_gpu_reviewer is approved here.\n");

        var result = manager.InstallOrRepair(paths, HardwareTier.Mid);
        var content = File.ReadAllText(paths.AgentsOverrideFile);

        Assert.True(result.Changed);
        Assert.Contains("local_gpu_reviewer is approved here.", content, StringComparison.Ordinal);
        Assert.Contains("Mid-tier models", content, StringComparison.Ordinal);
        Assert.Equal(1, Count(content, ProductInfo.ManagedAgentsStart));
    }

    [Fact]
    public void PreviewHashRejectsSourceOrPlanDriftBeforeWrite()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        File.WriteAllText(paths.AgentsOverrideFile, "# Original\n");
        var manager = new AgentsOverrideManager();
        var preview = manager.PreviewInstall(paths, HardwareTier.Mid, installReliabilityBaseline: true);
        File.AppendAllText(paths.AgentsOverrideFile, "# Changed after preview\n");
        var changed = File.ReadAllBytes(paths.AgentsOverrideFile);

        Assert.Throws<InvalidOperationException>(() => manager.InstallOrRepair(
            paths,
            HardwareTier.Mid,
            installReliabilityBaseline: true,
            expectedSourceSha256: preview.SourceSha256,
            expectedPlannedSha256: preview.PlannedSha256));
        Assert.Equal(changed, File.ReadAllBytes(paths.AgentsOverrideFile));

        preview = manager.PreviewInstall(paths, HardwareTier.Mid, installReliabilityBaseline: true);
        Assert.Throws<InvalidOperationException>(() => manager.InstallOrRepair(
            paths,
            HardwareTier.Entry,
            installReliabilityBaseline: true,
            expectedSourceSha256: preview.SourceSha256,
            expectedPlannedSha256: preview.PlannedSha256));
        Assert.Equal(changed, File.ReadAllBytes(paths.AgentsOverrideFile));
    }

    [Fact]
    public void AgentsPostWriteFailureRestoresUtf8BomBytesExactly()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("# Preserve encoding\r\n"))
            .ToArray();
        File.WriteAllBytes(paths.AgentsOverrideFile, original);

        Assert.Throws<InvalidOperationException>(() => new AgentsOverrideManager().InstallOrRepair(
            paths,
            HardwareTier.Entry,
            postWriteValidator: _ => false));

        Assert.Equal(original, File.ReadAllBytes(paths.AgentsOverrideFile));
        var backup = Directory.GetFiles(paths.CodexHome, "AGENTS.override.md.thalen-helper.*.bak").Single();
        Assert.Equal(original, File.ReadAllBytes(backup));
    }

    [Fact]
    public void AgentsConcurrentPostWriteEditIsNeverOverwrittenByRollback()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var original = Encoding.UTF8.GetBytes("# Before\r\n");
        var concurrent = Encoding.UTF8.GetBytes("# Concurrent user instructions\r\nKeep this exact text.\r\n");
        File.WriteAllBytes(paths.AgentsOverrideFile, original);

        var exception = Assert.Throws<IOException>(() => new AgentsOverrideManager().InstallOrRepair(
            paths,
            HardwareTier.Entry,
            postWriteValidator: path =>
            {
                File.WriteAllBytes(path, concurrent);
                return false;
            }));

        Assert.Contains("newer bytes were preserved", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(concurrent, File.ReadAllBytes(paths.AgentsOverrideFile));
        var backup = Directory.GetFiles(paths.CodexHome, "AGENTS.override.md.thalen-helper.*.bak").Single();
        Assert.Equal(original, File.ReadAllBytes(backup));
    }

    [Fact]
    public void FreshAgentsTemplateCanBeRemovedAsProductOwnedFile()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var manager = new AgentsOverrideManager();

        manager.InstallOrRepair(paths, HardwareTier.High);
        Assert.True(File.Exists(paths.AgentsOverrideFile));
        manager.Uninstall(paths, fileWasCreatedByProduct: true);
        Assert.False(File.Exists(paths.AgentsOverrideFile));
    }

    [Fact]
    public void ProductCreatedAgentsFilePreservesUserTextAddedAfterInstallation()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var manager = new AgentsOverrideManager();

        manager.InstallOrRepair(paths, HardwareTier.High);
        File.AppendAllText(paths.AgentsOverrideFile, "# User-added instruction" + Environment.NewLine);
        var result = manager.Uninstall(paths, fileWasCreatedByProduct: true);

        Assert.True(File.Exists(paths.AgentsOverrideFile));
        Assert.Equal("# User-added instruction" + Environment.NewLine, File.ReadAllText(paths.AgentsOverrideFile));
        Assert.Equal("preserved-user-content", result.Operation);
    }

    private static int Count(string content, string value)
        => (content.Length - content.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
}
