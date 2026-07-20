using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ThalenHelper.ControlCenter;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class ControlCenterUiTests
{
    [Theory]
    [InlineData("qwythos-9b-claude-mythos-5-1m", "Qwythos 9B")]
    [InlineData("qwen3:8b", "Qwen3 8B")]
    [InlineData("qwen3:14b", "Qwen3 14B")]
    [InlineData("qwen3-coder:30b", "Qwen3 Coder 30B")]
    public void ModelNamesAreHumanReadable(string source, string expected)
        => Assert.Equal(expected, MainForm.DisplayModel(source));

    [Fact]
    public void RoundedControlsUseAntialiasedPaintingWithoutBinaryRegions()
    {
        RunSta(() =>
        {
            using var button = UiTheme.Button("Test reviewer");
            using var panel = new RoundedPanel { Size = new Size(240, 120) };
            using var toggle = UiTheme.Toggle("Local reviews");
            button.CreateControl();
            panel.CreateControl();
            toggle.CreateControl();

            Assert.Null(button.Region);
            Assert.Null(panel.Region);
            Assert.Null(toggle.Region);
            Assert.Equal(Color.Transparent, toggle.BackColor);
            Assert.False(button.UseMnemonic);

            using GraphicsPath path = RoundedPanel.RoundedRectangle(new Rectangle(0, 0, 24, 18), 100, 1F);
            var bounds = path.GetBounds();
            Assert.InRange(bounds.Left, 0.5F, 1.5F);
            Assert.InRange(bounds.Top, 0.5F, 1.5F);
            Assert.InRange(bounds.Right, 22.5F, 23.5F);
            Assert.InRange(bounds.Bottom, 16.5F, 17.5F);
        });
    }

    [Fact]
    public void RoundedButtonsRenderLiteralAmpersandsInsteadOfMnemonicPrefixes()
    {
        RunSta(() =>
        {
            using var button = UiTheme.Button("Models & storage");

            Assert.Equal("Models & storage", button.Text);
            Assert.False(button.UseMnemonic);
            Assert.True(RoundedButton.TextDrawingFlags.HasFlag(TextFormatFlags.NoPrefix));
        });
    }

    [Fact]
    public void AdvancedMenuGlyphHasNoRectangularSurfaceAcrossTransparentAncestors()
    {
        RunSta(() =>
        {
            using var parent = new GradientPanel
            {
                Size = new Size(180, 100),
                GradientStart = Color.FromArgb(42, 32, 82),
                GradientEnd = Color.FromArgb(17, 22, 31)
            };
            using var transparentHost = new FlowLayoutPanel
            {
                Size = new Size(100, 60),
                Location = new Point(40, 20),
                BackColor = Color.Transparent,
                Padding = new Padding(10)
            };
            using var glyph = UiTheme.GlyphButton("⋯");
            glyph.Size = new Size(38, 30);
            glyph.Margin = new Padding(0);
            transparentHost.Controls.Add(glyph);
            parent.Controls.Add(transparentHost);
            CreateControls(parent);

            using var baseline = new Bitmap(parent.Width, parent.Height);
            var glyphColor = glyph.ForeColor;
            glyph.ForeColor = Color.Transparent;
            parent.DrawToBitmap(baseline, parent.ClientRectangle);

            using var rendered = new Bitmap(parent.Width, parent.Height);
            glyph.ForeColor = glyphColor;
            parent.DrawToBitmap(rendered, parent.ClientRectangle);

            var left = transparentHost.Left + glyph.Left;
            var top = transparentHost.Top + glyph.Top;
            foreach (var offset in new[] { new Point(0, 0), new Point(2, 2), new Point(35, 2), new Point(2, 27), new Point(37, 29) })
            {
                Assert.Equal(
                    baseline.GetPixel(left + offset.X, top + offset.Y).ToArgb(),
                    rendered.GetPixel(left + offset.X, top + offset.Y).ToArgb());
            }

            var changedPixels = 0;
            for (var y = 0; y < glyph.Height; y++)
            {
                for (var x = 0; x < glyph.Width; x++)
                {
                    if (baseline.GetPixel(left + x, top + y).ToArgb()
                        != rendered.GetPixel(left + x, top + y).ToArgb())
                    {
                        changedPixels++;
                    }
                }
            }

            Assert.InRange(changedPixels, 1, 300);
        });
    }

    [Fact]
    public void ToggleSwitchPublishesOneCheckedChangeAndAccessibleState()
    {
        RunSta(() =>
        {
            using var toggle = UiTheme.Toggle("Local reviews");
            toggle.CreateControl();
            var changes = 0;
            toggle.CheckedChanged += (_, _) => changes++;

            toggle.Checked = true;
            toggle.Checked = true;

            Assert.Equal(1, changes);
            Assert.True(toggle.Checked);
            Assert.True(toggle.AccessibilityObject.State.HasFlag(AccessibleStates.Checked));
        });
    }

    [Fact]
    public void ToggleSwitchSupportsMouseKeyboardAccessibilityAndDisabledInput()
    {
        RunSta(() =>
        {
            using var toggle = new TestToggleSwitch { AccessibleName = "Local reviews" };
            toggle.CreateControl();

            toggle.SimulateClick();
            Assert.True(toggle.Checked);

            toggle.Press(Keys.Space);
            Assert.False(toggle.Checked);

            toggle.Press(Keys.Enter);
            Assert.True(toggle.Checked);

            toggle.AccessibilityObject.DoDefaultAction();
            Assert.False(toggle.Checked);

            toggle.Enabled = false;
            toggle.SimulateClick();
            Assert.False(toggle.Checked);
        });
    }

    [Fact]
    public void ToggleSwitchCompositesRoundedCornersOverItsParentSurface()
    {
        RunSta(() =>
        {
            using var parent = new RoundedPanel
            {
                Size = new Size(180, 100),
                BackColor = Color.FromArgb(27, 34, 47)
            };
            using var toggle = UiTheme.Toggle("Local reviews");
            toggle.Location = new Point(48, 30);
            toggle.Checked = true;
            parent.Controls.Add(toggle);
            CreateControls(parent);

            using var enabled = new Bitmap(toggle.Width, toggle.Height);
            toggle.DrawToBitmap(enabled, toggle.ClientRectangle);
            Assert.Equal(parent.BackColor.ToArgb(), enabled.GetPixel(0, 0).ToArgb());
            Assert.NotEqual(Color.Black.ToArgb(), enabled.GetPixel(toggle.Width / 2, toggle.Height / 2).ToArgb());

            toggle.Enabled = false;
            using var disabled = new Bitmap(toggle.Width, toggle.Height);
            toggle.DrawToBitmap(disabled, toggle.ClientRectangle);
            Assert.Equal(parent.BackColor.ToArgb(), disabled.GetPixel(0, 0).ToArgb());

            using var focused = new Bitmap(toggle.Width, toggle.Height);
            using (var graphics = Graphics.FromImage(focused))
            {
                graphics.Clear(parent.BackColor);
                ToggleSwitch.Render(graphics, toggle.ClientRectangle, enabled: true, isChecked: true, focused: true, dpi: 96);
            }

            Assert.Equal(parent.BackColor.ToArgb(), focused.GetPixel(4, 4).ToArgb());
            Assert.NotEqual(parent.BackColor.ToArgb(), focused.GetPixel(toggle.Width / 2, 1).ToArgb());
        });
    }

    [Fact]
    public void GpuStatusReflectsTrackedHelperActivityAndReturnsToIdle()
    {
        RunSta(() =>
        {
            using var form = new MainForm();
            CreateControls(form);
            var type = typeof(MainForm);
            type.GetField("_currentState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(form, new ThalenHelper.Core.InstallationState
                {
                    Availability = ThalenHelper.Core.HelperAvailability.Enabled,
                    Preferences = new ThalenHelper.Core.HelperPreferences(LowImpactMode: true)
                });
            type.GetField("_managedActionsAllowed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(form, true);
            type.GetField("_managedConfigEnabled", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(form, true);

            var notice = (Label)type.GetField("_notice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
            var mode = (Label)type.GetField("_gpuModeValue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
            var hero = (Label)type.GetField("_heroMessage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
            notice.Text = "Manual Ollama startup";
            mode.Text = "LOW IMPACT";
            hero.Text = "Nothing is loaded until Codex asks for a review.";
            type.GetMethod("CapturePassiveGpuPresentation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(form, null);

            form.ApplyTrackedGpuActivity(new ThalenHelper.Core.ActiveModelReference(
                ThalenHelper.Core.ModelProviders.LmStudio,
                "qwythos-9b-claude-mythos-5-1m",
                "instance-1"));

            Assert.Equal("Qwythos 9B review via LM Studio", notice.Text);
            Assert.Equal("REVIEW TRACKED", mode.Text);
            Assert.Contains("tracking a bounded", hero.Text, StringComparison.OrdinalIgnoreCase);

            form.ApplyTrackedGpuActivity(null);

            Assert.Equal("Manual Ollama startup", notice.Text);
            Assert.Equal("LOW IMPACT", mode.Text);
            Assert.Equal("Nothing is loaded until Codex asks for a review.", hero.Text);
        });
    }

    [Theory]
    [InlineData(ReviewActivityPhase.Loading, "LOADING", "loading for review")]
    [InlineData(ReviewActivityPhase.Reviewing, "REVIEW ACTIVE", "review active")]
    [InlineData(ReviewActivityPhase.Releasing, "RELEASING", "release being verified")]
    [InlineData(ReviewActivityPhase.Attention, "CHECK STATUS", "needs a status check")]
    public void ReviewActivityPhasesAreTruthfulAndRestorePassiveStatus(
        ReviewActivityPhase phase,
        string expectedMode,
        string expectedNotice)
    {
        RunSta(() =>
        {
            using var form = new MainForm();
            CreateControls(form);
            var type = typeof(MainForm);
            type.GetField("_currentState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(form, new InstallationState
                {
                    Availability = HelperAvailability.Enabled,
                    Preferences = new HelperPreferences(LowImpactMode: true)
                });
            type.GetField("_managedActionsAllowed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(form, true);
            type.GetField("_managedConfigEnabled", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(form, true);

            var notice = (Label)type.GetField("_notice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
            var mode = (Label)type.GetField("_gpuModeValue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
            var release = (Button)type.GetField("_releaseGpuButton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
            notice.Text = "Manual Ollama startup";
            mode.Text = "LOW IMPACT";
            release.Visible = false;
            type.GetMethod("CapturePassiveGpuPresentation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(form, null);

            form.ApplyTrackedGpuActivity(
                new ReviewActivityReference(
                    1,
                    "operation-1",
                    ModelProviders.LmStudio,
                    "qwythos-9b-claude-mythos-5-1m",
                    phase,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
                null);

            Assert.Equal(expectedMode, mode.Text);
            Assert.Contains(expectedNotice, notice.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(release.Visible);

            form.ApplyTrackedGpuActivity(null, null);
            Assert.Equal("Manual Ollama startup", notice.Text);
            Assert.Equal("LOW IMPACT", mode.Text);
            Assert.False(release.Visible);
        });
    }

    [Fact]
    public void SupersedingRefreshCancelsOnlyTheOlderPassiveWork()
    {
        using var coordinator = new SupersedingRefreshCoordinator();
        var first = coordinator.Begin();
        using var firstCancelled = new ManualResetEventSlim();
        using var registration = first.Token.Register(firstCancelled.Set);

        var second = coordinator.Begin();
        Assert.True(firstCancelled.Wait(TimeSpan.FromSeconds(1)));
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);

        coordinator.Complete(first);
        Assert.False(second.IsCancellationRequested);

        var third = coordinator.Begin();
        Assert.True(second.IsCancellationRequested);
        Assert.False(third.IsCancellationRequested);
        coordinator.Complete(second);
        coordinator.Complete(third);
    }

    [Fact]
    public void RapidReviewToggleReversalUsesTheLatestPersistedAvailability()
    {
        Assert.Equal(
            ReviewToggleMutation.Resume,
            MainForm.SelectReviewToggleMutation(true, ThalenHelper.Core.HelperAvailability.Paused, managedConfigEnabled: true));
        Assert.Equal(
            ReviewToggleMutation.Pause,
            MainForm.SelectReviewToggleMutation(false, ThalenHelper.Core.HelperAvailability.Enabled, managedConfigEnabled: true));
    }

    [Fact]
    public async Task ToggleControlsReenableOnlyAfterPersistedStateReconciliation()
    {
        var reconciliationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reenabled = false;
        var completion = MainForm.ReconcileBeforeReenableAsync(
            () => reconciliationGate.Task,
            () => reenabled = true);

        await Task.Yield();
        Assert.False(reenabled);
        Assert.False(completion.IsCompleted);

        reconciliationGate.SetResult();
        await completion;
        Assert.True(reenabled);
    }

    [Theory]
    [InlineData(560, 520)]
    [InlineData(620, 570)]
    public void AutomaticRoutePresentationSeparatesDifferentDeepModelAtCompactSizes(int width, int height)
    {
        RunSta(() =>
        {
            using var form = new MainForm { Size = new Size(width, height) };
            CreateControls(form);
            var type = typeof(MainForm);
            var state = new ThalenHelper.Core.InstallationState
            {
                Preferences = new ThalenHelper.Core.HelperPreferences(
                    ModelSelectionMode: ThalenHelper.Core.ModelSelectionMode.Automatic)
            };
            var health = new ThalenHelper.Core.ReviewerHealthResult { EligibleInstalledModels = 3 };
            var quick = Plan("qwen3:8b", "Ollama");
            var standard = Plan("qwen3:14b", "Ollama");
            var deep = Plan("qwen3-coder:30b", "Ollama");

            type.GetMethod("UpdateRoutePresentation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(form, [state, health, quick, standard, deep]);
            PerformLayoutTree(form);

            Assert.Equal("Normal", ((Label)type.GetField("_normalRoutePurpose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!).Text);
            Assert.Equal("Qwen3 14B", ((Label)type.GetField("_routeValue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!).Text);
            Assert.Equal("Qwen3 Coder 30B", ((Label)type.GetField("_deepRouteValue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!).Text);
            Assert.False(MainForm.PlansShareRoute(standard, deep));
            var routingCard = Descendants(form).Single(control => control.AccessibleName == "Automatic routing overview");
            foreach (var routeLabel in Descendants(routingCard).OfType<Label>())
            {
                AssertWithinAncestors(routeLabel, routingCard);
            }

            var qwythos = Plan("qwythos-9b-claude-mythos-5-1m", "LM Studio");
            type.GetMethod("UpdateRoutePresentation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(form, [state, health, quick, qwythos, qwythos]);
            Assert.Equal("Normal & deep", ((Label)type.GetField("_normalRoutePurpose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!).Text);
            Assert.True(MainForm.PlansShareRoute(qwythos, qwythos));
        });
    }

    [Theory]
    [InlineData(560, 520)]
    [InlineData(620, 570)]
    public void CollapsedHomeSurfaceHasFiveOrFewerClearActionsAndNoClippedButtons(int width, int height)
    {
        RunSta(() =>
        {
            using var form = new MainForm
            {
                Size = new Size(width, height)
            };
            CreateControls(form);
            var formType = typeof(MainForm);
            ((Label)formType.GetField("_heroTitle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!)
                .Text = "External provider is network-exposed and local review is blocked";
            ((Label)formType.GetField("_heroMessage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!)
                .Text = "The detected local endpoint is not loopback-only, so no local model action is permitted until the exposure is fixed.";
            ((Label)formType.GetField("_notice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!)
                .Text = "Automatic task-aware routing is enabled for every Codex project using this managed integration; no model was loaded.";
            PerformLayoutTree(form);

            var visibleActions = Descendants(form)
                .Where(control => control is Button or ToggleSwitch or GlyphButton)
                .Where(control => control.Text != "Retry status")
                .Where(control => !HasAncestor(control, "Advanced settings"))
                .ToArray();
            Assert.True(visibleActions.Length <= 5, $"Expected at most five home actions, found {visibleActions.Length}: {string.Join(", ", visibleActions.Select(item => item.Text))}");

            Assert.Contains(visibleActions.OfType<Button>(), button => button.Text == "Test reviewer");
            Assert.Contains(visibleActions.OfType<Button>(), button => button.Text == "Models & storage");
            Assert.Contains(visibleActions.OfType<GlyphButton>(), button => button.AccessibleName == "Advanced settings");
            Assert.DoesNotContain(visibleActions.OfType<Button>(), button => button.Text is "Pause reviews" or "Resume reviews" or "Enable integration" or "Disable integration");
            Assert.False(Descendants(form).OfType<Button>().Single(button => button.Text == "Retry status").Visible);

            foreach (var button in visibleActions.OfType<Button>())
            {
                Assert.True(button.Parent is not null);
                Assert.True(button.Bounds.Right <= button.Parent.ClientRectangle.Right + 1, $"{button.Text} exceeds its parent width.");
                Assert.True(button.Bounds.Bottom <= button.Parent.ClientRectangle.Bottom + 1, $"{button.Text} is clipped by its parent height: button={button.Bounds}, parent={button.Parent.ClientRectangle}.");
            }

            var hero = Descendants(form).Single(control => control.AccessibleName == "Local reviewer overview");
            foreach (var label in Descendants(hero).OfType<Label>())
            {
                var requiredHeight = label.AutoEllipsis && !label.AutoSize
                    ? label.Font.Height
                    : label.PreferredHeight;
                Assert.True(label.Height >= requiredHeight, $"Hero label '{label.Text}' is shorter than its rendered text height.");
                AssertWithinAncestors(label, hero);
            }

            var status = (Label)formType.GetField("_notice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
            Assert.True(status.AutoEllipsis);
            AssertWithinAncestors(status, FindAncestor<RoundedPanel>(status));
        });
    }

    [Fact]
    public void ModelSetupRemainsAvailableToRecoverAnIncompleteFirstRun()
    {
        RunSta(() =>
        {
            using var form = new MainForm();
            CreateControls(form);

            var type = typeof(MainForm);
            type.GetField("_managedActionsAllowed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(form, false);
            type.GetField("_currentState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(form, null);
            type.GetMethod("SetActionControlsEnabled", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(form, [true]);

            var buttons = Descendants(form).OfType<Button>().ToArray();
            Assert.True(buttons.Single(button => button.Text == "Models & storage").Enabled);
            Assert.False(buttons.Single(button => button.Text == "Test reviewer").Enabled);
        });
    }

    [Theory]
    [InlineData(true, " THALEN_READY\r\n", true)]
    [InlineData(true, "THALEN READY", false)]
    [InlineData(false, "THALEN_READY", false)]
    public void ReviewerTestRequiresTheExactReadinessToken(bool modelRan, string findings, bool expected)
        => Assert.Equal(expected, MainForm.IsReadyResponse(new ThalenHelper.Core.ReviewerResult
        {
            ModelRan = modelRan,
            Findings = findings
        }));

    [Fact]
    public void ReviewerTestAcceptsTheStructuredReadinessClaimRequiredByTheReviewerPrompt()
        => Assert.True(MainForm.IsReadyResponse(new ThalenHelper.Core.ReviewerResult
        {
            ModelRan = true,
            Findings = "{\"findings\":[{\"claim\":\"THALEN_READY\"}]}",
            StructuredFindings =
            [
                new ThalenHelper.Core.StructuredReviewerFinding(
                    "F1",
                    "THALEN_READY",
                    "readiness-check",
                    "readiness-check",
                    "high",
                    "connectivity confirmed",
                    "none",
                "model did not follow this assignment")
            ]
        }));

    [Fact]
    public void ReviewerReadinessPromptParserAndVerifierShareOneStructuredContract()
    {
        var request = new ThalenHelper.Core.ReviewRequest(
            Assignment: "Return one structured advisory finding whose claim is exactly THALEN_READY.",
            Focus: "One bounded connectivity check only.",
            MaximumOutputTokens: 256,
            TaskKind: ThalenHelper.Core.ReviewTaskKind.DiffReview,
            Effort: ThalenHelper.Core.ReviewEffort.Standard,
            DesiredContextTokens: 8_192,
            EstimatedInputCharacters: 120);
        var prompt = ThalenHelper.Core.ReviewerService.BuildPrompt(
            request,
            ThalenHelper.Core.HardwareTier.Mid,
            ThalenHelper.Core.ReviewTaskKind.DiffReview);
        const string response = """{"findings":[{"id":"F1","claim":"THALEN_READY","location":"readiness-check","evidence":"readiness-check","confidence":"high","impact":"connectivity confirmed","verification":"none","falsePositiveCondition":"model did not follow this assignment"}]}""";
        var parsed = ThalenHelper.Core.ReviewerService.ParseStructuredFindingsWithStatus(response);

        Assert.Contains("Return only one JSON object", prompt, StringComparison.Ordinal);
        Assert.Contains("THALEN_READY", prompt, StringComparison.Ordinal);
        Assert.Equal("parsed", parsed.Status);
        Assert.True(MainForm.IsReadyResponse(new ThalenHelper.Core.ReviewerResult
        {
            ModelRan = true,
            Findings = response,
            StructuredFindings = parsed.Findings,
            StructuredFindingsStatus = parsed.Status
        }));
    }

    [Theory]
    [InlineData(ThalenHelper.Core.HelperAvailability.Enabled, false, true)]
    [InlineData(ThalenHelper.Core.HelperAvailability.Paused, false, true)]
    [InlineData(ThalenHelper.Core.HelperAvailability.Disabled, true, true)]
    [InlineData(ThalenHelper.Core.HelperAvailability.Paused, true, false)]
    public void ReviewToggleRepairsADisabledManagedCodexEntry(
        ThalenHelper.Core.HelperAvailability availability,
        bool managedConfigEnabled,
        bool expectedEnable)
        => Assert.Equal(expectedEnable, MainForm.ShouldEnableCodexEntry(availability, managedConfigEnabled));

    private static void CreateControls(Control root)
    {
        root.CreateControl();
        foreach (Control child in root.Controls)
        {
            CreateControls(child);
        }
    }

    private static ThalenHelper.Core.ReviewerPlanResult Plan(string model, string provider)
        => new()
        {
            Allowed = true,
            Model = model,
            Provider = provider
        };

    private static void PerformLayoutTree(Control root)
    {
        root.PerformLayout();
        foreach (Control child in root.Controls)
        {
            PerformLayoutTree(child);
        }

        root.PerformLayout();
    }

    private static void AssertWithinAncestors(Control control, Control boundary)
    {
        var bounds = control.Bounds;
        for (var ancestor = control.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            var context = $"control={control.Bounds}, relative={bounds}, ancestor={ancestor.ClientRectangle}";
            Assert.True(bounds.Left >= -1, $"'{control.Text}' exceeds the left edge of {ancestor.GetType().Name}: {context}");
            Assert.True(bounds.Top >= -1, $"'{control.Text}' exceeds the top edge of {ancestor.GetType().Name}: {context}");
            Assert.True(bounds.Right <= ancestor.ClientRectangle.Right + 1, $"'{control.Text}' exceeds the right edge of {ancestor.GetType().Name}: {context}");
            Assert.True(bounds.Bottom <= ancestor.ClientRectangle.Bottom + 1, $"'{control.Text}' exceeds the bottom edge of {ancestor.GetType().Name}: {context}");
            if (ReferenceEquals(ancestor, boundary))
            {
                break;
            }

            bounds.Offset(ancestor.Left, ancestor.Top);
        }
    }

    private static T FindAncestor<T>(Control control) where T : Control
    {
        for (var ancestor = control.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is T match)
            {
                return match;
            }
        }

        throw new Xunit.Sdk.XunitException($"No {typeof(T).Name} ancestor was found for {control.Name}.");
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool HasAncestor(Control control, string accessibleName)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (string.Equals(parent.AccessibleName, accessibleName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The STA UI test timed out.");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private sealed class TestToggleSwitch : ToggleSwitch
    {
        public void SimulateClick() => OnClick(EventArgs.Empty);

        public void Press(Keys key)
            => OnKeyDown(new KeyEventArgs(key));
    }
}
