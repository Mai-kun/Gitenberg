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

    /// <summary>
    /// EnsureCreated() does not add columns to tables that already exist, so quick-capture
    /// columns on Users are added with raw SQL for databases created before the feature.
    /// Must be called after <c>Database.EnsureCreated()</c>.
    /// </summary>
    public void EnsureUserCaptureColumnsCreated()
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = Database.GetDbConnection();
        var connectionOpened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
            connectionOpened = true;
        }

        try
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(Users);";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            AddColumnIfMissing(existingColumns, connection, "InboxPath", "inbox");
            AddColumnIfMissing(existingColumns, connection, "AttachmentsPath", "inbox/attachments");
        }
        finally
        {
            if (connectionOpened)
            {
                connection.Close();
            }
        }
    }

    private static void AddColumnIfMissing(
        HashSet<string> existingColumns,
        System.Data.Common.DbConnection connection,
        string columnName,
        string defaultValue
    )
    {
        if (existingColumns.Contains(columnName))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE Users ADD COLUMN {columnName} TEXT NOT NULL DEFAULT '{defaultValue}';";
        command.ExecuteNonQuery();
    }
}