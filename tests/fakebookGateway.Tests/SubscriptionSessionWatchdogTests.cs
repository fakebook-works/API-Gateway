namespace fakebookGateway.Tests;

using System.Security.Claims;
using fakebookGateway.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// An ordinary request is re-authorised on every call, but a GraphQL subscription is authorised
/// once and then streams for as long as the edge allows — an hour. Signing out of every device
/// therefore stopped the attacker's normal requests within the cache TTL while their open streams
/// kept delivering the victim's private messages and notifications until the connection happened
/// to drop. The watchdog re-checks the session and cancels the request when it is revoked.
/// </summary>
public sealed class SubscriptionSessionWatchdogTests
{
    private const long UserId = 42;
    private const long SessionId = 84;

    [Fact]
    public async Task An_open_subscription_is_cancelled_when_its_session_is_revoked()
    {
        var validator = new SequencedValidator(valid: true, thenValid: false);
        var streamCancelled = new TaskCompletionSource();
        var middleware = new GatewaySessionValidationMiddleware(
            context =>
            {
                context.RequestAborted.Register(() => streamCancelled.TrySetResult());
                // Stand in for a live SSE stream: stay open until cancelled.
                return Task.Delay(Timeout.Infinite, context.RequestAborted)
                    .ContinueWith(_ => { }, TaskScheduler.Default);
            },
            NullLogger<GatewaySessionValidationMiddleware>.Instance);

        await middleware.InvokeAsync(CreateSubscriptionContext(), validator, Options());

        await streamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(validator.ForcedRefreshes >= 1, "The watchdog must re-check with the cache bypassed.");
    }

    [Fact]
    public async Task An_ordinary_request_is_not_watched()
    {
        var validator = new SequencedValidator(valid: true, thenValid: false);
        var completed = false;
        var middleware = new GatewaySessionValidationMiddleware(
            _ =>
            {
                completed = true;
                return Task.CompletedTask;
            },
            NullLogger<GatewaySessionValidationMiddleware>.Instance);

        var context = CreateSubscriptionContext();
        context.Request.Headers.Accept = "application/json";

        await middleware.InvokeAsync(context, validator, Options());

        Assert.True(completed);
        Assert.Equal(0, validator.ForcedRefreshes);
    }

    [Fact]
    public async Task A_subscription_with_a_live_session_keeps_streaming()
    {
        var validator = new SequencedValidator(valid: true, thenValid: true);
        var middleware = new GatewaySessionValidationMiddleware(
            context => Task.Delay(TimeSpan.FromSeconds(3), context.RequestAborted),
            NullLogger<GatewaySessionValidationMiddleware>.Instance);

        // Completes normally rather than being torn down by the watchdog.
        await middleware.InvokeAsync(CreateSubscriptionContext(), validator, Options());

        Assert.True(validator.ForcedRefreshes >= 1);
    }

    [Fact]
    public async Task An_open_subscription_is_cancelled_when_revalidation_fails()
    {
        var validator = new FailingRefreshValidator();
        var streamCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new GatewaySessionValidationMiddleware(
            context =>
            {
                context.RequestAborted.Register(() => streamCancelled.TrySetResult());
                return Task.Delay(Timeout.Infinite, context.RequestAborted)
                    .ContinueWith(_ => { }, TaskScheduler.Default);
            },
            NullLogger<GatewaySessionValidationMiddleware>.Instance);

        await middleware.InvokeAsync(CreateSubscriptionContext(), validator, Options())
            .WaitAsync(TimeSpan.FromSeconds(20));

        await streamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(validator.ForcedRefreshes >= 1);
    }

    private static IOptionsMonitor<GatewayOptions> Options() =>
        new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions
        {
            InternalSharedSecret = "legacy-gateway-secret-at-least-32-bytes",
            SubscriptionSessionRecheckSeconds = 1
        });

    private static DefaultHttpContext CreateSubscriptionContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/graphql";
        context.Request.Headers.Accept = "text/event-stream";
        context.Request.Headers.Authorization = "Bearer access-token";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(GatewayConstants.UserIdClaim, UserId.ToString()),
                new Claim(GatewayConstants.SessionIdClaim, SessionId.ToString())
            ],
            authenticationType: "Test"));
        return context;
    }

    private sealed class SequencedValidator(bool valid, bool thenValid) : IAuthSessionValidator
    {
        private int _calls;

        public int ForcedRefreshes { get; private set; }

        public Task<GatewaySessionValidationResult> ValidateAsync(
            long userId,
            long sessionId,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            if (forceRefresh) ForcedRefreshes++;
            var isValid = Interlocked.Increment(ref _calls) == 1 ? valid : thenValid;
            return Task.FromResult(new GatewaySessionValidationResult(
                isValid,
                userId,
                sessionId,
                null,
                null));
        }
    }

    private sealed class FailingRefreshValidator : IAuthSessionValidator
    {
        public int ForcedRefreshes { get; private set; }

        public Task<GatewaySessionValidationResult> ValidateAsync(
            long userId,
            long sessionId,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            if (forceRefresh)
            {
                ForcedRefreshes++;
                throw new HttpRequestException("Auth validation is unavailable.");
            }

            return Task.FromResult(new GatewaySessionValidationResult(
                true,
                userId,
                sessionId,
                1,
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
