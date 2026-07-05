using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace fakebookGateway.Gateway;

public interface IAuthSessionValidator
{
    Task<GatewaySessionValidationResult> ValidateAsync(
        long userId,
        long sessionId,
        CancellationToken cancellationToken);
}

public sealed class AuthSessionValidator(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    IOptionsMonitor<GatewayOptions> options,
    ILogger<AuthSessionValidator> logger) : IAuthSessionValidator
{
    private const string GraphQlQuery = """
        query ValidateGatewaySession($input: GatewaySessionValidationInput!) {
          validateGatewaySession(input: $input) {
            isValid
            userId
            sessionId
            username
            status
            expiresAt
          }
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GatewaySessionValidationResult> ValidateAsync(
        long userId,
        long sessionId,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"auth-session:{userId}:{sessionId}";
        if (cache.TryGetValue(cacheKey, out GatewaySessionValidationResult? cached) && cached is not null)
        {
            return cached;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, string.Empty)
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

        var secret = options.CurrentValue.InternalSharedSecret;
        if (!string.IsNullOrWhiteSpace(secret))
        {
            request.Headers.TryAddWithoutValidation(GatewayConstants.GatewaySecretHeader, secret);
        }

        var client = httpClientFactory.CreateClient("auth-internal");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Auth session validation returned HTTP {StatusCode} for user {UserId}, session {SessionId}.",
                (int)response.StatusCode,
                userId,
                sessionId);

            return GatewaySessionValidationResult.Invalid(userId, sessionId);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            logger.LogWarning(
                "Auth session validation returned GraphQL errors for user {UserId}, session {SessionId}.",
                userId,
                sessionId);

            return GatewaySessionValidationResult.Invalid(userId, sessionId);
        }

        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("validateGatewaySession", out var validation) ||
            validation.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return GatewaySessionValidationResult.Invalid(userId, sessionId);
        }

        var result = new GatewaySessionValidationResult(
            validation.GetProperty("isValid").GetBoolean(),
            validation.TryGetInt64("userId") ?? userId,
            validation.TryGetInt64("sessionId") ?? sessionId,
            validation.TryGetString("username"),
            validation.TryGetInt16("status"),
            validation.TryGetDateTimeOffset("expiresAt"));

        CacheResult(cacheKey, result);
        return result;
    }

    private void CacheResult(string cacheKey, GatewaySessionValidationResult result)
    {
        var ttlSeconds = Math.Max(1, options.CurrentValue.SessionCacheSeconds);
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
    string? Username,
    short? Status,
    DateTimeOffset? ExpiresAt)
{
    public static GatewaySessionValidationResult Invalid(long userId, long sessionId) =>
        new(false, userId, sessionId, null, null, null);
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
