using fakebookGateway.Models;
using HotChocolate.Types;

namespace fakebookGateway.GraphQL;

[ExtendObjectType("Query")]
public class MessagingQueries {
    public List<Message> GetChatHistory(string conversationId) {
        return new List<Message> {
            new Message { Id = "msg_x1", SenderId = "usr_auth123", Text = "Hey, are we still meeting today?" },
            new Message { Id = "msg_x2", SenderId = "usr_99", Text = "Yes! See you at 5 PM." }
        };
    }
}