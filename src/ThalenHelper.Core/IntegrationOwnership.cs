namespace ThalenHelper.Core;

public enum IntegrationOwnershipStatus
{
    NotConfigured,
    ExternalUnmarked,
    ManagedValid,
    ManagedDrift,
    AmbiguousOrMalformed
}

public sealed record IntegrationOwnershipInspection(
    IntegrationOwnershipStatus Status,
    string Message);

public static class IntegrationOwnership
{
    public const string ManagedReviewerSection = "mcp_servers.local_gpu_reviewer";

    public static bool IsManagedByHelper(InstallationState? state)
        => state is not null
            && !state.ExistingIntegrationPreserved
            && state.ManagedConfigurationSections.Contains(ManagedReviewerSection, StringComparer.Ordinal);

    public static IntegrationOwnershipInspection Inspect(
        ProductPaths paths,
        InstallationState? state,
        CodexConfigManager? configManager = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var configOwnership = (configManager ?? new CodexConfigManager()).InspectOwnership(paths);
        var stateClaimsManaged = IsManagedByHelper(state);
        if (stateClaimsManaged)
        {
            return configOwnership == CodexIntegrationOwnership.ManagedValid
                ? new(IntegrationOwnershipStatus.ManagedValid, "The current Codex entry matches the packaged managed integration.")
                : new(IntegrationOwnershipStatus.ManagedDrift, "Managed state no longer matches the current Codex entry.");
        }

        return configOwnership switch
        {
            CodexIntegrationOwnership.ExternalUnmarked => new(
                IntegrationOwnershipStatus.ExternalUnmarked,
                "An external unmarked local_gpu_reviewer entry is present and remains outside helper control."),
            CodexIntegrationOwnership.ManagedValid => new(
                IntegrationOwnershipStatus.ManagedDrift,
                "The current Codex entry is marked as managed, but helper state does not prove ownership."),
            CodexIntegrationOwnership.ManagedDrift => new(
                IntegrationOwnershipStatus.ManagedDrift,
                "The marked Codex entry does not match the packaged integration contract."),
            CodexIntegrationOwnership.Invalid => new(
                IntegrationOwnershipStatus.AmbiguousOrMalformed,
                "The Codex reviewer configuration is malformed or ambiguous."),
            _ => new(IntegrationOwnershipStatus.NotConfigured, "No helper-owned Codex reviewer entry is configured.")
        };
    }
}
