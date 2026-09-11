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

    public Task<string> PresignedGetAsync(string objectKey, int expirySeconds = 3600, CancellationToken ct = default) =>
        client.PresignedGetObjectAsync(new PresignedGetObjectArgs()
            .WithBucket(options.Bucket).WithObject(objectKey).WithExpiry(expirySeconds));
}
