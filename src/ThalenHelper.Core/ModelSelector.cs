namespace ThalenHelper.Core;

public sealed class ModelSelector
{
    private const decimal GiB = 1024m * 1024m * 1024m;

    public ModelRecommendation Recommend(
        HardwareProfile profile,
        ModelManifest catalog,
        bool allowCpuFallback = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!profile.OperatingSystem.IsSupported
            || !string.Equals(profile.OperatingSystem.Architecture, "X64", StringComparison.OrdinalIgnoreCase))
        {
            return new ModelRecommendation(
                HardwareTier.Unsupported,
                null,
                0,
                true,
                false,
                "This build supports Windows x64 only. The helper can remain unconfigured, but local reviewing is disabled.",
                profile.OperatingSystem.Warning is null ? [] : [profile.OperatingSystem.Warning]);
        }

        var candidates = catalog.Models
            .Where(model => model.AutomaticSelectionAllowed && model.CommercialUseAllowed)
            .OrderByDescending(model => model.ParameterBillions)
            .ToArray();
        var usableGpu = profile.Gpus
            .Where(gpu => !gpu.IsIntegrated
                && gpu.DedicatedMemoryBytes > 0
                && gpu.AccelerationRoute is not AccelerationRoute.None and not AccelerationRoute.Unknown)
            .OrderByDescending(GetUsableVramGiB)
            .FirstOrDefault();

        if (usableGpu is not null)
        {
            var usableVramGiB = GetUsableVramGiB(usableGpu);
            if (profile.IsLaptop)
            {
                usableVramGiB *= 0.8m;
            }

            var totalRamGiB = profile.Memory.TotalBytes / GiB;
            var availableRamGiB = profile.Memory.AvailableBytes / GiB;
            var model = candidates.FirstOrDefault(candidate =>
                candidate.MinimumDedicatedVramGiB <= usableVramGiB
                && candidate.MinimumSystemRamGiB <= totalRamGiB
                && Math.Min(candidate.MinimumSystemRamGiB * 0.40m, 16m) <= availableRamGiB);

            if (model is not null)
            {
                var tier = GetHardwareTier(model);
                var warnings = new List<string>();
                if (usableGpu.Vendor == GpuVendor.Amd && usableGpu.AccelerationRoute == AccelerationRoute.AmdVulkan)
                {
                    warnings.Add("AMD Vulkan acceleration is a fallback route; validate runtime behavior before relying on it.");
                }

                if (profile.IsLaptop)
                {
                    warnings.Add("Laptop thermal and power limits reduced the usable GPU budget by 20%.");
                }

                return new ModelRecommendation(
                    tier,
                    model,
                    model.SafeDefaultContextTokens,
                    tier == HardwareTier.Entry,
                    false,
                    $"Selected {model.Tag} because it fits the conservative {usableVramGiB:F1} GiB usable-VRAM budget after reserving display and runtime headroom.",
                    warnings);
            }
        }

        if (allowCpuFallback)
        {
            var totalRamGiB = profile.Memory.TotalBytes / GiB;
            var availableRamGiB = profile.Memory.AvailableBytes / GiB;
            var cpuModel = candidates
                .Where(model => model.CpuFallbackReasonable
                    && model.MinimumSystemRamGiB <= totalRamGiB
                    && model.MinimumSystemRamGiB * 0.50m <= availableRamGiB)
                .OrderByDescending(model => model.ParameterBillions)
                .FirstOrDefault();
            if (cpuModel is not null)
            {
                return new ModelRecommendation(
                    HardwareTier.Entry,
                    cpuModel,
                    Math.Min(cpuModel.SafeDefaultContextTokens, 4096),
                    true,
                    true,
                    $"No safe dedicated-GPU fit was found. {cpuModel.Tag} is the bounded CPU fallback and requires explicit opt-in.",
                    ["CPU inference can be slow and may reduce system responsiveness."]);
            }
        }

        return new ModelRecommendation(
            HardwareTier.NoModel,
            null,
            0,
            true,
            !allowCpuFallback,
            "No model fits with safe GPU and memory headroom. Install in disabled/no-model mode or explicitly evaluate CPU fallback.",
            ["Shared graphics memory was not counted as dedicated VRAM."]);
    }

    private static decimal GetUsableVramGiB(GpuInfo gpu)
    {
        var dedicatedGiB = gpu.DedicatedMemoryBytes / GiB;
        var availableGiB = (gpu.AvailableDedicatedMemoryBytes ?? gpu.DedicatedMemoryBytes) / GiB;
        var reserve = dedicatedGiB switch
        {
            <= 2.25m => 0.5m,
            <= 4.5m => 0.75m,
            <= 8.5m => 1.25m,
            <= 16.5m => 2.0m,
            _ => 2.5m
        };
        return Math.Max(0, Math.Min(availableGiB, dedicatedGiB - reserve));
    }

    public static HardwareTier GetHardwareTier(ModelCatalogEntry model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ParseTier(model.PerformanceTier);
    }

    private static HardwareTier ParseTier(string tier)
    {
        return tier.ToLowerInvariant() switch
        {
            "entry" => HardwareTier.Entry,
            "mid" => HardwareTier.Mid,
            "high" => HardwareTier.High,
            "enthusiast" => HardwareTier.Enthusiast,
            _ => HardwareTier.NoModel
        };
    }
}
