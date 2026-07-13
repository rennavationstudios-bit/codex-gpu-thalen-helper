using System.Net;
using System.Security.Cryptography;
using System.Text;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class UpdateTests
{
    [Fact]
    public async Task UpdateRequiresExactRepositoryAssetsAndVerifiesSha256()
    {
        var installer = Encoding.UTF8.GetBytes("test installer bytes");
        var hash = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant();
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/repos/rennavationstudios-bit/codex-gpu-thalen-helper/releases" => FakeHttpMessageHandler.Json(ReleaseJson("v9.9.9")),
            "/installer.exe" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) },
            "/checksums.txt" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {ProjectUpdateService.InstallerAssetName}\n") },
            _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
        }));
        using var service = new ProjectUpdateService(new HttpClient(handler));

        var update = await service.CheckAsync();
        var result = await service.DownloadVerifyAndLaunchAsync(update, launch: false);

        Assert.True(update.UpdateAvailable);
        Assert.Equal("9.9.9", update.LatestVersion);
        Assert.True(result.Success);
        Assert.Equal(hash, result.Sha256);
        Assert.True(File.Exists(result.InstallerPath));
        Assert.Contains(handler.Requests, item => item.Path.Contains("rennavationstudios-bit/codex-gpu-thalen-helper", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateDeletesInstallerOnChecksumMismatch()
    {
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/repos/rennavationstudios-bit/codex-gpu-thalen-helper/releases" => FakeHttpMessageHandler.Json(ReleaseJson("v9.9.9")),
            "/installer.exe" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) },
            "/checksums.txt" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{new string('0', 64)}  {ProjectUpdateService.InstallerAssetName}\n") },
            _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
        }));
        using var service = new ProjectUpdateService(new HttpClient(handler));

        var update = await service.CheckAsync();
        var result = await service.DownloadVerifyAndLaunchAsync(update, launch: false);

        Assert.False(result.Success);
        Assert.Equal("UPDATE_CHECKSUM_MISMATCH", result.Code);
        Assert.Null(result.InstallerPath);
    }

    [Fact]
    public async Task OllamaInstallerRejectsOversizedResponseBeforeVerificationOrLaunch()
    {
        var oversized = new ByteArrayContent([]);
        oversized.Headers.ContentLength = 2L * 1024 * 1024 * 1024 + 1;
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/repos/ollama/ollama/releases/latest" => FakeHttpMessageHandler.Json("""
                {
                  "tag_name": "v0.12.0",
                  "assets": [
                    { "name": "OllamaSetup.exe", "browser_download_url": "https://github.com/ollama/ollama/releases/download/v0.12.0/OllamaSetup.exe" },
                    { "name": "sha256sum.txt", "browser_download_url": "https://github.com/ollama/ollama/releases/download/v0.12.0/sha256sum.txt" }
                  ]
                }
                """),
            "/ollama/ollama/releases/download/v0.12.0/sha256sum.txt" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{new string('0', 64)}  OllamaSetup.exe\n")
            },
            "/ollama/ollama/releases/download/v0.12.0/OllamaSetup.exe" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = oversized
            },
            _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)
        }));
        using var service = new OllamaInstallerService(new HttpClient(handler));

        var result = await service.DownloadVerifyAndLaunchAsync(waitForExit: false);

        Assert.False(result.Success);
        Assert.Equal("OLLAMA_INSTALLER_TOO_LARGE", result.Code);
        Assert.False(result.SignatureVerified);
        Assert.False(result.ChecksumVerified);
    }

    private static string ReleaseJson(string tag) => $$"""
        [{
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/rennavationstudios-bit/codex-gpu-thalen-helper/releases/tag/{{tag}}",
          "draft": false,
          "prerelease": true,
          "assets": [
            { "name": "Codex-GPU-Thalen-Helper-Setup.exe", "browser_download_url": "https://github.com/installer.exe" },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/checksums.txt" }
          ]
        }]
        """;
}
