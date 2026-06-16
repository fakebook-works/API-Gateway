namespace fakebookGateway.Models;

public class User {
    public string Id { get; set; }
    public string Username { get; set; }
}

public class MediaItem {
    public string Id { get; set; }
    public string Url { get; set; }
    public string MediaType { get; set; }
}

public class SearchResult {
    public string Query { get; set; }
    public List<User> MatchedUsers { get; set; }
}

public class FeedItem {
    public string Id { get; set; }
    public string Content { get; set; }
    public double RelevanceScore { get; set; }
}

public class Message {
    public string Id { get; set; }
    public string Text { get; set; }
    public string SenderId { get; set; }
}

public class Notification {
    public string Id { get; set; }
    public string AlertText { get; set; }
    public bool IsRead { get; set; }
}