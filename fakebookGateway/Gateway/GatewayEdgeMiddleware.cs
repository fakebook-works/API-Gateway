using System.Text.Json;
using Microsoft.Extensions.Options;

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

        var correlationId = TryGetSafeCorrelationId(context.Request, out var providedCorrelationId)
            ? providedCorrelationId
            : Guid.NewGuid().ToString("N");

        context.Items[GatewayConstants.CorrelationIdHeader] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[GatewayConstants.CorrelationIdHeader] = correlationId;
            return Task.CompletedTask;
        });

        await next(context);
    }

    private static bool TryGetSafeCorrelationId(HttpRequest request, out string value)
    {
        value = string.Empty;
        if (!request.Headers.TryGetValue(GatewayConstants.CorrelationIdHeader, out var values) ||
            values.Count != 1)
        {
            return false;
        }

        var candidate = values[0];
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.Length > GatewayConstants.MaxCorrelationIdLength ||
            candidate.Any(character => character is < '\x21' or > '\x7e'))
        {
            return false;
        }

        value = candidate;
        return true;
    }
}

public sealed class GatewaySessionValidationMiddleware(
    RequestDelegate next,
    ILogger<GatewaySessionValidationMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        IAuthSessionValidator sessionValidator,
        IOptionsMonitor<GatewayOptions> options)
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

        if (!GatewayRequests.AcceptsEventStream(context.Request))
        {
            await next(context);
            return;
        }

        // A subscription is authorised once and then streams for up to an hour. Signing out of
        // every device therefore used to stop ordinary requests within the cache TTL while open
        // streams kept delivering the victim's messages and notifications until the connection
        // happened to drop. Re-check periodically and cancel the request when the session goes.
        var recheckSeconds = Math.Clamp(options.CurrentValue.SubscriptionSessionRecheckSeconds, 1, 300);
        var originalAborted = context.RequestAborted;
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(originalAborted);
        context.RequestAborted = watchdog.Token;
        var monitor = MonitorSubscriptionSessionAsync(
            sessionValidator,
            userId.Value,
            sessionId.Value,
            TimeSpan.FromSeconds(recheckSeconds),
            watchdog,
            logger);
        try
        {
            await next(context);
        }
        finally
        {
            await watchdog.CancelAsync();
            context.RequestAborted = originalAborted;
            await monitor;
        }
    }

    private static async Task MonitorSubscriptionSessionAsync(
        IAuthSessionValidator sessionValidator,
        long userId,
        long sessionId,
        TimeSpan interval,
        CancellationTokenSource watchdog,
        ILogger logger)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(watchdog.Token))
            {
                var validation = await sessionValidator.ValidateAsync(
                    userId,
                    sessionId,
                    watchdog.Token,
                    forceRefresh: true);
                if (validation.IsValid)
                {
                    continue;
                }

                logger.LogInformation(
                    "Closing realtime stream for revoked session {SessionId} of user {UserId}.",
                    sessionId,
                    userId);
                await watchdog.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // The request finished or was cancelled; nothing to do.
        }
        catch (Exception exception)
        {
            // Session validation is the authority for a long-lived private stream. If it
            // becomes unavailable or returns an unexpected failure, keeping the stream open
            // would turn a validator fault into an authorization fail-open.
            logger.LogWarning(exception, "Realtime session watchdog failed; closing the stream.");
            await watchdog.CancelAsync();
        }
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
