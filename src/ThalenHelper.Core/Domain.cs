using System.Text.Json.Serialization;

namespace ThalenHelper.Core;

public enum GpuVendor
{
    Nvidia,
    Amd,
    Intel,
    Other
}

public enum AccelerationRoute
{
    None,
    NvidiaCuda,
    AmdRocm,
    AmdVulkan,
    IntelVulkan,
    Cpu,
    Unknown
}

public enum StorageMediaType
{
    Nvme,
    Ssd,
    Hdd,
    Unknown,
    Removable,
    Network
}

public enum HardwareTier
{
    Unsupported,
    NoModel,
    Entry,
    Mid,
    High,
    Enthusiast
}

public enum HelperAvailability
{
    Enabled,
    Paused,
    Disabled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelSelectionMode
{
    Pinned,
    Automatic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewTaskKind
{
    Auto,
    General,
    LogTriage,
    TestFailure,
    DiffReview,
    RepositoryAnalysis,
    EdgeCases
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewEffort
{
    Auto,
    Quick,
    Standard,
    Deep
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewBusyBehavior
{
    Skip,
    Queue
}

public sealed record OperatingSystemInfo(
    string ProductName,
    Version Version,
    string Architecture,
    bool IsSupported,
    string? Warning);

public sealed record CpuInfo(
    string Vendor,
    string Model,
    int PhysicalCores,
    int LogicalCores,
    bool SupportsAvx,
    bool SupportsAvx2,
    bool SupportsFma);

public sealed record MemoryInfo(ulong TotalBytes, ulong AvailableBytes);

public sealed record GpuInfo(
    GpuVendor Vendor,
    string Name,
    ulong DedicatedMemoryBytes,
    ulong SharedMemoryBytes,
    ulong? AvailableDedicatedMemoryBytes,
    string? DriverVersion,
    string? ComputeCapability,
    AccelerationRoute AccelerationRoute,
    bool IsIntegrated);

public sealed record StorageVolume(
    string RootPath,
    string FileSystem,
    ulong TotalBytes,
    ulong FreeBytes,
    StorageMediaType MediaType,
    bool IsFixed,
    bool IsSystem,
    bool IsEncrypted,
    bool IsSuitable,
    string? RejectionReason);

public sealed record HardwareProfile(
    OperatingSystemInfo OperatingSystem,
    CpuInfo Cpu,
    MemoryInfo Memory,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<StorageVolume> Volumes,
    bool IsLaptop,
    IReadOnlyList<string> Warnings);

public sealed record ModelManifest(
    int SchemaVersion,
    string CatalogVersion,
    DateOnly VerifiedOn,
    IReadOnlyList<ModelCatalogEntry> Models);

public sealed record ModelCatalogEntry(
    string Provider,
    string Tag,
    string? ExpectedDigest,
    ulong ExpectedDownloadBytes,
    string Family,
    decimal ParameterBillions,
    string IntendedTasks,
    decimal MinimumDedicatedVramGiB,
    decimal RecommendedDedicatedVramGiB,
    decimal MinimumSystemRamGiB,
    decimal RecommendedSystemRamGiB,
    decimal MinimumFreeDiskGiB,
    int SafeDefaultContextTokens,
    int MaximumContextTokens,
    string PerformanceTier,
    bool CpuFallbackReasonable,
    bool AutomaticSelectionAllowed,
    string LicenseName,
    string LicenseUrl,
    bool CommercialUseAllowed,
    string SourceUrl,
    DateOnly VerifiedOn);

public sealed record ModelRecommendation(
    HardwareTier HardwareTier,
    ModelCatalogEntry? Model,
    int ContextTokens,
    bool LowImpactMode,
    bool RequiresCpuOptIn,
    string Explanation,
    IReadOnlyList<string> Warnings);

public sealed record StorageRecommendation(
    StorageVolume? Volume,
    string? ModelDirectory,
    ulong RequiredBytes,
    ulong RemainingBytes,
    string Explanation,
    IReadOnlyList<string> Warnings);

public sealed record HelperPreferences(
    bool LowImpactMode = true,
    bool KeepWarm = false,
    bool AutoStartOllama = true,
    int IdleUnloadSeconds = 0,
    int MaximumInputCharacters = 110_000,
    int MaximumOutputTokens = 1_024,
    ModelSelectionMode ModelSelectionMode = ModelSelectionMode.Pinned,
    int VramReserveMiB = 2_048,
    int QuickContextTokens = 8_192,
    int StandardContextTokens = 16_384,
    int DeepContextTokens = 65_536,
    int MaximumContextTokens = 131_072,
    bool PreferQ4Quantization = true,
    bool PreferFlashAttention = true,
    string KeyCacheQuantization = "Q8_0",
    string ValueCacheQuantization = "Q8_0",
    bool PreferGpuKvCache = true,
    bool PreferModelProvidedChatTemplate = true,
    bool AllowCpuMoeOffloadWhenSupported = true,
    bool AllowExperimentalRuntimeOverrides = false);

public sealed record AccelerationResult(
    string Processor,
    ulong? SizeVramBytes,
    int? ContextLength,
    DateTimeOffset? ExpiresAt);

public sealed record InstallationState
{
    public int SchemaVersion { get; init; } = 1;
    public string ProductVersion { get; init; } = ProductInfo.Version;
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;
    public List<string> FilesCreated { get; init; } = [];
    public List<string> FilesModified { get; init; } = [];
    public Dictionary<string, string> BackupLocations { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ManagedConfigurationSections { get; init; } = [];
    public Dictionary<string, string?> PreviousUserEnvironment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool OllamaWasPreExisting { get; set; }
    public bool StartupEntryOwnedByHelper { get; set; }
    public bool ExistingIntegrationPreserved { get; set; }
    public bool ReliabilityBaselineInstalled { get; set; }
    public string? SelectedModel { get; set; }
    public string? SelectedModelDigest { get; set; }
    public bool SelectedModelOwnedByHelper { get; set; }
    public string? ModelStorageLocation { get; set; }
    public string? ManagedCodexHome { get; set; }
    public HardwareTier HardwareTier { get; set; } = HardwareTier.NoModel;
    public AccelerationResult? Acceleration { get; set; }
    public HelperPreferences Preferences { get; set; } = new();
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HelperAvailability Availability { get; set; } = HelperAvailability.Disabled;
    public DateTimeOffset? LastHealthCheckAt { get; set; }
    public string? LastHealthCheckCode { get; set; }
}

public sealed record OllamaModelInfo(
    string Name,
    string? Digest,
    ulong? SizeBytes,
    string? Family,
    string? ParameterSize,
    string? QuantizationLevel);

public sealed record OllamaRunningModel(
    string Name,
    string? Digest,
    ulong? SizeBytes,
    ulong? SizeVramBytes,
    int? ContextLength,
    DateTimeOffset? ExpiresAt);

public sealed record ReviewRequest(
    string Assignment,
    string? Context = null,
    string? Focus = null,
    int? MaximumOutputTokens = null,
    ReviewBusyBehavior BusyBehavior = ReviewBusyBehavior.Skip,
    int QueueTimeoutSeconds = 30,
    ReviewTaskKind TaskKind = ReviewTaskKind.Auto,
    ReviewEffort Effort = ReviewEffort.Auto,
    bool GpuIntensiveWorkloadActive = false,
    int? DesiredContextTokens = null,
    int? EstimatedInputCharacters = null);

public sealed record ModelRouteDecision(
    bool Allowed,
    ModelSelectionMode SelectionMode,
    ReviewTaskKind TaskKind,
    ReviewEffort Effort,
    string? Model,
    string? ExpectedDigest,
    HardwareTier HardwareTier,
    int ContextTokens,
    string Reason,
    IReadOnlyList<string> Warnings,
    ReviewerTuningPlan Tuning);

public sealed record ReviewerTuningPlan(
    string Quantization,
    int ContextTokens,
    bool FlashAttentionPreferred,
    string KeyCacheQuantization,
    string ValueCacheQuantization,
    int VramReserveMiB,
    string GpuOffload,
    string MoePlacement,
    string KvCacheLocation,
    string ChatTemplate,
    string ProviderControl,
    IReadOnlyList<string> ExperimentalOverrides);

public sealed record ReviewerPlanResult
{
    public string IntegrationName { get; init; } = ProductInfo.IntegrationName;
    public string IntegrationType { get; init; } = "read-only local stdio MCP reviewer";
    public string Provider { get; init; } = "Ollama";
    public bool Passive { get; init; } = true;
    public bool ModelRan { get; init; }
    public bool Allowed { get; init; }
    public ModelSelectionMode SelectionMode { get; init; }
    public ReviewTaskKind TaskKind { get; init; }
    public ReviewEffort Effort { get; init; }
    public string? Model { get; init; }
    public string? HardwareTier { get; init; }
    public int ContextTokens { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public ReviewerTuningPlan? Tuning { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record ReviewerResult
{
    public string IntegrationName { get; init; } = ProductInfo.IntegrationName;
    public string IntegrationType { get; init; } = "read-only local stdio MCP reviewer";
    public string Provider { get; init; } = "Ollama";
    public string? Model { get; init; }
    public string? HardwareTier { get; init; }
    public ModelSelectionMode SelectionMode { get; init; }
    public ReviewTaskKind TaskKind { get; init; }
    public ReviewEffort Effort { get; init; }
    public int ContextTokens { get; init; }
    public string? SelectionReason { get; init; }
    public ReviewerTuningPlan? Tuning { get; init; }
    public string? BoundedAssignment { get; init; }
    public string? Findings { get; init; }
    public IReadOnlyList<string> ConfirmedObservations { get; init; } = [];
    public IReadOnlyList<string> Hypotheses { get; init; } = [];
    public long ElapsedMilliseconds { get; init; }
    public IReadOnlyDictionary<string, object?> PerformanceMetadata { get; init; } =
        new Dictionary<string, object?>();
    public bool ModelRan { get; init; }
    public bool Paused { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record ReviewerHealthResult
{
    public string IntegrationName { get; init; } = ProductInfo.IntegrationName;
    public string IntegrationType { get; init; } = "read-only local stdio MCP reviewer";
    public string Provider { get; init; } = "Ollama";
    public string? Model { get; init; }
    public string? HardwareTier { get; init; }
    public ModelSelectionMode SelectionMode { get; init; }
    public int EligibleInstalledModels { get; init; }
    public bool EndpointReachable { get; init; }
    public bool ModelAvailable { get; init; }
    public bool ModelLoaded { get; init; }
    public bool ModelRan { get; init; }
    public bool Paused { get; init; }
    public HelperAvailability Availability { get; init; }
    public string Endpoint { get; init; } = "http://127.0.0.1:11434";
    public AccelerationResult? Acceleration { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record OllamaGenerationResult(
    string Response,
    string? Model,
    bool Done,
    long TotalDurationNanoseconds,
    long LoadDurationNanoseconds,
    int PromptEvalCount,
    int EvalCount,
    long EvalDurationNanoseconds);

public sealed record OllamaGenerationSpec(
    string Model,
    string Prompt,
    int ContextTokens,
    int OutputTokens,
    TimeSpan KeepAlive);

public sealed record OllamaRoutedGenerationResult(
    OllamaGenerationSpec Spec,
    OllamaGenerationResult Generation);

public static class ProductInfo
{
    public const string Name = "Codex GPU Thalen Helper";
    public const string Version = "0.1.0-beta.5";
    public const string IntegrationName = "local_gpu_reviewer";
    public const string ManagedConfigStart = "# BEGIN CODEX GPU THALEN HELPER (managed)";
    public const string ManagedConfigEnd = "# END CODEX GPU THALEN HELPER (managed)";
    public const string ManagedAgentsStart = "<!-- BEGIN CODEX GPU THALEN HELPER (managed) -->";
    public const string ManagedAgentsEnd = "<!-- END CODEX GPU THALEN HELPER (managed) -->";
    public const string ManagedReliabilityStart = "<!-- BEGIN CODEX RELIABILITY BASELINE (managed by Codex GPU Thalen Helper) -->";
    public const string ManagedReliabilityEnd = "<!-- END CODEX RELIABILITY BASELINE (managed by Codex GPU Thalen Helper) -->";
}
