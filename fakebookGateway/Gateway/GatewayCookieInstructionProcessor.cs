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
                context.Response.Cookies.Append(name, value, cookieOptions);
            }
        }
        else if (operation.Equals("CLEAR", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Cookies.Delete(name, cookieOptions);
        }
    }

    private static SameSiteMode ParseSameSite(string? value)
    {
        return Enum.TryParse<SameSiteMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : SameSiteMode.Lax;
    }
}
