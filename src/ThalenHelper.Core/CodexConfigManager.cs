using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace ThalenHelper.Core;

public sealed partial class CodexConfigManager
{
    public ManagedFileResult InstallOrRepair(
        ProductPaths paths,
        bool enabled,
        Func<string, bool>? startupValidator = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var originalExists = File.Exists(paths.CodexConfigFile);
        var originalBytes = originalExists ? File.ReadAllBytes(paths.CodexConfigFile) : [];
        var original = originalExists ? File.ReadAllText(paths.CodexConfigFile, Encoding.UTF8) : string.Empty;
        ValidateToml(original, allowEmpty: true);

        var withoutManaged = RemoveManagedBlock(original, out var hadManagedBlock);
        if (!hadManagedBlock && ContainsExistingIntegration(withoutManaged))
        {
            return new ManagedFileResult(
                paths.CodexConfigFile,
                false,
                false,
                null,
                "preserved-existing-unmanaged",
                "An existing unmarked mcp_servers.local_gpu_reviewer table was preserved byte-for-byte. No duplicate TOML table was added, and this helper did not activate or reconfigure that integration.");
        }

        var managed = BuildManagedBlock(paths, enabled);
        var merged = AppendBlock(withoutManaged, managed);
        ValidateToml(merged, allowEmpty: false);
        if (string.Equals(original, merged, StringComparison.Ordinal))
        {
            return new ManagedFileResult(paths.CodexConfigFile, false, false, null, "unchanged");
        }

        Directory.CreateDirectory(paths.CodexHome);
        var backup = originalExists ? CreateBackup(paths.CodexConfigFile) : null;
        try
        {
            WriteAtomic(paths.CodexConfigFile, merged);
            ValidateToml(File.ReadAllText(paths.CodexConfigFile, Encoding.UTF8), allowEmpty: false);
            if (startupValidator is not null && !startupValidator(paths.CodexHome))
            {
                throw new InvalidOperationException("A fresh Codex process rejected the managed MCP configuration.");
            }

            return new ManagedFileResult(
                paths.CodexConfigFile,
                !originalExists,
                true,
                backup,
                hadManagedBlock ? "updated" : "installed");
        }
        catch
        {
            RestoreOriginal(paths.CodexConfigFile, originalExists, originalBytes);
            throw;
        }
    }

    public ManagedFileResult SetEnabled(ProductPaths paths, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.CodexConfigFile))
        {
            throw new FileNotFoundException("Codex config.toml was not found.", paths.CodexConfigFile);
        }

        var originalBytes = File.ReadAllBytes(paths.CodexConfigFile);
        var original = File.ReadAllText(paths.CodexConfigFile, Encoding.UTF8);
        ValidateToml(original, allowEmpty: false);
        var start = original.IndexOf(ProductInfo.ManagedConfigStart, StringComparison.Ordinal);
        var end = original.IndexOf(ProductInfo.ManagedConfigEnd, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("The managed Codex integration section was not found.");
        }

        var blockEnd = end + ProductInfo.ManagedConfigEnd.Length;
        var block = original[start..blockEnd];
        var updatedBlock = EnabledLineRegex().Replace(block, $"enabled = {enabled.ToString().ToLowerInvariant()}", 1);
        if (string.Equals(block, updatedBlock, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The managed enabled setting was not found.");
        }

        var updated = original[..start] + updatedBlock + original[blockEnd..];
        ValidateToml(updated, allowEmpty: false);
        var backup = CreateBackup(paths.CodexConfigFile);
        try
        {
            WriteAtomic(paths.CodexConfigFile, updated);
            return new ManagedFileResult(paths.CodexConfigFile, false, true, backup, enabled ? "enabled" : "disabled");
        }
        catch
        {
            WriteAtomic(paths.CodexConfigFile, originalBytes);
            throw;
        }
    }

    public ManagedFileResult Uninstall(
        ProductPaths paths,
        string? originalBackupPath = null,
        bool fileWasCreatedByProduct = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.CodexConfigFile))
        {
            return new ManagedFileResult(paths.CodexConfigFile, false, false, null, "not-present");
        }

        var originalBytes = File.ReadAllBytes(paths.CodexConfigFile);
        var original = File.ReadAllText(paths.CodexConfigFile, Encoding.UTF8);
        ValidateToml(original, allowEmpty: true);
        var updated = RemoveManagedBlock(original, out var removed);
        if (!removed)
        {
            return new ManagedFileResult(paths.CodexConfigFile, false, false, null, "not-managed");
        }

        ValidateToml(updated, allowEmpty: true);
        var backup = CreateBackup(paths.CodexConfigFile);
        try
        {
            if (TryGetExactOriginalBytes(
                    paths.CodexConfigFile,
                    originalBackupPath,
                    originalBytes,
                    original,
                    out var exactOriginalBytes))
            {
                WriteAtomic(paths.CodexConfigFile, exactOriginalBytes);
                return new ManagedFileResult(
                    paths.CodexConfigFile,
                    false,
                    true,
                    backup,
                    "restored-exact-original");
            }

            if (fileWasCreatedByProduct
                && string.IsNullOrWhiteSpace(updated)
                && IsExactProductManagedRepresentation(originalBytes, original, string.Empty))
            {
                File.Delete(paths.CodexConfigFile);
            }
            else
            {
                WriteAtomic(
                    paths.CodexConfigFile,
                    EncodeUtf8PreservingPreamble(updated, originalBytes));
            }

            return new ManagedFileResult(paths.CodexConfigFile, false, true, backup, "removed");
        }
        catch
        {
            WriteAtomic(paths.CodexConfigFile, originalBytes);
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

        if (result.Created)
        {
            if (File.Exists(result.Path))
            {
                File.Delete(result.Path);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(result.BackupPath) || !File.Exists(result.BackupPath))
        {
            throw new InvalidOperationException("The exact Codex configuration backup required for rollback is unavailable.");
        }

        WriteAtomic(result.Path, File.ReadAllBytes(result.BackupPath));
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
            enabled_tools = ["local_gpu_health", "local_gpu_review"]
            default_tools_approval_mode = "prompt"
            supports_parallel_tool_calls = false
            startup_timeout_sec = 20
            tool_timeout_sec = 360
            env = { THALEN_HELPER_STATE_DIR = "{{state}}", OLLAMA_HOST = "http://127.0.0.1:11434" }

            [mcp_servers.local_gpu_reviewer.tools.local_gpu_health]
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

    private static string EscapeTomlBasicString(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string CreateBackup(string path)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var backup = $"{path}.thalen-helper.{timestamp}.bak";
        File.Copy(path, backup, false);
        return backup;
    }

    private static void RestoreOriginal(string path, bool existed, byte[] content)
    {
        if (existed)
        {
            WriteAtomic(path, content);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void WriteAtomic(string path, string content)
        => WriteAtomic(path, new UTF8Encoding(false).GetBytes(content));

    private static void WriteAtomic(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Managed path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, true);
    }

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

        exactOriginalBytes = File.ReadAllBytes(originalBackupPath!);
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
