using FluentAssertions;
using Gitenberg.Web.Database;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Repository = Gitenberg.Web.Models.Repository;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Database;

/// <summary>
/// Multi-repo migration: legacy single-repo databases are seeded into the
/// Repositories table, and the migration is idempotent (fresh EnsureCreated
/// databases pass through unchanged).
/// </summary>
public class RepositoriesMigrationTests
{
    private readonly TokenEncryptionService _encryptionService;

    public RepositoriesMigrationTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _encryptionService = new TokenEncryptionService(provider);
    }

    private AppDbContext CreateDbContext()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
                      .UseSqlite(connection)
                      .Options;

        var dbContext = new AppDbContext(options);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    /// <summary>Legacy database: the Users table exists without the new columns.</summary>
    private static AppDbContext CreateLegacyDbContext(out SqliteConnection connection)
    {
        connection = new SqliteConnection("DataSource=:memory:");
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
                    LastActivityAt TEXT NOT NULL,
                    InboxPath TEXT NOT NULL DEFAULT 'inbox',
                    AttachmentsPath TEXT NOT NULL DEFAULT 'inbox/attachments'
                );
                """;
            command.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
                      .UseSqlite(connection)
                      .Options;
        var dbContext = new AppDbContext(options);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private IRepositoryContextResolver CreateResolver(AppDbContext db) =>
        new RepositoryContextResolver(db, _encryptionService, NullLogger<RepositoryContextResolver>.Instance);

    [Fact]
    public async Task Migration_ShouldSeedRepositoryFromLegacyUserColumns_AndSelectIt()
    {
        var dbContext = CreateLegacyDbContext(out var connection);
        try
        {
            dbContext.Database.ExecuteSqlRaw("""
                INSERT INTO Users (TelegramId, GitHubToken, RepositoryOwner, RepositoryName, CreatedAt, LastActivityAt)
                VALUES (42, 'encrypted-legacy-token', 'octocat', 'my-notes', '2026-01-01 00:00:00', '2026-01-01 00:00:00');
                """);

            dbContext.EnsureFtsTableCreated();
            dbContext.EnsureRepositoriesTableCreated();

            var repository = await dbContext.Repositories.SingleAsync(r => r.TelegramUserId == 42);
            repository.RepositoryOwner.Should().Be("octocat");
            repository.RepositoryName.Should().Be("my-notes");
            repository.GitHubToken.Should().Be("encrypted-legacy-token");
            repository.InboxPath.Should().Be("inbox");

            var user = await dbContext.Users.AsNoTracking().SingleAsync(u => u.TelegramId == 42);
            user.SelectedRepositoryId.Should().Be(repository.Id);

            // The resolver resolves the seeded repository.
            var resolved = await CreateResolver(dbContext).ResolveActiveAsync(42);
            resolved.Should().BeNull(); // legacy token was never encrypted with this key ring
        }
        finally
        {
            await dbContext.DisposeAsync();
            connection.Dispose();
        }
    }

    [Fact]
    public async Task Migration_ShouldResolveActiveRepository_AfterSeedingWithEncryptedToken()
    {
        var dbContext = CreateDbContext();
        try
        {
            var user = new User { TelegramId = 42, CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow };
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();

            var repository = new Repository
            {
                TelegramUserId = 42,
                DisplayName = "octocat/my-notes",
                RepositoryOwner = "octocat",
                RepositoryName = "my-notes",
                GitHubToken = _encryptionService.EncryptToken("pat_42", TimeSpan.FromMinutes(10)),
                CreatedAt = DateTime.UtcNow,
            };
            dbContext.Repositories.Add(repository);
            await dbContext.SaveChangesAsync();

            // SelectedRepositoryId is null (user registered before the switch) —
            // the resolver falls back to the first repository.
            var resolved = await CreateResolver(dbContext).ResolveActiveAsync(42);

            resolved.Should().NotBeNull();
            resolved!.RepositoryId.Should().Be(repository.Id);
            resolved.Context.Token.Should().Be("pat_42");
            resolved.Context.Owner.Should().Be("octocat");
            resolved.Context.Repo.Should().Be("my-notes");
        }
        finally
        {
            await dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task Migration_ShouldBeIdempotent_WhenRunTwice()
    {
        var dbContext = CreateDbContext();
        try
        {
            var user = new User
            {
                TelegramId = 42,
                RepositoryOwner = "octocat",
                RepositoryName = "my-notes",
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow,
            };
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();

            dbContext.EnsureRepositoriesTableCreated();
            dbContext.EnsureRepositoriesTableCreated();

            dbContext.Repositories.Should().ContainSingle();
            (await dbContext.Users.AsNoTracking().SingleAsync(u => u.TelegramId == 42))
                .SelectedRepositoryId.Should().NotBeNull();
        }
        finally
        {
            await dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task Migration_ShouldNotSeedRepository_WhenOwnerIsEmpty()
    {
        var dbContext = CreateDbContext();
        try
        {
            dbContext.Users.Add(new User { TelegramId = 42, CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow });
            await dbContext.SaveChangesAsync();

            dbContext.EnsureRepositoriesTableCreated();

            dbContext.Repositories.Should().BeEmpty();
            (await dbContext.Users.AsNoTracking().SingleAsync(u => u.TelegramId == 42))
                .SelectedRepositoryId.Should().BeNull();
        }
        finally
        {
            await dbContext.DisposeAsync();
        }
    }
}
