using Minio;
using Minio.DataModel.Args;

namespace EduNexus.Api.Storage;

public sealed class StorageOptions
{
    public const string Section = "Storage";
    public string Endpoint { get; set; } = "127.0.0.1:9002";
    public string AccessKey { get; set; } = "minio";
    public string SecretKey { get; set; } = "minio12345";
    public bool Secure { get; set; } = false;
    public string Bucket { get; set; } = "edunexus-docs";
}

/// <summary>S3-compatible object storage (MinIO in dev/on-prem reference).</summary>
public sealed class ObjectStorage(IMinioClient client, StorageOptions options)
{
    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        if (!await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(options.Bucket), ct))
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(options.Bucket), ct);
    }

    public Task<string> PresignedPutAsync(string objectKey, int expirySeconds = 3600, CancellationToken ct = default) =>
        client.PresignedPutObjectAsync(new PresignedPutObjectArgs()
            .WithBucket(options.Bucket).WithObject(objectKey).WithExpiry(expirySeconds));

    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Permanently deletes an object (records disposal). Call <see cref="ConfirmDeletedAsync"/> afterwards.</summary>
    public Task RemoveAsync(string objectKey, CancellationToken ct = default) =>
        client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(options.Bucket).WithObject(objectKey), ct);

    /// <summary>
    /// Positive proof that an object is gone. The SDK's remove/stat calls were observed to return without error
    /// even when the storage server was unreachable, so deletion is confirmed independently: a signed GET for the
    /// first byte must answer 404. 200/206 means it still exists; anything else, or a connection error, is unknown
    /// and throws, so a disposal is never recorded as done on a guess.
    /// </summary>
    public async Task<bool> ConfirmDeletedAsync(string objectKey, CancellationToken ct = default)
    {
        var url = await PresignedGetAsync(objectKey, 300, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var res = await Probe.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        return res.StatusCode switch
        {
            System.Net.HttpStatusCode.NotFound => true,
            System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.PartialContent => false,
            _ => throw new InvalidOperationException($"Object storage answered {(int)res.StatusCode} when confirming deletion of '{objectKey}'."),
        };
    }

    public Task<string> PresignedGetAsync(string objectKey, int expirySeconds = 3600, CancellationToken ct = default) =>
        client.PresignedGetObjectAsync(new PresignedGetObjectArgs()
            .WithBucket(options.Bucket).WithObject(objectKey).WithExpiry(expirySeconds));
}
