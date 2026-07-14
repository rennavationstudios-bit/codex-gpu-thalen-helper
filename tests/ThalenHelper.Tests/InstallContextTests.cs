using System.Text.Json;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class InstallContextTests
{
    [Fact]
    public void ContextRoundTripsAtomicallyAndDrivesUninstallPathResolution()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();

        InstallContextStore.Save(paths);
        var context = InstallContextStore.Load(paths.InstallDirectory);
        var resolved = CliApplication.ResolvePaths(
            new ParsedArguments([
                "uninstall",
                "--install-dir", paths.InstallDirectory
            ]),
            "uninstall");

        Assert.NotNull(context);
        Assert.Equal(paths.InstallDirectory, context.InstallDirectory);
        Assert.Equal(paths.StateDirectory, context.StateDirectory);
        Assert.Equal(paths.CodexHome, context.CodexHome);
        Assert.Equal(paths.StateDirectory, resolved.StateDirectory);
        Assert.Equal(paths.CodexHome, resolved.CodexHome);
        Assert.False(File.Exists(InstallContextStore.GetPath(paths.InstallDirectory) + ".tmp"));
    }

    [Fact]
    public void ExplicitUninstallPathsOverrideStoredContext()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        InstallContextStore.Save(paths);
        var explicitState = Path.Combine(temporary.Path, "explicit state");
        var explicitCodex = Path.Combine(temporary.Path, "explicit codex");

        var resolved = CliApplication.ResolvePaths(
            new ParsedArguments([
                "uninstall",
                "--install-dir", paths.InstallDirectory,
                "--state-dir", explicitState,
                "--codex-home", explicitCodex
            ]),
            "uninstall");

        Assert.Equal(Path.GetFullPath(explicitState), resolved.StateDirectory);
        Assert.Equal(Path.GetFullPath(explicitCodex), resolved.CodexHome);
    }

    [Fact]
    public void RepairUsesStoredContextAndExplicitPathsStillTakePrecedence()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        InstallContextStore.Save(paths);

        var stored = CliApplication.ResolvePaths(
            new ParsedArguments([
                "repair",
                "--install-dir", paths.InstallDirectory
            ]),
            "repair");

        Assert.Equal(paths.StateDirectory, stored.StateDirectory);
        Assert.Equal(paths.CodexHome, stored.CodexHome);

        var explicitState = Path.Combine(temporary.Path, "explicit repair state");
        var explicitCodex = Path.Combine(temporary.Path, "explicit repair codex");
        var overridden = CliApplication.ResolvePaths(
            new ParsedArguments([
                "repair",
                "--install-dir", paths.InstallDirectory,
                "--state-dir", explicitState,
                "--codex-home", explicitCodex
            ]),
            "repair");

        Assert.Equal(Path.GetFullPath(explicitState), overridden.StateDirectory);
        Assert.Equal(Path.GetFullPath(explicitCodex), overridden.CodexHome);
    }

    [Fact]
    public async Task RepairReusesCustomContextIdempotently()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        Directory.CreateDirectory(paths.CodexHome);
        await File.WriteAllTextAsync(paths.CodexConfigFile, "model = \"preserve\"\n");
        await File.WriteAllTextAsync(paths.AgentsOverrideFile, "# Preserve this instruction\n");

        var installArguments = new[]
        {
            "install",
            "--yes",
            "--defer-model",
            "--auto-start", "false",
            "--install-dir", paths.InstallDirectory,
            "--state-dir", paths.StateDirectory,
            "--codex-home", paths.CodexHome
        };
        Assert.Equal(0, await CliApplication.RunAsync(installArguments));

        var repairArguments = new[]
        {
            "repair",
            "--install-dir", paths.InstallDirectory
        };
        Assert.Equal(0, await CliApplication.RunAsync(repairArguments));
        Assert.Equal(0, await CliApplication.RunAsync(repairArguments));

        var context = InstallContextStore.Load(paths.InstallDirectory);
        Assert.NotNull(context);
        Assert.Equal(paths.StateDirectory, context.StateDirectory);
        Assert.Equal(paths.CodexHome, context.CodexHome);

        var config = await File.ReadAllTextAsync(paths.CodexConfigFile);
        var agents = await File.ReadAllTextAsync(paths.AgentsOverrideFile);
        Assert.StartsWith("model = \"preserve\"", config, StringComparison.Ordinal);
        Assert.StartsWith("# Preserve this instruction", agents, StringComparison.Ordinal);
        Assert.Equal(1, Count(config, ProductInfo.ManagedConfigStart));
        Assert.Equal(1, Count(agents, ProductInfo.ManagedAgentsStart));
    }

    [Fact]
    public void UninstallWithoutExplicitInstallDirectoryProbesTheExecutableDirectory()
    {
        var resolved = CliApplication.ResolvePaths(
            new ParsedArguments(["uninstall"]),
            "uninstall");

        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory), resolved.InstallDirectory);
    }

    [Fact]
    public void MalformedOrForeignContextFailsClosed()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        Directory.CreateDirectory(paths.InstallDirectory);
        var contextPath = InstallContextStore.GetPath(paths.InstallDirectory);
        File.WriteAllText(contextPath, "not-json");
        Assert.Throws<InvalidDataException>(() => InstallContextStore.Load(paths.InstallDirectory));

        File.WriteAllText(contextPath, JsonSerializer.Serialize(new InstallContext(
            1,
            Path.Combine(temporary.Path, "different install"),
            paths.StateDirectory,
            paths.CodexHome)));
        Assert.Throws<InvalidDataException>(() => InstallContextStore.Load(paths.InstallDirectory));
    }

    [Fact]
    public async Task UninstallWithoutProductStateLeavesProtectedFilesByteForByteUnchanged()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var configBytes = System.Text.Encoding.UTF8.GetBytes(
            "# existing config\r\n" + ProductInfo.ManagedConfigStart + "\r\n[user]\r\nvalue = true\r\n");
        var agentsBytes = System.Text.Encoding.UTF8.GetBytes(
            "# existing instructions\r\n" + ProductInfo.ManagedAgentsStart + "\r\ncustom text\r\n");
        Directory.CreateDirectory(paths.CodexHome);
        await File.WriteAllBytesAsync(paths.CodexConfigFile, configBytes);
        await File.WriteAllBytesAsync(paths.AgentsOverrideFile, agentsBytes);
        InstallContextStore.Save(paths);

        var result = await new UninstallManager(paths, new StateStore(paths.StateFile))
            .UninstallAsync(removeOwnedModel: false);

        Assert.True(result.Success);
        Assert.Equal("UNINSTALLED", result.Code);
        Assert.Empty(result.ManagedFiles);
        Assert.Equal(configBytes, await File.ReadAllBytesAsync(paths.CodexConfigFile));
        Assert.Equal(agentsBytes, await File.ReadAllBytesAsync(paths.AgentsOverrideFile));
        Assert.False(File.Exists(InstallContextStore.GetPath(paths.InstallDirectory)));
        Assert.Contains("left untouched", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static int Count(string value, string marker)
        => value.Split(marker, StringSplitOptions.None).Length - 1;
}
