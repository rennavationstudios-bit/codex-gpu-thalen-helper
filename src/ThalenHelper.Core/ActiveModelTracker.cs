using System.Text;

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
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var value = File.ReadAllText(_path, Encoding.UTF8).Trim();
            if (value.StartsWith('{'))
            {
                var reference = System.Text.Json.JsonSerializer.Deserialize<ActiveModelReference>(value);
                if (reference is null || !ModelProviders.IsSupported(reference.Provider)) return null;
                return reference with { Provider = ModelProviders.Normalize(reference.Provider) };
            }
            OllamaClient.ValidateModelIdentifier(value);
            return new ActiveModelReference(ModelProviders.Ollama, value, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OllamaException)
        {
            return null;
        }
    }

    public void Set(string model)
        => Set(new ActiveModelReference(ModelProviders.Ollama, model, null));

    public void Set(ActiveModelReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!ModelProviders.IsSupported(reference.Provider)) throw new ArgumentException("Unsupported provider.", nameof(reference));
        if (string.IsNullOrWhiteSpace(reference.Model) || reference.Model.Length > 128) throw new ArgumentException("Invalid model.", nameof(reference));
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
