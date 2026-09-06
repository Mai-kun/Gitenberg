# Gitenberg

[Русский](README.md) | **English**

## Table of Contents

- [Features](#features)
  - [Notes](#notes)
  - [Storage and Sync](#storage-and-sync)
  - [Search](#search)
  - [Telegram](#telegram)
- [Tech Stack](#tech-stack)
- [Project Structure](#project-structure)
- [Quick Start (Development)](#quick-start-development)
- [Configuration](#configuration)
  - [`ConnectionStrings` section](#connectionstrings-section)
  - [`TelegramBot` section](#telegrambot-section)
  - [`Search` section](#search-section)
  - [Docker (.env)](#docker-env)
- [Docker Deployment](#docker-deployment)
- [Telegram Bot Setup](#telegram-bot-setup)
- [GitHub Setup](#github-setup)
- [API](#api)
- [Tests](#tests)
- [Architecture Notes](#architecture-notes)
- [Security](#security)
- [License](#license)

**Gitenberg** is Markdown notes that live directly in your GitHub repository, wrapped in a convenient editor inside Telegram.

The project consists of two parts:

- **Telegram Mini App** (vanilla JS frontend) — note explorer, Markdown editor with preview, search and settings;
- **ASP.NET Core backend** (.NET 10) — REST API, GitHub Contents API integration, Telegram bot, and background sync/indexing services.

Notes are stored as plain `.md` files in your repository: no proprietary formats, no vendor lock-in. You can always read and edit them right on GitHub, in your IDE, or with any other tool — Gitenberg simply adds a convenient mobile interface and offline sync on top.

## Features

### Notes
- 📝 **Markdown editor** based on [EasyMDE](https://github.com/Ionaru/EasyMDE) with live preview and code highlighting ([highlight.js](https://highlightjs.org/));
- 📁 **Folders** — hierarchical note organization: create, move, and delete both files and folders;
- 🏷️ **Callout blocks** (nested, GitHub-compatible `> [!NOTE]` syntax) — rendered in the preview;
- ℹ️ **File properties** — view the path, size, latest commit, and SHA of a note;
- 🌐 **Bilingual interface** — Russian and English, switchable on the fly.

### Storage and Sync
- 🔐 **Your GitHub repository** — notes are committed via the GitHub Contents API ([Octokit](https://github.com/octokit/octokit.net)) on your behalf, with meaningful commit messages;
- 📴 **Offline-first sync** — every change (create, edit, delete, move) is first written to a local SQLite queue and then flushed to GitHub in the background. Listings and note content render pending operations as an overlay, so the UI is always consistent with the local state;
- 🔄 **Manual sync** — a header button with an indicator of failed operations.

### Search
- 🔍 **Full-text search** across all notes powered by SQLite **FTS5**;
- ⚡ **Incremental indexing** — a background service compares file SHAs and downloads only changed notes (minimal GitHub API usage); the interval is configurable.

### Telegram
- 🤖 **Telegram bot** with a `/start` command and a Mini App launch button;
- ✅ **Authentication via `initData`** — API requests are validated against the Telegram signature (HMAC); each user's GitHub token is encrypted with ASP.NET Data Protection.

## Tech Stack

| Layer | Technologies |
|---|---|
| Backend | .NET 10, ASP.NET Core Minimal API, Entity Framework Core 10 (SQLite) |
| Integrations | Octokit (GitHub API), Telegram.Bot 22 |
| Search | SQLite FTS5 |
| Frontend | Vanilla JS (ES modules), EasyMDE, highlight.js, Telegram WebApp SDK |
| Tests | xUnit |
| Infrastructure | Docker, docker compose, ASP.NET Data Protection |

## Project Structure

```
Gitenberg/
├── Gitenberg.Web/              # Main application (backend + frontend)
│   ├── Features/
│   │   ├── Notes/              # Note CRUD: /api/notes
│   │   ├── Registration/       # GitHub repository binding: /api/register
│   │   ├── Search/             # FTS5 indexer and search: /api/notes/search
│   │   ├── Sync/               # Offline operation queue: /api/sync
│   │   └── TelegramBot/        # Bot, webhook, Telegram authentication
│   ├── Services/               # GitHubService, TokenEncryptionService
│   ├── Database/               # AppDbContext (EF Core / SQLite)
│   ├── Models/                 # Domain models
│   ├── DTOs/                   # Request contracts
│   ├── Infrastructure/         # Global exception handler
│   └── wwwroot/                # Mini App: editor, vault, api, i18n
├── Gitenberg.Tests/            # Unit tests (xUnit)
├── docker-compose.yml          # Production deployment
└── .env.example                # Environment variable template
```

## Quick Start (Development)

Requires the [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/Mai-kun/Gitenberg.git
cd Gitenberg

# 1. Configure the bot settings in Gitenberg.Web/appsettings.Development.json
#    or pass environment variables (see the "Configuration" section).

# 2. Run the application
dotnet run --project Gitenberg.Web
```

After startup:

- the Mini App is served at the application root (e.g. `https://localhost:7119` or `http://localhost:5159`);
- the SQLite database (`gitenberg.db`) is created automatically on first launch, along with the FTS table and the sync queue table.

For local webhook development, a tunnel ([localtunnel](https://localtunnel.me), ngrok) is handy — put the resulting HTTPS URL into `TelegramBot:HostAddress`.

## Configuration

All settings are read from the standard ASP.NET Core configuration (`appsettings*.json` + environment variables like `TelegramBot__BotToken`).

### `ConnectionStrings` section

| Setting | Default | Description |
|---|---|---|
| `DefaultConnection` | `Data Source=gitenberg.db` | SQLite connection string. |

### `TelegramBot` section

| Setting | Description |
|---|---|
| `BotToken` | Bot token from [@BotFather](https://t.me/BotFather). If empty, the bot and webhook are not registered (the app works in API/frontend-only mode). |
| `HostAddress` | Public HTTPS address of the deployment; used in the bot's "Open notes" button and for webhook registration. |
| `SecretToken` | Webhook secret token (`secret_token` in `setWebhook`) used to verify that requests come from Telegram. |

### `Search` section

| Setting | Default | Description |
|---|---|---|
| `IndexingIntervalMinutes` | `60` | Background re-indexing period (in minutes) for all users' notes. |

### Docker (.env)

For `docker compose`, copy `.env.example` to `.env` next to `docker-compose.yml`:

```env
TELEGRAM_BOT_TOKEN=YOUR_BOT_TOKEN
TELEGRAM_HOST_ADDRESS=https://your-domain.example
TELEGRAM_SECRET_TOKEN=your_secret_token_here
```

## Docker Deployment

```bash
cp .env.example .env    # fill in real values
docker compose up -d --build
```

The application is available on port `8080`. The compose file mounts two persistent volumes:

- `./data` — SQLite database (`/app/data/gitenberg.db`);
- `./keys` — Data Protection keys (`/app/keys`).

> ⚠️ Telegram Mini App requires **HTTPS**. Put the container behind a TLS-terminating reverse proxy (nginx, Caddy, Traefik) and specify that address in `TELEGRAM_HOST_ADDRESS`.

## Telegram Bot Setup

1. Create a bot with [@BotFather](https://t.me/BotFather) and get the token → `TelegramBot:BotToken`.
2. Deploy the application at an HTTPS address → `TelegramBot:HostAddress`.
3. Attach the Mini App via BotFather: `/setmenubutton` → pick your bot → enter `HostAddress`.
4. On startup, the application registers the webhook (`setWebhook`) automatically with a `secret_token`.
5. The user sends `/start` to the bot, taps the "Open notes" button, connects their GitHub repository — and starts working with notes.

## GitHub Setup

You need a personal access token with access to the notes repository:

- **Fine-grained token** (recommended):
    1. Settings -> Developer Settings -> Personal access tokens -> Fine-grained token -> Generate new token
    2. Fill in the "Token name"
    3. Under Repository access, choose "Only select repositories"
    4. Permissions -> Add permissions -> select "Contents" — Read and write
    5. Click "Generate token"

- **Classic token**: Select scopes -> `repo`.

The token is entered once during registration in the Mini App, stored encrypted (Data Protection, AES) in SQLite, and never returned to the client in plain text.

## API

All endpoints require Telegram authentication (`initData` in the header/request parameters, except for the bot webhook).

| Group | Methods | Description |
|---|---|---|
| `/api/register` | `POST` | Bind a GitHub repository to a Telegram user. |
| `/api/notes` | `GET`, `POST`, `DELETE` | Note list/tree, create and update (upsert), delete. |
| `/api/notes/content` | `GET` | Note content by path. |
| `/api/notes/move` | `POST` | Move a note (rename / change folder). |
| `/api/notes/search` | `GET` | Full-text search (FTS5). |
| `/api/sync` | `POST`, `GET` | Force-flush the pending operation queue, sync status. |
| `/api/bot/*` | `POST` | Telegram webhook (protected by `secret_token`). |

## Tests

```bash
dotnet test
```

Tests cover CRUD operation handling, the Notes/Registration/Search/TelegramBot features, token encryption, and the global exception handler.

## Architecture Notes

- **Local-first**: a write always lands in the local queue first (`PendingNoteOps` in SQLite) and is overlaid onto read results — the UI is never blocked by the network while GitHub is updated in the background.
- **No orchestrator**: a single ASP.NET Core process serves the API, the Mini App static files, and the bot — deployment stays as simple as possible.
- **Encryption at rest**: GitHub tokens are encrypted with ASP.NET Data Protection backed by file-stored keys; without the `keys` directory the database is useless to an attacker.
- **Frugal GitHub API usage**: the indexer downloads only files whose SHA changed; listings are cached.

## Security

- Telegram `initData` validation via HMAC-SHA256 with the bot token — user identity cannot be forged from outside.
- The webhook `secret_token` rejects requests that do not originate from Telegram.
- GitHub tokens are encrypted before being written to the database.

## License

[MIT](LICENSE)
