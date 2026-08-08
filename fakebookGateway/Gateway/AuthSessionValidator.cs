using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace fakebookGateway.Gateway;

public interface IAuthSessionValidator
{
    /// <param name="forceRefresh">
    /// Skips the local result cache. Long-lived subscriptions re-check their session periodically
    /// and must observe a revocation rather than the value cached when the stream opened.
    /// </param>
    Task<GatewaySessionValidationResult> ValidateAsync(
        long userId,
        long sessionId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}

public sealed class AuthSessionValidator(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    IOptionsMonitor<GatewayOptions> options,
    ILogger<AuthSessionValidator> logger) : IAuthSessionValidator
{
    private readonly ConcurrentDictionary<string, Lazy<Task<GatewaySessionValidationResult>>> _inflight = new();
    private const int MaxValidationResponseBytes = 64 * 1024;

    private const string GraphQlQuery = """
        query ValidateGatewaySession($input: GatewaySessionValidationInput!) {
          validateGatewaySession(input: $input) {
            isValid
            userId
            sessionId
            status
            expiresAt
          }
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GatewaySessionValidationResult> ValidateAsync(
        long userId,
        long sessionId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var cacheKey = $"auth-session:{userId}:{sessionId}";
        if (!forceRefresh &&
            cache.TryGetValue(cacheKey, out GatewaySessionValidationResult? cached) &&
            cached is not null)
        {
            return cached;
        }

        // Forced refreshes share their own in-flight slot, so several open subscriptions belonging
        // to one session collapse into a single upstream call instead of one call per stream.
        var inflightKey = forceRefresh ? $"force:{cacheKey}" : cacheKey;
        var pending = _inflight.GetOrAdd(
            inflightKey,
            _ => new Lazy<Task<GatewaySessionValidationResult>>(
                () => ValidateCoreAsync(userId, sessionId, forceRefresh),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var task = pending.Value;
        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (task.IsCompleted)
            {
                // Forced refreshes use a separate key. Removing the ordinary cache key here
                // leaves the `force:` entry resident forever, so every later subscription
                // watchdog check reuses the first result and never observes a revoked session.
                if (_inflight.TryGetValue(inflightKey, out var current) && ReferenceEquals(current, pending))
                {
                    _inflight.TryRemove(inflightKey, out _);
                }
            }
        }
    }

    private async Task<GatewaySessionValidationResult> ValidateCoreAsync(
        long userId,
        long sessionId,
        bool forceRefresh = false)
    {
        var cacheKey = $"auth-session:{userId}:{sessionId}";
        if (!forceRefresh &&
            cache.TryGetValue(cacheKey, out GatewaySessionValidationResult? cached) &&
            cached is not null)
        {
            return cached;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, string.Empty)
        {
            Content = JsonContent.Create(
                new
                {
                    query = GraphQlQuery,
                    variables = new
                    {
                        input = new
                        {
                            userId,
                            sessionId
                        }
                    }
                },
                options: JsonOptions)
        };

        var secret = options.CurrentValue.ResolveSubgraphSecret(GatewaySubgraphs.Authentication);
        if (!string.IsNullOrWhiteSpace(secret))
        {
            request.Headers.TryAddWithoutValidation(GatewayConstants.GatewaySecretHeader, secret);
        }

        var client = httpClientFactory.CreateClient("auth-internal");
        using var response = await client.SendAsync(request, CancellationToken.None);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Auth session validation returned HTTP {StatusCode} for user {UserId}, session {SessionId}.",
                (int)response.StatusCode,
                userId,
                sessionId);

            var invalid = GatewaySessionValidationResult.Invalid(userId, sessionId);
            CacheResult(cacheKey, invalid);
            return invalid;
        }

        var responseBody = await ReadBoundedResponseAsync(response.Content, MaxValidationResponseBytes);
        if (responseBody is null)
        {
            logger.LogWarning(
                "Auth session validation response exceeded {MaximumBytes} bytes for user {UserId}, session {SessionId}.",
                MaxValidationResponseBytes,
                userId,
                sessionId);

            var invalid = GatewaySessionValidationResult.Invalid(userId, sessionId);
            CacheResult(cacheKey, invalid);
            return invalid;
        }

        using var document = TryParseValidationResponse(responseBody);
        if (document is null)
        {
            logger.LogWarning(
                "Auth session validation returned malformed JSON for user {UserId}, session {SessionId}.",
                userId,
                sessionId);
            var invalid = GatewaySessionValidationResult.Invalid(userId, sessionId);
            CacheResult(cacheKey, invalid);
            return invalid;
        }
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            var invalid = GatewaySessionValidationResult.Invalid(userId, sessionId);
            CacheResult(cacheKey, invalid);
            return invalid;
        }

        if (root.TryGetProperty("errors", out var errors) &&
            (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() > 0))
        {
            logger.LogWarning(
                "Auth session validation returned GraphQL errors for user {UserId}, session {SessionId}.",
                userId,
                sessionId);

            var invalid = GatewaySessionValidationResult.Invalid(userId, sessionId);
            CacheResult(cacheKey, invalid);
            return invalid;
        }

        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("validateGatewaySession", out var validation) ||
            validation.ValueKind != JsonValueKind.Object ||
            !validation.TryGetProperty("isValid", out var isValidProperty) ||
            isValidProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            var invalid = GatewaySessionValidationResult.Invalid(userId, sessionId);
            CacheResult(cacheKey, invalid);
            return invalid;
        }

        var result = new GatewaySessionValidationResult(
            isValidProperty.GetBoolean(),
            validation.TryGetInt64("userId"),
            validation.TryGetInt64("sessionId"),
            validation.TryGetInt16("status"),
            validation.TryGetDateTimeOffset("expiresAt"));

        // A successful response is only authoritative for the exact tuple that was
        // requested. Do not let a malformed/misrouted Auth response (for example one
        // missing the IDs and status) turn into a valid session for the Gateway caller.
        if (result.IsValid &&
            (result.UserId != userId ||
             result.SessionId != sessionId ||
             result.Status != 1 ||
             result.ExpiresAt is null ||
             result.ExpiresAt <= DateTimeOffset.UtcNow))
        {
            result = GatewaySessionValidationResult.Invalid(userId, sessionId);
        }

        CacheResult(cacheKey, result);
        return result;
    }

    private static async Task<byte[]?> ReadBoundedResponseAsync(
        HttpContent content,
        int maximumBytes)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(CancellationToken.None);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 8 * 1024));
        var chunk = new byte[8 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), CancellationToken.None);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximumBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), CancellationToken.None);
        }
    }

    private static JsonDocument? TryParseValidationResponse(ReadOnlyMemory<byte> response)
    {
        try
        {
            return JsonDocument.Parse(response, new JsonDocumentOptions
            {
                MaxDepth = 16,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void CacheResult(string cacheKey, GatewaySessionValidationResult result)
    {
        var ttlSeconds = result.IsValid
            ? Math.Max(1, options.CurrentValue.SessionCacheSeconds)
            : Math.Max(1, options.CurrentValue.InvalidSessionCacheSeconds);
        var ttl = TimeSpan.FromSeconds(ttlSeconds);

        if (result.ExpiresAt is not null)
        {
            var untilSessionExpiry = result.ExpiresAt.Value - DateTimeOffset.UtcNow;
            if (untilSessionExpiry <= TimeSpan.Zero)
            {
                return;
            }

            ttl = untilSessionExpiry < ttl ? untilSessionExpiry : ttl;
        }

        cache.Set(cacheKey, result, ttl);
    }
}

public sealed record GatewaySessionValidationResult(
    bool IsValid,
    long? UserId,
    long? SessionId,
    short? Status,
    DateTimeOffset? ExpiresAt)
{
    public static GatewaySessionValidationResult Invalid(long userId, long sessionId) =>
        new(false, userId, sessionId, null, null);
}

internal static class JsonElementExtensions
{
    public static long? TryGetInt64(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out var value)
            ? value
            : null;
    }

    public static short? TryGetInt16(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt16(out var value)
            ? value
            : null;
    }

    public static string? TryGetString(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    public static DateTimeOffset? TryGetDateTimeOffset(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               property.TryGetDateTimeOffset(out var value)
            ? value
            : null;
    }
}
