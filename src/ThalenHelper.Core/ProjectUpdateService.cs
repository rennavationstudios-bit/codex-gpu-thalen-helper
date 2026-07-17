using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ThalenHelper.Core;

public sealed record ProjectUpdateInfo(
    bool UpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    string? InstallerUrl,
    string? ChecksumsUrl,
    bool Prerelease,
    string Code,
    string Message);

public sealed record ProjectUpdateResult(
    bool Success,
    string Code,
    string Message,
    string? InstallerPath,
    string? Sha256,
    bool Launched);

public sealed class ProjectUpdateService : IDisposable
{
    public const string Repository = "rennavationstudios-bit/codex-gpu-thalen-helper";
    public const string InstallerAssetName = "Codex-GPU-Thalen-Helper-Setup.exe";
    public const string ChecksumsAssetName = "SHA256SUMS.txt";
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public ProjectUpdateService(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = true
        });
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("codex-gpu-thalen-helper", ProductInfo.Version));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<ProjectUpdateInfo> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases?per_page=20");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new ProjectUpdateInfo(
                false, ProductInfo.Version, null, null, null, null, false,
                "UPDATE_CHECK_FAILED",
                $"GitHub returned HTTP {(int)response.StatusCode} while checking for updates.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new ProjectUpdateInfo(false, ProductInfo.Version, null, null, null, null, false, "UPDATE_RESPONSE_INVALID", "GitHub returned an invalid release inventory.");
        }

        ReleaseCandidate? best = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                continue;
            }

            var tag = release.GetProperty("tag_name").GetString();
            var html = release.GetProperty("html_url").GetString();
            var prerelease = release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            var installer = FindAsset(assets, InstallerAssetName);
            var checksums = FindAsset(assets, ChecksumsAssetName);
            if (tag is null || installer is null || checksums is null
                || !SemanticVersion.TryParse(tag, out var semanticVersion))
            {
                continue;
            }

            var version = tag.TrimStart('v', 'V');
            var candidate = new ReleaseCandidate(
                semanticVersion,
                version,
                html,
                installer,
                checksums,
                prerelease);
            if (best is null || candidate.SemanticVersion.CompareTo(best.SemanticVersion) > 0)
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            return new ProjectUpdateInfo(false, ProductInfo.Version, null, null, null, null, false, "NO_RELEASE_FOUND", "No complete published release with installer and checksums was found.");
        }

        var available = IsNewerVersion(best.Version, ProductInfo.Version);
        return new ProjectUpdateInfo(
            available,
            ProductInfo.Version,
            best.Version,
            best.ReleaseUrl,
            best.InstallerUrl,
            best.ChecksumsUrl,
            best.Prerelease,
            available ? "UPDATE_AVAILABLE" : "UP_TO_DATE",
            available ? $"Version {best.Version} is available." : "The published release is not newer than this installation.");
    }

    public async Task<ProjectUpdateResult> DownloadVerifyAndLaunchAsync(
        ProjectUpdateInfo update,
        bool launch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!update.UpdateAvailable || update.InstallerUrl is null || update.ChecksumsUrl is null)
        {
            return new ProjectUpdateResult(false, "NO_UPDATE", "No verified update is available to download.", null, null, false);
        }

        var installerUri = ValidateDownloadUri(update.InstallerUrl);
        var checksumsUri = ValidateDownloadUri(update.ChecksumsUrl);
        var checksumText = await DownloadStringAsync(checksumsUri, cancellationToken).ConfigureAwait(false);
        var expected = ParseChecksum(checksumText, InstallerAssetName);
        var directory = Path.Combine(Path.GetTempPath(), "CodexGpuThalenHelperUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, InstallerAssetName);
        await DownloadFileAsync(installerUri, destination, cancellationToken).ConfigureAwait(false);
        string actual;
        await using (var installerStream = File.OpenRead(destination))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(
                installerStream,
                cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }
        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expected),
            Convert.FromHexString(actual)))
        {
            File.Delete(destination);
            return new ProjectUpdateResult(false, "UPDATE_CHECKSUM_MISMATCH", "The downloaded installer failed SHA-256 verification and was deleted.", null, actual, false);
        }

        var launched = false;
        if (launch)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = destination,
                UseShellExecute = true
            });
            launched = process is not null;
        }

        return new ProjectUpdateResult(
            true,
            launch ? "UPDATE_LAUNCHED" : "UPDATE_VERIFIED",
            launch ? "The update passed SHA-256 verification and its installer was launched." : "The update passed SHA-256 verification.",
            destination,
            actual,
            launched);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    internal static bool IsNewerVersion(string candidate, string current)
        => SemanticVersion.TryParse(candidate, out var candidateVersion)
            && SemanticVersion.TryParse(current, out var currentVersion)
            && candidateVersion.CompareTo(currentVersion) > 0;

    private async Task<string> DownloadStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await SendWithApprovedRedirectsAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (content.Length > 256 * 1024)
        {
            throw new InvalidDataException("The release checksum file is unexpectedly large.");
        }

        return content;
    }

    private async Task DownloadFileAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var response = await SendWithApprovedRedirectsAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is > 512L * 1024 * 1024)
        {
            throw new InvalidDataException("The release installer exceeds the 512 MiB safety limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > 512L * 1024 * 1024)
            {
                throw new InvalidDataException("The release installer exceeds the 512 MiB safety limit.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ParseChecksum(string content, string assetName)
    {
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2
                && string.Equals(parts[^1].TrimStart('*'), assetName, StringComparison.Ordinal)
                && parts[0].Length == 64
                && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        throw new InvalidDataException($"No valid SHA-256 entry was found for {assetName}.");
    }

    private static Uri ValidateDownloadUri(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps
            || (uri.Host is not "github.com"
                and not "objects.githubusercontent.com"
                and not "githubusercontent.com"
                and not "release-assets.githubusercontent.com"))
        {
            throw new InvalidDataException("Release assets must use an approved HTTPS GitHub host.");
        }

        return uri;
    }

    private async Task<HttpResponseMessage> SendWithApprovedRedirectsAsync(Uri initial, CancellationToken cancellationToken)
    {
        var current = ValidateDownloadUri(initial.AbsoluteUri);
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            var response = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
            {
                var location = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                response.Dispose();
                current = ValidateDownloadUri(location.AbsoluteUri);
                continue;
            }

            return response;
        }

        throw new HttpRequestException("The GitHub release asset exceeded the redirect limit.");
    }

    private static string? FindAsset(IEnumerable<JsonElement> assets, string name)
        => assets.FirstOrDefault(asset => string.Equals(asset.GetProperty("name").GetString(), name, StringComparison.Ordinal))
            .TryGetProperty("browser_download_url", out var url)
                ? url.GetString()
                : null;

    private sealed record SemanticVersion(
        int Major,
        int Minor,
        int Patch,
        IReadOnlyList<string> Prerelease) : IComparable<SemanticVersion>
    {
        public static bool TryParse(string value, out SemanticVersion version)
        {
            version = null!;
            var normalized = value.Trim();
            if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            {
                normalized = normalized[1..];
            }

            var buildParts = normalized.Split('+');
            if (buildParts.Length > 2
                || (buildParts.Length == 2
                    && (buildParts[1].Length == 0
                        || buildParts[1].Split('.').Any(identifier => !IsValidIdentifier(identifier)))))
            {
                return false;
            }

            normalized = buildParts[0];
            var prereleaseIndex = normalized.IndexOf('-');
            var core = prereleaseIndex >= 0 ? normalized[..prereleaseIndex] : normalized;
            var prerelease = prereleaseIndex >= 0
                ? normalized[(prereleaseIndex + 1)..].Split('.')
                : [];
            var parts = core.Split('.');
            if (parts.Length != 3
                || !TryParseCoreNumber(parts[0], out var major)
                || !TryParseCoreNumber(parts[1], out var minor)
                || !TryParseCoreNumber(parts[2], out var patch)
                || (prereleaseIndex >= 0
                    && (prerelease.Length == 0
                        || prerelease.Any(identifier =>
                            !IsValidIdentifier(identifier)
                            || (identifier.All(char.IsDigit) && identifier.Length > 1 && identifier[0] == '0')))))
            {
                return false;
            }

            version = new SemanticVersion(major, minor, patch, prerelease);
            return true;
        }

        private static bool TryParseCoreNumber(string value, out int number)
        {
            number = 0;
            return value.Length > 0
                && (value.Length == 1 || value[0] != '0')
                && value.All(char.IsDigit)
                && int.TryParse(value, out number);
        }

        private static bool IsValidIdentifier(string value)
            => value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            var core = Major.CompareTo(other.Major);
            if (core == 0) core = Minor.CompareTo(other.Minor);
            if (core == 0) core = Patch.CompareTo(other.Patch);
            if (core != 0) return core;
            if (Prerelease.Count == 0) return other.Prerelease.Count == 0 ? 0 : 1;
            if (other.Prerelease.Count == 0) return -1;
            for (var index = 0; index < Math.Min(Prerelease.Count, other.Prerelease.Count); index++)
            {
                var leftNumeric = int.TryParse(Prerelease[index], out var left);
                var rightNumeric = int.TryParse(other.Prerelease[index], out var right);
                var comparison = leftNumeric && rightNumeric
                    ? left.CompareTo(right)
                    : leftNumeric
                        ? -1
                        : rightNumeric
                            ? 1
                            : string.CompareOrdinal(Prerelease[index], other.Prerelease[index]);
                if (comparison != 0) return comparison;
            }

            return Prerelease.Count.CompareTo(other.Prerelease.Count);
        }
    }

    private sealed record ReleaseCandidate(
        SemanticVersion SemanticVersion,
        string Version,
        string? ReleaseUrl,
        string InstallerUrl,
        string ChecksumsUrl,
        bool Prerelease);
}
