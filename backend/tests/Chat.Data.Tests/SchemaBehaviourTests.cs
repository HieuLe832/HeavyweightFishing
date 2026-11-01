using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chat.Data.Tests;

public class SchemaBehaviourTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static Task<PostgresException> Rejected(Task t) => Assert.ThrowsAsync<PostgresException>(() => t);

    [Fact]
    public async Task Enums_round_trip_through_ef_core()
    {
        await using (var db = pg.NewContext())
        {
            db.Users.Add(new User
            {
                Username = "Lan", Email = "lan@example.com", DisplayName = "Lan",
                Role = UserRole.Admin, Status = UserStatus.Banned,
            });
            await db.SaveChangesAsync();
        }

        await using var read = pg.NewContext();
        var user = await read.Users.SingleAsync(u => u.Username == "lan"); // citext: case-insensitive
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.Equal(UserStatus.Banned, user.Status);
        Assert.NotEqual(Guid.Empty, user.Id);
    }

    [Fact]
    public async Task Username_and_email_are_case_insensitive_unique()
    {
        var name = "Dup" + Guid.NewGuid().ToString("N")[..8];
        await pg.InsertUserAsync(name);
        var ex = await Rejected(pg.InsertUserAsync(name.ToUpperInvariant()));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task A_user_cannot_contact_or_block_themselves()
    {
        var u = await pg.InsertUserAsync();
        var c = await Rejected(pg.ExecAsync("INSERT INTO contacts (owner_id, contact_id) VALUES (@u, @u)", ("u", u)));
        var b = await Rejected(pg.ExecAsync("INSERT INTO blocks (blocker_id, blocked_id) VALUES (@u, @u)", ("u", u)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, c.SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, b.SqlState);
    }

    [Fact]
    public async Task Direct_key_is_set_exactly_for_direct_chats_and_unique()
    {
        var key = Guid.NewGuid() + ":" + Guid.NewGuid();

        var noKey = await Rejected(pg.ExecAsync("INSERT INTO conversations (type) VALUES ('direct')"));
        var groupKey = await Rejected(pg.ExecAsync(
            "INSERT INTO conversations (type, direct_key) VALUES ('group', @k)", ("k", key)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, noKey.SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, groupKey.SqlState);

        await pg.ExecAsync("INSERT INTO conversations (type, direct_key) VALUES ('direct', @k)", ("k", key));
        var dup = await Rejected(pg.ExecAsync(
            "INSERT INTO conversations (type, direct_key) VALUES ('direct', @k)", ("k", key)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, dup.SqlState);

        // Many groups, all with a NULL direct_key, are fine.
        await pg.InsertGroupAsync();
        await pg.InsertGroupAsync();
    }

    [Fact]
    public async Task Audit_log_rejects_update_delete_and_truncate()
    {
        await pg.ExecAsync("""
            INSERT INTO audit_logs (action, target_type, target_id, ip_address)
            VALUES ('user.ban', 'user', 'x', '127.0.0.1')
            """);

        foreach (var sql in new[]
                 {
                     "UPDATE audit_logs SET action = 'tampered'",
                     "DELETE FROM audit_logs",
                     "TRUNCATE audit_logs",
                 })
        {
            var ex = await Rejected(pg.ExecAsync(sql));
            Assert.Equal(PostgresErrorCodes.RaiseException, ex.SqlState);
        }
    }

    [Fact]
    public async Task A_user_cannot_have_an_uploaded_and_a_preset_avatar_at_once()
    {
        var user = await pg.InsertUserAsync();
        var media = Guid.NewGuid();
        await pg.ExecAsync("""
            INSERT INTO media (id, uploader_id, storage_key, mime_type, size_bytes, sha256, status)
            VALUES (@m, @u, 'k', 'image/png', 1, decode(md5('x'), 'hex'), 'ready')
            """, ("m", media), ("u", user));
        await pg.ExecAsync("UPDATE users SET avatar_preset_key = 'cap' WHERE id = @u", ("u", user));

        var ex = await Rejected(pg.ExecAsync("UPDATE users SET avatar_media_id = @m WHERE id = @u", ("m", media), ("u", user)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_users_one_avatar", ex.ConstraintName);
    }

    [Fact]
    public async Task Deleting_a_session_cascades_to_its_refresh_tokens_but_a_user_with_sessions_cannot_be_deleted()
    {
        var user = await pg.InsertUserAsync();
        var session = Guid.NewGuid();
        await pg.ExecAsync("""
            INSERT INTO sessions (id, user_id, device_name, user_agent, ip_address, expires_at)
            VALUES (@s, @u, 'phone', 'ua', '10.0.0.1', now() + interval '1 day')
            """, ("s", session), ("u", user));
        await pg.ExecAsync("""
            INSERT INTO refresh_tokens (session_id, token_hash, expires_at)
            VALUES (@s, decode(md5(random()::text), 'hex'), now() + interval '1 day')
            """, ("s", session));

        var ex = await Rejected(pg.ExecAsync("DELETE FROM users WHERE id = @u", ("u", user)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);

        await pg.ExecAsync("DELETE FROM sessions WHERE id = @s", ("s", session));
        var left = await pg.QueryAsync($"SELECT count(*)::text FROM refresh_tokens WHERE session_id = '{session}'");
        Assert.Equal("0", left.Single());
    }

    [Fact]
    public async Task Locking_the_conversation_row_gives_gap_free_unique_seq_under_concurrency()
    {
        var conversation = await pg.InsertGroupAsync();
        const int senders = 25;

        await Task.WhenAll(Enumerable.Range(0, senders).Select(async _ =>
        {
            await using var conn = await pg.DataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await using var bump = new NpgsqlCommand(
                "UPDATE conversations SET last_seq = last_seq + 1 WHERE id = @c RETURNING last_seq", conn, tx);
            bump.Parameters.AddWithValue("c", conversation);
            var seq = (long)(await bump.ExecuteScalarAsync())!;

            await using var insert = new NpgsqlCommand("""
                INSERT INTO messages (conversation_id, seq, client_msg_id, type, body)
                VALUES (@c, @s, gen_random_uuid(), 'system', 'hi')
                """, conn, tx);
            insert.Parameters.AddWithValue("c", conversation);
            insert.Parameters.AddWithValue("s", seq);
            await insert.ExecuteNonQueryAsync();

            await tx.CommitAsync();
        }));

        var seqs = await pg.QueryAsync(
            $"SELECT m.seq::text FROM messages m WHERE m.conversation_id = '{conversation}' ORDER BY m.seq");
        Assert.Equal(Enumerable.Range(1, senders).Select(i => i.ToString()), seqs);
    }

    [Fact]
    public async Task Client_message_id_dedupes_per_sender_but_not_system_messages()
    {
        var conversation = await pg.InsertGroupAsync();
        var sender = await pg.InsertUserAsync();
        var clientId = Guid.NewGuid();
        const string insert = """
            INSERT INTO messages (conversation_id, seq, sender_id, client_msg_id, type, body)
            VALUES (@c, @s, @u, @m, 'text', 'x')
            """;

        await pg.ExecAsync(insert, ("c", conversation), ("s", 1L), ("u", sender), ("m", clientId));
        var retry = await Rejected(pg.ExecAsync(insert, ("c", conversation), ("s", 2L), ("u", sender), ("m", clientId)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, retry.SqlState);

        // Pitfall: NULL senders are distinct, so the same client_msg_id twice is accepted for system messages.
        await pg.ExecAsync(insert, ("c", conversation), ("s", 3L), ("u", null), ("m", clientId));
        await pg.ExecAsync(insert, ("c", conversation), ("s", 4L), ("u", null), ("m", clientId));
    }
}
