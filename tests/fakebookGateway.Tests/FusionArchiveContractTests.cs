namespace fakebookGateway.Tests;

using System.IO.Compression;
using System.Text.Json;
using Xunit;

public sealed class FusionArchiveContractTests
{
    [Fact]
    public void Archive_ComposesEverySubgraphWithProductionTransportAndNamedClient()
    {
        using var archive = ZipFile.OpenRead(FindFusionArchive());
        using var metadata = ReadJson(archive, "archive-metadata.json");
        Assert.Equal(
            new[]
            {
                "Authentication",
                "Messaging",
                "Notification",
                "Payment",
                "Recommendation",
                "Search",
                "SocialGraph"
            },
            metadata.RootElement.GetProperty("sourceSchemas")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .OrderBy(name => name, StringComparer.Ordinal));

        using var settings = ReadJson(archive, "gateway/2.0.0/gateway-settings.json");
        var sources = settings.RootElement.GetProperty("sourceSchemas");
        var expected = new Dictionary<string, (string Url, string Client)>
        {
            ["Authentication"] = ("http://authentication:1001/graphql", "auth-fusion"),
            ["SocialGraph"] = ("http://social-graph:1002/graphql", "socialgraph-fusion"),
            ["Recommendation"] = ("http://recommendation:1003/graphql", "recommendation-fusion"),
            ["Search"] = ("http://search:1004/graphql", "search-fusion"),
            ["Notification"] = ("http://notification:1005/graphql", "notification-fusion"),
            ["Messaging"] = ("http://messaging:1006/graphql", "messaging-fusion"),
            ["Payment"] = ("http://payment:1007/graphql", "payment-fusion")
        };

        foreach (var (sourceName, transportContract) in expected)
        {
            var http = sources.GetProperty(sourceName).GetProperty("transports").GetProperty("http");
            Assert.Equal(transportContract.Url, http.GetProperty("url").GetString());
            Assert.Equal(transportContract.Client, http.GetProperty("clientName").GetString());
        }

        AssertSseCapability(sources.GetProperty("Messaging"));
        AssertSseCapability(sources.GetProperty("Notification"));
    }

    [Fact]
    public void LocalArchive_UsesCanonicalLoopbackPortsAndNamedClients()
    {
        using var archive = ZipFile.OpenRead(FindFusionArchive("gateway.local.far"));
        using var settings = ReadJson(archive, "gateway/2.0.0/gateway-settings.json");
        var sources = settings.RootElement.GetProperty("sourceSchemas");
        var expected = new Dictionary<string, (string Url, string Client)>
        {
            ["Authentication"] = ("http://127.0.0.1:1001/graphql", "auth-fusion"),
            ["SocialGraph"] = ("http://127.0.0.1:1002/graphql", "socialgraph-fusion"),
            ["Recommendation"] = ("http://127.0.0.1:1003/graphql", "recommendation-fusion"),
            ["Search"] = ("http://127.0.0.1:1004/graphql", "search-fusion"),
            ["Notification"] = ("http://127.0.0.1:1005/graphql", "notification-fusion"),
            ["Messaging"] = ("http://127.0.0.1:1006/graphql", "messaging-fusion"),
            ["Payment"] = ("http://127.0.0.1:1007/graphql", "payment-fusion")
        };

        foreach (var (sourceName, transportContract) in expected)
        {
            var http = sources.GetProperty(sourceName).GetProperty("transports").GetProperty("http");
            Assert.Equal(transportContract.Url, http.GetProperty("url").GetString());
            Assert.Equal(transportContract.Client, http.GetProperty("clientName").GetString());
        }

        AssertSseCapability(sources.GetProperty("Messaging"));
        AssertSseCapability(sources.GetProperty("Notification"));
    }

    [Theory]
    [InlineData("gateway.far")]
    [InlineData("gateway.local.far")]
    public void Archive_ContainsSocialGraphUserEntityLookupPlan(string archiveName)
    {
        using var archive = ZipFile.OpenRead(FindFusionArchive(archiveName));
        var sourceSchema = ReadText(archive, "source-schemas/SocialGraph/schema.graphqls");
        var gatewaySchema = ReadText(archive, "gateway/2.0.0/gateway.graphqls");

        Assert.Contains("type User @key(fields: \"id\")", sourceSchema, StringComparison.Ordinal);
        Assert.Contains(
            "field: \"userById(id: Long!): User\"",
            gatewaySchema,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gateway.far")]
    [InlineData("gateway.local.far")]
    public void Archive_ContainsMessagingAttachmentMetadataContract(string archiveName)
    {
        using var archive = ZipFile.OpenRead(FindFusionArchive(archiveName));
        var sourceSchema = ReadText(archive, "source-schemas/Messaging/schema.graphqls");

        foreach (var field in new[]
                 {
                     "assetId: String",
                     "mediaType: String",
                     "contentType: String",
                     "originalName: String",
                     "sizeBytes: Long",
                     "width: Int",
                     "height: Int",
                     "durationMs: Long",
                     "thumbnailUrl: String"
                 })
        {
            Assert.Contains(field, sourceSchema, StringComparison.Ordinal);
        }

        Assert.Contains(
            "attachments: [SendMessageAttachmentInput!]",
            sourceSchema,
            StringComparison.Ordinal);
    }

    private static void AssertSseCapability(JsonElement source)
    {
        var formats = source
            .GetProperty("transports")
            .GetProperty("http")
            .GetProperty("capabilities")
            .GetProperty("subscriptions")
            .GetProperty("formats")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Contains("text/event-stream", formats);
    }

    private static JsonDocument ReadJson(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        Assert.NotNull(entry);
        using var stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    private static string ReadText(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static string FindFusionArchive(string archiveName = "gateway.far") => Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "fakebookGateway",
            archiveName));
}
