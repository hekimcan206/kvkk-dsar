using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KvkkDsar.Api.Persistence;
using KvkkDsar.Api.Services;
using KvkkDsar.Catalog;
using KvkkDsar.Discovery;
using KvkkDsar.Inventory;
using KvkkDsar.Synthetic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(KvkkDsar.IntegrationTests.Infrastructure.DsarEnvironment))]

namespace KvkkDsar.IntegrationTests.Infrastructure;

/// <summary>Arms a crash between "source committed" and "workflow recorded it".</summary>
public sealed class ArmableFaults : IDsarFaults
{
    private volatile bool _crash;

    public bool CrashAfterSourceCommit { get => _crash; set => _crash = value; }

    void IDsarFaults.AfterSourceCommit(string source)
    {
        if (CrashAfterSourceCommit)
        {
            CrashAfterSourceCommit = false;
            throw new InvalidOperationException("simulated crash after the source transaction committed");
        }
    }
}

/// <summary>
/// PostgreSQL 17 initialised with the compose init script (five roles, two databases), the workflow schema migrated as
/// <c>dsar_migrator</c>, and the synthetic clinic loaded as <c>clinic_owner</c>. A pristine copy of the clinic is kept
/// as a template database, so tests that erase data get their own clone. The API runs in-process with the demo inbox,
/// six operators and the committed inventory and policy.
/// </summary>
public sealed class DsarEnvironment : IAsyncLifetime
{
    public const int People = 400;
    public const int Seed = 11;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly Dictionary<string, string> Passwords = new()
    {
        ["MIGRATOR_PASSWORD"] = "migrator-test",
        ["APP_PASSWORD"] = "app-test",
        ["CLINIC_OWNER_PASSWORD"] = "owner-test",
        ["FINDER_PASSWORD"] = "finder-test",
        ["ERASER_PASSWORD"] = "eraser-test",
    };

    public static readonly (string Name, string Key, string[] Roles)[] Operators =
    [
        ("ayse.intake", "key-intake", ["intake"]),
        ("veli.verifier", "key-verifier", ["verifier"]),
        ("deniz.dpo", "key-dpo", ["dpo", "intake"]),
        ("zeynep.legal", "key-legal", ["approver"]),
        ("emre.ops", "key-ops", ["executor"]),
        ("mert.dpo2", "key-dpo2", ["dpo", "approver"]),
        ("selin.audit", "key-audit", ["auditor"]),
    ];

    private readonly PostgreSqlContainer _postgres;
    private ApiFactory? _api;
    private int _clones;

    public DsarEnvironment()
    {
        var builder = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("dsar")
            .WithResourceMapping(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "postgres-init", "01-roles.sh")), "/docker-entrypoint-initdb.d/01-roles.sh");
        foreach (var (k, v) in Passwords)
        {
            builder = builder.WithEnvironment(k, v);
        }

        _postgres = builder.Build();
    }

    public ClinicDataset Dataset { get; private set; } = null!;

    public DataInventory Inventory { get; } = DataInventory.Load(Path.Combine(AppContext.BaseDirectory, "samples", "clinic.classification.json"));

    public DsarPolicy Policy { get; } = DsarPolicy.Load(Path.Combine(AppContext.BaseDirectory, "samples", "dsar-policy.json"));

    public ArmableFaults Faults { get; } = new();

    public NpgsqlDataSource Finder { get; private set; } = null!;

    public NpgsqlDataSource WorkflowApp { get; private set; } = null!;

    public SourceCatalog Catalog { get; private set; } = null!;

    public string Connection(string user, string password, string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Username = user,
            Password = password,
            Database = database,
            MaxPoolSize = 30,
            Timeout = 60, // the host is shared with other projects' containers
        }.ConnectionString;

    public string Superuser(string database = "dsar") => new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;

    public string Owner(string database = "clinic") => Connection("clinic_owner", "owner-test", database);

    public string FinderConnection(string database = "clinic") => Connection("dsar_finder", "finder-test", database);

    public string EraserConnection(string database = "clinic") => Connection("dsar_eraser", "eraser-test", database);

    public string AppConnection => Connection("dsar_app", "app-test", "dsar");

    public string MigratorConnection => Connection("dsar_migrator", "migrator-test", "dsar");

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await using (var migrator = PersistenceExtensions.CreateDataSource(MigratorConnection))
        {
            await new MigrationRunner(migrator, NullLogger<MigrationRunner>.Instance).MigrateAsync();
        }

        Dataset = ClinicDataset.Generate(People, Seed);
        await using (var owner = PersistenceExtensions.CreateDataSource(Owner()))
        {
            await ClinicDatabase.LoadAsync(owner, Dataset);
        }

        // Template for per-test clones; must be created before anything else connects to "clinic".
        NpgsqlConnection.ClearAllPools();
        await ExecAsync(Superuser(), "CREATE DATABASE clinic_tpl TEMPLATE clinic");

        Finder = PersistenceExtensions.CreateDataSource(FinderConnection());
        WorkflowApp = PersistenceExtensions.CreateDataSource(AppConnection);
        Catalog = await Postgres.PostgresCatalogReader.ReadAsync(Finder, ClinicDataset.Schema);

        _api = new ApiFactory(this);
        using var warmup = Client("key-dpo");
        (await warmup.GetAsync(new Uri("health/ready", UriKind.Relative))).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        if (Finder is not null)
        {
            await Finder.DisposeAsync();
        }

        if (WorkflowApp is not null)
        {
            await WorkflowApp.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    public DiscoveryEngine Engine() => DiscoveryEngine.Create(Inventory, Policy, Catalog);

    /// <summary>A private copy of the pristine clinic database for a test that changes data.</summary>
    public async Task<string> CloneClinicAsync()
    {
        var name = $"clinic_t{Interlocked.Increment(ref _clones)}";
        await ExecAsync(Superuser(), $"CREATE DATABASE {name} TEMPLATE clinic_tpl");
        return name;
    }

    public HttpClient Client(string apiKey)
    {
        var client = _api!.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    public IServiceProvider Services => _api!.Services;

    public static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode}: {body}");
        }

        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    private sealed class ApiFactory(DsarEnvironment env) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Workflow", env.AppConnection);
            builder.UseSetting("Dsar:FingerprintKey", Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()));
            builder.UseSetting("Dsar:OtpPepper", Convert.ToBase64String(Enumerable.Repeat((byte)8, 32).ToArray()));
            builder.UseSetting("Dsar:SubjectKey", Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
            builder.UseSetting("Dsar:DeadlineScanSeconds", "3600");
            builder.UseSetting("Demo:Inbox", "true");
            builder.UseSetting("Sources:0:Name", "clinic");
            builder.UseSetting("Sources:0:Schema", "clinic");
            builder.UseSetting("Sources:0:FinderConnection", env.FinderConnection());
            builder.UseSetting("Sources:0:EraserConnection", env.EraserConnection());
            builder.UseSetting("Sources:0:Inventory", "samples/clinic.classification.json");
            builder.UseSetting("Sources:0:Policy", "samples/dsar-policy.json");
            for (var i = 0; i < Operators.Length; i++)
            {
                builder.UseSetting($"Operators:{i}:Name", Operators[i].Name);
                builder.UseSetting($"Operators:{i}:KeySha256", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Operators[i].Key))));
                for (var r = 0; r < Operators[i].Roles.Length; r++)
                {
                    builder.UseSetting($"Operators:{i}:Roles:{r}", Operators[i].Roles[r]);
                }
            }

            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDsarFaults>();
                services.AddSingleton<IDsarFaults>(env.Faults);
            });
        }
    }
}

internal static class HttpExtensions
{
    public static Task<HttpResponseMessage> PostJson(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(new Uri(url, UriKind.Relative), body, DsarEnvironment.Json);

    public static Task<HttpResponseMessage> PostEmpty(this HttpClient client, string url) =>
        client.PostAsync(new Uri(url, UriKind.Relative), null);

    public static Task<HttpResponseMessage> Get(this HttpClient client, string url) =>
        client.GetAsync(new Uri(url, UriKind.Relative));
}
