using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace fakebookGateway.Gateway;

public sealed class FusionSubgraphHeaderHandler(
    IHttpContextAccessor httpContextAccessor,
    IOptionsMonitor<GatewayOptions> options,
    string subgraphName = "") : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var context = httpContextAccessor.HttpContext;
        if (context is null)
        {
            return base.SendAsync(request, cancellationToken);
        }

        FusionTrustedHeaders.Apply(
            request,
            context,
            options.CurrentValue,
            subgraphName,
            includeAuthorization: true,
            includeRefreshToken: false);

        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class AuthFusionSubgraphHeaderHandler(
    IHttpContextAccessor httpContextAccessor,
    IOptionsMonitor<GatewayOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var context = httpContextAccessor.HttpContext;
        if (context is null)
        {
            return base.SendAsync(request, cancellationToken);
        }

        FusionTrustedHeaders.Apply(
            request,
            context,
            options.CurrentValue,
            GatewaySubgraphs.Authentication,
            includeAuthorization: true,
            includeRefreshToken: true);

        return SendAndApplyCookieInstructionAsync(request, context, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAndApplyCookieInstructionAsync(
        HttpRequestMessage request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (!response.Headers.TryGetValues(GatewayConstants.CookieInstructionHeader, out var values))
        {
            return response;
        }

        var encodedInstruction = values.FirstOrDefault();
        response.Headers.Remove(GatewayConstants.CookieInstructionHeader);
        if (string.IsNullOrWhiteSpace(encodedInstruction))
        {
            return response;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encodedInstruction));
            if (JsonNode.Parse(json) is JsonObject instruction)
            {
                GatewayCookieInstructionProcessor.Apply(context, instruction);
                context.Items[GatewayConstants.CookieInstructionAppliedItem] = true;
            }
        }
        catch (FormatException)
        {
            // Ignore malformed internal cookie instruction headers.
        }
        catch (JsonException)
        {
            // Ignore malformed internal cookie instruction headers.
        }

        return response;
    }
}

public sealed class PaymentFusionSubgraphHeaderHandler(
    IHttpContextAccessor httpContextAccessor,
    IOptionsMonitor<GatewayOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var context = httpContextAccessor.HttpContext;
        if (context is null)
        {
            return base.SendAsync(request, cancellationToken);
        }

        FusionTrustedHeaders.Apply(
            request,
            context,
            options.CurrentValue,
            GatewaySubgraphs.Payment,
            includeAuthorization: false,
            includeRefreshToken: false);

        return base.SendAsync(request, cancellationToken);
    }
}

internal static class FusionTrustedHeaders
{
    private const string LegacyUsernameHeader = "X-Username";

    public static void Apply(
        HttpRequestMessage request,
        HttpContext context,
        GatewayOptions options,
        string subgraphName,
        bool includeAuthorization,
        bool includeRefreshToken)
    {
        request.Headers.Remove(GatewayConstants.UserIdHeader);
        request.Headers.Remove(GatewayConstants.SessionIdHeader);
        request.Headers.Remove(LegacyUsernameHeader);
        request.Headers.Remove(GatewayConstants.CorrelationIdHeader);
        request.Headers.Remove(GatewayConstants.GatewaySecretHeader);
        request.Headers.Remove(GatewayConstants.RefreshTokenHeader);
        request.Headers.Remove(GatewayConstants.LegacyInternalUserIdHeader);
        request.Headers.Remove(GatewayConstants.InternalAuthenticationServiceSecretHeader);
        request.Headers.Remove(GatewayConstants.InternalSocialGraphServiceSecretHeader);
        request.Headers.Remove(GatewayConstants.InternalRecommendationServiceSecretHeader);
        request.Headers.Remove(GatewayConstants.InternalSearchServiceSecretHeader);
        request.Headers.Remove(GatewayConstants.InternalNotificationServiceSecretHeader);
        request.Headers.Remove(GatewayConstants.InternalMessengerServiceSecretHeader);
        request.Headers.Remove(GatewayConstants.PaymentSecretHeader);
        foreach (var header in request.Headers
                     .Select(item => item.Key)
                     .Where(name => name.StartsWith("X-Internal-", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            request.Headers.Remove(header);
        }

        request.Headers.Remove("Authorization");
        if (includeAuthorization)
        {
            Copy(request, "Authorization", context.Request.Headers.Authorization.ToString());
        }
        Copy(
            request,
            GatewayConstants.UserIdHeader,
            context.Items[GatewayConstants.UserIdItem]?.ToString() ??
            context.User.GetLongClaim(GatewayConstants.UserIdClaim)?.ToString());
        Copy(
            request,
            GatewayConstants.SessionIdHeader,
            context.Items[GatewayConstants.SessionIdItem]?.ToString() ??
            context.User.GetLongClaim(GatewayConstants.SessionIdClaim)?.ToString());
        Copy(
            request,
            GatewayConstants.CorrelationIdHeader,
            context.Items[GatewayConstants.CorrelationIdHeader]?.ToString() ??
            context.Request.Headers[GatewayConstants.CorrelationIdHeader].ToString());
        Copy(
            request,
            GatewayConstants.GatewaySecretHeader,
            options.ResolveSubgraphSecret(subgraphName));

        if (includeRefreshToken &&
            !string.IsNullOrWhiteSpace(options.RefreshTokenCookieName) &&
            context.Request.Cookies.TryGetValue(options.RefreshTokenCookieName, out var refreshToken))
        {
            Copy(request, GatewayConstants.RefreshTokenHeader, refreshToken);
        }
    }

    private static void Copy(HttpRequestMessage request, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
