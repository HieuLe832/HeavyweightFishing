using Chat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chat.Data.Configurations;

// D1

internal class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        // One avatar at a time: an upload or a preset key from the Avatar Provider.
        b.ToTable("users", t => t.HasCheckConstraint("ck_users_one_avatar", "avatar_media_id IS NULL OR avatar_preset_key IS NULL"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.Username).HasColumnType("citext");
        b.Property(x => x.AvatarPresetKey).HasMaxLength(64);
        b.Property(x => x.Email).HasColumnType("citext");
        b.HasIndex(x => x.Username).IsUnique();
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.FailedLoginCount).HasDefaultValue((short)0);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        // The users <-> media foreign-key cycle: the migration adds this one after both tables exist.
        b.HasOne<Media>().WithMany().HasForeignKey(x => x.AvatarMediaId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class OAuthIdentityConfiguration : IEntityTypeConfiguration<OAuthIdentity>
{
    public void Configure(EntityTypeBuilder<OAuthIdentity> b)
    {
        b.ToTable("oauth_identities");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.Email).HasColumnType("citext");
        b.Property(x => x.LinkedAt).HasDefaultValueSql("now()");
        b.HasIndex(x => new { x.Provider, x.ProviderSubject }).IsUnique();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
