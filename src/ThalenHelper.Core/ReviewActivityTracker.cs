using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThalenHelper.Core;

/// <summary>
/// Publishes a small, non-authoritative signal while a helper review lifecycle is active.
/// This is deliberately separate from <see cref="ActiveModelTracker"/>, which remains the
/// only durable model-ownership and cleanup authority.
/// </summary>
public sealed class ReviewActivityTracker
{
    internal static readonly TimeSpan ActiveMaximumAge = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan AttentionMaximumAge = TimeSpan.FromMinutes(2);

    private readonly string _path;

    public ReviewActivityTracker(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _path = System.IO.Path.Combine(System.IO.Path.GetFullPath(stateDirectory), "active-review.json");
    }

    public string Path => _path;

    public ReviewActivityReference? ReadCurrent()
        => ReadCurrent(DateTimeOffset.UtcNow);

    internal ReviewActivityReference? ReadCurrent(DateTimeOffset now)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var activity = JsonSerializer.Deserialize<ReviewActivityReference>(
                File.ReadAllText(_path, Encoding.UTF8));
            if (activity is null
                || activity.SchemaVersion != 1
                || string.IsNullOrWhiteSpace(activity.OperationId)
                || activity.OperationId.Length > 64
                || !ModelProviders.IsSupported(activity.Provider)
                || string.IsNullOrWhiteSpace(activity.Model)
                || activity.Model.Length > 128
                || !Enum.IsDefined(activity.Phase)
                || activity.StartedAtUtc > now.AddMinutes(1)
                || activity.UpdatedAtUtc < activity.StartedAtUtc
                || activity.UpdatedAtUtc > now.AddMinutes(1)
                || now - activity.UpdatedAtUtc > MaximumAge(activity.Phase))
            {
                return null;
            }

            return activity with { Provider = ModelProviders.Normalize(activity.Provider) };
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return null;
        }
    }

    public ReviewActivityLease? TryBegin(
        string provider,
        string model,
        ReviewActivityPhase phase = ReviewActivityPhase.Loading)
    {
        if (!ModelProviders.IsSupported(provider))
        {
            throw new ArgumentException("Unsupported provider.", nameof(provider));
        }
        if (string.IsNullOrWhiteSpace(model) || model.Length > 128)
        {
            throw new ArgumentException("Invalid model.", nameof(model));
        }
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        var activity = new ReviewActivityReference(
            1,
            Guid.NewGuid().ToString("N"),
            ModelProviders.Normalize(provider),
            model,
            phase,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = _path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(activity) + Environment.NewLine,
                new UTF8Encoding(false));
            File.Move(temporary, _path, true);
            return new ReviewActivityLease(this, activity.OperationId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal bool TrySetExact(string operationId, ReviewActivityPhase phase)
    {
        if (!Enum.IsDefined(phase))
        {
            return false;
        }

        try
        {
            var current = ReadCurrent();
            if (current is null
                || !string.Equals(current.OperationId, operationId, StringComparison.Ordinal))
            {
                return false;
            }

            Write(current with { Phase = phase, UpdatedAtUtc = DateTimeOffset.UtcNow });
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal void ClearExact(string operationId)
    {
        try
        {
            var current = ReadCurrent();
            if (current is null
                || !string.Equals(current.OperationId, operationId, StringComparison.Ordinal))
            {
                return;
            }

            File.Delete(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Write(ReviewActivityReference activity)
    {
        var directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(activity) + Environment.NewLine,
            new UTF8Encoding(false));
        File.Move(temporary, _path, true);
    }

    private static TimeSpan MaximumAge(ReviewActivityPhase phase)
        => phase is ReviewActivityPhase.Attention or ReviewActivityPhase.Releasing
            ? AttentionMaximumAge
            : ActiveMaximumAge;
}

public sealed class ReviewActivityLease : IDisposable
{
    private ReviewActivityTracker? _owner;
    private readonly string _operationId;
    private bool _preserve;

    internal ReviewActivityLease(ReviewActivityTracker owner, string operationId)
    {
        _owner = owner;
        _operationId = operationId;
    }

    public bool TrySetPhase(ReviewActivityPhase phase)
        => _owner?.TrySetExact(_operationId, phase) == true;

    public void PreserveAsAttention()
    {
        if (TrySetPhase(ReviewActivityPhase.Attention))
        {
            _preserve = true;
        }
    }

    public void Complete()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.ClearExact(_operationId);
    }

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (!_preserve)
        {
            owner?.ClearExact(_operationId);
        }
    }
}

public sealed record ReviewActivityReference(
    int SchemaVersion,
    string OperationId,
    string Provider,
    string Model,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ReviewActivityPhase Phase,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewActivityPhase
{
    Loading,
    Reviewing,
    Releasing,
    Attention
}
