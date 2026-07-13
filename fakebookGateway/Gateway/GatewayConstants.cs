using System.Globalization;
using System.Security.Claims;

namespace fakebookGateway.Gateway;

public static class GatewayConstants
{
    public const string CorrelationIdHeader = "X-Correlation-ID";
    public const string GatewaySecretHeader = "X-Gateway-Secret";
    public const string RefreshTokenHeader = "X-Refresh-Token";
    public const string CookieInstructionHeader = "X-Fakebook-Refresh-Cookie-Instruction";
    public const string UserIdHeader = "X-User-Id";
    public const string SessionIdHeader = "X-Session-Id";

    public const string UserIdClaim = "user_id";
    public const string SessionIdClaim = "sid";

    public const string UserIdItem = "Fakebook.UserId";
    public const string SessionIdItem = "Fakebook.SessionId";
    public const string CookieInstructionAppliedItem = "Fakebook.CookieInstructionApplied";
}

public static class GatewayClaims
{
    public static long? GetLongClaim(this ClaimsPrincipal principal, string type)
    {
        var value = principal.FindFirst(type)?.Value;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

}
