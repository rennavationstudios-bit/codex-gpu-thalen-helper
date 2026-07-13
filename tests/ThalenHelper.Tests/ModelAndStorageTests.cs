using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class ModelAndStorageTests
{
    private const ulong GiB = 1024UL * 1024UL * 1024UL;

    [Fact]
    public void BundledCatalogIsValidAndAuditedForAutomaticUse()
    {
        var manifest = new ModelCatalogService().LoadBundled();

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.All(manifest.Models.Where(model => model.AutomaticSelectionAllowed), model =>
        {
            Assert.True(model.CommercialUseAllowed);
            Assert.NotNull(model.ExpectedDigest);
            Assert.True(model.ExpectedDownloadBytes > 0);
            Assert.InRange(model.SafeDefaultContextTokens, 512, model.MaximumContextTokens);
        });
        Assert.False(manifest.Models.Single(model => model.Tag == "qwen2.5-coder:3b").AutomaticSelectionAllowed);
        Assert.False(manifest.Models.Single(model => model.Tag == "qwen2.5-coder:32b").AutomaticSelectionAllowed);
    }

    [Fact]
    public void HardwareFixturesSelectOnlyExpectedSafeModel()
    {
        var selector = new ModelSelector();
        var catalog = new ModelCatalogService().LoadBundled();

        foreach (var fixture in FixtureFactory.LoadHardwareFixtures())
        {
            var result = selector.Recommend(FixtureFactory.Create(fixture), catalog, allowCpuFallback: false);
            Assert.Equal(fixture.ExpectedModel, result.Model?.Tag);
            if (result.Model is not null)
            {
                Assert.True(result.Model.AutomaticSelectionAllowed);
                Assert.True(result.Model.CommercialUseAllowed);
                Assert.Equal(ModelSelector.GetHardwareTier(result.Model), result.HardwareTier);
            }
        }
    }

    [Fact]
    public void SharedGraphicsMemoryIsNeverCountedAsDedicatedVram()
    {
        var fixture = FixtureFactory.LoadHardwareFixtures().Single(item => item.Name == "intel-integrated");
        var result = new ModelSelector().Recommend(
            FixtureFactory.Create(fixture),
            new ModelCatalogService().LoadBundled(),
            allowCpuFallback: false);

        Assert.Null(result.Model);
        Assert.Equal(HardwareTier.NoModel, result.HardwareTier);
    }

    [Fact]
    public void CpuFallbackRequiresExplicitOptInAndRemainsSmall()
    {
        var fixture = FixtureFactory.LoadHardwareFixtures().Single(item => item.Name == "cpu-only-16gb");
        var profile = FixtureFactory.Create(fixture);
        var catalog = new ModelCatalogService().LoadBundled();
        var selector = new ModelSelector();

        Assert.Null(selector.Recommend(profile, catalog, false).Model);
        var optedIn = selector.Recommend(profile, catalog, true);
        Assert.NotNull(optedIn.Model);
        Assert.True(optedIn.Model.CpuFallbackReasonable);
        Assert.True(optedIn.RequiresCpuOptIn);
        Assert.True(optedIn.LowImpactMode);
    }

    [Fact]
    public void StorageRecommendationPrefersNonSystemNvmeAndRejectsRemovable()
    {
        var catalog = new ModelCatalogService().LoadBundled();
        var model = catalog.Models.Single(item => item.Tag == "qwen2.5-coder:7b");
        var profile = FixtureFactory.Create(FixtureFactory.LoadHardwareFixtures().First()) with
        {
            Volumes =
            [
                Volume("C:\\", StorageMediaType.Ssd, 300, true, true),
                Volume("D:\\", StorageMediaType.Removable, 900, false, false),
                Volume("E:\\", StorageMediaType.Nvme, 250, false, true),
                Volume("F:\\", StorageMediaType.Hdd, 800, false, true)
            ]
        };

        var result = new StorageSelector().Recommend(profile, model);

        Assert.Equal("E:\\", result.Volume?.RootPath);
        Assert.StartsWith("E:\\", result.ModelDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.RequiredBytes > model.ExpectedDownloadBytes * 2);
    }

    [Fact]
    public void StorageRecommendationPreservesReserveAndReportsNoFit()
    {
        var model = new ModelCatalogService().LoadBundled().Models.Single(item => item.Tag == "qwen3-coder:30b");
        var profile = FixtureFactory.Create(FixtureFactory.LoadHardwareFixtures().First()) with
        {
            Volumes = [Volume("C:\\", StorageMediaType.Ssd, 21, true, true)]
        };

        var result = new StorageSelector().Recommend(profile, model);

        Assert.Null(result.Volume);
        Assert.Null(result.ModelDirectory);
    }

    [Fact]
    public void InvalidCatalogIsRejected()
    {
        var valid = new ModelCatalogService().LoadBundled();
        var duplicate = valid with { Models = [valid.Models[0], valid.Models[0]] };
        Assert.Throws<InvalidDataException>(() => new ModelCatalogService().Validate(duplicate));

        var invalidDigest = valid with
        {
            Models = [valid.Models[0] with { ExpectedDigest = "not-a-sha256" }, .. valid.Models.Skip(1)]
        };
        Assert.Throws<InvalidDataException>(() => new ModelCatalogService().Validate(invalidDigest));
    }

    private static StorageVolume Volume(
        string root,
        StorageMediaType media,
        ulong freeGiB,
        bool system,
        bool suitable)
        => new(root, "NTFS", 1000 * GiB, freeGiB * GiB, media, true, system, false, suitable, suitable ? null : "not suitable");
}
