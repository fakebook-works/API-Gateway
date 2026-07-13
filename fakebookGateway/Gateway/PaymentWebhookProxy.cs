using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;

namespace fakebookGateway.Gateway;

public sealed class PaymentGatewayOptions
{
    public const string SectionName = "PaymentGateway";
    public const int MaximumBodyBytes = 64 * 1024;

    public string WebhookEndpoint { get; set; } = "http://localhost:5016/internal/webhooks/payos";
    public int TimeoutSeconds { get; set; } = 10;
    public int WebhookPermitLimit { get; set; } = 60;
    public int WebhookWindowSeconds { get; set; } = 60;
}

public static class PaymentWebhookProxy
{
    public const string RateLimitPolicy = "payment-webhook";

    public static IEndpointRouteBuilder MapPaymentWebhookProxy(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/webhooks/payos", HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimitPolicy);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        IHttpClientFactory clients,
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<PaymentGatewayOptions> paymentOptions,
        CancellationToken cancellationToken)
    {
        var mediaType = context.Request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        if (context.Request.ContentLength is > PaymentGatewayOptions.MaximumBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = PaymentGatewayOptions.MaximumBodyBytes;
        }

        byte[] body;
        try
        {
            body = await ReadLimitedAsync(
                context.Request.Body,
                PaymentGatewayOptions.MaximumBodyBytes,
                cancellationToken);
        }
        catch (PayloadTooLargeException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (body.Length == 0)
        {
            return Results.BadRequest();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, paymentOptions.Value.WebhookEndpoint)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(
            GatewayConstants.CorrelationIdHeader,
            context.Items[GatewayConstants.CorrelationIdHeader]?.ToString());
        request.Headers.TryAddWithoutValidation(
            GatewayConstants.GatewaySecretHeader,
            gatewayOptions.Value.InternalSharedSecret);

        try
        {
            using var response = await clients.CreateClient("payment-webhook")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return Results.StatusCode(ToPublicStatus(response.StatusCode));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static int ToPublicStatus(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.OK => StatusCodes.Status200OK,
        System.Net.HttpStatusCode.BadRequest => StatusCodes.Status400BadRequest,
        System.Net.HttpStatusCode.RequestEntityTooLarge => StatusCodes.Status413PayloadTooLarge,
        System.Net.HttpStatusCode.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
        System.Net.HttpStatusCode.TooManyRequests => StatusCodes.Status429TooManyRequests,
        System.Net.HttpStatusCode.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status502BadGateway
    };

    private static async Task<byte[]> ReadLimitedAsync(
        Stream stream,
        int limit,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(Math.Min(limit, 4096));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > limit)
            {
                throw new PayloadTooLargeException();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private sealed class PayloadTooLargeException : Exception;
}
