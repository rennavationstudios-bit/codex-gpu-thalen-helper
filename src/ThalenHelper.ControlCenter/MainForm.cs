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
    private readonly Label _deepRouteValue = MetricValue();
    private readonly Label _deepRouteMeta = MetricMeta();
    private readonly Label _gpuValue = MetricValue();
    private readonly Label _gpuMeta = MetricMeta();
    private readonly Label _providersValue = MetricValue();
    private readonly Label _providersMeta = MetricMeta();
    private readonly Label _notice = UiTheme.Label("Checking local GPU status…", 9.5F, UiTheme.Text, FontStyle.Bold);
    private readonly Label _gpuModeValue = UiTheme.Label("CHECKING", 8F, UiTheme.Cyan, FontStyle.Bold);
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
    private Label _normalRoutePurpose = null!;
    private Control _deepRouteDivider = null!;
    private Control _deepRouteRow = null!;
    private RoundedPanel? _statePill;
    private Image? _brandIcon;
    private InstallationState? _currentState;
    private bool _managedActionsAllowed;
    private bool _managedConfigEnabled;
    private bool _refreshingUi;
    private bool _advancedExpanded;
    private int _operationInProgress;
    private long _refreshSequence;
    private readonly SupersedingRefreshCoordinator _refreshCoordinator = new();

    public MainForm()
    {
        Text = "Thalen AI — Local Review for Codex";
        Size = new Size(620, 570);
        UiTheme.Apply(this, new Size(560, 520));

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
            _refreshCoordinator.Dispose();
            _toolTip.Dispose();
            _brandIcon?.Dispose();
        }

        base.Dispose(disposing);
    }

    private Control BuildHero()
    {
        var hero = new GradientPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 16),
            Padding = new Padding(18),
            AccessibleName = "Local reviewer overview"
        };
        var shell = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var brand = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(4, 0, 4, 12)
        };
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var identity = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        _brandIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)?.ToBitmap();
        if (_brandIcon is not null)
        {
            identity.Controls.Add(new PictureBox
            {
                Image = _brandIcon,
                Size = new Size(28, 28),
                SizeMode = PictureBoxSizeMode.Zoom,
                Margin = new Padding(0, 0, 10, 0),
                AccessibleName = "Thalen AI app icon"
            });
        }
        var brandName = UiTheme.Label("Thalen AI", 10F, UiTheme.Text, FontStyle.Bold);
        brandName.Margin = new Padding(0, 5, 0, 0);
        identity.Controls.Add(brandName);

        var brandActions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        _statePill = new RoundedPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            CornerRadius = 14,
            Padding = new Padding(10, 5, 10, 5),
            Margin = new Padding(0, 1, 0, 0),
            BackColor = Color.FromArgb(21, 47, 43),
            OutlineColor = Color.FromArgb(64, UiTheme.Success)
        };
        _stateBadge.Margin = new Padding(0);
        _stateBadge.Padding = new Padding(0);
        _statePill.Controls.Add(_stateBadge);

        _advancedButton = UiTheme.Button("•••", AppButtonStyle.Quiet);
        _advancedButton.AutoSize = false;
        _advancedButton.Size = new Size(38, 30);
        _advancedButton.MinimumSize = new Size(38, 30);
        _advancedButton.MaximumSize = new Size(38, 30);
        _advancedButton.Padding = new Padding(7, 3, 7, 3);
        _advancedButton.Margin = new Padding(0, 0, 8, 0);
        _advancedButton.AccessibleName = "Advanced settings";
        SetHelp(_advancedButton, "Open less-common routing, GPU, repair, diagnostics, and disconnect settings.");
        _advancedButton.Click += (_, _) => _ = ToggleAdvancedAsync();
        RegisterAction(_advancedButton, managedOnly: false);
        brandActions.Controls.Add(_advancedButton);
        brandActions.Controls.Add(_statePill);

        brand.Controls.Add(identity, 0, 0);
        brand.Controls.Add(brandActions, 1, 0);

        var reviewCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            Padding = new Padding(18, 16, 18, 16),
            CornerRadius = 16
        };
        var reviewGrid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent
        };
        reviewGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        reviewGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var copy = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 3,
            Dock = DockStyle.Fill,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        copy.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        var section = UiTheme.SectionLabel("LOCAL REVIEWS");
        section.ForeColor = UiTheme.Cyan;
        section.Margin = new Padding(0, 0, 0, 4);
        _heroTitle.Font = UiTheme.BodyFont(18F, FontStyle.Bold);
        _heroTitle.AutoSize = false;
        _heroTitle.Dock = DockStyle.Fill;
        _heroTitle.AutoEllipsis = true;
        _heroTitle.TextAlign = ContentAlignment.MiddleLeft;
        _heroTitle.Margin = new Padding(0);
        _heroMessage.AutoSize = false;
        _heroMessage.Dock = DockStyle.Fill;
        _heroMessage.AutoEllipsis = true;
        _heroMessage.TextAlign = ContentAlignment.MiddleLeft;
        _heroMessage.Margin = new Padding(0);
        copy.Controls.Add(section, 0, 0);
        copy.Controls.Add(_heroTitle, 0, 1);
        copy.Controls.Add(_heroMessage, 0, 2);
        reviewGrid.Controls.Add(copy, 0, 0);

        var switchHost = new Panel
        {
            AutoSize = false,
            Size = new Size(72, 74),
            BackColor = Color.Transparent,
            Margin = new Padding(12, 0, 0, 0)
        };
        _reviewsToggle.Location = new Point(16, 25);
        switchHost.Controls.Add(_reviewsToggle);
        SetHelp(_reviewsToggle, "Turn local reviews on or pause them without disconnecting Codex.");
        _reviewsToggle.CheckedChanged += async (_, _) =>
        {
            if (!_refreshingUi)
            {
                var desiredEnabled = _reviewsToggle.Checked;
                await RunToggleActionAsync(() => ToggleReviewsAsync(desiredEnabled));
            }
        };
        RegisterAction(_reviewsToggle, managedOnly: true);
        reviewGrid.Controls.Add(switchHost, 1, 0);
        reviewCard.Controls.Add(reviewGrid);

        shell.Controls.Add(brand, 0, 0);
        shell.Controls.Add(reviewCard, 0, 1);
        hero.Controls.Add(shell);
        StyleBadge(_stateBadge, UiTheme.Muted);
        return hero;
    }

    private Control BuildOverview()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 16),
            Padding = new Padding(18, 15, 18, 13),
            CornerRadius = 16,
            AccessibleName = "Automatic routing overview"
        };
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 7,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var index = 0; index < 7; index++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 10)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = UiTheme.SectionLabel("AUTOMATIC ROUTING");
        title.ForeColor = UiTheme.Cyan;
        title.Margin = new Padding(0);
        _providersValue.Font = UiTheme.BodyFont(9F, FontStyle.Bold);
        _providersValue.Margin = new Padding(8, 0, 0, 0);
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(_providersValue, 1, 0);

        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(Divider(), 0, 1);
        _normalRoutePurpose = RoutePurpose("Normal & deep");
        layout.Controls.Add(RouteRow(_normalRoutePurpose, _routeValue, _routeMeta), 0, 2);
        _deepRouteDivider = Divider();
        _deepRouteRow = RouteRow(RoutePurpose("Deep"), _deepRouteValue, _deepRouteMeta);
        _deepRouteDivider.Visible = false;
        _deepRouteRow.Visible = false;
        layout.Controls.Add(_deepRouteDivider, 0, 3);
        layout.Controls.Add(_deepRouteRow, 0, 4);
        layout.Controls.Add(Divider(), 0, 5);
        layout.Controls.Add(RouteRow(RoutePurpose("Quick / GPU busy"), _gpuValue, _gpuMeta), 0, 6);
        card.Controls.Add(layout);
        return card;
    }

    private static Label RoutePurpose(string text)
    {
        var label = UiTheme.Label(text, 9F, UiTheme.Muted);
        label.AutoSize = false;
        label.Dock = DockStyle.Fill;
        label.Height = 22;
        label.AutoEllipsis = true;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Margin = new Padding(0, 2, 8, 0);
        return label;
    }

    private static Control RouteRow(Label purposeLabel, Label model, Label provider)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 8, 0, 8)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        model.Font = UiTheme.BodyFont(9F, FontStyle.Bold);
        model.AutoSize = false;
        model.Size = new Size(160, 22);
        model.AutoEllipsis = true;
        model.TextAlign = ContentAlignment.MiddleRight;
        model.Margin = new Padding(8, 2, 14, 0);
        provider.Font = UiTheme.BodyFont(8F, FontStyle.Bold);
        provider.AutoSize = false;
        provider.Dock = DockStyle.Fill;
        provider.Height = 22;
        provider.AutoEllipsis = true;
        provider.ForeColor = UiTheme.Cyan;
        provider.TextAlign = ContentAlignment.MiddleRight;
        provider.Margin = new Padding(0, 2, 0, 0);
        row.Controls.Add(purposeLabel, 0, 0);
        row.Controls.Add(model, 1, 0);
        row.Controls.Add(provider, 2, 0);
        return row;
    }

    private static Control Divider()
        => new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = UiTheme.Border,
            Margin = new Padding(0)
        };

    private Control BuildHomeControls()
    {
        var actions = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Top,
            Height = 52,
            Margin = new Padding(0, 0, 0, 16),
            BackColor = UiTheme.Canvas
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));

        var primaryHost = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 5, 0),
            BackColor = UiTheme.Canvas
        };
        _testReviewerButton = UiTheme.Button("Test reviewer", AppButtonStyle.Primary);
        _testReviewerButton.AutoSize = false;
        _testReviewerButton.Dock = DockStyle.Fill;
        _testReviewerButton.Margin = new Padding(0);
        SetHelp(_testReviewerButton, "Run one small confirmed review with the automatic route, then verify release.");
        _testReviewerButton.Click += async (_, _) => await RunActionAsync(TestLocalReviewAsync);
        RegisterAction(_testReviewerButton, managedOnly: true);
        primaryHost.Controls.Add(_testReviewerButton);

        _retryStatusButton = UiTheme.Button("Retry status", AppButtonStyle.Primary);
        _retryStatusButton.AutoSize = false;
        _retryStatusButton.Dock = DockStyle.Fill;
        _retryStatusButton.Margin = new Padding(0);
        SetHelp(_retryStatusButton, "Run the passive status checks again. This does not load a model.");
        _retryStatusButton.Click += async (_, _) => await RunActionAsync(() => Task.CompletedTask);
        RegisterAction(_retryStatusButton, managedOnly: false);
        primaryHost.Controls.Add(_retryStatusButton);
        _retryStatusButton.Visible = false;

        _manageModelsButton = UiTheme.Button("Models & storage", AppButtonStyle.Secondary);
        _manageModelsButton.AutoSize = false;
        _manageModelsButton.Dock = DockStyle.Fill;
        _manageModelsButton.Margin = new Padding(5, 0, 0, 0);
        SetHelp(_manageModelsButton, "Open guided model and storage setup. Nothing downloads without confirmation.");
        _manageModelsButton.Click += async (_, _) => await RunActionAsync(ShowModelSetupAsync);
        RegisterAction(_manageModelsButton, managedOnly: true);

        actions.Controls.Add(primaryHost, 0, 0);
        actions.Controls.Add(_manageModelsButton, 1, 0);
        return actions;
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

        var lowImpact = SettingRow(
            "Low-impact mode",
            "Keeps local review light and unloads immediately—recommended during emulators, Expo, and graphics work.",
            _lowImpactToggle);
        SetHelp(_lowImpactToggle, "Reduce GPU impact and unload immediately after each review.");
        _lowImpactToggle.CheckedChanged += async (_, _) =>
        {
            if (!_refreshingUi)
            {
                var desiredEnabled = _lowImpactToggle.Checked;
                await RunToggleActionAsync(() => ToggleLowImpactAsync(desiredEnabled));
            }
        };
        RegisterAction(_lowImpactToggle, managedOnly: true);
        AddStackRow(layout, lowImpact);

        var routing = SettingRow(
            "Automatic model routing",
            "Selects the best eligible installed model for each task. Turning this off pins the saved fallback model.",
            _automaticRoutingToggle);
        SetHelp(_automaticRoutingToggle, "Switch between task-aware automatic routing and one pinned model.");
        _automaticRoutingToggle.CheckedChanged += async (_, _) =>
        {
            if (!_refreshingUi)
            {
                var desiredEnabled = _automaticRoutingToggle.Checked;
                await RunToggleActionAsync(() => ToggleModelRoutingAsync(desiredEnabled));
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
                var desiredEnabled = _keepWarmToggle.Checked;
                await RunToggleActionAsync(() => ToggleKeepWarmAsync(desiredEnabled));
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
            Padding = new Padding(14, 11, 14, 11),
            CornerRadius = 14,
            BackColor = Color.FromArgb(14, 28, 25),
            OutlineColor = Color.FromArgb(51, 104, 83)
        };
        var row = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            Dock = DockStyle.Top,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var mark = UiTheme.Label("●", 11F, UiTheme.Success, FontStyle.Bold);
        mark.Margin = new Padding(0, 6, 0, 0);
        var copy = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        var heading = UiTheme.SectionLabel("GPU STATUS");
        heading.ForeColor = UiTheme.Cyan;
        heading.Margin = new Padding(0, 0, 0, 1);
        _notice.AutoSize = false;
        _notice.Dock = DockStyle.Fill;
        _notice.AutoEllipsis = true;
        _notice.TextAlign = ContentAlignment.MiddleLeft;
        _notice.Margin = new Padding(0);
        copy.Controls.Add(heading, 0, 0);
        copy.Controls.Add(_notice, 0, 1);
        _gpuModeValue.Margin = new Padding(12, 11, 0, 0);
        row.Controls.Add(mark, 0, 0);
        row.Controls.Add(copy, 1, 0);
        row.Controls.Add(_gpuModeValue, 2, 0);
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
        var refreshSequence = Interlocked.Increment(ref _refreshSequence);
        var refreshCancellation = _refreshCoordinator.Begin();
        var cancellationToken = refreshCancellation.Token;
        try
        {
            var hardware = new HardwareDetector().Detect();
            var store = new StateStore(_paths.StateFile);
            var state = await store.LoadAsync(cancellationToken).ConfigureAwait(true);
            using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"));
            var reviewer = new ReviewerService(_paths, store, client);
            var configManager = new CodexConfigManager();
            var managed = IntegrationOwnership.Inspect(_paths, state, configManager).Status == IntegrationOwnershipStatus.ManagedValid;
            var configEnabled = managed && configManager.TryReadManagedEnabled(_paths, out var enabledValue) && enabledValue;
            var listener = OllamaAutoStartManager.GetListenerStatus(11434);
            var externalExposure = !managed && listener.HasListeners && !listener.LoopbackOnly;

            var healthTask = reviewer.GetHealthAsync(cancellationToken);
            Task<ReviewerPlanResult>? quickPlanTask = null;
            Task<ReviewerPlanResult>? standardPlanTask = null;
            Task<ReviewerPlanResult>? deepPlanTask = null;
            if (managed && state?.Preferences.ModelSelectionMode == ModelSelectionMode.Automatic)
            {
                quickPlanTask = reviewer.PlanAsync(new ReviewRequest(
                    "Preview a small local review route.",
                    TaskKind: ReviewTaskKind.LogTriage,
                    Effort: ReviewEffort.Quick,
                    EstimatedInputCharacters: 1_000), cancellationToken);
                standardPlanTask = reviewer.PlanAsync(new ReviewRequest(
                    "Preview a normal code review route.",
                    TaskKind: ReviewTaskKind.DiffReview,
                    Effort: ReviewEffort.Standard,
                    EstimatedInputCharacters: 8_000), cancellationToken);
                deepPlanTask = reviewer.PlanAsync(new ReviewRequest(
                    "Preview a deep code review route.",
                    TaskKind: ReviewTaskKind.DiffReview,
                    Effort: ReviewEffort.Deep,
                    EstimatedInputCharacters: 24_000), cancellationToken);
            }

            var passiveTasks = new List<Task> { healthTask };
            if (quickPlanTask is not null) passiveTasks.Add(quickPlanTask);
            if (standardPlanTask is not null) passiveTasks.Add(standardPlanTask);
            if (deepPlanTask is not null) passiveTasks.Add(deepPlanTask);
            await Task.WhenAll(passiveTasks).ConfigureAwait(true);

            if (refreshSequence != Volatile.Read(ref _refreshSequence) || IsDisposed || Disposing)
            {
                return;
            }

            var health = await healthTask.ConfigureAwait(true);
            var quickPlan = quickPlanTask is null ? null : await quickPlanTask.ConfigureAwait(true);
            var standardPlan = standardPlanTask is null ? null : await standardPlanTask.ConfigureAwait(true);
            var deepPlan = deepPlanTask is null ? null : await deepPlanTask.ConfigureAwait(true);
            _currentState = state;
            _managedActionsAllowed = managed;
            _managedConfigEnabled = configEnabled;

            UpdateHero(state, health, managed, configEnabled, listener);
            _toolTip.SetToolTip(_heroTitle, _heroTitle.Text);
            _toolTip.SetToolTip(_heroMessage, _heroMessage.Text);
            UpdateRoutePresentation(state, health, quickPlan, standardPlan, deepPlan);
            _testReviewerButton.Visible = true;
            _retryStatusButton.Visible = false;
            var gpu = hardware.Gpus.OrderByDescending(item => item.DedicatedMemoryBytes).FirstOrDefault();
            _toolTip.SetToolTip(_notice, gpu is null
                ? "No discrete GPU was detected. CPU fallback is never automatic."
                : $"{gpu.Name} • {FormatBytes(gpu.DedicatedMemoryBytes)} VRAM");
            _releaseGpuButton.Visible = managed && health.ModelLoaded;

            _refreshingUi = true;
            _reviewsToggle.Checked = state?.Availability == HelperAvailability.Enabled && configEnabled;
            _lowImpactToggle.Checked = state?.Preferences.LowImpactMode == true;
            _automaticRoutingToggle.Checked = state?.Preferences.ModelSelectionMode == ModelSelectionMode.Automatic;
            _keepWarmToggle.Checked = state?.Preferences.KeepWarm == true;
            _refreshingUi = false;

            _notice.Text = !managed
                ? externalExposure
                    ? "External reviewer is network-exposed"
                    : "External reviewer preserved"
                : string.Equals(state?.LastHealthCheckCode, "EXTERNAL_AUTOSTART_UNVERIFIED", StringComparison.Ordinal)
                    ? "Ollama startup needs attention"
                    : state?.Preferences.AutoStartOllama == false
                        ? "Manual Ollama startup"
                        : health.ModelLoaded ? "Helper model loaded" : "No model loaded";
            _gpuModeValue.Text = state?.Preferences.LowImpactMode == true ? "LOW IMPACT" : "BALANCED";
            SetActionControlsEnabled(!IsOperationInProgress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer refresh or user action superseded this passive read.
        }
        catch (Exception exception)
        {
            if (refreshSequence != Volatile.Read(ref _refreshSequence) || IsDisposed || Disposing)
            {
                return;
            }

            _managedActionsAllowed = false;
            _refreshingUi = true;
            _reviewsToggle.Checked = false;
            _refreshingUi = false;
            StyleBadge(_stateBadge, UiTheme.Danger);
            _stateBadge.Text = "UNAVAILABLE";
            _heroTitle.Text = "Status could not be read";
            _heroMessage.Text = "No configuration was changed.";
            _routeValue.Text = "Unavailable";
            _routeMeta.Text = "—";
            _deepRouteValue.Text = "Unavailable";
            _deepRouteMeta.Text = "—";
            _deepRouteDivider.Visible = false;
            _deepRouteRow.Visible = false;
            _gpuValue.Text = "Unavailable";
            _gpuMeta.Text = "—";
            _providersValue.Text = "Needs attention";
            _notice.Text = exception.Message;
            _toolTip.SetToolTip(_notice, exception.Message);
            _gpuModeValue.Text = "UNAVAILABLE";
            SetActionControlsEnabled(false);
            _testReviewerButton.Visible = false;
            _retryStatusButton.Visible = true;
            _retryStatusButton.Enabled = true;
        }
        finally
        {
            _refreshCoordinator.Complete(refreshCancellation);
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
            var sharedNormalAndDeepRoute = PlansShareRoute(standardPlan, deepPlan);
            _normalRoutePurpose.Text = sharedNormalAndDeepRoute ? "Normal & deep" : "Normal";
            _deepRouteDivider.Visible = !sharedNormalAndDeepRoute;
            _deepRouteRow.Visible = !sharedNormalAndDeepRoute;
            _routeValue.Text = standardPlan?.Allowed == true && !string.IsNullOrWhiteSpace(standardPlan.Model)
                ? DisplayModel(standardPlan.Model)
                : "Unavailable";
            _routeMeta.Text = standardPlan?.Allowed == true ? standardPlan.Provider ?? "—" : "—";
            _deepRouteValue.Text = deepPlan?.Allowed == true && !string.IsNullOrWhiteSpace(deepPlan.Model)
                ? DisplayModel(deepPlan.Model)
                : "Unavailable";
            _deepRouteMeta.Text = deepPlan?.Allowed == true ? deepPlan.Provider ?? "—" : "—";
            _gpuValue.Text = quickPlan?.Allowed == true && !string.IsNullOrWhiteSpace(quickPlan.Model)
                ? DisplayModel(quickPlan.Model)
                : "Unavailable";
            _gpuMeta.Text = quickPlan?.Allowed == true ? quickPlan.Provider ?? "—" : "—";
            _toolTip.SetToolTip(_routeMeta, $"Quick: {quick}\nNormal: {PlanLabel(standardPlan, "Unavailable")}\nDeep: {deep}");
            var providers = new[] { quickPlan, standardPlan, deepPlan }
                .Where(plan => plan?.Allowed == true && !string.IsNullOrWhiteSpace(plan.Provider))
                .Select(plan => plan!.Provider)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _providersValue.Text = providers.Length == 0 ? "Unavailable" : "Task-aware";
            _providersMeta.Text = $"{health.EligibleInstalledModels} installed + validated model{(health.EligibleInstalledModels == 1 ? string.Empty : "s")} eligible";
            _toolTip.SetToolTip(_routeValue, PlanLabel(standardPlan, "Normal route unavailable"));
            _toolTip.SetToolTip(_deepRouteValue, deep);
            _toolTip.SetToolTip(_gpuValue, quick);
            return;
        }

        _normalRoutePurpose.Text = "Normal & deep";
        _deepRouteDivider.Visible = false;
        _deepRouteRow.Visible = false;
        _routeValue.Text = state?.SelectedModel is null ? "No model selected" : DisplayModel(state.SelectedModel);
        _routeMeta.Text = health.ModelAvailable
            ? ModelProviders.Normalize(state?.SelectedModelProvider)
            : "Needs setup";
        _gpuValue.Text = state?.SelectedModel is null ? "No model selected" : DisplayModel(state.SelectedModel);
        _gpuMeta.Text = _routeMeta.Text;
        _providersValue.Text = "Pinned";
        _providersMeta.Text = health.EndpointReachable ? "Loopback provider ready" : "Provider needs attention";
    }

    internal static bool PlansShareRoute(ReviewerPlanResult? standardPlan, ReviewerPlanResult? deepPlan)
    {
        if (standardPlan?.Allowed != true || deepPlan?.Allowed != true)
        {
            return standardPlan?.Allowed != true && deepPlan?.Allowed != true;
        }

        return !string.IsNullOrWhiteSpace(standardPlan.Model)
            && !string.IsNullOrWhiteSpace(deepPlan.Model)
            && string.Equals(standardPlan.Model, deepPlan.Model, StringComparison.OrdinalIgnoreCase)
            && string.Equals(standardPlan.Provider, deepPlan.Provider, StringComparison.OrdinalIgnoreCase);
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

    private async Task ToggleReviewsAsync(bool desiredEnabled)
    {
        var state = await new StateStore(_paths.StateFile).LoadAsync();
        if (state is null)
        {
            await ShowModelSetupAsync();
            return;
        }

        var configManager = new CodexConfigManager();
        if (IntegrationOwnership.Inspect(_paths, state, configManager).Status != IntegrationOwnershipStatus.ManagedValid)
        {
            await ShowProtectedStatusAsync();
            return;
        }

        var configEnabled = configManager.TryReadManagedEnabled(_paths, out var enabledValue) && enabledValue;
        switch (SelectReviewToggleMutation(desiredEnabled, state.Availability, configEnabled))
        {
            case ReviewToggleMutation.Enable:
                await RunControlAsync(() => Control().EnableAsync());
                break;
            case ReviewToggleMutation.Resume:
                await RunControlAsync(() => Control().ResumeAsync());
                break;
            case ReviewToggleMutation.Pause:
                await RunControlAsync(() => Control().PauseAsync());
                break;
        }
    }

    private async Task ToggleLowImpactAsync(bool desiredEnabled)
        => await RunControlAsync(() => Control().SetLowImpactAsync(desiredEnabled));

    private async Task ToggleKeepWarmAsync(bool desiredEnabled)
        => await RunControlAsync(() => Control().SetKeepWarmAsync(desiredEnabled));

    private async Task ToggleModelRoutingAsync(bool automaticEnabled)
    {
        var mode = automaticEnabled ? ModelSelectionMode.Automatic : ModelSelectionMode.Pinned;
        await RunControlAsync(() => Control().SetModelSelectionModeAsync(mode));
    }

    private Task ToggleAdvancedAsync()
    {
        _advancedExpanded = !_advancedExpanded;
        _advancedCard.Visible = _advancedExpanded;
        _advancedButton.Text = _advancedExpanded ? "×" : "•••";
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

    internal static ReviewToggleMutation SelectReviewToggleMutation(
        bool desiredEnabled,
        HelperAvailability availability,
        bool managedConfigEnabled)
    {
        if (!desiredEnabled)
        {
            return availability == HelperAvailability.Enabled
                ? ReviewToggleMutation.Pause
                : ReviewToggleMutation.None;
        }

        return ShouldEnableCodexEntry(availability, managedConfigEnabled)
            ? ReviewToggleMutation.Enable
            : ReviewToggleMutation.Resume;
    }

    internal static async Task ReconcileBeforeReenableAsync(Func<Task> reconcile, Action reenable)
    {
        try
        {
            await reconcile().ConfigureAwait(true);
        }
        finally
        {
            reenable();
        }
    }

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

    private async Task<ControlResult> RunControlAsync(Func<Task<ControlResult>> action)
    {
        var result = await action();
        if (result.State is not null)
        {
            _currentState = result.State;
        }

        _notice.Text = result.Message;
        _toolTip.SetToolTip(_notice, result.Message);
        return result;
    }

    private async Task ReconcileToggleStateAsync()
    {
        var store = new StateStore(_paths.StateFile);
        var state = await store.LoadAsync().ConfigureAwait(true);
        var configManager = new CodexConfigManager();
        var managed = IntegrationOwnership.Inspect(_paths, state, configManager).Status == IntegrationOwnershipStatus.ManagedValid;
        var configEnabled = managed && configManager.TryReadManagedEnabled(_paths, out var enabledValue) && enabledValue;

        _currentState = state;
        _managedActionsAllowed = managed;
        _managedConfigEnabled = configEnabled;
        _refreshingUi = true;
        try
        {
            _reviewsToggle.Checked = state?.Availability == HelperAvailability.Enabled && configEnabled;
            _lowImpactToggle.Checked = state?.Preferences.LowImpactMode == true;
            _automaticRoutingToggle.Checked = state?.Preferences.ModelSelectionMode == ModelSelectionMode.Automatic;
            _keepWarmToggle.Checked = state?.Preferences.KeepWarm == true;
        }
        finally
        {
            _refreshingUi = false;
        }
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

        Interlocked.Increment(ref _refreshSequence);
        _refreshCoordinator.CancelCurrent();

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

    private async Task RunToggleActionAsync(Func<Task> action)
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _refreshSequence);
        _refreshCoordinator.CancelCurrent();
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
                await ReconcileBeforeReenableAsync(
                    async () =>
                    {
                        if (!IsDisposed && !Disposing)
                        {
                            await ReconcileToggleStateAsync();
                        }
                    },
                    () =>
                    {
                        Interlocked.Exchange(ref _operationInProgress, 0);
                        if (!IsDisposed && !Disposing)
                        {
                            SetActionControlsEnabled(true);
                        }
                    });
            }
            catch (Exception exception)
            {
                _notice.Text = exception.Message;
                _toolTip.SetToolTip(_notice, exception.Message);
            }
        }

        if (!IsDisposed && !Disposing)
        {
            await RefreshAsync();
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

    private void StyleBadge(Label badge, Color color)
    {
        badge.ForeColor = color;
        badge.BackColor = Color.Transparent;
        badge.Padding = new Padding(0);
        badge.Margin = new Padding(0);
        if (_statePill is not null)
        {
            _statePill.BackColor = Color.FromArgb(24, color);
            _statePill.OutlineColor = Color.FromArgb(115, color);
            _statePill.Invalidate();
        }
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

internal enum ReviewToggleMutation
{
    None,
    Enable,
    Resume,
    Pause
}

internal sealed class SupersedingRefreshCoordinator : IDisposable
{
    private CancellationTokenSource? _current;

    public CancellationTokenSource Begin()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _current, next);
        previous?.Cancel();
        return next;
    }

    public void CancelCurrent()
        => Volatile.Read(ref _current)?.Cancel();

    public void Complete(CancellationTokenSource completed)
    {
        _ = Interlocked.CompareExchange(ref _current, null, completed);
        completed.Dispose();
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref _current, null);
        if (current is null)
        {
            return;
        }

        current.Cancel();
        current.Dispose();
    }
}
