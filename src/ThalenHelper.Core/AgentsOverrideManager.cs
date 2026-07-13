using System.Globalization;
using System.Text;

namespace ThalenHelper.Core;

public sealed class AgentsOverrideManager
{
    public ManagedFileResult InstallOrRepair(ProductPaths paths, HardwareTier tier)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var templatePath = Path.Combine(AppContext.BaseDirectory, "templates", "AGENTS.override.md");
        var template = File.ReadAllText(templatePath, Encoding.UTF8)
            .ReplaceLineEndings(Environment.NewLine)
            .Replace("{{TIER_GUIDANCE}}", GetTierGuidance(tier), StringComparison.Ordinal);
        ValidateMarkers(template);
        var originalExists = File.Exists(paths.AgentsOverrideFile);
        var original = originalExists ? File.ReadAllText(paths.AgentsOverrideFile, Encoding.UTF8) : string.Empty;
        var managedSection = ExtractManagedSection(template);
        var baseContent = RemoveManagedSection(original, out var hadManagedSection);
        var merged = originalExists
            ? AppendManagedSection(baseContent, managedSection)
            : template.TrimEnd() + Environment.NewLine;
        if (string.Equals(original, merged, StringComparison.Ordinal))
        {
            return new ManagedFileResult(paths.AgentsOverrideFile, false, false, null, "unchanged");
        }

        Directory.CreateDirectory(paths.CodexHome);
        var backup = originalExists ? CreateBackup(paths.AgentsOverrideFile) : null;
        try
        {
            WriteAtomic(paths.AgentsOverrideFile, merged);
            ValidateMarkers(File.ReadAllText(paths.AgentsOverrideFile, Encoding.UTF8));
            return new ManagedFileResult(
                paths.AgentsOverrideFile,
                !originalExists,
                true,
                backup,
                hadManagedSection ? "updated" : "installed");
        }
        catch
        {
            if (originalExists)
            {
                WriteAtomic(paths.AgentsOverrideFile, original);
            }
            else if (File.Exists(paths.AgentsOverrideFile))
            {
                File.Delete(paths.AgentsOverrideFile);
            }

            throw;
        }
    }

    public ManagedFileResult Uninstall(ProductPaths paths, bool fileWasCreatedByProduct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.AgentsOverrideFile))
        {
            return new ManagedFileResult(paths.AgentsOverrideFile, false, false, null, "not-present");
        }

        var original = File.ReadAllText(paths.AgentsOverrideFile, Encoding.UTF8);
        ValidateMarkers(original, requireMarkers: false);
        var updated = RemoveManagedSection(original, out var removed);
        if (!removed)
        {
            return new ManagedFileResult(paths.AgentsOverrideFile, false, false, null, "not-managed");
        }

        var backup = CreateBackup(paths.AgentsOverrideFile);
        try
        {
            if (fileWasCreatedByProduct && string.IsNullOrWhiteSpace(updated))
            {
                File.Delete(paths.AgentsOverrideFile);
                return new ManagedFileResult(paths.AgentsOverrideFile, false, true, backup, "removed-product-file");
            }

            WriteAtomic(paths.AgentsOverrideFile, updated.TrimEnd() + Environment.NewLine);
            return new ManagedFileResult(
                paths.AgentsOverrideFile,
                false,
                true,
                backup,
                fileWasCreatedByProduct ? "preserved-user-content" : "removed-managed-section");
        }
        catch
        {
            WriteAtomic(paths.AgentsOverrideFile, original);
            throw;
        }
    }

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

    private static string ExtractManagedSection(string content)
    {
        var start = content.IndexOf(ProductInfo.ManagedAgentsStart, StringComparison.Ordinal);
        var end = content.IndexOf(ProductInfo.ManagedAgentsEnd, StringComparison.Ordinal);
        return content[start..(end + ProductInfo.ManagedAgentsEnd.Length)];
    }

    private static string AppendManagedSection(string content, string managed)
    {
        var trimmed = content.TrimEnd();
        return string.IsNullOrEmpty(trimmed)
            ? managed + Environment.NewLine
            : trimmed + Environment.NewLine + Environment.NewLine + managed + Environment.NewLine;
    }

    private static string RemoveManagedSection(string content, out bool removed)
    {
        var start = content.IndexOf(ProductInfo.ManagedAgentsStart, StringComparison.Ordinal);
        var end = content.IndexOf(ProductInfo.ManagedAgentsEnd, StringComparison.Ordinal);
        if (start < 0 && end < 0)
        {
            removed = false;
            return content;
        }

        if (start < 0 || end < start)
        {
            throw new InvalidDataException("The managed AGENTS.override.md markers are malformed.");
        }

        if (content.IndexOf(ProductInfo.ManagedAgentsStart, start + ProductInfo.ManagedAgentsStart.Length, StringComparison.Ordinal) >= 0
            || content.IndexOf(ProductInfo.ManagedAgentsEnd, end + ProductInfo.ManagedAgentsEnd.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidDataException("Multiple managed AGENTS.override.md sections were found.");
        }

        var removeEnd = end + ProductInfo.ManagedAgentsEnd.Length;
        while (removeEnd < content.Length && content[removeEnd] is '\r' or '\n')
        {
            removeEnd++;
        }

        var before = content[..start].TrimEnd();
        var after = content[removeEnd..].TrimStart();
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

    private static void ValidateMarkers(string content, bool requireMarkers = true)
    {
        var starts = Count(content, ProductInfo.ManagedAgentsStart);
        var ends = Count(content, ProductInfo.ManagedAgentsEnd);
        if ((requireMarkers && (starts != 1 || ends != 1)) || (!requireMarkers && starts != ends))
        {
            throw new InvalidDataException("Invalid managed AGENTS.override.md markers.");
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

    private static string CreateBackup(string path)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var backup = $"{path}.thalen-helper.{timestamp}.bak";
        File.Copy(path, backup, false);
        return backup;
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}
