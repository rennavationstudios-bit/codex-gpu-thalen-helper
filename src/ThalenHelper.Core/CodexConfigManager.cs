using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace ThalenHelper.Core;

internal sealed record ExistingIntegrationInspection(
    bool ShouldPreserve,
    ProtectedFileSnapshot Snapshot,
    string Content);

public enum CodexIntegrationOwnership
{
    NotConfigured,
    ExternalUnmarked,
    ManagedValid,
    ManagedDrift,
    Invalid
}

public sealed record CodexConfigPreview(
    string Diff,
    bool Changed,
    bool ExistingIntegrationPreserved,
    bool ExistingIntegrationMigrated,
    string SourceSha256,
    string PlannedSha256,
    string Action);

public sealed partial class CodexConfigManager
{
    public CodexConfigPreview PreviewInstall(
        ProductPaths paths,
        bool enabled,
        bool migrateExisting = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var plan = BuildInstallPlan(paths, enabled, migrateExisting);
        return new CodexConfigPreview(
            BuildDiff(plan.Inspection.Content, plan.Merged),
            !string.Equals(plan.Inspection.Content, plan.Merged, StringComparison.Ordinal),
            plan.PreserveExisting,
            plan.MigrateExisting,
            plan.Inspection.Snapshot.SourceSha256,
            HashPlannedContent(plan.Inspection, plan.Merged),
            plan.Action);
    }

    public CodexIntegrationOwnership InspectOwnership(ProductPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var snapshot = ProtectedFileTransaction.Capture(paths.CodexConfigFile);
        if (!snapshot.Exists)
        {
            return CodexIntegrationOwnership.NotConfigured;
        }

        var content = ProtectedFileTransaction.DecodeUtf8(snapshot);
        if (string.IsNullOrWhiteSpace(content))
        {
            return CodexIntegrationOwnership.NotConfigured;
        }

        try
        {
            ValidateToml(content, allowEmpty: true);
            var hasStart = content.Contains(ProductInfo.ManagedConfigStart, StringComparison.Ordinal);
            var hasEnd = content.Contains(ProductInfo.ManagedConfigEnd, StringComparison.Ordinal);
            if (hasStart || hasEnd)
            {
                if (!TryGetManagedBlock(content, out var managedBlock, out var withoutManaged)
                    || !IsStructurallyBoundManagedReviewer(managedBlock, withoutManaged))
                {
                    return CodexIntegrationOwnership.Invalid;
                }

                return ManagedReviewerMatches(managedBlock, paths)
                    ? CodexIntegrationOwnership.ManagedValid
                    : CodexIntegrationOwnership.ManagedDrift;
            }

            return ContainsExistingIntegration(content)
                ? CodexIntegrationOwnership.ExternalUnmarked
                : CodexIntegrationOwnership.NotConfigured;
        }
        catch (Exception exception) when (exception is InvalidDataException or TomlException or ArgumentException)
        {
            return CodexIntegrationOwnership.Invalid;
        }
    }

    internal ExistingIntegrationInspection InspectExistingUnmanagedIntegration(ProductPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var snapshot = ProtectedFileTransaction.Capture(paths.CodexConfigFile);
        var original = snapshot.Exists
            ? ProtectedFileTransaction.DecodeUtf8(snapshot)
            : string.Empty;
        ValidateToml(original, allowEmpty: true);
        var withoutManaged = RemoveManagedBlock(original, out var hadManagedBlock);
        if (hadManagedBlock)
        {
            _ = TryGetManagedBlock(original, out var managedBlock, out _);
            if (!IsStructurallyBoundManagedReviewer(managedBlock, withoutManaged))
            {
                throw new InvalidOperationException(
                    "The managed Codex markers are not structurally bound to an exclusive local_gpu_reviewer table. No update was applied; review the protected-file diff manually.");
            }
        }

        return new ExistingIntegrationInspection(
            !hadManagedBlock && ContainsExistingIntegration(withoutManaged),
            snapshot,
            original);
    }

    internal ManagedFileResult PreserveExistingUnmanagedIntegration(
        ProductPaths paths,
        ExistingIntegrationInspection expected)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(expected);
        var current = InspectExistingUnmanagedIntegration(paths);
        if (!expected.ShouldPreserve
            || !current.ShouldPreserve
            || !string.Equals(
                current.Snapshot.SourceSha256,
                expected.Snapshot.SourceSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Codex config.toml changed after the existing integration preservation check. No update was applied; review the current file and retry.");
        }

        return PreservedExistingResult(paths);
    }

    public ManagedFileResult InstallOrRepair(
        ProductPaths paths,
        bool enabled,
        Func<string, bool>? startupValidator = null,
        string? expectedSourceSha256 = null,
        string? expectedPlannedSha256 = null,
        bool migrateExisting = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var plan = BuildInstallPlan(paths, enabled, migrateExisting);
        var inspection = plan.Inspection;
        var originalExists = inspection.Snapshot.Exists;
        var original = inspection.Content;
        ValidatePreviewBinding(
            inspection,
            plan.Merged,
            expectedSourceSha256,
            expectedPlannedSha256);
        if (plan.PreserveExisting)
        {
            return PreservedExistingResult(paths);
        }

        var merged = plan.Merged;
        ValidateToml(merged, allowEmpty: false);
        if (string.Equals(original, merged, StringComparison.Ordinal))
        {
            return new ManagedFileResult(paths.CodexConfigFile, false, false, null, "unchanged");
        }

        Directory.CreateDirectory(paths.CodexHome);
        var backup = originalExists
            ? ProtectedFileTransaction.CreateBackup(paths.CodexConfigFile, inspection.Snapshot)
            : null;
        var appliedBytes = EncodeUtf8PreservingPreamble(merged, inspection.Snapshot.Bytes);
        var replaced = false;
        try
        {
            ProtectedFileTransaction.ReplaceIfUnchanged(
                paths.CodexConfigFile,
                inspection.Snapshot,
                appliedBytes);
            replaced = true;
            var written = ProtectedFileTransaction.Capture(paths.CodexConfigFile);
            if (!written.Bytes.AsSpan().SequenceEqual(appliedBytes))
            {
                throw new InvalidOperationException("Codex config.toml changed immediately after the helper update.");
            }

            ValidateToml(ProtectedFileTransaction.DecodeUtf8(written), allowEmpty: false);
            if (startupValidator is not null && !startupValidator(paths.CodexHome))
            {
                throw new InvalidOperationException("A fresh Codex process rejected the managed MCP configuration.");
            }

            if (!ProtectedFileTransaction.Matches(
                    paths.CodexConfigFile,
                    new ProtectedFileSnapshot(true, appliedBytes, string.Empty)))
            {
                throw new InvalidOperationException("Codex config.toml changed during post-write validation.");
            }

            return new ManagedFileResult(
                paths.CodexConfigFile,
                !originalExists,
                true,
                backup,
                plan.Action)
            {
                AppliedBytes = appliedBytes
            };
        }
        catch (Exception exception)
        {
            if (replaced
                && !ProtectedFileTransaction.TryRestoreIfUnchanged(
                    paths.CodexConfigFile,
                    appliedBytes,
                    inspection.Snapshot))
            {
                throw new IOException(
                    "Codex config.toml changed after the helper update. The newer bytes were preserved; use the timestamped backup and current-file diff for manual review.",
                    exception);
            }

            throw;
        }
    }

    private CodexInstallPlan BuildInstallPlan(
        ProductPaths paths,
        bool enabled,
        bool migrateExisting)
    {
        var inspection = InspectExistingUnmanagedIntegration(paths);
        var withoutManaged = RemoveManagedBlock(inspection.Content, out var hadManagedBlock);
        if (inspection.ShouldPreserve && !migrateExisting)
        {
            return new CodexInstallPlan(
                inspection,
                inspection.Content,
                PreserveExisting: true,
                MigrateExisting: false,
                Action: "preserved-existing-unmanaged");
        }

        var managed = BuildManagedBlock(paths, enabled);
        if (inspection.ShouldPreserve)
        {
            var migrated = ReplaceExistingReviewerTable(inspection.Content, managed);
            ValidateToml(migrated, allowEmpty: false);
            return new CodexInstallPlan(
                inspection,
                migrated,
                PreserveExisting: false,
                MigrateExisting: true,
                Action: "migrated-existing");
        }

        var managedAlreadyMatches = hadManagedBlock
            && TryGetManagedBlock(inspection.Content, out var currentManagedBlock, out _)
            && ManagedReviewerMatches(currentManagedBlock, paths)
            && TryGetManagedEnabled(currentManagedBlock, out var currentEnabled)
            && currentEnabled == enabled;
        var merged = hadManagedBlock
            ? managedAlreadyMatches
                ? inspection.Content
                : ReplaceManagedBlockInPlace(inspection.Content, managed)
            : AppendBlock(withoutManaged, managed);
        return new CodexInstallPlan(
            inspection,
            merged,
            PreserveExisting: false,
            MigrateExisting: false,
            Action: hadManagedBlock ? "updated" : "installed");
    }

    private static void ValidatePreviewBinding(
        ExistingIntegrationInspection inspection,
        string planned,
        string? expectedSourceSha256,
        string? expectedPlannedSha256)
    {
        if ((expectedSourceSha256 is null) != (expectedPlannedSha256 is null))
        {
            throw new InvalidOperationException("Both config.toml preview hashes are required together.");
        }

        if (expectedSourceSha256 is not null
            && !string.Equals(
                expectedSourceSha256,
                inspection.Snapshot.SourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "config.toml changed after the before/after preview. Review a fresh diff before applying repair.");
        }

        var plannedSha256 = HashPlannedContent(inspection, planned);
        if (expectedPlannedSha256 is not null
            && !string.Equals(expectedPlannedSha256, plannedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The managed config.toml plan no longer matches the reviewed diff. Review a fresh diff before applying repair.");
        }
    }

    private static string HashPlannedContent(
        ExistingIntegrationInspection inspection,
        string planned)
    {
        if (string.Equals(inspection.Content, planned, StringComparison.Ordinal))
        {
            return inspection.Snapshot.SourceSha256;
        }

        return Convert.ToHexString(SHA256.HashData(
            EncodeUtf8PreservingPreamble(planned, inspection.Snapshot.Bytes)));
    }

    private static string BuildDiff(string before, string after)
    {
        var builder = new StringBuilder();
        builder.AppendLine("--- config.toml (before)");
        builder.AppendLine("+++ config.toml (after)");
        builder.AppendLine("@@ changed hunk preview; contents are written only to the explicit diff file @@");
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

    public ManagedFileResult SetEnabled(ProductPaths paths, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var snapshot = ProtectedFileTransaction.Capture(paths.CodexConfigFile);
        if (!snapshot.Exists)
        {
            throw new FileNotFoundException("Codex config.toml was not found.", paths.CodexConfigFile);
        }

        var original = ProtectedFileTransaction.DecodeUtf8(snapshot);
        ValidateToml(original, allowEmpty: false);
        if (!TryGetManagedBlock(original, out var managedBlock, out var withoutManaged)
            || !IsStructurallyBoundManagedReviewer(managedBlock, withoutManaged)
            || !ManagedReviewerMatches(managedBlock, paths))
        {
            throw new InvalidOperationException(
                "The managed Codex reviewer entry has changed since installation. No enabled setting was modified; review a fresh protected-file diff first.");
        }

        var start = original.IndexOf(ProductInfo.ManagedConfigStart, StringComparison.Ordinal);
        var end = original.IndexOf(ProductInfo.ManagedConfigEnd, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("The managed Codex integration section was not found.");
        }

        var blockEnd = end + ProductInfo.ManagedConfigEnd.Length;
        var block = original[start..blockEnd];
        var enabledLine = EnabledLineRegex();
        if (!enabledLine.IsMatch(block))
        {
            throw new InvalidOperationException("The managed enabled setting was not found.");
        }

        var updatedBlock = enabledLine.Replace(block, $"enabled = {enabled.ToString().ToLowerInvariant()}", 1);
        if (string.Equals(block, updatedBlock, StringComparison.Ordinal))
        {
            return new ManagedFileResult(paths.CodexConfigFile, false, false, null, "unchanged");
        }

        var updated = original[..start] + updatedBlock + original[blockEnd..];
        ValidateToml(updated, allowEmpty: false);
        var backup = ProtectedFileTransaction.CreateBackup(paths.CodexConfigFile, snapshot);
        var appliedBytes = EncodeUtf8PreservingPreamble(updated, snapshot.Bytes);
        var replaced = false;
        try
        {
            ProtectedFileTransaction.ReplaceIfUnchanged(paths.CodexConfigFile, snapshot, appliedBytes);
            replaced = true;
            return new ManagedFileResult(
                paths.CodexConfigFile,
                false,
                true,
                backup,
                enabled ? "enabled" : "disabled")
            {
                AppliedBytes = appliedBytes
            };
        }
        catch (Exception exception)
        {
            if (replaced
                && !ProtectedFileTransaction.TryRestoreIfUnchanged(
                    paths.CodexConfigFile,
                    appliedBytes,
                    snapshot))
            {
                throw new IOException(
                    "Codex config.toml changed after the helper control update. The newer bytes were preserved for manual review.",
                    exception);
            }

            throw;
        }
    }

    public ManagedFileResult Uninstall(
        ProductPaths paths,
        string? originalBackupPath = null,
        bool fileWasCreatedByProduct = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var snapshot = ProtectedFileTransaction.Capture(paths.CodexConfigFile);
        if (!snapshot.Exists)
        {
            return new ManagedFileResult(paths.CodexConfigFile, false, false, null, "not-present");
        }

        var originalBytes = snapshot.Bytes;
        var original = ProtectedFileTransaction.DecodeUtf8(snapshot);
        ValidateToml(original, allowEmpty: true);
        if (!TryGetManagedBlock(original, out var managedBlock, out var updated))
        {
            return new ManagedFileResult(paths.CodexConfigFile, false, false, null, "not-managed");
        }

        if (!IsStructurallyBoundManagedReviewer(managedBlock, updated)
            || !ManagedReviewerMatches(managedBlock, paths))
        {
            throw new InvalidDataException(
                "The managed Codex markers are not structurally bound to the exact local_gpu_reviewer table. The current file was preserved for manual cleanup.");
        }

        ValidateToml(updated, allowEmpty: true);
        var backup = ProtectedFileTransaction.CreateBackup(paths.CodexConfigFile, snapshot);
        byte[]? appliedBytes = null;
        var replaced = false;
        try
        {
            if (TryGetExactOriginalBytes(
                    paths.CodexConfigFile,
                    originalBackupPath,
                    originalBytes,
                    original,
                    out var exactOriginalBytes))
            {
                appliedBytes = exactOriginalBytes;
                ProtectedFileTransaction.ReplaceIfUnchanged(paths.CodexConfigFile, snapshot, appliedBytes);
                replaced = true;
                return new ManagedFileResult(
                    paths.CodexConfigFile,
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
                ProtectedFileTransaction.DeleteIfUnchanged(paths.CodexConfigFile, snapshot);
                return new ManagedFileResult(paths.CodexConfigFile, false, true, backup, "removed-product-file");
            }

            appliedBytes = EncodeUtf8PreservingPreamble(updated, originalBytes);
            ProtectedFileTransaction.ReplaceIfUnchanged(paths.CodexConfigFile, snapshot, appliedBytes);
            replaced = true;
            return new ManagedFileResult(paths.CodexConfigFile, false, true, backup, "removed")
            {
                AppliedBytes = appliedBytes
            };
        }
        catch (Exception exception)
        {
            if (replaced
                && appliedBytes is not null
                && !ProtectedFileTransaction.TryRestoreIfUnchanged(
                    paths.CodexConfigFile,
                    appliedBytes,
                    snapshot))
            {
                throw new IOException(
                    "Codex config.toml changed during uninstall. The newer bytes were preserved for manual review.",
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
            throw new InvalidOperationException("The exact helper-produced bytes required for guarded rollback are unavailable.");
        }

        ProtectedFileSnapshot original;
        if (result.Created)
        {
            original = new ProtectedFileSnapshot(false, [], "MISSING");
        }
        else if (string.IsNullOrWhiteSpace(result.BackupPath) || !File.Exists(result.BackupPath))
        {
            throw new InvalidOperationException("The exact Codex configuration backup required for rollback is unavailable.");
        }
        else
        {
            original = ProtectedFileTransaction.Capture(result.BackupPath);
        }

        if (!ProtectedFileTransaction.TryRestoreIfUnchanged(result.Path, result.AppliedBytes, original))
        {
            throw new InvalidOperationException(
                "Codex config.toml changed after the helper update. Rollback left the newer bytes untouched; review the current file and timestamped backup manually.");
        }
    }

    public static void ValidateToml(string content, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            if (allowEmpty)
            {
                return;
            }

            throw new InvalidDataException("Codex config.toml cannot be empty.");
        }

        try
        {
            _ = TomlSerializer.Deserialize<TomlTable>(content);
        }
        catch (TomlException exception)
        {
            throw new InvalidDataException("Invalid Codex TOML: " + exception.Message, exception);
        }
    }

    private static string BuildManagedBlock(ProductPaths paths, bool enabled)
    {
        var command = EscapeTomlBasicString(paths.McpExecutable);
        var state = EscapeTomlBasicString(paths.StateDirectory);
        return $$"""
            {{ProductInfo.ManagedConfigStart}}
            # This optional stdio MCP server is a community integration, not a native Codex subagent.
            [mcp_servers.local_gpu_reviewer]
            command = "{{command}}"
            enabled = {{enabled.ToString().ToLowerInvariant()}}
            required = false
            enabled_tools = ["local_gpu_health", "local_gpu_plan", "local_gpu_review"]
            env_vars = ["OLLAMA_MODELS"]
            default_tools_approval_mode = "prompt"
            supports_parallel_tool_calls = false
            startup_timeout_sec = 20
            tool_timeout_sec = 360
            env = { THALEN_HELPER_STATE_DIR = "{{state}}", OLLAMA_HOST = "http://127.0.0.1:11434" }

            [mcp_servers.local_gpu_reviewer.tools.local_gpu_health]
            approval_mode = "auto"

            [mcp_servers.local_gpu_reviewer.tools.local_gpu_plan]
            approval_mode = "auto"

            [mcp_servers.local_gpu_reviewer.tools.local_gpu_review]
            approval_mode = "prompt"
            {{ProductInfo.ManagedConfigEnd}}
            """;
    }

    private static string AppendBlock(string content, string block)
    {
        var trimmed = content.TrimEnd('\r', '\n', ' ', '\t');
        return string.IsNullOrEmpty(trimmed)
            ? block + Environment.NewLine
            : trimmed + Environment.NewLine + Environment.NewLine + block + Environment.NewLine;
    }

    private static string RemoveManagedBlock(string content, out bool removed)
    {
        var start = content.IndexOf(ProductInfo.ManagedConfigStart, StringComparison.Ordinal);
        var end = content.IndexOf(ProductInfo.ManagedConfigEnd, StringComparison.Ordinal);
        if (start < 0 && end < 0)
        {
            removed = false;
            return content;
        }

        if (start < 0 || end < start)
        {
            throw new InvalidDataException("The managed Codex configuration markers are malformed.");
        }

        if (content.IndexOf(ProductInfo.ManagedConfigStart, start + ProductInfo.ManagedConfigStart.Length, StringComparison.Ordinal) >= 0
            || content.IndexOf(ProductInfo.ManagedConfigEnd, end + ProductInfo.ManagedConfigEnd.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidDataException("Multiple managed Codex configuration sections were found.");
        }

        var removeEnd = ConsumeSingleLineEnding(
            content,
            end + ProductInfo.ManagedConfigEnd.Length);
        removed = true;
        return content[..start] + content[removeEnd..];
    }

    private static bool TryGetManagedBlock(
        string content,
        out string managedBlock,
        out string withoutManaged)
    {
        withoutManaged = RemoveManagedBlock(content, out var removed);
        if (!removed)
        {
            managedBlock = string.Empty;
            return false;
        }

        var start = content.IndexOf(ProductInfo.ManagedConfigStart, StringComparison.Ordinal);
        var end = content.IndexOf(ProductInfo.ManagedConfigEnd, start, StringComparison.Ordinal);
        managedBlock = content[start..(end + ProductInfo.ManagedConfigEnd.Length)];
        return true;
    }

    private static string EscapeTomlBasicString(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static bool ContainsExistingIntegration(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var document = TomlSerializer.Deserialize<TomlTable>(content);
        return document is not null
            && document.TryGetValue("mcp_servers", out var serversValue)
            && serversValue is TomlTable servers
            && servers.TryGetValue(ProductInfo.IntegrationName, out var reviewerValue)
            && reviewerValue is TomlTable;
    }

    private static bool ManagedReviewerMatches(string content, ProductPaths paths)
    {
        var reviewer = GetExclusiveManagedReviewer(content);
        if (reviewer is null || HasUnapprovedManagedEnvironment(reviewer))
        {
            return false;
        }

        var expectedReviewerKeys = new[]
        {
            "command",
            "default_tools_approval_mode",
            "enabled",
            "enabled_tools",
            "env",
            "env_vars",
            "required",
            "startup_timeout_sec",
            "supports_parallel_tool_calls",
            "tool_timeout_sec",
            "tools"
        };
        if (expectedReviewerKeys.Any(key => !reviewer.ContainsKey(key)))
        {
            return false;
        }

        if (!TryString(reviewer, "command", out var command)
            || !PathsEqual(command, paths.McpExecutable)
            || !TryBoolean(reviewer, "enabled", out _)
            || !TryBoolean(reviewer, "required", out var required)
            || required
            || !TryBoolean(reviewer, "supports_parallel_tool_calls", out var supportsParallel)
            || supportsParallel
            || !TryString(reviewer, "default_tools_approval_mode", out var defaultApproval)
            || !string.Equals(defaultApproval, "prompt", StringComparison.Ordinal)
            || !TryInteger(reviewer, "startup_timeout_sec", 20)
            || !TryInteger(reviewer, "tool_timeout_sec", 360)
            || !reviewer.TryGetValue("enabled_tools", out var enabledToolsValue)
            || enabledToolsValue is not TomlArray enabledTools
            || !enabledTools.OfType<string>().SequenceEqual(["local_gpu_health", "local_gpu_plan", "local_gpu_review"], StringComparer.Ordinal)
            || !reviewer.TryGetValue("env_vars", out var environmentVariablesValue)
            || environmentVariablesValue is not TomlArray environmentVariables
            || !environmentVariables.OfType<string>().SequenceEqual(["OLLAMA_MODELS"], StringComparer.Ordinal)
            || !reviewer.TryGetValue("env", out var envValue)
            || envValue is not TomlTable env
            || !TryString(env, "OLLAMA_HOST", out var host)
            || !string.Equals(host, "http://127.0.0.1:11434", StringComparison.Ordinal)
            || !TryString(env, "THALEN_HELPER_STATE_DIR", out var stateDirectory)
            || !PathsEqual(stateDirectory, paths.StateDirectory)
            || !reviewer.TryGetValue("tools", out var toolsValue)
            || toolsValue is not TomlTable tools
            || !HasExactApprovalMode(tools, "local_gpu_health", "auto")
            || !HasExactApprovalMode(tools, "local_gpu_plan", "auto")
            || !HasExactApprovalMode(tools, "local_gpu_review", "prompt"))
        {
            return false;
        }

        return true;
    }

    private static void ThrowIfUnapprovedManagedEnvironment(string content)
    {
        var document = TomlSerializer.Deserialize<TomlTable>(content);
        if (document is null
            || !document.TryGetValue("mcp_servers", out var serversValue)
            || serversValue is not TomlTable servers
            || !servers.TryGetValue(ProductInfo.IntegrationName, out var reviewerValue)
            || reviewerValue is not TomlTable reviewer
            || !HasUnapprovedManagedEnvironment(reviewer))
        {
            return;
        }

        throw new InvalidOperationException(
            "The existing local_gpu_reviewer block contains an unapproved environment entry. No automatic migration or repair was applied; review and remove the extra env or env_vars entry explicitly, then preview repair again.");
    }

    private static bool HasUnapprovedManagedEnvironment(TomlTable reviewer)
    {
        if (reviewer.TryGetValue("env_vars", out var environmentVariablesValue)
            && (environmentVariablesValue is not TomlArray environmentVariables
                || environmentVariables.Count != 1
                || environmentVariables[0] is not string imported
                || !string.Equals(imported, "OLLAMA_MODELS", StringComparison.Ordinal)))
        {
            return true;
        }

        if (!reviewer.TryGetValue("env", out var envValue))
        {
            return false;
        }

        if (envValue is not TomlTable environment)
        {
            return true;
        }

        return environment.Keys.Any(key => !string.Equals(key, "OLLAMA_HOST", StringComparison.Ordinal)
            && !string.Equals(key, "THALEN_HELPER_STATE_DIR", StringComparison.Ordinal));
    }

    private static bool TryGetManagedEnabled(string content, out bool enabled)
    {
        enabled = false;
        var reviewer = GetExclusiveManagedReviewer(content);
        return reviewer is not null && TryBoolean(reviewer, "enabled", out enabled);
    }

    private static bool IsStructurallyBoundManagedReviewer(string managedBlock, string withoutManaged)
    {
        ValidateToml(withoutManaged, allowEmpty: true);
        return GetExclusiveManagedReviewer(managedBlock) is not null
            && !ContainsExistingIntegration(withoutManaged);
    }

    private static TomlTable? GetExclusiveManagedReviewer(string content)
    {
        var document = TomlSerializer.Deserialize<TomlTable>(content);
        if (document is null
            || document.Count != 1
            || !document.TryGetValue("mcp_servers", out var serversValue)
            || serversValue is not TomlTable servers
            || servers.Count != 1
            || !servers.TryGetValue(ProductInfo.IntegrationName, out var reviewerValue)
            || reviewerValue is not TomlTable reviewer)
        {
            return null;
        }

        return reviewer;
    }

    private static bool HasExactApprovalMode(TomlTable tools, string name, string expected)
        => tools.TryGetValue(name, out var value)
            && value is TomlTable tool
            && tool.Count == 1
            && TryString(tool, "approval_mode", out var approval)
            && string.Equals(approval, expected, StringComparison.Ordinal);

    private static bool TryString(TomlTable table, string key, out string value)
    {
        value = string.Empty;
        if (!table.TryGetValue(key, out var raw) || raw is not string text)
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryBoolean(TomlTable table, string key, out bool value)
    {
        value = false;
        if (!table.TryGetValue(key, out var raw) || raw is not bool boolean)
        {
            return false;
        }

        value = boolean;
        return true;
    }

    private static bool TryInteger(TomlTable table, string key, long expected)
        => table.TryGetValue(key, out var raw)
            && raw is long integer
            && integer == expected;

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
    private static ManagedFileResult PreservedExistingResult(ProductPaths paths)
        => new(
            paths.CodexConfigFile,
            false,
            false,
            null,
            "preserved-existing-unmanaged",
            "An existing unmarked mcp_servers.local_gpu_reviewer table was preserved byte-for-byte. No duplicate TOML table was added, and this helper did not activate or reconfigure that integration.");

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
        ValidateToml(original, allowEmpty: true);
        if (currentBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            != backupSnapshot.Bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return false;
        }
        if (original.Contains(ProductInfo.ManagedConfigStart, StringComparison.Ordinal)
            || original.Contains(ProductInfo.ManagedConfigEnd, StringComparison.Ordinal))
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
        var start = current.IndexOf(ProductInfo.ManagedConfigStart, StringComparison.Ordinal);
        var end = current.IndexOf(ProductInfo.ManagedConfigEnd, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            return false;
        }

        var managed = current[start..(end + ProductInfo.ManagedConfigEnd.Length)];
        var expected = EncodeUtf8PreservingPreamble(AppendBlock(baseContent, managed), currentBytes);
        if (currentBytes.AsSpan().SequenceEqual(expected))
        {
            return true;
        }

        if (!ContainsExistingIntegration(baseContent))
        {
            return false;
        }

        try
        {
            var expectedMigration = EncodeUtf8PreservingPreamble(
                ReplaceExistingReviewerTable(baseContent, managed),
                currentBytes);
            return currentBytes.AsSpan().SequenceEqual(expectedMigration);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            return false;
        }
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

    private static string ReplaceExistingReviewerTable(string content, string managedBlock)
    {
        ThrowIfUnapprovedManagedEnvironment(content);
        var headers = ReadTableHeaders(content);
        var targets = headers
            .Select((header, index) => (header, index))
            .Where(item => IsReviewerPath(item.header.Path))
            .ToArray();
        var roots = targets.Where(item => item.header.Path.Count == 2).ToArray();
        if (roots.Length != 1
            || targets.Length == 0
            || targets[0].index != roots[0].index
            || targets.Any(item => item.header.IsArray))
        {
            throw new InvalidOperationException(
                "The existing local_gpu_reviewer table layout is ambiguous or displaced. No migration was applied.");
        }

        var first = targets[0].index;
        var last = targets[^1].index;
        if (Enumerable.Range(first, last - first + 1).Any(index => !IsReviewerPath(headers[index].Path)))
        {
            throw new InvalidOperationException(
                "The existing local_gpu_reviewer subtables are interleaved with unrelated tables. No migration was applied.");
        }

        var start = headers[first].Start;
        var end = last + 1 < headers.Count ? headers[last + 1].Start : content.Length;
        var expectedFamilyEnd = managedBlock.IndexOf(ProductInfo.ManagedConfigEnd, StringComparison.Ordinal);
        var expectedRoot = ReadTableHeaders(expectedFamilyEnd < 0 ? managedBlock : managedBlock[..expectedFamilyEnd])
            .FirstOrDefault(header => header.Path.Count == 2 && IsReviewerPath(header.Path));
        var expectedFamilyStart = expectedRoot?.Start ?? -1;
        if (expectedFamilyStart < 0 || expectedFamilyEnd < expectedFamilyStart)
        {
            throw new InvalidDataException("The packaged managed reviewer template is malformed.");
        }

        var expectedFamily = managedBlock[expectedFamilyStart..expectedFamilyEnd].TrimEnd('\r', '\n');
        var expectedHeaders = ReadTableHeaders(expectedFamily);
        var expectedSections = expectedHeaders
            .Select((header, index) => new TableSection(
                header.Path,
                expectedFamily[header.Start..(index + 1 < expectedHeaders.Count
                    ? expectedHeaders[index + 1].Start
                    : expectedFamily.Length)]))
            .ToList();
        var existingHasEnvTable = targets.Any(item => item.header.Path.Count == 3
            && string.Equals(item.header.Path[2], "env", StringComparison.Ordinal));
        if (existingHasEnvTable)
        {
            var rootIndex = expectedSections.FindIndex(section => section.Path.Count == 2);
            expectedSections[rootIndex] = expectedSections[rootIndex] with
            {
                Content = RemoveAssignmentLine(expectedSections[rootIndex].Content, "env")
            };
            if (!expectedSections.Any(section => section.Path.Count == 3
                && IsReviewerPath(section.Path)
                && string.Equals(section.Path[2], "env", StringComparison.Ordinal)))
            {
                var managedDocument = TomlSerializer.Deserialize<TomlTable>(managedBlock)!;
                var managedReviewer = (TomlTable)((TomlTable)managedDocument["mcp_servers"]!)[ProductInfo.IntegrationName]!;
                var managedEnv = (TomlTable)managedReviewer["env"]!;
                var envContent = new StringBuilder("[mcp_servers.local_gpu_reviewer.env]").AppendLine();
                foreach (var key in new[] { "THALEN_HELPER_STATE_DIR", "OLLAMA_HOST" })
                {
                    envContent.Append(key)
                        .Append(" = \"")
                        .Append(EscapeTomlBasicString((string)managedEnv[key]!))
                        .AppendLine("\"");
                }

                expectedSections.Add(new TableSection(
                    ["mcp_servers", ProductInfo.IntegrationName, "env"],
                    envContent.ToString()));
            }
        }
        var expectedByPath = expectedSections.ToDictionary(
            section => PathKey(section.Path),
            StringComparer.Ordinal);
        var foundManagedPaths = new HashSet<string>(StringComparer.Ordinal);
        var family = new StringBuilder();
        for (var index = first; index <= last; index++)
        {
            var header = headers[index];
            var sectionEnd = index + 1 < headers.Count ? headers[index + 1].Start : content.Length;
            var section = content[header.Start..sectionEnd];
            var key = PathKey(header.Path);
            if (expectedByPath.TryGetValue(key, out var expected))
            {
                family.Append(MergeManagedSection(section, expected.Content));
                foundManagedPaths.Add(key);
            }
            else
            {
                family.Append(section);
            }
        }

        foreach (var expected in expectedSections.Where(section => !foundManagedPaths.Contains(PathKey(section.Path))))
        {
            if (family.Length > 0 && family[^1] is not '\r' and not '\n')
            {
                family.AppendLine();
            }

            family.AppendLine().Append(expected.Content.TrimEnd('\r', '\n')).AppendLine();
        }

        var replacement = ProductInfo.ManagedConfigStart
            + Environment.NewLine
            + family.ToString().TrimEnd('\r', '\n')
            + Environment.NewLine
            + ProductInfo.ManagedConfigEnd
            + Environment.NewLine
            + (end < content.Length ? Environment.NewLine : string.Empty);
        var migrated = content[..start] + replacement + content[end..];
        ValidateToml(migrated, allowEmpty: false);
        if (ContainsExistingIntegration(RemoveManagedBlock(migrated, out _)))
        {
            throw new InvalidOperationException(
                "The existing local_gpu_reviewer definition was not isolated to one replaceable table family. No migration was applied.");
        }

        return migrated;
    }

    private static string MergeManagedSection(string existing, string expected)
    {
        var expectedAssignments = ReadAssignments(expected);
        if (expectedAssignments.Count == 0)
        {
            return existing;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (var line in ReadLines(existing))
        {
            if (!TryReadAssignment(line.Content, out var key, out var comment)
                || !expectedAssignments.TryGetValue(key, out var replacement))
            {
                builder.Append(line.Content).Append(line.Ending);
                continue;
            }

            if (!seen.Add(key))
            {
                throw new InvalidOperationException($"The existing reviewer table contains duplicate {key} assignments.");
            }

            var value = line.Content[(line.Content.IndexOf('=') + 1)..].Trim();
            if (value.StartsWith("\"\"\"", StringComparison.Ordinal)
                || value.StartsWith("'''", StringComparison.Ordinal)
                || (value.StartsWith("[", StringComparison.Ordinal) && !value.Contains(']')))
            {
                throw new InvalidOperationException($"The existing reviewer {key} assignment is multiline or ambiguous. No migration was applied.");
            }

            var indentLength = line.Content.Length - line.Content.TrimStart(' ', '\t').Length;
            builder.Append(line.Content[..indentLength])
                .Append(replacement)
                .Append(comment)
                .Append(line.Ending);
        }

        foreach (var assignment in expectedAssignments.Where(item => !seen.Contains(item.Key)))
        {
            if (builder.Length > 0 && builder[^1] is not '\r' and not '\n')
            {
                builder.AppendLine();
            }

            builder.Append(assignment.Value).AppendLine();
        }

        return builder.ToString();
    }

    private static Dictionary<string, string> ReadAssignments(string section)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ReadLines(section))
        {
            if (TryReadAssignment(line.Content, out var key, out var comment))
            {
                var assignment = line.Content.Trim();
                if (comment.Length > 0)
                {
                    assignment = assignment[..^comment.TrimStart().Length].TrimEnd();
                }
                result.Add(key, assignment);
            }
        }

        return result;
    }

    private static string RemoveAssignmentLine(string section, string assignmentKey)
    {
        var builder = new StringBuilder();
        foreach (var line in ReadLines(section))
        {
            if (TryReadAssignment(line.Content, out var key, out _)
                && string.Equals(key, assignmentKey, StringComparison.Ordinal))
            {
                continue;
            }

            builder.Append(line.Content).Append(line.Ending);
        }

        return builder.ToString();
    }

    private static bool TryReadAssignment(string line, out string key, out string comment)
    {
        key = string.Empty;
        comment = string.Empty;
        var trimmed = line.TrimStart(' ', '\t');
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return false;
        }

        var equals = trimmed.IndexOf('=');
        if (equals <= 0 || !TryParseDottedKey(trimmed[..equals].Trim(), out var keyPath) || keyPath.Count != 1)
        {
            return false;
        }

        key = keyPath[0];
        var commentIndex = FindUnquotedComment(trimmed, equals + 1);
        comment = commentIndex >= 0 ? " " + trimmed[commentIndex..].TrimStart() : string.Empty;
        return true;
    }

    private static int FindUnquotedComment(string value, int start)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = start; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (quote == '"' && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = quote == '\0' ? character : quote == character ? '\0' : quote;
                continue;
            }

            if (character == '#' && quote == '\0')
            {
                return index;
            }
        }

        return -1;
    }

    private static IReadOnlyList<TextLine> ReadLines(string content)
    {
        var result = new List<TextLine>();
        var start = 0;
        while (start < content.Length)
        {
            var end = content.IndexOfAny(['\r', '\n'], start);
            if (end < 0)
            {
                result.Add(new TextLine(content[start..], string.Empty));
                break;
            }

            var endingLength = content[end] == '\r' && end + 1 < content.Length && content[end + 1] == '\n' ? 2 : 1;
            result.Add(new TextLine(content[start..end], content.Substring(end, endingLength)));
            start = end + endingLength;
        }

        return result;
    }

    private static string PathKey(IReadOnlyList<string> path)
        => string.Join("\u001f", path);

    private static string ReplaceManagedBlockInPlace(string content, string managedBlock)
    {
        var start = content.IndexOf(ProductInfo.ManagedConfigStart, StringComparison.Ordinal);
        var end = content.IndexOf(ProductInfo.ManagedConfigEnd, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            throw new InvalidDataException("The managed Codex configuration markers are malformed.");
        }

        var blockEnd = end + ProductInfo.ManagedConfigEnd.Length;
        var existingBlock = content[start..blockEnd];
        ThrowIfUnapprovedManagedEnvironment(existingBlock);
        var familyStart = existingBlock.IndexOf('[', StringComparison.Ordinal);
        var familyEnd = existingBlock.IndexOf(ProductInfo.ManagedConfigEnd, StringComparison.Ordinal);
        if (familyStart < 0 || familyEnd < familyStart)
        {
            throw new InvalidDataException("The managed Codex reviewer family is malformed.");
        }

        var merged = ReplaceExistingReviewerTable(
            existingBlock[familyStart..familyEnd].TrimEnd('\r', '\n'),
            managedBlock).TrimEnd('\r', '\n');
        return content[..start] + merged + content[blockEnd..];
    }

    private static List<TableHeader> ReadTableHeaders(string content)
    {
        var result = new List<TableHeader>();
        var position = 0;
        while (position < content.Length)
        {
            var lineEnd = content.IndexOfAny(['\r', '\n'], position);
            if (lineEnd < 0)
            {
                lineEnd = content.Length;
            }

            var line = content[position..lineEnd];
            var trimmed = line.TrimStart(' ', '\t');
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                var isArray = trimmed.StartsWith("[[", StringComparison.Ordinal);
                var close = isArray
                    ? trimmed.IndexOf("]]", StringComparison.Ordinal)
                    : trimmed.IndexOf(']');
                if (close > (isArray ? 2 : 1))
                {
                    var keyText = trimmed[(isArray ? 2 : 1)..close];
                    if (!TryParseDottedKey(keyText, out var path))
                    {
                        if (keyText.Contains(ProductInfo.IntegrationName, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                "The existing local_gpu_reviewer table header uses an unsupported or ambiguous key form. No migration was applied.");
                        }

                        path = [];
                    }

                    result.Add(new TableHeader(position, path, isArray));
                }
            }

            if (lineEnd == content.Length)
            {
                break;
            }

            position = lineEnd + 1;
            if (content[lineEnd] == '\r'
                && position < content.Length
                && content[position] == '\n')
            {
                position++;
            }
        }

        return result;
    }

    private static bool TryParseDottedKey(string text, out IReadOnlyList<string> path)
    {
        var parts = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= text.Length)
            {
                path = [];
                return false;
            }

            string part;
            if (text[index] is '"' or '\'')
            {
                var quote = text[index++];
                var start = index;
                while (index < text.Length && text[index] != quote)
                {
                    if (quote == '"' && text[index] == '\\')
                    {
                        path = [];
                        return false;
                    }

                    index++;
                }

                if (index >= text.Length)
                {
                    path = [];
                    return false;
                }

                part = text[start..index++];
            }
            else
            {
                var start = index;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '-'))
                {
                    index++;
                }

                if (start == index)
                {
                    path = [];
                    return false;
                }

                part = text[start..index];
            }

            parts.Add(part);
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index == text.Length)
            {
                path = parts;
                return true;
            }

            if (text[index++] != '.')
            {
                path = [];
                return false;
            }
        }

        path = [];
        return false;
    }

    private static bool IsReviewerPath(IReadOnlyList<string> path)
        => path.Count >= 2
            && string.Equals(path[0], "mcp_servers", StringComparison.Ordinal)
            && string.Equals(path[1], ProductInfo.IntegrationName, StringComparison.Ordinal);

    private sealed record CodexInstallPlan(
        ExistingIntegrationInspection Inspection,
        string Merged,
        bool PreserveExisting,
        bool MigrateExisting,
        string Action);

    private sealed record TableHeader(int Start, IReadOnlyList<string> Path, bool IsArray);
    private sealed record TableSection(IReadOnlyList<string> Path, string Content);
    private sealed record TextLine(string Content, string Ending);

    [GeneratedRegex("(?m)^enabled\\s*=\\s*(true|false)\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnabledLineRegex();
}
