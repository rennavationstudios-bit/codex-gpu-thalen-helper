using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

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
        if (string.IsNullOrWhiteSpace(modelKey)
            || modelKey.Length > 128
            || !modelKey.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/'))
        {
            return Failure("MODEL_NOT_AUDITED", "The LM Studio model is not present in the audited catalog.", modelKey);
        }

        // A removed catalog entry or any later failure must not leave the prior pass routable.
        await _validationStore.RemoveAsync(ModelProviders.LmStudio, modelKey, CancellationToken.None).ConfigureAwait(false);

        if (!LmStudioModelFileBinding.ExactLoadedFileBindingSupported)
        {
            return Failure(
                "LMSTUDIO_EXACT_FILE_BINDING_UNAVAILABLE",
                "LM Studio registration is temporarily disabled because the loopback API does not expose the absolute file behind a loaded model key. The helper will not claim that an arbitrary GGUF path is the exact file LM Studio will execute.",
                modelKey);
        }

        var entry = _catalog.Models.FirstOrDefault(model =>
            string.Equals(model.Provider, ModelProviders.LmStudio, StringComparison.OrdinalIgnoreCase)
            && string.Equals(model.Tag, modelKey, StringComparison.Ordinal));
        if (entry is null || string.IsNullOrWhiteSpace(entry.ExpectedDigest))
        {
            return Failure("MODEL_NOT_AUDITED", "The LM Studio model is not present in the audited catalog.", modelKey);
        }

        var fullPath = Path.GetFullPath(modelPath);
        if (!LmStudioModelFileBinding.IsCanonicalCatalogBinding(entry, modelKey, fullPath))
        {
            return Failure("MODEL_FILE_CATALOG_BINDING_MISMATCH", "The selected GGUF path is not the exact indexed path bound to this audited LM Studio model key.", modelKey);
        }

        if (!LmStudioModelFileBinding.TryOpen(fullPath, out var modelStream, out var auditedProof)
            || auditedProof.Length != (long)entry.ExpectedDownloadBytes)
        {
            return Failure("MODEL_FILE_IDENTITY_MISMATCH", "The selected GGUF is missing, redirected, or has an unexpected size.", modelKey);
        }

        string digest;
        await using (modelStream.ConfigureAwait(false))
        {
            digest = await ComputeSha256Async(modelStream, cancellationToken).ConfigureAwait(false);
            if (!LmStudioModelFileBinding.TryReadProof(modelStream.SafeFileHandle, fullPath, out var proofAfterHash)
                || proofAfterHash != auditedProof)
            {
                return Failure("MODEL_FILE_IDENTITY_CHANGED", "The selected GGUF changed while its full digest was being validated.", modelKey);
            }
        }
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

            if (!LmStudioModelFileBinding.MatchesCurrentFile(auditedProof))
            {
                return Failure("MODEL_FILE_IDENTITY_CHANGED", "The validated LM Studio GGUF changed before the audited model key could be loaded.", modelKey, digest);
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

        if (!LmStudioModelFileBinding.MatchesCurrentFile(auditedProof))
        {
            return Failure("MODEL_FILE_IDENTITY_CHANGED", "The validated LM Studio GGUF changed before its registration could be recorded.", modelKey, digest, exactMs, reviewMs, true);
        }

        await _validationStore.UpsertAsync(new ModelValidationEntry(
            modelKey, digest, ModelValidationStore.CurrentProtocolVersion, DateTimeOffset.UtcNow,
            exactMs, reviewMs, "LM Studio GPU", null, 65_536, ModelProviders.LmStudio), cancellationToken).ConfigureAwait(false);
        state.RegisteredLocalModels.RemoveAll(item =>
            string.Equals(item.Provider, ModelProviders.LmStudio, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Model, modelKey, StringComparison.Ordinal));
        state.RegisteredLocalModels.Add(new LocalModelRegistration(
            ModelProviders.LmStudio, modelKey, digest, fullPath, DateTimeOffset.UtcNow,
            auditedProof.Length, auditedProof.LastWriteTimeUtc, auditedProof.FileIdentity));
        state.Preferences = state.Preferences with { PreferLmStudioForStandardAndDeep = true, ModelSelectionMode = ModelSelectionMode.Automatic };
        state.Availability = HelperAvailability.Enabled;
        state.ProductVersion = ProductInfo.Version;
        await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new LmStudioRegistrationResult(true, "LMSTUDIO_MODEL_VALIDATED", "Qwythos is validated, enabled for automatic standard/deep routing, and unloaded.", ModelProviders.LmStudio, modelKey, digest, exactMs, reviewMs, true);
    }

    private static async Task<string> ComputeSha256Async(FileStream stream, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static LmStudioRegistrationResult Failure(string code, string message, string model, string? digest = null,
        long exactMs = 0, long reviewMs = 0, bool unloaded = false)
        => new(false, code, message, ModelProviders.LmStudio, model, digest, exactMs, reviewMs, unloaded);
}

internal sealed record LmStudioModelFileProof(
    string FullPath,
    string FileIdentity,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

internal static class LmStudioModelFileBinding
{
    // LM Studio's current loopback API reports a model key and metadata, but not the
    // canonical absolute file loaded for that key. Keep automatic enrollment closed
    // until the runtime supplies an exact file identity that can be bound to this proof.
    internal static bool ExactLoadedFileBindingSupported => false;

    private const uint ReparsePointAttribute = (uint)FileAttributes.ReparsePoint;
    private const uint DirectoryAttribute = (uint)FileAttributes.Directory;

    internal static bool IsCanonicalCatalogBinding(
        ModelCatalogEntry entry,
        string modelKey,
        string modelPath)
    {
        if (!string.Equals(entry.Provider, ModelProviders.LmStudio, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(entry.Tag, modelKey, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(entry.IndexedModelPath)
            || Path.IsPathRooted(entry.IndexedModelPath))
        {
            return false;
        }

        try
        {
            var segments = entry.IndexedModelPath
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.None);
            if (segments.Length == 0
                || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
            {
                return false;
            }

            var indexedSuffix = Path.Combine(segments);
            var fullPath = Path.GetFullPath(modelPath);
            return fullPath.EndsWith(
                Path.DirectorySeparatorChar + indexedSuffix,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool TryOpen(
        string path,
        out FileStream stream,
        out LmStudioModelFileProof proof)
    {
        stream = null!;
        proof = null!;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (TryReadProof(stream.SafeFileHandle, fullPath, out proof))
            {
                return true;
            }

            stream.Dispose();
            stream = null!;
            proof = null!;
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            stream?.Dispose();
            stream = null!;
            proof = null!;
            return false;
        }
    }

    internal static bool TryReadProof(
        SafeFileHandle handle,
        string fullPath,
        out LmStudioModelFileProof proof)
    {
        proof = null!;
        if (handle.IsInvalid
            || !GetFileInformationByHandle(handle, out var information)
            || (information.FileAttributes & (ReparsePointAttribute | DirectoryAttribute)) != 0)
        {
            return false;
        }

        var length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        var creationTicks = ((long)information.CreationTimeHigh << 32) | information.CreationTimeLow;
        var lastWriteTicks = ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow;
        DateTimeOffset lastWriteTime;
        try
        {
            lastWriteTime = new DateTimeOffset(DateTime.FromFileTimeUtc(lastWriteTicks));
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        proof = new LmStudioModelFileProof(
            Path.GetFullPath(fullPath),
            $"{information.VolumeSerialNumber:x8}:{information.FileIndexHigh:x8}{information.FileIndexLow:x8}:{creationTicks:x16}",
            length,
            lastWriteTime);
        return true;
    }

    internal static bool MatchesCurrentFile(LmStudioModelFileProof expected)
    {
        if (!TryOpen(expected.FullPath, out var stream, out var current))
        {
            return false;
        }

        using (stream)
        {
            return current == expected;
        }
    }

    internal static bool MatchesRegistration(
        LocalModelRegistration registration,
        ModelCatalogEntry? catalog)
    {
        if (catalog is null
            || string.IsNullOrWhiteSpace(registration.FileIdentity)
            || !IsCanonicalCatalogBinding(catalog, registration.Model, registration.Path)
            || !TryOpen(registration.Path, out var stream, out var current))
        {
            return false;
        }

        using (stream)
        {
            return string.Equals(current.FullPath, Path.GetFullPath(registration.Path), StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.FileIdentity, registration.FileIdentity, StringComparison.Ordinal)
                && current.Length == registration.Length
                && registration.LastWriteTimeUtc.HasValue
                && current.LastWriteTimeUtc == registration.LastWriteTimeUtc.Value.ToUniversalTime();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
