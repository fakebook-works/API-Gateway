namespace fakebookGateway.Tests;

using fakebookGateway.Gateway;
using Microsoft.AspNetCore.Http;
using Xunit;

public sealed class GatewayEdgeSecurityTests
{
    [Fact]
    public async Task Invalid_correlation_id_is_replaced_before_forwarding()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[GatewayConstants.CorrelationIdHeader] = "\u0001" + new string('x', 200);
        var middleware = new GatewayEdgeMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);
        await context.Response.StartAsync();

        var correlationId = context.Items[GatewayConstants.CorrelationIdHeader]?.ToString();
        Assert.NotNull(correlationId);
        Assert.Matches("^[0-9a-f]{32}$", correlationId!);
    }

    [Fact]
    public async Task Duplicate_correlation_headers_are_not_combined()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Append(GatewayConstants.CorrelationIdHeader, "first");
        context.Request.Headers.Append(GatewayConstants.CorrelationIdHeader, "second");
        var middleware = new GatewayEdgeMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.NotEqual("first,second", context.Items[GatewayConstants.CorrelationIdHeader]?.ToString());
    }
}
