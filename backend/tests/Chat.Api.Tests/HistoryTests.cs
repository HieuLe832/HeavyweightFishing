using System.Net;
using System.Text.Json;
using Chat.Data;
using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class HistoryTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private HttpClient C => api.Client;

    /// <summary>A direct chat with <paramref name="count"/> messages (seq 1..count) from Alice, written straight to the database.</summary>
    private async Task<(Person Alice, Person Bob, Guid Chat)> SeedAsync(int count)
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var chat = await api.OpenDirectAsync(alice, bob);
        await api.WithDbAsync(async db =>
        {
            db.Messages.AddRange(Enumerable.Range(1, count).Select(i => new Message
            {
                Id = Guid.NewGuid(), ConversationId = chat, Seq = i, SenderId = alice.Id, ClientMsgId = Guid.NewGuid(),
                Type = MessageType.Text, Body = $"m{i}", CreatedAt = api.Time.GetUtcNow(),
            }));
            await db.Conversations.Where(c => c.Id == chat).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSeq, (long)count));
            await db.SaveChangesAsync();
        });
        return (alice, bob, chat);
    }

    private static long[] Seqs(JsonElement page) => page.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("seq").GetInt64()).ToArray();

    private async Task<JsonElement> PageAsync(Person who, Guid chat, string query = "")
    {
        var response = await C.GetAsync(who, $"/conversations/{chat}/messages{query}");
        response.EnsureSuccessStatusCode();
        return await response.JsonAsync();
    }

    [Fact]
    public async Task The_latest_page_is_the_last_fifty_in_ascending_order()
    {
        var (alice, _, chat) = await SeedAsync(120);

        var page = await PageAsync(alice, chat);

        Assert.Equal(Enumerable.Range(71, 50).Select(i => (long)i), Seqs(page));
        Assert.True(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Before_returns_the_page_just_older_than_seq()
    {
        var (alice, _, chat) = await SeedAsync(120);

        var older = await PageAsync(alice, chat, "?before=71");
        var oldest = await PageAsync(alice, chat, "?before=21");

        Assert.Equal(Enumerable.Range(21, 50).Select(i => (long)i), Seqs(older));
        Assert.True(older.GetProperty("hasMore").GetBoolean());
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), Seqs(oldest));
        Assert.False(oldest.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task After_returns_newer_messages_oldest_first()
    {
        var (alice, _, chat) = await SeedAsync(120);

        var tail = await PageAsync(alice, chat, "?after=100");
        var some = await PageAsync(alice, chat, "?after=50&limit=10");

        Assert.Equal(Enumerable.Range(101, 20).Select(i => (long)i), Seqs(tail));
        Assert.False(tail.GetProperty("hasMore").GetBoolean());
        Assert.Equal(Enumerable.Range(51, 10).Select(i => (long)i), Seqs(some));
        Assert.True(some.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("?after=120")]
    [InlineData("?after=9999")]
    public async Task After_the_last_seq_is_an_empty_page(string query)
    {
        var (alice, _, chat) = await SeedAsync(120);

        var page = await PageAsync(alice, chat, query);

        Assert.Empty(Seqs(page));
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task An_empty_conversation_has_an_empty_page()
    {
        var (alice, _, chat) = await SeedAsync(0);

        Assert.Empty(Seqs(await PageAsync(alice, chat)));
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=51")]
    [InlineData("?before=5&after=3")]
    [InlineData("?before=-1")]
    [InlineData("?after=-1")]
    public async Task Bad_limits_and_cursors_are_rejected(string query)
    {
        var (alice, _, chat) = await SeedAsync(3);

        await (await C.GetAsync(alice, $"/conversations/{chat}/messages{query}")).AssertErrorAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Deleted_messages_come_back_as_tombstones()
    {
        var (alice, _, chat) = await SeedAsync(10);
        await api.WithDbAsync(db => db.Messages.Where(m => m.ConversationId == chat && m.Seq == 5)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Body, (string?)null).SetProperty(m => m.DeletedAt, api.Time.GetUtcNow())));

        var page = await PageAsync(alice, chat);

        var tombstone = page.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("seq").GetInt64() == 5);
        Assert.Equal(JsonValueKind.Null, tombstone.GetProperty("body").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, tombstone.GetProperty("deletedAt").ValueKind);
    }

    [Fact]
    public async Task Reactions_are_counted_and_marked_mine_for_the_viewer()
    {
        var (alice, bob, chat) = await SeedAsync(3);
        var id = await api.WithDbAsync(db => db.Messages.Where(m => m.ConversationId == chat && m.Seq == 3).Select(m => m.Id).SingleAsync());
        await api.WithDbAsync(async db =>
        {
            db.MessageReactions.AddRange(
                new MessageReaction { MessageId = id, UserId = bob.Id, Emoji = "👍", CreatedAt = api.Time.GetUtcNow() },
                new MessageReaction { MessageId = id, UserId = bob.Id, Emoji = "❤", CreatedAt = api.Time.GetUtcNow() },
                new MessageReaction { MessageId = id, UserId = alice.Id, Emoji = "❤", CreatedAt = api.Time.GetUtcNow() });
            await db.SaveChangesAsync();
        });

        var page = await PageAsync(alice, chat);

        var reactions = page.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("seq").GetInt64() == 3).GetProperty("reactions")
            .EnumerateArray().ToDictionary(r => r.GetProperty("emoji").GetString()!, r => (r.GetProperty("count").GetInt32(), r.GetProperty("mine").GetBoolean()));
        Assert.Equal((1, false), reactions["👍"]);
        Assert.Equal((2, true), reactions["❤"]);
    }

    [Fact]
    public async Task Non_members_and_members_who_left_get_conversation_not_found()
    {
        var owner = await api.CreatePersonAsync();
        var member = await api.CreatePersonAsync();
        var stranger = await api.CreatePersonAsync();
        var chat = await api.CreateGroupAsync(owner, member);
        (await C.PostAsync(member, $"/conversations/{chat}/leave")).EnsureSuccessStatusCode();

        await (await C.GetAsync(stranger, $"/conversations/{chat}/messages")).AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
        await (await C.GetAsync(member, $"/conversations/{chat}/messages")).AssertErrorAsync(HttpStatusCode.NotFound, "conversation_not_found");
    }

    [Fact]
    public async Task History_still_works_between_blocked_users()
    {
        var (alice, bob, chat) = await SeedAsync(3);
        (await C.PutAsync(bob, $"/blocks/{alice.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(3, Seqs(await PageAsync(alice, chat)).Length);
        Assert.Equal(3, Seqs(await PageAsync(bob, chat)).Length);
    }

    [Fact]
    public async Task Needs_authentication()
    {
        var response = await C.GetAsync($"/conversations/{Guid.NewGuid()}/messages");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
