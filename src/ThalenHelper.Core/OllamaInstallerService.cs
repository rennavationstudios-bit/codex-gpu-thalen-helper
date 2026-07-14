using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThalenHelper.Core;

public sealed record OllamaReleaseInfo(
    string Version,
    Uri InstallerUri,
    Uri ChecksumUri,
    string ExpectedSha256,
    string Source);

public sealed record OllamaInstallResult(
    bool Success,
    string Code,
    string Message,
    string? Version,
    string? Source,
    bool SignatureVerified,
    bool ChecksumVerified);

public sealed partial class OllamaInstallerService : IDisposable
{
    private const string LatestReleaseApi = "https://api.github.com/repos/ollama/ollama/releases/latest";
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private const int MaximumChecksumBytes = 1024 * 1024;
    private const long MaximumInstallerBytes = 2L * 1024 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public OllamaInstallerService(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        });
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"Codex-GPU-Thalen-Helper/{ProductInfo.Version}");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<OllamaReleaseInfo> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            LatestReleaseApi,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var metadata = await ReadBoundedAsync(response.Content, MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(metadata);
        var tag = document.RootElement.GetProperty("tag_name").GetString()
            ?? throw new InvalidDataException("The official Ollama release has no version tag.");
        if (!VersionTagRegex().IsMatch(tag))
        {
            throw new InvalidDataException("The official Ollama version tag is invalid.");
        }

        var assets = document.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        var installer = FindAsset(assets, "OllamaSetup.exe");
        var checksum = FindAsset(assets, "sha256sum.txt");
        var installerUri = ValidateOfficialAssetUri(installer.GetProperty("browser_download_url").GetString());
        var checksumUri = ValidateOfficialAssetUri(checksum.GetProperty("browser_download_url").GetString());
        using var checksumResponse = await _httpClient.GetAsync(
            checksumUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        checksumResponse.EnsureSuccessStatusCode();
        var checksumBytes = await ReadBoundedAsync(checksumResponse.Content, MaximumChecksumBytes, cancellationToken).ConfigureAwait(false);
        var checksumText = System.Text.Encoding.UTF8.GetString(checksumBytes);
        var expected = checksumText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Select(line => ChecksumLineRegex().Match(line))
            .Where(match => match.Success && string.Equals(match.Groups[2].Value, "OllamaSetup.exe", StringComparison.Ordinal))
            .Select(match => match.Groups[1].Value.ToLowerInvariant())
            .SingleOrDefault()
            ?? throw new InvalidDataException("The official checksum file does not contain OllamaSetup.exe.");
        return new OllamaReleaseInfo(tag, installerUri, checksumUri, expected, $"Ollama GitHub release {tag}");
    }

    public async Task<OllamaInstallResult> DownloadVerifyAndLaunchAsync(
        bool waitForExit,
        CancellationToken cancellationToken = default)
    {
        var release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "CodexGpuThalenHelper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var installerPath = Path.Combine(temporaryDirectory, "OllamaSetup.exe");
        try
        {
            using (var response = await _httpClient.GetAsync(
                release.InstallerUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await DownloadBoundedAsync(response.Content, installerPath, cancellationToken).ConfigureAwait(false);
            }

            var actual = Convert.ToHexString(await SHA256.HashDataAsync(
                File.OpenRead(installerPath),
                cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual),
                Convert.FromHexString(release.ExpectedSha256)))
            {
                return new OllamaInstallResult(false, "OLLAMA_CHECKSUM_MISMATCH", "The official Ollama checksum did not match; the installer was not run.", release.Version, release.Source, false, false);
            }

            if (!AuthenticodeVerifier.Verify(installerPath, "Ollama Inc."))
            {
                return new OllamaInstallResult(false, "OLLAMA_SIGNATURE_INVALID", "Authenticode verification or the expected Ollama Inc. publisher check failed; the installer was not run.", release.Version, release.Source, false, true);
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                WorkingDirectory = temporaryDirectory
            });
            if (process is null)
            {
                return new OllamaInstallResult(false, "OLLAMA_INSTALLER_START_FAILED", "Windows did not launch the verified Ollama installer.", release.Version, release.Source, true, true);
            }

            if (waitForExit)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    return new OllamaInstallResult(false, "OLLAMA_INSTALLER_FAILED", $"The verified Ollama installer exited with code {process.ExitCode}.", release.Version, release.Source, true, true);
                }
            }

            return new OllamaInstallResult(true, "OLLAMA_INSTALLER_LAUNCHED", "The current official Ollama installer passed SHA-256 and Authenticode publisher checks and was launched.", release.Version, release.Source, true, true);
        }
        catch (OllamaInstallerTooLargeException)
        {
            return new OllamaInstallResult(false, "OLLAMA_INSTALLER_TOO_LARGE", "The official Ollama installer response exceeded the bounded download limit and was not run.", release.Version, release.Source, false, false);
        }
        finally
        {
            if (waitForExit && Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static JsonElement FindAsset(JsonElement[] assets, string name)
        => assets.SingleOrDefault(asset => string.Equals(asset.GetProperty("name").GetString(), name, StringComparison.Ordinal)) is var result
            && result.ValueKind != JsonValueKind.Undefined
            ? result
            : throw new InvalidDataException($"The official Ollama release is missing {name}.");

    private static Uri ValidateOfficialAssetUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/ollama/ollama/releases/download/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Ollama release asset URL is not an official versioned GitHub release URL.");
        }

        return uri;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declared && declared > maximumBytes)
        {
            throw new InvalidDataException("The Ollama metadata response exceeded its bounded size.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The Ollama metadata response exceeded its bounded size.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static async Task DownloadBoundedAsync(
        HttpContent content,
        string path,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumInstallerBytes)
        {
            throw new OllamaInstallerTooLargeException();
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > MaximumInstallerBytes)
                {
                    throw new OllamaInstallerTooLargeException();
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await output.DisposeAsync().ConfigureAwait(false);
            File.Delete(path);
            throw;
        }
    }

    private sealed class OllamaInstallerTooLargeException : Exception
    {
    }

    [GeneratedRegex("^v[0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionTagRegex();

    [GeneratedRegex("^([a-fA-F0-9]{64})\\s+[* ]?(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLineRegex();
}

internal static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static bool Verify(string path, string requiredOrganization)
    {
        var fullPath = Path.GetFullPath(path);
        var fileInfo = new WinTrustFileInfo(fullPath);
        var data = new WinTrustData(fileInfo);
        try
        {
            var action = GenericVerifyV2;
            if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0)
            {
                return false;
            }

#pragma warning disable SYSLIB0057 // No X509CertificateLoader API extracts the signer certificate from an Authenticode-signed PE file.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(fullPath));
#pragma warning restore SYSLIB0057
            var decoded = certificate.SubjectName.Decode(X500DistinguishedNameFlags.UseNewLines);
            return decoded.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Any(line => string.Equals(line, "O=" + requiredOrganization, StringComparison.Ordinal));
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            data.Dispose();
            fileInfo.Dispose();
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr windowHandle, ref Guid actionId, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeWinTrustFileInfo
    {
        public uint StructureSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData : IDisposable
    {
        public uint StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;

        public WinTrustData(WinTrustFileInfo fileInfo)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2;
            RevocationChecks = 0;
            UnionChoice = 1;
            FileInfo = fileInfo.Pointer;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00001000;
            UiContext = 0;
        }

        public readonly void Dispose()
        {
        }
    }

    private sealed class WinTrustFileInfo : IDisposable
    {
        private readonly IntPtr _pathPointer;
        public IntPtr Pointer { get; }

        public WinTrustFileInfo(string path)
        {
            _pathPointer = Marshal.StringToCoTaskMemUni(path);
            var native = new NativeWinTrustFileInfo
            {
                StructureSize = (uint)Marshal.SizeOf<NativeWinTrustFileInfo>(),
                FilePath = _pathPointer,
                FileHandle = IntPtr.Zero,
                KnownSubject = IntPtr.Zero
            };
            Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<NativeWinTrustFileInfo>());
            Marshal.StructureToPtr(native, Pointer, false);
        }

        public void Dispose()
        {
            Marshal.FreeCoTaskMem(Pointer);
            Marshal.FreeCoTaskMem(_pathPointer);
        }
    }
}
