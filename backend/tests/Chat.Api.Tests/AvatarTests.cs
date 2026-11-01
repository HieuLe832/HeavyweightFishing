using System.Net;
using Chat.Api.Avatars;
using Chat.Api.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class AvatarTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    /// <summary>A fresh provider, and a clock past the catalog cache another test may have filled.</summary>
    private async Task<Person> FreshAsync()
    {
        api.Avatars.Clear();
        api.Time.Advance(TimeSpan.FromMinutes(2));
        return await api.CreatePersonAsync();
    }

    // --- catalog and images ---

    [Fact]
    public async Task The_catalog_lists_the_providers_keys()
    {
        var me = await FreshAsync();
        api.Avatars.Set("01-a", TestFiles.Png());
        api.Avatars.Set("02-b", TestFiles.WebPLossy());

        var response = await api.Client.GetAsync(me, "/avatars");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var keys = (await response.JsonAsync()).GetProperty("avatars").EnumerateArray().Select(a => a.GetProperty("key").GetString());
        Assert.Equal(["01-a", "02-b"], keys);
    }

    [Fact]
    public async Task The_catalog_is_cached_for_a_minute()
    {
        var me = await FreshAsync();
        api.Avatars.Set("01-a", TestFiles.Png());

        (await api.Client.GetAsync(me, "/avatars")).EnsureSuccessStatusCode();
        (await api.Client.GetAsync(me, "/avatars")).EnsureSuccessStatusCode();
        Assert.Equal(1, api.Avatars.CatalogRequests);

        api.Time.Advance(TimeSpan.FromSeconds(61));
        (await api.Client.GetAsync(me, "/avatars")).EnsureSuccessStatusCode();
        Assert.Equal(2, api.Avatars.CatalogRequests);
    }

    [Fact]
    public async Task An_avatar_image_is_proxied_with_locked_down_headers()
    {
        var me = await FreshAsync();
        api.Avatars.Set("01-a", TestFiles.Png());

        var response = await api.Client.GetAsync(me, "/avatars/01-a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(TestFiles.Png(), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("default-src 'none'; sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromSeconds(300), response.Headers.CacheControl.MaxAge);
        var etag = response.Headers.ETag!;

        var again = Authed(HttpMethod.Get, "/avatars/01-a", me.Token);
        again.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await api.Client.SendAsync(again)).StatusCode);
    }

    [Theory]
    [InlineData("zz-none")]
    [InlineData("Bad_Key")]
    [InlineData("..%2Fsecret")]
    public async Task A_key_outside_the_catalog_or_malformed_is_avatar_not_found(string key)
    {
        var me = await FreshAsync();
        api.Avatars.Set("01-a", TestFiles.Png());

        await (await api.Client.GetAsync(me, $"/avatars/{key}")).AssertErrorAsync(HttpStatusCode.NotFound, "avatar_not_found");
    }

    [Fact]
    public async Task A_provider_that_is_down_gives_503_everywhere()
    {
        var me = await FreshAsync();
        api.Avatars.Set("01-a", TestFiles.Png());
        api.Avatars.Down = true;

        await (await api.Client.GetAsync(me, "/avatars")).AssertErrorAsync(HttpStatusCode.ServiceUnavailable, "avatar_provider_unavailable");
        await (await api.Client.GetAsync(me, "/avatars/01-a")).AssertErrorAsync(HttpStatusCode.ServiceUnavailable, "avatar_provider_unavailable");
    }

    [Fact]
    public async Task A_provider_that_answers_garbage_is_unavailable_and_bad_entries_are_skipped()
    {
        var me = await FreshAsync();
        api.Avatars.SetRawCatalog("<html>not json</html>", "text/html");
        await (await api.Client.GetAsync(me, "/avatars")).AssertErrorAsync(HttpStatusCode.ServiceUnavailable, "avatar_provider_unavailable");

        api.Avatars.SetRawCatalog("""
            {"avatars":[{"key":"../x","file":"../x.webp"},{"key":"sneaky","file":"../etc/passwd"},{"key":"ok","file":"ok.webp"}]}
            """);
        var keys = (await (await api.Client.GetAsync(me, "/avatars")).JsonAsync()).GetProperty("avatars").EnumerateArray()
            .Select(a => a.GetProperty("key").GetString());
        Assert.Equal(["ok"], keys);
    }

    [Fact]
    public async Task A_slow_provider_times_out()
    {
        var me = await FreshAsync();
        api.Avatars.Set("01-a", TestFiles.Png());
        api.Avatars.Delay = TimeSpan.FromSeconds(3); // longer than AvatarProvider:TimeoutSeconds (2)

        await (await api.Client.GetAsync(me, "/avatars")).AssertErrorAsync(HttpStatusCode.ServiceUnavailable, "avatar_provider_unavailable");
    }

    [Fact]
    public async Task Bytes_that_are_not_an_image_are_avatar_provider_invalid()
    {
        var me = await FreshAsync();
        api.Avatars.Set("t", TestFiles.Text());

        await (await api.Client.GetAsync(me, "/avatars/t")).AssertErrorAsync(HttpStatusCode.BadGateway, "avatar_provider_invalid");
    }

    [Fact]
    public async Task Without_a_token_avatars_are_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAsync("/avatars")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAsync("/avatars/01-a")).StatusCode);
    }

    [Fact]
    public async Task Without_a_base_url_the_feature_is_off()
    {
        var catalog = new AvatarCatalog(Options.Create(new AvatarProviderOptions()), api.Services.GetRequiredService<IHttpClientFactory>(),
            TimeProvider.System, NullLogger<AvatarCatalog>.Instance);

        Assert.False(catalog.Enabled);
        Assert.Empty((await catalog.KeysAsync(CancellationToken.None)).Value!);
        Assert.Equal(AuthError.AvatarProviderUnavailable, await catalog.CheckKeyAsync("01-a", CancellationToken.None));
    }
}
