using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class ResourcePressureTests
{
    private const ulong GiB = 1024UL * 1024UL * 1024UL;

    [Fact]
    public void HighWindowsCommitPressureRefusesOptionalReview()
    {
        var guard = CreateGuard(
            availableVram: 24UL * GiB,
            new WindowsMemorySnapshot(70, 16UL * GiB, 64UL * GiB, 3UL * GiB));

        var result = guard.Check(State(), selectedModelAlreadyLoaded: false);

        Assert.False(result.Allowed);
        Assert.Equal("WINDOWS_COMMIT_PRESSURE", result.Code);
    }

    [Fact]
    public void InsufficientAvailableVramRefusesBeforeModelLoad()
    {
        var guard = CreateGuard(
            availableVram: 20UL * GiB,
            new WindowsMemorySnapshot(40, 48UL * GiB, 96UL * GiB, 64UL * GiB));

        var result = guard.Check(State(), selectedModelAlreadyLoaded: false);

        Assert.False(result.Allowed);
        Assert.Equal("GPU_MEMORY_PRESSURE", result.Code);
    }

    [Fact]
    public void LoadedModelUsesOnlyRuntimeHeadroomThreshold()
    {
        var guard = CreateGuard(
            availableVram: 1UL * GiB,
            new WindowsMemorySnapshot(40, 48UL * GiB, 96UL * GiB, 64UL * GiB));

        var result = guard.Check(State(), selectedModelAlreadyLoaded: true);

        Assert.True(result.Allowed);
        Assert.Equal("RESOURCE_PRESSURE_OK", result.Code);
    }

    [Fact]
    public void UnknownCommitPressureFailsClosed()
    {
        var guard = new ResourcePressureGuard(
            () => Hardware(24UL * GiB),
            () => null,
            () => new ModelCatalogService().LoadBundled());

        var result = guard.Check(State(), selectedModelAlreadyLoaded: false);

        Assert.False(result.Allowed);
        Assert.Equal("WINDOWS_COMMIT_PRESSURE_UNKNOWN", result.Code);
    }

    [Fact]
    public void UnknownAvailableVramFailsClosedForDiscreteGpu()
    {
        var guard = new ResourcePressureGuard(
            () => Hardware(availableVram: null),
            () => new WindowsMemorySnapshot(40, 48UL * GiB, 96UL * GiB, 64UL * GiB),
            () => new ModelCatalogService().LoadBundled());

        var result = guard.Check(State(), selectedModelAlreadyLoaded: false);

        Assert.False(result.Allowed);
        Assert.Equal("GPU_MEMORY_PRESSURE_UNKNOWN", result.Code);
    }

    [Fact]
    public void MissingSelectedModelCatalogEntryFailsClosed()
    {
        var guard = new ResourcePressureGuard(
            () => Hardware(24UL * GiB),
            () => new WindowsMemorySnapshot(40, 48UL * GiB, 96UL * GiB, 64UL * GiB),
            () => new ModelManifest(1, "test", DateOnly.FromDateTime(DateTime.UtcNow), []));

        var result = guard.Check(State(), selectedModelAlreadyLoaded: false);

        Assert.False(result.Allowed);
        Assert.Equal("MODEL_CATALOG_ENTRY_MISSING", result.Code);
    }

    private static ResourcePressureGuard CreateGuard(
        ulong availableVram,
        WindowsMemorySnapshot memory)
        => new(
            () => Hardware(availableVram),
            () => memory,
            () => new ModelCatalogService().LoadBundled());

    private static InstallationState State()
        => new()
        {
            SelectedModel = "qwen3-coder:30b",
            HardwareTier = HardwareTier.High
        };

    private static HardwareProfile Hardware(ulong? availableVram)
        => new(
            new OperatingSystemInfo("Windows", new Version(10, 0, 26100), "X64", true, null),
            new CpuInfo("Vendor", "CPU", 12, 24, true, true, true),
            new MemoryInfo(64UL * GiB, 48UL * GiB),
            [new GpuInfo(
                GpuVendor.Nvidia,
                "Test GPU",
                24UL * GiB,
                32UL * GiB,
                availableVram,
                "test",
                "test",
                AccelerationRoute.NvidiaCuda,
                false)],
            [],
            false,
            []);
}
