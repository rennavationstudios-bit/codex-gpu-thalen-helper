using System.Runtime.InteropServices;

namespace ThalenHelper.Core;

public sealed record ResourcePressureCheck(
    bool Allowed,
    string Code,
    string Message);

internal sealed record WindowsMemorySnapshot(
    uint MemoryLoadPercent,
    ulong AvailablePhysicalBytes,
    ulong TotalCommitBytes,
    ulong AvailableCommitBytes);

internal sealed class ResourcePressureGuard
{
    private const ulong GiB = 1024UL * 1024UL * 1024UL;
    private const ulong MiB = 1024UL * 1024UL;
    private readonly Func<HardwareProfile> _hardwareProvider;
    private readonly Func<WindowsMemorySnapshot?> _memoryProvider;
    private readonly Func<ModelManifest> _catalogProvider;

    public ResourcePressureGuard()
        : this(
            () => new HardwareDetector().Detect(),
            CaptureWindowsMemory,
            () => new ModelCatalogService().LoadBundled())
    {
    }

    internal ResourcePressureGuard(
        Func<HardwareProfile> hardwareProvider,
        Func<WindowsMemorySnapshot?> memoryProvider,
        Func<ModelManifest> catalogProvider)
    {
        _hardwareProvider = hardwareProvider;
        _memoryProvider = memoryProvider;
        _catalogProvider = catalogProvider;
    }

    public ResourcePressureCheck Check(
        InstallationState state,
        bool selectedModelAlreadyLoaded)
    {
        ArgumentNullException.ThrowIfNull(state);
        WindowsMemorySnapshot? memory;
        HardwareProfile hardware;
        ModelCatalogEntry? selectedModel;
        try
        {
            memory = _memoryProvider();
            hardware = _hardwareProvider();
            selectedModel = _catalogProvider().Models.FirstOrDefault(model =>
                ModelIntegrity.NamesMatch(model.Tag, state.SelectedModel!));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new ResourcePressureCheck(
                false,
                "RESOURCE_PRESSURE_CHECK_FAILED",
                "Current GPU and Windows memory pressure could not be measured safely; optional local review was refused.");
        }

        if (memory is null)
        {
            return new ResourcePressureCheck(
                false,
                "WINDOWS_COMMIT_PRESSURE_UNKNOWN",
                "Windows commit pressure could not be measured; optional local review was refused.");
        }

        if (selectedModel is null)
        {
            return new ResourcePressureCheck(
                false,
                "MODEL_CATALOG_ENTRY_MISSING",
                "The selected model is not present in the audited resource catalog; optional local review was refused.");
        }

        var minimumPhysicalReserve = Math.Max(
            2UL * GiB,
            GiBToBytesSaturating(selectedModel.MinimumSystemRamGiB * 0.10m));
        if (memory.MemoryLoadPercent >= 90
            || memory.AvailablePhysicalBytes < minimumPhysicalReserve)
        {
            return new ResourcePressureCheck(
                false,
                "WINDOWS_MEMORY_PRESSURE",
                "Available physical memory is below the safe reserve; optional local review was refused.");
        }

        if (memory.TotalCommitBytes == 0)
        {
            return new ResourcePressureCheck(
                false,
                "WINDOWS_COMMIT_PRESSURE_UNKNOWN",
                "Windows commit limit was unavailable; optional local review was refused.");
        }

        var minimumCommitReserve = Math.Max(4UL * GiB, memory.TotalCommitBytes / 10UL);
        if (memory.AvailableCommitBytes < minimumCommitReserve)
        {
            return new ResourcePressureCheck(
                false,
                "WINDOWS_COMMIT_PRESSURE",
                "Available Windows commit is below the safe reserve; optional local review was refused.");
        }

        var gpu = hardware.Gpus
            .Where(candidate => !candidate.IsIntegrated
                && candidate.AccelerationRoute is not AccelerationRoute.None and not AccelerationRoute.Cpu)
            .OrderByDescending(candidate => candidate.DedicatedMemoryBytes)
            .FirstOrDefault();
        if (gpu is not null && gpu.AvailableDedicatedMemoryBytes is null)
        {
            return new ResourcePressureCheck(
                false,
                "GPU_MEMORY_PRESSURE_UNKNOWN",
                "Available dedicated GPU memory could not be measured; optional local review was refused.");
        }

        if (gpu?.AvailableDedicatedMemoryBytes is ulong availableVram)
        {
            var configuredReserve = (ulong)Math.Max(512, state.Preferences.VramReserveMiB) * MiB;
            var minimumVramReserve = selectedModelAlreadyLoaded
                ? configuredReserve
                : SaturatingAdd(
                    GiBToBytesSaturating(selectedModel.MinimumDedicatedVramGiB),
                    configuredReserve);
            if (availableVram < minimumVramReserve)
            {
                return new ResourcePressureCheck(
                    false,
                    "GPU_MEMORY_PRESSURE",
                    "Available dedicated GPU memory is below the selected model's safe reserve; optional local review was refused.");
            }
        }

        return new ResourcePressureCheck(
            true,
            "RESOURCE_PRESSURE_OK",
            "GPU memory, physical memory, and Windows commit reserves are sufficient.");
    }

    private static ulong GiBToBytesSaturating(decimal value)
    {
        var rounded = Math.Ceiling(value);
        return rounded <= 0 || rounded > ulong.MaxValue / GiB
            ? ulong.MaxValue
            : (ulong)rounded * GiB;
    }

    private static ulong SaturatingAdd(ulong left, ulong right)
        => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static WindowsMemorySnapshot? CaptureWindowsMemory()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };
        return GlobalMemoryStatusEx(ref status)
            ? new WindowsMemorySnapshot(
                status.MemoryLoad,
                status.AvailablePhysical,
                status.TotalPageFile,
                status.AvailablePageFile)
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
