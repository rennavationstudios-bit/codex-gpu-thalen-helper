using System.Text.Json;
using System.Text.Json.Serialization;
using ThalenHelper.Core;

return await CliApplication.RunAsync(args).ConfigureAwait(false);

internal static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var parsed = new ParsedArguments(args);
            if (parsed.Positionals.Count == 0 || parsed.Has("help") || parsed.Positionals[0] is "help" or "--help" or "-h")
            {
                WriteHelp();
                return 0;
            }

            var command = parsed.Positionals[0].ToLowerInvariant();
            var paths = ResolvePaths(parsed, command);
            var store = new StateStore(paths.StateFile);
            var control = new ControlService(paths, store);
            return command switch
            {
                "version" => Write(new { product = ProductInfo.Name, version = ProductInfo.Version }),
                "hardware" => Write(new HardwareDetector().Detect()),
                "status" => await StatusAsync(store).ConfigureAwait(false),
                "doctor" => await DoctorAsync(paths, store).ConfigureAwait(false),
                "install" => await InstallAsync(parsed, paths).ConfigureAwait(false),
                "repair" => await RepairAsync(paths).ConfigureAwait(false),
                "enable" => Write(await control.EnableAsync().ConfigureAwait(false)),
                "disable" => Write(await control.DisableAsync(!parsed.Has("keep-codex-entry")).ConfigureAwait(false)),
                "pause" => Write(await control.PauseAsync().ConfigureAwait(false)),
                "resume" => Write(await control.ResumeAsync().ConfigureAwait(false)),
                "release-gpu" => Write(await control.ReleaseGpuAsync().ConfigureAwait(false)),
                "low-impact" => await ToggleLowImpactAsync(parsed, control).ConfigureAwait(false),
                "keep-warm" => await ToggleKeepWarmAsync(parsed, control).ConfigureAwait(false),
                "model" => await ModelAsync(parsed, store, control).ConfigureAwait(false),
                "models" => await ModelsAsync(parsed, paths, store, control).ConfigureAwait(false),
                "test" => await TestAsync(store).ConfigureAwait(false),
                "ollama" => await OllamaAsync(parsed, paths, store).ConfigureAwait(false),
                "diagnostics" => await DiagnosticsAsync(parsed, paths, store).ConfigureAwait(false),
                "update" => await UpdateAsync(parsed).ConfigureAwait(false),
                "uninstall" => await UninstallAsync(parsed, paths, store).ConfigureAwait(false),
                _ => Fail("UNKNOWN_COMMAND", $"Unknown command: {command}")
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return Fail("COMMAND_FAILED", Sanitize(exception.Message));
        }
    }

    internal static ProductPaths ResolvePaths(ParsedArguments parsed, string command)
    {
        var requestedInstallDirectory = parsed.Get("install-dir");
        if (!string.Equals(command, "uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return ProductPaths.Resolve(
                requestedInstallDirectory,
                parsed.Get("state-dir"),
                parsed.Get("codex-home"));
        }

        var probe = ProductPaths.Resolve(requestedInstallDirectory ?? AppContext.BaseDirectory);
        var context = InstallContextStore.Load(probe.InstallDirectory);
        return ProductPaths.Resolve(
            probe.InstallDirectory,
            parsed.Get("state-dir") ?? context?.StateDirectory,
            parsed.Get("codex-home") ?? context?.CodexHome);
    }

    private static async Task<int> StatusAsync(StateStore store)
    {
        var state = await store.LoadAsync().ConfigureAwait(false);
        if (state is null)
        {
            return Fail("NOT_CONFIGURED", "The helper is not configured.");
        }

        using var client = new OllamaClient();
        var health = await new ReviewerService(store, client).GetHealthAsync().ConfigureAwait(false);
        return Write(new { state, health });
    }

    private static async Task<int> DoctorAsync(ProductPaths paths, StateStore store)
    {
        var hardware = new HardwareDetector().Detect();
        var state = await store.LoadAsync().ConfigureAwait(false);
        using var client = new OllamaClient();
        var health = await new ReviewerService(store, client).GetHealthAsync().ConfigureAwait(false);
        var autoStart = state is not null && new OllamaAutoStartManager().IsConfigured(paths);
        var checks = new
        {
            architectureSupported = hardware.OperatingSystem.IsSupported,
            statePresent = state is not null,
            configPresent = File.Exists(paths.CodexConfigFile),
            agentsOverridePresent = File.Exists(paths.AgentsOverrideFile),
            ollamaAutoStartConfigured = autoStart,
            ollamaLoopbackOnly = OllamaAutoStartManager.IsPortLoopbackOnly(11434),
            endpointReachable = health.EndpointReachable,
            selectedModelAvailable = health.ModelAvailable,
            selectedModelLoaded = health.ModelLoaded,
            note = "Doctor is passive and did not run local model inference."
        };
        return Write(new { checks, hardware, state, health });
    }

    private static async Task<int> InstallAsync(ParsedArguments parsed, ProductPaths paths)
    {
        if (!parsed.Has("yes"))
        {
            return Fail("CONFIRMATION_REQUIRED", "Use the interactive setup wizard, or supply --yes with explicit silent-install choices.");
        }

        if (parsed.Has("reliability-baseline"))
        {
            return Fail(
                "INTERACTIVE_PREVIEW_REQUIRED",
                "The optional reliability baseline can be selected only in the interactive setup wizard after reviewing its before/after diff preview.");
        }

        var autoStart = !string.Equals(parsed.Get("auto-start"), "false", StringComparison.OrdinalIgnoreCase);
        var options = new InstallationOptions(
            paths,
            parsed.Get("model"),
            parsed.Get("models-dir"),
            parsed.Has("allow-cpu"),
            parsed.Has("accept-restricted-license"),
            autoStart,
            parsed.Has("pull-and-validate"),
            InstallReliabilityBaseline: false);
        if (options.PullAndValidateModel)
        {
            Console.Error.WriteLine("Integration: local_gpu_reviewer | Provider: Ollama | Purpose: installer exact-response and bounded code-review validation");
        }

        var outcome = await new InstallationManager().ConfigureAsync(options).ConfigureAwait(false);
        Write(outcome);
        return outcome.Success ? 0 : 1;
    }

    private static async Task<int> RepairAsync(ProductPaths paths)
    {
        var outcome = await new InstallationManager().RepairAsync(paths).ConfigureAwait(false);
        return Write(outcome);
    }

    private static async Task<int> ToggleLowImpactAsync(ParsedArguments parsed, ControlService control)
    {
        var enabled = ParseOnOff(parsed, 1);
        return Write(await control.SetLowImpactAsync(enabled).ConfigureAwait(false));
    }

    private static async Task<int> ToggleKeepWarmAsync(ParsedArguments parsed, ControlService control)
    {
        var enabled = ParseOnOff(parsed, 1);
        return Write(await control.SetKeepWarmAsync(enabled).ConfigureAwait(false));
    }

    private static async Task<int> ModelAsync(ParsedArguments parsed, StateStore store, ControlService control)
    {
        if (parsed.Positionals.Count < 2)
        {
            return Fail("MISSING_SUBCOMMAND", "Use model recommend or model change <tag>.");
        }

        switch (parsed.Positionals[1].ToLowerInvariant())
        {
            case "recommend":
                {
                    var catalog = new ModelCatalogService().LoadBundled();
                    var hardware = new HardwareDetector().Detect();
                    var recommendation = new ModelSelector().Recommend(hardware, catalog, parsed.Has("allow-cpu"));
                    return Write(new { recommendation, hardware });
                }
            case "change":
                {
                    if (parsed.Positionals.Count < 3 || !parsed.Has("yes"))
                    {
                        return Fail("CONFIRMATION_REQUIRED", "Use model change <tag> --yes. This downloads and runs a bounded validation inference.");
                    }

                    var state = await store.LoadAsync().ConfigureAwait(false);
                    Console.Error.WriteLine($"Integration: local_gpu_reviewer | Provider: Ollama | Model: {parsed.Positionals[2]} | Purpose: bounded model-change validation");
                    var service = new ModelChangeService(store, control, new InstallationManager());
                    var result = await service.ChangeAsync(
                        parsed.Positionals[2],
                        parsed.Has("accept-restricted-license")).ConfigureAwait(false);
                    return Write(result);
                }
            default:
                return Fail("UNKNOWN_SUBCOMMAND", "Use model recommend or model change <tag>.");
        }
    }

    private static async Task<int> ModelsAsync(
        ParsedArguments parsed,
        ProductPaths paths,
        StateStore store,
        ControlService control)
    {
        if (parsed.Positionals.Count < 3
            || !string.Equals(parsed.Positionals[1], "move", StringComparison.OrdinalIgnoreCase)
            || !parsed.Has("yes"))
        {
            return Fail("CONFIRMATION_REQUIRED", "Use models move <fixed-local-directory> --yes.");
        }

        var result = await new ModelsMoveService(paths, store, control)
            .MoveAsync(parsed.Positionals[2]).ConfigureAwait(false);
        return Write(result);
    }

    private static async Task<int> TestAsync(StateStore store)
    {
        var state = await store.LoadAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The helper is not configured.");
        Console.Error.WriteLine($"Integration: local_gpu_reviewer | Provider: Ollama | Model: {state.SelectedModel ?? "none"} | Purpose: exact-response and bounded code-review test");
        var validation = await new InstallationManager().ValidateSelectedModelAsync(state).ConfigureAwait(false);
        return Write(validation);
    }

    private static async Task<int> OllamaAsync(ParsedArguments parsed, ProductPaths paths, StateStore store)
    {
        if (parsed.Positionals.Count < 2)
        {
            return Fail("MISSING_SUBCOMMAND", "Use ollama autostart, ollama verify, or ollama install --yes.");
        }

        var state = await store.LoadAsync().ConfigureAwait(false);
        if (state is not null && !IntegrationOwnership.IsManagedByHelper(state))
        {
            return Fail(
                "EXISTING_INTEGRATION_PRESERVED",
                "This helper does not own the existing local_gpu_reviewer integration, so Ollama, startup, models, and environment were not changed or probed.");
        }

        switch (parsed.Positionals[1].ToLowerInvariant())
        {
            case "autostart":
                if (state is null)
                {
                    return parsed.Has("quiet") ? 0 : Fail("NOT_CONFIGURED", "The helper is not configured.");
                }

                var startup = await new OllamaAutoStartManager().EnsureRunningAsync(paths, state).ConfigureAwait(false);
                var startupIsSafe = ModelIntegrity.IsOperationallySafe(startup, state);
                var previouslyEnabled = state.Availability == HelperAvailability.Enabled;
                state.LastHealthCheckAt = DateTimeOffset.UtcNow;
                state.LastHealthCheckCode = startup.Code;
                if (!startupIsSafe)
                {
                    state.Availability = HelperAvailability.Disabled;
                    GpuCoordination.RequestCancellation();
                }

                await store.SaveAsync(state).ConfigureAwait(false);
                if (!startupIsSafe && previouslyEnabled)
                {
                    new CodexConfigManager().SetEnabled(paths, false);
                }

                if (!parsed.Has("quiet"))
                {
                    _ = Write(startup);
                }

                return StartupVerificationExitCode(startup, state);
            case "verify":
                if (state is null)
                {
                    return Fail("NOT_CONFIGURED", "The helper is not configured.");
                }

                var verification = await new OllamaAutoStartManager().VerifyAsync(paths, state, false).ConfigureAwait(false);
                _ = Write(verification);
                return StartupVerificationExitCode(verification, state);
            case "install":
                if (!parsed.Has("yes"))
                {
                    return Fail("CONFIRMATION_REQUIRED", "Use ollama install --yes to download, verify, and launch the current official installer.");
                }

                using (var installer = new OllamaInstallerService())
                {
                    return Write(await installer.DownloadVerifyAndLaunchAsync(waitForExit: true).ConfigureAwait(false));
                }
            default:
                return Fail("UNKNOWN_SUBCOMMAND", "Use ollama autostart, ollama verify, or ollama install --yes.");
        }
    }

    private static async Task<int> DiagnosticsAsync(ParsedArguments parsed, ProductPaths paths, StateStore store)
    {
        if (parsed.Positionals.Count < 3 || !string.Equals(parsed.Positionals[1], "export", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("MISSING_DESTINATION", "Use diagnostics export <output.json>.");
        }

        var hardware = new HardwareDetector().Detect();
        var state = await store.LoadAsync().ConfigureAwait(false);
        using var client = new OllamaClient();
        var health = await new ReviewerService(store, client).GetHealthAsync().ConfigureAwait(false);
        await new DiagnosticsExporter().ExportAsync(parsed.Positionals[2], paths, hardware, state, health).ConfigureAwait(false);
        return Write(new { success = true, code = "DIAGNOSTICS_EXPORTED", path = Path.GetFullPath(parsed.Positionals[2]) });
    }

    private static async Task<int> UpdateAsync(ParsedArguments parsed)
    {
        using var updater = new ProjectUpdateService();
        var update = await updater.CheckAsync().ConfigureAwait(false);
        if (!parsed.Has("yes"))
        {
            return Write(update);
        }

        return Write(await updater.DownloadVerifyAndLaunchAsync(update, launch: true).ConfigureAwait(false));
    }

    private static async Task<int> UninstallAsync(ParsedArguments parsed, ProductPaths paths, StateStore store)
    {
        if (!parsed.Has("yes"))
        {
            return Fail("CONFIRMATION_REQUIRED", "Use uninstall --yes. Add --remove-owned-model only to remove a model downloaded by this helper.");
        }

        var result = await new UninstallManager(paths, store)
            .UninstallAsync(parsed.Has("remove-owned-model")).ConfigureAwait(false);
        return Write(result);
    }

    private static bool ParseOnOff(ParsedArguments parsed, int index)
    {
        if (parsed.Positionals.Count <= index)
        {
            throw new ArgumentException("Specify on or off.");
        }

        return parsed.Positionals[index].ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => throw new ArgumentException("Specify on or off.")
        };
    }

    internal static int StartupVerificationExitCode(
        OllamaStartupVerification verification,
        InstallationState state)
        => ModelIntegrity.IsOperationallySafe(verification, state) ? 0 : 1;

    private static int Write(object? value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
        return value is ControlResult { Success: false }
            or InstallationOutcome { Success: false }
            or ModelValidationResult { Success: false }
            or ModelChangeResult { Success: false }
            or ModelsMoveResult { Success: false }
            or UninstallResult { Success: false }
            or OllamaInstallResult { Success: false }
            or OllamaStartupVerification { EndpointReachable: false }
            ? 1
            : 0;
    }

    private static int Fail(string code, string message)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, code, message }, JsonOptions));
        return 2;
    }

    private static string Sanitize(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ').Trim() is var value && value.Length > 500 ? value[..500] : value;

    private static void WriteHelp()
    {
        Console.WriteLine("""
            Codex GPU Thalen Helper CLI

            thalen-helper status
            thalen-helper doctor
            thalen-helper enable | disable | pause | resume | release-gpu
            thalen-helper low-impact on|off
            thalen-helper keep-warm on|off
            thalen-helper model recommend [--allow-cpu]
            thalen-helper model change <tag> --yes [--accept-restricted-license]
            thalen-helper models move <fixed-local-directory> --yes
            thalen-helper repair
            thalen-helper test
            thalen-helper diagnostics export <output.json>
            thalen-helper update [--yes]
            thalen-helper ollama verify | autostart | install --yes
            thalen-helper uninstall --yes [--remove-owned-model]

            Silent configuration requires --yes and explicit --codex-home/--model/--models-dir choices.
            The optional reliability baseline is installed only from the interactive wizard after its diff preview.
            Real inference occurs only for test, model change, or install --pull-and-validate.
            """);
    }
}

internal sealed class ParsedArguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positionals { get; } = [];

    public ParsedArguments(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                Positionals.Add(argument);
                continue;
            }

            var option = argument[2..];
            var equals = option.IndexOf('=');
            if (equals >= 0)
            {
                _options[option[..equals]] = option[(equals + 1)..];
                continue;
            }

            if (index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                _options[option] = arguments[++index];
            }
            else
            {
                _options[option] = null;
            }
        }
    }

    public bool Has(string name) => _options.ContainsKey(name);
    public string? Get(string name) => _options.GetValueOrDefault(name);
}
