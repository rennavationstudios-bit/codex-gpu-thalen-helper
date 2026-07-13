using System.Net;
using System.Text;
using System.Text.Json;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory(string? suffix = null)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CodexGpuThalenHelperTests",
            Guid.NewGuid().ToString("N") + (suffix is null ? string.Empty : "-" + suffix));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public ProductPaths CreatePaths()
    {
        var install = System.IO.Path.Combine(Path, "Install folder ü");
        var state = System.IO.Path.Combine(Path, "State folder ü");
        var codex = System.IO.Path.Combine(Path, "Codex home ü");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(codex);
        File.WriteAllText(System.IO.Path.Combine(install, "local-gpu-reviewer.exe"), string.Empty);
        File.WriteAllText(System.IO.Path.Combine(install, "thalen-helper.exe"), string.Empty);
        return ProductPaths.Resolve(install, state, codex);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri?.AbsolutePath ?? string.Empty, body));
        return await _handler(request, cancellationToken);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal static class FixtureFactory
{
    private const ulong GiB = 1024UL * 1024UL * 1024UL;

    public static IReadOnlyList<HardwareFixture> LoadHardwareFixtures()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "hardware", "hardware-fixtures.json");
        return JsonSerializer.Deserialize<List<HardwareFixture>>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public static HardwareProfile Create(HardwareFixture fixture)
    {
        var supported = string.Equals(fixture.Architecture, "X64", StringComparison.OrdinalIgnoreCase);
        var gpus = fixture.Gpus.Select(gpu => new GpuInfo(
            Enum.Parse<GpuVendor>(gpu.Vendor),
            gpu.Name,
            Bytes(gpu.VramGiB),
            Bytes(gpu.SharedGiB),
            gpu.FreeVramGiB is null ? null : Bytes(gpu.FreeVramGiB.Value),
            gpu.Route == "None" ? "unsupported" : "test-driver",
            null,
            Enum.Parse<AccelerationRoute>(gpu.Route),
            gpu.Integrated)).ToArray();
        return new HardwareProfile(
            new OperatingSystemInfo("Windows test", new Version(10, 0, 22631), fixture.Architecture, supported, supported ? null : "Unsupported architecture"),
            new CpuInfo("Test", "Fixture CPU", 8, 16, true, true, true),
            new MemoryInfo(Bytes(fixture.RamGiB), Bytes(fixture.AvailableRamGiB)),
            gpus,
            [new StorageVolume("C:\\", "NTFS", 1000 * GiB, 500 * GiB, StorageMediaType.Ssd, true, true, false, true, null)],
            false,
            []);
    }

    public static ulong Bytes(double gib) => (ulong)(gib * GiB);
}

internal sealed record HardwareFixture(
    string Name,
    string Architecture,
    double RamGiB,
    double AvailableRamGiB,
    IReadOnlyList<GpuFixture> Gpus,
    string? ExpectedModel);

internal sealed record GpuFixture(
    string Vendor,
    string Name,
    double VramGiB,
    double SharedGiB,
    double? FreeVramGiB,
    string Route,
    bool Integrated);
