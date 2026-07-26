namespace fakebookGateway.Tests;

using fakebookGateway.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class FusionSubgraphHeaderHandlerTests
{
    [Fact]
    public void MissingDedicatedSecret_DoesNotFallBackToTheSharedGatewaySecret()
    {
        var options = new GatewayOptions
        {
            InternalSharedSecret = "legacy-shared-secret-at-least-32-bytes"
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            options.ResolveSubgraphSecret(GatewaySubgraphs.SocialGraph));

        Assert.Contains("dedicated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handler_ReplacesSpoofedTrustedHeadersWithGatewayContext()
    {
        var context = new DefaultHttpContext();
        context.Items[GatewayConstants.UserIdItem] = "123";
        context.Items[GatewayConstants.SessionIdItem] = "456";
        context.Items[GatewayConstants.CorrelationIdHeader] = "correlation-1";
        var capture = new CaptureHandler();
        var handler = new FusionSubgraphHeaderHandler(
            new HttpContextAccessor { HttpContext = context },
            new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions
            {
                InternalSharedSecret = "legacy-gateway-secret-at-least-32-bytes",
                SubgraphSecrets = new SubgraphSecretsOptions
                {
                    SocialGraph = "trusted-gateway-secret-at-least-32-bytes"
                }
            }),
            GatewaySubgraphs.SocialGraph)
        {
            InnerHandler = capture
        };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://socialgraph/graphql");
        request.Headers.TryAddWithoutValidation(GatewayConstants.UserIdHeader, "999");
        request.Headers.TryAddWithoutValidation(GatewayConstants.SessionIdHeader, "999");
        request.Headers.TryAddWithoutValidation("X-Username", "spoofed-username");
        request.Headers.TryAddWithoutValidation(GatewayConstants.GatewaySecretHeader, "spoofed-secret");

        using var response = await client.SendAsync(request);

        Assert.Equal("123", capture.Headers[GatewayConstants.UserIdHeader]);
        Assert.Equal("456", capture.Headers[GatewayConstants.SessionIdHeader]);
        Assert.False(capture.Headers.ContainsKey("X-Username"));
        Assert.Equal("correlation-1", capture.Headers[GatewayConstants.CorrelationIdHeader]);
        Assert.Equal(
            "trusted-gateway-secret-at-least-32-bytes",
            capture.Headers[GatewayConstants.GatewaySecretHeader]);
    }

    [Fact]
    public async Task NamedHandler_UsesTargetSecretAndNeverForwardsRefreshOrInternalServiceHeaders()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer access-token";
        context.Request.Headers.Cookie = "fb_refresh=raw-refresh-token";
        context.Items[GatewayConstants.UserIdItem] = "123";
        context.Items[GatewayConstants.SessionIdItem] = "456";
        context.Items[GatewayConstants.CorrelationIdHeader] = "correlation-2";
        var capture = new CaptureHandler();
        var handler = new FusionSubgraphHeaderHandler(
            new HttpContextAccessor { HttpContext = context },
            new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions
            {
                InternalSharedSecret = "fallback-secret-at-least-32-bytes",
                SubgraphSecrets = new SubgraphSecretsOptions
                {
                    Search = "search-secret-at-least-32-bytes-long"
                }
            }),
            GatewaySubgraphs.Search)
        {
            InnerHandler = capture
        };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://search/graphql");
        request.Headers.TryAddWithoutValidation(GatewayConstants.RefreshTokenHeader, "spoofed-refresh");
        request.Headers.TryAddWithoutValidation(GatewayConstants.LegacyInternalUserIdHeader, "999");
        request.Headers.TryAddWithoutValidation(
            GatewayConstants.InternalSearchServiceSecretHeader,
            "spoofed-internal-secret");
        request.Headers.TryAddWithoutValidation(GatewayConstants.PaymentSecretHeader, "spoofed-payment-secret");

        using var response = await client.SendAsync(request);

        Assert.Equal("Bearer access-token", capture.Headers["Authorization"]);
        Assert.Equal("123", capture.Headers[GatewayConstants.UserIdHeader]);
        Assert.Equal("456", capture.Headers[GatewayConstants.SessionIdHeader]);
        Assert.Equal(
            "search-secret-at-least-32-bytes-long",
            capture.Headers[GatewayConstants.GatewaySecretHeader]);
        Assert.False(capture.Headers.ContainsKey(GatewayConstants.RefreshTokenHeader));
        Assert.False(capture.Headers.ContainsKey(GatewayConstants.LegacyInternalUserIdHeader));
        Assert.False(capture.Headers.ContainsKey(GatewayConstants.InternalSearchServiceSecretHeader));
        Assert.False(capture.Headers.ContainsKey(GatewayConstants.PaymentSecretHeader));
    }

    [Fact]
    public async Task Handler_ForwardsTheResolvedClientAddressAndUserAgent()
    {
        // Subgraphs used to see only the gateway container address, which collapsed
        // Authentication's per-IP login throttle onto the identifier alone and left session
        // records with no device detail.
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("100.101.173.71");
        context.Request.Headers.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
        var capture = new CaptureHandler();
        var handler = new FusionSubgraphHeaderHandler(
            new HttpContextAccessor { HttpContext = context },
            new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions
            {
                InternalSharedSecret = "fallback-secret-at-least-32-bytes",
                SubgraphSecrets = new SubgraphSecretsOptions
                {
                    Authentication = "auth-secret-at-least-32-bytes-long-x"
                }
            }),
            GatewaySubgraphs.Authentication)
        {
            InnerHandler = capture
        };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://auth/graphql");
        // A client-supplied chain must never be passed through.
        request.Headers.TryAddWithoutValidation(GatewayConstants.ForwardedForHeader, "1.2.3.4, 5.6.7.8");
        request.Headers.TryAddWithoutValidation(GatewayConstants.UserAgentHeader, "spoofed-agent");

        using var response = await client.SendAsync(request);

        Assert.Equal("100.101.173.71", capture.Headers[GatewayConstants.ForwardedForHeader]);
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", capture.UserAgent);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// User-Agent is a structured header, so enumerating it yields one entry per product
        /// token. This keeps the value as it is actually serialised onto the wire.
        /// </summary>
        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            UserAgent = request.Headers.UserAgent.ToString();
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = string.Join(",", header.Value);
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
