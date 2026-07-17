using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThalenHelper.Core;

public sealed record ModelValidationEntry(
    string Tag,
    string Digest,
    int ProtocolVersion,
    DateTimeOffset PassedAtUtc,
    long ExactMilliseconds,
    long ReviewMilliseconds,
    string Processor,
    ulong? SizeVramBytes,
    int? ContextLength,
    string Provider = ModelProviders.Ollama);

public sealed record ModelValidationRegistry(
    int SchemaVersion,
    IReadOnlyList<ModelValidationEntry> Entries)
{
    public static ModelValidationRegistry Empty { get; } = new(ModelValidationStore.SchemaVersion, []);

    public bool HasCurrentPass(string tag, string? digest)
        => HasCurrentPass(ModelProviders.Ollama, tag, digest);

    public bool HasCurrentPass(string provider, string tag, string? digest)
    {
        var normalized = ModelValidationStore.NormalizeFullDigest(digest);
        return normalized is not null && Entries.Any(entry =>
            string.Equals(ModelProviders.Normalize(entry.Provider), ModelProviders.Normalize(provider), StringComparison.Ordinal)
            && ModelIntegrity.NamesMatch(entry.Tag, tag)
            && string.Equals(entry.Digest, normalized, StringComparison.Ordinal)
            && entry.ProtocolVersion == ModelValidationStore.CurrentProtocolVersion);
    }
}

public sealed class ModelValidationStateException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code => "VALIDATION_STATE_INVALID";
}

public sealed class ModelValidationStore
{
    public const int SchemaVersion = 1;
    public const int CurrentProtocolVersion = 1;
    public const string FileName = "model-validations.json";
    private const int MaximumEntries = 128;
    private const int MaximumFileBytes = 256 * 1024;
    private readonly string _path;
    private readonly string _mutexName;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ModelValidationStore(string stateDirectory)
    {
        var directory = System.IO.Path.GetFullPath(stateDirectory);
        _path = System.IO.Path.Combine(directory, FileName);
        var identity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..24];
        _mutexName = @"Local\CodexGpuThalenHelperModelValidations-" + identity;
    }

    public string Path => _path;

    public Task<ModelValidationRegistry> LoadAsync(CancellationToken cancellationToken = default)
        => RunLockedAsync(LoadUnlocked, cancellationToken);

    public Task RemoveAsync(string tag, CancellationToken cancellationToken = default)
        => RemoveAsync(ModelProviders.Ollama, tag, cancellationToken);

    public Task RemoveAsync(string provider, string tag, CancellationToken cancellationToken = default)
    {
        ValidateModelKey(provider, tag);
        return RunLockedAsync(() =>
        {
            var registry = LoadUnlocked();
            var retained = registry.Entries
                .Where(entry => !string.Equals(ModelProviders.Normalize(entry.Provider), ModelProviders.Normalize(provider), StringComparison.Ordinal)
                    || !ModelIntegrity.NamesMatch(entry.Tag, tag))
                .ToArray();
            if (retained.Length != registry.Entries.Count)
            {
                SaveUnlocked(new ModelValidationRegistry(SchemaVersion, retained));
            }
        }, cancellationToken);
    }

    public Task UpsertAsync(ModelValidationEntry entry, CancellationToken cancellationToken = default)
    {
        ValidateEntry(entry);
        return RunLockedAsync(() =>
        {
            var registry = LoadUnlocked();
            var entries = registry.Entries
                .Where(existing => !string.Equals(ModelProviders.Normalize(existing.Provider), ModelProviders.Normalize(entry.Provider), StringComparison.Ordinal)
                    || !ModelIntegrity.NamesMatch(existing.Tag, entry.Tag))
                .Append(entry with { Digest = NormalizeFullDigest(entry.Digest)!, Provider = ModelProviders.Normalize(entry.Provider) })
                .OrderBy(item => item.Provider, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (entries.Length > MaximumEntries)
            {
                throw Invalid("The model validation registry exceeds its entry limit.");
            }

            SaveUnlocked(new ModelValidationRegistry(SchemaVersion, entries));
        }, cancellationToken);
    }

    private ModelValidationRegistry LoadUnlocked()
    {
        if (!File.Exists(_path))
        {
            return ModelValidationRegistry.Empty;
        }

        try
        {
            var info = new FileInfo(_path);
            if (info.Length is < 2 or > MaximumFileBytes
                || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw Invalid("The model validation registry is not a bounded regular file.");
            }

            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var registry = JsonSerializer.Deserialize<ModelValidationRegistry>(stream, JsonOptions)
                ?? throw Invalid("The model validation registry is empty or malformed.");
            ValidateRegistry(registry);
            return registry;
        }
        catch (ModelValidationStateException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw Invalid("The model validation registry is corrupt or unreadable.", exception);
        }
    }

    private void SaveUnlocked(ModelValidationRegistry registry)
    {
        ValidateRegistry(registry);
        var directory = System.IO.Path.GetDirectoryName(_path)
            ?? throw Invalid("The model validation registry path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, registry, JsonOptions);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaximumFileBytes)
                {
                    throw Invalid("The model validation registry exceeds its size limit.");
                }
            }

            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
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

    private static void ValidateRegistry(ModelValidationRegistry registry)
    {
        if (registry.SchemaVersion != SchemaVersion
            || registry.Entries is null
            || registry.Entries.Count > MaximumEntries)
        {
            throw Invalid("The model validation registry schema or entry count is invalid.");
        }

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in registry.Entries)
        {
            ValidateEntry(entry);
            if (!tags.Add(ModelProviders.Normalize(entry.Provider) + "\0" + entry.Tag))
            {
                throw Invalid("The model validation registry contains duplicate model tags.");
            }
        }
    }

    private static void ValidateEntry(ModelValidationEntry entry)
    {
        try
        {
            ValidateModelKey(entry.Provider, entry.Tag);
        }
        catch (ArgumentException exception)
        {
            throw Invalid("The model validation registry contains an invalid model tag.", exception);
        }

        if (entry.Tag.Length > 128
            || NormalizeFullDigest(entry.Digest) is null
            || entry.ProtocolVersion is < 1 or > 1_000
            || entry.PassedAtUtc.Offset != TimeSpan.Zero
            || entry.PassedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5)
            || entry.ExactMilliseconds is < 0 or > 600_000
            || entry.ReviewMilliseconds is < 0 or > 600_000
            || string.IsNullOrWhiteSpace(entry.Processor)
            || entry.Processor.Length > 128
            || entry.Processor.Any(char.IsControl)
            || entry.SizeVramBytes > 1024UL * 1024 * 1024 * 1024
            || entry.ContextLength is < 0 or > 131_072)
        {
            throw Invalid("The model validation registry contains an invalid or unbounded entry.");
        }
    }

    private static void ValidateModelKey(string? provider, string tag)
    {
        if (!ModelProviders.IsSupported(provider))
        {
            throw Invalid("The model validation registry contains an unsupported provider.");
        }

        if (string.Equals(ModelProviders.Normalize(provider), ModelProviders.Ollama, StringComparison.Ordinal))
        {
            OllamaClient.ValidateModelIdentifier(tag);
            return;
        }

        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 128
            || !tag.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/'))
        {
            throw Invalid("The model validation registry contains an invalid LM Studio model key.");
        }
    }

    internal static string? NormalizeFullDigest(string? digest)
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

        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized.ToLowerInvariant()
            : null;
    }

    private static ModelValidationStateException Invalid(string message, Exception? inner = null)
        => new(message, inner);

}
