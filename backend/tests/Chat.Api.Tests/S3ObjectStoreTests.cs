using Chat.Api.Files;

namespace Chat.Api.Tests;

public class S3ObjectStoreTests
{
    private static async Task<S3ObjectStore> NewStoreAsync() => new(new ObjectStoreSettings(
        await SharedObjectStore.EndpointAsync(), SharedObjectStore.AccessKey, SharedObjectStore.SecretKey, "t-" + Guid.NewGuid().ToString("N")[..12]));

    [Fact]
    public async Task Objects_round_trip_and_a_missing_object_is_null()
    {
        using var store = await NewStoreAsync(); // also creates the bucket on first use
        var bytes = TestFiles.Png(30, 20);

        await store.PutAsync("media/2026/01/a", bytes, "image/png", CancellationToken.None);

        Assert.Equal(bytes, await store.GetAsync("media/2026/01/a", CancellationToken.None));
        Assert.Null(await store.GetAsync("media/2026/01/missing", CancellationToken.None));
        await store.DeleteAsync("media/2026/01/a", CancellationToken.None);
        Assert.Null(await store.GetAsync("media/2026/01/a", CancellationToken.None));
    }

    [Fact]
    public async Task Larger_files_survive_the_trip_byte_for_byte()
    {
        using var store = await NewStoreAsync();
        var bytes = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(bytes);

        await store.PutAsync("media/big", bytes, "application/octet-stream", CancellationToken.None);

        Assert.Equal(bytes, await store.GetAsync("media/big", CancellationToken.None));
    }
}
