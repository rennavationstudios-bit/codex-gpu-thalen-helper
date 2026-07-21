using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace ThalenHelper.Core;

public sealed class HardwareDetector
{
    private const ulong MiB = 1024UL * 1024UL;

    public HardwareProfile Detect()
    {
        var warnings = new List<string>();
        var operatingSystem = DetectOperatingSystem();
        var cpu = DetectCpu(warnings);
        var memory = DetectMemory(warnings);
        var gpus = DetectGpus(warnings);
        var volumes = DetectVolumes(warnings);
        var isLaptop = DetectLaptop(warnings);
        if (operatingSystem.Warning is not null)
        {
            warnings.Add(operatingSystem.Warning);
        }

        return new HardwareProfile(operatingSystem, cpu, memory, gpus, volumes, isLaptop, warnings);
    }

    private static OperatingSystemInfo DetectOperatingSystem()
    {
        var version = Environment.OSVersion.Version;
        var architecture = RuntimeInformation.OSArchitecture.ToString();
        var productName = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            "ProductName",
            "Windows")?.ToString() ?? "Windows";
        if (version.Build >= 22000 && productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        }

        var x64 = RuntimeInformation.OSArchitecture == Architecture.X64;
        var supportedVersion = version.Major >= 10 && version.Build >= 19045;
        string? warning = null;
        if (!x64)
        {
            warning = $"Unsupported Windows architecture: {architecture}. This release supports x64 only.";
        }
        else if (!supportedVersion)
        {
            warning = "Windows 10 22H2 or newer is required.";
        }
        else if (version.Build < 22000)
        {
            warning = "Windows 10 is no longer generally supported by Microsoft; Windows 11 is recommended.";
        }

        return new OperatingSystemInfo(productName, version, architecture, x64 && supportedVersion, warning);
    }

    private static CpuInfo DetectCpu(List<string> warnings)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            using var results = searcher.Get();
            var processors = results.Cast<ManagementObject>().ToArray();
            var first = processors.FirstOrDefault();
            var physicalCores = processors.Sum(item => Convert.ToInt32(item["NumberOfCores"], CultureInfo.InvariantCulture));
            var logicalCores = processors.Sum(item => Convert.ToInt32(item["NumberOfLogicalProcessors"], CultureInfo.InvariantCulture));
            return new CpuInfo(
                first?["Manufacturer"]?.ToString()?.Trim() ?? "Unknown",
                first?["Name"]?.ToString()?.Trim() ?? "Unknown CPU",
                physicalCores > 0 ? physicalCores : Environment.ProcessorCount,
                logicalCores > 0 ? logicalCores : Environment.ProcessorCount,
                Avx.IsSupported,
                Avx2.IsSupported,
                Fma.IsSupported);
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException)
        {
            warnings.Add("Detailed CPU information was unavailable; inference capability is conservative.");
            return new CpuInfo(
                "Unknown",
                Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown CPU",
                Environment.ProcessorCount,
                Environment.ProcessorCount,
                Avx.IsSupported,
                Avx2.IsSupported,
                Fma.IsSupported);
        }
    }

    private static MemoryInfo DetectMemory(List<string> warnings)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            using var results = searcher.Get();
            var item = results.Cast<ManagementObject>().First();
            var total = Convert.ToUInt64(item["TotalVisibleMemorySize"], CultureInfo.InvariantCulture) * 1024UL;
            var available = Convert.ToUInt64(item["FreePhysicalMemory"], CultureInfo.InvariantCulture) * 1024UL;
            return new MemoryInfo(total, available);
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException)
        {
            warnings.Add("Available physical memory could not be measured.");
            return new MemoryInfo(0, 0);
        }
    }

    private static IReadOnlyList<GpuInfo> DetectGpus(List<string> warnings)
    {
        var driverVersions = ReadVideoControllerDrivers();
        var adapters = DxgiAdapterReader.ReadAdapters(warnings)
            .Where(adapter => !adapter.Name.Contains("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase))
            .Select(adapter =>
            {
                var vendor = GetGpuVendor(adapter.VendorId, adapter.Name);
                var integrated = vendor == GpuVendor.Intel
                    || adapter.DedicatedMemoryBytes < 512UL * MiB;
                return new GpuInfo(
                    vendor,
                    adapter.Name,
                    adapter.DedicatedMemoryBytes,
                    adapter.SharedMemoryBytes,
                    null,
                    FindDriverVersion(driverVersions, adapter.Name),
                    null,
                    GetAccelerationRoute(vendor, adapter.Name),
                    integrated);
            })
            .ToList();

        var nvidia = ReadNvidiaSmi(warnings);
        foreach (var measured in nvidia)
        {
            var index = adapters.FindIndex(adapter =>
                adapter.Vendor == GpuVendor.Nvidia
                && (adapter.Name.Contains(measured.Name, StringComparison.OrdinalIgnoreCase)
                    || measured.Name.Contains(adapter.Name, StringComparison.OrdinalIgnoreCase)));
            var replacement = new GpuInfo(
                GpuVendor.Nvidia,
                measured.Name,
                measured.TotalMemoryBytes,
                index >= 0 ? adapters[index].SharedMemoryBytes : 0,
                measured.FreeMemoryBytes,
                measured.DriverVersion,
                measured.ComputeCapability,
                AccelerationRoute.NvidiaCuda,
                false);
            if (index >= 0)
            {
                adapters[index] = replacement;
            }
            else
            {
                adapters.Add(replacement);
            }
        }

        if (adapters.Count == 0)
        {
            warnings.Add("No display adapter with reliable memory data was detected.");
        }

        return adapters;
    }

    private static Dictionary<string, string?> ReadVideoControllerDrivers()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            using var results = searcher.Get();
            return BuildVideoControllerDrivers(results.Cast<ManagementObject>()
                .Select(item => (
                    item["Name"]?.ToString(),
                    item["DriverVersion"]?.ToString())));
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static Dictionary<string, string?> BuildVideoControllerDrivers(
        IEnumerable<(string? Name, string? DriverVersion)> controllers)
    {
        var drivers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var conflictingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var controller in controllers)
        {
            var name = controller.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var driverVersion = string.IsNullOrWhiteSpace(controller.DriverVersion)
                ? null
                : controller.DriverVersion.Trim();
            if (!drivers.TryGetValue(name, out var existingVersion))
            {
                drivers[name] = driverVersion;
                continue;
            }

            if (conflictingNames.Contains(name) || driverVersion is null)
            {
                continue;
            }

            if (existingVersion is null)
            {
                drivers[name] = driverVersion;
            }
            else if (!string.Equals(existingVersion, driverVersion, StringComparison.OrdinalIgnoreCase))
            {
                drivers[name] = null;
                conflictingNames.Add(name);
            }
        }

        return drivers;
    }

    private static string? FindDriverVersion(Dictionary<string, string?> drivers, string adapterName)
    {
        return drivers.FirstOrDefault(item =>
            item.Key.Contains(adapterName, StringComparison.OrdinalIgnoreCase)
            || adapterName.Contains(item.Key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static IReadOnlyList<NvidiaMeasurement> ReadNvidiaSmi(List<string> warnings)
    {
        var executable = ResolveNvidiaSmiPath(Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (executable is null)
        {
            if (DxgiAdapterReader.HasNvidiaAdapter)
            {
                warnings.Add("NVIDIA was detected, but nvidia-smi was unavailable; free VRAM and compute capability are unknown.");
            }

            return [];
        }

        try
        {
            using var process = Process.Start(CreateNvidiaSmiStartInfo(executable));
            if (process is null || !process.WaitForExit(5_000) || process.ExitCode != 0)
            {
                if (process is { HasExited: false })
                {
                    process.Kill(true);
                }

                return [];
            }

            var measurements = new List<NvidiaMeasurement>();
            foreach (var line in process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length < 5
                    || !ulong.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalMiB)
                    || !ulong.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var freeMiB))
                {
                    continue;
                }

                measurements.Add(new NvidiaMeasurement(
                    parts[0],
                    totalMiB * MiB,
                    freeMiB * MiB,
                    parts[3],
                    parts[4]));
            }

            return measurements;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (DxgiAdapterReader.HasNvidiaAdapter)
            {
                warnings.Add("NVIDIA was detected, but nvidia-smi was unavailable; free VRAM and compute capability are unknown.");
            }

            return [];
        }
    }

    internal static string? ResolveNvidiaSmiPath(string? systemDirectory, string? programFilesDirectory)
    {
        var candidates = new[]
        {
            CreateTrustedNvidiaSmiCandidate(systemDirectory, "nvidia-smi.exe"),
            CreateTrustedNvidiaSmiCandidate(programFilesDirectory, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        };

        return candidates.FirstOrDefault(path => path is not null && File.Exists(path));
    }

    internal static ProcessStartInfo CreateNvidiaSmiStartInfo(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("nvidia-smi must be launched from a trusted absolute path.", nameof(executablePath));
        }

        return new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executablePath),
            Arguments = "--query-gpu=name,memory.total,memory.free,driver_version,compute_cap --format=csv,noheader,nounits",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static string? CreateTrustedNvidiaSmiCandidate(string? trustedRoot, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(trustedRoot) || !Path.IsPathFullyQualified(trustedRoot))
        {
            return null;
        }

        var normalizedRoot = Path.GetFullPath(trustedRoot);
        var candidate = Path.GetFullPath(Path.Combine([normalizedRoot, .. segments]));
        return candidate.StartsWith(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    private static IReadOnlyList<StorageVolume> DetectVolumes(List<string> warnings)
    {
        var media = WindowsStorageReader.GetMediaTypes(warnings);
        var encrypted = WindowsStorageReader.GetEncryptedDriveLetters();
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var volumes = new List<StorageVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var isFixed = drive.DriveType == DriveType.Fixed;
                var mediaType = drive.DriveType switch
                {
                    DriveType.Network => StorageMediaType.Network,
                    DriveType.Removable => StorageMediaType.Removable,
                    DriveType.Fixed => media.GetValueOrDefault(drive.Name, StorageMediaType.Unknown),
                    _ => StorageMediaType.Unknown
                };
                var suitable = drive.IsReady
                    && isFixed
                    && mediaType is not StorageMediaType.Removable and not StorageMediaType.Network;
                var rejection = suitable
                    ? null
                    : !drive.IsReady
                        ? "Volume is not ready."
                        : mediaType is StorageMediaType.Removable or StorageMediaType.Network
                            ? "Volume is removable or network-attached."
                            : "Volume is not fixed local storage.";
                volumes.Add(new StorageVolume(
                    drive.Name,
                    drive.IsReady ? drive.DriveFormat : "Unknown",
                    drive.IsReady ? (ulong)drive.TotalSize : 0,
                    drive.IsReady ? (ulong)drive.AvailableFreeSpace : 0,
                    mediaType,
                    isFixed,
                    string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase),
                    encrypted.Contains(drive.Name.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase),
                    suitable,
                    rejection));
            }
            catch (IOException)
            {
                volumes.Add(new StorageVolume(
                    drive.Name,
                    "Unknown",
                    0,
                    0,
                    StorageMediaType.Unknown,
                    drive.DriveType == DriveType.Fixed,
                    string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase),
                    false,
                    false,
                    "Volume information could not be read."));
            }
        }

        return volumes;
    }

    private static bool DetectLaptop(List<string> warnings)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT PCSystemType FROM Win32_ComputerSystem");
            using var results = searcher.Get();
            var type = Convert.ToUInt16(results.Cast<ManagementObject>().First()["PCSystemType"], CultureInfo.InvariantCulture);
            return type == 2;
        }
        catch (ManagementException)
        {
            warnings.Add("Laptop form factor could not be determined.");
            return false;
        }
    }

    private static GpuVendor GetGpuVendor(uint vendorId, string name)
    {
        return vendorId switch
        {
            0x10DE => GpuVendor.Nvidia,
            0x1002 or 0x1022 => GpuVendor.Amd,
            0x8086 => GpuVendor.Intel,
            _ when name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) => GpuVendor.Nvidia,
            _ when name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) => GpuVendor.Amd,
            _ when name.Contains("Intel", StringComparison.OrdinalIgnoreCase) => GpuVendor.Intel,
            _ => GpuVendor.Other
        };
    }

    private static AccelerationRoute GetAccelerationRoute(GpuVendor vendor, string name)
    {
        return vendor switch
        {
            GpuVendor.Nvidia => AccelerationRoute.NvidiaCuda,
            GpuVendor.Amd when name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) => AccelerationRoute.AmdRocm,
            GpuVendor.Intel => AccelerationRoute.IntelVulkan,
            _ => AccelerationRoute.Unknown
        };
    }

    private sealed record NvidiaMeasurement(
        string Name,
        ulong TotalMemoryBytes,
        ulong FreeMemoryBytes,
        string DriverVersion,
        string ComputeCapability);
}

internal static class WindowsStorageReader
{
    public static IReadOnlyDictionary<string, StorageMediaType> GetMediaTypes(List<string> warnings)
    {
        var result = new Dictionary<string, StorageMediaType>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            var disks = Query(scope, "SELECT Number, BusType, FriendlyName FROM MSFT_Disk")
                .ToDictionary(
                    item => Convert.ToUInt32(item["Number"], CultureInfo.InvariantCulture),
                    item => new
                    {
                        BusType = Convert.ToUInt16(item["BusType"], CultureInfo.InvariantCulture),
                        FriendlyName = item["FriendlyName"]?.ToString() ?? string.Empty
                    });
            var physical = Query(scope, "SELECT DeviceId, MediaType, BusType, FriendlyName FROM MSFT_PhysicalDisk")
                .Select(item => new
                {
                    DeviceId = uint.TryParse(item["DeviceId"]?.ToString(), out var id) ? id : uint.MaxValue,
                    MediaType = Convert.ToUInt16(item["MediaType"], CultureInfo.InvariantCulture),
                    BusType = Convert.ToUInt16(item["BusType"], CultureInfo.InvariantCulture),
                    FriendlyName = item["FriendlyName"]?.ToString() ?? string.Empty
                })
                .ToArray();
            foreach (var partition in Query(scope, "SELECT DriveLetter, DiskNumber FROM MSFT_Partition WHERE DriveLetter IS NOT NULL"))
            {
                var driveLetter = partition["DriveLetter"]?.ToString();
                if (string.IsNullOrWhiteSpace(driveLetter) || driveLetter[0] == '\0')
                {
                    continue;
                }

                var diskNumber = Convert.ToUInt32(partition["DiskNumber"], CultureInfo.InvariantCulture);
                disks.TryGetValue(diskNumber, out var disk);
                var physicalDisk = physical.FirstOrDefault(item => item.DeviceId == diskNumber)
                    ?? physical.FirstOrDefault(item => disk is not null
                        && !string.IsNullOrWhiteSpace(disk.FriendlyName)
                        && item.FriendlyName.Contains(disk.FriendlyName, StringComparison.OrdinalIgnoreCase));
                var busType = physicalDisk?.BusType ?? disk?.BusType ?? 0;
                var mediaType = physicalDisk?.MediaType ?? 0;
                result[driveLetter.TrimEnd(':', '\\') + ":\\"] = busType switch
                {
                    7 => StorageMediaType.Removable,
                    17 => StorageMediaType.Nvme,
                    _ => mediaType switch
                    {
                        3 => StorageMediaType.Hdd,
                        4 => StorageMediaType.Ssd,
                        _ => StorageMediaType.Unknown
                    }
                };
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException)
        {
            warnings.Add("Storage media type could not be mapped reliably for every volume.");
        }

        return result;
    }

    public static IReadOnlySet<string> GetEncryptedDriveLetters()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
            scope.Connect();
            foreach (var item in Query(scope, "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume"))
            {
                var letter = item["DriveLetter"]?.ToString();
                var protection = Convert.ToUInt32(item["ProtectionStatus"], CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(letter) && protection != 0)
                {
                    result.Add(letter.TrimEnd('\\'));
                }
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException)
        {
            // Encryption detection is best-effort and does not make a ready fixed volume unsafe by itself.
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(ManagementScope scope, string query)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query));
        using var collection = searcher.Get();
        return collection.Cast<ManagementObject>()
            .Select(item => (IReadOnlyDictionary<string, object?>)item.Properties
                .Cast<PropertyData>()
                .ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }
}

internal static class DxgiAdapterReader
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    public static bool HasNvidiaAdapter { get; private set; }

    public static IReadOnlyList<DxgiAdapter> ReadAdapters(List<string> warnings)
    {
        var adapters = new List<DxgiAdapter>();
        if (!OperatingSystem.IsWindows())
        {
            return adapters;
        }

        IDXGIFactory1? factory = null;
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            var result = CreateDXGIFactory1(ref iid, out factory);
            Marshal.ThrowExceptionForHR(result);
            for (uint index = 0; ; index++)
            {
                var hr = factory.EnumAdapters1(index, out var adapter);
                if (hr == DxgiErrorNotFound)
                {
                    break;
                }

                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    Marshal.ThrowExceptionForHR(adapter.GetDesc1(out var description));
                    var item = new DxgiAdapter(
                        description.Description.TrimEnd('\0'),
                        description.VendorId,
                        description.DedicatedVideoMemory.ToUInt64(),
                        description.SharedSystemMemory.ToUInt64());
                    adapters.Add(item);
                    HasNvidiaAdapter |= description.VendorId == 0x10DE;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(adapter);
                }
            }
        }
        catch (Exception exception) when (exception is COMException or DllNotFoundException)
        {
            warnings.Add("DXGI adapter memory detection failed; GPU recommendations are disabled unless a vendor tool provides reliable data.");
        }
        finally
        {
            if (factory is not null)
            {
                Marshal.FinalReleaseComObject(factory);
            }
        }

        return adapters;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint adapter, out IntPtr adapterPointer);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr description, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint adapter, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 adapterObject);
        [PreserveSig] int IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint output, out IntPtr outputPointer);
        [PreserveSig] int GetDesc(out IntPtr description);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long userModeDriverVersion);
        [PreserveSig] int GetDesc1(out DxgiAdapterDescription1 description);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}

internal sealed record DxgiAdapter(
    string Name,
    uint VendorId,
    ulong DedicatedMemoryBytes,
    ulong SharedMemoryBytes);
