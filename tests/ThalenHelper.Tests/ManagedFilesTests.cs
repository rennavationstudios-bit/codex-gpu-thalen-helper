using System.Text;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class ManagedFilesTests
{
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
        Assert.Contains("approval_mode = \"auto\"", content, StringComparison.Ordinal);
        Assert.Contains("approval_mode = \"prompt\"", content, StringComparison.Ordinal);

        var repaired = manager.InstallOrRepair(paths, enabled: false, _ => true);
        Assert.False(repaired.Changed);
        Assert.Equal(content, File.ReadAllText(paths.CodexConfigFile));

        manager.SetEnabled(paths, true);
        Assert.Contains("enabled = true", File.ReadAllText(paths.CodexConfigFile), StringComparison.Ordinal);
        manager.Uninstall(paths);
        Assert.Equal(original.TrimEnd() + Environment.NewLine, File.ReadAllText(paths.CodexConfigFile));
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
    public void CodexConfigRefusesMalformedAndUnmanagedCollisions()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        File.WriteAllText(paths.CodexConfigFile, "[[broken");
        Assert.Throws<InvalidDataException>(() => new CodexConfigManager().InstallOrRepair(paths, false));

        File.WriteAllText(paths.CodexConfigFile, "[mcp_servers.local_gpu_reviewer]\ncommand = \"other.exe\"\n");
        Assert.Throws<InvalidOperationException>(() => new CodexConfigManager().InstallOrRepair(paths, false));
        Assert.DoesNotContain(ProductInfo.ManagedConfigStart, File.ReadAllText(paths.CodexConfigFile), StringComparison.Ordinal);
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
        Assert.DoesNotContain(Environment.UserName, content, StringComparison.OrdinalIgnoreCase);
        Assert.False(manager.InstallOrRepair(paths, HardwareTier.Entry).Changed);

        manager.Uninstall(paths, fileWasCreatedByProduct: false);
        Assert.Equal(original.TrimEnd() + Environment.NewLine, File.ReadAllText(paths.AgentsOverrideFile));
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
}
