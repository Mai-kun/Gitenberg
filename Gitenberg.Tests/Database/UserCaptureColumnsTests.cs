using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Gitenberg.Web.Database;
using Gitenberg.Web.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gitenberg.Tests.Database;

public class UserCaptureColumnsTests
{
    /// <summary>
    /// Simulates a gitenberg.db created before quick capture existed: the Users
    /// table is created manually so EnsureCreated() becomes a no-op.
    /// </summary>
    private static (SqliteConnection Connection, AppDbContext DbContext) CreateLegacyDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE Users (
                    TelegramId INTEGER NOT NULL PRIMARY KEY,
                    GitHubToken TEXT,
                    RepositoryOwner TEXT NOT NULL,
                    RepositoryName TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    LastActivityAt TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
                      .UseSqlite(connection)
                      .Options;
        var dbContext = new AppDbContext(options);
        dbContext.Database.EnsureCreated();

        return (connection, dbContext);
    }

    private static List<(string Name, string DefaultValue)> GetColumns(AppDbContext dbContext)
    {
        var columns = new List<(string Name, string DefaultValue)>();
        var connection = dbContext.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Users);";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add((reader.GetString(1), reader.IsDBNull(4) ? string.Empty : reader.GetString(4)));
        }

        return columns;
    }

    [Fact]
    public void EnsureUserCaptureColumnsCreated_ShouldAddMissingColumns_WithDefaults()
    {
        // Arrange
        var (connection, dbContext) = CreateLegacyDatabase();
        try
        {
            // Act
            dbContext.EnsureUserCaptureColumnsCreated();

            // Assert
            var columns = GetColumns(dbContext);
            columns.Should().Contain(c => c.Name == "InboxPath" && c.DefaultValue == "'inbox'");
            columns.Should().Contain(c => c.Name == "AttachmentsPath" && c.DefaultValue == "'inbox/attachments'");
        }
        finally
        {
            connection.Dispose();
        }
    }

    [Fact]
    public void EnsureUserCaptureColumnsCreated_ShouldBeIdempotent_WhenRunTwice()
    {
        // Arrange
        var (connection, dbContext) = CreateLegacyDatabase();
        try
        {
            // Act
            dbContext.EnsureUserCaptureColumnsCreated();
            dbContext.EnsureUserCaptureColumnsCreated();

            // Assert
            var columns = GetColumns(dbContext);
            columns.Count(c => c.Name == "InboxPath").Should().Be(1);
            columns.Count(c => c.Name == "AttachmentsPath").Should().Be(1);
        }
        finally
        {
            connection.Dispose();
        }
    }

    [Fact]
    public void EnsureUserCaptureColumnsCreated_ShouldBackfillExistingRows_WithDefaultValues()
    {
        // Arrange
        var (connection, dbContext) = CreateLegacyDatabase();
        try
        {
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO Users (TelegramId, RepositoryOwner, RepositoryName, CreatedAt, LastActivityAt)
                    VALUES (42, 'octocat', 'my-notes', '2026-01-01', '2026-01-01');
                    """;
                insert.ExecuteNonQuery();
            }

            // Act
            dbContext.EnsureUserCaptureColumnsCreated();

            // Assert
            var user = dbContext.Users.AsNoTracking().Single(u => u.TelegramId == 42);
            user.InboxPath.Should().Be("inbox");
            user.AttachmentsPath.Should().Be("inbox/attachments");
        }
        finally
        {
            connection.Dispose();
        }
    }
}
