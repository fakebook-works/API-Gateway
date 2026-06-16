using fakebookGateway.Models;
using HotChocolate.Types;

namespace fakebookGateway.GraphQL;

[ExtendObjectType("Query")]
public class AuthQueries {
    public User GetCurrentUser() {
        return new User {
            Id = "usr_auth123",
            Username = "alice_dev"
        };
    }

    public User GetUserById(string id) {
        return new User {
            Id = id,
            Username = $"user_mock_{id}"
        };
    }
}