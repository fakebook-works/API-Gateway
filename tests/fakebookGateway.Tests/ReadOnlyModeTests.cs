namespace fakebookGateway.Tests;

using System.Net;
using System.Net.Http.Json;
using Xunit;

public sealed class ReadOnlyModeTests
{
    private static GatewaySchemaTests.GatewayFactory CreateFactory(bool readOnly) =>
        new("Production", new Dictionary<string, string?>
        {
            ["Gateway:ReadOnlyMode"] = readOnly.ToString()
        });

    [Theory]
    [InlineData("mutation { createUser(input: {}) { success } }")]
    [InlineData("mutation { publish: createFeedPost(input: {}) { id } }")]
    [InlineData("mutation { createComment(input: {}) { id } }")]
    [InlineData("mutation { like(userId: 1, targetId: 2) }")]
    [InlineData("mutation { login(input: {}) { accessToken } like(userId: 1, targetId: 2) }")]
    [InlineData("mutation { ...WriteFields } fragment WriteFields on Mutation { like(userId: 1, targetId: 2) }")]
    public async Task ReadOnlyMode_RejectsSocialWrites(string query)
    {
        using var factory = CreateFactory(true);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/graphql", new { query });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues("X-Fakebook-Read-Only").Single());
        Assert.Contains("READ_ONLY", body, StringComparison.Ordinal);
        Assert.Empty(factory.Subgraphs.Requests);
    }

    [Fact]
    public async Task ReadOnlyMode_StillServesQueriesAndAccountAccess()
    {
        using var factory = CreateFactory(true);
        using var client = factory.CreateClient();
        using var queryResponse = await client.PostAsJsonAsync("/graphql", new { query = "query { __typename }" });
        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Equal("true", queryResponse.Headers.GetValues("X-Fakebook-Read-Only").Single());

        using var loginResponse = await client.PostAsJsonAsync("/graphql", new { query = "mutation { login(input: {}) { accessToken } }" });
        var loginBody = await loginResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("READ_ONLY", loginBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyMode_RejectsGetMutationsToo()
    {
        using var factory = CreateFactory(true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/graphql?query=mutation%20%7B%20like(userId%3A1%2CtargetId%3A2)%20%7D");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyMode_CoversGraphQlPathWithTrailingSlash()
    {
        using var factory = CreateFactory(true);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/graphql/", new { query = "mutation { like(userId: 1, targetId: 2) }" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyMode_RejectsBodiesItCannotInspect()
    {
        using var factory = CreateFactory(true);
        using var client = factory.CreateClient();
        using var nonJson = await client.PostAsync("/graphql", new StringContent("mutation { like }"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, nonJson.StatusCode);

        using var malformed = await client.PostAsync("/graphql", new StringContent(
            "{not-json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Empty(factory.Subgraphs.Requests);
    }

    [Fact]
    public async Task StatusEndpoint_IsBoundedAndReflectsConfiguration()
    {
        using var factory = CreateFactory(true);
        using var client = factory.CreateClient();
        using var status = await client.GetAsync("/site-status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("no-store", status.Headers.CacheControl?.ToString());
        Assert.Contains("\"readOnly\":true", await status.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/site-status")
        {
            Content = new StringContent("unexpected body")
        };
        using var badRequest = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
    }

    [Fact]
    public async Task DefaultMode_LeavesMutationsToGraphQl()
    {
        using var factory = CreateFactory(false);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/graphql", new { query = "mutation { createUser(input: {}) { success } }" });
        Assert.Equal("false", response.Headers.GetValues("X-Fakebook-Read-Only").Single());
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
