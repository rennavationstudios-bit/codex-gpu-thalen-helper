namespace ThalenHelper.Core;

public sealed record ModelChangeResult(
    bool Success,
    string Code,
    string Message,
    string? PreviousModel,
    string? SelectedModel,
    ModelValidationResult? Validation);

public sealed class ModelChangeService
{
    private readonly ProductPaths _paths;
    private readonly StateStore _store;
    private readonly ControlService _control;
    private readonly InstallationManager _installation;
    private readonly Func<OllamaClient> _clientFactory;

    public ModelChangeService(
        ProductPaths paths,
        StateStore store,
        ControlService control,
        InstallationManager installation,
        Func<OllamaClient>? clientFactory = null)
    {
        _paths = paths;
        _store = store;
        _control = control;
        _installation = installation;
        _clientFactory = clientFactory ?? (() => new OllamaClient());
    }

    public async Task<ModelChangeResult> ChangeAsync(
        string modelTag,
        bool acceptRestrictedLicense,
        CancellationToken cancellationToken = default)
    {
        OllamaClient.ValidateModelIdentifier(modelTag);
        var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No installation state was found.");
        var ownership = IntegrationOwnership.Inspect(_paths, state);
        if (ownership.Status != IntegrationOwnershipStatus.ManagedValid)
        {
            return new ModelChangeResult(
                false,
                ownership.Status == IntegrationOwnershipStatus.ExternalUnmarked
                    ? "EXISTING_INTEGRATION_PRESERVED"
                    : "INTEGRATION_OWNERSHIP_DRIFT",
                ownership.Message + " The model was not changed.",
                state.SelectedModel,
                state.SelectedModel,
                null);
        }

        var catalog = new ModelCatalogService().LoadBundled();
        var model = catalog.Models.FirstOrDefault(item => string.Equals(item.Tag, modelTag, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The requested model is not in the audited catalog.");
        if (!model.CommercialUseAllowed && !acceptRestrictedLicense)
        {
            throw new InvalidOperationException("This model has a restrictive license and requires explicit acceptance.");
        }

        var recommendation = new ModelSelector().Recommend(new HardwareDetector().Detect(), catalog, false);
        if (recommendation.Model is null || model.ParameterBillions > recommendation.Model.ParameterBillions)
        {
            throw new InvalidOperationException("The requested model exceeds the current conservative hardware recommendation.");
        }

        var previousModel = state.SelectedModel;
        var previousDigest = state.SelectedModelDigest;
        var previousOwnership = state.SelectedModelOwnedByHelper;
        var previousTier = state.HardwareTier;
        var previousAvailability = state.Availability;
        var previousAcceleration = state.Acceleration;
        var pause = await _control.PauseAsync(cancellationToken).ConfigureAwait(false);
        if (!pause.Success)
        {
            return new ModelChangeResult(false, pause.Code, pause.Message, previousModel, previousModel, null);
        }

        try
        {
            using (var client = _clientFactory())
            {
                var models = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
                var present = models.Any(item => ModelIntegrity.NamesMatch(item.Name, model.Tag));
                if (!present)
                {
                    await client.PullAsync(model.Tag, cancellationToken).ConfigureAwait(false);
                }

                state.SelectedModelOwnedByHelper = !present;
            }

            state.SelectedModel = model.Tag;
            state.SelectedModelDigest = model.ExpectedDigest;
            state.HardwareTier = ParseTier(model.PerformanceTier);
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            var validation = await _installation.ValidateSelectedModelAsync(_paths, state, cancellationToken).ConfigureAwait(false);
            if (!validation.Success)
            {
                throw new ModelValidationException(validation);
            }

            state.Acceleration = validation.Acceleration;
            state.Availability = previousAvailability == HelperAvailability.Enabled
                ? HelperAvailability.Paused
                : previousAvailability;
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            if (previousAvailability == HelperAvailability.Enabled)
            {
                var resume = await _control.ResumeAsync(cancellationToken).ConfigureAwait(false);
                if (!resume.Success)
                {
                    throw new ModelActivationException(resume);
                }
            }

            return new ModelChangeResult(true, "MODEL_CHANGED", "The new model passed validation and the prior model was preserved.", previousModel, model.Tag, validation);
        }
        catch (Exception exception) when (exception is OllamaException or ModelValidationException or ModelActivationException)
        {
            try
            {
                await new ModelValidationStore(_paths.StateDirectory)
                    .RemoveAsync(model.Tag, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (ModelValidationStateException)
            {
                // The corrupt registry already fails routing closed; rollback must still restore product state.
            }

            state.SelectedModel = previousModel;
            state.SelectedModelDigest = previousDigest;
            state.SelectedModelOwnedByHelper = previousOwnership;
            state.HardwareTier = previousTier;
            state.Acceleration = previousAcceleration;
            state.Availability = previousAvailability == HelperAvailability.Enabled
                ? HelperAvailability.Paused
                : previousAvailability;
            await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
            if (previousAvailability == HelperAvailability.Enabled)
            {
                var rollbackResume = await _control.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
                if (!rollbackResume.Success)
                {
                    var failedValidation = (exception as ModelValidationException)?.Result;
                    return new ModelChangeResult(
                        false,
                        "MODEL_ROLLBACK_VERIFICATION_FAILED",
                        "The previous selection was restored, but its current runtime verification failed. Local review remains paused until repair succeeds.",
                        previousModel,
                        previousModel,
                        failedValidation);
                }
            }

            var validation = (exception as ModelValidationException)?.Result;
            var activation = (exception as ModelActivationException)?.Result;
            return new ModelChangeResult(
                false,
                validation?.Code ?? activation?.Code ?? "MODEL_CHANGE_FAILED",
                "The new model failed validation or passive runtime activation; the previous selection was restored.",
                previousModel,
                previousModel,
                validation);
        }
    }

    private static HardwareTier ParseTier(string tier) => tier.ToLowerInvariant() switch
    {
        "entry" => HardwareTier.Entry,
        "mid" => HardwareTier.Mid,
        "high" => HardwareTier.High,
        "enthusiast" => HardwareTier.Enthusiast,
        _ => HardwareTier.NoModel
    };

    private sealed class ModelValidationException(ModelValidationResult result) : Exception(result.Message)
    {
        public ModelValidationResult Result { get; } = result;
    }

    private sealed class ModelActivationException(ControlResult result) : Exception(result.Message)
    {
        public ControlResult Result { get; } = result;
    }
}
