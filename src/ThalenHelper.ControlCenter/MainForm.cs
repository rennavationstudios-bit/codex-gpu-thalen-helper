using ThalenHelper.Core;

namespace ThalenHelper.ControlCenter;

public sealed class MainForm : Form
{
    private readonly ProductPaths _paths = ProductPaths.Resolve(installDirectory: AppContext.BaseDirectory);
    private readonly DarkToolTip _toolTip = UiTheme.ToolTip();
    private readonly Label _stateBadge = UiTheme.Label("CHECKING", 8.5F, UiTheme.Muted, FontStyle.Bold);
    private readonly Label _heroTitle = UiTheme.Label("Checking your local reviewer", 18F, UiTheme.Text, FontStyle.Bold);
    private readonly Label _heroMessage = UiTheme.Label("Reading passive local status. No model will be loaded.", 9.5F, UiTheme.Muted);
    private readonly Label _routeValue = MetricValue();
    private readonly Label _routeMeta = MetricMeta();
    private readonly Label _gpuValue = MetricValue();
    private readonly Label _gpuMeta = MetricMeta();
    private readonly Label _providersValue = MetricValue();
    private readonly Label _providersMeta = MetricMeta();
    private readonly Label _notice = UiTheme.Label("Low-impact mode protects other GPU workloads.", 9F, UiTheme.Muted);
    private readonly ToggleSwitch _reviewsToggle = UiTheme.Toggle("Local reviews");
    private readonly ToggleSwitch _lowImpactToggle = UiTheme.Toggle("Low-impact mode");
    private readonly ToggleSwitch _automaticRoutingToggle = UiTheme.Toggle("Automatic model routing");
    private readonly ToggleSwitch _keepWarmToggle = UiTheme.Toggle("Keep model warm");
    private readonly List<Control> _actionControls = [];
    private readonly List<Control> _managedActionControls = [];
    private Button _testReviewerButton = null!;
    private Button _retryStatusButton = null!;
    private Button _manageModelsButton = null!;
    private Button _advancedButton = null!;
    private Button _releaseGpuButton = null!;
    private RoundedPanel _advancedCard = null!;
    private InstallationState? _currentState;
    private bool _managedActionsAllowed;
    private bool _managedConfigEnabled;
    private bool _refreshingUi;
    private bool _advancedExpanded;
    private int _operationInProgress;

    public MainForm()
    {
        Text = "Thalen AI — Local Review for Codex";
        Size = new Size(1040, 740);
        UiTheme.Apply(this, new Size(860, 620));

        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = UiTheme.Canvas,
            AccessibleName = "Thalen AI Control Center"
        };
        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 0,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(24, 22, 24, 28),
            BackColor = UiTheme.Canvas
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        AddStackRow(stack, BuildHero());
        AddStackRow(stack, BuildOverview());
        AddStackRow(stack, BuildHomeControls());
        _advancedCard = BuildAdvancedSettings();
        _advancedCard.Visible = false;
        AddStackRow(stack, _advancedCard);
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
            Height = 202,
            Margin = new Padding(0, 0, 0, 16),
            Padding = new Padding(26, 21, 26, 21),
            AccessibleName = "Local reviewer overview"
        };
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));

        var copy = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 5,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent
        };
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var eyebrow = UiTheme.Label("THALEN AI  •  PRIVATE LOCAL REVIEW", 8.5F, UiTheme.Cyan, FontStyle.Bold);
        eyebrow.Margin = new Padding(0, 0, 0, 8);
        var product = UiTheme.Label("Local review for Codex", 22F, UiTheme.Text, FontStyle.Bold);
        product.Margin = new Padding(0, 0, 0, 3);
        var subtitle = UiTheme.Label("A task-aware second opinion on your own hardware. Codex stays in charge.", 10F, UiTheme.Muted);
        subtitle.Margin = new Padding(0, 0, 0, 12);
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
        _heroMessage.Margin = new Padding(0, 4, 0, 0);
        copy.Controls.Add(eyebrow, 0, 0);
        copy.Controls.Add(product, 0, 1);
        copy.Controls.Add(subtitle, 0, 2);
        copy.Controls.Add(stateLine, 0, 3);
        copy.Controls.Add(_heroMessage, 0, 4);

        var switchHost = SettingRow(
            "Local reviews",
            "Off pauses reviews but keeps Codex connected.",
            _reviewsToggle,
            compact: true);
        switchHost.Padding = new Padding(12, 38, 0, 0);
        SetHelp(_reviewsToggle, "Turn local reviews on or pause them without disconnecting Codex.");
        _reviewsToggle.CheckedChanged += async (_, _) =>
        {
            if (!_refreshingUi)
            {
                await RunActionAsync(ToggleReviewsAsync);
            }
        };
        RegisterAction(_reviewsToggle, managedOnly: true);

        grid.Controls.Add(copy, 0, 0);
        grid.Controls.Add(switchHost, 1, 0);
        hero.Controls.Add(grid);
        return hero;
    }

    private Control BuildOverview()
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            Dock = DockStyle.Top,
            Height = 134,
            Margin = new Padding(0, 0, 0, 16),
            BackColor = UiTheme.Canvas
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 39F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
        grid.Controls.Add(MetricCard("AUTOMATIC ROUTING", _routeValue, _routeMeta), 0, 0);
        grid.Controls.Add(MetricCard("GPU", _gpuValue, _gpuMeta), 1, 0);
        grid.Controls.Add(MetricCard("PROVIDERS", _providersValue, _providersMeta, last: true), 2, 0);
        return grid;
    }

    private static Control MetricCard(string title, Label value, Label meta, bool last = false)
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Margin = last ? new Padding(0) : new Padding(0, 0, 12, 0),
            Padding = new Padding(18, 15, 18, 14)
        };
        var table = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 3,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var heading = UiTheme.SectionLabel(title);
        value.AutoSize = false;
        value.Dock = DockStyle.Fill;
        value.AutoEllipsis = true;
        meta.AutoSize = false;
        meta.Dock = DockStyle.Fill;
        meta.AutoEllipsis = true;
        table.Controls.Add(heading, 0, 0);
        table.Controls.Add(value, 0, 1);
        table.Controls.Add(meta, 0, 2);
        card.Controls.Add(table);
        return card;
    }

    private Control BuildHomeControls()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 16),
            Padding = new Padding(20, 17, 20, 16)
        };
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = Color.Transparent
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(UiTheme.SectionLabel("LOCAL REVIEW"), 0, 0);

        var actions = ActionFlow();
        _testReviewerButton = AddActionButton(
            actions,
            "Test reviewer",
            "Runs one small confirmed review using the current automatic route, then verifies release.",
            TestLocalReviewAsync,
            AppButtonStyle.Primary,
            managedOnly: true);
        _retryStatusButton = AddActionButton(
            actions,
            "Retry status",
            "Run the passive status checks again. This does not load a model.",
            () => Task.CompletedTask,
            AppButtonStyle.Primary);
        _retryStatusButton.Visible = false;
        _manageModelsButton = AddActionButton(
            actions,
            "Models & storage",
            "Open guided model and storage setup. Nothing downloads without confirmation.",
            ShowModelSetupAsync,
            managedOnly: true);
        _advancedButton = AddActionButton(
            actions,
            "Advanced settings",
            "Show less-common controls, diagnostics, repair, and disconnect options.",
            ToggleAdvancedAsync,
            AppButtonStyle.Quiet);
        layout.Controls.Add(actions, 0, 1);

        var lowImpact = SettingRow(
            "Low-impact mode",
            "Keeps local review light and unloads immediately—recommended during emulators, Expo, and graphics work.",
            _lowImpactToggle);
        SetHelp(_lowImpactToggle, "Reduce GPU impact and unload immediately after each review.");
        _lowImpactToggle.CheckedChanged += async (_, _) =>
        {
            if (!_refreshingUi)
            {
                await RunActionAsync(ToggleLowImpactAsync);
            }
        };
        RegisterAction(_lowImpactToggle, managedOnly: true);
        layout.Controls.Add(lowImpact, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private RoundedPanel BuildAdvancedSettings()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 16),
            Padding = new Padding(20, 17, 20, 16),
            AccessibleName = "Advanced settings"
        };
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 0,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        AddStackRow(layout, UiTheme.SectionLabel("ADVANCED SETTINGS"));

        var routing = SettingRow(
            "Automatic model routing",
            "Selects the best eligible installed model for each task. Turning this off pins the saved fallback model.",
            _automaticRoutingToggle);
        SetHelp(_automaticRoutingToggle, "Switch between task-aware automatic routing and one pinned model.");
        _automaticRoutingToggle.CheckedChanged += async (_, _) =>
        {
            if (!_refreshingUi)
            {
                await RunActionAsync(ToggleModelRoutingAsync);
            }
        };
        RegisterAction(_automaticRoutingToggle, managedOnly: true);
        AddStackRow(layout, routing);

        var keepWarm = SettingRow(
            "Keep model warm",
            "Optional faster follow-ups. Automatic routing and low-impact mode normally keep this off.",
            _keepWarmToggle);
        SetHelp(_keepWarmToggle, "Keep one pinned model loaded briefly between reviews when safety policy permits.");
        _keepWarmToggle.CheckedChanged += async (_, _) =>
        {
            if (!_refreshingUi)
            {
                await RunActionAsync(ToggleKeepWarmAsync);
            }
        };
        RegisterAction(_keepWarmToggle, managedOnly: true);
        AddStackRow(layout, keepWarm);

        var actions = ActionFlow();
        _releaseGpuButton = AddActionButton(actions, "Release GPU", "Release only a proven helper-owned loaded model.", async () => await RunControlAsync(() => Control().ReleaseGpuAsync()), managedOnly: true);
        AddActionButton(actions, "Move storage", "Move helper-managed Ollama files with hash verification and rollback.", MoveModelsAsync, managedOnly: true);
        AddActionButton(actions, "Repair", "Recheck the managed integration, startup, paths, and passive health.", RepairAsync, managedOnly: true);
        AddActionButton(actions, "Reliability baseline", "Preview the optional managed AGENTS reliability section before applying it.", ConfigureReliabilityBaselineAsync, AppButtonStyle.Quiet);
        AddActionButton(actions, "Export diagnostics", "Save a redacted local diagnostics report.", ExportDiagnosticsAsync, AppButtonStyle.Quiet);
        AddActionButton(actions, "Disconnect from Codex", "Disable the helper MCP entry. Restart Codex to remove its tools from an open task.", DisconnectAsync, AppButtonStyle.Danger, managedOnly: true);
        AddActionButton(actions, "Uninstall help", "Explain safe removal while preserving unrelated Codex settings and existing models.", ShowUninstallGuidanceAsync, AppButtonStyle.Quiet);
        AddStackRow(layout, actions);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildNotice()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0),
            Padding = new Padding(18, 14, 18, 14),
            OutlineColor = Color.FromArgb(52, 61, 84)
        };
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Dock = DockStyle.Top,
            BackColor = Color.Transparent
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 26));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        var mark = UiTheme.Label("●", 10F, UiTheme.Cyan, FontStyle.Bold);
        mark.Margin = new Padding(0, 2, 8, 0);
        _notice.AutoSize = true;
        _notice.Dock = DockStyle.Fill;
        _notice.MaximumSize = new Size(900, 0);
        row.Controls.Add(mark, 0, 0);
        row.Controls.Add(_notice, 1, 0);
        card.Controls.Add(row);
        return card;
    }

    private async void OnShownAsync(object? sender, EventArgs eventArgs)
    {
        var state = File.Exists(_paths.StateFile)
            ? await new StateStore(_paths.StateFile).LoadAsync().ConfigureAwait(true)
            : null;
        if (state is null
            || string.Equals(state.LastHealthCheckCode, "INSTALLED_MODEL_SETUP_REQUIRED", StringComparison.Ordinal))
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
            _currentState = state;
            using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"));
            var reviewer = new ReviewerService(_paths, store, client);
            var health = await reviewer.GetHealthAsync().ConfigureAwait(true);
            var configManager = new CodexConfigManager();
            var managed = IntegrationOwnership.Inspect(_paths, state, configManager).Status == IntegrationOwnershipStatus.ManagedValid;
            var configEnabled = managed && configManager.TryReadManagedEnabled(_paths, out var enabledValue) && enabledValue;
            _managedActionsAllowed = managed;
            _managedConfigEnabled = configEnabled;
            var listener = OllamaAutoStartManager.GetListenerStatus(11434);
            var externalExposure = !managed && listener.HasListeners && !listener.LoopbackOnly;

            ReviewerPlanResult? quickPlan = null;
            ReviewerPlanResult? standardPlan = null;
            ReviewerPlanResult? deepPlan = null;
            if (managed && state?.Preferences.ModelSelectionMode == ModelSelectionMode.Automatic)
            {
                quickPlan = await reviewer.PlanAsync(new ReviewRequest(
                    "Preview a small local review route.",
                    TaskKind: ReviewTaskKind.LogTriage,
                    Effort: ReviewEffort.Quick,
                    EstimatedInputCharacters: 1_000)).ConfigureAwait(true);
                standardPlan = await reviewer.PlanAsync(new ReviewRequest(
                    "Preview a normal code review route.",
                    TaskKind: ReviewTaskKind.DiffReview,
                    Effort: ReviewEffort.Standard,
                    EstimatedInputCharacters: 8_000)).ConfigureAwait(true);
                deepPlan = await reviewer.PlanAsync(new ReviewRequest(
                    "Preview a deep code review route.",
                    TaskKind: ReviewTaskKind.DiffReview,
                    Effort: ReviewEffort.Deep,
                    EstimatedInputCharacters: 24_000)).ConfigureAwait(true);
            }

            UpdateHero(state, health, managed, configEnabled, listener);
            UpdateRoutePresentation(state, health, quickPlan, standardPlan, deepPlan);
            _testReviewerButton.Visible = true;
            _retryStatusButton.Visible = false;
            var gpu = hardware.Gpus.OrderByDescending(item => item.DedicatedMemoryBytes).FirstOrDefault();
            _gpuValue.Text = gpu?.Name.Replace("NVIDIA GeForce ", string.Empty, StringComparison.OrdinalIgnoreCase) ?? "CPU only";
            _gpuMeta.Text = gpu is null
                ? "CPU fallback is never automatic"
                : $"{FormatBytes(gpu.DedicatedMemoryBytes)} VRAM  •  {(health.ModelLoaded ? "Model loaded" : "GPU free")}";
            _releaseGpuButton.Visible = managed && health.ModelLoaded;

            _refreshingUi = true;
            _reviewsToggle.Checked = state?.Availability == HelperAvailability.Enabled && configEnabled;
            _lowImpactToggle.Checked = state?.Preferences.LowImpactMode == true;
            _automaticRoutingToggle.Checked = state?.Preferences.ModelSelectionMode == ModelSelectionMode.Automatic;
            _keepWarmToggle.Checked = state?.Preferences.KeepWarm == true;
            _refreshingUi = false;

            _notice.Text = !managed
                ? externalExposure
                    ? "This preserved external reviewer is network-exposed and outside Thalen's safety controls."
                    : "An existing external reviewer was preserved. Thalen will not take control without a reviewed migration."
                : string.Equals(state?.LastHealthCheckCode, "EXTERNAL_AUTOSTART_UNVERIFIED", StringComparison.Ordinal)
                    ? "An existing Ollama startup entry was preserved but could not be verified."
                    : state?.Preferences.AutoStartOllama == false
                        ? "Automatic Ollama startup is off. Start Ollama manually after sign-in when that provider is needed."
                        : "Models stay unloaded until Codex requests a review. Low-impact mode is recommended during GPU-heavy work.";
            SetActionControlsEnabled(!IsOperationInProgress);
        }
        catch (Exception exception)
        {
            _managedActionsAllowed = false;
            _refreshingUi = true;
            _reviewsToggle.Checked = false;
            _refreshingUi = false;
            StyleBadge(_stateBadge, UiTheme.Danger);
            _stateBadge.Text = "UNAVAILABLE";
            _heroTitle.Text = "Status could not be read";
            _heroMessage.Text = "No configuration was changed.";
            _routeValue.Text = "Unavailable";
            _routeMeta.Text = FriendlyHealth("STATUS_UNAVAILABLE", exception.Message);
            _notice.Text = exception.Message;
            SetActionControlsEnabled(false);
            _testReviewerButton.Visible = false;
            _retryStatusButton.Visible = true;
            _retryStatusButton.Enabled = true;
        }
    }

    private void UpdateRoutePresentation(
        InstallationState? state,
        ReviewerHealthResult health,
        ReviewerPlanResult? quickPlan,
        ReviewerPlanResult? standardPlan,
        ReviewerPlanResult? deepPlan)
    {
        if (state?.Preferences.ModelSelectionMode == ModelSelectionMode.Automatic)
        {
            var quick = PlanLabel(quickPlan, "Quick route unavailable");
            var deep = PlanLabel(deepPlan, "Deep route unavailable");
            _routeValue.Text = standardPlan?.Allowed == true && !string.IsNullOrWhiteSpace(standardPlan.Model)
                ? $"{DisplayModel(standardPlan.Model)}  •  {standardPlan.Provider}"
                : "Automatic route unavailable";
            _routeMeta.Text = $"Normal review  •  automatic   |   Quick: {quick}";
            _toolTip.SetToolTip(_routeMeta, $"Quick: {quick}\nNormal: {PlanLabel(standardPlan, "Unavailable")}\nDeep: {deep}");
            var providers = new[] { quickPlan, standardPlan, deepPlan }
                .Where(plan => plan?.Allowed == true && !string.IsNullOrWhiteSpace(plan.Provider))
                .Select(plan => plan!.Provider)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _providersValue.Text = providers.Length == 0 ? "No ready provider" : string.Join(" + ", providers);
            _providersMeta.Text = $"{health.EligibleInstalledModels} installed + validated model{(health.EligibleInstalledModels == 1 ? string.Empty : "s")} eligible";
            return;
        }

        var catalog = new ModelCatalogService().LoadBundled();
        var selected = catalog.Models.FirstOrDefault(item =>
            string.Equals(item.Tag, state?.SelectedModel, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ModelProviders.Normalize(item.Provider), ModelProviders.Normalize(state?.SelectedModelProvider), StringComparison.Ordinal));
        _routeValue.Text = state?.SelectedModel is null ? "No model selected" : DisplayModel(state.SelectedModel);
        _routeMeta.Text = health.ModelAvailable
            ? $"Installed + validated  •  {ModelProviders.Normalize(state?.SelectedModelProvider)}"
            : selected is null
                ? "Choose a supported installed model"
                : $"Not installed  •  {FormatBytes(selected.ExpectedDownloadBytes)} download if approved";
        _providersValue.Text = ModelProviders.Normalize(state?.SelectedModelProvider);
        _providersMeta.Text = health.EndpointReachable ? "Loopback provider ready" : "Provider needs attention";
    }

    private void UpdateHero(
        InstallationState? state,
        ReviewerHealthResult health,
        bool managed,
        bool configEnabled,
        OllamaListenerStatus listener)
    {
        if (state is null)
        {
            StyleBadge(_stateBadge, UiTheme.Warning);
            _stateBadge.Text = "SETUP";
            _heroTitle.Text = "Finish local reviewer setup";
            _heroMessage.Text = "Use models you already have or approve one guided download.";
            return;
        }

        if (!managed)
        {
            var exposed = listener.HasListeners && !listener.LoopbackOnly;
            StyleBadge(_stateBadge, exposed ? UiTheme.Danger : UiTheme.Cyan);
            _stateBadge.Text = exposed ? "EXTERNAL RISK" : "EXTERNAL";
            _heroTitle.Text = exposed ? "External provider is network-exposed" : "Existing reviewer preserved";
            _heroMessage.Text = exposed
                ? "That listener is outside Thalen's loopback safety boundary."
                : "Thalen did not replace or take control of the existing integration.";
            return;
        }

        if (!configEnabled)
        {
            StyleBadge(_stateBadge, UiTheme.Warning);
            _stateBadge.Text = "OFF";
            _heroTitle.Text = "Codex integration is off";
            _heroMessage.Text = "Turn Local reviews on, then restart Codex to reconnect its tools.";
            return;
        }

        switch (state.Availability)
        {
            case HelperAvailability.Enabled:
                StyleBadge(_stateBadge, UiTheme.Success);
                _stateBadge.Text = "READY";
                _heroTitle.Text = "Local reviews are on";
                _heroMessage.Text = health.ModelLoaded
                    ? "A helper-owned model is currently active."
                    : "Nothing is loaded until Codex asks for a review.";
                break;
            case HelperAvailability.Paused:
                StyleBadge(_stateBadge, UiTheme.Warning);
                _stateBadge.Text = "PAUSED";
                _heroTitle.Text = "Local reviews are paused";
                _heroMessage.Text = "Codex stays connected, but no new local reviews will run.";
                break;
            default:
                StyleBadge(_stateBadge, UiTheme.Muted);
                _stateBadge.Text = "OFF";
                _heroTitle.Text = state.SelectedModel is null ? "Choose a model to begin" : "Local review is disconnected";
                _heroMessage.Text = state.SelectedModel is null
                    ? "Open Models & storage to finish setup."
                    : "Reconnect after the passive safety checks pass.";
                break;
        }
    }

    private async Task ToggleReviewsAsync()
    {
        var state = _currentState ?? await new StateStore(_paths.StateFile).LoadAsync();
        if (state is null)
        {
            await ShowModelSetupAsync();
            return;
        }

        if (IntegrationOwnership.Inspect(_paths, state).Status != IntegrationOwnershipStatus.ManagedValid)
        {
            await ShowProtectedStatusAsync();
            return;
        }

        if (_reviewsToggle.Checked)
        {
            await RunControlAsync(() => ShouldEnableCodexEntry(state.Availability, _managedConfigEnabled)
                ? Control().EnableAsync()
                : Control().ResumeAsync());
        }
        else if (state.Availability == HelperAvailability.Enabled)
        {
            await RunControlAsync(() => Control().PauseAsync());
        }
    }

    private async Task ToggleLowImpactAsync()
        => await RunControlAsync(() => Control().SetLowImpactAsync(_lowImpactToggle.Checked));

    private async Task ToggleKeepWarmAsync()
        => await RunControlAsync(() => Control().SetKeepWarmAsync(_keepWarmToggle.Checked));

    private async Task ToggleModelRoutingAsync()
    {
        var mode = _automaticRoutingToggle.Checked ? ModelSelectionMode.Automatic : ModelSelectionMode.Pinned;
        await RunControlAsync(() => Control().SetModelSelectionModeAsync(mode));
    }

    private Task ToggleAdvancedAsync()
    {
        _advancedExpanded = !_advancedExpanded;
        _advancedCard.Visible = _advancedExpanded;
        _advancedButton.Text = _advancedExpanded ? "Hide advanced" : "Advanced settings";
        return Task.CompletedTask;
    }

    private async Task TestLocalReviewAsync()
    {
        var state = await new StateStore(_paths.StateFile).LoadAsync();
        if (IntegrationOwnership.Inspect(_paths, state).Status != IntegrationOwnershipStatus.ManagedValid)
        {
            await ShowProtectedStatusAsync();
            return;
        }

        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"));
        var reviewer = new ReviewerService(_paths, new StateStore(_paths.StateFile), client);
        var request = new ReviewRequest(
            "Return exactly THALEN_READY. Do not add explanation.",
            Focus: "One bounded connectivity check only.",
            MaximumOutputTokens: 32,
            BusyBehavior: ReviewBusyBehavior.Skip,
            TaskKind: ReviewTaskKind.DiffReview,
            Effort: ReviewEffort.Standard,
            DesiredContextTokens: 8_192,
            EstimatedInputCharacters: 120);
        var plan = await reviewer.PlanAsync(request);
        if (!plan.Allowed || string.IsNullOrWhiteSpace(plan.Model))
        {
            MessageBox.Show(this, plan.ErrorMessage ?? plan.Reason ?? "No safe local route is currently available.", "Test reviewer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (MessageBox.Show(
            this,
            $"Run one small review with {DisplayModel(plan.Model)} through {plan.Provider}? The model will unload afterward.",
            "Test reviewer",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _notice.Text = $"local_gpu_reviewer  •  {plan.Provider}  •  {DisplayModel(plan.Model)}  •  bounded test";
        var result = await reviewer.ReviewAsync(request);
        var after = await reviewer.GetHealthAsync();
        if (result.ModelRan && after.ModelLoaded)
        {
            _ = await Control().ReleaseGpuAsync();
            after = await reviewer.GetHealthAsync();
        }
        var released = !after.ModelLoaded;
        var responseVerified = IsReadyResponse(result);
        var passed = result.ModelRan && responseVerified && released;
        var message = result.ModelRan && responseVerified
            ? $"Review succeeded with {DisplayModel(result.Model ?? plan.Model)} through {result.Provider}.\n\nElapsed: {TimeSpan.FromMilliseconds(result.ElapsedMilliseconds):m\\:ss}.  Model released: {(released ? "Yes" : "Needs attention")}."
            : result.ModelRan
                ? "The model ran, but its response did not match the expected bounded readiness token."
                : result.ErrorMessage ?? "The model did not run.";
        MessageBox.Show(this, message, passed ? "Local review passed" : result.ErrorCode ?? "Local review needs attention", MessageBoxButtons.OK, passed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    internal static bool IsReadyResponse(ReviewerResult result)
        => result.ModelRan
           && string.Equals(result.Findings?.Trim(), "THALEN_READY", StringComparison.Ordinal);

    internal static bool ShouldEnableCodexEntry(HelperAvailability availability, bool managedConfigEnabled)
        => availability == HelperAvailability.Disabled || !managedConfigEnabled;

    private async Task ShowModelSetupAsync()
    {
        var state = await new StateStore(_paths.StateFile).LoadAsync();
        using var wizard = new SetupWizardForm(_paths, state?.Preferences.AutoStartOllama, startAtModelSelection: state is not null);
        _ = wizard.ShowDialog(this);
    }

    private Task ShowProtectedStatusAsync()
    {
        var listener = OllamaAutoStartManager.GetListenerStatus(11434);
        var listenerMessage = listener.HasListeners && !listener.LoopbackOnly
            ? "\n\nSafety warning: the current provider listener is not loopback-only."
            : "\n\nThe current listener check found no network-exposed Ollama endpoint.";
        MessageBox.Show(
            this,
            "A local_gpu_reviewer entry existed before this helper was installed, so it was preserved. Thalen cannot pause, disable, lock, unload, test, or secure that external integration without an explicitly reviewed migration."
                + listenerMessage,
            "Existing reviewer preserved",
            MessageBoxButtons.OK,
            listener.HasListeners && !listener.LoopbackOnly ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        return Task.CompletedTask;
    }

    private async Task MoveModelsAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose an empty folder on fixed local storage. The move is verified and rolls back on failure.",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK
            || MessageBox.Show(this, "Move and verify all helper-managed Ollama model files now?", "Move model storage", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        var store = new StateStore(_paths.StateFile);
        var result = await new ModelsMoveService(_paths, store, Control()).MoveAsync(dialog.SelectedPath);
        MessageBox.Show(this, result.Message, result.Code, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task RepairAsync()
    {
        var result = await new InstallationManager().RepairAsync(_paths);
        MessageBox.Show(this, result.Message, result.Code, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        MessageBox.Show(this, result.Changed
            ? "The reviewed managed section was applied and a timestamped backup was retained."
            : "The file already matches the reviewed choice; nothing changed.", ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    private async Task DisconnectAsync()
    {
        if (MessageBox.Show(this, "Disconnect the local reviewer from future Codex tasks? Existing models and unrelated Codex settings remain untouched.", "Disconnect from Codex", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        await RunControlAsync(() => Control().DisableAsync(true));
    }

    private Task ShowUninstallGuidanceAsync()
    {
        MessageBox.Show(this, "Open Windows Settings > Apps > Installed apps > Codex GPU Thalen Helper > Uninstall. Only helper-owned files and managed sections are removed. Existing models, Codex authentication, and unrelated settings are preserved.", "Uninstall help", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return Task.CompletedTask;
    }

    private async Task RunControlAsync(Func<Task<ControlResult>> action)
    {
        var result = await action();
        _notice.Text = result.Message;
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
        button.Click += async (_, _) => await RunActionAsync(action);
        panel.Controls.Add(button);
        RegisterAction(button, managedOnly);
        return button;
    }

    private void RegisterAction(Control control, bool managedOnly)
    {
        _actionControls.Add(control);
        if (managedOnly)
        {
            _managedActionControls.Add(control);
        }
    }

    private async Task RunActionAsync(Func<Task> action)
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
        foreach (var control in _actionControls)
        {
            var setupRecoveryAllowed = ReferenceEquals(control, _manageModelsButton) && _currentState is null;
            control.Enabled = enabled
                && (_managedActionsAllowed || setupRecoveryAllowed || !_managedActionControls.Contains(control));
        }
    }

    private void SetHelp(Control control, string help)
    {
        control.AccessibleDescription = help;
        _toolTip.SetToolTip(control, help);
    }

    private static FlowLayoutPanel ActionFlow()
        => new()
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6)
        };

    private static Control SettingRow(string title, string description, ToggleSwitch toggle, bool compact = false)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = Color.Transparent,
            Padding = compact ? new Padding(0) : new Padding(0, 10, 0, 8),
            Margin = new Padding(0)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var copy = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Top,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        var heading = UiTheme.Label(title, compact ? 9F : 10F, UiTheme.Text, FontStyle.Bold);
        heading.Margin = new Padding(0, 0, 0, 2);
        var detail = UiTheme.Label(description, compact ? 8F : 8.75F, UiTheme.Muted);
        detail.Margin = new Padding(0);
        copy.Controls.Add(heading);
        copy.Controls.Add(detail);
        toggle.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        toggle.Margin = new Padding(18, compact ? 3 : 5, 0, 0);
        row.Controls.Add(copy, 0, 0);
        row.Controls.Add(toggle, 1, 0);
        return row;
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

    private static string PlanLabel(ReviewerPlanResult? plan, string fallback)
        => plan?.Allowed == true && !string.IsNullOrWhiteSpace(plan.Model)
            ? $"{DisplayModel(plan.Model)} ({plan.Provider})"
            : fallback;

    internal static string DisplayModel(string model)
    {
        if (model.Contains("qwythos", StringComparison.OrdinalIgnoreCase))
        {
            return "Qwythos 9B";
        }

        return model
            .Replace("qwen3-coder:", "Qwen3 Coder ", StringComparison.OrdinalIgnoreCase)
            .Replace("qwen3:", "Qwen3 ", StringComparison.OrdinalIgnoreCase)
            .Replace("qwen2.5-coder:", "Qwen2.5 Coder ", StringComparison.OrdinalIgnoreCase)
            .Replace("b", "B", StringComparison.OrdinalIgnoreCase);
    }

    private static Label MetricValue() => UiTheme.Label("—", 13.5F, UiTheme.Text, FontStyle.Bold);
    private static Label MetricMeta() => UiTheme.Label("Checking…", 8.5F, UiTheme.Muted);

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
