using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace fakebookGateway.Gateway;

public sealed class FusionSubgraphHeaderHandler(
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

        ResetTrustedHeaders(request);
        CopyHeader(request, "Authorization", context.Request.Headers.Authorization.ToString());

        var userId = context.Items[GatewayConstants.UserIdItem]?.ToString() ??
                     context.User.GetLongClaim(GatewayConstants.UserIdClaim)?.ToString();
        CopyHeader(request, GatewayConstants.UserIdHeader, userId);

        var sessionId = context.Items[GatewayConstants.SessionIdItem]?.ToString() ??
                        context.User.GetLongClaim(GatewayConstants.SessionIdClaim)?.ToString();
        CopyHeader(request, GatewayConstants.SessionIdHeader, sessionId);

        var username = context.Items[GatewayConstants.UsernameItem]?.ToString() ??
                       context.User.GetClaimValue(GatewayConstants.UsernameClaim);
        CopyHeader(request, GatewayConstants.UsernameHeader, username);

        CopyHeader(
            request,
            GatewayConstants.CorrelationIdHeader,
            context.Items[GatewayConstants.CorrelationIdHeader]?.ToString() ??
            context.Request.Headers[GatewayConstants.CorrelationIdHeader].ToString());

        var refreshCookieName = options.CurrentValue.RefreshTokenCookieName;
        if (!string.IsNullOrWhiteSpace(refreshCookieName) &&
            context.Request.Cookies.TryGetValue(refreshCookieName, out var refreshToken))
        {
            CopyHeader(request, GatewayConstants.RefreshTokenHeader, refreshToken);
        }

        CopyHeader(request, GatewayConstants.GatewaySecretHeader, options.CurrentValue.InternalSharedSecret);

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

    private static void ResetTrustedHeaders(HttpRequestMessage request)
    {
        request.Headers.Remove(GatewayConstants.UserIdHeader);
        request.Headers.Remove(GatewayConstants.SessionIdHeader);
        request.Headers.Remove(GatewayConstants.UsernameHeader);
        request.Headers.Remove(GatewayConstants.CorrelationIdHeader);
        request.Headers.Remove(GatewayConstants.GatewaySecretHeader);
        request.Headers.Remove(GatewayConstants.RefreshTokenHeader);
    }

    private static void CopyHeader(HttpRequestMessage request, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
