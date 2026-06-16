using fakebookGateway.Models;
using HotChocolate.Types;

namespace fakebookGateway.GraphQL;

[ExtendObjectType("Query")]
public class NotificationQueries {
    public List<Notification> GetNotifications(string userId) {
        return new List<Notification> {
            new Notification {
                Id = "ntf_001",
                AlertText = "Bob sent you a friend request.",
                IsRead = false
            },
            new Notification {
                Id = "ntf_002",
                AlertText = "Charlie commented on your photo.",
                IsRead = true
            }
        };
    }
}