using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThalenHelper.Core;

public sealed partial class ModelCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ModelManifest Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = File.OpenRead(fullPath);
        var manifest = JsonSerializer.Deserialize<ModelManifest>(stream, JsonOptions)
            ?? throw new InvalidDataException("The model catalog is empty.");
        Validate(manifest);
        return manifest;
    }

    public ModelManifest LoadBundled()
    {
        return Load(Path.Combine(AppContext.BaseDirectory, "model-catalog", "models.v1.json"));
    }

    public void Validate(ModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = new List<string>();
        if (manifest.SchemaVersion != 1)
        {
            errors.Add("schemaVersion must be 1");
        }

        if (manifest.Models.Count == 0)
        {
            errors.Add("at least one model is required");
        }

        var duplicateTags = manifest.Models
            .GroupBy(model => model.Tag, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        errors.AddRange(duplicateTags.Select(tag => $"duplicate model tag: {tag}"));

        foreach (var model in manifest.Models)
        {
            if (!ModelIdentifierRegex().IsMatch(model.Tag))
            {
                errors.Add($"invalid model identifier: {model.Tag}");
            }

            if (!string.Equals(model.Provider, "Ollama", StringComparison.Ordinal))
            {
                errors.Add($"unsupported provider for {model.Tag}");
            }

            if (string.IsNullOrWhiteSpace(model.ExpectedDigest)
                || !ExpectedDigestRegex().IsMatch(model.ExpectedDigest))
            {
                errors.Add($"invalid expected SHA-256 digest for {model.Tag}");
            }

            if (model.ExpectedDownloadBytes == 0 || model.MinimumFreeDiskGiB <= 0)
            {
                errors.Add($"invalid size metadata for {model.Tag}");
            }

            if (model.SafeDefaultContextTokens > model.MaximumContextTokens)
            {
                errors.Add($"default context exceeds maximum for {model.Tag}");
            }

            if (model.AutomaticSelectionAllowed && !model.CommercialUseAllowed)
            {
                errors.Add($"restricted-license model cannot be auto-selected: {model.Tag}");
            }

            if (!Uri.TryCreate(model.LicenseUrl, UriKind.Absolute, out _)
                || !Uri.TryCreate(model.SourceUrl, UriKind.Absolute, out _))
            {
                errors.Add($"invalid source or license URL for {model.Tag}");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException("Invalid model catalog: " + string.Join("; ", errors));
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._/-]*:[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdentifierRegex();

    [GeneratedRegex("^(?:sha256:)?[a-fA-F0-9]{12,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExpectedDigestRegex();
}
