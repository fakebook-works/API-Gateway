namespace fakebookGateway.Tests;

using fakebookGateway.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class FusionSubgraphHeaderHandlerTests
{
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
                InternalSharedSecret = "trusted-gateway-secret-at-least-32-bytes"
            }))
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

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
