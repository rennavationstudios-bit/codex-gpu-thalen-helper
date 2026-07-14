using ThalenHelper.Core;

namespace ThalenHelper.ControlCenter;

internal sealed class ModelSelectionDialog : Form
{
    private readonly DarkToolTip _toolTip = UiTheme.ToolTip();
    private readonly ComboBox _models = UiTheme.ComboBox(500);
    private readonly CheckBox _license = UiTheme.CheckBox("I accept the model's separate restrictive license when required.");

    public ModelSelectionDialog(IReadOnlyList<ModelCatalogEntry> models)
    {
        Text = "Choose a local reviewer model";
        UiTheme.Apply(this, new Size(620, 410));
        Size = new Size(660, 450);
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(24);
        foreach (var model in models)
        {
            _models.Items.Add(model);
        }

        _models.DisplayMember = nameof(ModelCatalogEntry.Tag);
        _models.Format += (_, args) =>
        {
            if (args.ListItem is ModelCatalogEntry model)
            {
                args.Value = $"{model.Tag}  •  {FormatBytes(model.ExpectedDownloadBytes)} download";
            }
        };
        if (_models.Items.Count > 0)
        {
            _models.SelectedIndex = 0;
        }

        var title = UiTheme.Label("Choose a model", 20F, UiTheme.Text, FontStyle.Bold);
        var copy = UiTheme.Label("Only models within the conservative limit for this GPU are shown. Nothing downloads until you confirm on the next screen.", 9.5F, UiTheme.Muted);
        copy.MaximumSize = new Size(560, 0);
        copy.Margin = new Padding(0, 4, 0, 18);
        var ok = UiTheme.Button("Continue", AppButtonStyle.Primary);
        ok.DialogResult = DialogResult.OK;
        var cancel = UiTheme.Button("Cancel", AppButtonStyle.Quiet);
        cancel.DialogResult = DialogResult.Cancel;
        SetHelp(ok, "Continues to a separate confirmation. It does not download or load the selected model yet.");
        SetHelp(cancel, "Closes this window without changing the selected model or downloading anything.");
        var footer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 58,
            BackColor = UiTheme.Canvas
        };
        footer.Controls.Add(ok);
        footer.Controls.Add(cancel);
        var content = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Canvas
        };
        content.Controls.Add(title);
        content.Controls.Add(copy);
        content.Controls.Add(UiTheme.SectionLabel("HARDWARE-SAFE MODELS"));
        content.Controls.Add(_models);
        content.Controls.Add(_license);
        Controls.Add(content);
        Controls.Add(footer);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public ModelCatalogEntry? SelectedModel => _models.SelectedItem as ModelCatalogEntry;
    public bool AcceptRestrictedLicense => _license.Checked;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void SetHelp(Control control, string help)
    {
        control.AccessibleDescription = help;
        _toolTip.SetToolTip(control, help);
    }

    private static string FormatBytes(ulong bytes) => $"{bytes / 1024d / 1024d / 1024d:F1} GiB";
}

internal sealed class ReliabilityBaselineDialog : Form
{
    private readonly ProductPaths _paths;
    private readonly HardwareTier _tier;
    private readonly bool _installLocalGpuGuidance;
    private readonly CheckBox _install;
    private readonly TextBox _diff;
    private readonly Button _apply;
    private readonly DarkToolTip _toolTip = UiTheme.ToolTip();

    public ReliabilityBaselineDialog(
        ProductPaths paths,
        HardwareTier tier,
        bool installed,
        bool installLocalGpuGuidance)
    {
        _paths = paths;
        _tier = tier;
        _installLocalGpuGuidance = installLocalGpuGuidance;
        Text = "Optional Codex reliability baseline";
        UiTheme.Apply(this, new Size(900, 650));
        Size = new Size(980, 720);
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(24);
        _install = UiTheme.CheckBox("Install the sanitized managed reliability baseline", installed);
        _diff = UiTheme.TextBox();
        _diff.Multiline = true;
        _diff.ReadOnly = true;
        _diff.ScrollBars = ScrollBars.Both;
        _diff.WordWrap = false;
        _diff.Dock = DockStyle.Fill;
        _diff.Font = new Font("Cascadia Mono", 9F);
        _apply = UiTheme.Button("Apply reviewed diff", AppButtonStyle.Primary);
        _apply.DialogResult = DialogResult.OK;
        var cancel = UiTheme.Button("Cancel", AppButtonStyle.Quiet);
        cancel.DialogResult = DialogResult.Cancel;
        SetHelp(_apply, "Applies exactly the displayed managed-section diff after rechecking the source and planned hashes. A timestamped backup is retained when a file changes.");
        SetHelp(cancel, "Closes the preview without changing AGENTS.override.md.");
        var header = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Top,
            Height = 115,
            BackColor = UiTheme.Canvas
        };
        header.Controls.Add(UiTheme.Label("Reliability baseline", 20F, UiTheme.Text, FontStyle.Bold));
        var explanation = UiTheme.Label("Review the complete before/after diff. Existing instructions are never replaced; only the marked helper section can change.", 9.5F, UiTheme.Muted);
        explanation.MaximumSize = new Size(850, 0);
        explanation.Margin = new Padding(0, 5, 0, 12);
        header.Controls.Add(explanation);
        header.Controls.Add(_install);
        var footer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 58,
            BackColor = UiTheme.Canvas
        };
        footer.Controls.Add(_apply);
        footer.Controls.Add(cancel);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void SetHelp(Control control, string help)
    {
        control.AccessibleDescription = help;
        _toolTip.SetToolTip(control, help);
    }

    private void RefreshPreview()
    {
        try
        {
            Preview = new AgentsOverrideManager().PreviewInstall(
                _paths,
                _tier,
                _install.Checked,
                _installLocalGpuGuidance);
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
