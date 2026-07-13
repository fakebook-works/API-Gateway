using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using fakebookGateway.Gateway;
using Microsoft.IdentityModel.Tokens;

namespace fakebookGateway.Tests;

public sealed class PaymentWebhookProxyTests
{
    private const string GatewaySecret = "01234567890123456789012345678901";
    private const string SigningKey = "test-signing-key-at-least-32-bytes-long";

    [Fact]
    public async Task Composite_schema_exposes_payment_and_hides_internal_auth_fields()
    {
        var downstream = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"data\":{\"premiumPlans\":[{\"code\":\"MONTHLY\",\"amount\":52000,\"durationMonths\":1}]}}",
                Encoding.UTF8,
                "application/json")
        });
        await using var factory = CreateFactory(downstream);
        using var client = factory.CreateClient();

        using var schemaResponse = await client.PostAsJsonAsync("/graphql", new
        {
            query = "query { __schema { queryType { fields { name } } mutationType { fields { name } } } }"
        });
        using var schema = JsonDocument.Parse(await schemaResponse.Content.ReadAsStringAsync());
        var queries = schema.RootElement.GetProperty("data").GetProperty("__schema").GetProperty("queryType").GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString()).ToArray();
        var mutations = schema.RootElement.GetProperty("data").GetProperty("__schema").GetProperty("mutationType").GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString()).ToArray();

        Assert.Contains("premiumPlans", queries);
        Assert.Contains("premiumOrder", queries);
        Assert.Contains("createPremiumCheckout", mutations);
        Assert.DoesNotContain("paymentPremiumState", queries);
        Assert.DoesNotContain("validateGatewaySession", queries);
        Assert.DoesNotContain("setPaymentValidDate", mutations);

        using var plansResponse = await client.PostAsJsonAsync("/graphql", new
        {
            query = "query { premiumPlans { code amount durationMonths } }"
        });
        using var plans = JsonDocument.Parse(await plansResponse.Content.ReadAsStringAsync());
        Assert.Equal("MONTHLY", plans.RootElement.GetProperty("data").GetProperty("premiumPlans")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Proxy_preserves_raw_body_and_forwards_only_server_owned_headers()
    {
        var downstream = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var factory = CreateFactory(downstream);
        using var client = factory.CreateClient();
        var body = Encoding.UTF8.GetBytes("{\"signature\":\"abc\",\"data\":{\"amount\":52000}}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/payos")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Authorization = new("Bearer", "browser-token");
        request.Headers.Add("Cookie", "fb_refresh=browser-cookie");
        request.Headers.Add("X-User-Id", "999");
        request.Headers.Add("X-Session-Id", "888");
        request.Headers.Add("X-Gateway-Secret", "spoofed");
        request.Headers.Add("X-Correlation-ID", "payment-test-correlation");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, downstream.Body);
        Assert.Equal("application/json", downstream.ContentType);
        Assert.Equal(GatewaySecret, downstream.Header("X-Gateway-Secret"));
        Assert.Equal("payment-test-correlation", downstream.Header("X-Correlation-ID"));
        Assert.Null(downstream.Header("Authorization"));
        Assert.Null(downstream.Header("Cookie"));
        Assert.Null(downstream.Header("X-User-Id"));
        Assert.Null(downstream.Header("X-Session-Id"));
        Assert.Null(downstream.Header("X-Refresh-Token"));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest, HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway)]
    public async Task Proxy_exposes_only_safe_downstream_status(HttpStatusCode downstreamStatus, HttpStatusCode expected)
    {
        var downstream = new RecordingHandler(_ => new HttpResponseMessage(downstreamStatus)
        {
            Content = new StringContent("secret downstream details")
        });
        await using var factory = CreateFactory(downstream);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/webhooks/payos",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain("secret downstream details", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Proxy_rejects_empty_wrong_content_type_and_oversized_body_without_calling_payment()
    {
        var downstream = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var factory = CreateFactory(downstream);
        using var client = factory.CreateClient();

        using var empty = await client.PostAsync(
            "/api/webhooks/payos",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));
        using var wrongType = await client.PostAsync(
            "/api/webhooks/payos",
            new StringContent("{}", Encoding.UTF8, "text/plain"));
        using var oversized = await client.PostAsync(
            "/api/webhooks/payos",
            new StringContent(new string('x', 64 * 1024 + 1), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal(0, downstream.CallCount);
    }

    [Fact]
    public async Task Proxy_maps_payment_network_failure_to_service_unavailable()
    {
        var downstream = new RecordingHandler(_ => throw new HttpRequestException("injected outage"));
        await using var factory = CreateFactory(downstream);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/webhooks/payos",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Proxy_rate_limits_by_public_client_ip()
    {
        var downstream = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var factory = CreateFactory(downstream, webhookPermitLimit: 2);
        using var client = factory.CreateClient();

        using var first = await client.PostAsync("/api/webhooks/payos", new StringContent("{}", Encoding.UTF8, "application/json"));
        using var second = await client.PostAsync("/api/webhooks/payos", new StringContent("{}", Encoding.UTF8, "application/json"));
        using var third = await client.PostAsync("/api/webhooks/payos", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(2, downstream.CallCount);
    }

    [Fact]
    public async Task Payment_fusion_forwards_identity_but_never_refresh_cookie()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer access-token";
        context.Request.Headers.Cookie = "fb_refresh=raw-refresh-token";
        context.Items[GatewayConstants.UserIdItem] = "123";
        context.Items[GatewayConstants.SessionIdItem] = "456";
        context.Items[GatewayConstants.UsernameItem] = "alice";
        context.Items[GatewayConstants.CorrelationIdHeader] = "correlation-1";
        var accessor = new HttpContextAccessor { HttpContext = context };
        var downstream = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new PaymentFusionSubgraphHeaderHandler(
            accessor,
            new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions
            {
                InternalSharedSecret = GatewaySecret,
                RefreshTokenCookieName = "fb_refresh"
            }))
        {
            InnerHandler = downstream
        };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://payment.test/graphql");
        request.Headers.Add(GatewayConstants.UserIdHeader, "spoofed");
        request.Headers.Add(GatewayConstants.RefreshTokenHeader, "spoofed-refresh");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("123", downstream.Header(GatewayConstants.UserIdHeader));
        Assert.Equal("456", downstream.Header(GatewayConstants.SessionIdHeader));
        Assert.Equal("alice", downstream.Header(GatewayConstants.UsernameHeader));
        Assert.Equal(GatewaySecret, downstream.Header(GatewayConstants.GatewaySecretHeader));
        Assert.Equal("correlation-1", downstream.Header(GatewayConstants.CorrelationIdHeader));
        Assert.Null(downstream.Header("Authorization"));
        Assert.Null(downstream.Header(GatewayConstants.RefreshTokenHeader));
    }

    [Fact]
    public async Task Protected_payment_operation_rejects_missing_and_spoofed_identity()
    {
        var payment = new RecordingHandler(request =>
        {
            var hasUser = request.Headers.Contains(GatewayConstants.UserIdHeader);
            return GraphQlResponse(hasUser
                ? "{\"data\":{\"createPremiumCheckout\":{\"orderCode\":\"1\",\"status\":\"PENDING\",\"checkoutUrl\":\"https://pay.test\"}}}"
                : "{\"errors\":[{\"message\":\"Unauthorized.\",\"extensions\":{\"code\":\"UNAUTHENTICATED\"}}],\"data\":null}");
        });
        await using var factory = CreateFactory(payment);
        using var client = factory.CreateClient();
        var mutation = new { query = "mutation { createPremiumCheckout(input: { plan: MONTHLY }) { orderCode status checkoutUrl } }" };

        using var missing = await client.PostAsJsonAsync("/graphql", mutation);
        using var spoofRequest = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(mutation)
        };
        spoofRequest.Headers.Add(GatewayConstants.UserIdHeader, "999");
        spoofRequest.Headers.Add(GatewayConstants.SessionIdHeader, "888");
        spoofRequest.Headers.Add(GatewayConstants.GatewaySecretHeader, "spoofed");
        using var spoofed = await client.SendAsync(spoofRequest);

        Assert.Contains("UNAUTHENTICATED", await missing.Content.ReadAsStringAsync());
        Assert.Contains("UNAUTHENTICATED", await spoofed.Content.ReadAsStringAsync());
        Assert.Null(payment.Header(GatewayConstants.UserIdHeader));
    }

    [Fact]
    public async Task Protected_payment_operation_accepts_valid_session_and_rejects_revoked_session()
    {
        var payment = new RecordingHandler(_ => GraphQlResponse(
            "{\"data\":{\"createPremiumCheckout\":{\"orderCode\":\"1\",\"status\":\"PENDING\",\"checkoutUrl\":\"https://pay.test\"}}}"));
        var validAuth = new RecordingHandler(_ => GraphQlResponse(
            "{\"data\":{\"validateGatewaySession\":{\"isValid\":true,\"userId\":123,\"sessionId\":456,\"username\":\"alice\",\"status\":1,\"expiresAt\":\"2030-01-01T00:00:00Z\"}}}"));
        await using (var validFactory = CreateFactory(payment, authDownstream: validAuth))
        {
            using var client = validFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", CreateAccessToken());
            using var response = await client.PostAsJsonAsync("/graphql", new
            {
                query = "mutation { createPremiumCheckout(input: { plan: MONTHLY }) { orderCode status checkoutUrl } }"
            });
            Assert.True(validAuth.CallCount > 0, await response.Content.ReadAsStringAsync());
            Assert.Contains("\"orderCode\":\"1\"", await response.Content.ReadAsStringAsync());
            Assert.Equal("123", payment.Header(GatewayConstants.UserIdHeader));
            Assert.Equal("456", payment.Header(GatewayConstants.SessionIdHeader));
            Assert.Null(payment.Header("Authorization"));
        }

        var revokedPayment = new RecordingHandler(_ => throw new InvalidOperationException("Payment must not be called for a revoked session."));
        var revokedAuth = new RecordingHandler(_ => GraphQlResponse(
            "{\"data\":{\"validateGatewaySession\":{\"isValid\":false,\"userId\":123,\"sessionId\":456,\"username\":null,\"status\":1,\"expiresAt\":null}}}"));
        await using var revokedFactory = CreateFactory(revokedPayment, authDownstream: revokedAuth);
        using var revokedClient = revokedFactory.CreateClient();
        revokedClient.DefaultRequestHeaders.Authorization = new("Bearer", CreateAccessToken());
        using var revokedResponse = await revokedClient.PostAsJsonAsync("/graphql", new
        {
            query = "mutation { createPremiumCheckout(input: { plan: MONTHLY }) { orderCode } }"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);
        Assert.Equal(0, revokedPayment.CallCount);
    }

    private static WebApplicationFactory<global::Program> CreateFactory(
        RecordingHandler downstream,
        int webhookPermitLimit = 1000,
        RecordingHandler? authDownstream = null) =>
        new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Jwt:Issuer", "fakebook-auth");
            builder.UseSetting("Jwt:Audience", "fakebook");
            builder.UseSetting("Jwt:SigningKey", SigningKey);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Jwt:Issuer"] = "fakebook-auth",
                    ["Jwt:Audience"] = "fakebook",
                    ["Jwt:SigningKey"] = SigningKey,
                    ["Gateway:InternalSharedSecret"] = GatewaySecret,
                    ["Gateway:FusionArchivePath"] = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../fakebookGateway/gateway.far")),
                    ["Subgraphs:Authentication:Url"] = "http://localhost:5001/graphql",
                    ["Subgraphs:Payment:WebhookUrl"] = "http://payment.test/internal/webhooks/payos",
                    ["PaymentGateway:WebhookPermitLimit"] = webhookPermitLimit.ToString()
                }));
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("payment-webhook")
                    .ConfigurePrimaryHttpMessageHandler(() => downstream);
                services.AddHttpClient("payment-fusion")
                    .ConfigurePrimaryHttpMessageHandler(() => downstream);
                if (authDownstream is not null)
                {
                    services.AddHttpClient("auth-internal")
                        .ConfigurePrimaryHttpMessageHandler(_ => authDownstream);
                }
            });
        });

    private static HttpResponseMessage GraphQlResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string CreateAccessToken()
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "fakebook-auth",
            audience: "fakebook",
            claims:
            [
                new Claim("sub", "123"),
                new Claim(GatewayConstants.UserIdClaim, "123"),
                new Claim(GatewayConstants.SessionIdClaim, "456"),
                new Claim(GatewayConstants.UsernameClaim, "alice")
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public byte[] Body { get; private set; } = [];
        public string? ContentType { get; private set; }
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public string? Header(string name) => _headers.GetValueOrDefault(name);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            _headers.Clear();
            foreach (var header in request.Headers)
            {
                _headers[header.Key] = string.Join(",", header.Value);
            }
            return responseFactory(request);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
