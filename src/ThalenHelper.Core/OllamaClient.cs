using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThalenHelper.Core;

public sealed partial class OllamaClient : IDisposable
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _baseUri;

    public OllamaClient(Uri? baseUri = null)
        : this(baseUri, null)
    {
    }

    internal OllamaClient(Uri? baseUri, HttpClient? httpClient)
    {
        _baseUri = ValidateBaseUri(baseUri ?? ResolveConfiguredBaseUri());
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(CreateVerifiedHandler());
        _httpClient.BaseAddress = _baseUri;
    }

    public Uri BaseUri => _baseUri;

    public async Task<IReadOnlyList<OllamaModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(HttpMethod.Get, "/api/tags", null, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            throw new OllamaException("OLLAMA_MALFORMED_RESPONSE", "Ollama returned an invalid model inventory.");
        }

        return models.EnumerateArray().Select(model =>
        {
            var details = model.TryGetProperty("details", out var detailElement) ? detailElement : default;
            return new OllamaModelInfo(
                GetOptionalString(model, "name") ?? GetOptionalString(model, "model") ?? "unknown",
                GetOptionalString(model, "digest"),
                GetOptionalUInt64(model, "size"),
                details.ValueKind == JsonValueKind.Object ? GetOptionalString(details, "family") : null,
                details.ValueKind == JsonValueKind.Object ? GetOptionalString(details, "parameter_size") : null,
                details.ValueKind == JsonValueKind.Object ? GetOptionalString(details, "quantization_level") : null);
        }).ToArray();
    }

    public async Task<IReadOnlyList<OllamaRunningModel>> GetRunningModelsAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(HttpMethod.Get, "/api/ps", null, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            throw new OllamaException("OLLAMA_MALFORMED_RESPONSE", "Ollama returned invalid runtime metadata.");
        }

        return models.EnumerateArray().Select(model => new OllamaRunningModel(
            GetOptionalString(model, "name") ?? GetOptionalString(model, "model") ?? "unknown",
            GetOptionalString(model, "digest"),
            GetOptionalUInt64(model, "size"),
            GetOptionalUInt64(model, "size_vram"),
            GetOptionalInt32(model, "context_length"),
            GetOptionalDateTimeOffset(model, "expires_at"))).ToArray();
    }

    public async Task<OllamaGenerationResult> GenerateAsync(
        string model,
        string prompt,
        int contextTokens,
        int outputTokens,
        TimeSpan keepAlive,
        CancellationToken cancellationToken = default,
        ReviewBusyBehavior busyBehavior = ReviewBusyBehavior.Queue,
        TimeSpan? queueTimeout = null,
        Func<CancellationToken, Task>? preGenerationCheck = null)
    {
        ValidateModelIdentifier(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (prompt.Length > 120_000)
        {
            throw new OllamaException("INPUT_TOO_LARGE", "The bounded reviewer input exceeds 120,000 characters.");
        }

        if (contextTokens is < 512 or > 32_768)
        {
            throw new ArgumentOutOfRangeException(nameof(contextTokens));
        }

        if (outputTokens is < 64 or > 2_048)
        {
            throw new ArgumentOutOfRangeException(nameof(outputTokens));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var cancellationEvent = GpuCoordination.OpenCancellationEvent();
        var registration = ThreadPool.RegisterWaitForSingleObject(
            cancellationEvent,
            static (state, _) => ((CancellationTokenSource)state!).Cancel(),
            linked,
            Timeout.Infinite,
            true);
        try
        {
            using var lease = await GpuCoordination.AcquireAsync(
                busyBehavior,
                queueTimeout ?? TimeSpan.FromSeconds(30),
                linked.Token).ConfigureAwait(false);
            if (preGenerationCheck is not null)
            {
                await preGenerationCheck(linked.Token).ConfigureAwait(false);
            }

            var payload = new
            {
                model,
                prompt,
                stream = false,
                think = false,
                keep_alive = keepAlive == Timeout.InfiniteTimeSpan ? "-1" : $"{Math.Max(0, (int)keepAlive.TotalSeconds)}s",
                options = new
                {
                    num_ctx = contextTokens,
                    num_predict = outputTokens,
                    temperature = 0.1
                }
            };
            using var document = await SendJsonAsync(
                HttpMethod.Post,
                "/api/generate",
                payload,
                TimeSpan.FromMinutes(5),
                linked.Token).ConfigureAwait(false);
            var root = document.RootElement;
            var response = GetOptionalString(root, "response")
                ?? throw new OllamaException("OLLAMA_MALFORMED_RESPONSE", "Ollama did not return generated text.");
            response = HiddenReasoningRegex().Replace(response, string.Empty).Trim();
            return new OllamaGenerationResult(
                response,
                GetOptionalString(root, "model"),
                root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True,
                GetOptionalInt64(root, "total_duration") ?? 0,
                GetOptionalInt64(root, "load_duration") ?? 0,
                GetOptionalInt32(root, "prompt_eval_count") ?? 0,
                GetOptionalInt32(root, "eval_count") ?? 0,
                GetOptionalInt64(root, "eval_duration") ?? 0);
        }
        finally
        {
            _ = registration.Unregister(null);
        }
    }

    public async Task UnloadAsync(string model, CancellationToken cancellationToken = default)
    {
        ValidateModelIdentifier(model);
        var payload = new { model, keep_alive = 0, stream = false };
        using var response = await SendJsonAsync(
            HttpMethod.Post,
            "/api/generate",
            payload,
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PullAsync(string model, CancellationToken cancellationToken = default)
    {
        ValidateModelIdentifier(model);
        var payload = new { model, stream = false };
        using var response = await SendJsonAsync(
            HttpMethod.Post,
            "/api/pull",
            payload,
            TimeSpan.FromHours(2),
            cancellationToken).ConfigureAwait(false);
        if (response.RootElement.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            && !string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase))
        {
            throw new OllamaException("MODEL_PULL_FAILED", "Ollama did not report a successful model download.");
        }
    }

    public async Task DeleteAsync(string model, CancellationToken cancellationToken = default)
    {
        ValidateModelIdentifier(model);
        using var response = await SendJsonAsync(
            HttpMethod.Delete,
            "/api/delete",
            new { model },
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
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
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Ollama must use an absolute unauthenticated HTTP loopback URI.", nameof(uri));
        }

        var loopback = IPAddress.TryParse(uri.Host, out var address)
            ? IPAddress.IsLoopback(address)
            : string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (!loopback)
        {
            throw new ArgumentException("Non-loopback Ollama endpoints are not allowed.", nameof(uri));
        }

        if (uri.AbsolutePath is not "/" and not "")
        {
            throw new ArgumentException("The Ollama base URI cannot include a path.", nameof(uri));
        }

        return new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", uri.Port).Uri;
    }

    private static Uri ResolveConfiguredBaseUri()
    {
        var configured = Environment.GetEnvironmentVariable("OLLAMA_HOST");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new Uri("http://127.0.0.1:11434", UriKind.Absolute);
        }

        var normalized = configured.Contains("://", StringComparison.Ordinal)
            ? configured
            : "http://" + configured;
        return new Uri(normalized, UriKind.Absolute);
    }

    public static void ValidateModelIdentifier(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || !ModelIdentifierRegex().IsMatch(model))
        {
            throw new OllamaException("INVALID_MODEL_IDENTIFIER", "The selected model identifier is invalid.");
        }
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string path,
        object? payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!ApiPathRegex().IsMatch(path))
        {
            throw new InvalidOperationException("Ollama API path is not allowed.");
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
                throw new OllamaException(
                    response.StatusCode == HttpStatusCode.NotFound ? "OLLAMA_API_NOT_FOUND" : "OLLAMA_HTTP_ERROR",
                    $"Ollama returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, MaximumResponseBytes, linked.Token).ConfigureAwait(false);
            try
            {
                return JsonDocument.Parse(bytes);
            }
            catch (JsonException)
            {
                throw new OllamaException("OLLAMA_MALFORMED_RESPONSE", "Ollama returned malformed JSON.");
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new OllamaException("OLLAMA_TIMEOUT", "The bounded Ollama request timed out.", true);
        }
        catch (OllamaPeerIdentityException)
        {
            throw new OllamaException(
                "OLLAMA_PEER_IDENTITY_UNVERIFIED",
                "The loopback endpoint is not a verified current-user Ollama process.");
        }
        catch (HttpRequestException exception) when (ContainsPeerIdentityFailure(exception))
        {
            throw new OllamaException(
                "OLLAMA_PEER_IDENTITY_UNVERIFIED",
                "The loopback endpoint is not a verified current-user Ollama process.");
        }
        catch (HttpRequestException)
        {
            throw new OllamaException("OLLAMA_UNAVAILABLE", "The loopback Ollama endpoint is unavailable.", true);
        }
    }

    private static SocketsHttpHandler CreateVerifiedHandler()
        => new()
        {
            AllowAutoRedirect = false,
            ConnectCallback = OllamaPeerVerifier.ConnectVerifiedAsync,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseProxy = false
        };

    private static bool ContainsPeerIdentityFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OllamaPeerIdentityException)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
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
                throw new OllamaException("OLLAMA_RESPONSE_TOO_LARGE", "Ollama exceeded the bounded response size.");
            }

            memory.Write(buffer, 0, read);
        }
    }

    private static string? GetOptionalString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ulong? GetOptionalUInt64(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetUInt64(out var result) ? result : null;

    private static int? GetOptionalInt32(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static long? GetOptionalInt64(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt64(out var result) ? result : null;

    private static DateTimeOffset? GetOptionalDateTimeOffset(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.TryGetDateTimeOffset(out var result) ? result : null;

    [GeneratedRegex("^[a-z0-9][a-z0-9._/-]*:[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdentifierRegex();

    [GeneratedRegex("^/api/[a-z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ApiPathRegex();

    [GeneratedRegex("<think>[\\s\\S]*?</think>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HiddenReasoningRegex();
}

public sealed class OllamaException : Exception
{
    public OllamaException(string code, string message, bool retryable = false)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }
    public bool Retryable { get; }
}

public static class GpuCoordination
{
    private const string InferenceSemaphoreName = @"Local\CodexGpuThalenHelperInference";
    private const string CancellationEventName = @"Local\CodexGpuThalenHelperCancel";

    public static EventWaitHandle OpenCancellationEvent()
        => new(false, EventResetMode.ManualReset, CancellationEventName);

    public static void RequestCancellation()
    {
        using var cancellation = OpenCancellationEvent();
        cancellation.Set();
    }

    public static void ClearCancellation()
    {
        using var cancellation = OpenCancellationEvent();
        cancellation.Reset();
    }

    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
        => await AcquireAsync(
            ReviewBusyBehavior.Queue,
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

    public static async Task<IDisposable> AcquireAsync(
        ReviewBusyBehavior busyBehavior,
        TimeSpan queueTimeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var semaphore = new Semaphore(1, 1, InferenceSemaphoreName);
        if (busyBehavior is not ReviewBusyBehavior.Skip and not ReviewBusyBehavior.Queue)
        {
            semaphore.Dispose();
            throw new ArgumentOutOfRangeException(nameof(busyBehavior));
        }

        if (busyBehavior == ReviewBusyBehavior.Skip)
        {
            if (semaphore.WaitOne(0))
            {
                return new Lease(semaphore);
            }

            semaphore.Dispose();
            throw new OllamaException(
                "REVIEW_BUSY_SKIPPED",
                "Another local review is using the model; this optional review was skipped.",
                retryable: true);
        }

        if (queueTimeout != Timeout.InfiniteTimeSpan
            && (queueTimeout <= TimeSpan.Zero || queueTimeout > TimeSpan.FromMinutes(2)))
        {
            semaphore.Dispose();
            throw new ArgumentOutOfRangeException(nameof(queueTimeout));
        }

        var index = await Task.Run(() => queueTimeout == Timeout.InfiniteTimeSpan
            ? WaitHandle.WaitAny([semaphore, cancellationToken.WaitHandle])
            : WaitHandle.WaitAny([semaphore, cancellationToken.WaitHandle], queueTimeout),
            CancellationToken.None).ConfigureAwait(false);
        if (index == 1)
        {
            semaphore.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (index == WaitHandle.WaitTimeout)
        {
            semaphore.Dispose();
            throw new OllamaException(
                "REVIEW_QUEUE_TIMEOUT",
                "Another local review remained active until the bounded queue timeout; this optional review was skipped.",
                retryable: true);
        }

        return new Lease(semaphore);
    }

    private sealed class Lease(Semaphore semaphore) : IDisposable
    {
        private Semaphore? _semaphore = semaphore;

        public void Dispose()
        {
            var semaphoreToRelease = Interlocked.Exchange(ref _semaphore, null);
            if (semaphoreToRelease is null)
            {
                return;
            }

            semaphoreToRelease.Release();
            semaphoreToRelease.Dispose();
        }
    }
}
