using System.Text;
using System.Text.Json;

namespace ThalenHelper.Core;

public sealed class ActiveModelTracker
{
    private readonly string _path;

    public ActiveModelTracker(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _path = System.IO.Path.Combine(System.IO.Path.GetFullPath(stateDirectory), "active-routed-model.txt");
    }

    public string Path => _path;

    public string? Read()
        => ReadReference()?.Model;

    public ActiveModelReference? ReadReference()
        => Inspect().Reference;

    internal ActiveModelTrackerInspection Inspect()
    {
        if (!File.Exists(_path))
        {
            return new ActiveModelTrackerInspection(ActiveModelTrackerStatus.Absent, null);
        }

        try
        {
            var value = File.ReadAllText(_path, Encoding.UTF8).Trim();
            if (value.StartsWith('{'))
            {
                var reference = JsonSerializer.Deserialize<ActiveModelReference>(value);
                if (reference is null
                    || !ModelProviders.IsSupported(reference.Provider)
                    || string.IsNullOrWhiteSpace(reference.Model)
                    || reference.Model.Length > 128)
                {
                    return new ActiveModelTrackerInspection(ActiveModelTrackerStatus.Invalid, null);
                }

                var normalized = reference with { Provider = ModelProviders.Normalize(reference.Provider) };
                if (string.Equals(normalized.Provider, ModelProviders.Ollama, StringComparison.Ordinal))
                {
                    OllamaClient.ValidateModelIdentifier(normalized.Model);
                    if (!string.IsNullOrWhiteSpace(normalized.InstanceId))
                    {
                        return new ActiveModelTrackerInspection(ActiveModelTrackerStatus.Invalid, null);
                    }
                }

                return new ActiveModelTrackerInspection(ActiveModelTrackerStatus.Valid, normalized);
            }

            OllamaClient.ValidateModelIdentifier(value);
            return new ActiveModelTrackerInspection(
                ActiveModelTrackerStatus.Valid,
                new ActiveModelReference(ModelProviders.Ollama, value, null));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or OllamaException
            or JsonException)
        {
            return new ActiveModelTrackerInspection(ActiveModelTrackerStatus.Invalid, null);
        }
    }

    public void Set(string model)
        => Set(new ActiveModelReference(ModelProviders.Ollama, model, null));

    public void Set(ActiveModelReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!ModelProviders.IsSupported(reference.Provider)) throw new ArgumentException("Unsupported provider.", nameof(reference));
        if (string.IsNullOrWhiteSpace(reference.Model) || reference.Model.Length > 128) throw new ArgumentException("Invalid model.", nameof(reference));
        if (string.Equals(ModelProviders.Normalize(reference.Provider), ModelProviders.Ollama, StringComparison.Ordinal))
        {
            OllamaClient.ValidateModelIdentifier(reference.Model);
            if (!string.IsNullOrWhiteSpace(reference.InstanceId)) throw new ArgumentException("Ollama trackers do not support instance identifiers.", nameof(reference));
        }
        var directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + "." + Environment.ProcessId + ".tmp";
        var json = System.Text.Json.JsonSerializer.Serialize(reference with { Provider = ModelProviders.Normalize(reference.Provider) });
        File.WriteAllText(temporary, json + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temporary, _path, true);
    }

    public void Clear(string? expectedModel = null)
    {
        if (!string.IsNullOrWhiteSpace(expectedModel)
            && !string.Equals(Read(), expectedModel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed record ActiveModelReference(string Provider, string Model, string? InstanceId);

internal enum ActiveModelTrackerStatus
{
    Absent,
    Valid,
    Invalid
}

internal sealed record ActiveModelTrackerInspection(
    ActiveModelTrackerStatus Status,
    ActiveModelReference? Reference);
