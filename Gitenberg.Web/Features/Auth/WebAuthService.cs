using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Gitenberg.Web.Database;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Auth;

// Cookie-backed sessions for the web app. Only the SHA-256 hash of the token
// is stored, so a database leak cannot be replayed as a valid cookie.
public class WebAuthService(AppDbContext dbContext)
{
    public static void EnsureTableCreated(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS WebSessions (
                TokenHash TEXT PRIMARY KEY,
                UserId    INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL
            );
            """);
    }

    // Returns the plaintext token to hand to the client as a cookie value.
    public async Task<string> CreateSessionAsync(long userId, TimeSpan lifetime)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes)
                          .TrimEnd('=')
                          .Replace('+', '-')
                          .Replace('/', '_');
        var tokenHash = HashToken(token);
        var now = DateTime.UtcNow;

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO WebSessions (TokenHash, UserId, CreatedAt, ExpiresAt) VALUES ({tokenHash}, {userId}, {Format(now)}, {Format(now + lifetime)})"
        );
        return token;
    }

    // Resolves a cookie token to the user id, or null when the session is
    // unknown/expired. Expired rows are pruned on sight; a session past the
    // half-life is extended so active users are not logged out mid-work.
    public async Task<long?> ResolveSessionAsync(string? token, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var tokenHash = HashToken(token);
        var rows = await dbContext.Database.SqlQuery<WebSessionRow>(
            $"SELECT UserId AS UserId, ExpiresAt AS ExpiresAt FROM WebSessions WHERE TokenHash = {tokenHash}"
        ).ToListAsync();

        var row = rows.FirstOrDefault();
        if (row == null)
        {
            return null;
        }

        var expiresAt = Parse(row.ExpiresAt);
        if (expiresAt <= DateTime.UtcNow)
        {
            await DeleteSessionAsync(token);
            return null;
        }

        var remaining = expiresAt - DateTime.UtcNow;
        if (remaining < lifetime / 2)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE WebSessions SET ExpiresAt = {Format(DateTime.UtcNow + lifetime)} WHERE TokenHash = {tokenHash}"
            );
        }

        return row.UserId;
    }

    public async Task DeleteSessionAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM WebSessions WHERE TokenHash = {HashToken(token)}"
        );
    }

    private static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }

    private static string Format(DateTime value) => value.ToString("o", CultureInfo.InvariantCulture);

    private static DateTime Parse(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class WebSessionRow
    {
        public long UserId { get; init; }
        public string ExpiresAt { get; init; } = string.Empty;
    }
}
