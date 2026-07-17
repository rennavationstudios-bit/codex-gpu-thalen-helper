using System.Net;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class OllamaStartupTests
{
    [Fact]
    public async Task ConfigureAndVerifyProvesAutostartPathModelEndpointAndLoopback()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var state = CreateState(temporary.Path);
        CreateManifest(state);
        var platform = new FakeStartupPlatform { LoopbackOnly = true };
        var manager = new OllamaAutoStartManager(() => CreateInventoryClient(reachable: true), platform);

        manager.Configure(paths, state, enabled: true);
        var result = await manager.EnsureRunningAsync(paths, state);

        Assert.True(result.AutoStartConfigured);
        Assert.True(result.EndpointReachable);
        Assert.True(result.ModelStorageConfigured);
        Assert.True(result.SelectedModelStoredInConfiguredPath);
        Assert.True(result.SelectedModelAvailable);
        Assert.True(result.SelectedModelDigestMatches);
        Assert.True(result.LoopbackOnly);
        Assert.False(result.StartedNewProcess);
        Assert.Equal("OK", result.Code);
        Assert.Equal(Path.GetFullPath(state.ModelStorageLocation!), platform.UserEnvironment["OLLAMA_MODELS"]);
        Assert.Equal("127.0.0.1:11434", platform.UserEnvironment["OLLAMA_HOST"]);
        Assert.Contains("ollama autostart --quiet", platform.RunEntry, StringComparison.Ordinal);
        Assert.True(state.StartupEntryOwnedByHelper);
        Assert.True(manager.IsConfigured(paths));
        Assert.Equal(0, platform.StartCount);
    }

    [Fact]
    public async Task DecliningAutostartLeavesHelperConfiguredAndReportsManualRequirement()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var state = CreateState(temporary.Path);
        CreateManifest(state);
        var platform = new FakeStartupPlatform { LoopbackOnly = true };
        var manager = new OllamaAutoStartManager(() => CreateInventoryClient(reachable: true), platform);

        manager.Configure(paths, state, enabled: false);
        var result = await manager.VerifyAsync(paths, state, false);

        Assert.False(result.AutoStartConfigured);
        Assert.Null(platform.RunEntry);
        Assert.False(state.StartupEntryOwnedByHelper);
        Assert.False(manager.IsConfigured(paths));
        Assert.True(result.EndpointReachable);
        Assert.Equal("MANUAL_START_REQUIRED", result.Code);
        Assert.Contains("manually starting Ollama", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingUnhealthyProcessNeverCreatesDuplicate()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var state = CreateState(temporary.Path);
        var platform = new FakeStartupPlatform
        {
            LoopbackOnly = true,
            ProcessRunning = true,
            Executable = "ollama.exe"
        };
        var manager = new OllamaAutoStartManager(() => CreateInventoryClient(reachable: false), platform);
        manager.Configure(paths, state, enabled: true);

        var result = await manager.EnsureRunningAsync(paths, state);

        Assert.Equal("OLLAMA_PROCESS_UNHEALTHY", result.Code);
        Assert.Equal(0, platform.StartCount);
        Assert.Equal(15, platform.DelayCount);
    }

    [Fact]
    public async Task MisleadingExternalAutoStartArtifactIsPreservedButNeverCertified()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var state = CreateState(temporary.Path);
        CreateManifest(state);
        var platform = new FakeStartupPlatform
        {
            ExternalAutoStartArtifact = "Ollama telemetry bootstrap",
            LoopbackOnly = true
        };
        var manager = new OllamaAutoStartManager(() => CreateInventoryClient(reachable: true), platform);

        manager.Configure(paths, state, enabled: true);
        var result = await manager.VerifyAsync(paths, state, false);

        Assert.Null(platform.RunEntry);
        Assert.Equal("Ollama telemetry bootstrap", platform.ExternalAutoStartArtifact);
        Assert.False(state.StartupEntryOwnedByHelper);
        Assert.True(state.Preferences.AutoStartOllama);
        Assert.False(manager.IsConfigured(paths));
        Assert.False(result.AutoStartConfigured);
        Assert.Equal("EXTERNAL_AUTOSTART_UNVERIFIED", result.Code);
        Assert.Contains("preserved", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not verified", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, platform.StartCount);
    }

    [Fact]
    public void AugmentedOrChainedHelperRunCommandsAreNeverCertified()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var state = CreateState(temporary.Path);
        var platform = new FakeStartupPlatform { LoopbackOnly = true };
        var manager = new OllamaAutoStartManager(() => CreateInventoryClient(reachable: true), platform);
        manager.Configure(paths, state, enabled: true);
        var canonical = platform.RunEntry!;

        Assert.True(manager.IsConfigured(paths));
        foreach (var altered in new[]
                 {
                     "cmd.exe /c " + canonical,
                     "prefix " + canonical,
                     canonical + " --extra",
                     canonical + " & calc.exe",
                     canonical + " && ollama serve",
                     canonical + " ",
                     canonical.Replace("\" ollama", "-wrapper.exe\" ollama", StringComparison.Ordinal)
                 })
        {
            platform.RunEntry = altered;
            Assert.False(manager.IsConfigured(paths));
        }
    }

    [Fact]
    public void ListenerPolicyRequiresAtLeastOneListenerAndRejectsWildcards()
    {
        var absent = OllamaAutoStartManager.EvaluateListenerStatus([]);
        Assert.False(absent.HasListeners);
        Assert.False(absent.LoopbackOnly);
        Assert.True(OllamaAutoStartManager.HasNoNonLoopbackListener(absent));

        var loopback = OllamaAutoStartManager.EvaluateListenerStatus(
            [new IPEndPoint(IPAddress.Loopback, 11434), new IPEndPoint(IPAddress.IPv6Loopback, 11434)]);
        Assert.True(loopback.HasListeners);
        Assert.True(loopback.LoopbackOnly);
        Assert.True(OllamaAutoStartManager.HasNoNonLoopbackListener(loopback));
        Assert.Equal(2, loopback.ListenerCount);

        var exposed = OllamaAutoStartManager.EvaluateListenerStatus(
            [new IPEndPoint(IPAddress.IPv6Any, 11434)]);
        Assert.True(exposed.HasListeners);
        Assert.False(exposed.LoopbackOnly);
        Assert.False(OllamaAutoStartManager.HasNoNonLoopbackListener(exposed));
    }

    [Fact]
    public async Task MissingProcessStartsExactlyOnceThenVerifies()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var state = CreateState(temporary.Path);
        CreateManifest(state);
        var platform = new FakeStartupPlatform
        {
            LoopbackOnly = true,
            Executable = "ollama.exe"
        };
        var manager = new OllamaAutoStartManager(
            () => CreateInventoryClient(platform.StartCount > 0),
            platform);
        manager.Configure(paths, state, enabled: true);

        var result = await manager.EnsureRunningAsync(paths, state);

        Assert.Equal(1, platform.StartCount);
        Assert.True(result.StartedNewProcess);
        Assert.Equal("OK", result.Code);
    }

    [Fact]
    public async Task VerificationFailsClosedForNetworkExposureOrWrongModelPath()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var state = CreateState(temporary.Path);
        var platform = new FakeStartupPlatform { LoopbackOnly = false };
        var manager = new OllamaAutoStartManager(() => CreateInventoryClient(reachable: true), platform);
        manager.Configure(paths, state, enabled: true);

        var exposed = await manager.VerifyAsync(paths, state, false);
        Assert.Equal("OLLAMA_NETWORK_EXPOSURE", exposed.Code);
        Assert.False(exposed.LoopbackOnly);

        platform.LoopbackOnly = true;
        platform.UserEnvironment["OLLAMA_MODELS"] = Path.Combine(temporary.Path, "wrong");
        var wrongPath = await manager.VerifyAsync(paths, state, false);
        Assert.Equal("MODEL_PATH_NOT_CONFIGURED", wrongPath.Code);
        Assert.False(wrongPath.ModelStorageConfigured);
    }

    [Fact]
    public async Task SafeRestartStopsOnlyTheDiscoveredExecutable()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var state = CreateState(temporary.Path);
        CreateManifest(state);
        var expectedExecutable = Path.Combine(temporary.Path, "Ollama", "ollama.exe");
        var platform = new FakeStartupPlatform { LoopbackOnly = true, Executable = expectedExecutable };
        var manager = new OllamaAutoStartManager(() => CreateInventoryClient(reachable: true), platform);

        var result = await manager.ApplyConfigurationAsync(
            paths,
            state,
            Path.Combine(temporary.Path, "old-models"),
            enabled: true,
            allowSafeRestart: true);

        Assert.Equal("OK", result.Code);
        Assert.Equal(1, platform.StopCount);
        Assert.Equal(expectedExecutable, platform.LastStoppedExecutable);
    }

    [Fact]
    public async Task RequireIdleRefusesLoadedModelWithoutUnloadingStoppingOrChangingEnvironment()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var state = CreateState(temporary.Path);
        var platform = new FakeStartupPlatform
        {
            LoopbackOnly = true,
            ProcessRunning = true,
            Executable = Path.Combine(temporary.Path, "Ollama", "ollama.exe")
        };
        platform.UserEnvironment["OLLAMA_MODELS"] = Path.Combine(temporary.Path, "old-models");
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath switch
            {
                "/api/ps" => FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"foreign:latest\"}]}"),
                "/api/tags" => FakeHttpMessageHandler.Json("{\"models\":[]}"),
                _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
            }));
        var manager = new OllamaAutoStartManager(
            () => new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler)),
            platform);
        var mutationsBefore = platform.MutationCount;

        var result = await manager.ApplyConfigurationAsync(
            paths,
            state,
            Path.Combine(temporary.Path, "old-models"),
            enabled: true,
            allowSafeRestart: true,
            OllamaRestartModelPolicy.RequireIdle);

        Assert.Equal("OLLAMA_RESTART_NOT_IDLE", result.Code);
        Assert.Equal(mutationsBefore, platform.MutationCount);
        Assert.Equal(0, platform.StopCount);
        Assert.Equal(0, platform.StartCount);
        Assert.Equal(Path.Combine(temporary.Path, "old-models"), platform.UserEnvironment["OLLAMA_MODELS"]);
    }

    [Fact]
    public async Task ModelMustExistUnderExactConfiguredManifestTree()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var state = CreateState(temporary.Path);
        var platform = new FakeStartupPlatform { LoopbackOnly = true };
        var manager = new OllamaAutoStartManager(() => CreateInventoryClient(reachable: true), platform);
        manager.Configure(paths, state, enabled: true);

        var result = await manager.VerifyAsync(paths, state, false);

        Assert.Equal("MODEL_NOT_IN_CONFIGURED_PATH", result.Code);
        Assert.False(result.SelectedModelStoredInConfiguredPath);
    }

    [Fact]
    public async Task VerificationRejectsMismatchedModelDigest()
    {
        using var temporary = new TemporaryDirectory();
        var paths = temporary.CreatePaths();
        var state = CreateState(temporary.Path);
        CreateManifest(state);
        var platform = new FakeStartupPlatform { LoopbackOnly = true };
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]}")));
        var manager = new OllamaAutoStartManager(
            () => new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler)),
            platform);
        manager.Configure(paths, state, enabled: true);

        var result = await manager.VerifyAsync(paths, state, false);

        Assert.Equal("MODEL_DIGEST_MISMATCH", result.Code);
        Assert.False(result.SelectedModelDigestMatches);
    }

    [Theory]
    [InlineData("ollama", true)]
    [InlineData("Ollama App", true)]
    [InlineData("ollama-helper", false)]
    [InlineData("ollama-malware", false)]
    public void ProcessPolicyAcceptsOnlyExactKnownNames(string processName, bool expected)
        => Assert.Equal(expected, WindowsOllamaStartupPlatform.IsKnownOllamaProcessName(processName));

    [Fact]
    public void ProcessPolicyAcceptsOnlyTrustedExecutableOrKnownSibling()
    {
        var expected = Path.Combine("C:\\Program Files", "Ollama", "ollama.exe");
        Assert.True(WindowsOllamaStartupPlatform.IsExpectedOllamaExecutable(expected, expected));
        Assert.True(WindowsOllamaStartupPlatform.IsExpectedOllamaExecutable(
            Path.Combine("C:\\Program Files", "Ollama", "ollama app.exe"),
            expected));
        Assert.False(WindowsOllamaStartupPlatform.IsExpectedOllamaExecutable(
            Path.Combine("C:\\Other", "ollama.exe"),
            expected));
        Assert.False(WindowsOllamaStartupPlatform.IsExpectedOllamaExecutable(
            Path.Combine("C:\\Program Files", "Ollama", "ollama-helper.exe"),
            expected));
    }

    private static InstallationState CreateState(string root)
        => new()
        {
            SelectedModel = "qwen2.5-coder:1.5b",
            SelectedModelDigest = "d7372fd82851",
            ModelStorageLocation = Path.Combine(root, "models"),
            HardwareTier = HardwareTier.Entry,
            Availability = HelperAvailability.Enabled
        };

    private static void CreateManifest(InstallationState state)
    {
        var path = Path.Combine(
            state.ModelStorageLocation!,
            "manifests",
            "registry.ollama.ai",
            "library",
            "qwen2.5-coder",
            "1.5b");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"schemaVersion\":2}");
    }

    private static OllamaClient CreateInventoryClient(bool reachable)
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (!reachable)
            {
                throw new HttpRequestException("offline");
            }

            return Task.FromResult(request.RequestUri?.AbsolutePath switch
            {
                "/api/tags" => FakeHttpMessageHandler.Json("{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\",\"digest\":\"sha256:d7372fd828510000000000000000000000000000000000000000000000000000\"}]}"),
                "/api/ps" => FakeHttpMessageHandler.Json("{\"models\":[]}"),
                _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
            });
        });
        return new OllamaClient(new Uri("http://127.0.0.1:11434"), new HttpClient(handler));
    }
}

internal sealed class FakeStartupPlatform : IOllamaStartupPlatform
{
    public Dictionary<string, string?> UserEnvironment { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> ProcessEnvironment { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? RunEntry { get; set; }
    public string? ExternalAutoStartArtifact { get; set; }
    public bool ProcessRunning { get; set; }
    public bool LoopbackOnly { get; set; } = true;
    public string? Executable { get; set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public string? LastStoppedExecutable { get; private set; }
    public int DelayCount { get; private set; }
    public bool StopSucceeds { get; set; } = true;
    public int MutationCount { get; private set; }

    public string? GetUserEnvironmentVariable(string name) => UserEnvironment.GetValueOrDefault(name);
    public void SetUserEnvironmentVariable(string name, string? value)
    {
        MutationCount++;
        UserEnvironment[name] = value;
    }
    public void SetProcessEnvironmentVariable(string name, string? value)
    {
        MutationCount++;
        ProcessEnvironment[name] = value;
    }
    public string? GetRunEntry() => RunEntry;
    public void SetRunEntry(string? command)
    {
        MutationCount++;
        RunEntry = command;
    }
    public bool HasExternalAutoStart() => !string.IsNullOrWhiteSpace(ExternalAutoStartArtifact);
    public bool IsAnyOllamaProcessRunning() => ProcessRunning;
    public string? FindOllamaExecutable() => Executable;

    public bool StartOllama(string executable, string modelDirectory, HelperPreferences preferences)
    {
        StartCount++;
        MutationCount++;
        ProcessRunning = true;
        return true;
    }

    public bool StopOllamaProcesses(string expectedExecutable)
    {
        StopCount++;
        MutationCount++;
        LastStoppedExecutable = expectedExecutable;
        if (StopSucceeds)
        {
            ProcessRunning = false;
        }

        return StopSucceeds;
    }

    public bool IsPortLoopbackOnly(int port) => LoopbackOnly;
    public void BroadcastEnvironmentChange() => MutationCount++;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        DelayCount++;
        return Task.CompletedTask;
    }
}
