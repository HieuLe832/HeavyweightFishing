using Microsoft.EntityFrameworkCore;

namespace Chat.Data.Tests;

/// <summary>
/// The expected schema is written out from docs/diagrams/04-erd.drawio (plus conversations.last_seq).
/// Column format: name type, with a trailing ? when nullable.
/// </summary>
public class ErdSchemaTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly Dictionary<string, string[]> Tables = new()
    {
        ["users"] = ["id uuid", "username citext", "email citext", "email_verified_at timestamptz?",
            "password_hash text?", "display_name text", "avatar_media_id uuid?", "avatar_preset_key varchar?", "role user_role",
            "status user_status", "failed_login_count int2", "locked_until timestamptz?",
            "created_at timestamptz", "updated_at timestamptz"],
        ["oauth_identities"] = ["id uuid", "user_id uuid", "provider text", "provider_subject text",
            "email citext", "linked_at timestamptz"],
        ["sessions"] = ["id uuid", "user_id uuid", "device_name text", "user_agent text", "ip_address inet",
            "push_platform push_platform?", "push_token text?", "created_at timestamptz",
            "last_seen_at timestamptz", "expires_at timestamptz", "revoked_at timestamptz?",
            "revoke_reason text?"],
        ["refresh_tokens"] = ["id uuid", "session_id uuid", "token_hash bytea", "issued_at timestamptz",
            "expires_at timestamptz", "used_at timestamptz?"],
        ["verification_codes"] = ["id uuid", "user_id uuid", "purpose verification_purpose", "code_hash text",
            "attempts int2", "expires_at timestamptz", "consumed_at timestamptz?", "created_at timestamptz"],
        ["contacts"] = ["owner_id uuid", "contact_id uuid", "nickname text?", "created_at timestamptz"],
        ["blocks"] = ["blocker_id uuid", "blocked_id uuid", "created_at timestamptz"],
        ["conversations"] = ["id uuid", "type conversation_type", "title text?", "avatar_media_id uuid?",
            "direct_key text?", "last_seq int8", "created_at timestamptz", "updated_at timestamptz"],
        ["conversation_members"] = ["conversation_id uuid", "user_id uuid", "role member_role",
            "joined_at timestamptz", "left_at timestamptz?", "muted_until timestamptz?",
            "last_delivered_seq int8", "last_read_seq int8"],
        ["messages"] = ["id uuid", "conversation_id uuid", "seq int8", "sender_id uuid?", "client_msg_id uuid",
            "type message_type", "body text?", "reply_to_id uuid?", "created_at timestamptz",
            "edited_at timestamptz?", "deleted_at timestamptz?"],
        ["message_reactions"] = ["message_id uuid", "user_id uuid", "emoji text", "created_at timestamptz"],
        ["message_attachments"] = ["message_id uuid", "media_id uuid", "position int2"],
        ["media"] = ["id uuid", "uploader_id uuid", "storage_key text", "mime_type text", "size_bytes int8",
            "width int4?", "height int4?", "sha256 bytea", "status media_status", "created_at timestamptz"],
        ["reports"] = ["id uuid", "reporter_id uuid", "target_user_id uuid", "message_id uuid?",
            "reason report_reason", "details text?", "evidence jsonb", "status report_status",
            "resolved_by uuid?", "resolved_at timestamptz?", "created_at timestamptz"],
        ["audit_logs"] = ["id int8", "actor_id uuid?", "action text", "target_type text", "target_id text",
            "metadata jsonb", "ip_address inet", "created_at timestamptz"],
    };

    private static readonly string[] PrimaryKeys =
    [
        "users(id)", "oauth_identities(id)", "sessions(id)", "refresh_tokens(id)", "verification_codes(id)",
        "contacts(owner_id,contact_id)", "blocks(blocker_id,blocked_id)", "conversations(id)",
        "conversation_members(conversation_id,user_id)", "messages(id)",
        "message_reactions(message_id,user_id,emoji)", "message_attachments(message_id,media_id)",
        "media(id)", "reports(id)", "audit_logs(id)",
    ];

    private static readonly string[] UniqueKeys =
    [
        "users(username)", "users(email)", "oauth_identities(provider,provider_subject)",
        "refresh_tokens(token_hash)", "conversations(direct_key)", "messages(conversation_id,seq)",
        "messages(sender_id,client_msg_id)", "media(storage_key)",
    ];

    // table.column -> referenced table, delete rule (a = no action/restrict, c = cascade)
    private static readonly string[] ForeignKeys =
    [
        "users.avatar_media_id>media", "oauth_identities.user_id>users", "sessions.user_id>users",
        "refresh_tokens.session_id>sessions:c", "verification_codes.user_id>users",
        "contacts.owner_id>users", "contacts.contact_id>users", "blocks.blocker_id>users",
        "blocks.blocked_id>users", "conversations.avatar_media_id>media",
        "conversation_members.conversation_id>conversations", "conversation_members.user_id>users",
        "messages.conversation_id>conversations", "messages.sender_id>users", "messages.reply_to_id>messages",
        "message_reactions.message_id>messages", "message_reactions.user_id>users",
        "message_attachments.message_id>messages", "message_attachments.media_id>media",
        "media.uploader_id>users", "reports.reporter_id>users", "reports.target_user_id>users",
        "reports.message_id>messages", "reports.resolved_by>users", "audit_logs.actor_id>users",
    ];

    [Fact]
    public async Task Every_erd_table_has_exactly_the_drawn_columns()
    {
        var actual = (await pg.QueryAsync("""
            SELECT table_name || ' ' || column_name || ' ' || udt_name ||
                   CASE WHEN is_nullable = 'YES' THEN '?' ELSE '' END
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
            """)).Order().ToList();

        var expected = Tables
            .SelectMany(t => t.Value.Select(c => $"{t.Key} {c}"))
            .Order().ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Migration_is_in_sync_with_the_model()
    {
        using var db = pg.NewContext();
        Assert.False(db.Database.HasPendingModelChanges(), "model changed: add a migration");
    }

    [Fact]
    public async Task Primary_keys_match()
    {
        var actual = await pg.QueryAsync("""
            SELECT c.conrelid::regclass::text || '(' ||
                   (SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                    FROM unnest(c.conkey) WITH ORDINALITY k(attnum, ord)
                    JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum) || ')'
            FROM pg_constraint c
            WHERE c.contype = 'p' AND c.connamespace = 'public'::regnamespace
              AND c.conrelid::regclass::text <> '"__EFMigrationsHistory"'
            """);
        Assert.Equal(PrimaryKeys.Order(), actual.Order());
    }

    [Fact]
    public async Task Unique_keys_match()
    {
        var actual = await pg.QueryAsync("""
            SELECT i.indrelid::regclass::text || '(' ||
                   (SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                    FROM unnest(i.indkey::smallint[]) WITH ORDINALITY k(attnum, ord)
                    JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum) || ')'
            FROM pg_index i
            WHERE i.indisunique AND NOT i.indisprimary AND i.indrelid::regclass::text !~ '^"?__EF'
              AND i.indrelid IN (SELECT oid FROM pg_class WHERE relnamespace = 'public'::regnamespace)
            """);
        Assert.Equal(UniqueKeys.Order(), actual.Order());
    }

    [Fact]
    public async Task Foreign_keys_match_and_only_refresh_tokens_cascade()
    {
        var actual = await pg.QueryAsync("""
            SELECT c.conrelid::regclass::text || '.' || a.attname || '>' || c.confrelid::regclass::text ||
                   CASE c.confdeltype WHEN 'c' THEN ':c' WHEN 'a' THEN '' WHEN 'r' THEN '' ELSE ':?' END
            FROM pg_constraint c
            JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
            WHERE c.contype = 'f' AND c.connamespace = 'public'::regnamespace
            """);
        Assert.Equal(ForeignKeys.Order(), actual.Order());
    }

    [Fact]
    public async Task Enums_have_the_drawn_labels_in_any_order()
    {
        var actual = await pg.QueryAsync("""
            SELECT t.typname || '=' || string_agg(e.enumlabel, ',' ORDER BY e.enumlabel)
            FROM pg_type t JOIN pg_enum e ON e.enumtypid = t.oid GROUP BY t.typname
            """);
        // EF Core's migration creates enum labels alphabetically; nothing may rely on enum ordering.
        string[] expected =
        [
            "user_role=admin,user", "user_status=active,banned,pending", "push_platform=apns,fcm,web",
            "verification_purpose=reset_password,verify_email", "conversation_type=direct,group",
            "member_role=admin,member,owner", "message_type=media,system,text",
            "media_status=pending,ready,rejected", "report_reason=harassment,other,spam",
            "report_status=actioned,dismissed,open",
        ];
        Assert.Equal(expected.Order(), actual.Order());
    }
}
