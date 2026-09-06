using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace Gitenberg.Tests.Mocks;

/// <summary>
/// Hand-rolled ITelegramBotClient fake: records outgoing messages, serves a
/// configurable TGFile for GetFile and bytes for DownloadFile.
/// </summary>
public class FakeTelegramBotClient : ITelegramBotClient
{
    public List<(long ChatId, string Text, string? ButtonText)> SentMessages { get; } = new();

    // Served as the response to GetFileRequest (photo metadata).
    public TGFile FileToReturn { get; set; } = new() { FileId = "file-id", FilePath = "photos/photo.jpg" };

    // Bytes written into the destination stream by DownloadFile.
    public byte[] FileContent { get; set; } = [1, 2, 3, 4];

    public bool ThrowOnSendMessage { get; set; }

    public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (ThrowOnSendMessage && request is SendMessageRequest)
        {
            throw new InvalidOperationException("Telegram send failed");
        }

        if (request is SendMessageRequest sendMessage)
        {
            var buttonText = (sendMessage.ReplyMarkup as Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup)
                ?.InlineKeyboard.FirstOrDefault()?.FirstOrDefault()?.Text;
            SentMessages.Add((sendMessage.ChatId.Identifier ?? 0, sendMessage.Text, buttonText));

            var message = new Message();
            return Task.FromResult((TResponse)(object)message);
        }

        if (request is GetFileRequest)
        {
            return Task.FromResult((TResponse)(object)FileToReturn);
        }

        throw new NotSupportedException($"Request type {request.GetType().Name} is not supported by the fake.");
    }

    public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
    {
        destination.Write(FileContent, 0, FileContent.Length);
        destination.Position = 0;
        return Task.CompletedTask;
    }

    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default)
    {
        destination.Write(FileContent, 0, FileContent.Length);
        destination.Position = 0;
        return Task.CompletedTask;
    }

    public bool LocalBotServer => false;

    public long BotId => 123456;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public Telegram.Bot.Exceptions.IExceptionParser ExceptionsParser { get; set; } = null!;

    public event Telegram.Bot.AsyncEventHandler<Telegram.Bot.Args.ApiRequestEventArgs>? OnMakingApiRequest
    {
        add { }
        remove { }
    }

    public event Telegram.Bot.AsyncEventHandler<Telegram.Bot.Args.ApiResponseEventArgs>? OnApiResponseReceived
    {
        add { }
        remove { }
    }
}
