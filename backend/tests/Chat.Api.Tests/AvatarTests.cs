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

    // --- picking ---

    private static string? Preset(System.Text.Json.JsonElement e) => e.GetProperty("avatarPresetKey").GetString();

    private async Task<System.Text.Json.JsonElement> MeAsync(Person who) => await (await api.Client.GetAsync(who, "/auth/me")).JsonAsync();

    [Fact]
    public async Task Picking_a_preset_shows_in_every_profile_shape()
    {
        var alice = await FreshAsync();
        var bob = await api.CreatePersonAsync();
        api.Avatars.Set("01-a", TestFiles.Png());
        var opened = await api.Client.PostAsync(bob, "/conversations/direct", new { userId = alice.Id });
        var conversation = (await opened.JsonAsync()).GetProperty("conversation").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await api.Client.PutAsync(alice, "/users/me/avatar", new { presetKey = "01-a" })).StatusCode);

        var me = await MeAsync(alice);
        Assert.Equal("01-a", Preset(me));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, me.GetProperty("avatarMediaId").ValueKind);
        Assert.Equal("01-a", Preset(await (await api.Client.GetAsync(bob, $"/users/{alice.Id}")).JsonAsync()));
        var list = await (await api.Client.GetAsync(bob, "/conversations")).JsonAsync();
        Assert.Equal("01-a", Preset(list.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == conversation).GetProperty("peer")));
        var detail = await (await api.Client.GetAsync(bob, $"/conversations/{conversation}")).JsonAsync();
        Assert.Equal("01-a", Preset(detail.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == alice.Id)));
    }

    [Fact]
    public async Task A_preset_and_an_upload_replace_each_other()
    {
        var me = await FreshAsync();
        api.Avatars.Set("01-a", TestFiles.Png());
        var upload = await api.UploadedAsync(me);

        (await api.Client.PutAsync(me, "/users/me/avatar", new { mediaId = upload })).EnsureSuccessStatusCode();
        (await api.Client.PutAsync(me, "/users/me/avatar", new { presetKey = "01-a" })).EnsureSuccessStatusCode();
        var profile = await MeAsync(me);
        Assert.Equal("01-a", Preset(profile));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, profile.GetProperty("avatarMediaId").ValueKind);

        (await api.Client.PutAsync(me, "/users/me/avatar", new { mediaId = upload })).EnsureSuccessStatusCode();
        profile = await MeAsync(me);
        Assert.Equal(upload, profile.GetProperty("avatarMediaId").GetGuid());
        Assert.Null(Preset(profile));

        (await api.Client.DeleteAsync(me, "/users/me/avatar")).EnsureSuccessStatusCode();
        profile = await MeAsync(me);
        Assert.Null(Preset(profile));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, profile.GetProperty("avatarMediaId").ValueKind);
    }

    [Fact]
    public async Task Picking_needs_the_provider_and_a_known_key()
    {
        var me = await FreshAsync();
        api.Avatars.Set("01-a", TestFiles.Png());
        var upload = await api.UploadedAsync(me);

        await (await api.Client.PutAsync(me, "/users/me/avatar", new { presetKey = "zz-none" })).AssertErrorAsync(HttpStatusCode.NotFound, "avatar_not_found");
        await (await api.Client.PutAsync(me, "/users/me/avatar", new { })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await api.Client.PutAsync(me, "/users/me/avatar", new { mediaId = upload, presetKey = "01-a" })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");

        api.Avatars.Down = true;
        api.Time.Advance(TimeSpan.FromSeconds(61));
        await (await api.Client.PutAsync(me, "/users/me/avatar", new { presetKey = "01-a" })).AssertErrorAsync(HttpStatusCode.ServiceUnavailable, "avatar_provider_unavailable");
    }

    [Fact]
    public async Task A_stored_preset_survives_the_provider_going_away()
    {
        var me = await FreshAsync();
        api.Avatars.Set("01-a", TestFiles.Png());
        (await api.Client.PutAsync(me, "/users/me/avatar", new { presetKey = "01-a" })).EnsureSuccessStatusCode();

        api.Avatars.Down = true;
        api.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("01-a", Preset(await MeAsync(me)));
        await (await api.Client.GetAsync(me, "/avatars/01-a")).AssertErrorAsync(HttpStatusCode.ServiceUnavailable, "avatar_provider_unavailable");

        // Back, but the key left the pack: the choice stays, the image is gone, and another one can be picked.
        api.Avatars.Clear();
        api.Avatars.Set("02-b", TestFiles.Png());
        api.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("01-a", Preset(await MeAsync(me)));
        await (await api.Client.GetAsync(me, "/avatars/01-a")).AssertErrorAsync(HttpStatusCode.NotFound, "avatar_not_found");
        (await api.Client.PutAsync(me, "/users/me/avatar", new { presetKey = "02-b" })).EnsureSuccessStatusCode();
        Assert.Equal("02-b", Preset(await MeAsync(me)));
    }
}
