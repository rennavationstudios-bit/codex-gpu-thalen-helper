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
}
