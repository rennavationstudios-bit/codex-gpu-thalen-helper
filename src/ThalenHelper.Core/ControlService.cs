namespace ThalenHelper.Core;

public sealed record ControlResult(bool Success, string Code, string Message, InstallationState? State);

public sealed class ControlService
{
    private readonly ProductPaths _paths;
    private readonly StateStore _stateStore;
    private readonly Func<OllamaClient> _clientFactory;
    private readonly CodexConfigManager _codexConfig;
    private readonly OllamaAutoStartManager _autoStart;

    public ControlService(
        ProductPaths paths,
        StateStore stateStore,
        Func<OllamaClient>? clientFactory = null,
        CodexConfigManager? codexConfig = null,
        OllamaAutoStartManager? autoStart = null)
    {
        _paths = paths;
        _stateStore = stateStore;
        _clientFactory = clientFactory ?? (() => new OllamaClient());
        _codexConfig = codexConfig ?? new CodexConfigManager();
        _autoStart = autoStart ?? new OllamaAutoStartManager(_clientFactory);
    }

    public Task<InstallationState?> GetStatusAsync(CancellationToken cancellationToken = default)
        => _stateStore.LoadAsync(cancellationToken);

    public async Task<ControlResult> PauseAsync(CancellationToken cancellationToken = default)
    {
        var state = await RequireStateAsync(cancellationToken).ConfigureAwait(false);
        state.Availability = HelperAvailability.Paused;
        await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        GpuCoordination.RequestCancellation();
        var unload = await TryUnloadAsync(state.SelectedModel, cancellationToken).ConfigureAwait(false);
        return new ControlResult(
            true,
            unload ? "PAUSED" : "PAUSED_UNLOAD_UNCONFIRMED",
            unload
                ? "New local reviews are paused and the selected model was released."
                : "New local reviews are paused; Ollama was unavailable, so model unload could not be confirmed.",
            state);
    }

    public async Task<ControlResult> ResumeAsync(CancellationToken cancellationToken = default)
    {
        var state = await RequireStateAsync(cancellationToken).ConfigureAwait(false);
        var verification = await _autoStart.VerifyAsync(_paths, state, false, cancellationToken).ConfigureAwait(false);
        if (!ModelIntegrity.IsOperationallySafe(verification, state))
        {
            state.Availability = HelperAvailability.Paused;
            state.LastHealthCheckAt = DateTimeOffset.UtcNow;
            state.LastHealthCheckCode = verification.Code;
            await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            GpuCoordination.RequestCancellation();
            return new ControlResult(false, verification.Code, verification.Message, state);
        }

        GpuCoordination.ClearCancellation();
        state.Availability = HelperAvailability.Enabled;
        await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new ControlResult(true, "RESUMED", "Local reviews are enabled. The model remains unloaded until needed.", state);
    }

    public async Task<ControlResult> ReleaseGpuAsync(CancellationToken cancellationToken = default)
    {
        var state = await RequireStateAsync(cancellationToken).ConfigureAwait(false);
        GpuCoordination.RequestCancellation();
        var unloaded = await TryUnloadAsync(state.SelectedModel, cancellationToken).ConfigureAwait(false);
        if (state.Availability == HelperAvailability.Enabled)
        {
            GpuCoordination.ClearCancellation();
        }

        return new ControlResult(
            unloaded,
            unloaded ? "GPU_RELEASED" : "GPU_RELEASE_UNCONFIRMED",
            unloaded ? "The selected model is no longer loaded." : "Ollama was unavailable, so release could not be confirmed.",
            state);
    }

    public async Task<ControlResult> DisableAsync(bool disableCodexEntry, CancellationToken cancellationToken = default)
    {
        var state = await RequireStateAsync(cancellationToken).ConfigureAwait(false);
        state.Availability = HelperAvailability.Disabled;
        await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        GpuCoordination.RequestCancellation();
        _ = await TryUnloadAsync(state.SelectedModel, cancellationToken).ConfigureAwait(false);
        if (disableCodexEntry)
        {
            _codexConfig.SetEnabled(_paths, false);
        }

        return new ControlResult(
            true,
            "DISABLED",
            disableCodexEntry
                ? "Local review is disabled persistently. Restart Codex to remove the MCP tools from a running session."
                : "Local review calls are disabled persistently.",
            state);
    }

    public async Task<ControlResult> EnableAsync(CancellationToken cancellationToken = default)
    {
        var state = await RequireStateAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(state.SelectedModel))
        {
            return new ControlResult(false, "NO_MODEL", "Select and validate a model before enabling local review.", state);
        }

        var verification = await _autoStart.VerifyAsync(_paths, state, false, cancellationToken).ConfigureAwait(false);
        if (!ModelIntegrity.IsOperationallySafe(verification, state))
        {
            state.Availability = HelperAvailability.Disabled;
            state.LastHealthCheckAt = DateTimeOffset.UtcNow;
            state.LastHealthCheckCode = verification.Code;
            await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            GpuCoordination.RequestCancellation();
            return new ControlResult(false, verification.Code, verification.Message, state);
        }

        _codexConfig.SetEnabled(_paths, true);
        GpuCoordination.ClearCancellation();
        state.Availability = HelperAvailability.Enabled;
        await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new ControlResult(true, "ENABLED", "Local review is enabled. Restart Codex if the MCP tools are not visible.", state);
    }

    public async Task<ControlResult> SetLowImpactAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var state = await RequireStateAsync(cancellationToken).ConfigureAwait(false);
        state.Preferences = state.Preferences with
        {
            LowImpactMode = enabled,
            KeepWarm = enabled ? false : state.Preferences.KeepWarm,
            IdleUnloadSeconds = enabled ? 0 : state.Preferences.IdleUnloadSeconds
        };
        await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        if (enabled)
        {
            _ = await TryUnloadAsync(state.SelectedModel, cancellationToken).ConfigureAwait(false);
        }

        return new ControlResult(true, enabled ? "LOW_IMPACT_ON" : "LOW_IMPACT_OFF", "Preferences updated.", state);
    }

    public async Task<ControlResult> SetKeepWarmAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var state = await RequireStateAsync(cancellationToken).ConfigureAwait(false);
        if (enabled && state.HardwareTier == HardwareTier.Entry)
        {
            return new ControlResult(false, "KEEP_WARM_UNSAFE", "Keep-warm mode is unavailable on the entry hardware tier.", state);
        }

        state.Preferences = state.Preferences with
        {
            KeepWarm = enabled,
            LowImpactMode = enabled ? false : state.Preferences.LowImpactMode,
            IdleUnloadSeconds = enabled ? Math.Max(300, state.Preferences.IdleUnloadSeconds) : 0
        };
        await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        if (!enabled)
        {
            _ = await TryUnloadAsync(state.SelectedModel, cancellationToken).ConfigureAwait(false);
        }

        return new ControlResult(true, enabled ? "KEEP_WARM_ON" : "KEEP_WARM_OFF", "Preferences updated.", state);
    }

    private async Task<InstallationState> RequireStateAsync(CancellationToken cancellationToken)
        => await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The helper is not installed or configured.");

    private async Task<bool> TryUnloadAsync(string? model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return true;
        }

        try
        {
            using var client = _clientFactory();
            await client.UnloadAsync(model, cancellationToken).ConfigureAwait(false);
            var running = await client.GetRunningModelsAsync(cancellationToken).ConfigureAwait(false);
            return !running.Any(item => string.Equals(item.Name, model, StringComparison.OrdinalIgnoreCase));
        }
        catch (OllamaException)
        {
            return false;
        }
    }
}
