using System.Text.Json;

namespace fakebookGateway.Gateway;

public sealed class GatewayEdgeMiddleware(RequestDelegate next)
{
    private const string LegacyUsernameHeader = "X-Username";

    private static readonly string[] TrustedRequestHeaders =
    [
        GatewayConstants.UserIdHeader,
        GatewayConstants.SessionIdHeader,
        LegacyUsernameHeader,
        GatewayConstants.GatewaySecretHeader,
        GatewayConstants.RefreshTokenHeader,
        GatewayConstants.LegacyInternalUserIdHeader,
        GatewayConstants.InternalAuthenticationServiceSecretHeader,
        GatewayConstants.InternalSocialGraphServiceSecretHeader,
        GatewayConstants.InternalRecommendationServiceSecretHeader,
        GatewayConstants.InternalSearchServiceSecretHeader,
        GatewayConstants.InternalNotificationServiceSecretHeader,
        GatewayConstants.InternalMessengerServiceSecretHeader,
        GatewayConstants.PaymentSecretHeader
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        foreach (var header in TrustedRequestHeaders)
        {
            context.Request.Headers.Remove(header);
        }

        foreach (var header in context.Request.Headers.Keys
                     .Where(name => name.StartsWith("X-Internal-", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            context.Request.Headers.Remove(header);
        }

        var correlationId = context.Request.Headers.TryGetValue(
                GatewayConstants.CorrelationIdHeader,
                out var providedCorrelationId) &&
            !string.IsNullOrWhiteSpace(providedCorrelationId.ToString())
                ? providedCorrelationId.ToString()
                : Guid.NewGuid().ToString("N");

        context.Items[GatewayConstants.CorrelationIdHeader] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[GatewayConstants.CorrelationIdHeader] = correlationId;
            return Task.CompletedTask;
        });

        await next(context);
    }
}

public sealed class GatewaySessionValidationMiddleware(
    RequestDelegate next,
    ILogger<GatewaySessionValidationMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        IAuthSessionValidator sessionValidator)
    {
        if (!IsGraphQlRequest(context))
        {
            await next(context);
            return;
        }

        var hasBearerToken = context.Request.Headers.Authorization
            .ToString()
            .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

        if (!hasBearerToken)
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await WriteAuthErrorAsync(context, "Authentication is required.");
            return;
        }

        var userId = context.User.GetLongClaim(GatewayConstants.UserIdClaim);
        var sessionId = context.User.GetLongClaim(GatewayConstants.SessionIdClaim);
        if (userId is null || sessionId is null)
        {
            logger.LogWarning("Rejected access token missing user_id or sid claims.");
            await WriteAuthErrorAsync(context, "Authentication is required.");
            return;
        }

        var validation = await sessionValidator.ValidateAsync(
            userId.Value,
            sessionId.Value,
            context.RequestAborted);

        if (!validation.IsValid)
        {
            logger.LogWarning(
                "Rejected access token for invalid session {SessionId} of user {UserId}.",
                sessionId.Value,
                userId.Value);

            await WriteAuthErrorAsync(context, "Authentication is required.");
            return;
        }

        context.Items[GatewayConstants.UserIdItem] = userId.Value.ToString();
        context.Items[GatewayConstants.SessionIdItem] = sessionId.Value.ToString();

        await next(context);
    }

    private static bool IsGraphQlRequest(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/graphql", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteAuthErrorAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            errors = new[]
            {
                new
                {
                    message,
                    extensions = new
                    {
                        code = "UNAUTHENTICATED"
                    }
                }
            }
        }));
    }
}
