using System.Net;
using System.Net.Sockets;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class OllamaPeerSecurityTests
{
    [Theory]
    [InlineData("C:\\Custom Tools\\Ollama\\ollama.exe")]
    [InlineData("D:\\Apps\\Ollama\\ollama app.exe")]
    public void TrustedIdentityAllowsOfficialOllamaFromAnyAbsoluteDirectory(string executablePath)
    {
        Assert.True(OllamaPeerVerifier.IsTrustedIdentity(
            executablePath,
            "S-1-5-21-1000",
            "S-1-5-21-1000",
            signatureValid: true));
    }

    [Theory]
    [InlineData("C:\\Tools\\fake-ollama.exe", "S-1-5-21-1000", "S-1-5-21-1000", true)]
    [InlineData("C:\\Tools\\ollama.exe", "S-1-5-21-1000", "S-1-5-21-1000", false)]
    [InlineData("C:\\Tools\\ollama.exe", "S-1-5-21-2000", "S-1-5-21-1000", true)]
    [InlineData("ollama.exe", "S-1-5-21-1000", "S-1-5-21-1000", true)]
    public void TrustedIdentityRejectsWrongNameSignatureUserOrRelativePath(
        string executablePath,
        string processSid,
        string currentSid,
        bool signatureValid)
    {
        Assert.False(OllamaPeerVerifier.IsTrustedIdentity(executablePath, processSid, currentSid, signatureValid));
    }

    [Fact]
    public async Task ProductionClientRejectsArbitraryLoopbackPeerBeforeSendingHttpBytes()
    {
        await using var peer = new ArbitraryLoopbackPeer();
        using var client = new OllamaClient(peer.BaseUri);

        var exception = await Assert.ThrowsAsync<OllamaException>(() => client.GetModelsAsync());
        await peer.WaitForClosedConnectionAsync();

        Assert.Equal("OLLAMA_PEER_IDENTITY_UNVERIFIED", exception.Code);
        Assert.Equal(0, peer.BytesReceived);
    }

    [Fact]
    public async Task ExplicitOptInRealEndpointAcceptsVerifiedPeerWithoutRunningInference()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("THALEN_HELPER_REAL_GPU_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var client = new OllamaClient(new Uri("http://127.0.0.1:11434"));

        Assert.NotNull(await client.GetModelsAsync());
        Assert.NotNull(await client.GetRunningModelsAsync());
    }

    private sealed class ArbitraryLoopbackPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _server;
        private readonly TaskCompletionSource _connectionClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ArbitraryLoopbackPeer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _server = ObserveAsync();
        }

        public Uri BaseUri { get; }
        public int BytesReceived { get; private set; }

        public async Task WaitForClosedConnectionAsync()
            => await _connectionClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));

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
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
            }

            _cancellation.Dispose();
        }

        private async Task ObserveAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            await using var stream = client.GetStream();
            var oneByte = new byte[1];
            BytesReceived = await stream.ReadAsync(oneByte, _cancellation.Token);
            _connectionClosed.TrySetResult();
        }
    }
}
