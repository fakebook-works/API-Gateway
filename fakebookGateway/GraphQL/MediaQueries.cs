using fakebookGateway.Models;
using HotChocolate.Types;

namespace fakebookGateway.GraphQL;

[ExtendObjectType("Query")]
public class MediaQueries {
    public MediaItem GetMediaItem(string id) {
        return new MediaItem {
            Id = id,
            Url = "https://images.unsplash.com/photo-1618005182384-a83a8bd57fbe",
            MediaType = "Image"
        };
    }

    public List<MediaItem> GetUserPhotos(string userId) {
        return new List<MediaItem> {
            new MediaItem { Id = "img_01", Url = "https://example.com/feed1.jpg", MediaType = "Image" },
            new MediaItem { Id = "vid_02", Url = "https://example.com/video1.mp4", MediaType = "Video" }
        };
    }
}