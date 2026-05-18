using System.Net;
using System.Text;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ArtistImageServiceTests
{
    [Fact]
    public async Task FetchAndCacheAsync_PrefersExactDeezerArtistMatch()
    {
        using var persistence = new TestPersistenceService();
        var wrongImageUrl = "https://images.example/wrong.jpg";
        var futureImageUrl = "https://images.example/future.jpg";
        var handler = new StubHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://api.deezer.com/search/artist", StringComparison.Ordinal))
            {
                return JsonResponse($$"""
                {
                  "data": [
                    { "name": "Future Islands", "picture_big": "{{wrongImageUrl}}" },
                    { "name": "Future", "picture_xl": "{{futureImageUrl}}" }
                  ]
                }
                """);
            }

            return ImageResponse();
        });
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Future" };

        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Equal(futureImageUrl, handler.RequestedImageUrls.Single());
        Assert.False(string.IsNullOrWhiteSpace(artist.ImagePath));
        Assert.True(File.Exists(artist.ImagePath));
    }

    [Fact]
    public async Task FetchAndCacheAsync_QueuesConcurrentRequests()
    {
        using var persistence = new TestPersistenceService();
        var firstImageRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstImage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://api.deezer.com/search/artist", StringComparison.Ordinal))
            {
                var artistName = Uri.UnescapeDataString(request.RequestUri.Query)
                    .Contains("Second", StringComparison.OrdinalIgnoreCase)
                    ? "Second"
                    : "First";
                return JsonResponse($$"""
                { "data": [ { "name": "{{artistName}}", "picture_big": "https://images.example/{{artistName}}.jpg" } ] }
                """);
            }

            if (url.Contains("/First.jpg", StringComparison.Ordinal))
            {
                firstImageRequested.SetResult();
                await releaseFirstImage.Task;
            }

            return ImageResponse();
        });
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var first = new Artist { Id = Guid.NewGuid(), Name = "First" };
        var second = new Artist { Id = Guid.NewGuid(), Name = "Second" };

        var firstFetch = service.FetchAndCacheAsync(new[] { first });
        await firstImageRequested.Task;
        var secondFetch = service.FetchAndCacheAsync(new[] { second });
        releaseFirstImage.SetResult();
        await Task.WhenAll(firstFetch, secondFetch);

        Assert.True(File.Exists(first.ImagePath));
        Assert.True(File.Exists(second.ImagePath));
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage ImageResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 })
            {
                Headers = { ContentType = new("image/jpeg") }
            }
        };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public List<string> RequestedImageUrls { get; } = new();

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this(request => Task.FromResult(handler(request)))
        {
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "images.example")
                RequestedImageUrls.Add(request.RequestUri.ToString());

            return await _handler(request);
        }
    }
}
