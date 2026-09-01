using Gitenberg.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Database;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users { get; set; }

    public DbSet<IndexedNote> IndexedNotes { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite("Data Source=gitenberg.db");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasKey(u => u.TelegramId);
        modelBuilder.Entity<IndexedNote>().HasKey(n => new { n.TelegramUserId, n.NotePath });
    }

    /// <summary>
    /// EF Core cannot model SQLite virtual tables, so the FTS5 table is created with raw SQL.
    /// Must be called after <c>Database.EnsureCreated()</c>.
    /// </summary>
    public void EnsureFtsTableCreated()
    {
        Database.ExecuteSqlRaw(
            "CREATE VIRTUAL TABLE IF NOT EXISTS NoteSearchFts USING fts5(TelegramUserId, NotePath, Content);"
        );

        // WAL allows search reads to proceed while the indexing worker writes (no-op on in-memory databases).
        Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }
}