using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace ThalenHelper.Core;

public sealed record OllamaStartupVerification(
    bool AutoStartConfigured,
    bool EndpointReachable,
    bool ModelStorageConfigured,
    bool SelectedModelStoredInConfiguredPath,
    bool SelectedModelAvailable,
    bool SelectedModelDigestMatches,
    bool LoopbackOnly,
    bool StartedNewProcess,
    string Code,
    string Message);

public sealed record OllamaListenerStatus(
    bool HasListeners,
    bool LoopbackOnly,
    int ListenerCount);

public sealed record OllamaIdleVerification(bool Idle, string Code, string Message);

internal sealed record OllamaStartupConfigurationPlan(
    string ModelDirectory,
    string Host,
    string? RunEntry,
    bool Enabled,
    bool HelperOwnsAutoStart);

public enum OllamaRestartModelPolicy
{
    RequireIdle
}

public interface IOllamaStartupPlatform
{
    string? GetUserEnvironmentVariable(string name);
    void SetUserEnvironmentVariable(string name, string? value);
    void SetProcessEnvironmentVariable(string name, string? value);
    string? GetRunEntry();
    void SetRunEntry(string? command);
    bool HasExternalAutoStart();
    bool IsAnyOllamaProcessRunning();
    string? FindOllamaExecutable();
    bool StartOllama(string executable, string modelDirectory, HelperPreferences preferences);
    bool StopOllamaProcesses(string expectedExecutable);
    bool IsPortLoopbackOnly(int port);
    void BroadcastEnvironmentChange();
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class OllamaAutoStartManager
{
    private const string StartupMutexName = @"Local\CodexGpuThalenHelperOllamaStartup";
    private readonly Func<OllamaClient> _clientFactory;
    private readonly IOllamaStartupPlatform _platform;

    public OllamaAutoStartManager(
        Func<OllamaClient>? clientFactory = null,
        IOllamaStartupPlatform? platform = null)
    {
        _clientFactory = clientFactory ?? (() => new OllamaClient());
        _platform = platform ?? new WindowsOllamaStartupPlatform();
    }

    internal IOllamaStartupPlatform Platform => _platform;

    public void Configure(ProductPaths paths, InstallationState state, bool enabled)
        => ApplyConfiguration(PreviewConfiguration(paths, state, enabled), state);

    internal OllamaStartupConfigurationPlan PreviewConfiguration(
        ProductPaths paths,
        InstallationState state,
        bool enabled)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.ModelStorageLocation))
        {
            throw new InvalidOperationException("A model storage location must be selected before configuring Ollama startup.");
        }

        var modelDirectory = Path.GetFullPath(state.ModelStorageLocation);
        var externalAutoStart = _platform.HasExternalAutoStart();
        var helperOwnsAutoStart = enabled && !externalAutoStart;
        return new OllamaStartupConfigurationPlan(
            modelDirectory,
            "127.0.0.1:11434",
            helperOwnsAutoStart ? GetCanonicalRunCommand(paths) : null,
            enabled,
            helperOwnsAutoStart);
    }

    internal void ApplyConfiguration(OllamaStartupConfigurationPlan plan, InstallationState state)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        _platform.SetUserEnvironmentVariable("OLLAMA_MODELS", plan.ModelDirectory);
        _platform.SetUserEnvironmentVariable("OLLAMA_HOST", plan.Host);
        _platform.SetProcessEnvironmentVariable("OLLAMA_MODELS", plan.ModelDirectory);
        _platform.SetProcessEnvironmentVariable("OLLAMA_HOST", plan.Host);
        _platform.BroadcastEnvironmentChange();
        _platform.SetRunEntry(plan.RunEntry);
        state.Preferences = state.Preferences with { AutoStartOllama = plan.Enabled };
        state.StartupEntryOwnedByHelper = plan.HelperOwnsAutoStart;
    }

    public bool IsConfigured(ProductPaths paths)
    {
        var value = _platform.GetRunEntry();
        return string.Equals(value, GetCanonicalRunCommand(paths), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCanonicalRunCommand(ProductPaths paths)
        => $"\"{paths.CliExecutable}\" ollama autostart --quiet";

    public string? GetConfiguredUserModelDirectory()
        => _platform.GetUserEnvironmentVariable("OLLAMA_MODELS");

    public async Task<OllamaIdleVerification> VerifyIdleForStorageChangeAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = _clientFactory();
            var running = await client.GetRunningModelsAsync(cancellationToken).ConfigureAwait(false);
            return running.Count == 0
                ? new OllamaIdleVerification(true, "OLLAMA_IDLE", "Ollama has no loaded models.")
                : new OllamaIdleVerification(
                    false,
                    "OLLAMA_RESTART_NOT_IDLE",
                    "Ollama has a loaded model. Storage activation refused without unloading or stopping it.");
        }
        catch (OllamaException) when (!_platform.IsAnyOllamaProcessRunning())
        {
            return new OllamaIdleVerification(true, "OLLAMA_STOPPED", "Ollama is stopped and has no resident model.");
        }
        catch (OllamaException exception)
        {
            return new OllamaIdleVerification(
                false,
                "OLLAMA_RESTART_IDLE_UNCONFIRMED",
                $"Ollama is running but idle state could not be confirmed: {exception.Message}");
        }
    }

    public Task<OllamaStartupVerification> ApplyConfigurationAsync(
        ProductPaths paths,
        InstallationState state,
        string? previousModelDirectory,
        bool enabled,
        bool allowSafeRestart,
        CancellationToken cancellationToken = default)
        => ApplyConfigurationAsync(
            paths,
            state,
            previousModelDirectory,
            enabled,
            allowSafeRestart,
            OllamaRestartModelPolicy.RequireIdle,
            cancellationToken);

    public async Task<OllamaStartupVerification> ApplyConfigurationAsync(
        ProductPaths paths,
        InstallationState state,
        string? previousModelDirectory,
        bool enabled,
        bool allowSafeRestart,
        OllamaRestartModelPolicy restartModelPolicy,
        CancellationToken cancellationToken = default)
    {
        var desired = Path.GetFullPath(state.ModelStorageLocation!);
        var defaultDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ollama",
            "models"));
        var previous = string.IsNullOrWhiteSpace(previousModelDirectory)
            ? defaultDirectory
            : Path.GetFullPath(previousModelDirectory);
        var pathChanged = !string.Equals(previous, desired, StringComparison.OrdinalIgnoreCase);

        if (pathChanged && allowSafeRestart && restartModelPolicy == OllamaRestartModelPolicy.RequireIdle)
        {
            try
            {
                using var client = _clientFactory();
                var running = await client.GetRunningModelsAsync(cancellationToken).ConfigureAwait(false);
                if (running.Count > 0)
                {
                    var current = await VerifyAsync(paths, state, false, cancellationToken).ConfigureAwait(false);
                    return current with
                    {
                        Code = "OLLAMA_RESTART_NOT_IDLE",
                        Message = "Ollama has a loaded model. Activation refused without unloading or stopping any user-owned model."
                    };
                }
            }
            catch (OllamaException exception) when (!_platform.IsAnyOllamaProcessRunning())
            {
                _ = exception;
                // A stopped Ollama process is idle. Configuration may start it once with the new path.
            }
            catch (OllamaException exception)
            {
                var current = await VerifyAsync(paths, state, false, cancellationToken).ConfigureAwait(false);
                return current with
                {
                    Code = "OLLAMA_RESTART_IDLE_UNCONFIRMED",
                    Message = $"Ollama is running but idle state could not be confirmed: {exception.Message}"
                };
            }
        }

        Configure(paths, state, enabled);
        var initial = await VerifyAsync(paths, state, false, cancellationToken).ConfigureAwait(false);
        if (!initial.EndpointReachable || !pathChanged)
        {
            return initial.EndpointReachable
                ? initial
                : await EnsureRunningAsync(paths, state, cancellationToken).ConfigureAwait(false);
        }

        if (!allowSafeRestart)
        {
            return initial with
            {
                Code = "OLLAMA_RESTART_REQUIRED",
                Message = "OLLAMA_MODELS changed, but a running Ollama process has not been restarted to inherit the new directory."
            };
        }

        if (restartModelPolicy != OllamaRestartModelPolicy.RequireIdle)
        {
            throw new InvalidOperationException("Unsupported Ollama restart policy.");
        }

        return initial with
        {
            Code = "OLLAMA_RESTART_REQUIRED",
            Message = "OLLAMA_MODELS changed while Ollama was running. The helper will not stop a shared provider because runtime ownership cannot be proven atomically; close Ollama manually, then retry."
        };
    }

    public async Task<OllamaStartupVerification> EnsureRunningAsync(
        ProductPaths paths,
        InstallationState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(state);
        using var startupSemaphore = new Semaphore(1, 1, StartupMutexName);
        var acquired = false;
        try
        {
            acquired = await WaitForSemaphoreAsync(startupSemaphore, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                return Failure(paths, state, "STARTUP_LOCK_TIMEOUT", "Another Ollama startup check did not finish in time.");
            }

            var existing = await VerifyAsync(paths, state, false, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existing.Code, "OLLAMA_NETWORK_EXPOSURE", StringComparison.Ordinal))
            {
                return existing;
            }

            if (existing.EndpointReachable)
            {
                return existing;
            }

            if (_platform.IsAnyOllamaProcessRunning())
            {
                for (var attempt = 0; attempt < 15; attempt++)
                {
                    await _platform.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    var delayed = await VerifyAsync(paths, state, false, cancellationToken).ConfigureAwait(false);
                    if (delayed.EndpointReachable)
                    {
                        return delayed;
                    }
                }

                return Failure(
                    paths,
                    state,
                    "OLLAMA_PROCESS_UNHEALTHY",
                    "An Ollama process exists but its endpoint is unavailable; a duplicate process was not created.");
            }

            var executable = _platform.FindOllamaExecutable();
            if (executable is null)
            {
                return Failure(
                    paths,
                    state,
                    "OLLAMA_NOT_INSTALLED",
                    "Ollama is not installed. Startup remains configured for when Ollama becomes available.");
            }

            if (!_platform.StartOllama(executable, state.ModelStorageLocation!, state.Preferences))
            {
                return Failure(paths, state, "OLLAMA_START_FAILED", "Windows did not start Ollama.");
            }

            for (var attempt = 0; attempt < 30; attempt++)
            {
                await _platform.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                var verified = await VerifyAsync(paths, state, true, cancellationToken).ConfigureAwait(false);
                if (verified.EndpointReachable)
                {
                    return verified;
                }
            }

            return Failure(
                paths,
                state,
                "OLLAMA_START_TIMEOUT",
                "Ollama was started but its loopback endpoint did not become ready within 30 seconds.",
                startedNewProcess: true);
        }
        finally
        {
            if (acquired)
            {
                startupSemaphore.Release();
            }
        }
    }

    public async Task<OllamaStartupVerification> VerifyAsync(
        ProductPaths paths,
        InstallationState state,
        bool startedNewProcess,
        CancellationToken cancellationToken = default)
    {
        var autoStart = IsConfigured(paths);
        var externalAutoStartUnverified = !autoStart
            && state.Preferences.AutoStartOllama
            && _platform.HasExternalAutoStart();
        var modelStorage = IsModelPathConfigured(state);
        var storedInConfiguredPath = IsSelectedModelManifestPresent(state);
        var loopback = _platform.IsPortLoopbackOnly(11434);
        if (!loopback)
        {
            return new OllamaStartupVerification(
                autoStart,
                false,
                modelStorage,
                storedInConfiguredPath,
                false,
                false,
                false,
                startedNewProcess,
                "OLLAMA_NETWORK_EXPOSURE",
                "Ollama is listening on a non-loopback address; local review must remain disabled.");
        }

        try
        {
            using var client = _clientFactory();
            var models = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            var selected = state.SelectedModel;
            var selectedModel = ModelIntegrity.FindSelectedModel(models, selected);
            var available = selectedModel is not null;
            var digestMatches = selected is null
                || ModelIntegrity.DigestMatches(selectedModel?.Digest, state.SelectedModelDigest);
            var complete = loopback
                && modelStorage
                && (selected is null || (available && digestMatches && storedInConfiguredPath));
            var code = !loopback
                ? "OLLAMA_NETWORK_EXPOSURE"
                : !modelStorage
                    ? "MODEL_PATH_NOT_CONFIGURED"
                    : selected is not null && !storedInConfiguredPath
                        ? "MODEL_NOT_IN_CONFIGURED_PATH"
                        : selected is not null && !available
                            ? "SELECTED_MODEL_UNAVAILABLE"
                            : selected is not null && !digestMatches
                                ? "MODEL_DIGEST_MISMATCH"
                                : externalAutoStartUnverified
                                    ? "EXTERNAL_AUTOSTART_UNVERIFIED"
                                    : !autoStart
                                        ? "MANUAL_START_REQUIRED"
                                        : complete
                                            ? "OK"
                                            : "VERIFICATION_INCOMPLETE";
            var message = code switch
            {
                "OK" => "Ollama responded on loopback, the selected model is available from the configured model directory, and no duplicate process was started.",
                "EXTERNAL_AUTOSTART_UNVERIFIED" => "An existing Ollama startup artifact was preserved to avoid creating a duplicate, but its target and behavior were not verified. Automatic startup is not certified.",
                "MANUAL_START_REQUIRED" => "Ollama is healthy and loopback-only, but automatic startup was declined; local review requires manually starting Ollama after sign-in.",
                "OLLAMA_NETWORK_EXPOSURE" => "Ollama is listening on a non-loopback address; local review must remain disabled.",
                "MODEL_PATH_NOT_CONFIGURED" => "The persisted per-user OLLAMA_MODELS value does not match the selected model directory.",
                "MODEL_NOT_IN_CONFIGURED_PATH" => "The selected model manifest was not found beneath the configured OLLAMA_MODELS directory.",
                "SELECTED_MODEL_UNAVAILABLE" => "The configured selected model was not returned by the loopback Ollama endpoint.",
                "MODEL_DIGEST_MISMATCH" => "The selected model digest does not match the audited catalog digest. Local review must remain disabled.",
                _ => "Ollama startup verification is incomplete."
            };
            return new OllamaStartupVerification(
                autoStart,
                true,
                modelStorage,
                storedInConfiguredPath,
                available,
                digestMatches,
                loopback,
                startedNewProcess,
                code,
                message);
        }
        catch (OllamaException exception)
        {
            return new OllamaStartupVerification(
                autoStart,
                false,
                modelStorage,
                storedInConfiguredPath,
                false,
                false,
                loopback,
                startedNewProcess,
                exception.Code,
                exception.Message);
        }
    }

    public void RemoveOwnedStartupEntry() => _platform.SetRunEntry(null);

    public void RestoreOwnedEnvironment(InstallationState state)
    {
        Restore("OLLAMA_MODELS", state.ModelStorageLocation, state.PreviousUserEnvironment.GetValueOrDefault("OLLAMA_MODELS"));
        Restore("OLLAMA_HOST", "127.0.0.1:11434", state.PreviousUserEnvironment.GetValueOrDefault("OLLAMA_HOST"));
        _platform.BroadcastEnvironmentChange();
    }

    // An unused port is safe for the helper to start on loopback. It is not an
    // exposure and must not be confused with a wildcard/non-loopback listener.
    // Endpoint reachability is verified separately by the caller.
    public static bool IsPortLoopbackOnly(int port)
        => HasNoNonLoopbackListener(GetListenerStatus(port));

    internal static bool HasNoNonLoopbackListener(OllamaListenerStatus status)
        => !status.HasListeners || status.LoopbackOnly;

    public static OllamaListenerStatus GetListenerStatus(int port)
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(endpoint => endpoint.Port == port)
            .ToArray();
        return EvaluateListenerStatus(listeners);
    }

    internal static OllamaListenerStatus EvaluateListenerStatus(IEnumerable<IPEndPoint> listeners)
    {
        var endpoints = listeners.ToArray();
        return new OllamaListenerStatus(
            endpoints.Length > 0,
            endpoints.Length > 0 && endpoints.All(endpoint => IPAddress.IsLoopback(endpoint.Address)),
            endpoints.Length);
    }

    public static bool IsSelectedModelManifestPresent(InstallationState state)
    {
        if (string.IsNullOrWhiteSpace(state.ModelStorageLocation) || string.IsNullOrWhiteSpace(state.SelectedModel))
        {
            return false;
        }

        var separator = state.SelectedModel.LastIndexOf(':');
        if (separator <= 0 || separator == state.SelectedModel.Length - 1)
        {
            return false;
        }

        var name = state.SelectedModel[..separator];
        var tag = state.SelectedModel[(separator + 1)..];
        var components = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = Path.Combine(
            [state.ModelStorageLocation, "manifests", "registry.ollama.ai", "library", .. components, tag]);
        var root = Path.GetFullPath(state.ModelStorageLocation).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full);
    }

    private OllamaStartupVerification Failure(
        ProductPaths paths,
        InstallationState state,
        string code,
        string message,
        bool startedNewProcess = false)
        => new(
            IsConfigured(paths),
            false,
            IsModelPathConfigured(state),
            IsSelectedModelManifestPresent(state),
            false,
            false,
            _platform.IsPortLoopbackOnly(11434),
            startedNewProcess,
            code,
            message);

    private bool IsModelPathConfigured(InstallationState state)
    {
        var configured = _platform.GetUserEnvironmentVariable("OLLAMA_MODELS");
        return !string.IsNullOrWhiteSpace(configured)
            && !string.IsNullOrWhiteSpace(state.ModelStorageLocation)
            && string.Equals(
                Path.GetFullPath(configured),
                Path.GetFullPath(state.ModelStorageLocation),
                StringComparison.OrdinalIgnoreCase);
    }

    private void Restore(string name, string? ownedValue, string? priorValue)
    {
        var current = _platform.GetUserEnvironmentVariable(name);
        if (string.Equals(current, ownedValue, StringComparison.OrdinalIgnoreCase))
        {
            _platform.SetUserEnvironmentVariable(name, priorValue);
            _platform.SetProcessEnvironmentVariable(name, priorValue);
        }
    }

    private static async Task<bool> WaitForSemaphoreAsync(Semaphore semaphore, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await Task.Run(
            () => WaitHandle.WaitAny([semaphore, cancellationToken.WaitHandle], timeout),
            CancellationToken.None).ConfigureAwait(false);
        if (result == 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return result == 0;
    }
}

internal sealed class WindowsOllamaStartupPlatform : IOllamaStartupPlatform
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Codex GPU Thalen Helper - Ollama";

    public string? GetUserEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

    public void SetUserEnvironmentVariable(string name, string? value)
        => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);

    public void SetProcessEnvironmentVariable(string name, string? value)
        => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);

    public string? GetRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(RunValueName)?.ToString();
    }

    public void SetRunEntry(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("The per-user Windows startup registry key is unavailable.");
        if (command is null)
        {
            key.DeleteValue(RunValueName, false);
        }
        else
        {
            key.SetValue(RunValueName, command, RegistryValueKind.String);
        }
    }

    public bool HasExternalAutoStart()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
        {
            if (key is not null)
            {
                foreach (var name in key.GetValueNames())
                {
                    if (string.Equals(name, RunValueName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value = key.GetValue(name)?.ToString() ?? string.Empty;
                    if (name.Contains("Ollama", StringComparison.OrdinalIgnoreCase)
                        || value.Contains("Ollama", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        var startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return Directory.Exists(startupDirectory)
            && Directory.EnumerateFiles(startupDirectory)
                .Select(Path.GetFileName)
                .Any(name => name is not null
                    && name.Contains("Ollama", StringComparison.OrdinalIgnoreCase));
    }

    public bool IsAnyOllamaProcessRunning()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (IsKnownOllamaProcessName(process.ProcessName))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // A process can exit between enumeration and inspection.
                }
            }
        }

        return false;
    }

    public string? FindOllamaExecutable()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Ollama",
            "ollama.exe");
        if (File.Exists(local))
        {
            return local;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim(), "ollama.exe"))
            .FirstOrDefault(File.Exists);
    }

    public bool StartOllama(string executable, string modelDirectory, HelperPreferences preferences)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "serve",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.Environment["OLLAMA_MODELS"] = Path.GetFullPath(modelDirectory);
        start.Environment["OLLAMA_HOST"] = "127.0.0.1:11434";
        start.Environment["OLLAMA_KEEP_ALIVE"] = preferences.KeepWarm
            ? $"{Math.Clamp(preferences.IdleUnloadSeconds, 60, 600)}s"
            : "0";
        using var process = Process.Start(start);
        return process is not null;
    }

    public bool StopOllamaProcesses(string expectedExecutable)
    {
        var processes = Process.GetProcesses()
            .Where(process =>
            {
                try
                {
                    return IsKnownOllamaProcessName(process.ProcessName)
                        && IsExpectedOllamaExecutable(process.MainModule?.FileName, expectedExecutable);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    return false;
                }
            })
            .ToArray();
        try
        {
            foreach (var process in processes)
            {
                if (process.HasExited)
                {
                    continue;
                }

                _ = process.CloseMainWindow();
            }

            foreach (var process in processes)
            {
                if (!process.HasExited && !process.WaitForExit(5_000))
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(5_000))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    internal static bool IsKnownOllamaProcessName(string processName)
        => string.Equals(processName, "ollama", StringComparison.OrdinalIgnoreCase)
            || string.Equals(processName, "ollama app", StringComparison.OrdinalIgnoreCase);

    internal static bool IsExpectedOllamaExecutable(string? actualExecutable, string expectedExecutable)
    {
        if (string.IsNullOrWhiteSpace(actualExecutable) || string.IsNullOrWhiteSpace(expectedExecutable))
        {
            return false;
        }

        var actual = Path.GetFullPath(actualExecutable);
        var expected = Path.GetFullPath(expectedExecutable);
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var actualName = Path.GetFileName(actual);
        return string.Equals(actualName, "ollama app.exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetDirectoryName(actual),
                Path.GetDirectoryName(expected),
                StringComparison.OrdinalIgnoreCase);
    }

    public bool IsPortLoopbackOnly(int port)
        => OllamaAutoStartManager.IsPortLoopbackOnly(port);

    public void BroadcastEnvironmentChange()
    {
        _ = SendMessageTimeout(
            new IntPtr(0xffff),
            0x001A,
            IntPtr.Zero,
            "Environment",
            0x0002,
            5_000,
            out _);
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        IntPtr wordParameter,
        string longParameter,
        uint flags,
        uint timeout,
        out IntPtr result);
}
