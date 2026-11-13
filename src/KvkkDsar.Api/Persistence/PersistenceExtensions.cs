using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace KvkkDsar.Api.Persistence;

internal static class PersistenceExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Workflow")
            ?? throw new InvalidOperationException("Connection string 'Workflow' is not configured.");
        services.AddSingleton(_ => CreateDataSource(connectionString));
        services.AddSingleton<WorkflowStore>();
        services.AddSingleton<MigrationRunner>();
        return services;
    }

    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);

        // Request rows are locked for one short transaction per step; lock_timeout turns a stuck lock into a retryable
        // 503 and the idle timeout stops a stalled client from holding a request (or a source row) forever.
        builder.ConnectionStringBuilder.Options ??= "-c lock_timeout=5000 -c idle_in_transaction_session_timeout=60000";

        // No Kerberos here; skipping GSS negotiation also stops Npgsql probing for libgssapi, which chiseled images lack.
        builder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder.Build();
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default) =>
        await services.GetRequiredService<MigrationRunner>().MigrateAsync(cancellationToken);
}

/// <summary>Readiness: workflow database reachable within the probe timeout.</summary>
internal sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("Database unavailable.", ex);
        }
    }
}
