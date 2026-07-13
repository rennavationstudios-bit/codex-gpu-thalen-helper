namespace ThalenHelper.Core;

public sealed record ManagedFileResult(
    string Path,
    bool Created,
    bool Changed,
    string? BackupPath,
    string Operation,
    string? Warning = null)
{
    internal byte[]? AppliedBytes { get; init; }
}
