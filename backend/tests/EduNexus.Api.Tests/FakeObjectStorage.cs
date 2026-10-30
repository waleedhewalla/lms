using System.Collections.Concurrent;
using EduNexus.Api.Storage;

namespace EduNexus.Api.Tests;

/// <summary>
/// In-memory stand-in for object storage, with switches to script each way real storage can go wrong. The suite shares
/// one instance, so a test that flips a switch must call <see cref="Reset"/> when it is done.
/// </summary>
public sealed class FakeObjectStorage : IObjectStorage
{
    private readonly ConcurrentDictionary<string, long> objects = new();

    /// <summary>Every call fails as if the server were unreachable.</summary>
    public volatile bool Unavailable;
    /// <summary>The bucket cannot be created or verified.</summary>
    public volatile bool BucketMissing;
    /// <summary>Deletes are accepted but the object stays (so deletion cannot be confirmed).</summary>
    public volatile bool KeepAfterRemove;

    public void Put(string key, long size) => objects[key] = size;
    public bool Has(string key) => objects.ContainsKey(key);
    public void Reset() { Unavailable = false; BucketMissing = false; KeepAfterRemove = false; }

    private void ThrowIfDown()
    {
        if (Unavailable) throw new StorageUnavailableException("Object storage is unreachable (fake).");
    }

    public Task EnsureBucketAsync(CancellationToken ct = default)
    {
        ThrowIfDown();
        if (BucketMissing) throw new StorageUnavailableException("Storage bucket 'edunexus-docs' is not usable (fake).");
        return Task.CompletedTask;
    }

    public Task<string> PresignedPutAsync(string objectKey, int expirySeconds = 3600, CancellationToken ct = default) =>
        Task.FromResult($"http://fake-storage/edunexus-docs/{objectKey}?signed=1");

    public Task RemoveAsync(string objectKey, CancellationToken ct = default)
    {
        ThrowIfDown();
        if (!KeepAfterRemove) objects.TryRemove(objectKey, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmDeletedAsync(string objectKey, CancellationToken ct = default)
    {
        ThrowIfDown();
        return Task.FromResult(!objects.ContainsKey(objectKey));
    }

    public Task<StoredObject?> StatAsync(string objectKey, CancellationToken ct = default)
    {
        ThrowIfDown();
        return Task.FromResult(objects.TryGetValue(objectKey, out var size) ? new StoredObject(size) : null);
    }
}
