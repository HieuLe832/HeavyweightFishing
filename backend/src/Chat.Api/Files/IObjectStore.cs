using System.Collections.Concurrent;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;

namespace Chat.Api.Files;

/// <summary>Where the bytes of D7 live. The metadata is in PostgreSQL.</summary>
public interface IObjectStore
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct);

    /// <summary>The bytes, or null when there is no such object.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary>Keeps files in memory: for tests and a bare dev setup with no object store. Lost on restart.</summary>
public class InMemoryObjectStore : IObjectStore
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();

    public int Count => _objects.Count;

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        _objects[key] = content;
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken ct) => Task.FromResult(_objects.GetValueOrDefault(key));

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        _objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

/// <param name="Endpoint">The S3 API URL (RustFS in Compose, http://localhost:9000).</param>
public record ObjectStoreSettings(string Endpoint, string AccessKey, string SecretKey, string Bucket);

/// <summary>An S3-compatible store through AWSSDK.S3, path-style so it works with RustFS and similar. Creates the bucket on first use.</summary>
public sealed class S3ObjectStore(ObjectStoreSettings settings) : IObjectStore, IDisposable
{
    private readonly AmazonS3Client _client = new(new BasicAWSCredentials(settings.AccessKey, settings.SecretKey), new AmazonS3Config
    {
        ServiceURL = settings.Endpoint,
        ForcePathStyle = true,
        // Compatible stores do not all implement the SDK's default checksum trailers.
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    });
    private readonly SemaphoreSlim _bucketGate = new(1, 1);
    private bool _bucketReady;

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);
        using var stream = new MemoryStream(content);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.Bucket, Key = key, InputStream = stream, ContentType = contentType,
            DisablePayloadSigning = false, UseChunkEncoding = false,
        }, ct);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);
        try
        {
            using var response = await _client.GetObjectAsync(settings.Bucket, key, ct);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);
        await _client.DeleteObjectAsync(settings.Bucket, key, ct);
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketReady) return;
        await _bucketGate.WaitAsync(ct);
        try
        {
            if (!_bucketReady && !await AmazonS3Util.DoesS3BucketExistV2Async(_client, settings.Bucket))
                await _client.PutBucketAsync(settings.Bucket, ct);
            _bucketReady = true;
        }
        finally
        {
            _bucketGate.Release();
        }
    }

    public void Dispose() => _client.Dispose();
}
