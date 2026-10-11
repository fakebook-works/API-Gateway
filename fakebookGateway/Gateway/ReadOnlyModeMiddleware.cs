using System.Text.Json;
using HotChocolate.Language;
using Microsoft.Extensions.Options;

namespace fakebookGateway.Gateway;

/// <summary>Applies the maintainer's mode at the browser edge before Fusion executes a write.</summary>
public sealed class ReadOnlyModeMiddleware(RequestDelegate next)
{
    public const string StatusHeader = "X-Fakebook-Read-Only";
    private static readonly HashSet<string> AccountAccessMutations = new(StringComparer.Ordinal)
    {
        "login", "refreshToken", "logout", "logoutAll", "logoutSession",
        "verifyEmail", "resendEmailVerification", "requestPasswordReset", "resetPassword",
        "changePassword", "changeEmail"
    };

    public async Task InvokeAsync(HttpContext context, IOptionsMonitor<GatewayOptions> options)
    {
        if (context.Request.Path.StartsWithSegments("/graphql", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers[StatusHeader] = options.CurrentValue.ReadOnlyMode ? "true" : "false";
        }

        if (!options.CurrentValue.ReadOnlyMode ||
            !context.Request.Path.StartsWithSegments("/graphql", StringComparison.OrdinalIgnoreCase) ||
            (!HttpMethods.IsPost(context.Request.Method) && !HttpMethods.IsGet(context.Request.Method)))
        {
            await next(context);
            return;
        }

        string? query;
        if (HttpMethods.IsGet(context.Request.Method))
        {
            query = context.Request.Query["query"].ToString();
            if (string.IsNullOrEmpty(query))
            {
                await next(context);
                return;
            }
        }
        else
        {
            if (!context.Request.HasJsonContentType())
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }

            var maxBytes = options.CurrentValue.MaxRequestBodyBytes;
            if (context.Request.ContentLength is { } contentLength && contentLength > maxBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            context.Request.EnableBuffering(bufferLimit: maxBytes);
            try
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                query = body.RootElement.ValueKind == JsonValueKind.Object &&
                        body.RootElement.TryGetProperty("query", out var property) &&
                        property.ValueKind == JsonValueKind.String
                    ? property.GetString()
                    : null;
            }
            catch (JsonException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            catch (IOException)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            finally
            {
                context.Request.Body.Position = 0;
            }
        }

        if (query is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        DocumentNode document;
        try
        {
            document = Utf8GraphQLParser.Parse(query);
        }
        catch (SyntaxException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var blocked = document.Definitions
            .OfType<OperationDefinitionNode>()
            .Where(operation => operation.Operation == OperationType.Mutation)
            .Any(operation => operation.SelectionSet.Selections.Count == 0 ||
                operation.SelectionSet.Selections.Any(selection =>
                    selection is not FieldNode field || !AccountAccessMutations.Contains(field.Name.Value)));

        if (blocked)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new
            {
                errors = new[] { new { message = "Fakebook is in read-only mode. This action is unavailable.",
                    extensions = new { code = "READ_ONLY" } } }
            });
            return;
        }

        await next(context);
    }
}
