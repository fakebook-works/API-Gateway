using fakebookGateway.Models;
using HotChocolate.Types;

namespace fakebookGateway.GraphQL;

[ExtendObjectType("Query")]
public class SearchQueries {
    public SearchResult SearchPlatform(string query) {
        return new SearchResult {
            Query = query,
            MatchedUsers = new List<User> {
                new User { Id = "usr_99", Username = "john_doe" },
                new User { Id = "usr_100", Username = "johnny_cash" }
            }
        };
    }
}