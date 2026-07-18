using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThalenHelper.Core;

/// <summary>
/// Bounded client for LM Studio's loopback-only native and OpenAI-compatible APIs.
/// Production connections are accepted only from the signed, current-user LM Studio process.
/// </summary>
public sealed partial class LmStudioClient : IDisposable
{
    public const string ProviderName = "LM Studio";
    public const int DefaultContextLength = 65_536;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly Uri DefaultBaseUri = new("http://127.0.0.1:1234", UriKind.Absolute);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _baseUri;

    public LmStudioClient()
        : this(DefaultBaseUri, null)
    {
    }

    internal LmStudioClient(Uri baseUri, HttpClient? httpClient)
    {
        _baseUri = ValidateBaseUri(baseUri);
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(CreateVerifiedHandler());
        _httpClient.BaseAddress = _baseUri;
    }

    public Uri BaseUri => _baseUri;

    public async Task<IReadOnlyList<LmStudioModelInfo>> GetModelsAsync(
        CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(
            HttpMethod.Get,
            "/api/v1/models",
            null,
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            throw new LmStudioException(
                "LMSTUDIO_MALFORMED_RESPONSE",
                "LM Studio returned an invalid model inventory.");
        }

        var results = new List<LmStudioModelInfo>();
        foreach (var model in models.EnumerateArray())
        {
            var key = RequiredString(model, "key", "LM Studio omitted a model key.");
            ValidateIdentifier(key, "model key");
            var instances = new List<LmStudioLoadedInstance>();
            if (model.TryGetProperty("loaded_instances", out var loaded)
                && loaded.ValueKind == JsonValueKind.Array)
            {
                foreach (var instance in loaded.EnumerateArray())
                {
                    var id = RequiredString(instance, "id", "LM Studio omitted a loaded instance identity.");
                    ValidateIdentifier(id, "instance identity");
                    var contextLength = instance.TryGetProperty("config", out var instanceConfig)
                        && instanceConfig.ValueKind == JsonValueKind.Object
                            ? GetOptionalInt32(instanceConfig, "context_length")
                            : GetOptionalInt32(instance, "context_length");
                    instances.Add(new LmStudioLoadedInstance(
                        id,
                        contextLength));
                }
            }

            results.Add(new LmStudioModelInfo(
                key,
                GetOptionalString(model, "display_name"),
                GetOptionalString(model, "publisher"),
                GetOptionalString(model, "architecture"),
                model.TryGetProperty("quantization", out var quantization)
                    && quantization.ValueKind == JsonValueKind.Object
                        ? GetOptionalString(quantization, "name")
                        : GetOptionalString(model, "quantization"),
                quantization.ValueKind == JsonValueKind.Object
                    ? GetOptionalInt32(quantization, "bits_per_weight")
                    : GetOptionalInt32(model, "bits_per_weight"),
                GetOptionalUInt64(model, "size_bytes") ?? GetOptionalUInt64(model, "size"),
                GetOptionalDouble(model, "params_string") ?? GetOptionalDouble(model, "parameter_count"),
                GetOptionalInt32(model, "max_context_length"),
                model.TryGetProperty("capabilities", out var capabilities)
                    && capabilities.ValueKind == JsonValueKind.Object
                        ? GetOptionalBoolean(capabilities, "trained_for_tool_use")
                        : GetOptionalBoolean(model, "trained_for_tool_use"),
                instances));
        }

        return results;
    }

    public async Task<LmStudioLoadResult> LoadAsync(
        string modelKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(modelKey, "model key");
        var payload = new
        {
            model = modelKey,
            context_length = DefaultContextLength,
            flash_attention = true,
            offload_kv_cache_to_gpu = true,
            echo_load_config = true
        };
        using var document = await SendJsonAsync(
            HttpMethod.Post,
            "/api/v1/models/load",
            payload,
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var instanceId = RequiredString(root, "instance_id", "LM Studio omitted the loaded instance identity.");
        ValidateIdentifier(instanceId, "instance identity");

        JsonElement? effectiveConfig = null;
        try
        {
            // LM Studio may return the requested key as either model or model_key. If present,
            // it is a security boundary and must exactly identify the requested model.
            var responseModel = GetOptionalString(root, "model_key") ?? GetOptionalString(root, "model");
            if (responseModel is not null && !string.Equals(responseModel, modelKey, StringComparison.Ordinal))
            {
                throw new LmStudioException(
                    "MODEL_RESPONSE_IDENTITY_MISMATCH",
                    "LM Studio loaded a model identity that does not match the validated route.");
            }

            if (!root.TryGetProperty("load_config", out var config)
                || config.ValueKind != JsonValueKind.Object
                || GetOptionalInt32(config, "context_length") != DefaultContextLength
                || GetOptionalBoolean(config, "flash_attention") != true
                || GetOptionalBoolean(config, "offload_kv_cache_to_gpu") != true)
            {
                throw new LmStudioException(
                    "LMSTUDIO_LOAD_CONFIG_MISMATCH",
                    "LM Studio did not confirm the required 64K, Flash Attention, and GPU KV-cache configuration.");
            }

            effectiveConfig = config.Clone();

            if (!await VerifyLoadedInstanceAsync(modelKey, instanceId, cancellationToken).ConfigureAwait(false))
            {
                throw new LmStudioException(
                    "MODEL_RESPONSE_IDENTITY_MISMATCH",
                    "LM Studio did not attribute the loaded instance to the validated model key.");
            }
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                using var ignored = await SendJsonAsync(
                    HttpMethod.Post,
                    "/api/v1/models/unload",
                    new { instance_id = instanceId },
                    TimeSpan.FromSeconds(15),
                    cleanup.Token).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the primary validation failure.
            }

            throw;
        }

        return new LmStudioLoadResult(
            ProviderName,
            modelKey,
            instanceId,
            effectiveConfig);
    }

    public async Task<LmStudioGenerationResult> GenerateAsync(
        string modelKey,
        string instanceId,
        string prompt,
        int outputTokens,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(modelKey, "model key");
        ValidateIdentifier(instanceId, "instance identity");
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (prompt.Length > 120_000)
        {
            throw new LmStudioException("INPUT_TOO_LARGE", "The bounded reviewer input exceeds 120,000 characters.");
        }

        if (outputTokens is < 64 or > 2_048)
        {
            throw new ArgumentOutOfRangeException(nameof(outputTokens));
        }

        var payload = new
        {
            model = instanceId,
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0.1,
            max_tokens = outputTokens,
            stream = false
        };
        using var document = await SendJsonAsync(
            HttpMethod.Post,
            "/v1/chat/completions",
            payload,
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var responseModel = RequiredString(root, "model", "LM Studio omitted the generated model identity.");
        if (!string.Equals(responseModel, instanceId, StringComparison.Ordinal)
            && !string.Equals(responseModel, modelKey, StringComparison.Ordinal))
        {
            throw new LmStudioException(
                "MODEL_RESPONSE_IDENTITY_MISMATCH",
                "LM Studio returned generated text for a model identity that does not match the validated route.");
        }

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() != 1
            || !choices[0].TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object)
        {
            throw new LmStudioException(
                "LMSTUDIO_MALFORMED_RESPONSE",
                "LM Studio returned an invalid non-streaming chat response.");
        }

        var content = RequiredString(message, "content", "LM Studio omitted generated text.");
        return new LmStudioGenerationResult(
            ProviderName,
            modelKey,
            instanceId,
            content.Trim(),
            GetOptionalString(choices[0], "finish_reason"),
            root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                ? GetOptionalInt32(usage, "prompt_tokens") ?? 0
                : 0,
            root.TryGetProperty("usage", out usage) && usage.ValueKind == JsonValueKind.Object
                ? GetOptionalInt32(usage, "completion_tokens") ?? 0
                : 0);
    }

    /// <summary>
    /// Uses LM Studio's native v1 chat contract so reasoning can be explicitly disabled.
    /// This is the preferred reviewer path for a model whose default reasoning mode is on.
    /// </summary>
    public async Task<LmStudioGenerationResult> GenerateReasoningOffAsync(
        string modelKey,
        string instanceId,
        string prompt,
        int outputTokens,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(modelKey, "model key");
        ValidateIdentifier(instanceId, "instance identity");
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (prompt.Length > 120_000)
        {
            throw new LmStudioException("INPUT_TOO_LARGE", "The bounded reviewer input exceeds 120,000 characters.");
        }

        if (outputTokens is < 64 or > 2_048)
        {
            throw new ArgumentOutOfRangeException(nameof(outputTokens));
        }

        var payload = new
        {
            model = instanceId,
            input = prompt,
            context_length = DefaultContextLength,
            reasoning = "off",
            temperature = 0.1,
            max_output_tokens = outputTokens,
            stream = false,
            store = false
        };
        using var document = await SendJsonAsync(
            HttpMethod.Post,
            "/api/v1/chat",
            payload,
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var responseInstance = RequiredString(
            root,
            "model_instance_id",
            "LM Studio omitted the generated model instance identity.");
        if (!string.Equals(responseInstance, instanceId, StringComparison.Ordinal))
        {
            throw new LmStudioException(
                "MODEL_RESPONSE_IDENTITY_MISMATCH",
                "LM Studio returned generated text for a model instance that does not match the validated route.");
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            throw new LmStudioException(
                "LMSTUDIO_MALFORMED_RESPONSE",
                "LM Studio returned an invalid native chat response.");
        }

        var messages = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            var type = GetOptionalString(item, "type");
            if (string.Equals(type, "reasoning", StringComparison.Ordinal))
            {
                throw new LmStudioException(
                    "LMSTUDIO_REASONING_NOT_DISABLED",
                    "LM Studio returned reasoning content after the reviewer required reasoning off.");
            }

            if (string.Equals(type, "message", StringComparison.Ordinal))
            {
                messages.Add(RequiredString(item, "content", "LM Studio omitted generated text."));
            }
        }

        if (messages.Count != 1)
        {
            throw new LmStudioException(
                "LMSTUDIO_MALFORMED_RESPONSE",
                "LM Studio did not return exactly one advisory message.");
        }

        var promptTokens = 0;
        var completionTokens = 0;
        if (root.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
        {
            promptTokens = GetOptionalInt32(stats, "input_tokens") ?? 0;
            completionTokens = GetOptionalInt32(stats, "total_output_tokens") ?? 0;
            if ((GetOptionalInt32(stats, "reasoning_output_tokens") ?? 0) != 0)
            {
                throw new LmStudioException(
                    "LMSTUDIO_REASONING_NOT_DISABLED",
                    "LM Studio reported reasoning tokens after the reviewer required reasoning off.");
            }
        }

        return new LmStudioGenerationResult(
            ProviderName,
            modelKey,
            responseInstance,
            messages[0].Trim(),
            "stop",
            promptTokens,
            completionTokens);
    }

    public async Task UnloadAndWaitAsync(
        string modelKey,
        string instanceId,
        TimeSpan? pollTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(modelKey, "model key");
        ValidateIdentifier(instanceId, "instance identity");
        using (var response = await SendJsonAsync(
            HttpMethod.Post,
            "/api/v1/models/unload",
            new { instance_id = instanceId },
            TimeSpan.FromMinutes(1),
            cancellationToken).ConfigureAwait(false))
        {
            var responseInstance = GetOptionalString(response.RootElement, "instance_id");
            if (responseInstance is not null
                && !string.Equals(responseInstance, instanceId, StringComparison.Ordinal))
            {
                throw new LmStudioException(
                    "MODEL_RESPONSE_IDENTITY_MISMATCH",
                    "LM Studio acknowledged unloading a different model instance.");
            }
        }

        var timeout = pollTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(pollTimeout));
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            var models = await GetModelsAsync(cancellationToken).ConfigureAwait(false);
            var stillLoaded = models.Any(model =>
                string.Equals(model.Key, modelKey, StringComparison.Ordinal)
                && model.LoadedInstances.Any(instance =>
                    string.Equals(instance.Id, instanceId, StringComparison.Ordinal)));
            if (!stillLoaded)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        } while (DateTimeOffset.UtcNow < deadline);

        throw new LmStudioException(
            "LMSTUDIO_UNLOAD_TIMEOUT",
            "LM Studio did not unload the selected model within the bounded wait.",
            retryable: true);
    }

    private async Task<bool> VerifyLoadedInstanceAsync(
        string modelKey,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        do
        {
            var models = await GetModelsAsync(cancellationToken).ConfigureAwait(false);
            if (models.Any(model =>
                string.Equals(model.Key, modelKey, StringComparison.Ordinal)
                && model.LoadedInstances.Any(instance =>
                    string.Equals(instance.Id, instanceId, StringComparison.Ordinal))))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        } while (DateTimeOffset.UtcNow < deadline);

        return false;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public static Uri ValidateBaseUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttp
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.Port != 1234)
        {
            throw new ArgumentException(
                "LM Studio must use the unauthenticated HTTP loopback endpoint on port 1234.",
                nameof(uri));
        }

        var loopback = IPAddress.TryParse(uri.Host, out var address)
            ? IPAddress.IsLoopback(address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            : string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (!loopback)
        {
            throw new ArgumentException("Non-loopback LM Studio endpoints are not allowed.", nameof(uri));
        }

        if (uri.AbsolutePath is not "/" and not "")
        {
            throw new ArgumentException("The LM Studio base URI cannot include a path.", nameof(uri));
        }

        return DefaultBaseUri;
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string path,
        object? payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!AllowedApiPathRegex().IsMatch(path))
        {
            throw new InvalidOperationException("LM Studio API path is not allowed.");
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new LmStudioException(
                    response.StatusCode == HttpStatusCode.NotFound
                        ? "LMSTUDIO_API_NOT_FOUND"
                        : "LMSTUDIO_HTTP_ERROR",
                    $"LM Studio returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, MaximumResponseBytes, linked.Token).ConfigureAwait(false);
            try
            {
                return JsonDocument.Parse(bytes);
            }
            catch (JsonException)
            {
                throw new LmStudioException("LMSTUDIO_MALFORMED_RESPONSE", "LM Studio returned malformed JSON.");
            }
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new LmStudioException("LMSTUDIO_TIMEOUT", "The bounded LM Studio request timed out.", true);
        }
        catch (LmStudioPeerIdentityException)
        {
            throw new LmStudioException(
                "LMSTUDIO_PEER_IDENTITY_UNVERIFIED",
                "The loopback endpoint is not a verified current-user LM Studio process.");
        }
        catch (HttpRequestException exception) when (ContainsPeerIdentityFailure(exception))
        {
            throw new LmStudioException(
                "LMSTUDIO_PEER_IDENTITY_UNVERIFIED",
                "The loopback endpoint is not a verified current-user LM Studio process.");
        }
        catch (HttpRequestException)
        {
            throw new LmStudioException(
                "LMSTUDIO_UNAVAILABLE",
                "The loopback LM Studio endpoint is unavailable.",
                true);
        }
    }

    private static SocketsHttpHandler CreateVerifiedHandler()
        => new()
        {
            AllowAutoRedirect = false,
            ConnectCallback = LmStudioPeerVerifier.ConnectVerifiedAsync,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseProxy = false
        };

    private static bool ContainsPeerIdentityFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is LmStudioPeerIdentityException)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return memory.ToArray();
            }

            if (memory.Length + read > maximumBytes)
            {
                throw new LmStudioException(
                    "LMSTUDIO_RESPONSE_TOO_LARGE",
                    "LM Studio exceeded the bounded response size.");
            }

            memory.Write(buffer, 0, read);
        }
    }

    private static string RequiredString(JsonElement element, string property, string error)
        => GetOptionalString(element, property)
            ?? throw new LmStudioException("LMSTUDIO_MALFORMED_RESPONSE", error);

    private static string? GetOptionalString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ulong? GetOptionalUInt64(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetUInt64(out var result) ? result : null;

    private static int? GetOptionalInt32(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static bool? GetOptionalBoolean(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static double? GetOptionalDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (text is not null)
            {
                var match = ParameterCountRegex().Match(text);
                if (match.Success
                    && double.TryParse(
                        match.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var billions))
                {
                    return billions;
                }
            }
        }

        return null;
    }

    private static void ValidateIdentifier(string identifier, string label)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !SafeIdentifierRegex().IsMatch(identifier))
        {
            throw new LmStudioException(
                "INVALID_MODEL_IDENTIFIER",
                $"The LM Studio {label} is invalid.");
        }
    }

    [GeneratedRegex("^(?:/api/v1/(?:models(?:/(?:load|unload))?|chat)|/v1/chat/completions)$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedApiPathRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierRegex();

    [GeneratedRegex("^([0-9]+(?:\\.[0-9]+)?)\\s*[Bb]$")]
    private static partial Regex ParameterCountRegex();
}

public sealed record LmStudioModelInfo(
    string Key,
    string? DisplayName,
    string? Publisher,
    string? Architecture,
    string? Quantization,
    int? BitsPerWeight,
    ulong? SizeBytes,
    double? ParameterBillions,
    int? MaxContextLength,
    bool? TrainedForToolUse,
    IReadOnlyList<LmStudioLoadedInstance> LoadedInstances);

public sealed record LmStudioLoadedInstance(string Id, int? ContextLength);

public sealed record LmStudioLoadResult(
    string Provider,
    string ModelKey,
    string InstanceId,
    JsonElement? EffectiveLoadConfig);

public sealed record LmStudioGenerationResult(
    string Provider,
    string ModelKey,
    string InstanceId,
    string Response,
    string? FinishReason,
    int PromptTokens,
    int CompletionTokens);

public sealed class LmStudioException : Exception
{
    public LmStudioException(string code, string message, bool retryable = false)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }
    public bool Retryable { get; }
}
