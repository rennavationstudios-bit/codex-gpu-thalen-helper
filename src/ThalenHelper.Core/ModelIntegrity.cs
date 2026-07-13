namespace ThalenHelper.Core;

public static class ModelIntegrity
{
    public static bool NamesMatch(string actual, string selected)
        => string.Equals(actual, selected, StringComparison.OrdinalIgnoreCase)
            || actual.StartsWith(selected + "@", StringComparison.OrdinalIgnoreCase);

    public static OllamaModelInfo? FindSelectedModel(
        IEnumerable<OllamaModelInfo> models,
        string? selectedModel)
        => string.IsNullOrWhiteSpace(selectedModel)
            ? null
            : models.FirstOrDefault(model => NamesMatch(model.Name, selectedModel));

    public static bool DigestMatches(string? actualDigest, string? expectedDigest)
    {
        var actual = NormalizeDigest(actualDigest);
        var expected = NormalizeDigest(expectedDigest);
        return actual is not null
            && expected is not null
            && actual.Length >= expected.Length
            && actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOperationallySafe(
        OllamaStartupVerification verification,
        InstallationState state)
        => verification.EndpointReachable
            && verification.Code is "OK" or "MANUAL_START_REQUIRED"
            && verification.ModelStorageConfigured
            && verification.LoopbackOnly
            && (string.IsNullOrWhiteSpace(state.SelectedModel)
                || (verification.SelectedModelStoredInConfiguredPath
                    && verification.SelectedModelAvailable
                    && verification.SelectedModelDigestMatches));

    private static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var normalized = digest.Trim();
        if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }

        return normalized.Length is >= 12 and <= 64
            && normalized.All(Uri.IsHexDigit)
            ? normalized.ToLowerInvariant()
            : null;
    }
}
