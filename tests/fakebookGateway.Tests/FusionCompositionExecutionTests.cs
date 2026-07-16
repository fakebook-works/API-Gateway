namespace fakebookGateway.Tests;

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using fakebookGateway.Gateway;
using Microsoft.IdentityModel.Tokens;
using Xunit;

public sealed class FusionCompositionExecutionTests :
    IClassFixture<GatewaySchemaTests.GatewayFactory>
{
    private readonly GatewaySchemaTests.GatewayFactory _factory;
    private readonly HttpClient _client;

    public FusionCompositionExecutionTests(GatewaySchemaTests.GatewayFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RecommendFeed_RanksThenHydratesUserAndGroupPostsThroughFusion()
    {
        _factory.Subgraphs.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(new
            {
                query =
                    """
                    query RecommendedFeed($userId: ID!) {
                      recommendFeed(userId: $userId, take: 2) {
                        postId
                        post {
                          __typename
                          ... on FeedPostDetail { id content author { id name } }
                          ... on GroupPostDetail { id content group { id name canJoin } }
                        }
                      }
                    }
                    """,
                variables = new { userId = "42" }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateAccessToken());

        using var response = await _client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, Diagnostic(responseBody));
        using var document = JsonDocument.Parse(responseBody);
        Assert.False(document.RootElement.TryGetProperty("errors", out _), Diagnostic(responseBody));
        var feed = document.RootElement
            .GetProperty("data")
            .GetProperty("recommendFeed");
        Assert.Equal(2, feed.GetArrayLength());
        Assert.Equal("1001", feed[0].GetProperty("postId").GetString());
        Assert.Equal("FeedPostDetail", feed[0].GetProperty("post").GetProperty("__typename").GetString());
        Assert.True(
            feed[1].GetProperty("postId").GetString() == "1002",
            Diagnostic(responseBody));
        Assert.Equal("GroupPostDetail", feed[1].GetProperty("post").GetProperty("__typename").GetString());
        Assert.Equal("Fusion Group", feed[1].GetProperty("post").GetProperty("group").GetProperty("name").GetString());

        var downstream = _factory.Subgraphs.Requests
            .Where(item => item.Service is "Recommendation" or "SocialGraph")
            .ToArray();
        Assert.Contains(downstream, item => item.Service == "Recommendation");
        Assert.Contains(downstream, item => item.Service == "SocialGraph");
        Assert.All(downstream, item =>
        {
            Assert.Equal("42", item.Headers[GatewayConstants.UserIdHeader]);
            Assert.Equal(
                item.Service == "Recommendation"
                    ? "recommendation-test-secret-at-least-32-bytes"
                    : "socialgraph-test-secret-at-least-32-bytes",
                item.Headers[GatewayConstants.GatewaySecretHeader]);
        });
    }

    [Fact]
    public async Task SearchMessagingAndNotification_RouteThroughNamedClientsWithTrustedIdentity()
    {
        _factory.Subgraphs.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(new
            {
                query =
                    """
                    query IntegratedServices {
                      searchUsers(keyword: "alice", pageSize: 1) {
                        items { user { id name avatar isVerified } }
                        pageInfo { pageNumber hasNextPage }
                      }
                      myConversations(first: 1) {
                        items { id }
                        pageInfo { hasNextPage }
                      }
                      notifications(first: 1) {
                        nodes { id actionType }
                        pageInfo { hasNextPage }
                        unreadCount
                      }
                    }
                    """
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateAccessToken());
        request.Headers.TryAddWithoutValidation(GatewayConstants.UserIdHeader, "999");
        request.Headers.TryAddWithoutValidation(GatewayConstants.GatewaySecretHeader, "spoofed");
        request.Headers.TryAddWithoutValidation(GatewayConstants.LegacyInternalUserIdHeader, "888");
        request.Headers.TryAddWithoutValidation(
            GatewayConstants.InternalSearchServiceSecretHeader,
            "spoofed-internal-secret");

        using var response = await _client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, Diagnostic(responseBody));
        using var document = JsonDocument.Parse(responseBody);
        Assert.False(document.RootElement.TryGetProperty("errors", out _), Diagnostic(responseBody));
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(501, data.GetProperty("searchUsers").GetProperty("items")[0]
            .GetProperty("user").GetProperty("id").GetInt64());
        Assert.Equal("search-user-501", data.GetProperty("searchUsers").GetProperty("items")[0]
            .GetProperty("user").GetProperty("name").GetString());
        Assert.Equal(0, data.GetProperty("myConversations").GetProperty("items").GetArrayLength());
        Assert.Equal(1, data.GetProperty("notifications").GetProperty("unreadCount").GetInt32());

        var expectedSecrets = new Dictionary<string, string>
        {
            ["Search"] = "search-test-secret-at-least-32-bytes",
            ["SocialGraph"] = "socialgraph-test-secret-at-least-32-bytes",
            ["Messaging"] = "messaging-test-secret-at-least-32-bytes",
            ["Notification"] = "notification-test-secret-at-least-32-bytes"
        };
        foreach (var (service, expectedSecret) in expectedSecrets)
        {
            var downstream = Assert.Single(
                _factory.Subgraphs.Requests,
                captured => captured.Service == service);
            Assert.Equal("42", downstream.Headers[GatewayConstants.UserIdHeader]);
            Assert.Equal("99", downstream.Headers[GatewayConstants.SessionIdHeader]);
            Assert.Equal(expectedSecret, downstream.Headers[GatewayConstants.GatewaySecretHeader]);
            Assert.True(downstream.Headers.ContainsKey("Authorization"));
            Assert.False(downstream.Headers.ContainsKey(GatewayConstants.RefreshTokenHeader));
            Assert.False(downstream.Headers.ContainsKey(GatewayConstants.LegacyInternalUserIdHeader));
            Assert.False(downstream.Headers.ContainsKey(GatewayConstants.InternalSearchServiceSecretHeader));
        }
    }

    [Fact]
    public async Task RecommendReels_RanksThenHydratesReelThroughFusion()
    {
        _factory.Subgraphs.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(new
            {
                query =
                    """
                    query RecommendedReels($userId: ID!) {
                      recommendReels(userId: $userId, mode: FOLLOWING, take: 1) {
                        reelId
                        reel { id content authorId media { id url type } }
                      }
                    }
                    """,
                variables = new { userId = "42" }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateAccessToken());

        using var response = await _client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, Diagnostic(responseBody));
        using var document = JsonDocument.Parse(responseBody);
        Assert.False(document.RootElement.TryGetProperty("errors", out _), Diagnostic(responseBody));
        var item = document.RootElement.GetProperty("data").GetProperty("recommendReels")[0];
        Assert.Equal("2001", item.GetProperty("reelId").GetString());
        Assert.Equal(2001, item.GetProperty("reel").GetProperty("id").GetInt64());
        Assert.Equal("recommended-reel", item.GetProperty("reel").GetProperty("content").GetString());

        Assert.Contains(_factory.Subgraphs.Requests, captured => captured.Service == "Recommendation");
        Assert.Contains(_factory.Subgraphs.Requests, captured => captured.Service == "SocialGraph");
    }

    [Fact]
    public async Task Search_HydratesEveryResultKindThroughSocialGraph()
    {
        _factory.Subgraphs.Clear();
        var cases = new[]
        {
            (
                "query { searchGroups(keyword: \"fusion\", pageSize: 1) { items { group { id name } } } }",
                "searchGroups",
                "group",
                601L),
            (
                "query { searchFeedPosts(keyword: \"fusion\", pageSize: 1) { items { post { id content author { id name } } } } }",
                "searchFeedPosts",
                "post",
                1001L),
            (
                "query { searchGroupPosts(keyword: \"fusion\", pageSize: 1) { items { post { id content group { id name } } } } }",
                "searchGroupPosts",
                "post",
                1002L),
            (
                "query { searchReels(keyword: \"fusion\", pageSize: 1) { items { reel { id content authorId } } } }",
                "searchReels",
                "reel",
                2001L)
        };

        foreach (var (query, rootField, hydratedField, expectedId) in cases)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
            {
                Content = JsonContent.Create(new { query })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateAccessToken());

            using var response = await _client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, Diagnostic(responseBody));
            using var document = JsonDocument.Parse(responseBody);
            Assert.False(document.RootElement.TryGetProperty("errors", out _), Diagnostic(responseBody));
            var hydrated = document.RootElement.GetProperty("data").GetProperty(rootField)
                .GetProperty("items")[0].GetProperty(hydratedField);
            Assert.Equal(expectedId, hydrated.GetProperty("id").GetInt64());
        }

        Assert.Contains(_factory.Subgraphs.Requests, captured => captured.Service == "Search");
        Assert.Contains(_factory.Subgraphs.Requests, captured => captured.Service == "SocialGraph");
    }

    [Fact]
    public async Task Messaging_UserReferencesHydrateThroughSocialGraphEntityLookup()
    {
        _factory.Subgraphs.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(new
            {
                query =
                    """
                    query MessagingUsers {
                      myConversations(first: 1) {
                        items {
                          id
                          participants {
                            userId
                            user { id name avatar isVerified }
                          }
                        }
                      }
                    }
                    """
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateAccessToken());

        using var response = await _client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, Diagnostic(responseBody));
        using var document = JsonDocument.Parse(responseBody);
        Assert.False(document.RootElement.TryGetProperty("errors", out _), Diagnostic(responseBody));
        var user = document.RootElement.GetProperty("data").GetProperty("myConversations")
            .GetProperty("items")[0].GetProperty("participants")[0].GetProperty("user");
        Assert.Equal(501, user.GetProperty("id").GetInt64());
        Assert.Equal("search-user-501", user.GetProperty("name").GetString());

        Assert.Contains(_factory.Subgraphs.Requests, captured => captured.Service == "Messaging");
        Assert.Contains(
            _factory.Subgraphs.Requests,
            captured => captured.Service == "SocialGraph" &&
                        captured.Body.Contains("userById", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MessagingSubscription_StreamsThroughGatewayWithTrustedHandshakeHeaders()
    {
        _factory.Subgraphs.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(new
            {
                query =
                    """
                    subscription InboxEvents {
                      inboxEvents {
                        eventId
                        kind
                        occurredAt
                      }
                    }
                    """
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateAccessToken());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, Diagnostic(responseBody));
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("MESSAGE_CREATED", responseBody, StringComparison.Ordinal);
        Assert.Contains("event: next", responseBody, StringComparison.Ordinal);
        var downstream = Assert.Single(
            _factory.Subgraphs.Requests,
            captured => captured.Service == "Messaging");
        Assert.Equal("42", downstream.Headers[GatewayConstants.UserIdHeader]);
        Assert.Equal("99", downstream.Headers[GatewayConstants.SessionIdHeader]);
        Assert.Equal(
            "messaging-test-secret-at-least-32-bytes",
            downstream.Headers[GatewayConstants.GatewaySecretHeader]);
        Assert.True(downstream.Headers.ContainsKey("Authorization"));
    }

    private string Diagnostic(string responseBody) =>
        responseBody + Environment.NewLine + string.Join(
            Environment.NewLine,
            _factory.Subgraphs.Requests.Select(item =>
                $"{item.Service} [{string.Join("; ", item.Headers.Select(header => $"{header.Key}={header.Value}"))}]: {item.Body}"));

    private static string CreateAccessToken()
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes("gateway-test-jwt-key-at-least-32-bytes")),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "fakebook-auth",
            audience: "fakebook",
            claims:
            [
                new Claim(GatewayConstants.UserIdClaim, "42"),
                new Claim(GatewayConstants.SessionIdClaim, "99")
            ],
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
