using System.Net;
using System.Security.Cryptography;
using Chat.Api.Files;
using Chat.Data;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class MediaUploadTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private InMemoryObjectStore Store => api.Services.GetRequiredService<InMemoryObjectStore>();

    // --- upload ---

    [Fact]
    public async Task An_image_is_stored_with_its_detected_type_size_and_hash()
    {
        var who = await api.CreatePersonAsync();
        var bytes = TestFiles.Png(640, 480);

        // The client claims it is a PDF; the bytes decide.
        var response = await api.Client.UploadAsync(who, bytes, "holiday.pdf", "application/pdf");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal("image/png", body.GetProperty("mimeType").GetString());
        Assert.Equal(bytes.Length, body.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(640, body.GetProperty("width").GetInt32());
        Assert.Equal(480, body.GetProperty("height").GetInt32());
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/media/{id}", response.Headers.Location!.OriginalString);
        var row = await api.WithDbAsync(db => db.Media.AsNoTracking().SingleAsync(m => m.Id == id));
        Assert.Equal(who.Id, row.UploaderId);
        Assert.Equal(MediaStatus.Ready, row.Status);
        Assert.Equal(SHA256.HashData(bytes), row.Sha256);
        Assert.Equal(bytes, await Store.GetAsync(row.StorageKey, CancellationToken.None));
    }

    [Fact]
    public async Task A_pdf_and_a_text_file_have_no_size()
    {
        var who = await api.CreatePersonAsync();

        var pdf = await (await api.Client.UploadAsync(who, TestFiles.Pdf())).JsonAsync();
        var text = await (await api.Client.UploadAsync(who, TestFiles.Text())).JsonAsync();

        Assert.Equal("application/pdf", pdf.GetProperty("mimeType").GetString());
        Assert.Equal("text/plain", text.GetProperty("mimeType").GetString());
        Assert.False(pdf.TryGetProperty("width", out var width) && width.ValueKind != System.Text.Json.JsonValueKind.Null);
    }

    [Theory]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0x80 })]
    [InlineData(new byte[0])]
    public async Task A_file_of_an_unknown_type_is_refused_and_nothing_is_stored(byte[] bytes)
    {
        var who = await api.CreatePersonAsync();
        var before = Store.Count;

        var response = await api.Client.UploadAsync(who, bytes, "photo.png", "image/png");

        await response.AssertErrorAsync(HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        Assert.Equal(before, Store.Count);
        Assert.False(await api.WithDbAsync(db => db.Media.AnyAsync(m => m.UploaderId == who.Id)));
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused()
    {
        var who = await api.CreatePersonAsync();
        var tooBig = new byte[10 * 1024 * 1024 + 1];
        Array.Fill(tooBig, (byte)'a');

        var response = await api.Client.UploadAsync(who, tooBig);

        await response.AssertErrorAsync(HttpStatusCode.RequestEntityTooLarge, "file_too_large");
    }

    [Fact]
    public async Task A_file_at_the_limit_is_accepted()
    {
        var who = await api.CreatePersonAsync();
        var atLimit = new byte[10 * 1024 * 1024];
        Array.Fill(atLimit, (byte)'a');

        Assert.Equal(HttpStatusCode.Created, (await api.Client.UploadAsync(who, atLimit)).StatusCode);
    }

    [Fact]
    public async Task Uploads_are_rate_limited_per_user()
    {
        var who = await api.CreatePersonAsync();
        var other = await api.CreatePersonAsync();
        api.Time.Advance(TimeSpan.FromMinutes(5)); // an empty window

        for (var i = 0; i < 20; i++) Assert.Equal(HttpStatusCode.Created, (await api.Client.UploadAsync(who, TestFiles.Text($"note {i}"))).StatusCode);

        await (await api.Client.UploadAsync(who, TestFiles.Text("one too many"))).AssertErrorAsync((HttpStatusCode)429, "rate_limited");
        Assert.Equal(HttpStatusCode.Created, (await api.Client.UploadAsync(other, TestFiles.Text("someone else"))).StatusCode);
    }

    [Fact]
    public async Task Upload_needs_a_session_and_a_part_named_file()
    {
        var who = await api.CreatePersonAsync();

        var anonymous = new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Png()), "file", "a.png" } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().PostAsync("/media", anonymous)).StatusCode);
        await (await api.Client.UploadAsync(who, TestFiles.Png(), partName: "upload")).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
        await (await api.Client.PostAsync(who, "/media", new { file = "nope" })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    // --- download ---

    [Fact]
    public async Task An_image_is_served_inline_with_locked_down_headers()
    {
        var who = await api.CreatePersonAsync();
        var bytes = TestFiles.Gif(5, 5);
        var id = await api.UploadedAsync(who, bytes);

        var response = await api.DownloadAsync(who, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/gif", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType ?? string.Join(',', response.Headers.GetValues("Content-Disposition")));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("default-src 'none'; sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("private", response.Headers.CacheControl!.ToString());
        Assert.Equal($"\"{Convert.ToHexStringLower(SHA256.HashData(bytes))}\"", response.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task A_pdf_and_text_are_offered_as_downloads_and_text_says_it_is_utf8()
    {
        var who = await api.CreatePersonAsync();
        var pdf = await api.DownloadAsync(who, await api.UploadedAsync(who, TestFiles.Pdf()));
        var text = await api.DownloadAsync(who, await api.UploadedAsync(who, TestFiles.Svg()));

        Assert.Equal("attachment", pdf.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("attachment", text.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("text/plain", text.Content.Headers.ContentType!.MediaType); // markup is text, never an image or page
        Assert.Equal("utf-8", text.Content.Headers.ContentType.CharSet);
    }

    [Fact]
    public async Task A_matching_etag_gets_304()
    {
        var who = await api.CreatePersonAsync();
        var id = await api.UploadedAsync(who);
        var etag = (await api.DownloadAsync(who, id)).Headers.ETag!.Tag;
        var request = Authed(HttpMethod.Get, $"/media/{id}", who.Token);
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        Assert.Equal(HttpStatusCode.NotModified, (await api.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_stranger_a_missing_file_and_a_file_that_is_not_ready_all_look_the_same()
    {
        var owner = await api.CreatePersonAsync();
        var stranger = await api.CreatePersonAsync();
        var id = await api.UploadedAsync(owner);
        var pending = await api.UploadedAsync(owner);
        await api.WithDbAsync(db => db.Media.Where(m => m.Id == pending).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MediaStatus.Pending)));

        await (await api.DownloadAsync(stranger, id)).AssertErrorAsync(HttpStatusCode.NotFound, "media_not_found");
        await (await api.DownloadAsync(owner, Guid.NewGuid())).AssertErrorAsync(HttpStatusCode.NotFound, "media_not_found");
        await (await api.DownloadAsync(owner, pending)).AssertErrorAsync(HttpStatusCode.NotFound, "media_not_found");
        Assert.Equal(HttpStatusCode.OK, (await api.DownloadAsync(owner, id)).StatusCode);
    }

    [Fact]
    public async Task Download_needs_a_session()
    {
        var who = await api.CreatePersonAsync();
        var id = await api.UploadedAsync(who);

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().GetAsync($"/media/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_file_attached_to_a_message_is_readable_by_its_members_until_they_leave_or_it_is_deleted()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var carol = await api.CreatePersonAsync();
        var group = await api.CreateGroupAsync(alice, bob, carol);
        var id = await api.UploadedAsync(alice);
        await using var aliceHub = await api.ConnectAsync(alice);
        var sent = await aliceHub.SendMediaAsync(group, [id]);

        Assert.Equal(HttpStatusCode.OK, (await api.DownloadAsync(bob, id)).StatusCode);
        (await api.Client.PostAsync(carol, $"/conversations/{group}/leave")).EnsureSuccessStatusCode();
        await (await api.DownloadAsync(carol, id)).AssertErrorAsync(HttpStatusCode.NotFound, "media_not_found");

        await aliceHub.InvokeAsync("DeleteMessage", sent.GetProperty("id").GetGuid());
        await (await api.DownloadAsync(bob, id)).AssertErrorAsync(HttpStatusCode.NotFound, "media_not_found");
        Assert.Equal(HttpStatusCode.OK, (await api.DownloadAsync(alice, id)).StatusCode); // still the uploader's
    }

    // --- avatars ---

    [Fact]
    public async Task A_users_avatar_is_set_replaced_cleared_and_readable_by_people_who_can_see_them()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var first = await api.UploadedAsync(alice);
        var second = await api.UploadedAsync(alice, TestFiles.Jpeg());

        (await api.Client.PutAsync(alice, "/users/me/avatar", new { mediaId = first })).EnsureSuccessStatusCode();
        (await api.Client.PutAsync(alice, "/users/me/avatar", new { mediaId = second })).EnsureSuccessStatusCode();
        var profile = await (await api.Client.GetAsync(bob, $"/users/{alice.Id}")).JsonAsync();
        Assert.Equal(second, profile.GetProperty("avatarMediaId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await api.DownloadAsync(bob, second)).StatusCode);
        await (await api.DownloadAsync(bob, first)).AssertErrorAsync(HttpStatusCode.NotFound, "media_not_found"); // no longer an avatar

        (await api.Client.DeleteAsync(alice, "/users/me/avatar")).EnsureSuccessStatusCode();
        profile = await (await api.Client.GetAsync(bob, $"/users/{alice.Id}")).JsonAsync();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, profile.GetProperty("avatarMediaId").ValueKind);
    }

    [Fact]
    public async Task Someone_who_blocked_you_looks_missing_so_their_avatar_is_hidden()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var avatar = await api.UploadedAsync(alice);
        (await api.Client.PutAsync(alice, "/users/me/avatar", new { mediaId = avatar })).EnsureSuccessStatusCode();
        (await api.Client.PutAsync(alice, $"/blocks/{bob.Id}")).EnsureSuccessStatusCode();

        await (await api.DownloadAsync(bob, avatar)).AssertErrorAsync(HttpStatusCode.NotFound, "media_not_found");
    }

    [Fact]
    public async Task An_avatar_must_be_an_image_the_caller_uploaded()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var pdf = await api.UploadedAsync(alice, TestFiles.Pdf());
        var bobs = await api.UploadedAsync(bob);

        await (await api.Client.PutAsync(alice, "/users/me/avatar", new { mediaId = pdf })).AssertErrorAsync(HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        await (await api.Client.PutAsync(alice, "/users/me/avatar", new { mediaId = bobs })).AssertErrorAsync(HttpStatusCode.NotFound, "media_not_found");
        await (await api.Client.PutAsync(alice, "/users/me/avatar", new { mediaId = Guid.NewGuid() })).AssertErrorAsync(HttpStatusCode.NotFound, "media_not_found");
        await (await api.Client.PutAsync(alice, "/users/me/avatar", new { })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task A_group_avatar_is_managed_by_owners_and_admins_and_seen_by_members_only()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var outsider = await api.CreatePersonAsync();
        var group = await api.CreateGroupAsync(owner, member);
        var avatar = await api.UploadedAsync(owner);
        var memberImage = await api.UploadedAsync(member);

        await (await api.Client.PutAsync(member, $"/conversations/{group}/avatar", new { mediaId = memberImage })).AssertErrorAsync(HttpStatusCode.Forbidden, "insufficient_role");
        var set = await api.Client.PutAsync(owner, $"/conversations/{group}/avatar", new { mediaId = avatar });
        set.EnsureSuccessStatusCode();
        Assert.Equal(avatar, (await set.JsonAsync()).GetProperty("conversation").GetProperty("avatarMediaId").GetGuid());

        Assert.Equal(HttpStatusCode.OK, (await api.DownloadAsync(member, avatar)).StatusCode);
        await (await api.DownloadAsync(outsider, avatar)).AssertErrorAsync(HttpStatusCode.NotFound, "media_not_found");
        var list = await (await api.Client.GetAsync(member, "/conversations")).JsonAsync();
        Assert.Equal(avatar, list.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == group).GetProperty("avatarMediaId").GetGuid());

        (await api.Client.DeleteAsync(owner, $"/conversations/{group}/avatar")).EnsureSuccessStatusCode();
        await (await api.DownloadAsync(member, avatar)).AssertErrorAsync(HttpStatusCode.NotFound, "media_not_found");
    }

    [Fact]
    public async Task Group_avatar_edge_cases()
    {
        var owner = await api.CreatePersonAsync();
        var other = await api.CreatePersonAsync();
        var group = await api.CreateGroupAsync(owner, other);
        var direct = await api.OpenDirectAsync(owner, other);
        var stranger = await api.CreatePersonAsync();
        var image = await api.UploadedAsync(owner);
        var strangerImage = await api.UploadedAsync(stranger);
        var pdf = await api.UploadedAsync(owner, TestFiles.Pdf());

        await (await api.Client.PutAsync(owner, $"/conversations/{direct}/avatar", new { mediaId = image })).AssertErrorAsync(HttpStatusCode.BadRequest, "not_a_group");
        await (await api.Client.PutAsync(stranger, $"/conversations/{group}/avatar", new { mediaId = strangerImage })).AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
        await (await api.Client.PutAsync(owner, $"/conversations/{group}/avatar", new { mediaId = pdf })).AssertErrorAsync(HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        await (await api.Client.PutAsync(owner, $"/conversations/{group}/avatar", new { mediaId = strangerImage })).AssertErrorAsync(HttpStatusCode.NotFound, "media_not_found");
        await (await api.Client.PutAsync(owner, $"/conversations/{group}/avatar", new { })).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }
}
