using System.Text.Json;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class ReviewActivityTrackerTests
{
    [Fact]
    public void ActivityLifecycleIsDisplayOnlyExactAndIndependentFromOwnership()
    {
        using var temporary = new TemporaryDirectory("review-activity");
        var activity = new ReviewActivityTracker(temporary.Path);
        var ownership = new ActiveModelTracker(temporary.Path);

        using var first = activity.TryBegin(
            ModelProviders.LmStudio,
            "fixture-model",
            ReviewActivityPhase.Loading);
        Assert.NotNull(first);
        var loading = Assert.IsType<ReviewActivityReference>(activity.ReadCurrent());
        Assert.Equal(ModelProviders.LmStudio, loading.Provider);
        Assert.Equal("fixture-model", loading.Model);
        Assert.Equal(ReviewActivityPhase.Loading, loading.Phase);
        Assert.False(File.Exists(ownership.Path));

        Assert.True(first!.TrySetPhase(ReviewActivityPhase.Reviewing));
        Assert.Equal(ReviewActivityPhase.Reviewing, activity.ReadCurrent()!.Phase);

        using var second = activity.TryBegin(
            ModelProviders.Ollama,
            "qwen3:8b",
            ReviewActivityPhase.Reviewing);
        var replacement = Assert.IsType<ReviewActivityReference>(activity.ReadCurrent());
        Assert.NotEqual(loading.OperationId, replacement.OperationId);
        first.Dispose();
        Assert.Equal(replacement.OperationId, activity.ReadCurrent()!.OperationId);

        second!.Complete();
        Assert.Null(activity.ReadCurrent());
        Assert.False(File.Exists(ownership.Path));
    }

    [Fact]
    public void StaleFutureMalformedAndUnknownPhaseRecordsAreIgnored()
    {
        using var temporary = new TemporaryDirectory("review-activity-invalid");
        var tracker = new ReviewActivityTracker(temporary.Path);
        var now = DateTimeOffset.UtcNow;

        Write(tracker.Path, new ReviewActivityReference(
            1,
            "stale",
            ModelProviders.LmStudio,
            "fixture-model",
            ReviewActivityPhase.Loading,
            now.AddMinutes(-11),
            now.AddMinutes(-11)));
        Assert.Null(tracker.ReadCurrent(now));

        Write(tracker.Path, new ReviewActivityReference(
            1,
            "future",
            ModelProviders.Ollama,
            "qwen3:8b",
            ReviewActivityPhase.Reviewing,
            now.AddMinutes(2),
            now.AddMinutes(2)));
        Assert.Null(tracker.ReadCurrent(now));

        File.WriteAllText(tracker.Path, "{not-json");
        Assert.Null(tracker.ReadCurrent(now));

        File.WriteAllText(
            tracker.Path,
            "{\"SchemaVersion\":1,\"OperationId\":\"bad-phase\",\"Provider\":\"Ollama\",\"Model\":\"qwen3:8b\",\"Phase\":\"Unknown\",\"StartedAtUtc\":\"2026-01-01T00:00:00Z\",\"UpdatedAtUtc\":\"2026-01-01T00:00:00Z\"}");
        Assert.Null(tracker.ReadCurrent(now));

        File.WriteAllText(
            tracker.Path,
            $"{{\"SchemaVersion\":1,\"OperationId\":\"numeric-phase\",\"Provider\":\"Ollama\",\"Model\":\"qwen3:8b\",\"Phase\":999,\"StartedAtUtc\":\"{now:O}\",\"UpdatedAtUtc\":\"{now:O}\"}}");
        Assert.Null(tracker.ReadCurrent(now));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.TryBegin(
            ModelProviders.Ollama,
            "qwen3:8b",
            (ReviewActivityPhase)999));
    }

    [Fact]
    public void SerializedActivityContainsNoPromptPathDigestOrMachineIdentity()
    {
        using var temporary = new TemporaryDirectory("review-activity-privacy");
        var tracker = new ReviewActivityTracker(temporary.Path);
        using var activity = tracker.TryBegin(ModelProviders.LmStudio, "fixture-model");

        var serialized = File.ReadAllText(tracker.Path);

        Assert.DoesNotContain("prompt", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("context", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("digest", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("machine", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user", serialized, StringComparison.OrdinalIgnoreCase);
    }

    private static void Write(string path, ReviewActivityReference activity)
        => File.WriteAllText(path, JsonSerializer.Serialize(activity));
}
