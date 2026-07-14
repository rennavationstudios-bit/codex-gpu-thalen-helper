using ThalenHelper.Core;

namespace ThalenHelper.ControlCenter;

public sealed class SetupWizardForm : Form
{
    private readonly ProductPaths _paths;
    private readonly HardwareProfile _hardware;
    private readonly ModelRecommendation _recommendation;
    private readonly StorageRecommendation? _storage;
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
    private readonly TextBox _modelDirectory = UiTheme.TextBox(590);
    private readonly ComboBox _modelChoice = UiTheme.ComboBox(480);
    private readonly RadioButton _setupLater = UiTheme.RadioButton(
        "Install the helper now and finish model setup later",
        true);
    private readonly RadioButton _useModelNow = UiTheme.RadioButton(
        "Verify or acquire this selected model now");
    private readonly CheckBox _autoStart = UiTheme.CheckBox(
        "Start Ollama automatically after I sign in to Windows",
        true);
    private readonly CheckBox _installOllama = UiTheme.CheckBox(
        "Install the current official signed Ollama release if it is missing",
        true);
    private readonly CheckBox _installReliabilityBaseline = UiTheme.CheckBox(
        "Add the optional sanitized Codex reliability baseline");
    private readonly Label _modelStatus = UiTheme.Label(string.Empty, 9F, UiTheme.Muted);
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
        _hardware = new HardwareDetector().Detect();
        var catalog = new ModelCatalogService().LoadBundled();
        _recommendation = new ModelSelector().Recommend(_hardware, catalog, false);
        _storage = _recommendation.Model is null
            ? null
            : new StorageSelector().Recommend(_hardware, _recommendation.Model);

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
                .FirstOrDefault(model => string.Equals(
                    model.Tag,
                    _recommendation.Model.Tag,
                    StringComparison.OrdinalIgnoreCase));
        }

        _modelDirectory.Text = GetExistingOllamaModelsPath() ?? _storage?.ModelDirectory ?? string.Empty;
        _autoStart.Checked = autoStartPreference ?? true;
        _modelStatus.MaximumSize = new Size(780, 0);
        _result.MaximumSize = new Size(790, 0);

        Text = "Set up Codex GPU Thalen Helper";
        Size = new Size(960, 790);
        UiTheme.Apply(this, new Size(900, 720));
        BuildShell();
        WireEvents();
        RefreshModelStatus();
        _page = startAtModelSelection ? 2 : 0;
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
        _setupLater.CheckedChanged += (_, _) => UpdateNavigation();
        _useModelNow.CheckedChanged += (_, _) => UpdateNavigation();
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
        SetHelp(_browse, "Chooses a fixed local folder for Ollama models. Network and removable locations are rejected during validation.");
        SetHelp(_modelChoice, "Lists only models allowed by the bundled catalog and safe for the detected hardware tier.");
        SetHelp(_modelDirectory, "The folder Ollama should use for model files. An existing OLLAMA_MODELS value is preserved as the initial choice.");
        SetHelp(_setupLater, "Installs and configures the helper without downloading or running a model. Local review stays disabled until model setup is completed.");
        SetHelp(_useModelNow, "Uses only the named selected model. After confirmation, Ollama verifies its inventory and may repair or download that same model before bounded validation.");
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
        _next.Enabled = _page != 3 || _agentsPreview is not null;
        _next.Text = _page switch
        {
            3 when _useModelNow.Checked => "Confirm & finish",
            3 => "Install helper only",
            4 => "Close",
            _ => "Next"
        };
        var help = _page switch
        {
            3 when _useModelNow.Checked => "Shows a final named confirmation, then lets Ollama verify or acquire only that selected model, validates it, and unloads it.",
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

        var selectedModel = _modelChoice.SelectedItem as ModelCatalogEntry ?? _recommendation.Model;
        if (_useModelNow.Checked && selectedModel is null)
        {
            MessageBox.Show(
                this,
                "No supported model is selected. Choose Set up later or select a model.",
                ProductInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (_useModelNow.Checked && selectedModel is not null)
        {
            var manifestHint = IsSelectedModelPresent()
                ? "A matching manifest was found in the selected folder, but Ollama's inventory remains authoritative."
                : "No matching manifest was found in the selected folder.";
            var action = $"Selected model: {selectedModel.Tag} (about {FormatGiB(selectedModel.ExpectedDownloadBytes)}).\n\n"
                + $"{manifestHint} Ollama may repair or download this same selected model if its inventory is missing or inconsistent. "
                + "Setup will not switch to or download a different fallback model. It will validate the selected model locally and unload it when finished.";
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

        _next.Enabled = false;
        _back.Enabled = false;
        _installing = true;
        _page = 4;
        _result.Text = "Applying the reviewed choices...";
        RenderPage();
        try
        {
            var pullAndValidate = _useModelNow.Checked;
            if (pullAndValidate && selectedModel is not null)
            {
                _result.Text = $"Using local_gpu_reviewer with Ollama and {selectedModel.Tag} for bounded validation. This model will be unloaded afterward.";
            }

            var outcome = await new InstallationManager().ConfigureAsync(
                new InstallationOptions(
                    _paths,
                    selectedModel?.Tag,
                    string.IsNullOrWhiteSpace(_modelDirectory.Text) ? null : _modelDirectory.Text,
                    false,
                    false,
                    _autoStart.Checked,
                    pullAndValidate,
                    InstallReliabilityBaseline: _installReliabilityBaseline.Checked,
                    ExpectedAgentsSourceSha256: _agentsPreview?.SourceSha256,
                    ExpectedAgentsPlannedSha256: _agentsPreview?.PlannedSha256,
                    EnsureOllamaInstalledAsync: async cancellationToken =>
                    {
                        if (!_installOllama.Checked || await IsOllamaReachableAsync())
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
                    AllowAutomaticModelFallback: false),
                _installationCancellation.Token);

            _result.Text = BuildResultMessage(outcome, pullAndValidate);
        }
        catch (OperationCanceledException)
        {
            _result.Text = "Setup was cancelled safely. The managed Codex entry remains disabled. Reopen guided setup whenever you are ready.";
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
            "This helper connects Codex to a local Ollama model as an optional, read-only second opinion.",
            "It does not replace Codex, require an OpenAI API key, send telemetry, or expose filesystem, shell, Git, deployment, or messaging tools.");
        panel.Controls.Add(FeatureCard(
            "YOU STAY IN CONTROL",
            "Choose an existing model, approve a model download, or install the helper now and decide later."));
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
            "Your PC looks ready",
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
                ? "No safe model fit was found. You can still install the helper in disabled mode."
                : $"{_recommendation.Model.Tag} | {_recommendation.Explanation}"));
        return panel;
    }

    private Control SelectionPage()
    {
        var panel = Page(
            "Choose your model path",
            "Point to an existing Ollama folder or use the recommended fixed local folder. Nothing is downloaded on this step.");

        panel.Controls.Add(FieldLabel("MODEL"));
        panel.Controls.Add(_modelChoice);
        panel.Controls.Add(FieldLabel("MODEL STORAGE FOLDER"));
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

        var statusCard = new RoundedPanel
        {
            Width = 805,
            Height = 84,
            Padding = new Padding(17, 14, 17, 12),
            Margin = new Padding(0, 8, 0, 16),
            OutlineColor = Color.FromArgb(55, 65, 91)
        };
        statusCard.Controls.Add(_modelStatus);
        panel.Controls.Add(statusCard);

        panel.Controls.Add(FieldLabel("WHAT SHOULD SETUP DO?"));
        panel.Controls.Add(_setupLater);
        panel.Controls.Add(_useModelNow);
        var note = UiTheme.Label(
            "Network and removable model folders are rejected. If you choose Use this model now, setup asks again before any download or inference.",
            8.75F,
            UiTheme.Muted);
        note.MaximumSize = new Size(790, 0);
        note.Margin = new Padding(0, 10, 0, 0);
        panel.Controls.Add(note);
        return panel;
    }

    private Control ReviewPage()
    {
        var selected = _modelChoice.SelectedItem as ModelCatalogEntry ?? _recommendation.Model;
        var action = _useModelNow.Checked
            ? $"Verify or acquire only {selected?.Tag ?? "the selected model"} (about {FormatGiB(selected?.ExpectedDownloadBytes ?? 0)}). Ollama may repair or download that same model if inventory disagrees with local files."
            : "Install the helper and managed settings only; do not download or load a model.";
        var panel = Page(
            "Review exactly what will happen",
            action,
            "Existing config.toml and AGENTS.override.md files are never replaced. Updates use backups, managed markers, atomic writes, idempotent merges, and rollback.");
        panel.Controls.Add(_autoStart);
        panel.Controls.Add(_installOllama);
        panel.Controls.Add(_installReliabilityBaseline);

        var startupNote = UiTheme.Label(
            "If automatic startup is off, the helper remains installed but local review requires manually starting Ollama after each sign-in.",
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
                ? "Please leave this window open. The helper will not preload a model unless you explicitly selected Use this model now."
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

    private void RefreshModelStatus()
    {
        var selected = _modelChoice.SelectedItem as ModelCatalogEntry ?? _recommendation.Model;
        if (selected is null)
        {
            _modelStatus.Text = "No supported model was recommended. Setup can continue in disabled mode.";
            _modelStatus.ForeColor = UiTheme.Warning;
            _useModelNow.Enabled = false;
            _setupLater.Checked = true;
            return;
        }

        _useModelNow.Enabled = true;
        if (IsSelectedModelPresent())
        {
            _modelStatus.Text = $"MANIFEST FOUND  |  {selected.Tag}  |  about {FormatGiB(selected.ExpectedDownloadBytes)}\nOllama inventory is authoritative. After confirmation, Ollama may repair or download this same model if inventory disagrees.";
            _modelStatus.ForeColor = UiTheme.Success;
        }
        else
        {
            _modelStatus.Text = $"NOT FOUND HERE  |  {selected.Tag}  |  about {FormatGiB(selected.ExpectedDownloadBytes)}\nSet up later downloads nothing. Use this model now asks for confirmation before downloading.";
            _modelStatus.ForeColor = UiTheme.Muted;
        }
    }

    private bool IsSelectedModelPresent()
    {
        var selected = _modelChoice.SelectedItem as ModelCatalogEntry ?? _recommendation.Model;
        if (selected is null || string.IsNullOrWhiteSpace(_modelDirectory.Text))
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
            var selectedModel = _modelChoice.SelectedItem as ModelCatalogEntry ?? _recommendation.Model;
            var tier = selectedModel is null
                ? HardwareTier.NoModel
                : ModelSelector.GetHardwareTier(selectedModel);
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

    private static string BuildResultMessage(InstallationOutcome outcome, bool pulledAndValidated)
    {
        if (outcome.Code == "EXISTING_INTEGRATION_PRESERVED")
        {
            return "An existing unmarked local_gpu_reviewer entry was detected and protected.\n\nIt was not inspected or tested. The helper did not replace its Codex entry, add invocation guidance, change its Ollama startup, move its models, or select a model. Packaged pause, lock, pressure, unload, and model controls do not apply to that external entry.\n\nNo helper-owned MCP entry was installed, so no helper-owned Codex restart is claimed.";
        }

        var startup = outcome.OllamaStartup switch
        {
            { AutoStartConfigured: true } => "Ollama automatic startup is configured for this Windows user. The startup helper checks for an existing loopback endpoint or process before starting anything.",
            { Code: "EXTERNAL_AUTOSTART_UNVERIFIED" } => "Another Ollama startup artifact was preserved to avoid creating a duplicate, but its target and next-login behavior were not verified. Review/remove it or use manual startup before enabling local review.",
            _ when !outcome.State.Preferences.AutoStartOllama => "Automatic startup was declined. Start Ollama manually after each sign-in before using local review.",
            null => "Automatic startup will be configured after a model folder is selected. No startup entry was created during deferred model setup.",
            _ => "Automatic startup has not passed verification. The helper remains disabled until startup, loopback, and model-path checks succeed."
        };
        var model = pulledAndValidated
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
}
