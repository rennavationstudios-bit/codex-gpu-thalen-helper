using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ThalenHelper.Core;

public sealed record AgentsOverridePreview(
    string Diff,
    bool Changed,
    bool ExistingLocalGpuGuidancePreserved,
    bool ReliabilityBaselineSelected,
    string SourceSha256,
    string PlannedSha256);

public sealed class AgentsOverrideManager
{
    public AgentsOverridePreview PreviewInstall(
        ProductPaths paths,
        HardwareTier tier,
        bool installReliabilityBaseline)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var snapshot = ReadSnapshot(paths.AgentsOverrideFile);
        var plan = BuildPlan(snapshot.Content, tier, installReliabilityBaseline);
        return new AgentsOverridePreview(
            BuildDiff(snapshot.Content, plan.Merged),
            !string.Equals(snapshot.Content, plan.Merged, StringComparison.Ordinal),
            plan.PreserveExistingLocalGpuGuidance,
            installReliabilityBaseline,
            snapshot.SourceSha256,
            HashPlannedContent(plan.Merged));
    }

    public ManagedFileResult InstallOrRepair(
        ProductPaths paths,
        HardwareTier tier,
        bool installReliabilityBaseline = false,
        string? expectedSourceSha256 = null,
        string? expectedPlannedSha256 = null,
        Func<string, bool>? postWriteValidator = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var snapshot = ReadSnapshot(paths.AgentsOverrideFile);
        var plan = BuildPlan(snapshot.Content, tier, installReliabilityBaseline);
        ValidatePreviewBinding(snapshot, plan.Merged, expectedSourceSha256, expectedPlannedSha256);
        if (string.Equals(snapshot.Content, plan.Merged, StringComparison.Ordinal))
        {
            return new ManagedFileResult(
                paths.AgentsOverrideFile,
                false,
                false,
                null,
                plan.PreserveExistingLocalGpuGuidance ? "preserved-existing-unmanaged" : "unchanged",
                plan.Warning);
        }

        Directory.CreateDirectory(paths.CodexHome);
        AssertSnapshotUnchanged(paths.AgentsOverrideFile, snapshot.SourceSha256);
        var backup = snapshot.Exists ? CreateBackup(paths.AgentsOverrideFile) : null;
        AssertSnapshotUnchanged(paths.AgentsOverrideFile, snapshot.SourceSha256);
        var replaced = false;
        try
        {
            WriteAtomic(paths.AgentsOverrideFile, plan.Merged);
            replaced = true;
            ValidateAllMarkers(File.ReadAllText(paths.AgentsOverrideFile, Encoding.UTF8));
            if (postWriteValidator is not null && !postWriteValidator(paths.AgentsOverrideFile))
            {
                throw new InvalidOperationException("The managed AGENTS.override.md update failed post-write validation.");
            }

            return new ManagedFileResult(
                paths.AgentsOverrideFile,
                !snapshot.Exists,
                true,
                backup,
                plan.HadAnyManagedSection ? "updated" : "installed",
                plan.Warning);
        }
        catch
        {
            if (replaced)
            {
                RestoreOriginal(paths.AgentsOverrideFile, snapshot.Exists, snapshot.Bytes);
            }

            throw;
        }
    }

    public ManagedFileResult Uninstall(
        ProductPaths paths,
        bool fileWasCreatedByProduct,
        string? originalBackupPath = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.AgentsOverrideFile))
        {
            return new ManagedFileResult(paths.AgentsOverrideFile, false, false, null, "not-present");
        }

        var originalBytes = File.ReadAllBytes(paths.AgentsOverrideFile);
        var original = File.ReadAllText(paths.AgentsOverrideFile, Encoding.UTF8);
        ValidateAllMarkers(original);
        var updated = RemoveManagedSection(
            original,
            ProductInfo.ManagedAgentsStart,
            ProductInfo.ManagedAgentsEnd,
            out var removedLocalGpu);
        updated = RemoveManagedSection(
            updated,
            ProductInfo.ManagedReliabilityStart,
            ProductInfo.ManagedReliabilityEnd,
            out var removedReliability);
        if (!removedLocalGpu && !removedReliability)
        {
            return new ManagedFileResult(paths.AgentsOverrideFile, false, false, null, "not-managed");
        }

        var backup = CreateBackup(paths.AgentsOverrideFile);
        try
        {
            if (TryGetExactOriginalBytes(
                    paths.AgentsOverrideFile,
                    originalBackupPath,
                    originalBytes,
                    original,
                    out var exactOriginalBytes))
            {
                WriteAtomic(paths.AgentsOverrideFile, exactOriginalBytes);
                return new ManagedFileResult(
                    paths.AgentsOverrideFile,
                    false,
                    true,
                    backup,
                    "restored-exact-original");
            }

            if (fileWasCreatedByProduct
                && string.IsNullOrWhiteSpace(updated)
                && IsExactProductManagedRepresentation(originalBytes, original, string.Empty))
            {
                File.Delete(paths.AgentsOverrideFile);
                return new ManagedFileResult(paths.AgentsOverrideFile, false, true, backup, "removed-product-file");
            }

            WriteAtomic(
                paths.AgentsOverrideFile,
                EncodeUtf8PreservingPreamble(updated, originalBytes));
            return new ManagedFileResult(
                paths.AgentsOverrideFile,
                false,
                true,
                backup,
                fileWasCreatedByProduct ? "preserved-user-content" : "removed-managed-sections");
        }
        catch
        {
            WriteAtomic(paths.AgentsOverrideFile, originalBytes);
            throw;
        }
    }

    public static bool HasManagedLocalGpuSection(string content)
        => content.Contains(ProductInfo.ManagedAgentsStart, StringComparison.Ordinal)
            && content.Contains(ProductInfo.ManagedAgentsEnd, StringComparison.Ordinal);

    public static bool HasManagedReliabilitySection(string content)
        => content.Contains(ProductInfo.ManagedReliabilityStart, StringComparison.Ordinal)
            && content.Contains(ProductInfo.ManagedReliabilityEnd, StringComparison.Ordinal);

    private static MergePlan BuildPlan(string original, HardwareTier tier, bool installReliabilityBaseline)
    {
        ValidateAllMarkers(original);
        var localTemplate = ReadTemplate("AGENTS.local-gpu-reviewer.md")
            .Replace("{{TIER_GUIDANCE}}", GetTierGuidance(tier), StringComparison.Ordinal);
        var reliabilityTemplate = ReadTemplate("AGENTS.reliability-baseline.md");
        ValidateAllMarkers(localTemplate);
        ValidateAllMarkers(reliabilityTemplate);

        var baseContent = RemoveManagedSection(
            original,
            ProductInfo.ManagedAgentsStart,
            ProductInfo.ManagedAgentsEnd,
            out var hadLocalGpu);
        baseContent = RemoveManagedSection(
            baseContent,
            ProductInfo.ManagedReliabilityStart,
            ProductInfo.ManagedReliabilityEnd,
            out var hadReliability);
        var preserveExisting = !hadLocalGpu
            && baseContent.Contains(ProductInfo.IntegrationName, StringComparison.OrdinalIgnoreCase);
        var merged = baseContent;
        if (!preserveExisting)
        {
            merged = AppendManagedSection(merged, ExtractManagedSection(
                localTemplate,
                ProductInfo.ManagedAgentsStart,
                ProductInfo.ManagedAgentsEnd));
        }

        if (installReliabilityBaseline)
        {
            merged = AppendManagedSection(merged, ExtractManagedSection(
                reliabilityTemplate,
                ProductInfo.ManagedReliabilityStart,
                ProductInfo.ManagedReliabilityEnd));
        }

        var preserveExactBytes = preserveExisting && !installReliabilityBaseline && !hadLocalGpu && !hadReliability;
        if (preserveExactBytes)
        {
            merged = original;
        }

        if (preserveExactBytes)
        {
            // User-owned guidance remains exactly as read; no newline or encoding normalization is allowed.
        }
        else if (string.IsNullOrWhiteSpace(merged))
        {
            merged = string.Empty;
        }
        else
        {
            merged = merged.TrimEnd() + Environment.NewLine;
        }

        return new MergePlan(
            merged,
            preserveExisting,
            hadLocalGpu || hadReliability,
            preserveExisting
                ? "Existing unmarked local_gpu_reviewer guidance was kept user-owned; no duplicate managed local GPU section was added."
                : null);
    }

    private static string ReadTemplate(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "templates", fileName), Encoding.UTF8)
            .ReplaceLineEndings(Environment.NewLine);

    private static string GetTierGuidance(HardwareTier tier)
    {
        return tier switch
        {
            HardwareTier.Entry => "Entry-tier models are limited to repeated-pattern inspection, log grouping, error categorization, checklist comparison, obvious code smells, and simple fixture or edge-case suggestions.",
            HardwareTier.Mid => "Mid-tier models may also assist with bounded diff review, test-failure analysis, repository mapping, and additional debugging hypotheses.",
            HardwareTier.High or HardwareTier.Enthusiast => "Strong local models may assist with broader bounded read-only review, but their conclusions still require independent verification by the primary Codex agent.",
            _ => "No local model is currently recommended. Keep the reviewer disabled until hardware and runtime validation succeed."
        };
    }

    private static string ExtractManagedSection(string content, string startMarker, string endMarker)
    {
        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        var end = content.IndexOf(endMarker, StringComparison.Ordinal);
        return content[start..(end + endMarker.Length)];
    }

    private static string AppendManagedSection(string content, string managed)
    {
        var trimmed = content.TrimEnd();
        return string.IsNullOrEmpty(trimmed)
            ? managed + Environment.NewLine
            : trimmed + Environment.NewLine + Environment.NewLine + managed + Environment.NewLine;
    }

    private static string RemoveManagedSection(
        string content,
        string startMarker,
        string endMarker,
        out bool removed)
    {
        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        var end = content.IndexOf(endMarker, StringComparison.Ordinal);
        if (start < 0 && end < 0)
        {
            removed = false;
            return content;
        }

        if (start < 0 || end < start)
        {
            throw new InvalidDataException("The managed AGENTS.override.md markers are malformed.");
        }

        if (content.IndexOf(startMarker, start + startMarker.Length, StringComparison.Ordinal) >= 0
            || content.IndexOf(endMarker, end + endMarker.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidDataException("Multiple managed AGENTS.override.md sections were found.");
        }

        var removeEnd = ConsumeSingleLineEnding(content, end + endMarker.Length);
        removed = true;
        return content[..start] + content[removeEnd..];
    }

    private static void ValidateAllMarkers(string content)
    {
        ValidateMarkerPair(content, ProductInfo.ManagedAgentsStart, ProductInfo.ManagedAgentsEnd);
        ValidateMarkerPair(content, ProductInfo.ManagedReliabilityStart, ProductInfo.ManagedReliabilityEnd);
    }

    private static void ValidateMarkerPair(string content, string startMarker, string endMarker)
    {
        var starts = Count(content, startMarker);
        var ends = Count(content, endMarker);
        if (starts != ends || starts > 1)
        {
            throw new InvalidDataException("Invalid or duplicate managed AGENTS.override.md markers.");
        }
    }

    private static int Count(string content, string value)
    {
        var count = 0;
        for (var index = 0; (index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
        {
            count++;
        }

        return count;
    }

    private static string BuildDiff(string before, string after)
    {
        var builder = new StringBuilder();
        builder.AppendLine("--- AGENTS.override.md (before)");
        builder.AppendLine("+++ AGENTS.override.md (after)");
        builder.AppendLine("@@ full-file preview; existing content is not logged or packaged @@");
        foreach (var line in before.ReplaceLineEndings("\n").Split('\n'))
        {
            builder.Append('-').AppendLine(line);
        }

        foreach (var line in after.ReplaceLineEndings("\n").Split('\n'))
        {
            builder.Append('+').AppendLine(line);
        }

        return builder.ToString();
    }

    private static string CreateBackup(string path)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var backup = $"{path}.thalen-helper.{timestamp}.bak";
        File.Copy(path, backup, false);
        return backup;
    }

    private static FileSnapshot ReadSnapshot(string path)
    {
        if (!File.Exists(path))
        {
            return new FileSnapshot(false, [], string.Empty, "MISSING");
        }

        var bytes = File.ReadAllBytes(path);
        return new FileSnapshot(
            true,
            bytes,
            File.ReadAllText(path, Encoding.UTF8),
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string HashPlannedContent(string content)
        => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(content)));

    private static void ValidatePreviewBinding(
        FileSnapshot snapshot,
        string planned,
        string? expectedSourceSha256,
        string? expectedPlannedSha256)
    {
        if (expectedSourceSha256 is not null
            && !string.Equals(expectedSourceSha256, snapshot.SourceSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "AGENTS.override.md changed after the before/after preview. Review a fresh diff before applying the optional baseline choice.");
        }

        var plannedSha256 = HashPlannedContent(planned);
        if (expectedPlannedSha256 is not null
            && !string.Equals(expectedPlannedSha256, plannedSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The managed AGENTS.override.md plan no longer matches the reviewed diff. Review a fresh diff before applying it.");
        }
    }

    private static void AssertSnapshotUnchanged(string path, string expectedSourceSha256)
    {
        var current = ReadSnapshot(path).SourceSha256;
        if (!string.Equals(current, expectedSourceSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "AGENTS.override.md changed while the managed update was being prepared. No update was applied; review a fresh diff.");
        }
    }

    private static void RestoreOriginal(string path, bool existed, byte[] bytes)
    {
        if (existed)
        {
            WriteAtomic(path, bytes);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static bool TryGetExactOriginalBytes(
        string target,
        string? originalBackupPath,
        byte[] currentBytes,
        string current,
        out byte[] exactOriginalBytes)
    {
        exactOriginalBytes = [];
        if (!IsManagedBackupForTarget(target, originalBackupPath))
        {
            return false;
        }

        var original = File.ReadAllText(originalBackupPath!, Encoding.UTF8);
        if (original.Contains(ProductInfo.ManagedAgentsStart, StringComparison.Ordinal)
            || original.Contains(ProductInfo.ManagedAgentsEnd, StringComparison.Ordinal)
            || original.Contains(ProductInfo.ManagedReliabilityStart, StringComparison.Ordinal)
            || original.Contains(ProductInfo.ManagedReliabilityEnd, StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsExactProductManagedRepresentation(currentBytes, current, original))
        {
            return false;
        }

        exactOriginalBytes = File.ReadAllBytes(originalBackupPath!);
        return true;
    }

    private static bool IsExactProductManagedRepresentation(
        byte[] currentBytes,
        string current,
        string baseContent)
    {
        var expected = baseContent;
        var found = false;
        if (HasManagedLocalGpuSection(current))
        {
            expected = AppendManagedSection(expected, ExtractManagedSection(
                current,
                ProductInfo.ManagedAgentsStart,
                ProductInfo.ManagedAgentsEnd));
            found = true;
        }

        if (HasManagedReliabilitySection(current))
        {
            expected = AppendManagedSection(expected, ExtractManagedSection(
                current,
                ProductInfo.ManagedReliabilityStart,
                ProductInfo.ManagedReliabilityEnd));
            found = true;
        }

        if (!found)
        {
            return false;
        }

        expected = string.IsNullOrWhiteSpace(expected)
            ? string.Empty
            : expected.TrimEnd() + Environment.NewLine;
        var expectedBytes = new UTF8Encoding(false).GetBytes(expected);
        return currentBytes.AsSpan().SequenceEqual(expectedBytes);
    }

    private static int ConsumeSingleLineEnding(string content, int index)
    {
        if (index >= content.Length)
        {
            return index;
        }

        if (content[index] == '\r')
        {
            index++;
            if (index < content.Length && content[index] == '\n')
            {
                index++;
            }

            return index;
        }

        return content[index] == '\n' ? index + 1 : index;
    }

    private static byte[] EncodeUtf8PreservingPreamble(string content, byte[] originalBytes)
    {
        var payload = new UTF8Encoding(false).GetBytes(content);
        var preamble = Encoding.UTF8.GetPreamble();
        if (!originalBytes.AsSpan().StartsWith(preamble))
        {
            return payload;
        }

        var result = new byte[preamble.Length + payload.Length];
        preamble.CopyTo(result, 0);
        payload.CopyTo(result, preamble.Length);
        return result;
    }

    private static bool IsManagedBackupForTarget(string target, string? backup)
    {
        if (string.IsNullOrWhiteSpace(backup) || !File.Exists(backup))
        {
            return false;
        }

        var targetFull = Path.GetFullPath(target);
        var backupFull = Path.GetFullPath(backup);
        return string.Equals(
                Path.GetDirectoryName(targetFull),
                Path.GetDirectoryName(backupFull),
                StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(backupFull).StartsWith(
                Path.GetFileName(targetFull) + ".thalen-helper.",
                StringComparison.OrdinalIgnoreCase)
            && backupFull.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteAtomic(string path, string content)
        => WriteAtomic(path, new UTF8Encoding(false).GetBytes(content));

    private static void WriteAtomic(string path, byte[] content)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, true);
    }

    private sealed record MergePlan(
        string Merged,
        bool PreserveExistingLocalGpuGuidance,
        bool HadAnyManagedSection,
        string? Warning);

    private sealed record FileSnapshot(bool Exists, byte[] Bytes, string Content, string SourceSha256);
}
