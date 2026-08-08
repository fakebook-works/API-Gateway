namespace fakebookGateway.Tests;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using fakebookGateway.Gateway;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class AuthSessionValidatorTests
{
    [Fact]
    public async Task Forced_refresh_does_not_reuse_a_completed_result()
    {
        var calls = 0;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var handler = new SequencedHandler(() =>
        {
            var isValid = Interlocked.Increment(ref calls) == 1;
            return JsonSerializer.Serialize(new
            {
                data = new
                {
                    validateGatewaySession = new
                    {
                        isValid,
                        userId = 42L,
                        sessionId = 84L,
                        status = 1,
                        expiresAt = "2030-01-01T00:00:00Z"
                    }
                }
            });
        });

        var validator = new AuthSessionValidator(
            new StaticHttpClientFactory(new HttpClient(handler)
            {
                BaseAddress = new Uri("http://auth.test/graphql")
            }),
            cache,
            new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions
            {
                SubgraphSecrets = new SubgraphSecretsOptions
                {
                    Authentication = "authentication-secret-that-is-at-least-32-bytes"
                }
            }),
            NullLogger<AuthSessionValidator>.Instance);

        var first = await validator.ValidateAsync(42, 84, CancellationToken.None, forceRefresh: true);
        var second = await validator.ValidateAsync(42, 84, CancellationToken.None, forceRefresh: true);

        Assert.True(first.IsValid);
        Assert.False(second.IsValid);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_valid_response_for_a_different_identity_is_rejected()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var handler = new SequencedHandler(() =>
            JsonSerializer.Serialize(new
            {
                data = new
                {
                    validateGatewaySession = new
                    {
                        isValid = true,
                        userId = 99L,
                        sessionId = 100L,
                        status = 1,
                        expiresAt = "2030-01-01T00:00:00Z"
                    }
                }
            }));
        var validator = CreateValidator(cache, handler);

        var result = await validator.ValidateAsync(42, 84, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task A_valid_response_with_missing_identity_fields_is_rejected()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var handler = new SequencedHandler(() =>
            JsonSerializer.Serialize(new
            {
                data = new
                {
                    validateGatewaySession = new
                    {
                        isValid = true,
                        status = 1,
                        expiresAt = "2030-01-01T00:00:00Z"
                    }
                }
            }));
        var validator = CreateValidator(cache, handler);

        var result = await validator.ValidateAsync(42, 84, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("{\"errors\":true}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":{\"validateGatewaySession\":{\"isValid\":\"true\"}}}")]
    [InlineData("[]")]
    public async Task Malformed_payload_shapes_fail_closed(string payload)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var handler = new SequencedHandler(() => payload);
        var validator = CreateValidator(cache, handler);

        var result = await validator.ValidateAsync(42, 84, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    private sealed class SequencedHandler(Func<string> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseFactory(), Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private static AuthSessionValidator CreateValidator(
        IMemoryCache cache,
        HttpMessageHandler handler) => new(
        new StaticHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://auth.test/graphql")
        }),
        cache,
        new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions
        {
            SubgraphSecrets = new SubgraphSecretsOptions
            {
                Authentication = "authentication-secret-that-is-at-least-32-bytes"
            }
        }),
        NullLogger<AuthSessionValidator>.Instance);

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
