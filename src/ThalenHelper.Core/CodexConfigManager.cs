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
        var original = originalExists ? File.ReadAllText(paths.CodexConfigFile, Encoding.UTF8) : string.Empty;
        ValidateToml(original, allowEmpty: true);

        var withoutManaged = RemoveManagedBlock(original, out var hadManagedBlock);
        if (!hadManagedBlock && ExistingIntegrationRegex().IsMatch(withoutManaged))
        {
            throw new InvalidOperationException(
                "An unmanaged mcp_servers.local_gpu_reviewer table already exists. It was not overwritten.");
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
            RestoreOriginal(paths.CodexConfigFile, originalExists, original);
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
            WriteAtomic(paths.CodexConfigFile, original);
            throw;
        }
    }

    public ManagedFileResult Uninstall(ProductPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.CodexConfigFile))
        {
            return new ManagedFileResult(paths.CodexConfigFile, false, false, null, "not-present");
        }

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
            if (string.IsNullOrWhiteSpace(updated))
            {
                File.Delete(paths.CodexConfigFile);
            }
            else
            {
                WriteAtomic(paths.CodexConfigFile, updated);
            }

            return new ManagedFileResult(paths.CodexConfigFile, false, true, backup, "removed");
        }
        catch
        {
            WriteAtomic(paths.CodexConfigFile, original);
            throw;
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

        var removeEnd = end + ProductInfo.ManagedConfigEnd.Length;
        while (removeEnd < content.Length && content[removeEnd] is '\r' or '\n')
        {
            removeEnd++;
        }

        var before = content[..start].TrimEnd('\r', '\n', ' ', '\t');
        var after = content[removeEnd..].TrimStart('\r', '\n');
        removed = true;
        if (string.IsNullOrEmpty(before))
        {
            return after;
        }

        if (string.IsNullOrEmpty(after))
        {
            return before + Environment.NewLine;
        }

        return before + Environment.NewLine + Environment.NewLine + after;
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

    private static void RestoreOriginal(string path, bool existed, string content)
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
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Managed path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    [GeneratedRegex("(?m)^\\s*\\[\\s*mcp_servers\\.local_gpu_reviewer\\s*\\]\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ExistingIntegrationRegex();

    [GeneratedRegex("(?m)^enabled\\s*=\\s*(true|false)\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnabledLineRegex();
}
