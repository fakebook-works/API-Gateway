using fakebookGateway.GraphQL;

namespace fakebookGateway {
    class Program {
        static void Main(string[] args) {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services
                .AddGraphQLServer()
                .AddQueryType()
                .AddTypeExtension<AuthQueries>()
                .AddTypeExtension<MediaQueries>()
                .AddTypeExtension<SearchQueries>()
                .AddTypeExtension<RecommendationQueries>()
                .AddTypeExtension<MessagingQueries>()
                .AddTypeExtension<NotificationQueries>();

            var app = builder.Build();

            app.MapGraphQL("/api");

            app.Run();
        }
    }
}