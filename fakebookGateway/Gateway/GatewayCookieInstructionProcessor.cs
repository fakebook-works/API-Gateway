using System.Text.Json.Nodes;

namespace fakebookGateway.Gateway;

public static class GatewayCookieInstructionProcessor
{
    public static void Apply(HttpContext context, JsonObject instruction)
    {
        var operation = instruction.TryGetString("operation");
        var name = instruction.TryGetString("name");
        if (string.IsNullOrWhiteSpace(operation) || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var cookieOptions = new CookieOptions
        {
            Path = instruction.TryGetString("path") ?? "/",
            HttpOnly = instruction.TryGetBoolean("httpOnly") ?? true,
            Secure = instruction.TryGetBoolean("secure") ?? true,
            SameSite = ParseSameSite(instruction.TryGetString("sameSite"))
        };

        if (instruction.TryGetInt32("maxAgeSeconds") is { } maxAgeSeconds)
        {
            cookieOptions.MaxAge = TimeSpan.FromSeconds(Math.Max(0, maxAgeSeconds));
        }

        if (instruction.TryGetDateTimeOffset("expiresAt") is { } expiresAt)
        {
            cookieOptions.Expires = expiresAt;
        }

        if (operation.Equals("SET", StringComparison.OrdinalIgnoreCase))
        {
            var value = instruction.TryGetString("value");
            if (!string.IsNullOrEmpty(value))
            {
                DeleteLegacyRootCookie(context, name, cookieOptions);
                context.Response.Cookies.Append(name, value, cookieOptions);
            }
        }
        else if (operation.Equals("CLEAR", StringComparison.OrdinalIgnoreCase))
        {
            DeleteLegacyRootCookie(context, name, cookieOptions);
            context.Response.Cookies.Delete(name, cookieOptions);
        }
    }

    private static void DeleteLegacyRootCookie(
        HttpContext context,
        string name,
        CookieOptions currentOptions)
    {
        if (string.Equals(currentOptions.Path, "/", StringComparison.Ordinal))
        {
            return;
        }

        // A refresh cookie used to be issued at Path=/ under this same name. Deleting that
        // exact cookie prevents it from shadowing the newer /graphql cookie on token refresh.
        context.Response.Cookies.Delete(name, new CookieOptions
        {
            Path = "/",
            HttpOnly = currentOptions.HttpOnly,
            Secure = currentOptions.Secure,
            SameSite = currentOptions.SameSite
        });
    }

    private static SameSiteMode ParseSameSite(string? value)
    {
        return Enum.TryParse<SameSiteMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : SameSiteMode.Lax;
    }
}
