namespace fakebookGateway.Gateway;

public sealed class SubgraphEndpointsOptions
{
    public const string SectionName = "Subgraphs";

    public SubgraphEndpointOptions Authentication { get; set; } =
        new() { Url = "http://127.0.0.1:1001/graphql" };
    public SubgraphEndpointOptions SocialGraph { get; set; } =
        new() { Url = "http://127.0.0.1:1002/graphql" };
    public SubgraphEndpointOptions Recommendation { get; set; } =
        new() { Url = "http://127.0.0.1:1003/graphql" };
    public SubgraphEndpointOptions Search { get; set; } =
        new() { Url = "http://127.0.0.1:1004/graphql" };
    public SubgraphEndpointOptions Notification { get; set; } =
        new() { Url = "http://127.0.0.1:1005/graphql" };
    public SubgraphEndpointOptions Messaging { get; set; } =
        new() { Url = "http://127.0.0.1:1006/graphql" };
    public SubgraphEndpointOptions Payment { get; set; } =
        new() { Url = "http://127.0.0.1:1007/graphql" };

    public Uri Resolve(string subgraphName)
    {
        var configured = subgraphName switch
        {
            GatewaySubgraphs.Authentication => Authentication.Url,
            GatewaySubgraphs.SocialGraph => SocialGraph.Url,
            GatewaySubgraphs.Recommendation => Recommendation.Url,
            GatewaySubgraphs.Search => Search.Url,
            GatewaySubgraphs.Notification => Notification.Url,
            GatewaySubgraphs.Messaging => Messaging.Url,
            GatewaySubgraphs.Payment => Payment.Url,
            _ => throw new InvalidOperationException($"Unknown Fusion subgraph '{subgraphName}'.")
        };

        return new Uri(configured, UriKind.Absolute);
    }

    public bool HasValidEndpoints() =>
        GatewaySubgraphs.All.All(name => IsValidEndpoint(ResolveConfiguredUrl(name)));

    private string ResolveConfiguredUrl(string subgraphName) => subgraphName switch
    {
        GatewaySubgraphs.Authentication => Authentication.Url,
        GatewaySubgraphs.SocialGraph => SocialGraph.Url,
        GatewaySubgraphs.Recommendation => Recommendation.Url,
        GatewaySubgraphs.Search => Search.Url,
        GatewaySubgraphs.Notification => Notification.Url,
        GatewaySubgraphs.Messaging => Messaging.Url,
        GatewaySubgraphs.Payment => Payment.Url,
        _ => string.Empty
    };

    private static bool IsValidEndpoint(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        !string.IsNullOrWhiteSpace(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);
}

public sealed class SubgraphEndpointOptions
{
    public string Url { get; set; } = string.Empty;
}
