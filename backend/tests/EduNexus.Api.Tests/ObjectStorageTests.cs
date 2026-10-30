using System.Net;
using System.Net.Http.Headers;
using EduNexus.Api.Storage;
using Minio;

namespace EduNexus.Api.Tests;

/// <summary>
/// The real ObjectStorage against scripted S3-style answers. The MinIO SDK itself points at a closed port, which is the
/// point: its calls fail or are meaningless here, and only the independent probe may decide.
/// </summary>
public class ObjectStorageTests
{
    private sealed class Scripted(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage S3Error(HttpStatusCode status, string code) =>
        new(status) { Content = new StringContent($"<?xml version=\"1.0\"?><Error><Code>{code}</Code><Message>x</Message></Error>") };

    private static (ObjectStorage Storage, Scripted Handler) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var options = new StorageOptions { Endpoint = "127.0.0.1:9", Bucket = "edunexus-docs" };
        var client = new MinioClient().WithEndpoint(options.Endpoint).WithCredentials("k", "s").WithSSL(false).Build();
        var handler = new Scripted(respond);
        return (new ObjectStorage(client, options, new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) }), handler);
    }

    [Fact]
    public async Task ConfirmDeleted_Only_Believes_NoSuchKey()
    {
        var (gone, _) = Build(_ => S3Error(HttpStatusCode.NotFound, "NoSuchKey"));
        Assert.True(await gone.ConfirmDeletedAsync("t/a.pdf"));

        foreach (var still in new[] { HttpStatusCode.OK, HttpStatusCode.PartialContent, HttpStatusCode.RequestedRangeNotSatisfiable })
        {
            var (present, _) = Build(_ => new HttpResponseMessage(still));
            Assert.False(await present.ConfirmDeletedAsync("t/a.pdf"));
        }

        // A missing bucket also answers 404, and a denied request is not an answer about the object.
        var (noBucket, _) = Build(_ => S3Error(HttpStatusCode.NotFound, "NoSuchBucket"));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => noBucket.ConfirmDeletedAsync("t/a.pdf"));
        var (denied, _) = Build(_ => S3Error(HttpStatusCode.Forbidden, "AccessDenied"));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => denied.ConfirmDeletedAsync("t/a.pdf"));
        var (down, _) = Build(_ => throw new HttpRequestException("connection refused"));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => down.ConfirmDeletedAsync("t/a.pdf"));
    }

    [Fact]
    public async Task Stat_Reads_Size_From_Whichever_Header_The_Server_Sends()
    {
        var (partial, _) = Build(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[1]) };
            r.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 1234);
            return r;
        });
        Assert.Equal(1234, (await partial.StatAsync("t/a.pdf"))!.Size);

        var (whole, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[55]) });
        Assert.Equal(55, (await whole.StatAsync("t/a.pdf"))!.Size);

        var (empty, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        Assert.Equal(0, (await empty.StatAsync("t/empty.txt"))!.Size);

        var (missing, _) = Build(_ => S3Error(HttpStatusCode.NotFound, "NoSuchKey"));
        Assert.Null(await missing.StatAsync("t/a.pdf"));

        var (noBucket, _) = Build(_ => S3Error(HttpStatusCode.NotFound, "NoSuchBucket"));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => noBucket.StatAsync("t/a.pdf"));
    }

    [Fact]
    public async Task EnsureBucket_Trusts_The_Probe_Not_The_Sdk_And_Remembers_Only_Success()
    {
        var bucketExists = false;
        var (storage, handler) = Build(_ => bucketExists ? S3Error(HttpStatusCode.NotFound, "NoSuchKey") : S3Error(HttpStatusCode.NotFound, "NoSuchBucket"));

        // The SDK cannot reach anything here, yet a probe that says "NoSuchBucket" must not pass...
        await Assert.ThrowsAsync<StorageUnavailableException>(() => storage.EnsureBucketAsync());
        var afterFailure = handler.Requests;
        await Assert.ThrowsAsync<StorageUnavailableException>(() => storage.EnsureBucketAsync());   // ...and failure is not cached
        Assert.True(handler.Requests > afterFailure);

        // ...while a probe that finds the bucket passes whatever the SDK did (a lost create race looks like an SDK error).
        bucketExists = true;
        await storage.EnsureBucketAsync();
        var afterSuccess = handler.Requests;
        await storage.EnsureBucketAsync();
        Assert.Equal(afterSuccess, handler.Requests);   // success is remembered: no probe per request

        var (down, _) = Build(_ => throw new HttpRequestException("connection refused"));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => down.EnsureBucketAsync());
    }
}
