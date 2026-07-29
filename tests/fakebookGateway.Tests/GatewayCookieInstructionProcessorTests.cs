namespace fakebookGateway.Tests;

using System.Text.Json.Nodes;
using fakebookGateway.Gateway;
using Microsoft.AspNetCore.Http;
using Xunit;

public sealed class GatewayCookieInstructionProcessorTests
{
    [Theory]
    [InlineData("SET")]
    [InlineData("CLEAR")]
    public void Apply_DeletesTheLegacyRootCookieWhenUsingTheScopedCookie(string operation)
    {
        var context = new DefaultHttpContext();
        var instruction = new JsonObject
        {
            ["operation"] = operation,
            ["name"] = "fb_refresh",
            ["value"] = operation == "SET" ? "rotated-token" : null,
            ["path"] = "/graphql",
            ["httpOnly"] = true,
            ["secure"] = true,
            ["sameSite"] = "Lax",
            ["maxAgeSeconds"] = 3600
        };

        GatewayCookieInstructionProcessor.Apply(context, instruction);

        var headers = context.Response.Headers.SetCookie.ToArray();
        Assert.Contains(headers, value =>
            value is not null &&
            value.StartsWith("fb_refresh=", StringComparison.Ordinal) &&
            value.Contains("path=/", StringComparison.OrdinalIgnoreCase) &&
            !value.Contains("path=/graphql", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("expires=", StringComparison.OrdinalIgnoreCase));

        if (operation == "SET")
        {
            Assert.Contains(headers, value =>
                value is not null &&
                value.StartsWith("fb_refresh=rotated-token", StringComparison.Ordinal) &&
                value.Contains("path=/graphql", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.Contains(headers, value =>
                value is not null &&
                value.StartsWith("fb_refresh=", StringComparison.Ordinal) &&
                value.Contains("path=/graphql", StringComparison.OrdinalIgnoreCase) &&
                value.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        }
    }
}
