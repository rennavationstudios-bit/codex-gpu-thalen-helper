using System.Diagnostics;
using System.Security.Cryptography;

namespace ThalenHelper.Core;

public sealed record LmStudioRegistrationResult(
    bool Success,
    string Code,
    string Message,
    string Provider,
    string Model,
    string? Digest,
    long ExactMilliseconds,
    long ReviewMilliseconds,
    bool Unloaded);

public sealed class LmStudioRegistrationService
{
    private readonly StateStore _stateStore;
    private readonly ModelValidationStore _validationStore;
    private readonly LmStudioClient _client;
    private readonly ModelManifest _catalog;
    private readonly ActiveModelTracker _tracker;
    private readonly ResourcePressureGuard _pressure = new();

    public LmStudioRegistrationService(ProductPaths paths, StateStore stateStore, LmStudioClient client)
    {
        _stateStore = stateStore;
        _validationStore = new ModelValidationStore(paths.StateDirectory);
        _client = client;
        _catalog = new ModelCatalogService().LoadBundled();
        _tracker = new ActiveModelTracker(paths.StateDirectory);
    }

    public async Task<LmStudioRegistrationResult> ValidateAndEnableAsync(
        string modelKey,
        string modelPath,
        CancellationToken cancellationToken = default)
    {
        var entry = _catalog.Models.FirstOrDefault(model =>
            string.Equals(model.Provider, ModelProviders.LmStudio, StringComparison.OrdinalIgnoreCase)
            && string.Equals(model.Tag, modelKey, StringComparison.Ordinal));
        if (entry is null || string.IsNullOrWhiteSpace(entry.ExpectedDigest))
        {
            return Failure("MODEL_NOT_AUDITED", "The LM Studio model is not present in the audited catalog.", modelKey);
        }

        var fullPath = Path.GetFullPath(modelPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length != (long)entry.ExpectedDownloadBytes)
        {
            return Failure("MODEL_FILE_IDENTITY_MISMATCH", "The selected GGUF is missing, redirected, or has an unexpected size.", modelKey);
        }

        var digest = await ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(digest, ModelValidationStore.NormalizeFullDigest(entry.ExpectedDigest), StringComparison.Ordinal))
        {
            return Failure("MODEL_DIGEST_MISMATCH", "The selected GGUF does not match the audited full SHA-256 digest.", modelKey);
        }

        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return Failure("NOT_CONFIGURED", "Install the helper before registering an LM Studio model.", modelKey);
        }

        var exactMs = 0L;
        var reviewMs = 0L;
        var unloaded = false;
        LmStudioLoadResult? load = null;
        try
        {
            using var lease = await GpuCoordination.AcquireAsync(
                ReviewBusyBehavior.Queue,
                TimeSpan.FromSeconds(120),
                cancellationToken).ConfigureAwait(false);
            var inventory = await _client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            var model = inventory.SingleOrDefault(candidate => string.Equals(candidate.Key, modelKey, StringComparison.Ordinal));
            if (model is null || model.SizeBytes != entry.ExpectedDownloadBytes || model.MaxContextLength < 65_536)
            {
                return Failure("MODEL_IDENTITY_MISMATCH", "LM Studio did not report the exact audited model identity and capabilities.", modelKey, digest);
            }
            if (inventory.SelectMany(candidate => candidate.LoadedInstances).Any())
            {
                return Failure("FOREIGN_MODEL_LOADED", "LM Studio already has a model loaded. The helper will not unload or replace a user-owned instance.", modelKey, digest);
            }

            var routedState = state with { SelectedModel = modelKey, SelectedModelDigest = digest, SelectedModelProvider = ModelProviders.LmStudio };
            var pressure = _pressure.Check(routedState, selectedModelAlreadyLoaded: false);
            if (!pressure.Allowed)
            {
                return Failure(pressure.Code, pressure.Message, modelKey, digest);
            }

            load = await _client.LoadAsync(modelKey, cancellationToken).ConfigureAwait(false);
            _tracker.Set(new ActiveModelReference(ModelProviders.LmStudio, modelKey, load.InstanceId));
            var timer = Stopwatch.StartNew();
            var exact = await _client.GenerateReasoningOffAsync(modelKey, load.InstanceId,
                "Return exactly this text and nothing else: THALEN_LMSTUDIO_OK", 64, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            exactMs = timer.ElapsedMilliseconds;
            if (!string.Equals(exact.Response, "THALEN_LMSTUDIO_OK", StringComparison.Ordinal))
            {
                return Failure("MODEL_EXACT_OUTPUT_FAILED", "Qwythos did not pass the reasoning-off exact-output check.", modelKey, digest);
            }

            timer.Restart();
            var review = await _client.GenerateReasoningOffAsync(modelKey, load.InstanceId,
                "Review this code as an advisory reviewer. Return one concise risk and one test suggestion only:\npublic bool IsAllowed(string? value) => value!.Length > 0;",
                256, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            reviewMs = timer.ElapsedMilliseconds;
            if (string.IsNullOrWhiteSpace(review.Response) || review.Response.Length > 8_000)
            {
                return Failure("MODEL_REVIEW_VALIDATION_FAILED", "Qwythos did not return a bounded review response.", modelKey, digest);
            }
        }
        catch (LmStudioException exception)
        {
            return Failure(exception.Code, exception.Message, modelKey, digest, exactMs, reviewMs, unloaded);
        }
        catch (OllamaException exception)
        {
            return Failure(exception.Code, exception.Message, modelKey, digest, exactMs, reviewMs, unloaded);
        }
        finally
        {
            if (load is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try
                {
                    await _client.UnloadAndWaitAsync(modelKey, load.InstanceId, TimeSpan.FromSeconds(30), cleanup.Token).ConfigureAwait(false);
                    unloaded = true;
                    _tracker.Clear(modelKey);
                }
                catch (Exception exception) when (exception is LmStudioException or OperationCanceledException)
                {
                    unloaded = false;
                }
            }
        }

        if (!unloaded)
        {
            return Failure("GPU_RELEASE_FAILED", "Qwythos ran but LM Studio did not prove the helper-created instance was unloaded; registration was refused.", modelKey, digest, exactMs, reviewMs, false);
        }

        await _validationStore.UpsertAsync(new ModelValidationEntry(
            modelKey, digest, ModelValidationStore.CurrentProtocolVersion, DateTimeOffset.UtcNow,
            exactMs, reviewMs, "LM Studio GPU", null, 65_536, ModelProviders.LmStudio), cancellationToken).ConfigureAwait(false);
        info.Refresh();
        state.RegisteredLocalModels.RemoveAll(item =>
            string.Equals(item.Provider, ModelProviders.LmStudio, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Model, modelKey, StringComparison.Ordinal));
        state.RegisteredLocalModels.Add(new LocalModelRegistration(
            ModelProviders.LmStudio, modelKey, digest, fullPath, DateTimeOffset.UtcNow,
            info.Length, info.LastWriteTimeUtc));
        state.Preferences = state.Preferences with { PreferLmStudioForStandardAndDeep = true, ModelSelectionMode = ModelSelectionMode.Automatic };
        state.Availability = HelperAvailability.Enabled;
        state.ProductVersion = ProductInfo.Version;
        await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new LmStudioRegistrationResult(true, "LMSTUDIO_MODEL_VALIDATED", "Qwythos is validated, enabled for automatic standard/deep routing, and unloaded.", ModelProviders.LmStudio, modelKey, digest, exactMs, reviewMs, true);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static LmStudioRegistrationResult Failure(string code, string message, string model, string? digest = null,
        long exactMs = 0, long reviewMs = 0, bool unloaded = false)
        => new(false, code, message, ModelProviders.LmStudio, model, digest, exactMs, reviewMs, unloaded);
}
