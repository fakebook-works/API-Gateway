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
    public string RefreshTokenCookieName { get; set; } = "fb_refresh";
    public string RefreshTokenCookiePath { get; set; } = "/";
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

        return string.IsNullOrWhiteSpace(configured)
            ? InternalSharedSecret
            : configured;
    }
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
