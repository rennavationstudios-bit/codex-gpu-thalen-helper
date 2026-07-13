using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThalenHelper.Core;

public sealed class DiagnosticsExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task ExportAsync(
        string destination,
        ProductPaths paths,
        HardwareProfile hardware,
        InstallationState? state,
        ReviewerHealthResult health,
        CancellationToken cancellationToken = default)
    {
        var safeHardware = new
        {
            operatingSystem = hardware.OperatingSystem,
            cpu = hardware.Cpu,
            memory = hardware.Memory,
            gpus = hardware.Gpus,
            volumes = hardware.Volumes.Select(volume => new
            {
                root = volume.RootPath,
                volume.FileSystem,
                volume.TotalBytes,
                volume.FreeBytes,
                volume.MediaType,
                volume.IsFixed,
                volume.IsSystem,
                volume.IsEncrypted,
                volume.IsSuitable,
                volume.RejectionReason
            }),
            hardware.IsLaptop,
            hardware.Warnings
        };
        var safeState = state is null ? null : new
        {
            state.SchemaVersion,
            state.ProductVersion,
            state.InstalledAt,
            state.SelectedModel,
            state.SelectedModelDigest,
            state.SelectedModelOwnedByHelper,
            modelStorageLocation = RedactPath(state.ModelStorageLocation),
            state.HardwareTier,
            state.Acceleration,
            state.Preferences,
            state.ExistingIntegrationPreserved,
            state.ReliabilityBaselineInstalled,
            state.Availability,
            state.LastHealthCheckAt,
            state.LastHealthCheckCode
        };
        var report = new
        {
            schemaVersion = 1,
            generatedAt = DateTimeOffset.UtcNow,
            product = ProductInfo.Name,
            version = ProductInfo.Version,
            privacy = "No hostname, username, serial number, Windows license identifier, network identifier, prompt, response, credential, or arbitrary file content is included.",
            hardware = safeHardware,
            installation = safeState,
            integration = new
            {
                codexConfigPresent = File.Exists(paths.CodexConfigFile),
                codexManagedSectionPresent = ContainsMarker(paths.CodexConfigFile, ProductInfo.ManagedConfigStart),
                agentsOverridePresent = File.Exists(paths.AgentsOverrideFile),
                agentsManagedSectionPresent = ContainsMarker(paths.AgentsOverrideFile, ProductInfo.ManagedAgentsStart),
                reliabilityBaselinePresent = ContainsMarker(paths.AgentsOverrideFile, ProductInfo.ManagedReliabilityStart)
            },
            health
        };

        var full = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, true);
        await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static bool ContainsMarker(string path, string marker)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var reader = new StreamReader(path);
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? RedactPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var root = Path.GetPathRoot(path) ?? "<drive>\\";
        return Path.Combine(root, "<redacted>", Path.GetFileName(path.TrimEnd('\\', '/')));
    }
}
