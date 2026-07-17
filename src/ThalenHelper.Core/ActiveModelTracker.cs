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
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var model = File.ReadAllText(_path, Encoding.UTF8).Trim();
            OllamaClient.ValidateModelIdentifier(model);
            return model;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OllamaException)
        {
            return null;
        }
    }

    public void Set(string model)
    {
        OllamaClient.ValidateModelIdentifier(model);
        var directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temporary, model + Environment.NewLine, new UTF8Encoding(false));
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
