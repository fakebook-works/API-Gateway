namespace fakebookGateway.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class GatewaySchemaTests : IClassFixture<GatewaySchemaTests.GatewayFactory>
{
    private readonly HttpClient _client;

    public GatewaySchemaTests(GatewayFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task PublicSchema_ExposesCanonicalSocialGraphFeedContract()
    {
        using var response = await _client.PostAsJsonAsync("/graphql", new
        {
            query =
                """
                query GatewayContract {
                  __schema {
                    queryType { fields { name } }
                    mutationType { fields { name } }
                    types {
                      kind
                      name
                      fields { name }
                      inputFields { name }
                      possibleTypes { name }
                    }
                  }
                }
                """
        });
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, responseBody);
        using var document = JsonDocument.Parse(responseBody);
        var data = document.RootElement.GetProperty("data").GetProperty("__schema");
        var queryFields = FieldNames(data.GetProperty("queryType"));
        var mutationFields = FieldNames(data.GetProperty("mutationType"));

        Assert.Contains("visitedGroups", queryFields);
        Assert.Contains("recommendFeed", queryFields);
        Assert.Contains("postDetail", queryFields);
        Assert.Contains("postDetails", queryFields);
        Assert.Contains("homeStories", queryFields);
        Assert.Contains("myStories", queryFields);
        Assert.Contains("premiumPlans", queryFields);
        Assert.Contains("premiumOrder", queryFields);
        Assert.DoesNotContain("object", queryFields);
        Assert.DoesNotContain("association", queryFields);
        Assert.DoesNotContain("reelCandidates", queryFields);
        Assert.DoesNotContain("recommendationItem", queryFields);
        Assert.DoesNotContain("hello", queryFields);
        Assert.DoesNotContain("validateGatewaySession", queryFields);
        Assert.DoesNotContain("paymentPremiumState", queryFields);

        Assert.Contains("createUser", mutationFields);
        Assert.Contains("recordGroupVisit", mutationFields);
        Assert.Contains("createFeedPost", mutationFields);
        Assert.Contains("createNormalStory", mutationFields);
        Assert.Contains("createShareStory", mutationFields);
        Assert.Contains("deleteStory", mutationFields);
        Assert.Contains("createPremiumCheckout", mutationFields);
        Assert.DoesNotContain("addObject", mutationFields);
        Assert.DoesNotContain("createGroupPost", mutationFields);
        Assert.DoesNotContain("register", mutationFields);
        Assert.DoesNotContain("setPaymentValidDate", mutationFields);

        var types = data.GetProperty("types").EnumerateArray().ToArray();
        var homePost = Assert.Single(types, type => TypeName(type) == "HomePost");
        Assert.Equal(
            new[] { "FeedPostDetail", "GroupPostDetail" },
            homePost.GetProperty("possibleTypes")
                .EnumerateArray()
                .Select(TypeName)
                .OrderBy(name => name));

        var createFeedPostInput = Assert.Single(
            types,
            type => TypeName(type) == "CreateFeedPostInput");
        Assert.Equal(
            new[] { "authorId", "content", "media", "privacy" },
            createFeedPostInput.GetProperty("inputFields")
                .EnumerateArray()
                .Select(field => field.GetProperty("name").GetString()!)
                .OrderBy(name => name));

        var groupPost = Assert.Single(types, type => TypeName(type) == "GroupPostDetail");
        Assert.Contains("group", FieldNames(groupPost));

        var recommendationItem = Assert.Single(
            types,
            type => TypeName(type) == "RecommendationItem");
        Assert.Equal(
            new[] { "post", "postId" },
            FieldNames(recommendationItem).OrderBy(name => name));

        var userType = Assert.Single(types, type => TypeName(type) == "UserType");
        var authUserFields = FieldNames(userType);
        foreach (var unsupportedIdentityField in new[] { "username", "phone", "displayName", "dob", "gender" })
        {
            Assert.DoesNotContain(unsupportedIdentityField, authUserFields);
        }

        Assert.Contains("validDate", FieldNames(userType));

        var socialGraphCreateUserInput = Assert.Single(
            types,
            type => TypeName(type) == "CreateUserInput");
        var socialProfileInputs = InputFieldNamesOrEmpty(socialGraphCreateUserInput);
        Assert.Contains("name", socialProfileInputs);
        Assert.Contains("birthdate", socialProfileInputs);
        Assert.Contains("gender", socialProfileInputs);
        Assert.Contains("location", socialProfileInputs);

        Assert.DoesNotContain(
            types,
            type => FieldNamesOrEmpty(type).Contains("username", StringComparer.OrdinalIgnoreCase) ||
                    InputFieldNamesOrEmpty(type).Contains("username", StringComparer.OrdinalIgnoreCase));
    }

    private static string[] FieldNames(JsonElement type) =>
        type.GetProperty("fields")
            .EnumerateArray()
            .Select(field => field.GetProperty("name").GetString()!)
            .ToArray();

    private static string[] FieldNamesOrEmpty(JsonElement type) =>
        type.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array
            ? fields.EnumerateArray().Select(FieldName).ToArray()
            : [];

    private static string[] InputFieldNamesOrEmpty(JsonElement type) =>
        type.TryGetProperty("inputFields", out var fields) && fields.ValueKind == JsonValueKind.Array
            ? fields.EnumerateArray().Select(FieldName).ToArray()
            : [];

    private static string FieldName(JsonElement field) =>
        field.GetProperty("name").GetString()!;

    private static string TypeName(JsonElement type) =>
        type.GetProperty("name").GetString()!;

    public sealed class GatewayFactory : WebApplicationFactory<Program>
    {
        public TestSubgraphHandler Subgraphs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:FusionArchivePath"] = FindFusionArchive(),
                    ["Gateway:InternalSharedSecret"] = "gateway-test-secret-at-least-32-bytes",
                    ["Gateway:SessionCacheSeconds"] = "30",
                    ["Jwt:Issuer"] = "fakebook-auth",
                    ["Jwt:Audience"] = "fakebook",
                    ["Jwt:SigningKey"] = "gateway-test-jwt-key-at-least-32-bytes",
                    ["Subgraphs:Authentication:Url"] = "http://localhost:5001/graphql"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(Subgraphs);
                services
                    .AddHttpClient("fusion")
                    .ConfigurePrimaryHttpMessageHandler<TestSubgraphHandler>();
                services
                    .AddHttpClient("auth-internal")
                    .ConfigurePrimaryHttpMessageHandler<TestSubgraphHandler>();
            });
        }

        private static string FindFusionArchive() => Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "fakebookGateway",
                "gateway.far"));
    }
}
