using System.Diagnostics;
using System.Text.Json;
using ThalenHelper.Core;

namespace ThalenHelper.ControlCenter;

public sealed class MainForm : Form
{
    private readonly ProductPaths _paths = ProductPaths.Resolve(installDirectory: AppContext.BaseDirectory);
    private readonly Label _stateValue = ValueLabel();
    private readonly Label _modelValue = ValueLabel();
    private readonly Label _sizeValue = ValueLabel();
    private readonly Label _tierValue = ValueLabel();
    private readonly Label _gpuValue = ValueLabel();
    private readonly Label _vramValue = ValueLabel();
    private readonly Label _acceleratorValue = ValueLabel();
    private readonly Label _loadedValue = ValueLabel();
    private readonly Label _storageValue = ValueLabel();
    private readonly Label _freeValue = ValueLabel();
    private readonly Label _healthValue = ValueLabel();
    private readonly Label _notice = new() { AutoSize = true, ForeColor = Color.DarkSlateBlue, Padding = new Padding(8) };

    public MainForm()
    {
        Text = ProductInfo.Name;
        MinimumSize = new Size(920, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.FromArgb(247, 249, 252);

        var title = new Label
        {
            Text = "Codex GPU Thalen Helper",
            Font = new Font("Segoe UI Semibold", 22F),
            AutoSize = true,
            ForeColor = Color.FromArgb(27, 42, 65),
            Margin = new Padding(16, 16, 16, 2)
        };
        var subtitle = new Label
        {
            Text = "Optional bounded local Ollama reviewer • independent community project",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(18, 0, 16, 16)
        };

        var status = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 11,
            Padding = new Padding(18),
            Margin = new Padding(16),
            BackColor = Color.White,
            Dock = DockStyle.Top
        };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(status, 0, "State", _stateValue);
        AddRow(status, 1, "Selected model", _modelValue);
        AddRow(status, 2, "Expected model size", _sizeValue);
        AddRow(status, 3, "Hardware tier", _tierValue);
        AddRow(status, 4, "GPU", _gpuValue);
        AddRow(status, 5, "Dedicated VRAM", _vramValue);
        AddRow(status, 6, "Accelerator", _acceleratorValue);
        AddRow(status, 7, "Model loaded", _loadedValue);
        AddRow(status, 8, "Model storage", _storageValue);
        AddRow(status, 9, "Free disk space", _freeValue);
        AddRow(status, 10, "Last health check", _healthValue);

        var controls = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(16),
            WrapContents = true
        };
        AddButton(controls, "Pause", async () => await Control().PauseAsync());
        AddButton(controls, "Resume", async () => await Control().ResumeAsync());
        AddButton(controls, "Release GPU", async () => await Control().ReleaseGpuAsync());
        AddButton(controls, "Enable", async () => await Control().EnableAsync());
        AddButton(controls, "Disable", async () => await Control().DisableAsync(true));
        AddButton(controls, "Low impact", async () => await ToggleLowImpactAsync());
        AddButton(controls, "Keep warm", async () => await ToggleKeepWarmAsync());
        AddButton(controls, "Local review test", TestLocalReviewAsync);
        AddButton(controls, "Change model", ChangeModelAsync);
        AddButton(controls, "Move models", MoveModelsAsync);
        AddButton(controls, "Repair integration", RepairAsync);
        AddButton(controls, "Reliability baseline", ConfigureReliabilityBaselineAsync);
        AddButton(controls, "Export diagnostics", ExportDiagnosticsAsync);
        AddButton(controls, "Uninstall guidance", ShowUninstallGuidanceAsync);

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        layout.Controls.Add(title);
        layout.Controls.Add(subtitle);
        layout.Controls.Add(status);
        layout.Controls.Add(controls);
        layout.Controls.Add(_notice);
        Controls.Add(layout);
        Shown += OnShownAsync;
    }

    private async void OnShownAsync(object? sender, EventArgs eventArgs)
    {
        if (!File.Exists(_paths.StateFile))
        {
            using var wizard = new SetupWizardForm(_paths);
            _ = wizard.ShowDialog(this);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private ControlService Control()
    {
        var store = new StateStore(_paths.StateFile);
        return new ControlService(_paths, store);
    }

    private async Task RefreshAsync()
    {
        try
        {
            var hardware = new HardwareDetector().Detect();
            var store = new StateStore(_paths.StateFile);
            var state = await store.LoadAsync().ConfigureAwait(true);
            using var client = new OllamaClient();
            var health = await new ReviewerService(store, client).GetHealthAsync().ConfigureAwait(true);
            var catalog = new ModelCatalogService().LoadBundled();
            var model = catalog.Models.FirstOrDefault(item => string.Equals(item.Tag, state?.SelectedModel, StringComparison.OrdinalIgnoreCase));
            var gpu = hardware.Gpus.OrderByDescending(item => item.DedicatedMemoryBytes).FirstOrDefault();
            var volume = state?.ModelStorageLocation is null
                ? null
                : hardware.Volumes.FirstOrDefault(item => string.Equals(
                    item.RootPath,
                    Path.GetPathRoot(state.ModelStorageLocation),
                    StringComparison.OrdinalIgnoreCase));

            _stateValue.Text = state?.Availability.ToString() ?? "Not configured";
            _stateValue.ForeColor = state?.Availability == HelperAvailability.Enabled ? Color.DarkGreen : Color.DarkOrange;
            _modelValue.Text = state?.SelectedModel ?? "None";
            _sizeValue.Text = model is null ? "—" : FormatBytes(model.ExpectedDownloadBytes);
            _tierValue.Text = state?.HardwareTier.ToString() ?? "—";
            _gpuValue.Text = gpu?.Name ?? "No supported dedicated GPU";
            _vramValue.Text = gpu is null ? "—" : FormatBytes(gpu.DedicatedMemoryBytes);
            _acceleratorValue.Text = state?.Acceleration?.Processor ?? gpu?.AccelerationRoute.ToString() ?? "Unknown";
            _loadedValue.Text = health.ModelLoaded ? "Yes" : "No";
            _storageValue.Text = state?.ModelStorageLocation ?? "—";
            _freeValue.Text = volume is null ? "—" : FormatBytes(volume.FreeBytes);
            _healthValue.Text = health.ErrorCode is null ? "OK (passive)" : $"{health.ErrorCode}: {health.ErrorMessage}";
            _notice.Text = state?.Preferences.AutoStartOllama == false
                ? "Automatic Ollama startup is off. Start Ollama manually after signing in before local review."
                : "Low-impact operation is recommended while other GPU workloads are active.";
        }
        catch (Exception exception)
        {
            _healthValue.Text = "Unavailable";
            _notice.Text = exception.Message;
        }
    }

    private async Task ToggleLowImpactAsync()
    {
        var state = await new StateStore(_paths.StateFile).LoadAsync();
        await RunControlAsync(() => Control().SetLowImpactAsync(!(state?.Preferences.LowImpactMode ?? false)));
    }

    private async Task ToggleKeepWarmAsync()
    {
        var state = await new StateStore(_paths.StateFile).LoadAsync();
        await RunControlAsync(() => Control().SetKeepWarmAsync(!(state?.Preferences.KeepWarm ?? false)));
    }

    private async Task TestLocalReviewAsync()
    {
        var state = await new StateStore(_paths.StateFile).LoadAsync();
        if (state is null || string.IsNullOrWhiteSpace(state.SelectedModel))
        {
            MessageBox.Show(this, "No model is configured.", ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(
            this,
            $"This will run a small bounded Ollama inference using {state.SelectedModel} and then unload it. Continue?",
            "Run local review test",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _notice.Text = $"local_gpu_reviewer • Ollama • {state.SelectedModel} • bounded exact-response and code-review validation";
        var validation = await new InstallationManager().ValidateSelectedModelAsync(state);
        MessageBox.Show(this, validation.Message, validation.Code, MessageBoxButtons.OK,
            validation.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        await RefreshAsync();
    }

    private async Task ChangeModelAsync()
    {
        var catalog = new ModelCatalogService().LoadBundled();
        var recommendation = new ModelSelector().Recommend(new HardwareDetector().Detect(), catalog, false);
        var safeModels = catalog.Models
            .Where(model => recommendation.Model is not null
                && model.ParameterBillions <= recommendation.Model.ParameterBillions)
            .ToArray();
        using var dialog = new ModelSelectionDialog(safeModels);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedModel is null)
        {
            return;
        }

        if (MessageBox.Show(this, "The selected model will be downloaded if needed and run through bounded validation. Continue?", "Change model", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        var store = new StateStore(_paths.StateFile);
        var result = await new ModelChangeService(store, Control(), new InstallationManager())
            .ChangeAsync(dialog.SelectedModel.Tag, dialog.AcceptRestrictedLicense);
        MessageBox.Show(this, result.Message, result.Code, MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        await RefreshAsync();
    }

    private async Task MoveModelsAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose an empty directory on fixed local storage. The move uses SHA-256 verification and rolls back on failure.",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (MessageBox.Show(this, "Move and verify all Ollama model files now?", "Move models", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        var store = new StateStore(_paths.StateFile);
        var result = await new ModelsMoveService(_paths, store, Control()).MoveAsync(dialog.SelectedPath);
        MessageBox.Show(this, result.Message, result.Code, MessageBoxButtons.OK, MessageBoxIcon.Information);
        await RefreshAsync();
    }

    private async Task RepairAsync()
    {
        var result = await new InstallationManager().RepairAsync(_paths);
        MessageBox.Show(this, result.Message, result.Code, MessageBoxButtons.OK, MessageBoxIcon.Information);
        await RefreshAsync();
    }

    private async Task ConfigureReliabilityBaselineAsync()
    {
        var state = await new StateStore(_paths.StateFile).LoadAsync()
            ?? throw new InvalidOperationException("The helper is not configured.");
        var managedPaths = string.IsNullOrWhiteSpace(state.ManagedCodexHome)
            ? _paths
            : ProductPaths.Resolve(_paths.InstallDirectory, _paths.StateDirectory, state.ManagedCodexHome);
        using var dialog = new ReliabilityBaselineDialog(
            managedPaths,
            state.HardwareTier,
            state.ReliabilityBaselineInstalled);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Preview is null)
        {
            return;
        }

        var result = await new InstallationManager().ConfigureReliabilityBaselineAsync(
            managedPaths,
            dialog.InstallBaseline,
            dialog.Preview.SourceSha256,
            dialog.Preview.PlannedSha256);
        MessageBox.Show(
            this,
            result.Changed
                ? "The reviewed managed instruction diff was applied. A timestamped backup was retained for rollback."
                : "AGENTS.override.md already matches the reviewed choice; no write or backup was needed.",
            ProductInfo.Name,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        await RefreshAsync();
    }

    private async Task ExportDiagnosticsAsync()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "JSON report (*.json)|*.json",
            FileName = "codex-gpu-thalen-helper-diagnostics.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var hardware = new HardwareDetector().Detect();
        var store = new StateStore(_paths.StateFile);
        var state = await store.LoadAsync();
        using var client = new OllamaClient();
        var health = await new ReviewerService(store, client).GetHealthAsync();
        await new DiagnosticsExporter().ExportAsync(dialog.FileName, _paths, hardware, state, health);
        MessageBox.Show(this, "Redacted diagnostics exported. No prompts, responses, usernames, hostnames, serials, or credentials were included.", ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private Task ShowUninstallGuidanceAsync()
    {
        MessageBox.Show(
            this,
            "Use Windows Settings > Apps > Installed apps > Codex GPU Thalen Helper > Uninstall. The uninstaller removes only managed Codex sections and asks separately before deleting a helper-owned model. Ollama and pre-existing models are preserved.",
            "Uninstall guidance",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return Task.CompletedTask;
    }

    private async Task RunControlAsync(Func<Task<ControlResult>> action)
    {
        try
        {
            var result = await action();
            _notice.Text = result.Message;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        await RefreshAsync();
    }

    private void AddButton(FlowLayoutPanel panel, string text, Func<Task> action)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(5),
            FlatStyle = FlatStyle.System
        };
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                button.Enabled = true;
            }
        };
        panel.Controls.Add(button);
    }

    private static void AddRow(TableLayoutPanel table, int row, string label, Label value)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 4, 0, 4) }, 0, row);
        table.Controls.Add(value, 1, row);
    }

    private static Label ValueLabel() => new() { AutoSize = true, ForeColor = Color.FromArgb(27, 42, 65), Padding = new Padding(0, 4, 0, 4), MaximumSize = new Size(620, 0) };

    private static string FormatBytes(ulong bytes)
    {
        var value = (double)bytes;
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:F1} {units[unit]}";
    }
}

internal sealed class ModelSelectionDialog : Form
{
    private readonly ComboBox _models = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
    private readonly CheckBox _license = new() { Text = "I accept the model's separate restrictive license when required.", AutoSize = true };

    public ModelSelectionDialog(IReadOnlyList<ModelCatalogEntry> models)
    {
        Text = "Change local reviewer model";
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);
        Font = new Font("Segoe UI", 10F);
        foreach (var model in models)
        {
            _models.Items.Add(model);
        }

        _models.DisplayMember = nameof(ModelCatalogEntry.Tag);
        if (_models.Items.Count > 0)
        {
            _models.SelectedIndex = 0;
        }

        var ok = new Button { Text = "Continue", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        panel.Controls.Add(new Label { Text = "Choose a model within the current conservative hardware limit:", AutoSize = true });
        panel.Controls.Add(_models);
        panel.Controls.Add(_license);
        panel.Controls.Add(new FlowLayoutPanel { AutoSize = true, Controls = { ok, cancel } });
        Controls.Add(panel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public ModelCatalogEntry? SelectedModel => _models.SelectedItem as ModelCatalogEntry;
    public bool AcceptRestrictedLicense => _license.Checked;
}

internal sealed class ReliabilityBaselineDialog : Form
{
    private readonly ProductPaths _paths;
    private readonly HardwareTier _tier;
    private readonly CheckBox _install;
    private readonly TextBox _diff;
    private readonly Button _apply;

    public ReliabilityBaselineDialog(ProductPaths paths, HardwareTier tier, bool installed)
    {
        _paths = paths;
        _tier = tier;
        Text = "Optional Codex reliability baseline";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 650);
        Padding = new Padding(18);
        Font = new Font("Segoe UI", 10F);
        _install = new CheckBox
        {
            Text = "Install the sanitized managed reliability baseline",
            Checked = installed,
            AutoSize = true
        };
        _diff = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F)
        };
        _apply = new Button { Text = "Apply reviewed diff", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var header = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Top,
            AutoSize = true
        };
        header.Controls.Add(new Label
        {
            Text = "Review the complete before/after diff. Existing instructions are never replaced; only marked helper sections can change.",
            AutoSize = true,
            MaximumSize = new Size(820, 0)
        });
        header.Controls.Add(_install);
        var footer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Controls = { _apply, cancel }
        };
        Controls.Add(_diff);
        Controls.Add(footer);
        Controls.Add(header);
        AcceptButton = _apply;
        CancelButton = cancel;
        _install.CheckedChanged += (_, _) => RefreshPreview();
        RefreshPreview();
    }

    public bool InstallBaseline => _install.Checked;
    public AgentsOverridePreview? Preview { get; private set; }

    private void RefreshPreview()
    {
        try
        {
            Preview = new AgentsOverrideManager().PreviewInstall(_paths, _tier, _install.Checked);
            _diff.Text = Preview.Diff;
            _apply.Enabled = true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Preview = null;
            _diff.Text = "Preview unavailable. No change can be applied.\r\n\r\n" + exception.Message;
            _apply.Enabled = false;
        }
    }
}
