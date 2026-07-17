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

public sealed partial class CodexConfigManager
{
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
        Func<string, bool>? startupValidator = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var inspection = InspectExistingUnmanagedIntegration(paths);
        var originalExists = inspection.Snapshot.Exists;
        var original = inspection.Content;

        var withoutManaged = RemoveManagedBlock(original, out var hadManagedBlock);
        if (inspection.ShouldPreserve)
        {
            return PreservedExistingResult(paths);
        }

        var managed = BuildManagedBlock(paths, enabled);
        var merged = AppendBlock(withoutManaged, managed);
        ValidateToml(merged, allowEmpty: false);
        if (string.Equals(original, merged, StringComparison.Ordinal))
        {
            return new ManagedFileResult(paths.CodexConfigFile, false, false, null, "unchanged");
        }

        Directory.CreateDirectory(paths.CodexHome);
        var backup = originalExists
            ? ProtectedFileTransaction.CreateBackup(paths.CodexConfigFile, inspection.Snapshot)
            : null;
        var appliedBytes = new UTF8Encoding(false).GetBytes(merged);
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
                hadManagedBlock ? "updated" : "installed")
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
        if (reviewer is null)
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
            "required",
            "startup_timeout_sec",
            "supports_parallel_tool_calls",
            "tool_timeout_sec",
            "tools"
        };
        if (!reviewer.Keys.Order(StringComparer.Ordinal).SequenceEqual(expectedReviewerKeys, StringComparer.Ordinal))
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
            || !reviewer.TryGetValue("env", out var envValue)
            || envValue is not TomlTable env
            || !env.Keys.Order(StringComparer.Ordinal).SequenceEqual(["OLLAMA_HOST", "THALEN_HELPER_STATE_DIR"], StringComparer.Ordinal)
            || !TryString(env, "OLLAMA_HOST", out var host)
            || !string.Equals(host, "http://127.0.0.1:11434", StringComparison.Ordinal)
            || !TryString(env, "THALEN_HELPER_STATE_DIR", out var stateDirectory)
            || !PathsEqual(stateDirectory, paths.StateDirectory)
            || !reviewer.TryGetValue("tools", out var toolsValue)
            || toolsValue is not TomlTable tools
            || !tools.Keys.Order(StringComparer.Ordinal).SequenceEqual(["local_gpu_health", "local_gpu_plan", "local_gpu_review"], StringComparer.Ordinal)
            || !HasExactApprovalMode(tools, "local_gpu_health", "auto")
            || !HasExactApprovalMode(tools, "local_gpu_plan", "auto")
            || !HasExactApprovalMode(tools, "local_gpu_review", "prompt"))
        {
            return false;
        }

        return true;
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
        var expected = new UTF8Encoding(false).GetBytes(AppendBlock(baseContent, managed));
        return currentBytes.AsSpan().SequenceEqual(expected);
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

    [GeneratedRegex("(?m)^enabled\\s*=\\s*(true|false)\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnabledLineRegex();
}
