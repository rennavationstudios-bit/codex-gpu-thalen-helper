using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class CliPolicyTests
{
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
