using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chat.Data.Configurations;

// D6

internal class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> b)
    {
        b.ToTable("messages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        // UK1: total order per conversation. UK2: a client can retry a send without duplicating it.
        // System messages have a NULL sender, and NULLs are distinct, so UK2 does not dedupe them.
        b.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
        b.HasIndex(x => new { x.SenderId, x.ClientMsgId }).IsUnique();

        b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Message>().WithMany().HasForeignKey(x => x.ReplyToId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class MessageReactionConfiguration : IEntityTypeConfiguration<MessageReaction>
{
    public void Configure(EntityTypeBuilder<MessageReaction> b)
    {
        b.ToTable("message_reactions");
        b.HasKey(x => new { x.MessageId, x.UserId, x.Emoji });
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class MessageAttachmentConfiguration : IEntityTypeConfiguration<MessageAttachment>
{
    public void Configure(EntityTypeBuilder<MessageAttachment> b)
    {
        b.ToTable("message_attachments");
        b.HasKey(x => new { x.MessageId, x.MediaId });
        b.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Media>().WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Restrict);
    }
}
