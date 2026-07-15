using ModelContextProtocol.Client;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class McpProtocolTests
{
    [Fact]
    public async Task FreshStdioMcpSessionListsOnlyBoundedToolsAndRejectsAnUnverifiedLoopbackPeer()
    {
        using var temporary = new TemporaryDirectory();
        await using var ollama = new LoopbackOllamaStub();
        var paths = ProductPaths.Resolve(
            AppContext.BaseDirectory,
            Path.Combine(temporary.Path, "state"),
            Path.Combine(temporary.Path, "codex-home"));
        Directory.CreateDirectory(paths.StateDirectory);
        Directory.CreateDirectory(paths.CodexHome);
        new CodexConfigManager().InstallOrRepair(paths, enabled: true);
        var store = new StateStore(paths.StateFile);
        var modelDirectory = Path.Combine(temporary.Path, "Isolated models");
        var manifest = Path.Combine(
            modelDirectory,
            "manifests",
            "registry.ollama.ai",
            "library",
            "qwen2.5-coder",
            "1.5b");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "manifest");
        await store.SaveAsync(new InstallationState
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            ModelStorageLocation = modelDirectory,
            HardwareTier = HardwareTier.Entry,
            ManagedConfigurationSections = [IntegrationOwnership.ManagedReviewerSection],
            Availability = HelperAvailability.Enabled
        });
        GpuCoordination.ClearCancellation();
        var repository = FindRepositoryRoot();
        var dotnet = FindDotnetHost(repository);
        var server = Path.Combine(AppContext.BaseDirectory, "local-gpu-reviewer.dll");
        Assert.True(File.Exists(server));
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["THALEN_HELPER_STATE_DIR"] = paths.StateDirectory;
        environment["CODEX_HOME"] = paths.CodexHome;
        environment["OLLAMA_HOST"] = ollama.BaseUri.AbsoluteUri;
        environment["OLLAMA_MODELS"] = modelDirectory;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "local_gpu_reviewer integration test",
            Command = dotnet,
            Arguments = [server],
            WorkingDirectory = repository,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        });
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        Assert.Equal(["local_gpu_health", "local_gpu_review"], tools.Select(tool => tool.Name).Order());
        var toolSchema = JsonSerializer.Serialize(tools);
        Assert.Contains("busyBehavior", toolSchema, StringComparison.Ordinal);
        Assert.Contains("queueTimeoutSeconds", toolSchema, StringComparison.Ordinal);

        var health = await client.CallToolAsync("local_gpu_health", new Dictionary<string, object?>());
        var healthJson = JsonSerializer.Serialize(health.StructuredContent);
        Assert.Contains("\"endpointReachable\":false", healthJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"modelRan\":false", healthJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OLLAMA_PEER_IDENTITY_UNVERIFIED", healthJson, StringComparison.Ordinal);

        var review = await client.CallToolAsync("local_gpu_review", new Dictionary<string, object?>
        {
            ["assignment"] = "This prompt must never reach an unverified peer.",
            ["context"] = "untrusted repository text",
            ["busyBehavior"] = "skip",
            ["queueTimeoutSeconds"] = 5
        });
        Assert.True(
            review.StructuredContent is not null,
            $"{JsonSerializer.Serialize(review)}; requests={string.Join(',', ollama.RequestPaths)}; lastBody={ollama.LastGenerateBody}");
        var reviewJson = JsonSerializer.Serialize(review.StructuredContent);
        Assert.Contains("OLLAMA_PEER_IDENTITY_UNVERIFIED", reviewJson, StringComparison.Ordinal);
        Assert.Contains("\"modelRan\":false", reviewJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ollama.GenerationCount);
        GpuCoordination.ClearCancellation();
        Assert.DoesNotContain("agent_message", ollama.LastGenerateBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("untrusted repository text", ollama.LastGenerateBody, StringComparison.Ordinal);

        var pausedState = (await store.LoadAsync())! with { Availability = HelperAvailability.Paused };
        await store.SaveAsync(pausedState);
        var paused = await client.CallToolAsync("local_gpu_review", new Dictionary<string, object?>
        {
            ["assignment"] = "This must not run."
        });
        var pausedJson = JsonSerializer.Serialize(paused.StructuredContent);
        Assert.Contains("\"errorCode\":\"PAUSED\"", pausedJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"modelRan\":false", pausedJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ollama.GenerationCount);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "global.json")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string FindDotnetHost(string repository)
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty, "dotnet.exe"),
            Path.Combine(repository, ".tools", "dotnet", "dotnet.exe")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new FileNotFoundException("A dotnet host was not found for the fresh MCP process test.");
    }
}

internal sealed class LoopbackOllamaStub : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _server;

    public LoopbackOllamaStub()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUri = new Uri($"http://127.0.0.1:{endpoint.Port}");
        _server = RunAsync();
    }

    public Uri BaseUri { get; }
    public int GenerationCount { get; private set; }
    public string LastGenerateBody { get; private set; } = string.Empty;
    public List<string> RequestPaths { get; } = [];

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _listener.Stop();
        try
        {
            await _server;
        }
        catch (OperationCanceledException)
        {
        }

        _cancellation.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }

            _ = HandleAsync(client, _cancellation.Token);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            var request = await ReadRequestAsync(stream, cancellationToken);
            lock (RequestPaths)
            {
                RequestPaths.Add(request.Path);
            }
            var body = request.Path switch
            {
                "/api/tags" => "{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\"}]}",
                "/api/ps" => "{\"models\":[]}",
                "/api/generate" => Generate(request.Body),
                _ => "{}"
            };
            var payload = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
        }
    }

    private string Generate(string body)
    {
        LastGenerateBody = body;
        GenerationCount++;
        return "{\"model\":\"qwen2.5-coder:1.5b\",\"response\":\"MCP_REVIEW_OK\",\"done\":true}";
    }

    private static async Task<(string Path, string Body)> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var single = new byte[1];
        while (bytes.Count < 64 * 1024)
        {
            var read = await stream.ReadAsync(single, cancellationToken);
            if (read == 0)
            {
                break;
            }

            bytes.Add(single[0]);
            var count = bytes.Count;
            if (count >= 4
                && bytes[count - 4] == '\r'
                && bytes[count - 3] == '\n'
                && bytes[count - 2] == '\r'
                && bytes[count - 1] == '\n')
            {
                break;
            }
        }

        var headers = Encoding.ASCII.GetString(bytes.ToArray());
        var lines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var path = lines[0].Split(' ')[1];
        if (lines.Any(line => line.Equals("Expect: 100-continue", StringComparison.OrdinalIgnoreCase)))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), cancellationToken);
        }

        var contentLength = lines
            .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(line => int.Parse(line[(line.IndexOf(':') + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();
        if (contentLength == 0 && lines.Any(line => line.Equals("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)))
        {
            using var body = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadAsciiLineAsync(stream, cancellationToken);
                var semicolon = sizeLine.IndexOf(';');
                var sizeText = semicolon >= 0 ? sizeLine[..semicolon] : sizeLine;
                var size = int.Parse(sizeText, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                if (size == 0)
                {
                    _ = await ReadAsciiLineAsync(stream, cancellationToken);
                    break;
                }

                var chunk = new byte[size];
                await ReadExactlyAsync(stream, chunk, cancellationToken);
                await body.WriteAsync(chunk, cancellationToken);
                _ = await ReadAsciiLineAsync(stream, cancellationToken);
            }

            return (path, Encoding.UTF8.GetString(body.ToArray()));
        }

        var bodyBytes = new byte[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(bodyBytes.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return (path, Encoding.UTF8.GetString(bodyBytes, 0, offset));
    }

    private static async Task<string> ReadAsciiLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 8 * 1024)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (one[0] == '\n')
            {
                break;
            }

            if (one[0] != '\r')
            {
                bytes.Add(one[0]);
            }
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }
}
