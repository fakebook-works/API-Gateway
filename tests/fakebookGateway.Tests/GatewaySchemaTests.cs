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
                      subscriptionType { fields { name } }
                    types {
                      kind
                      name
                      fields { name }
                      inputFields { name }
                      possibleTypes { name }
                      enumValues { name }
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
        var subscriptionFields = FieldNames(data.GetProperty("subscriptionType"));

        Assert.Contains("visitedGroups", queryFields);
        Assert.Contains("profiles", queryFields);
        Assert.Contains("friendSuggestions", queryFields);
        Assert.Contains("friendRelationProfiles", queryFields);
        Assert.Contains("groups", queryFields);
        Assert.Contains("profile", queryFields);
        Assert.Contains("group", queryFields);
        Assert.Contains("profilePosts", queryFields);
        Assert.Contains("profileReels", queryFields);
        Assert.Contains("groupUserPosts", queryFields);
        Assert.Contains("userPhotos", queryFields);
        Assert.Contains("groupPhotos", queryFields);
        Assert.Contains("groupUserPhotos", queryFields);
        Assert.Contains("myFeedPhotoCandidates", queryFields);
        Assert.Contains("groupPhotoCandidates", queryFields);
        Assert.DoesNotContain("ownedMedia", queryFields);
        Assert.Contains("likedReels", queryFields);
        Assert.Contains("sharedReels", queryFields);
        Assert.Contains("watchedReels", queryFields);
        Assert.Contains("friends", queryFields);
        Assert.Contains("incomingFriendRequests", queryFields);
        Assert.Contains("outgoingFriendRequests", queryFields);
        Assert.Contains("following", queryFields);
        Assert.Contains("followers", queryFields);
        Assert.Contains("blockedUsers", queryFields);
        Assert.Contains("memberGroups", queryFields);
        Assert.Contains("adminGroups", queryFields);
        Assert.Contains("relationshipState", queryFields);
        Assert.Contains("groupViewerState", queryFields);
        Assert.Contains("pendingGroupJoins", queryFields);
        Assert.Contains("groupMembers", queryFields);
        Assert.Contains("groupAdmins", queryFields);
        Assert.Contains("groupPosts", queryFields);
        Assert.Contains("comments", queryFields);
        Assert.Contains("contentEngagement", queryFields);
        Assert.Contains("savedContent", queryFields);
        Assert.Contains("likedUsers", queryFields);
        Assert.Contains("storyViewers", queryFields);
        Assert.Contains("taggedUsers", queryFields);
        Assert.Contains("mentionedUsers", queryFields);
        Assert.Contains("groupJoinRequests", queryFields);
        Assert.Contains("recommendFeed", queryFields);
        Assert.Contains("recommendReels", queryFields);
        Assert.Contains("postDetail", queryFields);
        Assert.Contains("postDetails", queryFields);
        Assert.Contains("homeStories", queryFields);
        Assert.Contains("myStories", queryFields);
        Assert.Contains("premiumPlans", queryFields);
        Assert.Contains("premiumOrder", queryFields);
        Assert.Contains("fastSearch", queryFields);
        Assert.Contains("searchUsers", queryFields);
        Assert.Contains("searchDirectContacts", queryFields);
        Assert.Contains("searchFriends", queryFields);
        Assert.Contains("searchGroups", queryFields);
        Assert.Contains("searchFeedPosts", queryFields);
        Assert.Contains("searchGroupPosts", queryFields);
        Assert.Contains("searchReels", queryFields);
        Assert.Contains("myConversations", queryFields);
        Assert.Contains("myDirectConversations", queryFields);
        Assert.Contains("conversation", queryFields);
        Assert.Contains("conversationMessages", queryFields);
        Assert.Contains("userPresence", queryFields);
        Assert.Contains("notifications", queryFields);
        Assert.DoesNotContain("object", queryFields);
        Assert.DoesNotContain("association", queryFields);
        Assert.DoesNotContain("reelCandidates", queryFields);
        Assert.DoesNotContain("recommendationItem", queryFields);
        Assert.DoesNotContain("reelRecommendationItem", queryFields);
        Assert.DoesNotContain("userSearchResult", queryFields);
        Assert.DoesNotContain("groupSearchResult", queryFields);
        Assert.DoesNotContain("feedPostSearchResult", queryFields);
        Assert.DoesNotContain("groupPostSearchResult", queryFields);
        Assert.DoesNotContain("reelSearchResult", queryFields);
        Assert.DoesNotContain("userById", queryFields);
        Assert.DoesNotContain("hello", queryFields);
        Assert.DoesNotContain("_service", queryFields);
        Assert.DoesNotContain("_entities", queryFields);
        Assert.DoesNotContain("validateGatewaySession", queryFields);
        Assert.DoesNotContain("paymentPremiumState", queryFields);

        Assert.Contains("createUser", mutationFields);
        Assert.Contains("recordGroupVisit", mutationFields);
        Assert.Contains("createFeedPost", mutationFields);
        Assert.Contains("createGroupPost", mutationFields);
        Assert.Contains("createReel", mutationFields);
        Assert.Contains("createGroup", mutationFields);
        Assert.Contains("updateGroup", mutationFields);
        Assert.Contains("updatePost", mutationFields);
        Assert.Contains("deleteContent", mutationFields);
        Assert.Contains("removeUserAvatar", mutationFields);
        Assert.Contains("removeUserBackground", mutationFields);
        Assert.Contains("removeGroupAvatar", mutationFields);
        Assert.Contains("removeGroupBackground", mutationFields);
        Assert.Contains("inviteGroupUser", mutationFields);
        Assert.Contains("requestJoinGroup", mutationFields);
        Assert.Contains("approveGroupJoinRequest", mutationFields);
        Assert.Contains("sendFriendRequest", mutationFields);
        Assert.Contains("acceptFriendRequest", mutationFields);
        Assert.Contains("followUser", mutationFields);
        Assert.Contains("blockUser", mutationFields);
        Assert.Contains("createNormalStory", mutationFields);
        Assert.Contains("createShareStory", mutationFields);
        Assert.Contains("deleteStory", mutationFields);
        Assert.Contains("createPremiumCheckout", mutationFields);
        Assert.Contains("createDirectConversation", mutationFields);
        Assert.Contains("createGroupConversation", mutationFields);
        Assert.Contains("sendMessage", mutationFields);
        Assert.Contains("markConversationRead", mutationFields);
        Assert.Contains("markNotificationRead", mutationFields);
        Assert.Contains("markAllNotificationsRead", mutationFields);
        Assert.Contains("recordSearchResultView", mutationFields);
        Assert.DoesNotContain("addObject", mutationFields);
        Assert.DoesNotContain("register", mutationFields);
        Assert.DoesNotContain("setPaymentValidDate", mutationFields);

        Assert.Contains("conversationEvents", subscriptionFields);
        Assert.Contains("inboxEvents", subscriptionFields);
        Assert.Contains("presenceEvents", subscriptionFields);
        Assert.Contains("notificationCreated", subscriptionFields);

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
            new[] { "authorId", "content", "media", "mentionedUserIds", "privacy", "taggedUserIds" },
            createFeedPostInput.GetProperty("inputFields")
                .EnumerateArray()
                .Select(field => field.GetProperty("name").GetString()!)
                .OrderBy(name => name));

        var updatePostInput = Assert.Single(
            types,
            type => TypeName(type) == "UpdatePostInput");
        Assert.Equal(
            new[] { "content", "id", "media", "privacy" },
            updatePostInput.GetProperty("inputFields")
                .EnumerateArray()
                .Select(field => field.GetProperty("name").GetString()!)
                .OrderBy(name => name));

        var groupPost = Assert.Single(types, type => TypeName(type) == "GroupPostDetail");
        Assert.Contains("group", FieldNames(groupPost));
        Assert.Contains("mentions", FieldNames(groupPost));
        var feedPost = Assert.Single(types, type => TypeName(type) == "FeedPostDetail");
        Assert.Contains("sharedSource", FieldNames(feedPost));
        Assert.Contains("mentions", FieldNames(feedPost));
        Assert.Contains("taggedUsers", FieldNames(feedPost));
        var sharedPostSource = Assert.Single(types, type => TypeName(type) == "SharedPostSourceResult");
        Assert.Equal(
            new[] { "author", "content", "id", "isAvailable", "media", "mentions", "type" },
            FieldNames(sharedPostSource).OrderBy(name => name));

        var commentThreadItem = Assert.Single(types, type => TypeName(type) == "CommentThreadItemResult");
        Assert.Contains("mentions", FieldNames(commentThreadItem));
        var mentionUser = Assert.Single(types, type => TypeName(type) == "MentionUserResult");
        Assert.Equal(
            new[] { "available", "name", "userId" },
            FieldNames(mentionUser).OrderBy(name => name));

        var friendSuggestion = Assert.Single(
            types,
            type => TypeName(type) == "FriendSuggestionResult");
        Assert.Equal(
            new[] { "mutualFriendCount", "mutualFriends", "profile" },
            FieldNames(friendSuggestion).OrderBy(name => name));

        var recommendationItem = Assert.Single(
            types,
            type => TypeName(type) == "RecommendationItem");
        Assert.Equal(
            new[] { "post", "postId" },
            FieldNames(recommendationItem).OrderBy(name => name));

        var reelRecommendationItem = Assert.Single(
            types,
            type => TypeName(type) == "ReelRecommendationItem");
        Assert.Equal(
            new[] { "reel", "reelId" },
            FieldNames(reelRecommendationItem).OrderBy(name => name));

        Assert.Equal(
            new[] { "user" },
            FieldNames(Assert.Single(types, type => TypeName(type) == "UserSearchResult")));
        Assert.Equal(
            new[] { "group" },
            FieldNames(Assert.Single(types, type => TypeName(type) == "GroupSearchResult")));
        Assert.Equal(
            new[] { "post" },
            FieldNames(Assert.Single(types, type => TypeName(type) == "FeedPostSearchResult")));
        Assert.Equal(
            new[] { "post" },
            FieldNames(Assert.Single(types, type => TypeName(type) == "GroupPostSearchResult")));
        Assert.Equal(
            new[] { "author", "reel" },
            FieldNames(Assert.Single(types, type => TypeName(type) == "ReelSearchResult")));

        var fusionUser = Assert.Single(types, type => TypeName(type) == "User");
        Assert.Equal(
            new[]
            {
                "avatar",
                "bio",
                "followerCount",
                "followingCount",
                "friendCount",
                "id",
                "isVerified",
                "name",
                "privacy"
            },
            FieldNames(fusionUser).OrderBy(name => name));

        var notificationActionType = Assert.Single(
            types,
            type => TypeName(type) == "NotificationActionType");
        Assert.Equal(
            new[]
            {
                "COMMENT",
                "FRIEND_ACCEPT",
                "FRIEND_REQUEST",
                "GROUP_INVITE",
                "GROUP_JOIN_ACCEPTED",
                "GROUP_JOIN_REQUEST",
                "LIKE",
                "MENTION",
                "SHARE",
                "TAG"
            },
            notificationActionType.GetProperty("enumValues")
                .EnumerateArray()
                .Select(FieldName)
                .OrderBy(name => name));

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
                    ["Gateway:SubgraphSecrets:Authentication"] = "auth-test-secret-at-least-32-bytes",
                    ["Gateway:SubgraphSecrets:SocialGraph"] = "socialgraph-test-secret-at-least-32-bytes",
                    ["Gateway:SubgraphSecrets:Recommendation"] = "recommendation-test-secret-at-least-32-bytes",
                    ["Gateway:SubgraphSecrets:Search"] = "search-test-secret-at-least-32-bytes",
                    ["Gateway:SubgraphSecrets:Messaging"] = "messaging-test-secret-at-least-32-bytes",
                    ["Gateway:SubgraphSecrets:Notification"] = "notification-test-secret-at-least-32-bytes",
                    ["Gateway:SubgraphSecrets:Payment"] = "payment-test-secret-at-least-32-bytes",
                    ["Gateway:SessionCacheSeconds"] = "30",
                    ["Jwt:Issuer"] = "fakebook-auth",
                    ["Jwt:Audience"] = "fakebook",
                    ["Jwt:SigningKey"] = "gateway-test-jwt-key-at-least-32-bytes",
                    ["Subgraphs:Authentication:Url"] = "http://localhost:1001/graphql"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(Subgraphs);
                services
                    .AddHttpClient("fusion")
                    .ConfigurePrimaryHttpMessageHandler<TestSubgraphHandler>();
                foreach (var clientName in new[]
                {
                    "auth-fusion",
                    "socialgraph-fusion",
                    "recommendation-fusion",
                    "search-fusion",
                    "messaging-fusion",
                    "notification-fusion",
                    "payment-fusion"
                })
                {
                    services
                        .AddHttpClient(clientName)
                        .ConfigurePrimaryHttpMessageHandler<TestSubgraphHandler>();
                }
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
