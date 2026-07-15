# Hardware and storage detection

The profiler targets Windows x64 and collects only data needed for safe local inference.

## Operating system and CPU

Windows version/edition and process architecture are read from Windows APIs/WMI. CPU vendor/model, physical/logical cores, and AVX/AVX2/FMA support are reported. ARM64 and other unsupported architectures are rejected. Windows 10 receives an end-of-general-support warning.

## Memory and GPU

Immediately before optional inference, the helper requires current Windows physical-memory and commit-reserve measurements. When a discrete GPU is present, it also requires a current available-dedicated-VRAM measurement; unknown free VRAM fails closed instead of guessing. The initial beta obtains that measurement from supported NVIDIA tooling. AMD or Intel adapters whose current dedicated-VRAM availability cannot be measured remain installable but local review is refused with `GPU_MEMORY_PRESSURE_UNKNOWN` until a supported measurement route is available.

Installed/available system RAM is measured separately. GPU adapters are enumerated through DXGI, which provides modern dedicated and shared memory values without relying on the unreliable `Win32_VideoController.AdapterRAM` field.

For NVIDIA, `nvidia-smi` augments adapter data with total/free VRAM, driver version, and compute capability. It is resolved only from the Windows system directory or the established NVIDIA `Program Files` location and is launched by absolute path; the current directory and `PATH` are never searched. AMD/Intel routes are labeled conservatively. Integrated adapters and adapters with unsupported routes are not treated as dedicated-accelerator candidates. Shared memory is never added to dedicated VRAM.

Usable VRAM is the lesser of free/total dedicated VRAM after display/runtime reserve. The reserve increases for larger cards. Laptop profiles reduce the usable budget by 20 percent.

## Storage

Windows Storage WMI maps partitions to physical disks and distinguishes NVMe/SSD/HDD/USB where reliable. Drive readiness, filesystem, free/total space, fixed/removable/network status, system-volume status, and best-effort BitLocker state are recorded. Serial numbers are not queried.

Selection order is NVMe, SSD, unknown fixed media, then HDD. Removable/network volumes are rejected. Required space is the larger of catalog minimum and 2.15 times expected download bytes. Non-system volumes retain at least 10 GiB; system volumes retain at least 20 GiB or 10 percent of total size, whichever is larger.

## Fixture coverage

Tests cover MX330 2 GB plus integrated Intel, NVIDIA 4/6/8/12/16/24 GB, supported AMD, integrated Intel only, CPU-only 8/16 GB, multiple GPUs, unsupported routes/architectures, insufficient disk, HDD, removable media, and competing SSD/NVMe volumes.
