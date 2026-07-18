using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ThalenHelper.ControlCenter;

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
            button.CreateControl();
            panel.CreateControl();

            Assert.Null(button.Region);
            Assert.Null(panel.Region);

            using GraphicsPath path = RoundedPanel.RoundedRectangle(new Rectangle(0, 0, 24, 18), 100, 1F);
            var bounds = path.GetBounds();
            Assert.InRange(bounds.Left, 0.5F, 1.5F);
            Assert.InRange(bounds.Top, 0.5F, 1.5F);
            Assert.InRange(bounds.Right, 22.5F, 23.5F);
            Assert.InRange(bounds.Bottom, 16.5F, 17.5F);
        });
    }

    [Fact]
    public void CollapsedHomeSurfaceHasFiveOrFewerClearActionsAndNoClippedButtons()
    {
        RunSta(() =>
        {
            using var form = new MainForm
            {
                Size = new Size(900, 650)
            };
            CreateControls(form);
            form.PerformLayout();

            var visibleActions = Descendants(form)
                .Where(control => control is Button or ToggleSwitch)
                .Where(control => control.Text != "Retry status")
                .Where(control => !HasAncestor(control, "Advanced settings"))
                .ToArray();
            Assert.True(visibleActions.Length <= 5, $"Expected at most five home actions, found {visibleActions.Length}: {string.Join(", ", visibleActions.Select(item => item.Text))}");

            Assert.Contains(visibleActions.OfType<Button>(), button => button.Text == "Test reviewer");
            Assert.Contains(visibleActions.OfType<Button>(), button => button.Text == "Models & storage");
            Assert.Contains(visibleActions.OfType<Button>(), button => button.Text == "Advanced settings");
            Assert.DoesNotContain(visibleActions.OfType<Button>(), button => button.Text is "Pause reviews" or "Resume reviews" or "Enable integration" or "Disable integration");
            Assert.False(Descendants(form).OfType<Button>().Single(button => button.Text == "Retry status").Visible);

            foreach (var button in visibleActions.OfType<Button>())
            {
                Assert.True(button.Parent is not null);
                Assert.True(button.Bounds.Right <= button.Parent.ClientRectangle.Right + 1, $"{button.Text} exceeds its parent width.");
                Assert.True(button.Bounds.Bottom <= button.Parent.ClientRectangle.Bottom + 1, $"{button.Text} is clipped by its parent height.");
            }

            var hero = Descendants(form).Single(control => control.AccessibleName == "Local reviewer overview");
            foreach (var label in Descendants(hero).OfType<Label>().Where(label => label.Visible))
            {
                var bounds = hero.RectangleToClient(label.Parent!.RectangleToScreen(label.Bounds));
                Assert.True(bounds.Bottom <= hero.ClientRectangle.Bottom + 1, $"Hero label '{label.Text}' is clipped by its fixed height.");
            }
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
}
