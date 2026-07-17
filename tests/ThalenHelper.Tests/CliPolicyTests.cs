using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class CliPolicyTests
{
    [Fact]
    public void InstallationDefaultsNeverPullOrLoadAModel()
    {
        using var temporary = new TemporaryDirectory();
        var options = new InstallationOptions(temporary.CreatePaths());

        Assert.False(options.PullAndValidateModel);
        Assert.False(options.InstallReliabilityBaseline);
    }

    [Fact]
    public async Task ReliabilityBaselineCannotBypassInteractiveDiffPreview()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();

        var exitCode = await CliApplication.RunAsync(
        [
            "install",
            "--yes",
            "--reliability-baseline",
            "--install-dir", paths.InstallDirectory,
            "--state-dir", paths.StateDirectory,
            "--codex-home", paths.CodexHome
        ]);

        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(paths.CodexConfigFile));
        Assert.False(File.Exists(paths.AgentsOverrideFile));
        Assert.False(File.Exists(paths.StateFile));
    }

    [Fact]
    public async Task DeferredInstallerBootstrapMergesOnceWithoutModelOrInference()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        await File.WriteAllTextAsync(paths.CodexConfigFile, "model = \"preserve\"\n");
        await File.WriteAllTextAsync(paths.AgentsOverrideFile, "# Preserve this instruction\n");

        var arguments = new[]
        {
            "install",
            "--yes",
            "--defer-model",
            "--auto-start", "false",
            "--install-dir", paths.InstallDirectory,
            "--state-dir", paths.StateDirectory,
            "--codex-home", paths.CodexHome
        };
        Assert.Equal(0, await CliApplication.RunAsync(arguments));
        Assert.Equal(0, await CliApplication.RunAsync(arguments));

        var state = await new StateStore(paths.StateFile).LoadAsync();
        Assert.NotNull(state);
        Assert.Null(state.SelectedModel);
        Assert.Null(state.ModelStorageLocation);
        Assert.Equal(HelperAvailability.Disabled, state.Availability);
        Assert.False(state.Preferences.AutoStartOllama);
        Assert.False(state.StartupEntryOwnedByHelper);
        Assert.False(state.SelectedModelOwnedByHelper);
        Assert.Contains(paths.CodexConfigFile, state.BackupLocations.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(paths.AgentsOverrideFile, state.BackupLocations.Keys, StringComparer.OrdinalIgnoreCase);

        var config = await File.ReadAllTextAsync(paths.CodexConfigFile);
        var agents = await File.ReadAllTextAsync(paths.AgentsOverrideFile);
        Assert.StartsWith("model = \"preserve\"", config, StringComparison.Ordinal);
        Assert.StartsWith("# Preserve this instruction", agents, StringComparison.Ordinal);
        Assert.Equal(1, Count(config, ProductInfo.ManagedConfigStart));
        Assert.Equal(1, Count(agents, ProductInfo.ManagedAgentsStart));
        Assert.DoesNotContain(ProductInfo.ManagedReliabilityStart, agents, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupVerificationExitPolicyRejectsReachableUnsafeStates()
    {
        var state = new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851"
        };
        var safe = new OllamaStartupVerification(
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            false,
            "OK",
            "Verified.");

        Assert.Equal(0, CliApplication.StartupVerificationExitCode(safe, state));
        Assert.Equal(
            1,
            CliApplication.StartupVerificationExitCode(
                safe with { ModelStorageConfigured = false, Code = "MODEL_PATH_NOT_CONFIGURED" },
                state));
        Assert.Equal(
            1,
            CliApplication.StartupVerificationExitCode(
                safe with { SelectedModelDigestMatches = false, Code = "MODEL_DIGEST_MISMATCH" },
                state));
        Assert.Equal(
            1,
            CliApplication.StartupVerificationExitCode(
                safe with { LoopbackOnly = false, Code = "OLLAMA_NETWORK_EXPOSURE" },
                state));
    }

    [Fact]
    public async Task ModelStorageActivationAndRecoveryRequireExplicitConfirmationWithoutTouchingState()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var common = new[]
        {
            "--install-dir", paths.InstallDirectory,
            "--state-dir", paths.StateDirectory,
            "--codex-home", paths.CodexHome
        };

        var activate = await CliApplication.RunAsync(
            ["models", "activate", Path.Combine(temporary.Path, "destination"), .. common]);
        var recover = await CliApplication.RunAsync(["models", "recover", .. common]);

        Assert.Equal(2, activate);
        Assert.Equal(2, recover);
        Assert.False(File.Exists(paths.StateFile));
    }

    [Fact]
    public void ModelStorageActivationResultUsesNormalResultExitPolicy()
    {
        var success = new ModelsActivationResult(
            true,
            "MODELS_ACTIVATED_SOURCE_PRESERVED",
            "ok",
            "source",
            "destination",
            1,
            1,
            true,
            false,
            "OK");

        Assert.Equal(0, CliApplication.ResultExitCode(success));
        Assert.Equal(1, CliApplication.ResultExitCode(success with { Success = false, Code = "FAILED" }));
    }

    private static int Count(string value, string marker)
        => value.Split(marker, StringSplitOptions.None).Length - 1;
}
