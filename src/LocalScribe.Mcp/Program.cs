using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;
using LocalScribe.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// stdio MCP: stdout belongs to the protocol. ALL logging goes to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

string? rootArg = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--storage-root") rootArg = args[i + 1];

// Load the user's real settings (projection behavior must match the App); override
// only the storage root when --storage-root is passed. persistMigration:false - this is a
// read-only server; it must never write-migrate settings.json (could race a running App).
string settingsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "LocalScribe", "settings.json");
var settings = await new SettingsStore(settingsPath).LoadOrDefaultAsync(persistMigration: false, default);
if (rootArg is not null) settings = settings with { StorageRoot = rootArg };

var paths = new StoragePaths(settings.StorageRoot);
var time = TimeProvider.System;
var embeddings = new LazyEmbeddingProvider();
var corpus = new McpCorpus(paths, settings, time,
    new McpConsentStore(paths),
    new McpLexicalCatalog(paths, settings, time),
    new SemanticIndexStore(paths),
    new MatterStore(paths.MattersDir),
    new LocalScribe.Core.Assistant.SummaryStore(paths),
    embeddings);

builder.Services.AddSingleton(corpus);
builder.Services.AddSingleton(new McpAuditLog(paths, time));
builder.Services.AddSingleton<TimeProvider>(time);
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "localscribe", Version =
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" };
    })
    .WithStdioServerTransport()
    .WithTools<LocalScribeTools>();

var host = builder.Build();
await using (embeddings)
    await host.RunAsync();
