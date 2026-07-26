namespace fakebookGateway.Gateway;

internal static class GatewayRequests
{
    /// <summary>
    /// True when the caller negotiated a GraphQL subscription over server-sent events. Such a
    /// request is answered by a stream that stays open for as long as the edge allows, so it needs
    /// different handling from an ordinary request/response exchange.
    /// </summary>
    public static bool AcceptsEventStream(HttpRequest request)
    {
        foreach (var value in request.Headers.Accept)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var candidate in value.Split(','))
            {
                var mediaType = candidate.AsSpan().Trim();
                var parameterIndex = mediaType.IndexOf(';');
                if (parameterIndex >= 0)
                {
                    mediaType = mediaType[..parameterIndex].TrimEnd();
                }

                if (mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
