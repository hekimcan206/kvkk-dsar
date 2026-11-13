using System.Security.Cryptography;
using System.Text;

namespace KvkkDsar.Api.Infrastructure;

internal static class Roles
{
    public const string Intake = "intake";
    public const string Verifier = "verifier";
    public const string Dpo = "dpo";
    public const string Approver = "approver";
    public const string Executor = "executor";
    public const string Auditor = "auditor";
}

internal sealed class OperatorOptions
{
    public string Name { get; set; } = "";

    /// <summary>SHA-256 (hex) of the operator's API key; the key itself is never configured on the server.</summary>
    public string KeySha256 { get; set; } = "";

    public List<string> Roles { get; set; } = [];
}

internal sealed record Operator(string Name, IReadOnlySet<string> Roles);

/// <summary>
/// Who is acting. Each operator has an API key (only its hash is configured) and roles. The workflow records the
/// operator's name on every step, which is what makes four-eyes approval enforceable. A deliberately small scheme
/// for the demo; production would put OIDC in front and map groups to the same roles.
/// </summary>
internal sealed class OperatorDirectory
{
    private readonly List<(byte[] Hash, Operator Operator)> _operators;

    public OperatorDirectory(IConfiguration configuration)
    {
        _operators = [.. (configuration.GetSection("Operators").Get<List<OperatorOptions>>() ?? [])
            .Where(o => o.Name.Length > 0 && o.KeySha256.Length == 64)
            .Select(o => (Convert.FromHexString(o.KeySha256), new Operator(o.Name, o.Roles.ToHashSet(StringComparer.OrdinalIgnoreCase))))];
    }

    public Operator? Find(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        Operator? match = null;
        foreach (var (expected, op) in _operators)
        {
            // Compare against every entry in constant time; no early exit on a match.
            if (CryptographicOperations.FixedTimeEquals(expected, hash))
            {
                match = op;
            }
        }

        return match;
    }
}

internal static class OperatorEndpointExtensions
{
    public const string HeaderName = "X-Api-Key";
    private const string ItemKey = "dsar.operator";

    /// <summary>Requires an operator with one of the roles; 401 without a valid key, 403 without the role.</summary>
    public static TBuilder RequireRole<TBuilder>(this TBuilder builder, params string[] roles) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var directory = context.HttpContext.RequestServices.GetRequiredService<OperatorDirectory>();
            var op = directory.Find(context.HttpContext.Request.Headers[HeaderName]);
            if (op is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "A valid X-Api-Key is required.");
            }

            if (!roles.Any(op.Roles.Contains))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: $"Requires one of the roles: {string.Join(", ", roles)}.");
            }

            context.HttpContext.Items[ItemKey] = op;
            return await next(context);
        });

    public static Operator Operator(this HttpContext context) =>
        context.Items[ItemKey] as Operator ?? throw new InvalidOperationException("Endpoint is missing RequireRole.");
}
