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
                "gateway-test-secret-at-least-32-bytes",
                item.Headers[GatewayConstants.GatewaySecretHeader]);
        });
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
