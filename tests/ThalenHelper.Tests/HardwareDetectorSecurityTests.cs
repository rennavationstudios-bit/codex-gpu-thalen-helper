using System.Diagnostics;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class HardwareDetectorSecurityTests
{
    [Fact]
    public void ResolverUsesSystemDirectoryCandidateFirst()
    {
        using var temporary = new TemporaryDirectory();
        var system = Path.Combine(temporary.Path, "Windows", "System32");
        var programFiles = Path.Combine(temporary.Path, "Program Files");
        var systemTool = Path.Combine(system, "nvidia-smi.exe");
        var legacyTool = Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(systemTool)!);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyTool)!);
        File.WriteAllText(systemTool, "inert test marker");
        File.WriteAllText(legacyTool, "inert test marker");

        var resolved = HardwareDetector.ResolveNvidiaSmiPath(system, programFiles);

        Assert.Equal(Path.GetFullPath(systemTool), resolved);
    }

    [Fact]
    public void ResolverFallsBackToLegacyProgramFilesCandidate()
    {
        using var temporary = new TemporaryDirectory();
        var system = Path.Combine(temporary.Path, "Windows", "System32");
        var programFiles = Path.Combine(temporary.Path, "Program Files");
        var legacyTool = Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyTool)!);
        File.WriteAllText(legacyTool, "inert test marker");

        var resolved = HardwareDetector.ResolveNvidiaSmiPath(system, programFiles);

        Assert.Equal(Path.GetFullPath(legacyTool), resolved);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("relative-system", "relative-program-files")]
    public void ResolverRejectsMissingOrRelativeTrustedRoots(string? system, string? programFiles)
    {
        Assert.Null(HardwareDetector.ResolveNvidiaSmiPath(system, programFiles));
    }

    [Fact]
    public void ResolverNeverSearchesCurrentDirectoryOrPath()
    {
        using var temporary = new TemporaryDirectory();
        var untrustedTool = Path.Combine(temporary.Path, "nvidia-smi.exe");
        File.WriteAllText(untrustedTool, "inert test marker");

        var resolved = HardwareDetector.ResolveNvidiaSmiPath(
            Path.Combine(temporary.Path, "missing-system"),
            Path.Combine(temporary.Path, "missing-program-files"));

        Assert.Null(resolved);
    }

    [Fact]
    public void StartInfoRequiresAndPreservesAnAbsoluteExecutablePath()
    {
        var executable = Path.Combine(Path.GetTempPath(), "trusted", "nvidia-smi.exe");

        var startInfo = HardwareDetector.CreateNvidiaSmiStartInfo(executable);

        Assert.Equal(Path.GetFullPath(executable), startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Contains("--query-gpu=", startInfo.Arguments, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => HardwareDetector.CreateNvidiaSmiStartInfo("nvidia-smi.exe"));
    }
}
