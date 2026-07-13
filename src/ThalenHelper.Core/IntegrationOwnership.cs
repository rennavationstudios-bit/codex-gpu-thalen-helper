namespace ThalenHelper.Core;

public static class IntegrationOwnership
{
    public const string ManagedReviewerSection = "mcp_servers.local_gpu_reviewer";

    public static bool IsManagedByHelper(InstallationState? state)
        => state is not null
            && !state.ExistingIntegrationPreserved
            && state.ManagedConfigurationSections.Contains(ManagedReviewerSection, StringComparer.Ordinal);
}
