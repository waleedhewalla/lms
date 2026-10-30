using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
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

/// <summary>An object that is really in storage. <see cref="Size"/> is null only if the server did not say.</summary>
public sealed record StoredObject(long? Size);

/// <summary>
/// Storage did not answer, answered with something unexpected, or is missing its bucket. Callers treat it as
/// "unknown", never as success: endpoints answer 503, and disposal reports the item as pending.
/// </summary>
public sealed class StorageUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public interface IObjectStorage
{
    /// <summary>Makes sure the bucket exists and is usable, verified independently of the SDK.</summary>
    Task EnsureBucketAsync(CancellationToken ct = default);

    Task<string> PresignedPutAsync(string objectKey, int expirySeconds = 3600, CancellationToken ct = default);

    /// <summary>Permanently deletes an object (records disposal). Call <see cref="ConfirmDeletedAsync"/> afterwards.</summary>
    Task RemoveAsync(string objectKey, CancellationToken ct = default);

    /// <summary>True only when storage positively says the object is gone; throws <see cref="StorageUnavailableException"/> if it cannot tell.</summary>
    Task<bool> ConfirmDeletedAsync(string objectKey, CancellationToken ct = default);

    /// <summary>The object's details, or null when it is not there; throws <see cref="StorageUnavailableException"/> if it cannot tell.</summary>
    Task<StoredObject?> StatAsync(string objectKey, CancellationToken ct = default);
}

/// <summary>
/// S3-compatible object storage (MinIO in dev/on-prem reference). The MinIO SDK was observed to return without error
/// even when the server was unreachable, so nothing here trusts an SDK result as proof: every claim (bucket is there,
/// object is gone, object exists) is checked with an independent signed request, and anything unclear throws
/// <see cref="StorageUnavailableException"/>.
/// </summary>
public sealed partial class ObjectStorage(IMinioClient client, StorageOptions options, HttpClient probe) : IObjectStorage
{
    /// <summary>Never written: asking for it only tells us whether the bucket answers (NoSuchKey) or is missing (NoSuchBucket).</summary>
    private const string SentinelKey = ".edunexus-probe";

    private volatile bool bucketVerified;

    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        if (bucketVerified) return;
        // Whatever the SDK says (including an error from losing a create race to another replica), the probe decides.
        Exception? sdkError = null;
        try
        {
            if (!await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(options.Bucket), ct))
                await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(options.Bucket), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sdkError = ex;
        }

        var p = await ProbeAsync(SentinelKey, ct);
        if (p.Code == "NoSuchKey" || p.Status is HttpStatusCode.OK or HttpStatusCode.PartialContent or HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            bucketVerified = true;
            return;
        }
        throw new StorageUnavailableException(
            $"Storage bucket '{options.Bucket}' is not usable: storage answered {(int)p.Status} ({p.Code ?? "no error code"}).", sdkError);
    }

    // Presigning is local arithmetic: it proves nothing about the server, so the upload endpoint calls EnsureBucketAsync first.
    public Task<string> PresignedPutAsync(string objectKey, int expirySeconds = 3600, CancellationToken ct = default) =>
        client.PresignedPutObjectAsync(new PresignedPutObjectArgs()
            .WithBucket(options.Bucket).WithObject(objectKey).WithExpiry(expirySeconds));

    public async Task RemoveAsync(string objectKey, CancellationToken ct = default)
    {
        try { await client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(options.Bucket).WithObject(objectKey), ct); }
        catch (Exception ex) when (ex is Minio.Exceptions.MinioException or HttpRequestException)
        {
            throw new StorageUnavailableException($"Storage refused to delete '{objectKey}': {ex.Message}", ex);
        }
    }

    public async Task<bool> ConfirmDeletedAsync(string objectKey, CancellationToken ct = default)
    {
        var p = await ProbeAsync(objectKey, ct);
        // A 404 alone is not proof: a missing bucket answers 404 too (NoSuchBucket), and then nothing is confirmed.
        if (p.Status == HttpStatusCode.NotFound && p.Code == "NoSuchKey") return true;
        if (p.Status is HttpStatusCode.OK or HttpStatusCode.PartialContent or HttpStatusCode.RequestedRangeNotSatisfiable) return false;
        throw new StorageUnavailableException(
            $"Storage answered {(int)p.Status} ({p.Code ?? "no error code"}) when confirming deletion of '{objectKey}'.");
    }

    public async Task<StoredObject?> StatAsync(string objectKey, CancellationToken ct = default)
    {
        var p = await ProbeAsync(objectKey, ct);
        if (p.Status == HttpStatusCode.NotFound && p.Code == "NoSuchKey") return null;
        if (p.Status is HttpStatusCode.OK or HttpStatusCode.PartialContent or HttpStatusCode.RequestedRangeNotSatisfiable) return new StoredObject(p.Size);
        throw new StorageUnavailableException(
            $"Storage answered {(int)p.Status} ({p.Code ?? "no error code"}) when looking up '{objectKey}'.");
    }

    public Task<string> PresignedGetAsync(string objectKey, int expirySeconds = 3600, CancellationToken ct = default) =>
        client.PresignedGetObjectAsync(new PresignedGetObjectArgs()
            .WithBucket(options.Bucket).WithObject(objectKey).WithExpiry(expirySeconds));

    private readonly record struct ProbeResult(HttpStatusCode Status, string? Code, long? Size);

    /// <summary>
    /// A signed GET for the first byte. 206 carries the total size in Content-Range; 200 in Content-Length; an empty
    /// object answers 416. Errors carry an S3 code in the body (NoSuchKey, NoSuchBucket, AccessDenied...).
    /// </summary>
    private async Task<ProbeResult> ProbeAsync(string objectKey, CancellationToken ct)
    {
        var url = await PresignedGetAsync(objectKey, 300, ct);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            using var res = await probe.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            switch (res.StatusCode)
            {
                case HttpStatusCode.PartialContent: return new(res.StatusCode, null, res.Content.Headers.ContentRange?.Length);
                case HttpStatusCode.OK: return new(res.StatusCode, null, res.Content.Headers.ContentLength);
                case HttpStatusCode.RequestedRangeNotSatisfiable: return new(res.StatusCode, null, 0);
                default:
                    var match = ErrorCode().Match(await res.Content.ReadAsStringAsync(ct));
                    return new(res.StatusCode, match.Success ? match.Groups[1].Value : null, null);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new StorageUnavailableException($"Object storage is unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new StorageUnavailableException("Object storage did not answer in time.", ex);
        }
    }

    [GeneratedRegex("<Code>([^<]+)</Code>")]
    private static partial Regex ErrorCode();
}
