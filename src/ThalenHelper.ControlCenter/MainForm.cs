using ThalenHelper.Core;

namespace ThalenHelper.ControlCenter;

public sealed class MainForm : Form
{
    private readonly ProductPaths _paths = ProductPaths.Resolve(installDirectory: AppContext.BaseDirectory);
    private readonly DarkToolTip _toolTip = UiTheme.ToolTip();
    private readonly Label _stateBadge = UiTheme.Label("CHECKING", 8.5F, UiTheme.Muted, FontStyle.Bold);
    private readonly Label _heroTitle = UiTheme.Label("Checking your local reviewer", 17F, UiTheme.Text, FontStyle.Bold);
    private readonly Label _heroMessage = UiTheme.Label("Reading passive local status. No model will be loaded.", 9.5F, UiTheme.Muted);
    private readonly Label _modelValue = MetricValue();
    private readonly Label _modelMeta = MetricMeta();
    private readonly Label _gpuValue = MetricValue();
    private readonly Label _gpuMeta = MetricMeta();
    private readonly Label _ollamaValue = MetricValue();
    private readonly Label _ollamaMeta = MetricMeta();
    private readonly Label _storageValue = DetailValue();
    private readonly Label _loadedValue = DetailValue();
    private readonly Label _hardwareValue = DetailValue();
    private readonly Label _technicalValue = DetailValue();
    private readonly Label _notice = UiTheme.Label("Low-impact mode protects other GPU workloads.", 9F, UiTheme.Muted);
    private readonly List<Button> _actionButtons = [];
    private readonly List<Button> _managedActionButtons = [];
    private Button _primaryAction = null!;
    private Button _lowImpactButton = null!;
    private Button _keepWarmButton = null!;
    private Func<Task>? _primaryActionCommand;
    private int _operationInProgress;
    private bool _managedActionsAllowed;

    public MainForm()
    {
        Text = ProductInfo.Name;
        Size = new Size(1080, 790);
        UiTheme.Apply(this, new Size(980, 700));

        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = UiTheme.Canvas
        };
        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 0,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(24, 22, 24, 26),
            BackColor = UiTheme.Canvas
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        AddStackRow(stack, BuildHero());
        AddStackRow(stack, BuildMetrics());
        AddStackRow(stack, BuildDetails());
        AddStackRow(stack, BuildActions());
        AddStackRow(stack, BuildNotice());

        scroll.Controls.Add(stack);
        Controls.Add(scroll);
        Shown += OnShownAsync;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private Control BuildHero()
    {
        var hero = new GradientPanel
        {
            Dock = DockStyle.Top,
            Height = 164,
            Margin = new Padding(0, 0, 0, 16),
            Padding = new Padding(26, 22, 26, 22),
            AccessibleName = "Local reviewer overview"
        };
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F));

        var copy = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent
        };
        var eyebrow = UiTheme.Label("PRIVATE AI REVIEW", 8.5F, UiTheme.Cyan, FontStyle.Bold);
        eyebrow.Margin = new Padding(0, 0, 0, 9);
        var product = UiTheme.Label("Codex GPU Thalen Helper", 22F, UiTheme.Text, FontStyle.Bold);
        product.Margin = new Padding(0, 0, 0, 4);
        var subtitle = UiTheme.Label("A quiet, local second opinion—only when you ask for it.", 10F, UiTheme.Muted);
        subtitle.Margin = new Padding(0, 0, 0, 14);
        var stateLine = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        StyleBadge(_stateBadge, UiTheme.Muted);
        _heroTitle.Margin = new Padding(10, 3, 0, 0);
        stateLine.Controls.Add(_stateBadge);
        stateLine.Controls.Add(_heroTitle);
        copy.Controls.Add(eyebrow);
        copy.Controls.Add(product);
        copy.Controls.Add(subtitle);
        copy.Controls.Add(stateLine);
        _heroMessage.Margin = new Padding(0, 5, 0, 0);
        copy.Controls.Add(_heroMessage);

        var actionHost = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(8, 42, 0, 0)
        };
        _primaryAction = UiTheme.Button("Checking…", AppButtonStyle.Primary);
        _primaryAction.MinimumSize = new Size(210, 46);
        _primaryAction.Enabled = false;
        _primaryAction.Click += async (_, _) =>
        {
            if (_primaryActionCommand is not null)
            {
                await RunButtonActionAsync(_primaryActionCommand);
            }
        };
        SetHelp(_primaryAction, "Runs the safest primary action for the current state. It never downloads or preloads a model without a separate confirmation.");
        var passive = UiTheme.Label("Passive status • zero telemetry", 8.5F, UiTheme.Muted);
        passive.Margin = new Padding(4, 2, 0, 0);
        actionHost.Controls.Add(_primaryAction);
        actionHost.Controls.Add(passive);

        grid.Controls.Add(copy, 0, 0);
        grid.Controls.Add(actionHost, 1, 0);
        hero.Controls.Add(grid);
        return hero;
    }

    private Control BuildMetrics()
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            Dock = DockStyle.Top,
            Height = 126,
            Margin = new Padding(0, 0, 0, 16),
            BackColor = UiTheme.Canvas
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
        grid.Controls.Add(MetricCard("MODEL", _modelValue, _modelMeta), 0, 0);
        grid.Controls.Add(MetricCard("GPU", _gpuValue, _gpuMeta), 1, 0);
        grid.Controls.Add(MetricCard("OLLAMA", _ollamaValue, _ollamaMeta), 2, 0);
        return grid;
    }

    private static Control MetricCard(string title, Label value, Label meta)
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 12, 0),
            Padding = new Padding(18, 15, 18, 14)
        };
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent
        };
        flow.Controls.Add(UiTheme.SectionLabel(title));
        value.Margin = new Padding(0, 0, 0, 5);
        meta.Margin = new Padding(0);
        flow.Controls.Add(value);
        flow.Controls.Add(meta);
        card.Controls.Add(flow);
        return card;
    }

    private Control BuildDetails()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Top,
            Height = 184,
            Margin = new Padding(0, 0, 0, 16),
            Padding = new Padding(20, 16, 20, 16)
        };
        var title = UiTheme.SectionLabel("SYSTEM DETAILS");
        title.Dock = DockStyle.Top;
        var table = new TableLayoutPanel
        {
            ColumnCount = 4,
            RowCount = 2,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 10, 0, 0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        AddDetail(table, 0, 0, "Model storage", _storageValue);
        AddDetail(table, 2, 0, "Model loaded", _loadedValue);
        AddDetail(table, 0, 1, "Hardware", _hardwareValue);
        AddDetail(table, 2, 1, "Technical status", _technicalValue);
        card.Controls.Add(table);
        card.Controls.Add(title);
        return card;
    }

    private Control BuildActions()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Top,
            Height = 292,
            Margin = new Padding(0, 0, 0, 16),
            Padding = new Padding(20, 16, 20, 14)
        };
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

        var left = ActionColumn();
        left.Controls.Add(UiTheme.SectionLabel("REVIEW CONTROLS"));
        var review = ActionFlow();
        AddActionButton(review, "Pause reviews", "Temporarily rejects new helper-owned reviews, requests cancellation of an active review, and unloads the selected model. The Codex MCP entry remains configured.", async () => await Control().PauseAsync(), managedOnly: true);
        AddActionButton(review, "Resume reviews", "Verifies Ollama, loopback networking, model storage, and model integrity before allowing reviews again. It does not preload the model.", async () => await Control().ResumeAsync(), managedOnly: true);
        AddActionButton(review, "Release GPU", "Unloads the selected model without disabling future reviews. Use this before an emulator, graphics build, or other GPU-heavy work.", async () => await Control().ReleaseGpuAsync(), managedOnly: true);
        AddActionButton(review, "Enable integration", "Persistently enables the helper-owned Codex MCP entry after safety checks. Restart Codex only if the tools are not already visible.", async () => await Control().EnableAsync(), managedOnly: true);
        AddActionButton(review, "Disable integration", "Persistently disables the helper-owned MCP entry, cancels helper work, and unloads its model. Restart Codex to remove the tools from the current session.", async () => await Control().DisableAsync(true), AppButtonStyle.Danger, managedOnly: true);
        left.Controls.Add(review);
        left.Controls.Add(UiTheme.SectionLabel("GPU BEHAVIOR"));
        var preferences = ActionFlow();
        _lowImpactButton = AddActionButton(preferences, "Low impact", "Keeps GPU impact minimal and unloads the model immediately after every response. Recommended while emulators, Expo, or graphics tools are active.", ToggleLowImpactAsync, managedOnly: true);
        _keepWarmButton = AddActionButton(preferences, "Keep warm", "Keeps the model in GPU memory for a bounded idle period for faster follow-up reviews. Entry-tier hardware blocks this option.", ToggleKeepWarmAsync, managedOnly: true);
        left.Controls.Add(preferences);

        var right = ActionColumn();
        right.Padding = new Padding(18, 0, 0, 0);
        right.Controls.Add(UiTheme.SectionLabel("MODEL & SETUP"));
        var setup = ActionFlow();
        AddActionButton(setup, "Test local review", "Runs one small, explicitly confirmed local inference and then unloads the model. This is the only button here that intentionally runs a model.", TestLocalReviewAsync, AppButtonStyle.Primary, managedOnly: true);
        AddActionButton(setup, "Choose model", "Shows hardware-safe supported models. After confirmation, it downloads only when missing, validates locally, and records ownership safely.", ChangeModelAsync, managedOnly: true);
        AddActionButton(setup, "Move model storage", "Moves helper-managed Ollama model files to an empty fixed local folder, verifies every file with SHA-256, and rolls back on failure.", MoveModelsAsync, managedOnly: true);
        AddActionButton(setup, "Repair integration", "Rechecks the managed configuration, startup, model path, and passive health. Existing unowned integrations remain untouched.", RepairAsync, managedOnly: true);
        right.Controls.Add(setup);
        right.Controls.Add(UiTheme.SectionLabel("PRIVACY & SUPPORT"));
        var support = ActionFlow();
        AddActionButton(support, "Reliability baseline", "Previews the complete before/after AGENTS.override.md diff before optionally adding or removing only the marked sanitized reliability section.", ConfigureReliabilityBaselineAsync, AppButtonStyle.Quiet);
        AddActionButton(support, "Export diagnostics", "Saves a redacted JSON report without prompts, responses, usernames, hostnames, serial numbers, model contents, or credentials.", ExportDiagnosticsAsync, AppButtonStyle.Quiet);
        AddActionButton(support, "Uninstall help", "Explains how to remove only helper-owned files and settings while preserving Ollama, pre-existing models, Codex authentication, and unrelated configuration.", ShowUninstallGuidanceAsync, AppButtonStyle.Quiet);
        right.Controls.Add(support);

        grid.Controls.Add(left, 0, 0);
        grid.Controls.Add(right, 1, 0);
        card.Controls.Add(grid);
        return card;
    }

    private Control BuildNotice()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Top,
            Height = 66,
            Margin = new Padding(0),
            Padding = new Padding(18, 14, 18, 12),
            OutlineColor = Color.FromArgb(52, 61, 84)
        };
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent
        };
        var mark = UiTheme.Label("●", 10F, UiTheme.Cyan, FontStyle.Bold);
        mark.Margin = new Padding(0, 2, 10, 0);
        _notice.MaximumSize = new Size(900, 0);
        _notice.Margin = new Padding(0, 1, 0, 0);
        row.Controls.Add(mark);
        row.Controls.Add(_notice);
        card.Controls.Add(row);
        return card;
    }

    private async void OnShownAsync(object? sender, EventArgs eventArgs)
    {
        var state = File.Exists(_paths.StateFile)
            ? await new StateStore(_paths.StateFile).LoadAsync().ConfigureAwait(true)
            : null;
        if (state is null
            || string.Equals(
                state.LastHealthCheckCode,
                "INSTALLED_MODEL_SETUP_REQUIRED",
                StringComparison.Ordinal))
        {
            using var wizard = new SetupWizardForm(
                _paths,
                state?.Preferences.AutoStartOllama,
                startAtModelSelection: state is not null);
            _ = wizard.ShowDialog(this);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private ControlService Control()
        => new(_paths, new StateStore(_paths.StateFile));

    private async Task RefreshAsync()
    {
        try
        {
            var hardware = new HardwareDetector().Detect();
            var store = new StateStore(_paths.StateFile);
            var state = await store.LoadAsync().ConfigureAwait(true);
            using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"));
            var health = await new ReviewerService(_paths, store, client).GetHealthAsync().ConfigureAwait(true);
            var catalog = new ModelCatalogService().LoadBundled();
            var model = catalog.Models.FirstOrDefault(item => string.Equals(item.Tag, state?.SelectedModel, StringComparison.OrdinalIgnoreCase));
            var gpu = hardware.Gpus.OrderByDescending(item => item.DedicatedMemoryBytes).FirstOrDefault();
            var managed = IntegrationOwnership.Inspect(_paths, state).Status == IntegrationOwnershipStatus.ManagedValid;
            _managedActionsAllowed = managed;
            var listener = OllamaAutoStartManager.GetListenerStatus(11434);
            var externalExposure = !managed && listener.HasListeners && !listener.LoopbackOnly;

            UpdateHero(state, health, managed, listener);
            _modelValue.Text = state?.SelectedModel ?? (managed ? "Not selected" : "Existing setup");
            _modelMeta.Text = model is null
                ? managed ? "Choose a supported model to begin" : "Preserved without takeover"
                : $"{FormatBytes(model.ExpectedDownloadBytes)} expected download";
            _gpuValue.Text = gpu?.Name ?? "No dedicated GPU";
            _gpuMeta.Text = gpu is null
                ? "CPU fallback is never automatic"
                : $"{FormatBytes(gpu.DedicatedMemoryBytes)} VRAM • {gpu.AccelerationRoute}";
            _ollamaValue.Text = managed
                ? health.EndpointReachable ? "Online" : "Needs attention"
                : externalExposure ? "NETWORK EXPOSED" : listener.HasListeners ? "External • loopback" : "External • offline";
            _ollamaValue.ForeColor = managed && health.EndpointReachable
                ? UiTheme.Success
                : externalExposure || managed
                    ? UiTheme.Warning
                    : UiTheme.Cyan;
            _ollamaMeta.Text = managed
                ? health.ModelLoaded ? "Model currently loaded" : "No helper model loaded"
                : externalExposure
                    ? "Unsafe listener detected outside helper control"
                    : "Listener checked; external model state was not queried";
            _storageValue.Text = state?.ModelStorageLocation ?? (managed ? "Not selected" : "Existing path preserved");
            _loadedValue.Text = managed ? health.ModelLoaded ? "Yes" : "No" : "Not checked";
            _loadedValue.ForeColor = managed && health.ModelLoaded ? UiTheme.Warning : managed ? UiTheme.Success : UiTheme.Muted;
            _hardwareValue.Text = state is null
                ? "Setup required"
                : $"{state.HardwareTier} tier • {gpu?.Name ?? "CPU only"}";
            _technicalValue.Text = managed
                ? health.ErrorCode is null ? "Healthy (passive check)" : FriendlyHealth(health.ErrorCode, health.ErrorMessage)
                : externalExposure
                    ? "External reviewer is active; packaged safeguards do not control its exposed Ollama listener"
                    : "External local_gpu_reviewer preserved; packaged controls do not apply";
            _technicalValue.ForeColor = managed && health.ErrorCode is not null || externalExposure ? UiTheme.Warning : UiTheme.Muted;
            _lowImpactButton.Text = state?.Preferences.LowImpactMode == true ? "Low impact: On" : "Low impact: Off";
            _keepWarmButton.Text = state?.Preferences.KeepWarm == true ? "Keep warm: On" : "Keep warm: Off";
            _notice.Text = !managed
                ? externalExposure
                    ? "Codex may see the external reviewer, but this app cannot pause, lock, unload, or secure it. Ollama is reachable beyond loopback; stop and correct that external startup before local review."
                    : "Codex may see the preserved external reviewer, but this app cannot pause, lock, unload, or verify it. Packaged controls apply only to helper-managed integrations."
                : string.Equals(state?.LastHealthCheckCode, "EXTERNAL_AUTOSTART_UNVERIFIED", StringComparison.Ordinal)
                    ? "Another Ollama startup artifact was preserved to avoid duplication, but it is not verified. Review/remove it or use manual startup before enabling local review."
                : state?.Preferences.AutoStartOllama == false
                    ? "Automatic Ollama startup is off. Start Ollama manually after sign-in before using local review."
                    : "Low-impact mode is recommended while emulators, Expo, device testing, or graphics workloads are active.";
            SetActionControlsEnabled(!IsOperationInProgress);
        }
        catch (Exception exception)
        {
            _managedActionsAllowed = false;
            StyleBadge(_stateBadge, UiTheme.Danger);
            _stateBadge.Text = "UNAVAILABLE";
            _heroTitle.Text = "Status could not be read";
            _heroMessage.Text = "No configuration was changed.";
            _technicalValue.Text = exception.Message;
            _notice.Text = exception.Message;
            ConfigurePrimary("Retry status", RefreshAsync, "Runs the passive status check again. It does not load or download a model.");
        }
    }

    private void UpdateHero(
        InstallationState? state,
        ReviewerHealthResult health,
        bool managed,
        OllamaListenerStatus listener)
    {
        if (state is null)
        {
            StyleBadge(_stateBadge, UiTheme.Warning);
            _stateBadge.Text = "SETUP";
            _heroTitle.Text = "Finish local reviewer setup";
            _heroMessage.Text = "Choose a supported model or point to an existing Ollama model folder.";
            ConfigurePrimary("Open guided setup", ShowSetupAsync, "Opens the guided setup. Downloads occur only after an explicit model choice and confirmation.");
            return;
        }

        if (!managed)
        {
            var exposed = listener.HasListeners && !listener.LoopbackOnly;
            StyleBadge(_stateBadge, exposed ? UiTheme.Danger : UiTheme.Cyan);
            _stateBadge.Text = exposed ? "EXTERNAL RISK" : "EXTERNAL";
            _heroTitle.Text = exposed ? "External Ollama is network-exposed" : "Codex reviewer is external";
            _heroMessage.Text = exposed
                ? "The preserved reviewer is outside this app, and its Ollama listener is not loopback-only."
                : "Codex may use the preserved reviewer, but this Control Center does not control or verify it.";
            ConfigurePrimary("Understand external setup", ShowProtectedStatusAsync, "Explains the preserved ownership boundary and which packaged safeguards do not apply.");
            return;
        }

        switch (state.Availability)
        {
            case HelperAvailability.Enabled:
                StyleBadge(_stateBadge, UiTheme.Success);
                _stateBadge.Text = "READY";
                _heroTitle.Text = "Local review is ready";
                _heroMessage.Text = health.ModelLoaded
                    ? "The selected model is loaded. Release GPU when other workloads need headroom."
                    : "The model stays unloaded until Codex requests a review.";
                ConfigurePrimary("Pause reviews", async () => await RunControlAsync(() => Control().PauseAsync()), "Pauses new reviews and unloads the selected model while leaving the MCP entry configured.");
                break;
            case HelperAvailability.Paused:
                StyleBadge(_stateBadge, UiTheme.Warning);
                _stateBadge.Text = "PAUSED";
                _heroTitle.Text = "Local review is paused";
                _heroMessage.Text = "No new helper reviews will run until you resume.";
                ConfigurePrimary("Resume reviews", async () => await RunControlAsync(() => Control().ResumeAsync()), "Verifies the local safety checks and resumes reviews without preloading the model.");
                break;
            default:
                StyleBadge(_stateBadge, UiTheme.Muted);
                _stateBadge.Text = "OFF";
                _heroTitle.Text = state.SelectedModel is null ? "Choose a model to begin" : "Local review is disabled";
                _heroMessage.Text = state.SelectedModel is null
                    ? "Open guided setup to use an existing model or explicitly approve a supported download."
                    : "Enable after the passive safety checks pass.";
                ConfigurePrimary(
                    state.SelectedModel is null ? "Finish guided setup" : "Enable integration",
                    state.SelectedModel is null ? ShowSetupAsync : async () => await RunControlAsync(() => Control().EnableAsync()),
                    state.SelectedModel is null
                        ? "Shows supported models and asks before any download or local validation."
                        : "Verifies Ollama, model storage, integrity, and loopback networking before enabling the MCP entry.");
                break;
        }
    }

    private void ConfigurePrimary(string text, Func<Task> command, string help)
    {
        _primaryAction.Text = text;
        _primaryAction.Enabled = !IsOperationInProgress;
        _primaryActionCommand = command;
        SetHelp(_primaryAction, help);
    }

    private async Task ShowSetupAsync()
    {
        var state = await new StateStore(_paths.StateFile).LoadAsync();
        using var wizard = new SetupWizardForm(_paths, state?.Preferences.AutoStartOllama);
        _ = wizard.ShowDialog(this);
        await RefreshAsync();
    }

    private Task ShowProtectedStatusAsync()
    {
        var listener = OllamaAutoStartManager.GetListenerStatus(11434);
        var listenerMessage = listener.HasListeners && !listener.LoopbackOnly
            ? "\n\nSafety warning: Ollama currently has a non-loopback listener. Local review is not safe until that external startup is corrected."
            : "\n\nThe current listener check found no network-exposed Ollama endpoint.";
        MessageBox.Show(
            this,
            "A local_gpu_reviewer entry already existed before this helper was installed, so it was preserved byte-for-byte. Codex can expose that external reviewer's tools, but this Control Center cannot pause, disable, lock, unload, test, or secure it. The packaged concurrency lock, pressure guard, and rollback controls apply only after a separate explicitly reviewed migration to the packaged integration."
                + listenerMessage,
            "External reviewer preserved",
            MessageBoxButtons.OK,
            listener.HasListeners && !listener.LoopbackOnly ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        return Task.CompletedTask;
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
        if (IntegrationOwnership.Inspect(_paths, state).Status != IntegrationOwnershipStatus.ManagedValid)
        {
            await ShowProtectedStatusAsync();
            return;
        }

        if (state is null || string.IsNullOrWhiteSpace(state.SelectedModel))
        {
            MessageBox.Show(this, "Choose and validate a model first.", ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(
            this,
            $"Run one small local inference with {state.SelectedModel}, verify the response, and unload it afterward?",
            "Test local review",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _notice.Text = $"local_gpu_reviewer • Ollama • {state.SelectedModel} • bounded validation";
        var validation = await new InstallationManager().ValidateSelectedModelAsync(_paths, state);
        MessageBox.Show(this, validation.Message, validation.Code, MessageBoxButtons.OK,
            validation.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        await RefreshAsync();
    }

    private async Task ChangeModelAsync()
    {
        var state = await new StateStore(_paths.StateFile).LoadAsync();
        if (state is null)
        {
            await ShowSetupAsync();
            return;
        }

        if (IntegrationOwnership.Inspect(_paths, state).Status != IntegrationOwnershipStatus.ManagedValid)
        {
            await ShowProtectedStatusAsync();
            return;
        }

        if (state.SelectedModel is null)
        {
            await ShowSetupAsync();
            return;
        }

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

        if (MessageBox.Show(
            this,
            $"Use {dialog.SelectedModel.Tag}? It will download only if missing, run one bounded local validation, and unload afterward.",
            "Confirm model",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        var store = new StateStore(_paths.StateFile);
        var result = await new ModelChangeService(_paths, store, Control(), new InstallationManager())
            .ChangeAsync(dialog.SelectedModel.Tag, dialog.AcceptRestrictedLicense);
        MessageBox.Show(this, result.Message, result.Code, MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        await RefreshAsync();
    }

    private async Task MoveModelsAsync()
    {
        var state = await new StateStore(_paths.StateFile).LoadAsync();
        if (IntegrationOwnership.Inspect(_paths, state).Status != IntegrationOwnershipStatus.ManagedValid)
        {
            await ShowProtectedStatusAsync();
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose an empty folder on fixed local storage. The move is verified and rolls back on failure.",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (MessageBox.Show(this, "Move and verify all helper-managed Ollama model files now?", "Move model storage", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
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
        var managed = IntegrationOwnership.Inspect(managedPaths, state).Status == IntegrationOwnershipStatus.ManagedValid;
        using var dialog = new ReliabilityBaselineDialog(
            managedPaths,
            state.HardwareTier,
            state.ReliabilityBaselineInstalled,
            installLocalGpuGuidance: managed);
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
                ? "The reviewed managed section was applied and a timestamped backup was retained."
                : "The file already matches the reviewed choice; nothing changed.",
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
        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"));
        var health = await new ReviewerService(_paths, store, client).GetHealthAsync();
        await new DiagnosticsExporter().ExportAsync(dialog.FileName, _paths, hardware, state, health);
        MessageBox.Show(this, "Redacted diagnostics exported. Prompts, responses, identities, model contents, and credentials were excluded.", ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private Task ShowUninstallGuidanceAsync()
    {
        MessageBox.Show(
            this,
            "Open Windows Settings > Apps > Installed apps > Codex GPU Thalen Helper > Uninstall. Only helper-owned files and marked sections are removed. Pre-existing Ollama, models, Codex authentication, and unrelated configuration are preserved.",
            "Uninstall help",
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

    private Button AddActionButton(
        FlowLayoutPanel panel,
        string text,
        string help,
        Func<Task> action,
        AppButtonStyle style = AppButtonStyle.Secondary,
        bool managedOnly = false)
    {
        var button = UiTheme.Button(text, style);
        SetHelp(button, help);
        button.Click += async (_, _) => await RunButtonActionAsync(action);
        panel.Controls.Add(button);
        _actionButtons.Add(button);
        if (managedOnly)
        {
            _managedActionButtons.Add(button);
        }
        return button;
    }

    private async Task RunButtonActionAsync(Func<Task> action)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            SetActionControlsEnabled(false);
            await action();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try
            {
                if (!IsDisposed && !Disposing)
                {
                    await RefreshAsync();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _operationInProgress, 0);
                if (!IsDisposed && !Disposing)
                {
                    SetActionControlsEnabled(true);
                }
            }
        }
    }

    private bool IsOperationInProgress
        => Volatile.Read(ref _operationInProgress) != 0;

    private void SetActionControlsEnabled(bool enabled)
    {
        _primaryAction.Enabled = enabled && _primaryActionCommand is not null;
        foreach (var actionButton in _actionButtons)
        {
            actionButton.Enabled = enabled
                && (_managedActionsAllowed || !_managedActionButtons.Contains(actionButton));
        }
    }

    private void SetHelp(Control control, string help)
    {
        control.AccessibleDescription = help;
        _toolTip.SetToolTip(control, help);
    }

    private static FlowLayoutPanel ActionColumn()
        => new()
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent
        };

    private static FlowLayoutPanel ActionFlow()
        => new()
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            MaximumSize = new Size(490, 0),
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 7)
        };

    private static void AddDetail(TableLayoutPanel table, int column, int row, string name, Label value)
    {
        var label = UiTheme.Label(name, 8.75F, UiTheme.Muted);
        label.Padding = new Padding(0, 7, 0, 5);
        value.Padding = new Padding(0, 7, 8, 5);
        value.MaximumSize = new Size(300, 42);
        table.Controls.Add(label, column, row);
        table.Controls.Add(value, column + 1, row);
    }

    private static void AddStackRow(TableLayoutPanel stack, Control control)
    {
        var row = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(control, 0, row);
    }

    private static void StyleBadge(Label badge, Color color)
    {
        badge.ForeColor = color;
        badge.BackColor = Color.FromArgb(34, color);
        badge.Padding = new Padding(9, 5, 9, 5);
        badge.Margin = new Padding(0);
    }

    private static Label MetricValue() => UiTheme.Label("—", 14F, UiTheme.Text, FontStyle.Bold);
    private static Label MetricMeta() => UiTheme.Label("Checking…", 8.5F, UiTheme.Muted);
    private static Label DetailValue() => UiTheme.Label("—", 9F, UiTheme.Text);

    private static string FriendlyHealth(string code, string? message)
        => code switch
        {
            "MANUAL_START_REQUIRED" => "Ollama must be started manually after sign-in",
            "EXTERNAL_AUTOSTART_UNVERIFIED" => "Existing Ollama startup is preserved but not verified",
            "OLLAMA_NETWORK_EXPOSURE" => "Blocked: Ollama is not loopback-only",
            "MODEL_PATH_NOT_CONFIGURED" => "Model folder needs attention",
            "SELECTED_MODEL_UNAVAILABLE" => "Selected model is not installed",
            "MODEL_DIGEST_MISMATCH" => "Model integrity check failed",
            _ => string.IsNullOrWhiteSpace(message) ? code : message
        };

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
