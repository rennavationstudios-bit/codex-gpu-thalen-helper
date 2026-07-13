using ThalenHelper.Core;

namespace ThalenHelper.ControlCenter;

public sealed class SetupWizardForm : Form
{
    private readonly ProductPaths _paths;
    private readonly HardwareProfile _hardware;
    private readonly ModelRecommendation _recommendation;
    private readonly StorageRecommendation? _storage;
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, Padding = new Padding(28), BackColor = Color.White };
    private readonly Label _step = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(18, 12, 0, 6) };
    private readonly Button _back = new() { Text = "Back", AutoSize = true, Enabled = false };
    private readonly Button _next = new() { Text = "Next", AutoSize = true };
    private readonly TextBox _modelDirectory = new() { Width = 600 };
    private readonly CheckBox _autoStart = new() { Text = "Start Ollama automatically for this Windows user after sign-in", Checked = true, AutoSize = true };
    private readonly CheckBox _pullAndValidate = new() { Text = "Download and run the selected model validation now", Checked = true, AutoSize = true };
    private readonly CheckBox _installOllama = new() { Text = "Install Ollama from its current official signed Windows release if missing", Checked = true, AutoSize = true };
    private readonly CheckBox _installReliabilityBaseline = new()
    {
        Text = "Add the optional sanitized Codex reliability baseline",
        Checked = false,
        AutoSize = true
    };
    private readonly TextBox _reliabilityPreview = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Width = 720,
        Height = 210,
        Font = new Font("Consolas", 8.5F)
    };
    private readonly ComboBox _modelChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(720, 0) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true };
    private readonly CancellationTokenSource _installationCancellation = new();
    private AgentsOverridePreview? _agentsPreview;
    private bool _installing;
    private int _page;

    public SetupWizardForm(ProductPaths paths)
    {
        _paths = paths;
        _hardware = new HardwareDetector().Detect();
        var catalog = new ModelCatalogService().LoadBundled();
        _recommendation = new ModelSelector().Recommend(_hardware, catalog, false);
        _storage = _recommendation.Model is null ? null : new StorageSelector().Recommend(_hardware, _recommendation.Model);
        foreach (var model in catalog.Models.Where(model =>
            model.CommercialUseAllowed
            && _recommendation.Model is not null
            && model.ParameterBillions <= _recommendation.Model.ParameterBillions))
        {
            _modelChoice.Items.Add(model);
        }

        _modelChoice.DisplayMember = nameof(ModelCatalogEntry.Tag);
        if (_recommendation.Model is not null)
        {
            _modelChoice.SelectedItem = _modelChoice.Items.Cast<ModelCatalogEntry>()
                .FirstOrDefault(model => string.Equals(model.Tag, _recommendation.Model.Tag, StringComparison.OrdinalIgnoreCase));
        }
        _modelDirectory.Text = GetExistingOllamaModelsPath() ?? _storage?.ModelDirectory ?? string.Empty;

        Text = "Codex GPU Thalen Helper Setup";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(860, 700);
        Font = new Font("Segoe UI", 10F);
        var footer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 62,
            Padding = new Padding(16)
        };
        footer.Controls.Add(_next);
        footer.Controls.Add(_back);
        footer.Controls.Add(_cancel);
        Controls.Add(_pageHost);
        Controls.Add(footer);
        Controls.Add(_step);
        _back.Click += (_, _) => { _page--; RenderPage(); };
        _next.Click += async (_, _) => await AdvanceAsync();
        _cancel.Click += (_, _) =>
        {
            if (_installing)
            {
                _installationCancellation.Cancel();
                _cancel.Enabled = false;
                _result.Text = "Cancelling safely. Partial model downloads remain under Ollama's control and the Codex integration stays disabled.";
            }
            else
            {
                Close();
            }
        };
        _installReliabilityBaseline.CheckedChanged += (_, _) => RefreshReliabilityPreview();
        _modelChoice.SelectedIndexChanged += (_, _) => RefreshReliabilityPreview();
        FormClosing += (_, eventArgs) =>
        {
            if (_installing)
            {
                _installationCancellation.Cancel();
                eventArgs.Cancel = true;
            }
        };
        RenderPage();
    }

    private void RenderPage()
    {
        _pageHost.Controls.Clear();
        _step.Text = $"Step {_page + 1} of 5";
        _back.Enabled = _page > 0 && _page < 4;
        _next.Text = _page switch { 3 => "Install and validate", 4 => "Close", _ => "Next" };
        Control page = _page switch
        {
            0 => WelcomePage(),
            1 => HardwarePage(),
            2 => SelectionPage(),
            3 => PrivacyPage(),
            _ => ResultPage()
        };
        page.Dock = DockStyle.Fill;
        _pageHost.Controls.Add(page);
    }

    private async Task AdvanceAsync()
    {
        if (_page == 4)
        {
            Close();
            return;
        }

        if (_page < 3)
        {
            _page++;
            RenderPage();
            return;
        }

        _next.Enabled = false;
        _back.Enabled = false;
        _installing = true;
        _page = 4;
        _result.Text = "Preparing isolated per-user configuration…";
        RenderPage();
        try
        {
            var selectedModel = _modelChoice.SelectedItem as ModelCatalogEntry ?? _recommendation.Model;
            if (_pullAndValidate.Checked && selectedModel is not null)
            {
                _result.Text = $"local_gpu_reviewer • Ollama • {selectedModel.Tag} • exact-response and bounded code-review validation";
            }

            var outcome = await new InstallationManager().ConfigureAsync(new InstallationOptions(
                _paths,
                selectedModel?.Tag,
                string.IsNullOrWhiteSpace(_modelDirectory.Text) ? null : _modelDirectory.Text,
                false,
                false,
                _autoStart.Checked,
                _pullAndValidate.Checked,
                InstallReliabilityBaseline: _installReliabilityBaseline.Checked,
                ExpectedAgentsSourceSha256: _agentsPreview?.SourceSha256,
                ExpectedAgentsPlannedSha256: _agentsPreview?.PlannedSha256,
                EnsureOllamaInstalledAsync: async cancellationToken =>
                {
                    if (!_installOllama.Checked || await IsOllamaReachableAsync())
                    {
                        return;
                    }

                    _result.Text = "Downloading and verifying the current official Ollama installer…";
                    using var installer = new OllamaInstallerService();
                    var install = await installer.DownloadVerifyAndLaunchAsync(waitForExit: true, cancellationToken);
                    if (!install.Success)
                    {
                        throw new InvalidOperationException(install.Message);
                    }
                }),
                _installationCancellation.Token);
            _result.Text = $"{outcome.Message}\n\nState: {outcome.State.Availability}\nModel: {outcome.State.SelectedModel ?? "none"}\nStorage: {outcome.State.ModelStorageLocation ?? "not selected"}\n\nRestart Codex so it loads the managed MCP integration. The executable is unsigned; Windows may show a SmartScreen warning.";
        }
        catch (OperationCanceledException)
        {
            _result.Text = "Setup was cancelled safely. The managed Codex entry remains disabled. Run setup or Repair when you are ready to continue.";
        }
        catch (Exception exception)
        {
            _result.Text = "Setup did not complete. Invalid managed configuration was rolled back or left disabled.\n\n" + exception.Message;
        }
        finally
        {
            _next.Enabled = true;
            _cancel.Enabled = true;
            _installing = false;
        }
    }

    private Control WelcomePage() => Page(
        "Welcome",
        "Use a hardware-appropriate local Ollama model as an optional, bounded, read-only GPU reviewer for Codex on Windows.",
        "This independent community project is not made, endorsed, or supported by OpenAI or Ollama. It does not replace Codex, bypass limits, guarantee lower costs, or require an OpenAI API key. Codex authentication remains completely separate.",
        "The helper has zero telemetry. The MCP server accepts only text that Codex explicitly supplies and exposes no filesystem, shell, Git, deployment, email, or mutation tools.");

    private Control HardwarePage()
    {
        var gpu = _hardware.Gpus.OrderByDescending(item => item.DedicatedMemoryBytes).FirstOrDefault();
        return Page(
            "Hardware detection",
            $"Windows: {_hardware.OperatingSystem.ProductName} ({_hardware.OperatingSystem.Architecture})",
            $"CPU: {_hardware.Cpu.Model} • {_hardware.Cpu.PhysicalCores} physical / {_hardware.Cpu.LogicalCores} logical cores",
            $"RAM: {FormatGiB(_hardware.Memory.TotalBytes)} total • {FormatGiB(_hardware.Memory.AvailableBytes)} currently available",
            gpu is null
                ? "GPU: no supported dedicated GPU detected"
                : $"GPU: {gpu.Name} • {FormatGiB(gpu.DedicatedMemoryBytes)} dedicated VRAM • {gpu.AccelerationRoute}",
            _hardware.OperatingSystem.Warning ?? "Windows x64 support check passed.");
    }

    private Control SelectionPage()
    {
        var panel = (FlowLayoutPanel)Page(
            "Conservative model and storage selection",
            _recommendation.Model is null
                ? "No model is recommended. Setup will continue in disabled/no-model mode."
                : $"Recommended model: {_recommendation.Model.Tag} ({_recommendation.Explanation})",
            _storage?.Explanation ?? "No storage directory is required in no-model mode.",
            _storage?.Volume is null
                ? ""
                : $"Required with reserve: {FormatGiB(_storage.RequiredBytes)} • remaining after download: {FormatGiB(_storage.RemainingBytes)}");
        panel.Controls.Add(new Label { Text = "Model storage directory", AutoSize = true, Margin = new Padding(0, 18, 0, 4) });
        panel.Controls.Add(new Label { Text = "Safe model choices for this hardware", AutoSize = true, Margin = new Padding(0, 8, 0, 4) });
        panel.Controls.Add(_modelChoice);
        panel.Controls.Add(_modelDirectory);
        panel.Controls.Add(new Label { Text = "Removable and network locations are rejected. Existing Ollama installations keep their current model directory when one is already configured.", AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Color.DimGray });
        return panel;
    }

    private Control PrivacyPage()
    {
        var panel = (FlowLayoutPanel)Page(
            "Installation choices",
            "The local GPU guidance section is installed automatically unless equivalent unmarked local_gpu_reviewer guidance already exists. The broader reliability baseline below is optional and unchecked by default.",
            "Existing config.toml and AGENTS.override.md files are never replaced. Changes use distinct managed markers, timestamped backups, atomic writes, idempotent upgrades, and surgical rollback.",
            "Ollama is kept loopback-only at 127.0.0.1:11434. The selected model is unloaded after requests by default. Automatic startup uses a single per-user helper entry that checks for an existing endpoint/process before starting anything.");
        panel.Controls.Add(_autoStart);
        panel.Controls.Add(_installOllama);
        panel.Controls.Add(_pullAndValidate);
        panel.Controls.Add(_installReliabilityBaseline);
        panel.Controls.Add(new Label
        {
            Text = "Before/after diff preview for AGENTS.override.md (review this before selecting Install and validate):",
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            Margin = new Padding(0, 12, 0, 4)
        });
        panel.Controls.Add(_reliabilityPreview);
        panel.Controls.Add(new Label { Text = "If automatic startup is declined, local review requires manually starting Ollama after each sign-in. Model validation runs local inference only when selected above.", AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Color.DarkSlateBlue, Margin = new Padding(0, 12, 0, 0) });
        RefreshReliabilityPreview();
        return panel;
    }

    private void RefreshReliabilityPreview()
    {
        try
        {
            var selectedModel = _modelChoice.SelectedItem as ModelCatalogEntry ?? _recommendation.Model;
            var tier = selectedModel is null ? HardwareTier.NoModel : ModelSelector.GetHardwareTier(selectedModel);
            _agentsPreview = new AgentsOverrideManager().PreviewInstall(
                _paths,
                tier,
                _installReliabilityBaseline.Checked);
            _reliabilityPreview.Text = _agentsPreview.Diff;
            if (_page == 3 && !_installing)
            {
                _next.Enabled = true;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _agentsPreview = null;
            _reliabilityPreview.Text = "Preview unavailable. Setup will not change a malformed managed instruction file.\r\n\r\n" + exception.Message;
            if (_page == 3 && !_installing)
            {
                _next.Enabled = false;
            }
        }
    }

    private Control ResultPage()
    {
        var panel = (FlowLayoutPanel)Page("Setup result");
        panel.Controls.Add(_result);
        return panel;
    }

    private static Control Page(string title, params string[] lines)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        panel.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI Semibold", 20F), AutoSize = true, ForeColor = Color.FromArgb(27, 42, 65), Margin = new Padding(0, 0, 0, 14) });
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            panel.Controls.Add(new Label { Text = line, AutoSize = true, MaximumSize = new Size(720, 0), Margin = new Padding(0, 4, 0, 8) });
        }

        return panel;
    }

    private static async Task<bool> IsOllamaReachableAsync()
    {
        try
        {
            using var client = new OllamaClient();
            _ = await client.GetModelsAsync();
            return true;
        }
        catch (OllamaException)
        {
            return false;
        }
    }

    private static string? GetExistingOllamaModelsPath()
    {
        var configured = Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
        return Directory.Exists(standard) ? standard : null;
    }

    private static string FormatGiB(ulong bytes) => $"{bytes / 1024d / 1024d / 1024d:F1} GiB";
}
