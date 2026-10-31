using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chat.Data.Configurations;

// D4

internal class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> b)
    {
        b.ToTable("contacts", t => t.HasCheckConstraint("ck_contacts_not_self", "owner_id <> contact_id"));
        b.HasKey(x => new { x.OwnerId, x.ContactId });
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class BlockConfiguration : IEntityTypeConfiguration<Block>
{
    public void Configure(EntityTypeBuilder<Block> b)
    {
        b.ToTable("blocks", t => t.HasCheckConstraint("ck_blocks_not_self", "blocker_id <> blocked_id"));
        b.HasKey(x => new { x.BlockerId, x.BlockedId });
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.BlockerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.BlockedId).OnDelete(DeleteBehavior.Restrict);
    }
}
