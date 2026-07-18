using ThalenHelper.Core;

namespace ThalenHelper.ControlCenter;

public sealed class SetupWizardForm : Form
{
    private const ulong GiB = 1024UL * 1024UL * 1024UL;
    private readonly ProductPaths _paths;
    private readonly InstallationState? _initialState;
    private readonly HardwareProfile _hardware;
    private readonly ModelManifest _catalog;
    private readonly ModelRecommendation _recommendation;
    private readonly StorageRecommendation? _storage;
    private readonly HashSet<string> _gpuCompatibleModels;
    private readonly DarkToolTip _toolTip = UiTheme.ToolTip();
    private readonly Panel _pageHost = new()
    {
        Dock = DockStyle.Fill,
        Padding = new Padding(34, 28, 34, 24),
        BackColor = UiTheme.Canvas
    };
    private readonly Label _step = UiTheme.Label("STEP 1 OF 5", 8.5F, UiTheme.Cyan, FontStyle.Bold);
    private readonly Button _back = UiTheme.Button("Back", AppButtonStyle.Quiet);
    private readonly Button _next = UiTheme.Button("Next", AppButtonStyle.Primary);
    private readonly Button _cancel = UiTheme.Button("Cancel", AppButtonStyle.Quiet);
    private readonly Button _browse = UiTheme.Button("Browse...", AppButtonStyle.Secondary);
    private readonly Button _browseLmStudio = UiTheme.Button("Choose GGUF...", AppButtonStyle.Secondary);
    private readonly TextBox _modelDirectory = UiTheme.TextBox(590);
    private readonly TextBox _lmStudioFile = UiTheme.TextBox(590);
    private readonly ComboBox _modelChoice = UiTheme.ComboBox(480);
    private readonly RadioButton _setupLater = UiTheme.RadioButton(
        "Install the helper now and finish model setup later",
        true);
    private readonly RadioButton _useOllama = UiTheme.RadioButton(
        "Use Ollama storage (verify an existing model or confirm one download)");
    private readonly RadioButton _registerLmStudio = UiTheme.RadioButton(
        "Register an existing LM Studio / GGUF model");
    private readonly CheckBox _allowCpuFallback = UiTheme.CheckBox(
        "Show CPU-safe fallback models when no conservative GPU fit is available");
    private readonly CheckBox _autoStart = UiTheme.CheckBox(
        "Start Ollama automatically after I sign in to Windows",
        true);
    private readonly CheckBox _installOllama = UiTheme.CheckBox(
        "Install the current official signed Ollama release if it is missing",
        true);
    private readonly CheckBox _installReliabilityBaseline = UiTheme.CheckBox(
        "Add the optional sanitized Codex reliability baseline");
    private readonly Label _modelStatus = UiTheme.Label(string.Empty, 9F, UiTheme.Muted);
    private readonly Label _storageStatus = UiTheme.Label(string.Empty, 9F, UiTheme.Muted);
    private readonly Label _result = UiTheme.Label(string.Empty, 10F, UiTheme.Text);
    private readonly TextBox _reliabilityPreview = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Width = 790,
        Height = 170,
        Font = new Font("Cascadia Mono", 8.5F),
        BackColor = UiTheme.SurfaceRaised,
        ForeColor = UiTheme.Muted,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly CancellationTokenSource _installationCancellation = new();
    private AgentsOverridePreview? _agentsPreview;
    private bool _installing;
    private int _page;

    public SetupWizardForm(
        ProductPaths paths,
        bool? autoStartPreference = null,
        bool startAtModelSelection = false)
    {
        _paths = paths;
        _initialState = new StateStore(paths.StateFile)
            .LoadAsync()
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        _hardware = new HardwareDetector().Detect();
        _catalog = new ModelCatalogService().LoadBundled();
        var selector = new ModelSelector();
        _recommendation = selector.Recommend(_hardware, _catalog, false);
        _storage = _recommendation.Model is null
            ? null
            : new StorageSelector().Recommend(_hardware, _recommendation.Model);
        _gpuCompatibleModels = selector.GetCompatibleModels(_hardware, _catalog, false)
            .Select(ModelKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _modelDirectory.Text = GetExistingOllamaModelsPath() ?? _storage?.ModelDirectory ?? string.Empty;
        _lmStudioFile.ReadOnly = true;
        _autoStart.Checked = autoStartPreference ?? true;
        _modelStatus.MaximumSize = new Size(780, 0);
        _storageStatus.MaximumSize = new Size(780, 0);
        _result.MaximumSize = new Size(790, 0);

        Text = "Set up Codex GPU Thalen Helper";
        Size = new Size(960, 790);
        UiTheme.Apply(this, new Size(900, 720));
        BuildShell();
        WireEvents();
        _page = startAtModelSelection ? 2 : 0;
        RefreshSetupPath();
        RenderPage();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _installationCancellation.Dispose();
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildShell()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiTheme.Canvas
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72F));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(_pageHost, 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        var header = new GradientPanel
        {
            Dock = DockStyle.Fill,
            CornerRadius = 0,
            Padding = new Padding(34, 22, 34, 18),
            GradientStart = Color.FromArgb(39, 23, 82),
            GradientEnd = UiTheme.Canvas
        };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent
        };
        var eyebrow = UiTheme.Label("PRIVATE AI REVIEW", 8.5F, UiTheme.Cyan, FontStyle.Bold);
        eyebrow.Margin = new Padding(0, 0, 0, 7);
        var title = UiTheme.Label("Make local review yours", 23F, UiTheme.Text, FontStyle.Bold);
        title.Margin = new Padding(0, 0, 0, 3);
        var subtitle = UiTheme.Label(
            "Five clear steps. No account, no telemetry, and no surprise model download.",
            9.5F,
            UiTheme.Muted);
        flow.Controls.Add(eyebrow);
        flow.Controls.Add(title);
        flow.Controls.Add(subtitle);
        header.Controls.Add(flow);
        return header;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = UiTheme.Surface,
            Padding = new Padding(28, 15, 24, 10)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _step.Margin = new Padding(4, 12, 0, 0);
        footer.Controls.Add(_step, 0, 0);

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        actions.Controls.Add(_next);
        actions.Controls.Add(_back);
        actions.Controls.Add(_cancel);
        footer.Controls.Add(actions, 1, 0);
        return footer;
    }

    private void WireEvents()
    {
        _back.Click += (_, _) =>
        {
            if (_page > 0)
            {
                _page--;
                RenderPage();
            }
        };
        _next.Click += async (_, _) => await AdvanceAsync();
        _cancel.Click += (_, _) =>
        {
            if (_installing)
            {
                _installationCancellation.Cancel();
                _cancel.Enabled = false;
                _result.Text = "Cancelling safely. Managed configuration will remain disabled if setup cannot finish.";
                return;
            }

            Close();
        };
        _browse.Click += (_, _) => BrowseForModelDirectory();
        _browseLmStudio.Click += (_, _) => BrowseForLmStudioModel();
        _modelChoice.SelectedIndexChanged += (_, _) =>
        {
            RefreshModelStatus();
            RefreshReliabilityPreview();
            UpdateNavigation();
        };
        _modelDirectory.TextChanged += (_, _) =>
        {
            RefreshModelStatus();
            UpdateNavigation();
        };
        _lmStudioFile.TextChanged += (_, _) =>
        {
            RefreshModelStatus();
            UpdateNavigation();
        };
        _setupLater.CheckedChanged += (_, _) => RefreshSetupPath();
        _useOllama.CheckedChanged += (_, _) => RefreshSetupPath();
        _registerLmStudio.CheckedChanged += (_, _) => RefreshSetupPath();
        _allowCpuFallback.CheckedChanged += (_, _) =>
        {
            RefreshModelChoices();
            RefreshModelStatus();
            RefreshReliabilityPreview();
            UpdateNavigation();
        };
        _installReliabilityBaseline.CheckedChanged += (_, _) => RefreshReliabilityPreview();
        FormClosing += (_, eventArgs) =>
        {
            if (_installing)
            {
                _installationCancellation.Cancel();
                eventArgs.Cancel = true;
            }
        };

        SetHelp(_back, "Returns to the previous setup step without applying changes.");
        SetHelp(_next, "Continues to the next step. The label changes before any action that can download a model.");
        SetHelp(_cancel, "Closes setup without applying the remaining choices. You can reopen setup from the dashboard.");
        SetHelp(_browse, "Chooses a fixed local folder for Ollama models. The wizard shows the drive type, current free space, model download size, and required safety reserve before setup can continue.");
        SetHelp(_browseLmStudio, "Chooses an existing audited GGUF file already indexed by LM Studio. The helper never downloads an LM Studio model or silently substitutes another file.");
        SetHelp(_modelChoice, "Shows the provider, approximate size, and hardware fit for each audited model. Models outside this PC's conservative GPU or explicitly enabled CPU budget remain unavailable and explained.");
        SetHelp(_modelDirectory, "The fixed local folder Ollama should use for model files. An existing OLLAMA_MODELS value is used only as the initial displayed choice.");
        SetHelp(_lmStudioFile, "The exact existing GGUF file selected for LM Studio registration. It is not copied, moved, or downloaded by this wizard.");
        SetHelp(_setupLater, "Installs and configures the helper without downloading or running a model. Local review stays disabled until model setup is completed.");
        SetHelp(_useOllama, "Uses only the named Ollama model and destination shown. If it is missing, setup requires a second confirmation before downloading that exact model; no fallback is allowed.");
        SetHelp(_registerLmStudio, "Registers only an existing audited LM Studio GGUF. Setup requires the exact file and a named confirmation before bounded validation; it never downloads a replacement.");
        SetHelp(_allowCpuFallback, "Shows only catalog models explicitly marked reasonable for CPU fallback and only when current system RAM has conservative headroom. CPU use can be slow and never turns on automatically.");
        SetHelp(_autoStart, "Creates one per-user startup helper. It checks the loopback endpoint and existing Ollama processes before starting anything, preventing duplicates.");
        SetHelp(_installOllama, "If Ollama cannot be reached, downloads and verifies the current official signed Windows installer. It never installs a model by itself.");
        SetHelp(_installReliabilityBaseline, "Optionally adds only the sanitized managed reliability section shown in the diff. Existing instructions are preserved.");
    }

    private void RenderPage()
    {
        _pageHost.Controls.Clear();
        _step.Text = $"STEP {_page + 1} OF 5";
        _back.Visible = _page > 0 && _page < 4;
        _cancel.Visible = _page < 4;
        Control page = _page switch
        {
            0 => WelcomePage(),
            1 => HardwarePage(),
            2 => SelectionPage(),
            3 => ReviewPage(),
            _ => ResultPage()
        };
        page.Dock = DockStyle.Fill;
        _pageHost.Controls.Add(page);
        UpdateNavigation();
    }

    private void UpdateNavigation()
    {
        if (_installing)
        {
            _next.Enabled = false;
            _back.Enabled = false;
            return;
        }

        _back.Enabled = true;
        var modelPathReady = IsModelPathReady();
        _next.Enabled = (_page is not 2 and not 3 || modelPathReady)
            && (_page != 3 || _agentsPreview is not null);
        _next.Text = _page switch
        {
            3 when _useOllama.Checked => "Confirm Ollama setup",
            3 when _registerLmStudio.Checked => "Confirm LM Studio setup",
            3 => "Install helper only",
            4 => "Close",
            _ => "Next"
        };
        var help = _page switch
        {
            3 when _useOllama.Checked => "Shows the exact Ollama provider, model, download size, required free-space reserve, and destination before any acquisition or validation.",
            3 when _registerLmStudio.Checked => "Shows the exact LM Studio provider, audited model, existing GGUF path, and file size before bounded registration validation.",
            3 => "Applies the reviewed managed settings without downloading or loading a model. Local review remains disabled until model setup is completed.",
            4 => "Closes setup and returns to the dashboard.",
            _ => "Continues to the next setup step without applying changes."
        };
        SetHelp(_next, help);
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

        var selectedOption = _modelChoice.SelectedItem as ModelChoiceOption;
        var selectedModel = selectedOption?.Model;
        if (!_setupLater.Checked && (selectedOption is null || !selectedOption.Available))
        {
            MessageBox.Show(
                this,
                selectedOption?.Reason ?? "No supported model is selected. Choose Finish setup later or select a compatible model.",
                ProductInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (_useOllama.Checked && selectedModel is not null)
        {
            var manifestHint = IsSelectedModelPresent()
                ? "A matching manifest was found in the selected folder, but Ollama's inventory remains authoritative."
                : "No matching manifest was found in the selected folder.";
            var storage = GetStorageAssessment(selectedModel, _modelDirectory.Text);
            var action = $"Provider: {ModelProviders.Ollama}\n"
                + $"Selected model: {selectedModel.Tag}\n"
                + $"Approximate download: {FormatGiB(selectedModel.ExpectedDownloadBytes)}\n"
                + $"Required free space including temporary overhead and reserve: {FormatGiB(storage.TotalRequiredBytes)}\n"
                + $"Destination: {Path.GetFullPath(_modelDirectory.Text)}\n\n"
                + $"{manifestHint} Ollama may repair or download this same selected model if its inventory is missing or inconsistent. "
                + "Setup will not switch to or download a different fallback model. It will validate the selected model locally with zero keep-alive and verify release when finished.";
            var confirmation = MessageBox.Show(
                this,
                action + "\n\nContinue?",
                "Confirm selected model",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);
            if (confirmation != DialogResult.OK)
            {
                return;
            }
        }
        else if (_registerLmStudio.Checked && selectedModel is not null)
        {
            var fullPath = Path.GetFullPath(_lmStudioFile.Text);
            var action = $"Provider: {ModelProviders.LmStudio}\n"
                + $"Selected model: {selectedModel.Tag}\n"
                + $"Expected existing file size: {FormatGiB(selectedModel.ExpectedDownloadBytes)}\n"
                + $"Existing GGUF: {fullPath}\n\n"
                + "The helper will not download, copy, move, or substitute a model. It will hash and bind this exact audited file, run bounded local validation, and unload only the helper-created LM Studio instance. Continue?";
            var confirmation = MessageBox.Show(
                this,
                action,
                "Confirm existing LM Studio model",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);
            if (confirmation != DialogResult.OK)
            {
                return;
            }
        }

        _next.Enabled = false;
        _back.Enabled = false;
        _installing = true;
        _page = 4;
        _result.Text = "Applying the reviewed choices...";
        RenderPage();
        try
        {
            var stateStore = new StateStore(_paths.StateFile);
            var priorState = await stateStore.LoadAsync(_installationCancellation.Token);
            var pullAndValidate = _useOllama.Checked;
            if (pullAndValidate && selectedModel is not null)
            {
                _result.Text = $"Using local_gpu_reviewer with Ollama and {selectedModel.Tag} for bounded validation. Zero keep-alive will be requested and release verified afterward.";
            }
            else if (_registerLmStudio.Checked && selectedModel is not null)
            {
                _result.Text = $"Registering the exact existing LM Studio model {selectedModel.Tag}. No download or substitute model is allowed.";
            }

            var outcome = await new InstallationManager().ConfigureAsync(
                new InstallationOptions(
                    _paths,
                    _useOllama.Checked ? selectedModel?.Tag : null,
                    _useOllama.Checked && !string.IsNullOrWhiteSpace(_modelDirectory.Text) ? _modelDirectory.Text : null,
                    selectedOption?.RequiresCpuOptIn == true,
                    false,
                    _useOllama.Checked
                        ? _autoStart.Checked
                        : priorState?.Preferences.AutoStartOllama ?? false,
                    pullAndValidate,
                    InstallReliabilityBaseline: _installReliabilityBaseline.Checked,
                    ExpectedAgentsSourceSha256: _agentsPreview?.SourceSha256,
                    ExpectedAgentsPlannedSha256: _agentsPreview?.PlannedSha256,
                    EnsureOllamaInstalledAsync: async cancellationToken =>
                    {
                        if (!_useOllama.Checked || !_installOllama.Checked || await IsOllamaReachableAsync())
                        {
                            return;
                        }

                        _result.Text = "Downloading and verifying the current official Ollama installer...";
                        using var installer = new OllamaInstallerService();
                        var install = await installer.DownloadVerifyAndLaunchAsync(
                            waitForExit: true,
                            cancellationToken);
                        if (!install.Success)
                        {
                            throw new InvalidOperationException(install.Message);
                        }
                    },
                    DeferModelSelection: !_useOllama.Checked,
                    AllowAutomaticModelFallback: false),
                _installationCancellation.Token);

            LmStudioRegistrationResult? lmStudio = null;
            if (_registerLmStudio.Checked
                && selectedModel is not null
                && outcome.Success
                && outcome.Code != "EXISTING_INTEGRATION_PRESERVED")
            {
                using var client = new LmStudioClient();
                lmStudio = await new LmStudioRegistrationService(
                        _paths,
                        stateStore,
                        client)
                    .ValidateAndEnableAsync(
                        selectedModel.Tag,
                        Path.GetFullPath(_lmStudioFile.Text),
                        _installationCancellation.Token);
            }

            _result.Text = BuildResultMessage(outcome, pullAndValidate, lmStudio);
        }
        catch (OperationCanceledException)
        {
            _result.Text = "Setup was cancelled safely. No unconfirmed provider was enabled; any prior validated route remains protected. Reopen guided setup whenever you are ready.";
        }
        catch (Exception exception)
        {
            _result.Text = "Setup did not complete. Invalid managed configuration was rolled back or left disabled.\n\n" + exception.Message;
        }
        finally
        {
            _installing = false;
            _next.Enabled = true;
            _next.Text = "Close";
            _cancel.Visible = false;
            _back.Visible = false;
            UpdateNavigation();
        }
    }

    private Control WelcomePage()
    {
        var panel = Page(
            "Private review, without the setup maze",
            "This helper connects Codex to an audited local Ollama or explicitly registered LM Studio model as an optional, read-only second opinion.",
            "It does not replace Codex, require an OpenAI API key, send telemetry, or expose filesystem, shell, Git, deployment, or messaging tools.");
        panel.Controls.Add(FeatureCard(
            "YOU STAY IN CONTROL",
            "Finish later, use an existing Ollama store, register an existing LM Studio GGUF, or approve one exact model download after reviewing its size and destination."));
        panel.Controls.Add(FeatureCard(
            "BUILT FOR GPU SHARING",
            "Concurrency stays at one. Pressure guards refuse work when GPU memory or Windows commit headroom is too low."));
        panel.Controls.Add(FeatureCard(
            "SAFE BY DEFAULT",
            "Ollama stays on 127.0.0.1, models unload after use, and existing Codex integration is preserved instead of replaced."));
        return panel;
    }

    private Control HardwarePage()
    {
        var gpu = _hardware.Gpus.OrderByDescending(item => item.DedicatedMemoryBytes).FirstOrDefault();
        var panel = Page(
            _recommendation.Model is null ? "Safe setup is still available" : "Your PC has a conservative model fit",
            "The recommendation is conservative so the reviewer does not crowd out emulators, graphics builds, or device work.");
        panel.Controls.Add(FeatureCard(
            "GPU",
            gpu is null
                ? "No supported dedicated GPU detected. CPU fallback is never automatic."
                : $"{gpu.Name} | {FormatGiB(gpu.DedicatedMemoryBytes)} VRAM | {gpu.AccelerationRoute}"));
        panel.Controls.Add(FeatureCard(
            "MEMORY",
            $"{FormatGiB(_hardware.Memory.TotalBytes)} installed | {FormatGiB(_hardware.Memory.AvailableBytes)} currently available"));
        panel.Controls.Add(FeatureCard(
            "RECOMMENDATION",
            _recommendation.Model is null
                ? "No safe dedicated-GPU fit was found. Finish setup later, or explicitly show CPU-safe catalog choices if system RAM has enough headroom."
                : $"{_recommendation.Model.Provider} | {_recommendation.Model.Tag} | {_recommendation.Explanation}"));
        return panel;
    }

    private Control SelectionPage()
    {
        var panel = Page(
            "Choose how models should be handled",
            "Finish later is the default. Every provider, model, size, hardware fit, and destination is shown before setup can acquire or validate anything.");

        panel.Controls.Add(FieldLabel("REQUIRED — SETUP PATH"));
        panel.Controls.Add(_setupLater);
        panel.Controls.Add(_useOllama);
        panel.Controls.Add(_registerLmStudio);
        panel.Controls.Add(_allowCpuFallback);

        panel.Controls.Add(FieldLabel("REQUIRED FOR MODEL SETUP — MODEL"));
        panel.Controls.Add(_modelChoice);

        panel.Controls.Add(FieldLabel("REQUIRED WHEN USING OLLAMA — MODEL STORAGE FOLDER"));
        var storageRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        storageRow.Controls.Add(_modelDirectory);
        storageRow.Controls.Add(_browse);
        panel.Controls.Add(storageRow);

        panel.Controls.Add(FieldLabel("REQUIRED WHEN USING LM STUDIO — EXISTING GGUF"));
        var lmStudioRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        lmStudioRow.Controls.Add(_lmStudioFile);
        lmStudioRow.Controls.Add(_browseLmStudio);
        panel.Controls.Add(lmStudioRow);

        var statusCard = new RoundedPanel
        {
            Width = 805,
            Height = 132,
            Padding = new Padding(17, 14, 17, 12),
            Margin = new Padding(0, 8, 0, 16),
            OutlineColor = Color.FromArgb(55, 65, 91)
        };
        var statusFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent
        };
        statusFlow.Controls.Add(_modelStatus);
        statusFlow.Controls.Add(_storageStatus);
        statusCard.Controls.Add(statusFlow);
        panel.Controls.Add(statusCard);
        var note = UiTheme.Label(
            "Base installation remains passive: Finish later downloads and loads nothing. Choose one model for this step; after setup, use Choose model again to add and validate more compatible models one at a time, each with its own named confirmation.",
            8.75F,
            UiTheme.Muted);
        note.MaximumSize = new Size(790, 0);
        note.Margin = new Padding(0, 10, 0, 0);
        panel.Controls.Add(note);
        return panel;
    }

    private Control ReviewPage()
    {
        var selected = (_modelChoice.SelectedItem as ModelChoiceOption)?.Model;
        var action = _useOllama.Checked
            ? $"{ModelProviders.Ollama} | {selected?.Tag ?? "no model"} | about {FormatGiB(selected?.ExpectedDownloadBytes ?? 0)} | destination: {_modelDirectory.Text}. No fallback model is allowed."
            : _registerLmStudio.Checked
                ? $"{ModelProviders.LmStudio} | {selected?.Tag ?? "no model"} | existing file: {_lmStudioFile.Text}. No model is downloaded or substituted."
                : "Install the helper and managed settings only; do not download, load, validate, or select a model.";
        var panel = Page(
            "Review exactly what will happen",
            action,
            "Existing config.toml and AGENTS.override.md files are never replaced. Updates use backups, managed markers, atomic writes, idempotent merges, and rollback.");
        _autoStart.Visible = _useOllama.Checked;
        _installOllama.Visible = _useOllama.Checked;
        panel.Controls.Add(_autoStart);
        panel.Controls.Add(_installOllama);
        panel.Controls.Add(_installReliabilityBaseline);

        var startupNote = UiTheme.Label(
            _useOllama.Checked
                ? "If automatic startup is off, the helper remains installed but local review requires manually starting Ollama after each sign-in."
                : _registerLmStudio.Checked
                    ? "LM Studio remains user-controlled. Start its loopback local server before Codex requests an LM Studio review."
                    : "No provider startup is configured while model setup is deferred.",
            8.75F,
            UiTheme.Warning);
        startupNote.MaximumSize = new Size(790, 0);
        startupNote.Margin = new Padding(0, 6, 0, 14);
        panel.Controls.Add(startupNote);
        panel.Controls.Add(FieldLabel("AGENTS.OVERRIDE.MD BEFORE / AFTER PREVIEW"));
        panel.Controls.Add(_reliabilityPreview);
        RefreshReliabilityPreview();
        return panel;
    }

    private Control ResultPage()
    {
        var panel = Page(
            _installing ? "Setting things up" : "Setup result",
            _installing
                ? "Please leave this window open. The passive path never loads a model; an explicit provider path uses only the exact model you confirmed."
                : "You can return to the dashboard for status, model setup, GPU controls, and help.");
        var card = new RoundedPanel
        {
            Width = 805,
            Height = 260,
            Padding = new Padding(22),
            Margin = new Padding(0, 10, 0, 0),
            OutlineColor = _installing ? UiTheme.Accent : UiTheme.Border
        };
        card.Controls.Add(_result);
        panel.Controls.Add(card);
        return panel;
    }

    private void BrowseForModelDirectory()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose a fixed local folder for Ollama models",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(_modelDirectory.Text) ? _modelDirectory.Text : string.Empty
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            _modelDirectory.Text = picker.SelectedPath;
        }
    }

    private void BrowseForLmStudioModel()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Choose an existing audited LM Studio GGUF",
            Filter = "GGUF model (*.gguf)|*.gguf",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            DereferenceLinks = true
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            _lmStudioFile.Text = picker.FileName;
        }
    }

    private void RefreshModelStatus()
    {
        var selected = _modelChoice.SelectedItem as ModelChoiceOption;
        if (selected is null)
        {
            _modelStatus.Text = _setupLater.Checked
                ? "NO MODEL SELECTED  |  Passive setup remains available."
                : "No audited model is available for this provider.";
            _modelStatus.ForeColor = UiTheme.Warning;
            _storageStatus.Text = "No disk space will be used by the passive setup path.";
            _storageStatus.ForeColor = UiTheme.Muted;
            return;
        }

        var model = selected.Model;
        var route = selected.RequiresCpuOptIn ? "CPU FALLBACK" : selected.Available ? "HARDWARE FIT" : "NOT A SAFE FIT";
        _modelStatus.Text = $"{route}  |  {model.Provider}  |  {model.Tag}  |  {FormatGiB(model.ExpectedDownloadBytes)}\n{selected.Reason}";
        _modelStatus.ForeColor = selected.Available
            ? selected.RequiresCpuOptIn ? UiTheme.Warning : UiTheme.Success
            : UiTheme.Danger;

        if (_setupLater.Checked)
        {
            _storageStatus.Text = "PASSIVE DEFAULT  |  No model is downloaded, loaded, validated, or selected. You can finish model setup later.";
            _storageStatus.ForeColor = UiTheme.Cyan;
            return;
        }

        if (_registerLmStudio.Checked)
        {
            var file = GetLmStudioFileAssessment(model, _lmStudioFile.Text);
            _storageStatus.Text = file.Message;
            _storageStatus.ForeColor = file.Ready ? UiTheme.Success : UiTheme.Warning;
            return;
        }

        var storage = GetStorageAssessment(model, _modelDirectory.Text);
        var manifest = IsSelectedModelPresent() ? "matching manifest found" : "model may require download";
        _storageStatus.Text = $"{storage.Message}\n{manifest}; required including reserve: {FormatGiB(storage.TotalRequiredBytes)}.";
        _storageStatus.ForeColor = storage.Ready ? UiTheme.Success : UiTheme.Warning;
    }

    private void RefreshSetupPath()
    {
        if (!_setupLater.Checked && !_useOllama.Checked && !_registerLmStudio.Checked)
        {
            return;
        }

        _modelDirectory.Enabled = _useOllama.Checked;
        _browse.Enabled = _useOllama.Checked;
        _lmStudioFile.Enabled = _registerLmStudio.Checked;
        _browseLmStudio.Enabled = _registerLmStudio.Checked;
        _allowCpuFallback.Enabled = !_setupLater.Checked && _gpuCompatibleModels.Count == 0;
        if (!_allowCpuFallback.Enabled && _allowCpuFallback.Checked)
        {
            _allowCpuFallback.Checked = false;
        }
        _autoStart.Enabled = _useOllama.Checked;
        _installOllama.Enabled = _useOllama.Checked;
        RefreshModelChoices();
        RefreshModelStatus();
        RefreshReliabilityPreview();
        UpdateNavigation();
    }

    private void RefreshModelChoices()
    {
        var prior = (_modelChoice.SelectedItem as ModelChoiceOption)?.Model.Tag;
        var provider = _registerLmStudio.Checked ? ModelProviders.LmStudio : ModelProviders.Ollama;
        var options = _catalog.Models
            .Where(model => string.Equals(ModelProviders.Normalize(model.Provider), provider, StringComparison.Ordinal))
            .Select(BuildModelChoice)
            .OrderByDescending(option => option.Available)
            .ThenBy(option => option.Model.ParameterBillions)
            .ToArray();

        _modelChoice.BeginUpdate();
        try
        {
            _modelChoice.Items.Clear();
            foreach (var option in options)
            {
                _modelChoice.Items.Add(option);
            }

            var desired = options.FirstOrDefault(option => string.Equals(option.Model.Tag, prior, StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault(option => option.Available
                    && string.Equals(option.Model.Tag, _recommendation.Model?.Tag, StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault(option => option.Available)
                ?? options.FirstOrDefault();
            if (desired is not null)
            {
                _modelChoice.SelectedItem = desired;
            }
        }
        finally
        {
            _modelChoice.EndUpdate();
        }
    }

    private ModelChoiceOption BuildModelChoice(ModelCatalogEntry model)
    {
        if (!model.CommercialUseAllowed)
        {
            return new ModelChoiceOption(
                model,
                false,
                false,
                $"Unavailable: the bundled setup does not acquire this model without a separate license workflow. Requires {model.MinimumDedicatedVramGiB:F1} GiB VRAM and {model.MinimumSystemRamGiB:F0} GiB system RAM.");
        }

        if (_gpuCompatibleModels.Contains(ModelKey(model)))
        {
            return new ModelChoiceOption(
                model,
                true,
                false,
                $"Conservative GPU fit. Catalog minimum: {model.MinimumDedicatedVramGiB:F1} GiB dedicated VRAM and {model.MinimumSystemRamGiB:F0} GiB system RAM; {model.IntendedTasks}");
        }

        if (_gpuCompatibleModels.Count == 0
            && _allowCpuFallback.Checked
            && IsCpuFallbackCompatible(model))
        {
            return new ModelChoiceOption(
                model,
                true,
                true,
                $"Explicit CPU-safe fallback. This model is catalogued as CPU-reasonable and current RAM has conservative headroom, but inference may be slow and reduce responsiveness; {model.IntendedTasks}");
        }

        var bestGpu = _hardware.Gpus
            .Where(gpu => !gpu.IsIntegrated)
            .OrderByDescending(gpu => gpu.DedicatedMemoryBytes)
            .FirstOrDefault();
        var detected = bestGpu is null ? "no supported dedicated GPU was detected" : $"the largest detected GPU reports {FormatGiB(bestGpu.DedicatedMemoryBytes)} VRAM";
        var cpuHint = _gpuCompatibleModels.Count == 0
            && model.CpuFallbackReasonable
            && IsCpuFallbackCompatible(model)
            ? " Enable the explicit CPU-safe option to make this slower fallback selectable."
            : string.Empty;
        return new ModelChoiceOption(
            model,
            false,
            false,
            $"Unavailable: needs at least {model.MinimumDedicatedVramGiB:F1} GiB dedicated VRAM and {model.MinimumSystemRamGiB:F0} GiB system RAM; {detected}.{cpuHint}");
    }

    private bool IsCpuFallbackCompatible(ModelCatalogEntry model)
    {
        const decimal gib = 1024m * 1024m * 1024m;
        var totalRam = _hardware.Memory.TotalBytes / gib;
        var availableRam = _hardware.Memory.AvailableBytes / gib;
        return _hardware.OperatingSystem.IsSupported
            && string.Equals(_hardware.OperatingSystem.Architecture, "X64", StringComparison.OrdinalIgnoreCase)
            && model.CpuFallbackReasonable
            && model.MinimumSystemRamGiB <= totalRam
            && model.MinimumSystemRamGiB * 0.50m <= availableRam;
    }

    private bool IsModelPathReady()
    {
        if (_setupLater.Checked)
        {
            return true;
        }

        if (_modelChoice.SelectedItem is not ModelChoiceOption { Available: true } selected)
        {
            return false;
        }

        return _registerLmStudio.Checked
            ? GetLmStudioFileAssessment(selected.Model, _lmStudioFile.Text).Ready
            : _useOllama.Checked && GetStorageAssessment(selected.Model, _modelDirectory.Text).Ready;
    }

    private StorageAssessment GetStorageAssessment(ModelCatalogEntry model, string directory)
    {
        var required = RequiredModelBytes(model);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new StorageAssessment(false, checked(required + 10UL * GiB), "Choose a fixed local model folder. No destination has been selected.");
        }

        try
        {
            var fullPath = Path.GetFullPath(directory);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return new StorageAssessment(false, required, "The selected destination has no local drive root.");
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return new StorageAssessment(false, required, $"{root} is not ready. Connect or unlock the drive before continuing.");
            }

            var volume = _hardware.Volumes.FirstOrDefault(item => string.Equals(item.RootPath, root, StringComparison.OrdinalIgnoreCase));
            var isFixed = drive.DriveType == DriveType.Fixed && (volume?.IsFixed ?? true);
            var reserve = volume?.IsSystem == true
                ? Math.Max(20UL * GiB, (ulong)drive.TotalSize / 10)
                : 10UL * GiB;
            var totalRequired = checked(required + reserve);
            var free = (ulong)drive.AvailableFreeSpace;
            var media = volume?.MediaType.ToString().ToUpperInvariant() ?? drive.DriveType.ToString().ToUpperInvariant();
            var ready = isFixed && free >= totalRequired;
            var status = isFixed ? "FIXED LOCAL" : drive.DriveType.ToString().ToUpperInvariant();
            var message = $"{status} {media}  |  {root}  |  actual free: {FormatGiB(free)}";
            if (!isFixed)
            {
                message += "  |  unavailable: model acquisition requires fixed local storage.";
            }
            else if (!ready)
            {
                message += $"  |  unavailable: {FormatGiB(totalRequired)} is required including safety reserve.";
            }

            return new StorageAssessment(ready, totalRequired, message);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return new StorageAssessment(false, required, "The selected model destination cannot be inspected safely: " + exception.Message);
        }
    }

    private static FileAssessment GetLmStudioFileAssessment(ModelCatalogEntry model, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new FileAssessment(false, "Choose the exact existing GGUF already indexed by LM Studio. Nothing will be downloaded.");
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                return new FileAssessment(false, "The selected GGUF does not exist.");
            }

            var info = new FileInfo(fullPath);
            var expectedSuffix = model.IndexedModelPath?.Replace('/', Path.DirectorySeparatorChar);
            var indexed = !string.IsNullOrWhiteSpace(expectedSuffix)
                && fullPath.EndsWith(Path.DirectorySeparatorChar + expectedSuffix, StringComparison.OrdinalIgnoreCase);
            if (!indexed)
            {
                return new FileAssessment(false, "The selected file is not the exact LM Studio catalog path for this audited model key.");
            }

            if ((ulong)info.Length != model.ExpectedDownloadBytes)
            {
                return new FileAssessment(false, $"The selected GGUF is {FormatGiB((ulong)Math.Max(0, info.Length))}; the audited model expects {FormatGiB(model.ExpectedDownloadBytes)} before full digest validation.");
            }

            var drive = new DriveInfo(Path.GetPathRoot(fullPath)!);
            var driveStatus = drive.IsReady
                ? $"{drive.DriveType.ToString().ToUpperInvariant()} {drive.Name} | actual free: {FormatGiB((ulong)drive.AvailableFreeSpace)}"
                : $"{drive.Name} is not ready";
            return new FileAssessment(true, $"EXISTING GGUF  |  {FormatGiB((ulong)info.Length)}  |  {driveStatus}\nFull identity and SHA-256 are verified only after the final named confirmation.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return new FileAssessment(false, "The selected GGUF cannot be inspected safely: " + exception.Message);
        }
    }

    private static ulong RequiredModelBytes(ModelCatalogEntry model)
    {
        var catalogMinimum = (ulong)Math.Ceiling(model.MinimumFreeDiskGiB) * GiB;
        var temporaryOverhead = (ulong)Math.Ceiling(model.ExpectedDownloadBytes * 2.15m);
        return Math.Max(catalogMinimum, temporaryOverhead);
    }

    private static string ModelKey(ModelCatalogEntry model)
        => ModelProviders.Normalize(model.Provider) + "\0" + model.Tag;

    private bool IsSelectedModelPresent()
    {
        var selected = (_modelChoice.SelectedItem as ModelChoiceOption)?.Model;
        if (selected is null
            || !string.Equals(ModelProviders.Normalize(selected.Provider), ModelProviders.Ollama, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(_modelDirectory.Text))
        {
            return false;
        }

        return OllamaAutoStartManager.IsSelectedModelManifestPresent(new InstallationState
        {
            SelectedModel = selected.Tag,
            ModelStorageLocation = _modelDirectory.Text
        });
    }

    private void RefreshReliabilityPreview()
    {
        try
        {
            var selectedModel = _useOllama.Checked
                ? (_modelChoice.SelectedItem as ModelChoiceOption)?.Model
                : null;
            var tier = selectedModel is not null
                ? ModelSelector.GetHardwareTier(selectedModel)
                : _initialState?.SelectedModel is not null
                    ? _initialState.HardwareTier
                    : HardwareTier.NoModel;
            _agentsPreview = new AgentsOverrideManager().PreviewInstall(
                _paths,
                tier,
                _installReliabilityBaseline.Checked,
                installLocalGpuGuidance: new CodexConfigManager().InspectOwnership(_paths) != CodexIntegrationOwnership.ExternalUnmarked);
            _reliabilityPreview.Text = _agentsPreview.Diff;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _agentsPreview = null;
            _reliabilityPreview.Text = "Preview unavailable. Setup will not change a malformed managed instruction file.\r\n\r\n" + exception.Message;
        }

        UpdateNavigation();
    }

    private static string BuildResultMessage(
        InstallationOutcome outcome,
        bool pulledAndValidated,
        LmStudioRegistrationResult? lmStudio)
    {
        if (outcome.Code == "EXISTING_INTEGRATION_PRESERVED")
        {
            return "An existing unmarked local_gpu_reviewer entry was detected and protected.\n\nIt was not inspected or tested. The helper did not replace its Codex entry, add invocation guidance, change its Ollama startup, move its models, or select a model. Packaged pause, lock, pressure, unload, and model controls do not apply to that external entry.\n\nNo helper-owned MCP entry was installed, so no helper-owned Codex restart is claimed.";
        }

        var startup = lmStudio is not null
            ? "LM Studio remains a separate loopback provider. This setup path did not install, start, or configure Ollama."
            : outcome.OllamaStartup switch
            {
                { AutoStartConfigured: true } => "Ollama automatic startup is configured for this Windows user. The startup helper checks for an existing loopback endpoint or process before starting anything.",
                { Code: "EXTERNAL_AUTOSTART_UNVERIFIED" } => "Another Ollama startup artifact was preserved to avoid creating a duplicate, but its target and next-login behavior were not verified. Review/remove it or use manual startup before enabling local review.",
                _ when !outcome.State.Preferences.AutoStartOllama => "Automatic startup was declined. Start Ollama manually after each sign-in before using local review.",
                null => "Automatic startup will be configured after a model folder is selected. No startup entry was created during deferred model setup.",
                _ => "Automatic startup has not passed verification. The helper remains disabled until startup, loopback, and model-path checks succeed."
            };
        var model = lmStudio is not null
            ? lmStudio.Success
                ? $"Provider: {lmStudio.Provider}. Model: {lmStudio.Model}. Validation result: {lmStudio.Code}. The helper-created instance was unloaded: {lmStudio.Unloaded}."
                : $"LM Studio registration was refused safely ({lmStudio.Code}): {lmStudio.Message} No fallback model was downloaded or selected."
            : pulledAndValidated
            ? $"Model: {outcome.State.SelectedModel ?? "none"}. Validation result: {outcome.Code}."
            : $"Model setup is still required. Selected model: {outcome.State.SelectedModel ?? "none"}. No model was downloaded or loaded by setup.";
        var restart = IntegrationOwnership.IsManagedByHelper(outcome.State)
            ? "Restart Codex once so it loads the managed MCP integration."
            : "The existing MCP integration remained protected.";
        return $"{outcome.Message}\n\n{model}\n\n{startup}\n\n{restart}\n\nThis build is unsigned, so Windows may show a SmartScreen warning.";
    }

    private static FlowLayoutPanel Page(string title, params string[] lines)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = UiTheme.Canvas,
            Padding = new Padding(2)
        };
        var heading = UiTheme.Label(title, 20F, UiTheme.Text, FontStyle.Bold);
        heading.Margin = new Padding(0, 0, 0, 10);
        panel.Controls.Add(heading);
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var label = UiTheme.Label(line, 9.5F, UiTheme.Muted);
            label.MaximumSize = new Size(790, 0);
            label.Margin = new Padding(0, 0, 0, 9);
            panel.Controls.Add(label);
        }

        return panel;
    }

    private static Control FeatureCard(string title, string body)
    {
        var card = new RoundedPanel
        {
            Width = 805,
            Height = 82,
            Padding = new Padding(17, 13, 17, 11),
            Margin = new Padding(0, 7, 0, 5)
        };
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent
        };
        var heading = UiTheme.Label(title, 8.25F, UiTheme.Cyan, FontStyle.Bold);
        heading.Margin = new Padding(0, 0, 0, 5);
        var copy = UiTheme.Label(body, 9.25F, UiTheme.Text);
        copy.MaximumSize = new Size(755, 0);
        flow.Controls.Add(heading);
        flow.Controls.Add(copy);
        card.Controls.Add(flow);
        return card;
    }

    private static Label FieldLabel(string text)
    {
        var label = UiTheme.SectionLabel(text);
        label.ForeColor = UiTheme.Warning;
        label.Margin = new Padding(0, 12, 0, 7);
        return label;
    }

    private void SetHelp(Control control, string text)
    {
        _toolTip.SetToolTip(control, text);
        control.AccessibleDescription = text;
    }

    private static async Task<bool> IsOllamaReachableAsync()
    {
        try
        {
            using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"));
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
        var configured = Environment.GetEnvironmentVariable(
            "OLLAMA_MODELS",
            EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var standard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ollama",
            "models");
        return Directory.Exists(standard) ? standard : null;
    }

    private static string FormatGiB(ulong bytes)
        => $"{bytes / 1024d / 1024d / 1024d:F1} GiB";

    private sealed record ModelChoiceOption(
        ModelCatalogEntry Model,
        bool Available,
        bool RequiresCpuOptIn,
        string Reason)
    {
        public override string ToString()
        {
            var status = Available ? RequiresCpuOptIn ? "CPU" : "Fits" : "Unavailable";
            return $"{status}  |  {Model.Provider}  |  {Model.Tag}  |  {FormatGiB(Model.ExpectedDownloadBytes)}";
        }
    }

    private sealed record StorageAssessment(bool Ready, ulong TotalRequiredBytes, string Message);
    private sealed record FileAssessment(bool Ready, string Message);
}
