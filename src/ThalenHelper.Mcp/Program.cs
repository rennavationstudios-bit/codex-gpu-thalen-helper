using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using ThalenHelper.Core;

var builder = Host.CreateEmptyApplicationBuilder(settings: null);
var paths = ProductPaths.Resolve();
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new StateStore(paths.StateFile));
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
    [Description("Passively reports the local reviewer, selected model, Ollama endpoint, and load state. This tool never runs inference.")]
    public static async Task<ReviewerHealthResult> HealthAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var reviewer = services.GetRequiredService<ReviewerService>();
        return await reviewer.GetHealthAsync(cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "local_gpu_review",
        Title = "Bounded local GPU review",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Runs one bounded advisory review using only explicitly supplied text. It cannot read files, use a shell, edit code, or make external changes.")]
    public static async Task<ReviewerResult> ReviewAsync(
        IServiceProvider services,
        [Description("A precise bounded assignment, up to 12,000 characters.")] string assignment,
        [Description("Optional explicitly supplied context, up to 96,000 characters.")] string? context = null,
        [Description("Optional focus, up to 2,000 characters.")] string? focus = null,
        [Description("Optional response token limit from 64 through 2,048.")] int? maximumOutputTokens = null,
        [Description("When another review is active, skip immediately (default) or enter a bounded queue.")] ReviewBusyBehavior busyBehavior = ReviewBusyBehavior.Skip,
        [Description("Bounded queue timeout from 1 through 120 seconds when busyBehavior is queue.")] int queueTimeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var reviewer = services.GetRequiredService<ReviewerService>();
        try
        {
            return await reviewer.ReviewAsync(
                new ReviewRequest(
                    assignment,
                    context,
                    focus,
                    maximumOutputTokens,
                    busyBehavior,
                    queueTimeoutSeconds),
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
