using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using ThalenHelper.Core;

var builder = Host.CreateEmptyApplicationBuilder(settings: null);
var paths = ProductPaths.Resolve(installDirectory: AppContext.BaseDirectory);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new StateStore(paths.StateFile));
// OllamaClient normalizes any configured loopback host to 127.0.0.1 and rejects
// non-loopback endpoints before a request can be sent. The managed Codex block
// pins OLLAMA_HOST to the default loopback port; tests may use another loopback port.
builder.Services.AddSingleton(_ => new OllamaClient());
builder.Services.AddSingleton<ReviewerService>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = ProductInfo.IntegrationName,
            Version = ProductInfo.Version
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync().ConfigureAwait(false);

[McpServerToolType]
public static class LocalGpuReviewerTools
{
    [McpServerTool(
        Name = "local_gpu_health",
        Title = "Local GPU reviewer health",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Passively reports the local reviewer, task-aware provider/model pool, eligible loopback endpoints, and load state. This tool never runs inference; local_gpu_plan remains authoritative for a specific task route.")]
    public static async Task<ReviewerHealthResult> HealthAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var reviewer = services.GetRequiredService<ReviewerService>();
        return await reviewer.GetHealthAsync(cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "local_gpu_plan",
        Title = "Plan a local GPU review",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Passively selects the safest installed audited model and context for a proposed bounded task. It never loads or runs a model and never downloads one.")]
    public static async Task<ReviewerPlanResult> PlanAsync(
        IServiceProvider services,
        [Description("A short description of the proposed bounded review task.")] string assignment,
        [Description("Task category; auto uses deterministic assignment cues, then falls back conservatively to the supplied size estimate.")] ReviewTaskKind taskKind = ReviewTaskKind.Auto,
        [Description("Requested review depth; auto chooses from task type and estimated size.")] ReviewEffort effort = ReviewEffort.Auto,
        [Description("Estimated total characters Codex would explicitly supply to the reviewer, from 0 through 110,000.")] int? estimatedInputCharacters = null,
        [Description("Set true while an emulator, graphics build, rendering, video, game, or other GPU-heavy workload is active.")] bool gpuIntensiveWorkloadActive = false,
        [Description("Optional desired context from 512 through 131,072 tokens; the router caps it to the model and policy maximum.")] int? desiredContextTokens = null,
        CancellationToken cancellationToken = default)
    {
        var reviewer = services.GetRequiredService<ReviewerService>();
        try
        {
            return await reviewer.PlanAsync(
                new ReviewRequest(
                    Assignment: assignment,
                    TaskKind: taskKind,
                    Effort: effort,
                    GpuIntensiveWorkloadActive: gpuIntensiveWorkloadActive,
                    DesiredContextTokens: desiredContextTokens,
                    EstimatedInputCharacters: estimatedInputCharacters),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return new ReviewerPlanResult
            {
                Allowed = false,
                ErrorCode = "INVALID_INPUT",
                ErrorMessage = exception.Message.Length <= 240 ? exception.Message : exception.Message[..240]
            };
        }
    }

    [McpServerTool(
        Name = "local_gpu_review",
        Title = "Bounded local GPU review",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Runs one bounded advisory review using only explicitly supplied text. The result preserves the original findings text, adds shape-validated advisory structuredFindings, and reports structuredFindingsStatus for parsed, partially rejected, malformed, or not-run output. It cannot read files, use a shell, edit code, or make external changes.")]
    public static async Task<ReviewerResult> ReviewAsync(
        IServiceProvider services,
        [Description("A precise bounded assignment, up to 12,000 characters.")] string assignment,
        [Description("Optional explicitly supplied context, up to 96,000 characters.")] string? context = null,
        [Description("Optional focus, up to 2,000 characters.")] string? focus = null,
        [Description("Optional response token limit from 64 through 2,048.")] int? maximumOutputTokens = null,
        [Description("When another review is active, skip immediately (default) or enter a bounded queue.")] ReviewBusyBehavior busyBehavior = ReviewBusyBehavior.Skip,
        [Description("Bounded queue timeout from 1 through 120 seconds when busyBehavior is queue.")] int queueTimeoutSeconds = 30,
        [Description("Task category; auto uses deterministic focus and assignment cues, then falls back conservatively to supplied text size.")] ReviewTaskKind taskKind = ReviewTaskKind.Auto,
        [Description("Requested review depth; auto chooses from task type and supplied text size.")] ReviewEffort effort = ReviewEffort.Auto,
        [Description("Set true while an emulator, graphics build, rendering, video, game, or other GPU-heavy workload is active.")] bool gpuIntensiveWorkloadActive = false,
        [Description("Optional desired context from 512 through 131,072 tokens; the router caps it to the model and policy maximum.")] int? desiredContextTokens = null,
        CancellationToken cancellationToken = default)
    {
        var reviewer = services.GetRequiredService<ReviewerService>();
        try
        {
            return await reviewer.ReviewAsync(
                new ReviewRequest(
                    Assignment: assignment,
                    Context: context,
                    Focus: focus,
                    MaximumOutputTokens: maximumOutputTokens,
                    BusyBehavior: busyBehavior,
                    QueueTimeoutSeconds: queueTimeoutSeconds,
                    TaskKind: taskKind,
                    Effort: effort,
                    GpuIntensiveWorkloadActive: gpuIntensiveWorkloadActive,
                    DesiredContextTokens: desiredContextTokens),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return new ReviewerResult
            {
                ModelRan = false,
                Paused = false,
                ErrorCode = "INVALID_INPUT",
                ErrorMessage = exception.Message.Length <= 240 ? exception.Message : exception.Message[..240]
            };
        }
    }
}
