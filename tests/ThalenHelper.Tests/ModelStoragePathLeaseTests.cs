using System.Diagnostics;
using ThalenHelper.Core;

namespace ThalenHelper.Tests;

public sealed class ModelStoragePathLeaseTests
{
    [Fact]
    public void CandidateRejectsAJunctionComponent()
    {
        using var temporary = new TemporaryDirectory();
        var target = Path.Combine(temporary.Path, "actual-volume-directory");
        var junction = Path.Combine(temporary.Path, "redirected-models");
        Directory.CreateDirectory(target);
        CreateJunction(junction, target);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                ModelStoragePathLease.ValidateCandidate(Path.Combine(junction, "ollama")));

            Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void CandidateRejectsADirectorySymbolicLinkWhenWindowsAllowsCreatingOne()
    {
        using var temporary = new TemporaryDirectory();
        var target = Path.Combine(temporary.Path, "symbolic-target");
        var link = Path.Combine(temporary.Path, "symbolic-models");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (UnauthorizedAccessException)
        {
            // Junction coverage above exercises the same reparse-point rejection on
            // Windows hosts where symbolic-link creation is not enabled.
            return;
        }

        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                ModelStoragePathLease.ValidateCandidate(Path.Combine(link, "ollama")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void LeaseBlocksCreatedDirectoryRenameUntilDisposed()
    {
        using var temporary = new TemporaryDirectory();
        var models = Path.Combine(temporary.Path, "models", "ollama");
        var moved = Path.Combine(temporary.Path, "models", "moved");

        using (var guarded = ModelStoragePathLease.AcquireOrCreate(models))
        {
            Assert.Throws<IOException>(() => Directory.Move(models, moved));
            Assert.True(Directory.Exists(models));
            Assert.False(Directory.Exists(moved));
            guarded.ValidateUnchanged();
        }

        Directory.Move(models, moved);
        using var lease = ModelStoragePathLease.AcquireExisting(moved);
        lease.ValidateUnchanged();
    }

    [Fact]
    public void LeaseBlocksCreatedDirectoryReplacementUntilDisposed()
    {
        using var temporary = new TemporaryDirectory();
        var models = Path.Combine(temporary.Path, "models", "ollama");
        var replacement = Path.Combine(temporary.Path, "replacement");
        Directory.CreateDirectory(replacement);

        using (var guarded = ModelStoragePathLease.AcquireOrCreate(models))
        {
            Assert.Throws<IOException>(() => Directory.Delete(models));
            Assert.True(Directory.Exists(models));
            Assert.True(Directory.Exists(replacement));
            guarded.ValidateUnchanged();
        }

        Directory.Delete(models);
        Directory.Move(replacement, models);
        Assert.True(Directory.Exists(models));
        Assert.False(Directory.Exists(replacement));
    }

    [Fact]
    public void LeaseAllowsIoAndNamespaceChangesBelowTheGuardedDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var models = Path.Combine(temporary.Path, "models", "ollama");
        var staging = Path.Combine(models, "staging");
        var ready = Path.Combine(models, "ready");
        var manifest = Path.Combine(staging, "manifest.txt");

        using var guarded = ModelStoragePathLease.AcquireOrCreate(models);
        Directory.CreateDirectory(staging);
        File.WriteAllText(manifest, "model-ready");
        Assert.Equal("model-ready", File.ReadAllText(manifest));

        Directory.Move(staging, ready);
        File.Delete(Path.Combine(ready, "manifest.txt"));
        Directory.Delete(ready);

        guarded.ValidateUnchanged();
    }

    private static void CreateJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "/d",
                "/c",
                "mklink",
                "/J",
                junction,
                target
            }
        }) ?? throw new InvalidOperationException("The junction fixture process could not start.");
        process.WaitForExit(10_000);
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, $"Junction fixture creation failed: {error}");
    }
}
