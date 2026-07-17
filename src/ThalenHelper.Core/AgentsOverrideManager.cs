using System.Security.Cryptography;
using System.Text;

namespace ThalenHelper.Core;

public sealed record AgentsOverridePreview(
    string Diff,
    bool Changed,
    bool ExistingLocalGpuGuidancePreserved,
    bool ReliabilityBaselineSelected,
    string SourceSha256,
    string PlannedSha256,
    string Action);

public sealed class AgentsOverrideManager
{
    public AgentsOverridePreview PreviewInstall(
        ProductPaths paths,
        HardwareTier tier,
        bool installReliabilityBaseline,
        bool installLocalGpuGuidance = true,
        bool forceManagedLocalGpuGuidance = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var snapshot = ReadSnapshot(paths.AgentsOverrideFile);
        var plan = BuildPlan(
            snapshot.Content,
            tier,
            installReliabilityBaseline,
            installLocalGpuGuidance,
            forceManagedLocalGpuGuidance);
        return new AgentsOverridePreview(
            BuildDiff(snapshot.Content, plan.Merged),
            !string.Equals(snapshot.Content, plan.Merged, StringComparison.Ordinal),
            plan.PreserveExistingLocalGpuGuidance,
            installReliabilityBaseline,
            snapshot.SourceSha256,
            string.Equals(snapshot.Content, plan.Merged, StringComparison.Ordinal)
                ? snapshot.SourceSha256
                : HashPlannedContent(snapshot, plan.Merged),
            plan.PreserveExistingLocalGpuGuidance
                ? "preserved-existing-unmanaged"
                : !string.Equals(snapshot.Content, plan.Merged, StringComparison.Ordinal)
                    ? plan.HadAnyManagedSection ? "updated" : "installed"
                    : "unchanged");
    }

    public ManagedFileResult InstallOrRepair(
        ProductPaths paths,
        HardwareTier tier,
        bool installReliabilityBaseline = false,
        string? expectedSourceSha256 = null,
        string? expectedPlannedSha256 = null,
        Func<string, bool>? postWriteValidator = null,
        bool installLocalGpuGuidance = true,
        bool forceManagedLocalGpuGuidance = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var snapshot = ReadSnapshot(paths.AgentsOverrideFile);
        var plan = BuildPlan(
            snapshot.Content,
            tier,
            installReliabilityBaseline,
            installLocalGpuGuidance,
            forceManagedLocalGpuGuidance);
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
        var protectedSnapshot = new ProtectedFileSnapshot(
            snapshot.Exists,
            snapshot.Bytes,
            snapshot.SourceSha256);
        var backup = snapshot.Exists
            ? ProtectedFileTransaction.CreateBackup(paths.AgentsOverrideFile, protectedSnapshot)
            : null;
        var appliedBytes = EncodeUtf8PreservingPreamble(plan.Merged, snapshot.Bytes);
        var replaced = false;
        try
        {
            ProtectedFileTransaction.ReplaceIfUnchanged(
                paths.AgentsOverrideFile,
                protectedSnapshot,
                appliedBytes);
            replaced = true;
            var written = ProtectedFileTransaction.Capture(paths.AgentsOverrideFile);
            if (!written.Bytes.AsSpan().SequenceEqual(appliedBytes))
            {
                throw new InvalidOperationException("AGENTS.override.md changed immediately after the helper update.");
            }

            ValidateAllMarkers(ProtectedFileTransaction.DecodeUtf8(written));
            if (postWriteValidator is not null && !postWriteValidator(paths.AgentsOverrideFile))
            {
                throw new InvalidOperationException("The managed AGENTS.override.md update failed post-write validation.");
            }

            if (!ProtectedFileTransaction.Matches(
                    paths.AgentsOverrideFile,
                    new ProtectedFileSnapshot(true, appliedBytes, string.Empty)))
            {
                throw new InvalidOperationException("AGENTS.override.md changed during post-write validation.");
            }

            return new ManagedFileResult(
                paths.AgentsOverrideFile,
                !snapshot.Exists,
                true,
                backup,
                plan.HadAnyManagedSection ? "updated" : "installed",
                plan.Warning)
            {
                AppliedBytes = appliedBytes
            };
        }
        catch (Exception exception)
        {
            if (replaced
                && !ProtectedFileTransaction.TryRestoreIfUnchanged(
                    paths.AgentsOverrideFile,
                    appliedBytes,
                    protectedSnapshot))
            {
                throw new IOException(
                    "AGENTS.override.md changed after the helper update. The newer bytes were preserved; use the timestamped backup and current-file diff for manual review.",
                    exception);
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
        var snapshot = ProtectedFileTransaction.Capture(paths.AgentsOverrideFile);
        if (!snapshot.Exists)
        {
            return new ManagedFileResult(paths.AgentsOverrideFile, false, false, null, "not-present");
        }

        var originalBytes = snapshot.Bytes;
        var original = ProtectedFileTransaction.DecodeUtf8(snapshot);
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

        var backup = ProtectedFileTransaction.CreateBackup(paths.AgentsOverrideFile, snapshot);
        byte[]? appliedBytes = null;
        var replaced = false;
        try
        {
            if (TryGetExactOriginalBytes(
                    paths.AgentsOverrideFile,
                    originalBackupPath,
                    originalBytes,
                    original,
                    out var exactOriginalBytes))
            {
                appliedBytes = exactOriginalBytes;
                ProtectedFileTransaction.ReplaceIfUnchanged(paths.AgentsOverrideFile, snapshot, appliedBytes);
                replaced = true;
                return new ManagedFileResult(
                    paths.AgentsOverrideFile,
                    false,
                    true,
                    backup,
                    "restored-exact-original")
                {
                    AppliedBytes = appliedBytes
                };
            }

            if (fileWasCreatedByProduct
                && string.IsNullOrWhiteSpace(updated)
                && IsExactProductManagedRepresentation(originalBytes, original, string.Empty))
            {
                ProtectedFileTransaction.DeleteIfUnchanged(paths.AgentsOverrideFile, snapshot);
                return new ManagedFileResult(paths.AgentsOverrideFile, false, true, backup, "removed-product-file");
            }

            appliedBytes = EncodeUtf8PreservingPreamble(updated, originalBytes);
            ProtectedFileTransaction.ReplaceIfUnchanged(paths.AgentsOverrideFile, snapshot, appliedBytes);
            replaced = true;
            return new ManagedFileResult(
                paths.AgentsOverrideFile,
                false,
                true,
                backup,
                fileWasCreatedByProduct ? "preserved-user-content" : "removed-managed-sections")
            {
                AppliedBytes = appliedBytes
            };
        }
        catch (Exception exception)
        {
            if (replaced
                && appliedBytes is not null
                && !ProtectedFileTransaction.TryRestoreIfUnchanged(
                    paths.AgentsOverrideFile,
                    appliedBytes,
                    snapshot))
            {
                throw new IOException(
                    "AGENTS.override.md changed during uninstall. The newer bytes were preserved for manual review.",
                    exception);
            }

            throw;
        }
    }

    public void Rollback(ManagedFileResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Changed)
        {
            return;
        }

        if (result.AppliedBytes is null)
        {
            throw new InvalidOperationException("The exact helper-produced AGENTS.override.md bytes required for guarded rollback are unavailable.");
        }

        ProtectedFileSnapshot original;
        if (result.Created)
        {
            original = new ProtectedFileSnapshot(false, [], "MISSING");
        }
        else if (string.IsNullOrWhiteSpace(result.BackupPath) || !File.Exists(result.BackupPath))
        {
            throw new InvalidOperationException("The exact AGENTS.override.md backup required for rollback is unavailable.");
        }
        else
        {
            original = ProtectedFileTransaction.Capture(result.BackupPath);
        }

        if (!ProtectedFileTransaction.TryRestoreIfUnchanged(result.Path, result.AppliedBytes, original))
        {
            throw new InvalidOperationException(
                "AGENTS.override.md changed after the helper update. Rollback left the newer bytes untouched; review the current file and timestamped backup manually.");
        }
    }

    public static bool HasManagedLocalGpuSection(string content)
        => content.Contains(ProductInfo.ManagedAgentsStart, StringComparison.Ordinal)
            && content.Contains(ProductInfo.ManagedAgentsEnd, StringComparison.Ordinal);

    public static bool HasManagedReliabilitySection(string content)
        => content.Contains(ProductInfo.ManagedReliabilityStart, StringComparison.Ordinal)
            && content.Contains(ProductInfo.ManagedReliabilityEnd, StringComparison.Ordinal);

    private static MergePlan BuildPlan(
        string original,
        HardwareTier tier,
        bool installReliabilityBaseline,
        bool installLocalGpuGuidance,
        bool forceManagedLocalGpuGuidance)
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
        var preserveExisting = installLocalGpuGuidance
            && !hadLocalGpu
            && !forceManagedLocalGpuGuidance
            && baseContent.Contains(ProductInfo.IntegrationName, StringComparison.OrdinalIgnoreCase);
        var resemblesPackagedGuidance = baseContent.Contains("## Optional local GPU reviewer", StringComparison.OrdinalIgnoreCase)
            && baseContent.Contains("local_gpu_health", StringComparison.OrdinalIgnoreCase)
            && baseContent.Contains("local_gpu_plan", StringComparison.OrdinalIgnoreCase)
            && baseContent.Contains("local_gpu_review", StringComparison.OrdinalIgnoreCase);
        if (installLocalGpuGuidance
            && forceManagedLocalGpuGuidance
            && !hadLocalGpu
            && resemblesPackagedGuidance)
        {
            throw new InvalidOperationException(
                "Existing unmarked local GPU guidance materially resembles the packaged managed section. The file was preserved to avoid duplicating instructions; review and consolidate that section before migration.");
        }
        var merged = original;
        var preserveOriginalPrefix = installLocalGpuGuidance
            && forceManagedLocalGpuGuidance
            && !hadLocalGpu
            && !hadReliability;
        var localSection = ExtractManagedSection(
            localTemplate,
            ProductInfo.ManagedAgentsStart,
            ProductInfo.ManagedAgentsEnd);
        if (hadLocalGpu)
        {
            merged = installLocalGpuGuidance
                ? ReplaceManagedSectionInPlace(
                    merged,
                    ProductInfo.ManagedAgentsStart,
                    ProductInfo.ManagedAgentsEnd,
                    localSection)
                : RemoveManagedSectionAndSeparator(
                    merged,
                    ProductInfo.ManagedAgentsStart,
                    ProductInfo.ManagedAgentsEnd);
        }
        else if (installLocalGpuGuidance && !preserveExisting)
        {
            merged = preserveOriginalPrefix
                ? AppendManagedSectionPreservingPrefix(merged, localSection)
                : AppendManagedSection(merged, localSection);
        }

        var reliabilitySection = ExtractManagedSection(
            reliabilityTemplate,
            ProductInfo.ManagedReliabilityStart,
            ProductInfo.ManagedReliabilityEnd);
        if (hadReliability)
        {
            merged = installReliabilityBaseline
                ? ReplaceManagedSectionInPlace(
                    merged,
                    ProductInfo.ManagedReliabilityStart,
                    ProductInfo.ManagedReliabilityEnd,
                    reliabilitySection)
                : RemoveManagedSectionAndSeparator(
                    merged,
                    ProductInfo.ManagedReliabilityStart,
                    ProductInfo.ManagedReliabilityEnd);
        }
        else if (installReliabilityBaseline)
        {
            merged = AppendManagedSection(merged, reliabilitySection);
        }

        var preserveExactBytes = !installReliabilityBaseline
            && !hadLocalGpu
            && !hadReliability
            && (preserveExisting || !installLocalGpuGuidance);
        if (preserveExactBytes)
        {
            merged = original;
        }

        if (preserveExactBytes || preserveOriginalPrefix || hadLocalGpu || hadReliability)
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

    private static string AppendManagedSectionPreservingPrefix(string content, string managed)
    {
        if (content.Length == 0)
        {
            return managed + Environment.NewLine;
        }

        var separator = content.EndsWith("\r\n", StringComparison.Ordinal)
            || content.EndsWith('\n')
            || content.EndsWith('\r')
                ? Environment.NewLine
                : Environment.NewLine + Environment.NewLine;
        return content + separator + managed + Environment.NewLine;
    }

    private static string ReplaceManagedSectionInPlace(
        string content,
        string startMarker,
        string endMarker,
        string replacement)
    {
        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        var end = content.IndexOf(endMarker, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            throw new InvalidDataException("The managed AGENTS.override.md markers are malformed.");
        }

        var blockEnd = end + endMarker.Length;
        return content[..start] + replacement + content[blockEnd..];
    }

    private static string RemoveManagedSectionAndSeparator(
        string content,
        string startMarker,
        string endMarker)
    {
        var updated = RemoveManagedSection(content, startMarker, endMarker, out var removed);
        if (!removed)
        {
            return updated;
        }

        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        if (start >= 4 && content.AsSpan(start - 4, 4).SequenceEqual("\r\n\r\n"))
        {
            return updated.Remove(start - 2, 2);
        }

        if (start >= 2 && content.AsSpan(start - 2, 2).SequenceEqual("\n\n"))
        {
            return updated.Remove(start - 1, 1);
        }

        if (start >= 2 && content.AsSpan(start - 2, 2).SequenceEqual("\r\r"))
        {
            return updated.Remove(start - 1, 1);
        }

        return updated;
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
        builder.AppendLine("@@ changed hunk preview; existing content is not logged or packaged @@");
        var beforeLines = before.ReplaceLineEndings("\n").Split('\n');
        var afterLines = after.ReplaceLineEndings("\n").Split('\n');
        var prefix = 0;
        while (prefix < beforeLines.Length
            && prefix < afterLines.Length
            && string.Equals(beforeLines[prefix], afterLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < beforeLines.Length - prefix
            && suffix < afterLines.Length - prefix
            && string.Equals(
                beforeLines[^(suffix + 1)],
                afterLines[^(suffix + 1)],
                StringComparison.Ordinal))
        {
            suffix++;
        }

        if (prefix == beforeLines.Length && prefix == afterLines.Length)
        {
            return builder.AppendLine("@@ no changes @@").ToString();
        }

        var contextStart = Math.Max(0, prefix - 3);
        var beforeChangeEnd = beforeLines.Length - suffix;
        var afterChangeEnd = afterLines.Length - suffix;
        var contextCount = Math.Min(3, suffix);
        builder.Append("@@ -").Append(contextStart + 1).Append(',').Append(beforeChangeEnd - contextStart + contextCount)
            .Append(" +").Append(contextStart + 1).Append(',').Append(afterChangeEnd - contextStart + contextCount)
            .AppendLine(" @@");
        for (var index = contextStart; index < prefix; index++)
        {
            builder.Append(' ').AppendLine(beforeLines[index]);
        }

        for (var index = prefix; index < beforeLines.Length - suffix; index++)
        {
            builder.Append('-').AppendLine(beforeLines[index]);
        }

        for (var index = prefix; index < afterLines.Length - suffix; index++)
        {
            builder.Append('+').AppendLine(afterLines[index]);
        }

        for (var index = beforeLines.Length - suffix; index < beforeLines.Length - suffix + contextCount; index++)
        {
            builder.Append(' ').AppendLine(beforeLines[index]);
        }

        return builder.ToString();
    }

    private static FileSnapshot ReadSnapshot(string path)
    {
        var snapshot = ProtectedFileTransaction.Capture(path);
        return new FileSnapshot(
            snapshot.Exists,
            snapshot.Bytes,
            snapshot.Exists ? ProtectedFileTransaction.DecodeUtf8(snapshot) : string.Empty,
            snapshot.SourceSha256);
    }

    private static string HashPlannedContent(FileSnapshot snapshot, string content)
        => Convert.ToHexString(SHA256.HashData(
            EncodeUtf8PreservingPreamble(content, snapshot.Bytes)));

    private static void ValidatePreviewBinding(
        FileSnapshot snapshot,
        string planned,
        string? expectedSourceSha256,
        string? expectedPlannedSha256)
    {
        if (expectedSourceSha256 is not null
            && !string.Equals(expectedSourceSha256, snapshot.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "AGENTS.override.md changed after the before/after preview. Review a fresh diff before applying the optional baseline choice.");
        }

        var plannedSha256 = string.Equals(snapshot.Content, planned, StringComparison.Ordinal)
            ? snapshot.SourceSha256
            : HashPlannedContent(snapshot, planned);
        if (expectedPlannedSha256 is not null
            && !string.Equals(expectedPlannedSha256, plannedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The managed AGENTS.override.md plan no longer matches the reviewed diff. Review a fresh diff before applying it.");
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

        var backupSnapshot = ProtectedFileTransaction.Capture(originalBackupPath!);
        if (!backupSnapshot.Exists)
        {
            return false;
        }

        var original = ProtectedFileTransaction.DecodeUtf8(backupSnapshot);
        if (currentBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            != backupSnapshot.Bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return false;
        }
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

        exactOriginalBytes = backupSnapshot.Bytes;
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
        var expectedBytes = EncodeUtf8PreservingPreamble(expected, currentBytes);
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

    private sealed record MergePlan(
        string Merged,
        bool PreserveExistingLocalGpuGuidance,
        bool HadAnyManagedSection,
        string? Warning);

    private sealed record FileSnapshot(bool Exists, byte[] Bytes, string Content, string SourceSha256);
}
