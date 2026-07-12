using System.Text;
using fakebookGateway.Gateway;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

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
        options => !string.IsNullOrWhiteSpace(options.InternalSharedSecret),
        "Gateway:InternalSharedSecret is required.")
    .Validate(
        options => options.SessionCacheSeconds > 0,
        "Gateway:SessionCacheSeconds must be greater than zero.")
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

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IAuthSessionValidator, AuthSessionValidator>();
builder.Services.AddTransient<FusionSubgraphHeaderHandler>();

builder.Services.AddHttpClient("auth-internal", (services, client) =>
{
    var gatewayOptions = services.GetRequiredService<IOptions<GatewayOptions>>().Value;
    client.BaseAddress = new Uri(gatewayOptions.AuthenticationGraphQLEndpoint);
});

builder.Services
    .AddHttpClient("auth-fusion")
    .AddHttpMessageHandler<FusionSubgraphHeaderHandler>();

builder.Services
    .AddHttpClient("fusion")
    .AddHttpMessageHandler<FusionSubgraphHeaderHandler>();

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
            ClockSkew = TimeSpan.Zero,
            NameClaimType = GatewayConstants.UsernameClaim
        };
    });

builder.Services.AddAuthorization();

var fusionArchivePath = ResolveContentPath(
    builder.Environment,
    builder.Configuration[$"{GatewayOptions.SectionName}:FusionArchivePath"] ??
    new GatewayOptions().FusionArchivePath);

builder
    .AddGraphQLGateway()
    .AddFileSystemConfiguration(fusionArchivePath);

var app = builder.Build();

app.UseMiddleware<GatewayEdgeMiddleware>();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<GatewaySessionValidationMiddleware>();
app.UseMiddleware<GraphQlCookieResponseMiddleware>();

app.MapGraphQL("/graphql");
app.MapGet("/", () => Results.Redirect("/graphql"));

app.Run();

static string ResolveContentPath(IHostEnvironment environment, string path) =>
    System.IO.Path.IsPathRooted(path)
        ? path
        : System.IO.Path.Combine(environment.ContentRootPath, path);

public partial class Program;
