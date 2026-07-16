namespace fakebookGateway.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed partial class TestSubgraphHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();

    public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

    public void Clear() => _requests.Clear();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(
            header => header.Key,
            header => string.Join(",", header.Value),
            StringComparer.OrdinalIgnoreCase);
        var service = request.RequestUri?.Port switch
        {
            5001 or 1001 => "Authentication",
            5223 or 1002 => "SocialGraph",
            8000 or 1003 => "Recommendation",
            5191 or 1004 => "Search",
            5014 or 1005 => "Notification",
            5013 or 1006 => "Messaging",
            5016 or 1007 => "Payment",
            _ => "Unknown"
        };
        _requests.Enqueue(new CapturedRequest(service, body, headers));

        if (service == "Messaging" &&
            request.Headers.Accept.Any(value =>
                string.Equals(value.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) &&
            body.Contains("subscription", StringComparison.OrdinalIgnoreCase))
        {
            var eventId = "11111111-1111-1111-1111-111111111111";
            var eventStream =
                "event: next\n" +
                $"data: {{\"data\":{{\"inboxEvents\":{{\"eventId\":\"{eventId}\",\"kind\":\"MESSAGE_CREATED\",\"occurredAt\":\"2026-07-16T00:00:00Z\"}}}}}}\n\n" +
                "event: complete\n\n";
            var streamContent = new StringContent(eventStream, Encoding.UTF8);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream")
            {
                CharSet = "utf-8"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = streamContent
            };
        }

        var payload = string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body)!;
        if (payload is JsonObject variableRequest &&
            variableRequest["variables"] is JsonArray variables)
        {
            var query = variableRequest["query"]?.GetValue<string>() ?? string.Empty;
            var lines = variables.Select((variable, index) =>
            {
                var itemRequest = new JsonObject
                {
                    ["query"] = query,
                    ["variables"] = variable?.DeepClone()
                };
                var result = Process(service, itemRequest);
                result["variableIndex"] = index;
                return result.ToJsonString();
            });
            var jsonLinesContent = new StringContent(
                string.Join("\n", lines) + "\n",
                Encoding.UTF8);
            jsonLinesContent.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl")
            {
                CharSet = "utf-8"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = jsonLinesContent
            };
        }

        JsonNode responsePayload = payload is JsonArray batch
            ? new JsonArray(batch.Select(item => ProcessPayload(service, item!.AsObject())).ToArray())
            : ProcessPayload(service, payload.AsObject());

        var content = new StringContent(responsePayload.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/graphql-response+json")
        {
            CharSet = "utf-8"
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        };
    }

    private static JsonNode ProcessPayload(string service, JsonObject request)
    {
        if (request["variables"] is not JsonArray variables)
        {
            return Process(service, request);
        }

        var query = request["query"]?.GetValue<string>() ?? string.Empty;
        return new JsonArray(variables.Select(variable =>
        {
            var itemRequest = new JsonObject
            {
                ["query"] = query,
                ["variables"] = variable?.DeepClone()
            };
            return (JsonNode)Process(service, itemRequest);
        }).ToArray());
    }

    private static JsonObject Process(string service, JsonObject request) => service switch
    {
        "Authentication" => AuthenticationResponse(),
        "Recommendation" => RecommendationResponse(request),
        "SocialGraph" => SocialGraphResponse(request),
        "Search" => SearchResponse(request),
        "Messaging" => MessagingResponse(request),
        "Notification" => NotificationResponse(request),
        _ => new JsonObject
        {
            ["errors"] = new JsonArray(new JsonObject { ["message"] = "Unexpected test transport." })
        }
    };

    private static JsonObject SearchResponse(JsonObject request)
    {
        var query = request["query"]?.GetValue<string>() ?? string.Empty;
        if (query.Contains("fastSearch", StringComparison.Ordinal))
        {
            var fieldName = GetResponseFieldName(query, "fastSearch");
            return new JsonObject
            {
                ["data"] = new JsonObject
                {
                    [fieldName] = new JsonArray(
                        new JsonObject
                        {
                            ["__typename"] = "UserSearchResult",
                            ["referenceId"] = "501"
                        },
                        new JsonObject
                        {
                            ["__typename"] = "GroupSearchResult",
                            ["referenceId"] = "601"
                        })
                }
            };
        }

        var searchFields = new[]
        {
            "searchUsers",
            "searchGroups",
            "searchFeedPosts",
            "searchGroupPosts",
            "searchReels"
        }.Where(field => query.Contains(field, StringComparison.Ordinal));
        var data = new JsonObject();
        foreach (var searchField in searchFields)
        {
            var resultType = searchField switch
            {
                "searchUsers" => "UserSearchResult",
                "searchGroups" => "GroupSearchResult",
                "searchFeedPosts" => "FeedPostSearchResult",
                "searchGroupPosts" => "GroupPostSearchResult",
                _ => "ReelSearchResult"
            };
            var referenceId = searchField switch
            {
                "searchUsers" => "501",
                "searchGroups" => "601",
                "searchFeedPosts" => "1001",
                "searchGroupPosts" => "1002",
                _ => "2001"
            };
            data[GetResponseFieldName(query, searchField)] = new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject
                {
                    ["__typename"] = resultType,
                    ["referenceId"] = referenceId
                }),
                ["pageInfo"] = new JsonObject
                {
                    ["pageNumber"] = 1,
                    ["pageSize"] = 1,
                    ["hasPreviousPage"] = false,
                    ["hasNextPage"] = false
                }
            };
        }

        return new JsonObject { ["data"] = data };
    }

    private static JsonObject MessagingResponse(JsonObject request)
    {
        var query = request["query"]?.GetValue<string>() ?? string.Empty;
        var fieldName = GetResponseFieldName(query, "myConversations");
        var items = query.Contains("participants", StringComparison.Ordinal)
            ? new JsonArray(new JsonObject
            {
                ["id"] = "11111111-2222-3333-4444-555555555555",
                ["participants"] = new JsonArray(new JsonObject
                {
                    ["userId"] = 501,
                    ["role"] = "MEMBER",
                    ["joinedAt"] = "2026-07-12T00:00:00Z",
                    ["leftAt"] = null,
                    ["lastDeliveredSequence"] = 0,
                    ["lastReadSequence"] = 0,
                    ["user"] = new JsonObject
                    {
                        ["__typename"] = "User",
                        ["id"] = 501
                    }
                })
            })
            : new JsonArray();
        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                [fieldName] = new JsonObject
                {
                    ["items"] = items,
                    ["pageInfo"] = new JsonObject
                    {
                        ["startCursor"] = null,
                        ["endCursor"] = null,
                        ["hasNextPage"] = false,
                        ["hasPreviousPage"] = false
                    }
                }
            }
        };
    }

    private static JsonObject NotificationResponse(JsonObject request)
    {
        var query = request["query"]?.GetValue<string>() ?? string.Empty;
        var fieldName = GetResponseFieldName(query, "notifications");
        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                [fieldName] = new JsonObject
                {
                    ["nodes"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "7001",
                        ["actionType"] = "LIKE"
                    }),
                    ["edges"] = new JsonArray(),
                    ["pageInfo"] = new JsonObject
                    {
                        ["hasNextPage"] = false,
                        ["endCursor"] = null
                    },
                    ["unreadCount"] = 1
                }
            }
        };
    }

    private static JsonObject AuthenticationResponse() => new()
    {
        ["data"] = new JsonObject
        {
            ["validateGatewaySession"] = new JsonObject
            {
                ["isValid"] = true,
                ["userId"] = 42,
                ["sessionId"] = 99,
                ["status"] = 0,
                ["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O")
            }
        }
    };

    private static JsonObject RecommendationResponse(JsonObject request)
    {
        var query = request["query"]?.GetValue<string>() ?? string.Empty;
        if (query.Contains("recommendReels", StringComparison.Ordinal))
        {
            var reelsFieldName = GetResponseFieldName(query, "recommendReels");
            return new JsonObject
            {
                ["data"] = new JsonObject
                {
                    [reelsFieldName] = new JsonArray(
                        new JsonObject { ["reelId"] = "2001" })
                }
            };
        }

        var fieldName = GetResponseFieldName(query, "recommendFeed");
        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                [fieldName] = new JsonArray(
                    new JsonObject { ["postId"] = "1001" },
                    new JsonObject { ["postId"] = "1002" })
            }
        };
    }

    private static JsonObject SocialGraphResponse(JsonObject request)
    {
        var query = request["query"]?.GetValue<string>() ?? string.Empty;
        if (query.Contains("userById", StringComparison.Ordinal))
        {
            var userId = FindId(request["variables"], "id");
            return LookupResponse(query, "userById", SearchUser(long.Parse(userId)));
        }

        if (query.Contains("_entities", StringComparison.Ordinal))
        {
            var userId = FindId(request["variables"], "id");
            return new JsonObject
            {
                ["data"] = new JsonObject
                {
                    [GetResponseFieldName(query, "_entities")] = new JsonArray(
                        new JsonObject
                        {
                            ["__typename"] = "User",
                            ["id"] = long.Parse(userId),
                            ["name"] = $"search-user-{userId}",
                            ["avatar"] = "search-user.jpg",
                            ["bio"] = "search user",
                            ["isVerified"] = true
                        })
                }
            };
        }

        var searchLookups = new JsonObject();
        if (query.Contains("userSearchResult", StringComparison.Ordinal))
        {
            const string referenceId = "501";
            searchLookups[GetResponseFieldName(query, "userSearchResult")] = new JsonObject
            {
                ["referenceId"] = referenceId,
                ["user"] = SearchUser(501)
            };
        }

        if (query.Contains("groupSearchResult", StringComparison.Ordinal))
        {
            const string referenceId = "601";
            searchLookups[GetResponseFieldName(query, "groupSearchResult")] = new JsonObject
            {
                ["referenceId"] = referenceId,
                ["group"] = SearchGroup(601)
            };
        }

        if (query.Contains("feedPostSearchResult", StringComparison.Ordinal))
        {
            const string referenceId = "1001";
            searchLookups[GetResponseFieldName(query, "feedPostSearchResult")] = new JsonObject
            {
                ["referenceId"] = referenceId,
                ["post"] = Post(referenceId)
            };
        }

        if (query.Contains("groupPostSearchResult", StringComparison.Ordinal))
        {
            const string referenceId = "1002";
            searchLookups[GetResponseFieldName(query, "groupPostSearchResult")] = new JsonObject
            {
                ["referenceId"] = referenceId,
                ["post"] = Post(referenceId)
            };
        }

        if (query.Contains("reelSearchResult", StringComparison.Ordinal))
        {
            const string referenceId = "2001";
            searchLookups[GetResponseFieldName(query, "reelSearchResult")] = new JsonObject
            {
                ["referenceId"] = referenceId,
                ["reel"] = Reel(referenceId)
            };
        }

        if (searchLookups.Count > 0)
        {
            return new JsonObject { ["data"] = searchLookups };
        }

        if (query.Contains("reelRecommendationItem", StringComparison.Ordinal))
        {
            var reelId = FindId(request["variables"], "reelId");
            return LookupResponse(query, "reelRecommendationItem", new JsonObject
            {
                ["reelId"] = reelId,
                ["reel"] = Reel(reelId)
            });
        }

        var fieldName = GetResponseFieldName(query, "recommendationItem");
        var postId = FindId(request["variables"], "postId");
        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                [fieldName] = new JsonObject
                {
                    ["postId"] = postId,
                    ["post"] = Post(postId)
                }
            }
        };
    }

    private static JsonObject LookupResponse(string query, string field, JsonObject value) =>
        new()
        {
            ["data"] = new JsonObject
            {
                [GetResponseFieldName(query, field)] = value
            }
        };

    private static JsonObject SearchGroup(long id) => new()
    {
        ["id"] = id,
        ["avatar"] = "search-group.jpg",
        ["background"] = "search-group-background.jpg",
        ["name"] = $"search-group-{id}",
        ["bio"] = "search group",
        ["privacy"] = 0,
        ["create"] = "2026-07-12T00:00:00Z",
        ["memberCount"] = 10,
        ["adminCount"] = 1
    };

    private static JsonObject SearchUser(long id) => new()
    {
        ["__typename"] = "User",
        ["id"] = id,
        ["name"] = $"search-user-{id}",
        ["avatar"] = "search-user.jpg",
        ["bio"] = "search user",
        ["isVerified"] = true
    };

    private static JsonObject Reel(string reelId) => new()
    {
        ["id"] = long.Parse(reelId),
        ["type"] = 5,
        ["content"] = "recommended-reel",
        ["privacy"] = 0,
        ["create"] = "2026-07-12T00:00:00Z",
        ["authorId"] = 201,
        ["media"] = new JsonArray()
    };

    private static JsonObject Post(string postId)
    {
        if (postId == "1002")
        {
            return new JsonObject
            {
                ["__typename"] = "GroupPostDetail",
                ["id"] = 1002,
                ["type"] = 3,
                ["content"] = "group-post",
                ["privacy"] = 0,
                ["create"] = "2026-07-12T00:00:00Z",
                ["author"] = Author(202),
                ["group"] = new JsonObject
                {
                    ["id"] = 300,
                    ["name"] = "Fusion Group",
                    ["avatar"] = "group.jpg",
                    ["canJoin"] = true
                },
                ["media"] = new JsonArray()
            };
        }

        return new JsonObject
        {
            ["__typename"] = "FeedPostDetail",
            ["id"] = 1001,
            ["type"] = 2,
            ["content"] = "feed-post",
            ["privacy"] = 0,
            ["create"] = "2026-07-12T00:00:00Z",
            ["author"] = Author(201),
            ["media"] = new JsonArray()
        };
    }

    private static JsonObject Author(long id) => new()
    {
        ["id"] = id,
        ["name"] = $"author-{id}",
        ["avatar"] = "author.jpg",
        ["isVerified"] = false,
        ["canFollow"] = true
    };

    private static string FindId(JsonNode? node, string fieldName)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Key.Contains(fieldName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value is JsonValue value)
                {
                    return value.ToString();
                }

                var nested = FindId(property.Value, fieldName);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var nested = FindId(item, fieldName);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return string.Empty;
    }

    private static string GetResponseFieldName(string query, string field)
    {
        var match = RootFieldRegex().Match(query);
        while (match.Success)
        {
            if (string.Equals(match.Groups["field"].Value, field, StringComparison.Ordinal))
            {
                return match.Groups["alias"].Success
                    ? match.Groups["alias"].Value
                    : field;
            }

            match = match.NextMatch();
        }

        return field;
    }

    [GeneratedRegex("(?:(?<alias>[_A-Za-z][_0-9A-Za-z]*)\\s*:\\s*)?(?<field>[_A-Za-z][_0-9A-Za-z]*)\\s*\\(")]
    private static partial Regex RootFieldRegex();
}

public sealed record CapturedRequest(
    string Service,
    string Body,
    IReadOnlyDictionary<string, string> Headers);
