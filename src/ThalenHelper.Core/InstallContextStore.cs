using System.Text.Json;

namespace ThalenHelper.Core;

public sealed record InstallContext(
    int SchemaVersion,
    string InstallDirectory,
    string StateDirectory,
    string CodexHome);

public static class InstallContextStore
{
    public const string FileName = ".thalen-helper-install-context.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static void Save(ProductPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.InstallDirectory);
        var context = new InstallContext(
            1,
            NormalizeDirectoryPath(paths.InstallDirectory),
            Path.GetFullPath(paths.StateDirectory),
            Path.GetFullPath(paths.CodexHome));
        var destination = GetPath(paths.InstallDirectory);
        var temporary = destination + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(context, JsonOptions));
            File.Move(temporary, destination, true);
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }

    public static InstallContext? Load(string installDirectory)
    {
        var expectedInstallDirectory = NormalizeDirectoryPath(installDirectory);
        var path = GetPath(expectedInstallDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        InstallContext context;
        try
        {
            context = JsonSerializer.Deserialize<InstallContext>(File.ReadAllText(path))
                ?? throw new InvalidDataException("The install context is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The install context is malformed.", exception);
        }

        if (context.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(context.InstallDirectory)
            || string.IsNullOrWhiteSpace(context.StateDirectory)
            || string.IsNullOrWhiteSpace(context.CodexHome)
            || !Path.IsPathFullyQualified(context.InstallDirectory)
            || !Path.IsPathFullyQualified(context.StateDirectory)
            || !Path.IsPathFullyQualified(context.CodexHome)
            || !string.Equals(
                NormalizeDirectoryPath(context.InstallDirectory),
                expectedInstallDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The install context paths are invalid or do not match this installation.");
        }

        return context with
        {
            InstallDirectory = expectedInstallDirectory,
            StateDirectory = Path.GetFullPath(context.StateDirectory),
            CodexHome = Path.GetFullPath(context.CodexHome)
        };
    }

    public static void Delete(string installDirectory)
    {
        var path = GetPath(installDirectory);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static string GetPath(string installDirectory)
        => Path.Combine(Path.GetFullPath(installDirectory), FileName);

    private static string NormalizeDirectoryPath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
