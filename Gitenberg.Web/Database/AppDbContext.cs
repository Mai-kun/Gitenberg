using Gitenberg.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Database;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users { get; set; }

    public DbSet<Repository> Repositories { get; set; }

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
        modelBuilder.Entity<Repository>().HasKey(r => r.Id);
        modelBuilder.Entity<Repository>()
                    .HasOne<User>()
                    .WithMany()
                    .HasForeignKey(r => r.TelegramUserId)
                    .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<IndexedNote>().HasKey(n => new { n.TelegramUserId, n.RepositoryId, n.NotePath });
    }

    /// <summary>
    /// EF Core cannot model SQLite virtual tables, so the FTS5 table is created with raw SQL.
    /// Must be called after <c>Database.EnsureCreated()</c>.
    /// </summary>
    public void EnsureFtsTableCreated()
    {
        Database.ExecuteSqlRaw(
            "CREATE VIRTUAL TABLE IF NOT EXISTS NoteSearchFts USING fts5(TelegramUserId, RepositoryId, NotePath, Content);"
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
        var existingColumns = GetTableColumns("Users");

        AddColumnIfMissing(existingColumns, "Users", "InboxPath", "TEXT NOT NULL DEFAULT 'inbox'");
        AddColumnIfMissing(existingColumns, "Users", "AttachmentsPath", "TEXT NOT NULL DEFAULT 'inbox/attachments'");
    }

    /// <summary>
    /// Multi-repo migration: introduces the Repositories table and the
    /// RepositoryId dimension on everything that stores per-note data
    /// (search index, pending ops, reminders). Databases created before
    /// multi-repo had a single repository per user stored on the Users row,
    /// so that row is seeded as the user's first repository. Idempotent:
    /// every step checks the current schema and skips when already applied,
    /// so databases created fresh under the new model (including test
    /// EnsureCreated databases) pass through unchanged.
    /// Must be called after <c>Database.EnsureCreated()</c>, the FTS table,
    /// PendingNoteOps and Reminders tables.
    /// </summary>
    public void EnsureRepositoriesTableCreated()
    {
        Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS Repositories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TelegramUserId INTEGER NOT NULL,
                DisplayName TEXT NOT NULL,
                RepositoryOwner TEXT NOT NULL,
                RepositoryName TEXT NOT NULL,
                GitHubToken TEXT NULL,
                InboxPath TEXT NOT NULL DEFAULT 'inbox',
                AttachmentsPath TEXT NOT NULL DEFAULT 'inbox/attachments',
                CreatedAt TEXT NOT NULL
            );
            """);

        var userColumns = GetTableColumns("Users");
        AddColumnIfMissing(userColumns, "Users", "SelectedRepositoryId", "INTEGER NULL");

        // Seed one repository per user from the legacy single-repo columns;
        // users without a configured repo get none.
        Database.ExecuteSqlRaw("""
            INSERT INTO Repositories (TelegramUserId, DisplayName, RepositoryOwner, RepositoryName, GitHubToken, InboxPath, AttachmentsPath, CreatedAt)
            SELECT u.TelegramId,
                   u.RepositoryOwner || '/' || u.RepositoryName,
                   u.RepositoryOwner,
                   u.RepositoryName,
                   u.GitHubToken,
                   u.InboxPath,
                   u.AttachmentsPath,
                   strftime('%Y-%m-%d %H:%M:%S', 'now')
            FROM Users u
            WHERE u.RepositoryOwner IS NOT NULL AND u.RepositoryOwner != ''
              AND NOT EXISTS (SELECT 1 FROM Repositories r WHERE r.TelegramUserId = u.TelegramId);
            """);

        Database.ExecuteSqlRaw("""
            UPDATE Users
            SET SelectedRepositoryId = (SELECT r.Id FROM Repositories r WHERE r.TelegramUserId = Users.TelegramId)
            WHERE SelectedRepositoryId IS NULL
              AND EXISTS (SELECT 1 FROM Repositories r WHERE r.TelegramUserId = Users.TelegramId);
            """);

        BackfillRepositoryIdColumn("PendingNoteOps");
        BackfillRepositoryIdColumn("Reminders");

        RebuildIndexedNotesWithRepositoryId();
        RebuildNoteSearchFtsWithRepositoryId();
    }

    /// <summary>
    /// Adds the RepositoryId column to a raw-SQL per-note table (if the table
    /// exists and lacks it) and maps pre-existing rows to the user's seeded
    /// repository.
    /// </summary>
    private void BackfillRepositoryIdColumn(string tableName)
    {
        if (!TableExists(tableName))
        {
            return;
        }

        var columns = GetTableColumns(tableName);
        if (!columns.Contains("RepositoryId"))
        {
            // Identifier is a compile-time constant: inline it (a hole would
            // become a bound parameter, and ALTER TABLE @p0 is invalid).
#pragma warning disable EF1003 // identifier from a compile-time constant, cannot be a parameter
            Database.ExecuteSqlRaw("ALTER TABLE " + tableName + " ADD COLUMN RepositoryId TEXT NULL;");
#pragma warning restore EF1003
        }

#pragma warning disable EF1003 // identifier from a compile-time constant, cannot be a parameter
        Database.ExecuteSqlRaw(
            "UPDATE " + tableName + " " +
            "SET RepositoryId = (" +
            "SELECT r.Id FROM Repositories r WHERE r.TelegramUserId = CAST(" + tableName + ".TelegramUserId AS INTEGER)) " +
            "WHERE RepositoryId IS NULL;");
#pragma warning restore EF1003
    }

    // SQLite cannot change a primary key in place: rebuild the table with the
    // RepositoryId dimension, mapping existing rows to the seeded repository.
    private void RebuildIndexedNotesWithRepositoryId()
    {
        if (!TableExists("IndexedNotes") || GetTableColumns("IndexedNotes").Contains("RepositoryId"))
        {
            return;
        }

        Database.ExecuteSqlRaw("""
            CREATE TABLE "IndexedNotes_new" (
                "TelegramUserId" INTEGER NOT NULL,
                "RepositoryId" INTEGER NOT NULL,
                "NotePath" TEXT NOT NULL,
                "Sha" TEXT NOT NULL,
                CONSTRAINT "PK_IndexedNotes" PRIMARY KEY ("TelegramUserId", "RepositoryId", "NotePath")
            );
            """);
        Database.ExecuteSqlRaw("""
            INSERT INTO "IndexedNotes_new" ("TelegramUserId", "RepositoryId", "NotePath", "Sha")
            SELECT n."TelegramUserId", r.Id, n."NotePath", n."Sha"
            FROM "IndexedNotes" n
            JOIN Repositories r ON r.TelegramUserId = n."TelegramUserId";
            """);
        Database.ExecuteSqlRaw("DROP TABLE \"IndexedNotes\";");
        Database.ExecuteSqlRaw("ALTER TABLE \"IndexedNotes_new\" RENAME TO \"IndexedNotes\";");
    }

    // FTS5 tables cannot be altered either: rename, recreate with the
    // RepositoryId column, copy the rows over, drop the legacy one. Copying
    // (instead of starting empty) avoids a full re-download of every note.
    private void RebuildNoteSearchFtsWithRepositoryId()
    {
        if (!TableExists("NoteSearchFts") || GetTableColumns("NoteSearchFts").Contains("RepositoryId"))
        {
            return;
        }

        Database.ExecuteSqlRaw("ALTER TABLE NoteSearchFts RENAME TO NoteSearchFts_legacy;");
        Database.ExecuteSqlRaw(
            "CREATE VIRTUAL TABLE NoteSearchFts USING fts5(TelegramUserId, RepositoryId, NotePath, Content);");
        Database.ExecuteSqlRaw("""
            INSERT INTO NoteSearchFts (TelegramUserId, RepositoryId, NotePath, Content)
            SELECT f.TelegramUserId,
                   (SELECT r.Id FROM Repositories r WHERE r.TelegramUserId = CAST(f.TelegramUserId AS INTEGER)),
                   f.NotePath,
                   f.Content
            FROM NoteSearchFts_legacy f;
            """);
        Database.ExecuteSqlRaw("DROP TABLE NoteSearchFts_legacy;");
    }

    private bool TableExists(string tableName)
    {
        var count = Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {tableName}"
        ).Single();
        return count > 0;
    }

    private HashSet<string> GetTableColumns(string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists(tableName))
        {
            return columns;
        }

        // Table names come from compile-time constants, so inline concatenation
        // is safe here; identifiers cannot be bound as SQL parameters. Note:
        // SqlQuery (not Raw) would turn the hole into @p0 — and '@p0' in quotes
        // is a literal, so pragma_table_info would return nothing.
#pragma warning disable EF1003 // identifier from a compile-time constant, cannot be a parameter
        var rows = Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_table_info('" + tableName + "')"
        ).ToList();
#pragma warning restore EF1003
        foreach (var name in rows)
        {
            columns.Add(name);
        }

        return columns;
    }

    private void AddColumnIfMissing(
        HashSet<string> existingColumns,
        string tableName,
        string columnName,
        string columnDefinition
    )
    {
        if (existingColumns.Contains(columnName))
        {
            return;
        }

        // Identifiers must be inlined: an interpolated hole would become a
        // bound parameter, and ALTER TABLE @p0 is a syntax error.
#pragma warning disable EF1003 // identifiers from compile-time constants, cannot be parameters
        Database.ExecuteSqlRaw(
            "ALTER TABLE " + tableName + " ADD COLUMN " + columnName + " " + columnDefinition + ";");
#pragma warning restore EF1003
    }
}
