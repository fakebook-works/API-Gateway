using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace fakebookGateway.Gateway;

public sealed class GraphQlCookieResponseMiddleware(
    RequestDelegate next,
    ILogger<GraphQlCookieResponseMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/graphql", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (AcceptsEventStream(context.Request))
        {
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await next(context);
            return;
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);

            buffer.Position = 0;
            if (!ShouldProcess(context, buffer))
            {
                await buffer.CopyToAsync(originalBody, context.RequestAborted);
                return;
            }

            JsonNode? root;
            try
            {
                root = await JsonNode.ParseAsync(buffer, cancellationToken: context.RequestAborted);
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Unable to parse GraphQL response for cookie processing.");
                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody, context.RequestAborted);
                return;
            }

            if (root is null)
            {
                return;
            }

            var changed = ProcessNode(root, context);
            if (!changed)
            {
                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody, context.RequestAborted);
                return;
            }

            context.Response.Headers.ContentLength = null;
            await JsonSerializer.SerializeAsync(originalBody, root, JsonOptions, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static bool ShouldProcess(HttpContext context, Stream body)
    {
        var contentType = context.Response.ContentType;
        return body.Length > 0 &&
            context.Response.StatusCode < 500 &&
            !string.IsNullOrWhiteSpace(contentType) &&
            (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
             contentType.Contains("+json", StringComparison.OrdinalIgnoreCase));
    }

    private static bool AcceptsEventStream(HttpRequest request)
    {
        foreach (var value in request.Headers.Accept)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var candidate in value.Split(','))
            {
                var mediaType = candidate.AsSpan().Trim();
                var parameterIndex = mediaType.IndexOf(';');
                if (parameterIndex >= 0)
                {
                    mediaType = mediaType[..parameterIndex].TrimEnd();
                }

                if (mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ProcessNode(JsonNode node, HttpContext context)
    {
        var changed = false;

        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("refreshToken", out var refreshTokenNode) &&
                refreshTokenNode is JsonValue)
            {
                obj["refreshToken"] = null;
                changed = true;
            }

            if (LooksLikeCookieInstruction(obj))
            {
                if (!context.Items.ContainsKey(GatewayConstants.CookieInstructionAppliedItem))
                {
                    GatewayCookieInstructionProcessor.Apply(context, obj);
                }

                if (obj.TryGetPropertyValue("value", out _))
                {
                    obj["value"] = null;
                    changed = true;
                }
            }

            foreach (var property in obj.ToList())
            {
                if (property.Value is not null)
                {
                    changed |= ProcessNode(property.Value, context);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    changed |= ProcessNode(item, context);
                }
            }
        }

        return changed;
    }

    private static bool LooksLikeCookieInstruction(JsonObject obj) =>
        obj.ContainsKey("operation") &&
        obj.ContainsKey("name") &&
        obj.ContainsKey("path") &&
        obj.ContainsKey("maxAgeSeconds");

}

internal static class JsonObjectExtensions
{
    public static string? TryGetString(this JsonObject obj, string propertyName)
    {
        return obj.TryGetPropertyValue(propertyName, out var value) &&
               value is JsonValue jsonValue &&
               jsonValue.TryGetValue<string>(out var result)
            ? result
            : null;
    }

    public static bool? TryGetBoolean(this JsonObject obj, string propertyName)
    {
        return obj.TryGetPropertyValue(propertyName, out var value) &&
               value is JsonValue jsonValue &&
               jsonValue.TryGetValue<bool>(out var result)
            ? result
            : null;
    }

    public static int? TryGetInt32(this JsonObject obj, string propertyName)
    {
        return obj.TryGetPropertyValue(propertyName, out var value) &&
               value is JsonValue jsonValue &&
               jsonValue.TryGetValue<int>(out var result)
            ? result
            : null;
    }

    public static DateTimeOffset? TryGetDateTimeOffset(this JsonObject obj, string propertyName)
    {
        return obj.TryGetPropertyValue(propertyName, out var value) &&
               value is JsonValue jsonValue &&
               jsonValue.TryGetValue<DateTimeOffset>(out var result)
            ? result
            : null;
    }
}
