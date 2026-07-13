namespace ThalenHelper.Core;

public sealed record ProductPaths(
    string InstallDirectory,
    string StateDirectory,
    string StateFile,
    string CodexHome,
    string CodexConfigFile,
    string AgentsOverrideFile)
{
    public static ProductPaths Resolve(
        string? installDirectory = null,
        string? stateDirectory = null,
        string? codexHome = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var resolvedInstall = Path.GetFullPath(installDirectory
            ?? Path.Combine(localAppData, "Programs", "Codex GPU Thalen Helper"));
        var resolvedState = Path.GetFullPath(stateDirectory
            ?? Environment.GetEnvironmentVariable("THALEN_HELPER_STATE_DIR")
            ?? Path.Combine(localAppData, "CodexGPUThalenHelper"));
        var resolvedCodexHome = Path.GetFullPath(codexHome
            ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(userProfile, ".codex"));

        return new ProductPaths(
            resolvedInstall,
            resolvedState,
            Path.Combine(resolvedState, "state.json"),
            resolvedCodexHome,
            Path.Combine(resolvedCodexHome, "config.toml"),
            Path.Combine(resolvedCodexHome, "AGENTS.override.md"));
    }

    public string McpExecutable => Path.Combine(InstallDirectory, "local-gpu-reviewer.exe");
    public string CliExecutable => Path.Combine(InstallDirectory, "thalen-helper.exe");
    public string ControlCenterExecutable => Path.Combine(InstallDirectory, "ThalenHelper.ControlCenter.exe");
}
