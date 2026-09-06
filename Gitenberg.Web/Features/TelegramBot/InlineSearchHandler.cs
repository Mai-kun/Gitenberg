using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Models;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Web.Features.TelegramBot;

/// <summary>
/// Answers Telegram inline queries (typing "@bot query" in any chat) with
/// FTS5 search results from the user's notes. Choosing a result sends the
/// note text into the chat; a signed URL button downloads the note as a file.
/// </summary>
public class InlineSearchHandler(
    ITelegramBotClient botClient,
    AppDbContext dbContext,
    BotConfiguration botConfig,
    NoteIndexer indexer,
    InlineFileLinkService fileLinks,
    ILogger<InlineSearchHandler> logger
)
{
    private const int MaxResults = 10;
    private const int MaxMessageLength = 4096;
    private const int MaxTitleLength = 256;
    private const int MaxUrlLength = 256;
    private const string ZipMimeType = "application/zip";
    private const string TruncationSuffix = "\n\n…Обрезано. Выберите результат «файлом», чтобы получить полный текст.";

    public async Task HandleInlineQueryAsync(InlineQuery inlineQuery, CancellationToken cancellationToken)
    {
        try
        {
            var results = await BuildResultsAsync(inlineQuery, cancellationToken);
            await botClient.AnswerInlineQuery(
                inlineQuery.Id,
                results,
                cacheTime: 0,
                isPersonal: true,
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to answer inline query for Telegram ID: {TelegramId}", inlineQuery.From?.Id);
            await AnswerEmptyQuietlyAsync(inlineQuery.Id, cancellationToken);
        }
    }

    private async Task<List<InlineQueryResult>> BuildResultsAsync(InlineQuery inlineQuery, CancellationToken cancellationToken)
    {
        var telegramId = inlineQuery.From?.Id ?? 0;
        if (telegramId == 0)
        {
            return [];
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId, cancellationToken);
        if (user == null)
        {
            return [BuildRegistrationArticle()];
        }

        var query = inlineQuery.Query.Trim();
        if (query.Length == 0)
        {
            return [BuildHintArticle()];
        }

        // Same freshness contract as the web search endpoint: refresh the
        // index first (cheap when nothing changed), then query it.
        try
        {
            await indexer.SynchronizeUserByIdAsync(telegramId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Search index refresh failed during inline query for Telegram ID: {TelegramId}",
                telegramId
            );
        }

        var matchExpression = FtsQueryBuilder.Build(query);
        if (matchExpression.Length == 0)
        {
            return [];
        }

        List<InlineNoteHit> hits;
        try
        {
            // TelegramUserId is stored as TEXT in the FTS table, so it must be bound as a string.
            hits = await dbContext.Database.SqlQuery<InlineNoteHit>(
                $"""
                SELECT NotePath,
                       snippet(NoteSearchFts, 2, '', '', '…', 12) AS Snippet,
                       Content
                FROM NoteSearchFts
                WHERE TelegramUserId = {telegramId.ToString(CultureInfo.InvariantCulture)}
                  AND NoteSearchFts MATCH {matchExpression}
                ORDER BY rank
                LIMIT {MaxResults}
                """
            ).ToListAsync(cancellationToken);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Raised by FTS5 for malformed MATCH expressions; FtsQueryBuilder
            // prevents this, so treat anything else as "no results".
            return [];
        }

        return hits.SelectMany(hit => BuildResultsForNote(user, hit)).ToList();
    }

    /// <summary>
    /// Two results per note: choosing the article sends the note text into
    /// the chat, choosing the document makes Telegram fetch the note as a
    /// file and deliver it as a document message.
    /// </summary>
    private IEnumerable<InlineQueryResult> BuildResultsForNote(User user, InlineNoteHit hit)
    {
        yield return BuildNoteArticle(user, hit);

        var document = BuildNoteDocument(user, hit);
        if (document != null)
        {
            yield return document;
        }
    }

    private InlineQueryResultArticle BuildNoteArticle(User user, InlineNoteHit hit)
    {
        // The index stores "path\ncontent"; the note body starts after the path line.
        var content = hit.Content;
        if (content.StartsWith(hit.NotePath + "\n", StringComparison.Ordinal))
        {
            content = content[(hit.NotePath.Length + 1)..];
        }

        var header = $"📄 {hit.NotePath}\n\n";
        var body = content.Length > MaxMessageLength - header.Length - TruncationSuffix.Length
            ? content[..(MaxMessageLength - header.Length - TruncationSuffix.Length)] + TruncationSuffix
            : content;

        return new InlineQueryResultArticle
        {
            Id = ResultId("text", hit.NotePath),
            Title = BuildTitle(hit.NotePath),
            Description = BuildDescription(hit.Snippet),
            InputMessageContent = new InputTextMessageContent
            {
                MessageText = header + body,
            },
            ReplyMarkup = BuildKeyboard(user, hit.NotePath),
        };
    }

    private InlineQueryResultDocument? BuildNoteDocument(User user, InlineNoteHit hit)
    {
        if (string.IsNullOrWhiteSpace(botConfig.HostAddress))
        {
            return null;
        }

        var documentUrl = fileLinks.BuildFileUrl(
            user.TelegramId,
            hit.NotePath,
            InlineFileLinkService.ZipFormat,
            botConfig.HostAddress
        );
        if (documentUrl.Length > MaxUrlLength)
        {
            return null;
        }

        return new InlineQueryResultDocument
        {
            Id = ResultId("file", hit.NotePath),
            Title = BuildTitle(hit.NotePath),
            Description = "Отправить файлом (.md в ZIP)",
            DocumentUrl = documentUrl,
            MimeType = ZipMimeType,
            Caption = $"📄 {hit.NotePath}",
        };
    }

    private InlineKeyboardMarkup? BuildKeyboard(User user, string notePath)
    {
        var githubPath = string.Join('/', notePath.Split('/').Select(Uri.EscapeDataString));
        var githubUrl =
            $"https://github.com/{user.RepositoryOwner}/{user.RepositoryName}/blob/HEAD/{githubPath}";
        if (githubUrl.Length > MaxUrlLength)
        {
            return null;
        }

        return new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl("Открыть на GitHub", githubUrl));
    }

    /// <summary>Stable, unique result id: a hex SHA-256 of the kind and path (64 chars, the API maximum).</summary>
    private static string ResultId(string kind, string notePath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{notePath}")));

    private static string BuildTitle(string notePath)
    {
        var title = Path.GetFileName(notePath);
        if (title.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            title = title[..^3];
        }

        return title.Length > MaxTitleLength ? title[..MaxTitleLength] : title;
    }

    private static string BuildDescription(string snippet)
    {
        var description = string.Join(' ', snippet.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        return description.Length > 100 ? description[..100] : description;
    }

    private static InlineQueryResultArticle BuildRegistrationArticle() => new()
    {
        Id = "registration-hint",
        Title = "Gitenberg",
        Description = "Аккаунт не привязан",
        InputMessageContent = new InputTextMessageContent
        {
            MessageText =
                "Вы еще не зарегистрированы в Gitenberg. Откройте личный чат с ботом и отправьте /start, "
                + "чтобы привязать свой репозиторий GitHub и искать свои заметки где угодно.",
        },
    };

    private static InlineQueryResultArticle BuildHintArticle() => new()
    {
        Id = "search-hint",
        Title = "Поиск по заметкам",
        Description = "Наберите запрос после @бота",
        InputMessageContent = new InputTextMessageContent
        {
            MessageText = "Наберите запрос, чтобы найти нужную заметку, и отправьте её прямо в этот чат.",
        },
    };

    private async Task AnswerEmptyQuietlyAsync(string inlineQueryId, CancellationToken cancellationToken)
    {
        try
        {
            await botClient.AnswerInlineQuery(
                inlineQueryId,
                [],
                cacheTime: 0,
                isPersonal: true,
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send an empty inline answer after an error.");
        }
    }

    private record InlineNoteHit(string NotePath, string Snippet, string Content);
}
