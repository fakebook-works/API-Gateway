using System.Text;
using fakebookGateway.Gateway;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IO.Compression;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection(GatewayOptions.SectionName))
    .Configure<IConfiguration>((options, configuration) =>
    {
        options.AuthenticationGraphQLEndpoint =
            configuration["Subgraphs:Authentication:Url"] ??
            configuration["Subgraphs:Authentication:GraphQLEndpoint"] ??
            options.AuthenticationGraphQLEndpoint;
    })
    .Validate(
        options => GatewaySubgraphs.All.All(subgraph =>
            Encoding.UTF8.GetByteCount(options.ResolveSubgraphSecret(subgraph)) >= 32),
        "Every resolved Gateway subgraph secret must contain at least 32 UTF-8 bytes.")
    .Validate(
        options => options.SessionCacheSeconds > 0,
        "Gateway:SessionCacheSeconds must be greater than zero.")
    .Validate(
        options => options.AuthSessionValidationTimeoutSeconds > 0,
        "Gateway:AuthSessionValidationTimeoutSeconds must be greater than zero.")
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.RefreshTokenCookieName),
        "Gateway:RefreshTokenCookieName is required.")
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.RefreshTokenCookiePath) &&
                   options.RefreshTokenCookiePath.StartsWith('/'),
        "Gateway:RefreshTokenCookiePath must start with '/'.")
    .Validate(
        options => Enum.IsDefined(options.RefreshTokenCookieSameSite),
        "Gateway:RefreshTokenCookieSameSite is invalid.")
    .Validate(
        options => Uri.TryCreate(options.AuthenticationGraphQLEndpoint, UriKind.Absolute, out _),
        "Subgraphs:Authentication:Url must be an absolute URL.")
    .ValidateOnStart();

builder.Services
    .AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.SigningKey), "Jwt:SigningKey is required.")
    .Validate(
        options => Encoding.UTF8.GetByteCount(options.SigningKey) >= 32,
        "Jwt:SigningKey must be at least 32 bytes.")
    .ValidateOnStart();

builder.Services
    .AddOptions<PaymentGatewayOptions>()
    .Bind(builder.Configuration.GetSection(PaymentGatewayOptions.SectionName))
    .Configure<IConfiguration>((options, configuration) =>
    {
        options.WebhookEndpoint =
            configuration["Subgraphs:Payment:WebhookUrl"] ??
            options.WebhookEndpoint;
    })
    .Validate(
        options => Uri.TryCreate(options.WebhookEndpoint, UriKind.Absolute, out _),
        "Subgraphs:Payment:WebhookUrl must be an absolute URL.")
    .Validate(options => options.TimeoutSeconds > 0, "PaymentGateway:TimeoutSeconds must be greater than zero.")
    .Validate(options => options.WebhookPermitLimit > 0, "PaymentGateway:WebhookPermitLimit must be greater than zero.")
    .Validate(options => options.WebhookWindowSeconds > 0, "PaymentGateway:WebhookWindowSeconds must be greater than zero.")
    .ValidateOnStart();

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IAuthSessionValidator, AuthSessionValidator>();
builder.Services.AddTransient<FusionSubgraphHeaderHandler>();
builder.Services.AddTransient<AuthFusionSubgraphHeaderHandler>();
builder.Services.AddTransient<PaymentFusionSubgraphHeaderHandler>();

builder.Services.AddHttpClient("auth-internal", (services, client) =>
{
    var gatewayOptions = services.GetRequiredService<IOptions<GatewayOptions>>().Value;
    client.BaseAddress = new Uri(gatewayOptions.AuthenticationGraphQLEndpoint);
    client.Timeout = TimeSpan.FromSeconds(gatewayOptions.AuthSessionValidationTimeoutSeconds);
});

builder.Services
    .AddHttpClient("auth-fusion")
    .AddHttpMessageHandler<AuthFusionSubgraphHeaderHandler>();

builder.Services
    .AddHttpClient("payment-fusion")
    .AddHttpMessageHandler<PaymentFusionSubgraphHeaderHandler>();

builder.Services
    .AddHttpClient("fusion")
    .AddHttpMessageHandler<FusionSubgraphHeaderHandler>();

AddFusionClient(builder.Services, "socialgraph-fusion", GatewaySubgraphs.SocialGraph);
AddFusionClient(builder.Services, "recommendation-fusion", GatewaySubgraphs.Recommendation);
AddFusionClient(builder.Services, "search-fusion", GatewaySubgraphs.Search);
AddFusionClient(builder.Services, "messaging-fusion", GatewaySubgraphs.Messaging);
AddFusionClient(builder.Services, "notification-fusion", GatewaySubgraphs.Notification);

builder.Services.AddHttpClient("payment-webhook", (services, client) =>
{
    var options = services.GetRequiredService<IOptions<PaymentGatewayOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy(PaymentWebhookProxy.RateLimitPolicy, context =>
    {
        var settings = context.RequestServices.GetRequiredService<IOptions<PaymentGatewayOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.WebhookPermitLimit,
                Window = TimeSpan.FromSeconds(settings.WebhookWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        var allowedOrigins = builder.Configuration
            .GetSection($"{GatewayOptions.SectionName}:AllowedOrigins")
            .Get<string[]>() ?? new GatewayOptions().AllowedOrigins;

        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((options, configuredJwtOptions) =>
    {
        var jwtOptions = configuredJwtOptions.Value;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

var fusionArchivePath = ResolveContentPath(
    builder.Environment,
    builder.Configuration[$"{GatewayOptions.SectionName}:FusionArchivePath"] ??
    new GatewayOptions().FusionArchivePath);

builder.Services
    .AddHealthChecks()
    .AddCheck(
        "self",
        () => HealthCheckResult.Healthy(),
        tags: ["live", "ready"])
    .AddCheck(
        "fusion_archive",
        () => CheckFusionArchive(fusionArchivePath),
        tags: ["ready"]);

builder
    .AddGraphQLGateway()
    .AddFileSystemConfiguration(fusionArchivePath);

var app = builder.Build();

app.UseMiddleware<GatewayEdgeMiddleware>();
app.UseCors("Frontend");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<GatewaySessionValidationMiddleware>();
app.UseMiddleware<GraphQlCookieResponseMiddleware>();

app.MapGraphQL("/graphql");
app.MapPaymentWebhookProxy();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapGet("/", () => Results.Redirect("/graphql"));

app.Run();

static string ResolveContentPath(IHostEnvironment environment, string path) =>
    System.IO.Path.IsPathRooted(path)
        ? path
        : System.IO.Path.Combine(environment.ContentRootPath, path);

static IHttpClientBuilder AddFusionClient(
    IServiceCollection services,
    string clientName,
    string subgraphName) =>
    services
        .AddHttpClient(clientName)
        .AddHttpMessageHandler(serviceProvider => new FusionSubgraphHeaderHandler(
            serviceProvider.GetRequiredService<IHttpContextAccessor>(),
            serviceProvider.GetRequiredService<IOptionsMonitor<GatewayOptions>>(),
            subgraphName));

static HealthCheckResult CheckFusionArchive(string path)
{
    try
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return HealthCheckResult.Unhealthy("Fusion archive is missing or empty.");
        }

        using var archive = ZipFile.OpenRead(path);
        var hasMetadata = archive.GetEntry("archive-metadata.json") is not null;
        var hasGatewaySchema = archive.Entries.Any(entry =>
            entry.FullName.StartsWith("gateway/", StringComparison.Ordinal) &&
            entry.FullName.EndsWith("/gateway.graphqls", StringComparison.Ordinal));

        return hasMetadata && hasGatewaySchema
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Fusion archive is incomplete.");
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
    {
        return HealthCheckResult.Unhealthy("Fusion archive cannot be read.", exception);
    }
}

public partial class Program;
