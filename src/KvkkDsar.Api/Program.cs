using System.Diagnostics;
using System.Text.Json.Serialization;
using KvkkDsar.Api.Endpoints;
using KvkkDsar.Api.Infrastructure;
using KvkkDsar.Api.Persistence;
using KvkkDsar.Api.Services;
using KvkkDsar.Synthetic;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 32 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPersistence(builder.Configuration);
builder.Services.Configure<DsarOptions>(builder.Configuration.GetSection(DsarOptions.Section));
builder.Services.Configure<EvidenceOptions>(builder.Configuration.GetSection(EvidenceOptions.Section));
builder.Services.AddSingleton<SourceRegistry>();
builder.Services.AddSingleton<SubjectVault>();
builder.Services.AddSingleton<EvidenceSigner>();
builder.Services.AddSingleton<OperatorDirectory>();
builder.Services.AddSingleton<DsarMetrics>();
builder.Services.AddSingleton<DsarService>();
builder.Services.AddSingleton<DeadlineMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeadlineMonitor>());
if (builder.Configuration.GetValue<bool>("Demo:Inbox"))
{
    builder.Services.AddSingleton<IOtpSender, DemoInbox>();
}
else
{
    builder.Services.AddSingleton<IOtpSender, UnconfiguredOtpSender>();
}

builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("workflow-db", tags: [HealthEndpoints.ReadyTag]);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));
builder.Services.AddExceptionHandler<DsarExceptionHandler>();
builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "kvkk-dsar";
    document.Info.Version = "v1";
    document.Info.Description = "KVKK data-subject request workflow: identity verification, inventory-driven discovery, 30-day deadline, approved erasure/anonymisation, signed evidence pack.";
    return Task.CompletedTask;
}));

var app = builder.Build();

// One image, several roles: `--migrate-only` applies the workflow migrations as the schema owner; `--seed-clinic`
// loads the synthetic clinic into the source database as its owner; with no flag the service serves requests.
if (args.Contains("--migrate-only"))
{
    await app.Services.MigrateDatabaseAsync();
    return 0;
}

if (args.Contains("--seed-clinic"))
{
    var people = app.Configuration.GetValue("Seed:People", 2000);
    var owner = app.Configuration["Seed:OwnerConnection"] ?? throw new InvalidOperationException("Seed:OwnerConnection is required for --seed-clinic.");
    await using var ownerDb = PersistenceExtensions.CreateDataSource(owner);
    var dataset = ClinicDataset.Generate(people, app.Configuration.GetValue("Seed:Seed", 2026));
    await ClinicDatabase.LoadAsync(ownerDb, dataset);
    Console.WriteLine($"seeded {people} synthetic people, {dataset.Tables.Values.Sum(t => t.Rows.Count)} rows in {dataset.Tables.Count} tables");
    return 0;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(options =>
    options.GetLevel = (context, _, exception) =>
        exception is null && context.Response.StatusCode < 400 && context.Request.Path.StartsWithSegments("/health")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("kvkk-dsar"));
}

app.MapHealthEndpoints();
app.MapGroup("/api/v1").WithGroupName("v1").MapRequestEndpoints();
if (app.Configuration.GetValue<bool>("Demo:Inbox"))
{
    app.MapDemoInbox();
}

await app.RunAsync();
return 0;

/// <summary>Entry point; public so WebApplicationFactory can boot the app in integration tests.</summary>
public partial class Program;
