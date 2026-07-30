namespace fakebookGateway.Tests;

using System.Net.Http.Json;
using Xunit;

public sealed class GatewayEnvironmentTests
{
    [Fact]
    public async Task RuntimeSubgraphConfiguration_OverridesTheUrlStoredInTheArchive()
    {
        using var factory = new GatewaySchemaTests.GatewayFactory(
            "Production",
            new Dictionary<string, string?>
            {
                ["Subgraphs:Authentication:Url"] =
                    "http://auth.runtime-override.test:1001/custom/graphql"
            });
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/graphql", new
        {
            query = "query { health }"
        });
        _ = await response.Content.ReadAsStringAsync();

        var request = Assert.Single(
            factory.Subgraphs.Requests,
            item => item.Service == "Authentication");
        Assert.Equal(
            "http://auth.runtime-override.test:1001/custom/graphql",
            request.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task Development_ServesNitroForBrowserNavigation()
    {
        using var factory = new GatewaySchemaTests.GatewayFactory("Development");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/graphql");
        request.Headers.Accept.ParseAdd("text/html");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<!doctype html", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_DoesNotServeNitroButStillExecutesGraphQlPost()
    {
        using var factory = new GatewaySchemaTests.GatewayFactory("Production");
        using var client = factory.CreateClient();
        using var browserRequest = new HttpRequestMessage(HttpMethod.Get, "/graphql");
        browserRequest.Headers.Accept.ParseAdd("text/html");

        using var browserResponse = await client.SendAsync(browserRequest);
        var browserBody = await browserResponse.Content.ReadAsStringAsync();

        Assert.NotEqual("text/html", browserResponse.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<!doctype html", browserBody, StringComparison.OrdinalIgnoreCase);

        using var graphQlResponse = await client.PostAsJsonAsync("/graphql", new
        {
            query = "query { __typename }"
        });
        var graphQlBody = await graphQlResponse.Content.ReadAsStringAsync();

        Assert.True(graphQlResponse.IsSuccessStatusCode, graphQlBody);
        Assert.Contains("\"__typename\":\"Query\"", graphQlBody, StringComparison.Ordinal);
    }
}
