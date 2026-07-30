namespace fakebookGateway.Gateway;

using Microsoft.Extensions.Options;

/// <summary>
/// Replaces the transport URL embedded in the Fusion archive with the reviewed runtime
/// endpoint for the target subgraph. The archive remains a composition artifact; deployment
/// topology is supplied through Subgraphs:&lt;Name&gt;:Url and can be changed without rebuilding it.
/// </summary>
public sealed class FusionSubgraphEndpointHandler(
    IOptionsMonitor<SubgraphEndpointsOptions> options,
    string subgraphName) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var endpoint = new UriBuilder(options.CurrentValue.Resolve(subgraphName));

        // Fusion normally uses POST, but preserve a generated query string if a future transport
        // uses GET. Configured endpoints themselves reject query/fragment components at startup.
        if (!string.IsNullOrEmpty(request.RequestUri?.Query))
        {
            endpoint.Query = request.RequestUri.Query.TrimStart('?');
        }

        request.RequestUri = endpoint.Uri;
        return base.SendAsync(request, cancellationToken);
    }
}
