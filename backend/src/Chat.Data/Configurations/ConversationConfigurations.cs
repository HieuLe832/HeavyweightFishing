using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chat.Data.Configurations;

// D5

internal class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> b)
    {
        b.ToTable("conversations", t => t.HasCheckConstraint(
            "ck_conversations_direct_key",
            "(type = 'direct'::conversation_type) = (direct_key IS NOT NULL)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.LastSeq).HasDefaultValue(0L);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        // Unique, and NULL for groups (PostgreSQL treats NULLs as distinct).
        b.HasIndex(x => x.DirectKey).IsUnique();
        b.HasOne<Media>().WithMany().HasForeignKey(x => x.AvatarMediaId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class ConversationMemberConfiguration : IEntityTypeConfiguration<ConversationMember>
{
    public void Configure(EntityTypeBuilder<ConversationMember> b)
    {
        b.ToTable("conversation_members");
        b.HasKey(x => new { x.ConversationId, x.UserId });
        b.Property(x => x.JoinedAt).HasDefaultValueSql("now()");
        b.Property(x => x.LastDeliveredSeq).HasDefaultValue(0L);
        b.Property(x => x.LastReadSeq).HasDefaultValue(0L);
        b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
