using System.Net;
using System.Text;
using System.Text.Json;
using Chat.Api.Push;
using Chat.Data;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class PushTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static string NewToken() => "tok-" + Guid.NewGuid().ToString("N");

    private async Task RegisterAsync(Person who, string token, string platform = "fcm") =>
        (await api.Client.PutAsync(who, "/auth/sessions/current/push-token", new { platform, token })).EnsureSuccessStatusCode();

    private async Task<Person> SecondDeviceAsync(Person who)
    {
        var response = await api.Client.LoginAsync(who.Username);
        response.EnsureSuccessStatusCode();
        var body = await response.JsonAsync();
        return who with { Token = body.GetProperty("accessToken").GetString()! };
    }

    /// <summary>A direct chat from a connected sender to an offline receiver who has registered a device.</summary>
    private async Task<(Person Sender, Person Receiver, Guid Chat, string Token, Microsoft.AspNetCore.SignalR.Client.HubConnection SenderHub)> OfflineReceiverAsync()
    {
        var sender = await api.CreatePersonAsync("Sally Sender");
        var receiver = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(sender, receiver);
        var token = NewToken();
        await RegisterAsync(receiver, token);
        return (sender, receiver, chat, token, await api.ConnectAsync(sender));
    }

    [Fact]
    public async Task An_offline_member_is_notified_on_every_device_with_the_sender_and_a_preview()
    {
        var (_, receiver, chat, tablet, senderHub) = await OfflineReceiverAsync();
        var phone = await SecondDeviceAsync(receiver);
        var phoneToken = NewToken();
        await RegisterAsync(phone, phoneToken, "apns");

        var sent = await SendMessageTests.SendAsync(senderHub, chat, "  are you coming tonight?  ");

        var onTablet = Assert.Single(api.Push.To(tablet));
        Assert.Equal(PushPlatform.Fcm, onTablet.Platform);
        Assert.Equal("Sally Sender", onTablet.Message.Title);
        Assert.Equal("are you coming tonight?", onTablet.Message.Body);
        Assert.Equal(chat, onTablet.Message.ConversationId);
        Assert.Equal(sent.GetProperty("id").GetGuid(), onTablet.Message.MessageId);
        Assert.Equal(1, onTablet.Message.Seq);
        Assert.Equal(PushPlatform.Apns, Assert.Single(api.Push.To(phoneToken)).Platform);
    }

    [Fact]
    public async Task Group_titles_show_long_text_is_cut_and_the_sender_is_never_notified()
    {
        var owner = await api.CreatePersonAsync("Olga");
        var member = await api.CreatePersonAsync();
        var group = await api.CreateGroupAsync(owner, member);
        var memberToken = NewToken();
        var ownerToken = NewToken();
        await RegisterAsync(member, memberToken);
        await RegisterAsync(owner, ownerToken);
        await using var ownerHub = await api.ConnectAsync(owner);

        await SendMessageTests.SendAsync(ownerHub, group, new string('x', 150));

        var push = Assert.Single(api.Push.To(memberToken));
        Assert.Equal("Olga · g", push.Message.Title);
        Assert.Equal(new string('x', PushDispatcher.PreviewLength) + "…", push.Message.Body);
        Assert.Empty(api.Push.To(ownerToken));
    }

    [Fact]
    public async Task An_online_member_is_not_notified()
    {
        var (_, receiver, chat, token, senderHub) = await OfflineReceiverAsync();
        await using var receiverHub = await api.ConnectAsync(receiver);

        await SendMessageTests.SendAsync(senderHub, chat, "hi");

        Assert.Empty(api.Push.To(token));
    }

    [Fact]
    public async Task A_muted_member_is_not_notified_until_the_mute_lapses()
    {
        var (_, receiver, chat, token, senderHub) = await OfflineReceiverAsync();
        (await api.Client.PutAsync(receiver, $"/conversations/{chat}/mute", new { until = api.Time.GetUtcNow().AddHours(1) })).EnsureSuccessStatusCode();

        await SendMessageTests.SendAsync(senderHub, chat, "shh");
        Assert.Empty(api.Push.To(token));

        api.Time.Advance(TimeSpan.FromHours(2));
        await SendMessageTests.SendAsync(senderHub, chat, "ding");
        Assert.Equal("ding", Assert.Single(api.Push.To(token)).Message.Body);
    }

    [Fact]
    public async Task Nobody_is_notified_who_left_is_banned_signed_out_or_has_no_device()
    {
        var owner = await api.CreatePersonAsync();
        var left = await api.CreatePersonAsync();
        var banned = await api.CreatePersonAsync();
        var signedOut = await api.CreatePersonAsync();
        var noDevice = await api.CreatePersonAsync();
        var group = await api.CreateGroupAsync(owner, left, banned, signedOut, noDevice);
        var tokens = new Dictionary<string, string>();
        foreach (var (name, person) in new[] { ("left", left), ("banned", banned), ("signedOut", signedOut) })
        {
            tokens[name] = NewToken();
            await RegisterAsync(person, tokens[name]);
        }
        (await api.Client.PostAsync(left, $"/conversations/{group}/leave")).EnsureSuccessStatusCode();
        await api.WithDbAsync(db => db.Users.Where(u => u.Id == banned.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Banned)));
        (await api.Client.PostAsync(signedOut, "/auth/logout")).EnsureSuccessStatusCode();
        await using var ownerHub = await api.ConnectAsync(owner);

        await SendMessageTests.SendAsync(ownerHub, group, "anyone?");

        Assert.DoesNotContain(api.Push.Sent, s => tokens.ContainsValue(s.Token));
    }

    [Fact]
    public async Task A_message_deleted_before_dispatch_sends_nothing()
    {
        var (_, _, chat, token, senderHub) = await OfflineReceiverAsync();
        var sent = await SendMessageTests.SendAsync(senderHub, chat, "oops");
        Assert.Single(api.Push.To(token));
        await senderHub.InvokeAsync("DeleteMessage", sent.GetProperty("id").GetGuid());
        var dispatcher = api.Services.GetServices<Chat.Api.Realtime.IBusHandler>().OfType<PushDispatcher>().Single();
        var late = new { messageId = sent.GetProperty("id").GetGuid(), conversationId = chat, seq = 1, senderId = Guid.NewGuid(), offlineUserIds = new[] { Guid.NewGuid() } };

        await dispatcher.HandleAsync(JsonSerializer.SerializeToUtf8Bytes(late), CancellationToken.None);

        Assert.Single(api.Push.To(token));
    }

    [Fact]
    public async Task A_dead_token_is_cleared_and_one_failing_device_does_not_stop_the_others()
    {
        var (_, receiver, chat, dead, senderHub) = await OfflineReceiverAsync();
        var phone = await SecondDeviceAsync(receiver);
        var broken = NewToken();
        await RegisterAsync(phone, broken);
        var tablet = await SecondDeviceAsync(receiver);
        var good = NewToken();
        await RegisterAsync(tablet, good);
        api.Push.Results[dead] = PushResult.InvalidToken;
        api.Push.Failing[broken] = true;

        await SendMessageTests.SendAsync(senderHub, chat, "one");
        await SendMessageTests.SendAsync(senderHub, chat, "two");

        Assert.Single(api.Push.To(dead)); // cleared after the first
        Assert.Equal(2, api.Push.To(good).Count);
        var stored = await api.WithDbAsync(db => db.Sessions.Where(s => s.UserId == receiver.Id).Select(s => s.PushToken).ToListAsync());
        Assert.DoesNotContain(dead, stored);
        Assert.Contains(good, stored);
    }

    // --- registering a device ---

    [Fact]
    public async Task Registering_replaces_the_token_and_deleting_clears_it()
    {
        var who = await api.CreatePersonAsync();
        var first = NewToken();
        var second = NewToken();

        await RegisterAsync(who, first);
        await RegisterAsync(who, second, "web");
        var row = await api.WithDbAsync(db => db.Sessions.Where(s => s.UserId == who.Id).Select(s => new { s.PushToken, s.PushPlatform }).SingleAsync());
        Assert.Equal(second, row.PushToken);
        Assert.Equal(PushPlatform.Web, row.PushPlatform);

        (await api.Client.DeleteAsync(who, "/auth/sessions/current/push-token")).EnsureSuccessStatusCode();
        row = await api.WithDbAsync(db => db.Sessions.Where(s => s.UserId == who.Id).Select(s => new { s.PushToken, s.PushPlatform }).SingleAsync());
        Assert.Null(row.PushToken);
        Assert.Null(row.PushPlatform);
    }

    [Theory]
    [InlineData("carrier-pigeon", "abc")]
    [InlineData(null, "abc")]
    [InlineData("fcm", "")]
    [InlineData("fcm", "has space")]
    public async Task Invalid_registrations_are_refused(string? platform, string token)
    {
        var who = await api.CreatePersonAsync();

        var response = await api.Client.PutAsync(who, "/auth/sessions/current/push-token", new { platform, token });

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task A_token_over_the_limit_is_refused_and_registering_needs_a_session()
    {
        var who = await api.CreatePersonAsync();

        var tooLong = await api.Client.PutAsync(who, "/auth/sessions/current/push-token", new { platform = "fcm", token = new string('a', PushEndpoints.MaxTokenLength + 1) });
        await tooLong.AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().PutAsync("/auth/sessions/current/push-token",
            new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task A_token_moves_to_the_new_session_when_the_same_device_signs_in_again()
    {
        var who = await api.CreatePersonAsync();
        var token = NewToken();
        await RegisterAsync(who, token);
        var again = await SecondDeviceAsync(who);

        await RegisterAsync(again, token);

        var holders = await api.WithDbAsync(db => db.Sessions.CountAsync(s => s.UserId == who.Id && s.PushToken == token));
        Assert.Equal(1, holders);
    }
}
