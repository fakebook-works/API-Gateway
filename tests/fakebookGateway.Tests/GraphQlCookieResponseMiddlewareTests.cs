namespace fakebookGateway.Tests;

using System.Text;
using fakebookGateway.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class GraphQlCookieResponseMiddlewareTests
{
    [Fact]
    public async Task EventStream_IsVisibleBeforeDownstreamCompletesAndDisablesProxyBuffering()
    {
        var firstChunkWritten = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new GraphQlCookieResponseMiddleware(
            async context =>
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync("event: next\ndata: {}\n\n");
                await context.Response.Body.FlushAsync();
                firstChunkWritten.SetResult();
                await allowCompletion.Task;
            },
            NullLogger<GraphQlCookieResponseMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/graphql";
        context.Request.Headers.Accept = "application/json, text/event-stream; charset=utf-8";
        context.Response.Body = new MemoryStream();

        var invocation = middleware.InvokeAsync(context);
        await firstChunkWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(invocation.IsCompleted);
        Assert.True(context.Response.Body.Length > 0);
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"]);
        Assert.Equal("no-cache, no-store", context.Response.Headers.CacheControl);

        allowCompletion.SetResult();
        await invocation;
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        Assert.Contains("event: next", await reader.ReadToEndAsync());
    }
}
