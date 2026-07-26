namespace fakebookGateway.Gateway;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    public string FusionArchivePath { get; set; } = "gateway.far";
    public string AuthenticationGraphQLEndpoint { get; set; } = "http://localhost:1001/graphql";
    public string InternalSharedSecret { get; set; } = string.Empty;
    public SubgraphSecretsOptions SubgraphSecrets { get; set; } = new();
    public int SessionCacheSeconds { get; set; } = 30;
    public int InvalidSessionCacheSeconds { get; set; } = 2;
    public int AuthSessionValidationTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Maximum accepted request body size (bytes) for the public GraphQL endpoint.
    /// Guards against oversized-query memory/CPU amplification. Default 2 MiB — far
    /// above any legitimate GraphQL JSON payload, well below the Kestrel 30 MB default.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 2 * 1024 * 1024;

    public GatewayRateLimitOptions RateLimit { get; set; } = new();
    public GatewayGraphQlSecurityOptions GraphQLSecurity { get; set; } = new();
    public string[] TrustedProxyNetworks { get; set; } = ["127.0.0.0/8", "::1/128"];
    public string RefreshTokenCookieName { get; set; } = "fb_refresh";
    // Matches Auth:RefreshTokenCookiePath. The refresh cookie is only ever read for /graphql
    // requests, so a wider scope only exposes it to other services on the same edge origin.
    public string RefreshTokenCookiePath { get; set; } = "/graphql";
    public SameSiteMode RefreshTokenCookieSameSite { get; set; } = SameSiteMode.Lax;
    public string[] AllowedOrigins { get; set; } =
    [
        "http://localhost:3001",
        "http://localhost:5173",
        "http://localhost:5174"
    ];

    public string ResolveSubgraphSecret(string subgraphName)
    {
        var configured = subgraphName switch
        {
            GatewaySubgraphs.Authentication => SubgraphSecrets.Authentication,
            GatewaySubgraphs.SocialGraph => SubgraphSecrets.SocialGraph,
            GatewaySubgraphs.Recommendation => SubgraphSecrets.Recommendation,
            GatewaySubgraphs.Search => SubgraphSecrets.Search,
            GatewaySubgraphs.Messaging => SubgraphSecrets.Messaging,
            GatewaySubgraphs.Notification => SubgraphSecrets.Notification,
            GatewaySubgraphs.Payment => SubgraphSecrets.Payment,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"No dedicated Gateway secret is configured for subgraph '{subgraphName}'.");
        }

        return configured;
    }

    public bool HasDedicatedDistinctSubgraphSecrets()
    {
        var configured = ConfiguredSubgraphSecrets();

        return HasStrongDedicatedSubgraphSecrets() &&
               configured.Distinct(StringComparer.Ordinal).Count() == configured.Length &&
               configured.All(secret => !string.Equals(secret, InternalSharedSecret, StringComparison.Ordinal));
    }

    public bool HasStrongDedicatedSubgraphSecrets() =>
        ConfiguredSubgraphSecrets().All(secret =>
            !string.IsNullOrWhiteSpace(secret) &&
            System.Text.Encoding.UTF8.GetByteCount(secret) >= 32);

    private string?[] ConfiguredSubgraphSecrets() =>
        new[]
        {
            SubgraphSecrets.Authentication,
            SubgraphSecrets.SocialGraph,
            SubgraphSecrets.Recommendation,
            SubgraphSecrets.Search,
            SubgraphSecrets.Messaging,
            SubgraphSecrets.Notification,
            SubgraphSecrets.Payment
        };
}

public sealed class GatewayRateLimitOptions
{
    /// <summary>Master switch. When false, no limiter is attached to /graphql.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sliding-window length in seconds for both partitions.</summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Permits per window for an authenticated caller (partitioned by user id).
    /// Deliberately generous so normal SPA browsing never trips it; it caps a single
    /// compromised/abusive account rather than legitimate use.
    /// </summary>
    public int AuthenticatedPermitLimit { get; set; } = 600;

    /// <summary>
    /// Permits per window for anonymous callers (partitioned by real client IP, resolved
    /// via X-Forwarded-For from the trusted edge). Caps unauthenticated flood traffic
    /// (login/register/public queries) without starving legitimate sign-in bursts.
    /// </summary>
    public int AnonymousPermitLimit { get; set; } = 240;
}

public sealed class GatewayGraphQlSecurityOptions
{
    /// <summary>Maximum validated selection depth. Normal Fakebook operations stay below 10.</summary>
    public int MaxDepth { get; set; } = 15;

    public int MaxAllowedFields { get; set; } = 512;
    public int MaxAllowedNodes { get; set; } = 4_096;
    public int MaxAllowedTokens { get; set; } = 8_192;
    public int MaxPlanningMilliseconds { get; set; } = 3_000;
    public int MaxExpandedPlannerNodes { get; set; } = 5_000;
    public int MaxPlannerQueueSize { get; set; } = 2_500;
    public int MaxGeneratedOptionsPerWorkItem { get; set; } = 128;
    public int ExecutionTimeoutSeconds { get; set; } = 20;
    public int MaxConcurrentExecutions { get; set; } = 64;
}

public sealed class SubgraphSecretsOptions
{
    public string? Authentication { get; set; }
    public string? SocialGraph { get; set; }
    public string? Recommendation { get; set; }
    public string? Search { get; set; }
    public string? Messaging { get; set; }
    public string? Notification { get; set; }
    public string? Payment { get; set; }
}

public static class GatewaySubgraphs
{
    public const string Authentication = "Authentication";
    public const string SocialGraph = "SocialGraph";
    public const string Recommendation = "Recommendation";
    public const string Search = "Search";
    public const string Messaging = "Messaging";
    public const string Notification = "Notification";
    public const string Payment = "Payment";

    public static readonly string[] All =
    [
        Authentication,
        SocialGraph,
        Recommendation,
        Search,
        Messaging,
        Notification,
        Payment
    ];
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "fakebook-auth";
    public string Audience { get; set; } = "fakebook";
    public string SigningKey { get; set; } = string.Empty;
}
