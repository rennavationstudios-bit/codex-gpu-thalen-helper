namespace ThalenHelper.Core;

internal sealed record OllamaRuntimeOwnershipCheck(
    bool Allowed,
    bool SelectedModelAlreadyLoaded,
    string Code,
    string Message);

internal static class OllamaRuntimeOwnership
{
    public const string ForeignModelCode = "FOREIGN_MODEL_LOADED";

    public static OllamaRuntimeOwnershipCheck Inspect(
        IReadOnlyList<OllamaRunningModel> runningModels,
        ActiveModelTrackerInspection tracker,
        string requestedModel)
    {
        ArgumentNullException.ThrowIfNull(runningModels);
        ArgumentNullException.ThrowIfNull(tracker);
        OllamaClient.ValidateModelIdentifier(requestedModel);

        if (tracker.Status == ActiveModelTrackerStatus.Invalid)
        {
            return Refused("The helper model ownership marker is invalid, so the Ollama runtime cannot be claimed safely.");
        }

        if (runningModels.Count == 0)
        {
            return tracker.Status == ActiveModelTrackerStatus.Absent
                ? new OllamaRuntimeOwnershipCheck(
                    true,
                    false,
                    "OLLAMA_RUNTIME_UNCLAIMED",
                    "No Ollama model is loaded and the runtime can be claimed for this bounded operation.")
                : Refused("The helper model ownership marker is stale because its tracked Ollama model is not running.");
        }

        var reference = tracker.Reference;
        if (reference is null
            || !MatchesTrackedOllamaModel(tracker, requestedModel)
            || runningModels.Count != 1
            || !ModelIntegrity.NamesMatch(runningModels[0].Name, reference.Model))
        {
            return Refused("Ollama already has a model loaded that is not provably owned by this helper operation. It was left untouched.");
        }

        return new OllamaRuntimeOwnershipCheck(
            true,
            true,
            "OLLAMA_RUNTIME_HELPER_OWNED",
            "The single loaded Ollama model matches the helper ownership marker and requested model.");
    }

    public static bool MatchesTrackedOllamaModel(
        ActiveModelTrackerInspection tracker,
        string requestedModel)
    {
        var reference = tracker.Reference;
        return tracker.Status == ActiveModelTrackerStatus.Valid
            && reference is not null
            && string.Equals(ModelProviders.Normalize(reference.Provider), ModelProviders.Ollama, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(reference.InstanceId)
            && ModelIntegrity.NamesMatch(reference.Model, requestedModel);
    }

    private static OllamaRuntimeOwnershipCheck Refused(string message)
        => new(false, false, ForeignModelCode, message);
}
