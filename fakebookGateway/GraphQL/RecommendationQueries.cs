using fakebookGateway.Models;
using HotChocolate.Types;

namespace fakebookGateway.GraphQL;

[ExtendObjectType("Query")]
public class RecommendationQueries {
    public List<FeedItem> GetRecommendedFeed(string userId) {
        return new List<FeedItem> {
            new FeedItem {
                Id = "post_88",
                Content = "Check out this amazing landscape photo!",
                RelevanceScore = 0.95
            },
            new FeedItem {
                Id = "post_12",
                Content = "10 Tips for learning GraphQL with C# and .NET",
                RelevanceScore = 0.82
            }
        };
    }
}