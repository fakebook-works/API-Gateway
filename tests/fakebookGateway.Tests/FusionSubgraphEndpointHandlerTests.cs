namespace fakebookGateway.Tests;

using fakebookGateway.Gateway;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class FusionSubgraphEndpointHandlerTests
{
    public static TheoryData<string, string> CanonicalEndpoints => new()
    {
        { GatewaySubgraphs.Authentication, "http://127.0.0.1:1001/graphql" },
        { GatewaySubgraphs.SocialGraph, "http://127.0.0.1:1002/graphql" },
        { GatewaySubgraphs.Recommendation, "http://127.0.0.1:1003/graphql" },
        { GatewaySubgraphs.Search, "http://127.0.0.1:1004/graphql" },
        { GatewaySubgraphs.Notification, "http://127.0.0.1:1005/graphql" },
        { GatewaySubgraphs.Messaging, "http://127.0.0.1:1006/graphql" },
        { GatewaySubgraphs.Payment, "http://127.0.0.1:1007/graphql" }
    };

    [Theory]
    [MemberData(nameof(CanonicalEndpoints))]
    public void Defaults_UseCanonicalLoopbackEndpoints(string subgraphName, string expected)
    {
        var options = new SubgraphEndpointsOptions();

        Assert.True(options.HasValidEndpoints());
        Assert.Equal(expected, options.Resolve(subgraphName).AbsoluteUri);
    }

    [Fact]
    public async Task Handler_ReplacesArchivedUrlWithConfiguredEndpointAndPreservesQuery()
    {
        var options = new SubgraphEndpointsOptions
        {
            SocialGraph = new SubgraphEndpointOptions
            {
                Url = "https://social.override.test:7443/custom/graphql"
            }
        };
        var capture = new CaptureRequestUriHandler();
        var handler = new FusionSubgraphEndpointHandler(
            new StaticOptionsMonitor<SubgraphEndpointsOptions>(options),
            GatewaySubgraphs.SocialGraph)
        {
            InnerHandler = capture
        };
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(
            "http://archive-host.invalid:1002/graphql?operation=query");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(
            "https://social.override.test:7443/custom/graphql?operation=query",
            capture.RequestUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1/graphql")]
    [InlineData("http://user:password@127.0.0.1/graphql")]
    [InlineData("http://127.0.0.1/graphql?unsafe=true")]
    [InlineData("/graphql")]
    public void Validation_RejectsUnsafeOrNonAbsoluteEndpoint(string endpoint)
    {
        var options = new SubgraphEndpointsOptions
        {
            SocialGraph = new SubgraphEndpointOptions { Url = endpoint }
        };

        Assert.False(options.HasValidEndpoints());
    }

    private sealed class CaptureRequestUriHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
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
