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
    public void CompatibleModelChoicesAreDynamicAndExcludeModelsThatExceedHardware()
    {
        var selector = new ModelSelector();
        var catalog = new ModelCatalogService().LoadBundled();
        var eightGigabyte = FixtureFactory.Create(
            FixtureFactory.LoadHardwareFixtures().Single(item => item.Name == "nvidia-8gb"));
        var rtx3090 = FixtureFactory.Create(
            FixtureFactory.LoadHardwareFixtures().Single(item => item.Name == "nvidia-rtx3090-24gb"));

        var entryChoices = selector.GetCompatibleModels(eightGigabyte, catalog);
        var enthusiastChoices = selector.GetCompatibleModels(rtx3090, catalog);

        Assert.Contains(entryChoices, model => model.Tag == "qwen3:8b");
        Assert.DoesNotContain(entryChoices, model => model.Tag == "qwen3:14b");
        Assert.DoesNotContain(entryChoices, model => model.Provider == ModelProviders.LmStudio);
        Assert.Contains(enthusiastChoices, model => model.Tag == "qwen3-coder:30b");
        Assert.Contains(enthusiastChoices, model => model.Provider == ModelProviders.LmStudio);
    }

    [Fact]
    public void CpuOnlyChoicesRemainEmptyUntilExplicitlyEnabled()
    {
        var selector = new ModelSelector();
        var catalog = new ModelCatalogService().LoadBundled();
        var profile = FixtureFactory.Create(
            FixtureFactory.LoadHardwareFixtures().Single(item => item.Name == "cpu-only-16gb"));

        Assert.Empty(selector.GetCompatibleModels(profile, catalog));
        Assert.All(selector.GetCompatibleModels(profile, catalog, allowCpuFallback: true), model =>
            Assert.True(model.CpuFallbackReasonable));
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
    public void ExplicitAttachedFixedVolumeIsAllowedWithDisconnectWarningButNeverAutoSelected()
    {
        var model = new ModelCatalogService().LoadBundled().Models.Single(item => item.Tag == "qwen3:8b");
        var profile = FixtureFactory.Create(FixtureFactory.LoadHardwareFixtures().First()) with
        {
            Volumes = [Volume("X:\\", StorageMediaType.Removable, 300, false, false)]
        };

        var automatic = new StorageSelector().Recommend(profile, model);
        var explicitSelection = InstallationManager.ValidateCustomStorage(profile, model, @"X:\Models\Ollama");

        Assert.Null(automatic.Volume);
        Assert.Equal("X:\\", explicitSelection.Volume?.RootPath);
        Assert.Equal(@"X:\Models\Ollama", explicitSelection.ModelDirectory);
        Assert.Contains(explicitSelection.Warnings, warning =>
            warning.Contains("connected", StringComparison.OrdinalIgnoreCase)
            && warning.Contains("drive letter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExplicitNetworkOrNonFixedModelStorageStillFailsClosed()
    {
        var model = new ModelCatalogService().LoadBundled().Models.Single(item => item.Tag == "qwen3:8b");
        var profile = FixtureFactory.Create(FixtureFactory.LoadHardwareFixtures().First()) with
        {
            Volumes =
            [
                new StorageVolume("N:\\", "NTFS", 1000 * GiB, 300 * GiB, StorageMediaType.Network, true, false, false, false, "network"),
                new StorageVolume("R:\\", "NTFS", 1000 * GiB, 300 * GiB, StorageMediaType.Removable, false, false, false, false, "not fixed")
            ]
        };

        Assert.Throws<InvalidOperationException>(() =>
            InstallationManager.ValidateCustomStorage(profile, model, @"N:\Models"));
        Assert.Throws<InvalidOperationException>(() =>
            InstallationManager.ValidateCustomStorage(profile, model, @"R:\Models"));
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

        var unsafeResources = valid with
        {
            Models = [valid.Models[0] with { MinimumDedicatedVramGiB = decimal.MaxValue }, .. valid.Models.Skip(1)]
        };
        Assert.Throws<InvalidDataException>(() => new ModelCatalogService().Validate(unsafeResources));
    }

    private static StorageVolume Volume(
        string root,
        StorageMediaType media,
        ulong freeGiB,
        bool system,
        bool suitable)
        => new(root, "NTFS", 1000 * GiB, freeGiB * GiB, media, true, system, false, suitable, suitable ? null : "not suitable");
}
