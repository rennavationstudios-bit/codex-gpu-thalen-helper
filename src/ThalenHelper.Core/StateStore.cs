using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThalenHelper.Core;

internal sealed record StateStoreLoadResult(
    InstallationState? State,
    ProtectedFileSnapshot Revision);

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private readonly string _mutexName;

    public StateStore(string path)
    {
        _path = System.IO.Path.GetFullPath(path);
        var identity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))[..24];
        _mutexName = @"Local\CodexGpuThalenHelperState-" + identity;
    }

    public string Path => _path;

    internal string MutexName => _mutexName;

    public async Task<InstallationState?> LoadAsync(CancellationToken cancellationToken = default)
        => (await LoadWithRevisionAsync(cancellationToken).ConfigureAwait(false)).State;

    internal Task<StateStoreLoadResult> LoadWithRevisionAsync(CancellationToken cancellationToken = default)
        => RunLockedAsync(() =>
        {
            var revision = ProtectedFileTransaction.Capture(_path);
            if (!revision.Exists)
            {
                return new StateStoreLoadResult(null, revision);
            }

            var state = JsonSerializer.Deserialize<InstallationState>(revision.Bytes, JsonOptions)
                ?? throw new InvalidDataException("Installation state is empty or malformed.");
            return new StateStoreLoadResult(state, revision);
        }, cancellationToken);

    public async Task SaveAsync(InstallationState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await RunLockedAsync(() =>
        {
            var current = ProtectedFileTransaction.Capture(_path);
            if (state.ModelStorageTransition is not null)
            {
                throw new InvalidOperationException(
                    "Model storage transition state must be written with revision-bound compare-and-swap.");
            }

            if (current.Exists)
            {
                var currentState = JsonSerializer.Deserialize<InstallationState>(current.Bytes, JsonOptions)
                    ?? throw new InvalidDataException("Installation state is empty or malformed.");
                if (currentState.ModelStorageTransition is not null)
                {
                    throw new InvalidOperationException(
                        "A model storage transition is pending. This stale state write was refused.");
                }
            }

            _ = SaveIfUnchanged(state, current);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal Task<ProtectedFileSnapshot> SaveIfUnchangedAsync(
        InstallationState state,
        ProtectedFileSnapshot expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(expected);
        return RunLockedAsync(() => SaveIfUnchanged(state, expected), cancellationToken);
    }

    private ProtectedFileSnapshot SaveIfUnchanged(InstallationState state, ProtectedFileSnapshot expected)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        ProtectedFileTransaction.ReplaceIfUnchanged(_path, expected, bytes);
        return new ProtectedFileSnapshot(
            true,
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private Task<T> RunLockedAsync<T>(Func<T> action, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            using var mutex = new Mutex(false, _mutexName);
            var acquired = false;
            try
            {
                try
                {
                    var index = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle]);
                    if (index == 1)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    acquired = true;
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return action();
            }
            finally
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
            }
        }, CancellationToken.None);

    private Task RunLockedAsync(Action action, CancellationToken cancellationToken)
        => RunLockedAsync(() =>
        {
            action();
            return true;
        }, cancellationToken);
}
