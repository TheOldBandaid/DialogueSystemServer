using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

public sealed class DialogueDbContext(DbContextOptions<DialogueDbContext> options) : DbContext(options)
{
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<Dialogue> Dialogues => Set<Dialogue>();
    public DbSet<DialogueRevision> DialogueRevisions => Set<DialogueRevision>();
    public DbSet<DialogueLine> Lines => Set<DialogueLine>();
    public DbSet<DialogueChoice> Choices => Set<DialogueChoice>();
    public DbSet<DialoguePreview> Previews => Set<DialoguePreview>();
    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Dialogue>().Property(dialogue => dialogue.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<DialogueRevision>().HasOne<Dialogue>().WithMany()
            .HasForeignKey(revision => revision.DialogueId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DialogueRevision>().HasIndex(revision => new { revision.DialogueId, revision.Id });
        modelBuilder.Entity<Account>().HasIndex(account => account.Login).IsUnique();
        modelBuilder.Entity<Character>().HasIndex(character => character.Key).IsUnique();
        modelBuilder.Entity<DialoguePreview>()
            .HasIndex(preview => new { preview.UserId, preview.DialogueId, preview.StartedAtUnixMilliseconds });
        modelBuilder.Entity<PreviewHistoryEntry>()
            .HasKey(entry => new { entry.PreviewId, entry.Step });
        modelBuilder.Entity<DialoguePreview>()
            .HasMany(preview => preview.History).WithOne()
            .HasForeignKey(entry => entry.PreviewId);
        modelBuilder.Entity<DialoguePreview>().HasOne<Account>().WithMany()
            .HasForeignKey(preview => preview.UserId);
        modelBuilder.Entity<DialogueLine>().HasAlternateKey(line => new { line.DialogueId, line.Id });
        modelBuilder.Entity<DialogueLine>()
            .HasOne(line => line.Speaker).WithMany()
            .HasForeignKey(line => line.SpeakerId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Dialogue>()
            .HasOne<DialogueLine>().WithMany()
            .HasPrincipalKey(line => new { line.DialogueId, line.Id })
            .HasForeignKey(dialogue => new { dialogue.Id, dialogue.StartLineId })
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DialogueChoice>()
            .HasOne<DialogueLine>().WithMany(line => line.Choices)
            .HasPrincipalKey(line => new { line.DialogueId, line.Id })
            .HasForeignKey(choice => new { choice.DialogueId, choice.SourceLineId })
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DialogueChoice>()
            .HasOne<DialogueLine>().WithMany()
            .HasPrincipalKey(line => new { line.DialogueId, line.Id })
            .HasForeignKey(choice => new { choice.DialogueId, choice.TargetLineId })
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<DialogueChoice>()
            .HasIndex(choice => new { choice.DialogueId, choice.SourceLineId, choice.Position })
            .IsUnique();
        modelBuilder.Entity<DialoguePreview>()
            .HasOne<DialogueLine>().WithMany()
            .HasPrincipalKey(line => new { line.DialogueId, line.Id })
            .HasForeignKey(preview => new { preview.DialogueId, preview.CurrentLineId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
