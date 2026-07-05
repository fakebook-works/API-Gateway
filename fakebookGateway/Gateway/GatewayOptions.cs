namespace fakebookGateway.Gateway;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    public string FusionArchivePath { get; set; } = "gateway.far";
    public string AuthenticationGraphQLEndpoint { get; set; } = "http://localhost:5001/graphql";
    public string InternalSharedSecret { get; set; } = string.Empty;
    public int SessionCacheSeconds { get; set; } = 30;
    public string RefreshTokenCookieName { get; set; } = "fb_refresh";
    public string[] AllowedOrigins { get; set; } =
    [
        "http://localhost:3000",
        "http://localhost:5173",
        "http://localhost:5174"
    ];
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "fakebook-auth";
    public string Audience { get; set; } = "fakebook";
    public string SigningKey { get; set; } = string.Empty;
}
