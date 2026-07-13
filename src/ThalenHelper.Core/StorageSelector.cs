namespace ThalenHelper.Core;

public sealed class StorageSelector
{
    private const ulong GiB = 1024UL * 1024UL * 1024UL;

    public StorageRecommendation Recommend(
        HardwareProfile profile,
        ModelCatalogEntry model,
        string directoryName = "CodexGPUThalenHelper\\Models")
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(model);

        var declaredMinimum = checked((ulong)Math.Ceiling(model.MinimumFreeDiskGiB) * GiB);
        var downloadWithTemporaryOverhead = checked((ulong)Math.Ceiling(model.ExpectedDownloadBytes * 2.15m));
        var required = Math.Max(declaredMinimum, downloadWithTemporaryOverhead);

        var candidates = profile.Volumes
            .Where(volume => volume.IsSuitable
                && volume.IsFixed
                && volume.MediaType is not StorageMediaType.Network and not StorageMediaType.Removable)
            .Select(volume => new
            {
                Volume = volume,
                Reserve = Math.Max(10UL * GiB, volume.IsSystem ? Math.Max(20UL * GiB, volume.TotalBytes / 10) : 10UL * GiB)
            })
            .Where(item => item.Volume.FreeBytes >= checked(required + item.Reserve))
            .OrderBy(item => Rank(item.Volume.MediaType))
            .ThenBy(item => item.Volume.IsSystem)
            .ThenByDescending(item => item.Volume.FreeBytes)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new StorageRecommendation(
                null,
                null,
                required,
                0,
                "No suitable fixed local volume has enough free space after safety reserves.",
                ["Removable and network storage are never selected automatically."]);
        }

        var selected = candidates[0];
        var warnings = new List<string>();
        if (selected.Volume.MediaType == StorageMediaType.Hdd)
        {
            warnings.Add("The selected location is on an HDD; model load and update operations may be noticeably slower.");
        }
        else if (selected.Volume.MediaType == StorageMediaType.Unknown)
        {
            warnings.Add("The storage media type could not be determined reliably; confirm performance before downloading a large model.");
        }

        if (selected.Volume.IsEncrypted)
        {
            warnings.Add("The selected volume is encrypted. It must be unlocked before Ollama starts.");
        }

        var modelDirectory = Path.Combine(selected.Volume.RootPath, directoryName);
        var remaining = selected.Volume.FreeBytes - required;
        return new StorageRecommendation(
            selected.Volume,
            modelDirectory,
            required,
            remaining,
            $"Selected {selected.Volume.RootPath} because it is the fastest suitable fixed volume with enough free space and reserve.",
            warnings);
    }

    private static int Rank(StorageMediaType mediaType)
    {
        return mediaType switch
        {
            StorageMediaType.Nvme => 0,
            StorageMediaType.Ssd => 1,
            StorageMediaType.Unknown => 2,
            StorageMediaType.Hdd => 3,
            _ => 10
        };
    }
}
