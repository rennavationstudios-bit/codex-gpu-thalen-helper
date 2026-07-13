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
            if (tag is null || installer is null || checksums is null)
            {
                continue;
            }

            var version = tag.TrimStart('v', 'V');
            var available = !string.Equals(version, ProductInfo.Version, StringComparison.OrdinalIgnoreCase);
            return new ProjectUpdateInfo(
                available,
                ProductInfo.Version,
                version,
                html,
                installer,
                checksums,
                prerelease,
                available ? "UPDATE_AVAILABLE" : "UP_TO_DATE",
                available ? $"Version {version} is available." : "This installation matches the latest published release.");
        }

        return new ProjectUpdateInfo(false, ProductInfo.Version, null, null, null, null, false, "NO_RELEASE_FOUND", "No complete published release with installer and checksums was found.");
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
}
