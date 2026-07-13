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
            5001 => "Authentication",
            5223 => "SocialGraph",
            8000 => "Recommendation",
            _ => "Unknown"
        };
        _requests.Enqueue(new CapturedRequest(service, body, headers));

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
        _ => new JsonObject
        {
            ["errors"] = new JsonArray(new JsonObject { ["message"] = "Unexpected test transport." })
        }
    };

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
        var fieldName = GetResponseFieldName(query, "recommendationItem");
        var postId = FindPostId(request["variables"]);
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

    private static string FindPostId(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Key.Contains("postId", StringComparison.OrdinalIgnoreCase) &&
                    property.Value is JsonValue value)
                {
                    return value.ToString();
                }

                var nested = FindPostId(property.Value);
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
                var nested = FindPostId(item);
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
