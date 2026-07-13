using System.Text.Json;

namespace ThalenHelper.Core;

public sealed record UninstallResult(
    bool Success,
    string Code,
    string Message,
    bool ModelRemoved,
    bool OllamaPreserved,
    IReadOnlyList<ManagedFileResult> ManagedFiles,
    string ReportPath);

public sealed class UninstallManager
{
    private readonly ProductPaths _paths;
    private readonly StateStore _store;
    private readonly CodexConfigManager _codexConfig;
    private readonly AgentsOverrideManager _agentsOverride;
    private readonly OllamaAutoStartManager _autoStart;
    private readonly Func<OllamaClient> _clientFactory;

    public UninstallManager(
        ProductPaths paths,
        StateStore store,
        CodexConfigManager? codexConfig = null,
        AgentsOverrideManager? agentsOverride = null,
        OllamaAutoStartManager? autoStart = null,
        Func<OllamaClient>? clientFactory = null)
    {
        _paths = paths;
        _store = store;
        _codexConfig = codexConfig ?? new CodexConfigManager();
        _agentsOverride = agentsOverride ?? new AgentsOverrideManager();
        _clientFactory = clientFactory ?? (() => new OllamaClient());
        _autoStart = autoStart ?? new OllamaAutoStartManager(_clientFactory);
    }

    public async Task<UninstallResult> UninstallAsync(
        bool removeOwnedModel,
        CancellationToken cancellationToken = default)
    {
        var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var managedPaths = state?.ManagedCodexHome is null
            ? _paths
            : ProductPaths.Resolve(_paths.InstallDirectory, _paths.StateDirectory, state.ManagedCodexHome);
        var managed = new List<ManagedFileResult>();
        var modelRemoved = false;
        if (state is not null)
        {
            state.Availability = HelperAvailability.Disabled;
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            GpuCoordination.RequestCancellation();
            if (!string.IsNullOrWhiteSpace(state.SelectedModel))
            {
                try
                {
                    using var client = _clientFactory();
                    await client.UnloadAsync(state.SelectedModel, cancellationToken).ConfigureAwait(false);
                    if (removeOwnedModel && state.SelectedModelOwnedByHelper)
                    {
                        await client.DeleteAsync(state.SelectedModel, cancellationToken).ConfigureAwait(false);
                        modelRemoved = true;
                    }
                }
                catch (OllamaException)
                {
                    // Optional Ollama failure must not block surgical Codex cleanup.
                }
            }

            _autoStart.RemoveOwnedStartupEntry();
            _autoStart.RestoreOwnedEnvironment(state);
        }

        StopOwnedMcpProcesses(managedPaths.McpExecutable);
        managed.Add(UninstallCodexConfigWithRecovery(managedPaths));
        var agentsWasCreated = state?.FilesCreated.Contains(managedPaths.AgentsOverrideFile, StringComparer.OrdinalIgnoreCase) == true;
        managed.Add(UninstallAgentsWithRecovery(managedPaths, agentsWasCreated));
        var manualCleanupRequired = managed.Any(item =>
            string.Equals(item.Operation, "manual-cleanup-required", StringComparison.Ordinal));
        var reportPath = Path.Combine(
            Path.GetTempPath(),
            $"CodexGpuThalenHelper-uninstall-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
        var report = new
        {
            product = ProductInfo.Name,
            version = state?.ProductVersion ?? ProductInfo.Version,
            uninstalledAt = DateTimeOffset.UtcNow,
            modelRemoved,
            ollamaPreserved = true,
            managedFiles = managed.Select(item => new { item.Operation, file = Path.GetFileName(item.Path), item.Changed }),
            note = "Codex authentication, unrelated Codex configuration, pre-existing Ollama, and pre-existing models were preserved."
        };
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
        if (!manualCleanupRequired && File.Exists(_paths.StateFile))
        {
            File.Delete(_paths.StateFile);
        }

        if (!manualCleanupRequired
            && Directory.Exists(_paths.StateDirectory)
            && !Directory.EnumerateFileSystemEntries(_paths.StateDirectory).Any())
        {
            Directory.Delete(_paths.StateDirectory, false);
        }

        if (!manualCleanupRequired)
        {
            GpuCoordination.ClearCancellation();
        }

        return new UninstallResult(
            !manualCleanupRequired,
            manualCleanupRequired ? "UNINSTALL_MANUAL_CLEANUP_REQUIRED" : "UNINSTALLED",
            manualCleanupRequired
                ? "A managed Codex file is malformed or unsafe to edit. Its current bytes and helper state were preserved for manual cleanup and retry."
                : "Managed Codex integration, instructions, startup entry, and state were removed surgically.",
            modelRemoved,
            true,
            managed,
            reportPath);
    }

    private ManagedFileResult UninstallCodexConfigWithRecovery(ProductPaths paths)
    {
        try
        {
            return _codexConfig.Uninstall(paths);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return PreserveForManualCleanup(paths.CodexConfigFile);
        }
    }

    private ManagedFileResult UninstallAgentsWithRecovery(
        ProductPaths paths,
        bool agentsWasCreated)
    {
        try
        {
            return _agentsOverride.Uninstall(paths, agentsWasCreated);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return PreserveForManualCleanup(paths.AgentsOverrideFile);
        }
    }

    private static ManagedFileResult PreserveForManualCleanup(string target)
    {
        string? safetyCopy = null;
        if (File.Exists(target))
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
            safetyCopy = $"{target}.thalen-helper.manual-cleanup.{timestamp}.bak";
            File.Copy(target, safetyCopy, false);
        }

        return new ManagedFileResult(target, false, false, safetyCopy, "manual-cleanup-required");
    }

    private static void StopOwnedMcpProcesses(string expectedExecutable)
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("local-gpu-reviewer"))
        {
            using (process)
            {
                try
                {
                    var actual = process.MainModule?.FileName;
                    if (actual is not null
                        && string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(entireProcessTree: true);
                        _ = process.WaitForExit(5_000);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Inno Setup will retry file removal; do not terminate unrelated or unverifiable processes.
                }
            }
        }
    }
}
