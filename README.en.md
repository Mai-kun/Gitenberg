# Gitenberg

[Русский](README.md) | **English**

## Table of Contents

- [Features](#features)
  - [Notes](#notes)
  - [Storage and Sync](#storage-and-sync)
  - [Search](#search)
  - [Web access](#web-access)
- [Tech Stack](#tech-stack)
- [Project Structure](#project-structure)
- [Quick Start (Development)](#quick-start-development)
- [Configuration](#configuration)
  - [`ConnectionStrings` section](#connectionstrings-section)
  - [`WebAuth` section](#webauth-section)
  - [`Search` section](#search-section)
  - [Docker (.env)](#docker-env)
- [Docker Deployment](#docker-deployment)
- [GitHub Setup](#github-setup)
- [API](#api)
- [Tests](#tests)
- [Architecture Notes](#architecture-notes)
- [Security](#security)
- [License](#license)

**Gitenberg** is Markdown notes that live directly in your GitHub repository, wrapped in a convenient web editor.

The project consists of two parts:

- **Web frontend** (vanilla JS SPA) — note explorer, Markdown editor with preview, search and settings;
- **ASP.NET Core backend** (.NET 10) — REST API, session authentication, GitHub Contents API integration, and background sync/indexing services.

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
- 🔄 **Manual sync** — a header button with an indicator of failed operations;
- 📦 **Storage export** — download a ZIP archive of the whole repository (all notes and attachments) from Settings, built on the fly via the GitHub API.

### Search
- 🔍 **Full-text search** across all notes powered by SQLite **FTS5**;
- ⚡ **Incremental indexing** — a background service compares file SHAs and downloads only changed notes (minimal GitHub API usage); the interval is configurable.

### Web access
- 🔑 **Sign-in with a GitHub token** — the app validates the token against the GitHub API, binds a repository and issues an HTTP-only cookie session; no repeated sign-in while the session lives;
- 💻 **Works in any browser** — no Telegram, from Chrome to mobile Safari.

### Observability
- 📊 **Centralized logging to [Seq](https://datalust.co/seq)** — structured application and HTTP request logs (Serilog); the Seq UI is brought up alongside the app via docker compose.

## Tech Stack

| Layer | Technologies |
|---|---|
| Backend | .NET 10, ASP.NET Core Minimal API, Entity Framework Core 10 (SQLite) |
| Integrations | Octokit (GitHub API) |
| Search | SQLite FTS5 |
| Frontend | Vanilla JS (ES modules), EasyMDE, highlight.js |
| Tests | xUnit |
| Infrastructure | Docker, docker compose, ASP.NET Data Protection, Serilog + Seq |

## Project Structure

```
Gitenberg/
├── Gitenberg.Web/              # Main application (backend + frontend)
│   ├── Features/
│   │   ├── Notes/              # Note CRUD: /api/notes
│   │   ├── Auth/               # GitHub-token sign-in, cookie sessions: /api/auth
│   │   ├── Search/             # FTS5 indexer and search: /api/notes/search
│   │   ├── Sync/               # Offline operation queue: /api/sync
│   │   └── Shares/             # Public note share links
│   ├── Services/               # GitHubService, TokenEncryptionService
│   ├── Database/               # AppDbContext (EF Core / SQLite)
│   ├── Models/                 # Domain models
│   ├── DTOs/                   # Request contracts
│   ├── Infrastructure/         # Global exception handler
│   └── wwwroot/                # Web frontend: editor, vault, api, i18n
├── Gitenberg.Tests/            # Unit tests (xUnit)
├── docker-compose.yml          # Production deployment
└── .env.example                # Environment variable template
```

## Quick Start (Development)

Requires the [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/Mai-kun/Gitenberg.git
cd Gitenberg

# 1. If needed, set the configuration in Gitenberg.Web/appsettings.Development.json
#    or via environment variables (see the "Configuration" section).

# 2. Run the application
dotnet run --project Gitenberg.Web
```

After startup:

- the app is served at the application root (e.g. `https://localhost:7119` or `http://localhost:5159`);
- the SQLite database (`gitenberg.db`) is created automatically on first launch, along with the FTS table, the sync queue table and the sessions table.
- open the app in a browser, sign in with a GitHub token and repository — and work with your notes.

## Configuration

All settings are read from the standard ASP.NET Core configuration (`appsettings*.json` + environment variables like `WebAuth__SessionLifetimeDays`).

### `ConnectionStrings` section

| Setting | Default | Description |
|---|---|---|
| `DefaultConnection` | `Data Source=gitenberg.db` | SQLite connection string. |

### `WebAuth` section

| Setting | Default | Description |
|---|---|---|
| `CookieName` | `gitenberg_session` | Session cookie name. |
| `SessionLifetimeDays` | `30` | Session lifetime; an actively used session is extended automatically. |

### `Search` section

| Setting | Default | Description |
|---|---|---|
| `IndexingIntervalMinutes` | `60` | Background re-indexing period (in minutes) for all users' notes. |

### `Serilog` section (logging)

Logs are written to the console and to Seq (`Serilog.Sinks.Seq` sink). The Seq server address is set in `appsettings.json` (`Serilog:WriteTo:Seq:Args:serverUrl`) or via the `Serilog__WriteTo__Seq__Args__serverUrl` environment variable (defaults to `http://seq:5341` in compose). If Seq is unreachable, the app keeps running and logs to the console.

### Docker (.env)

For `docker compose`, copy `.env.example` to `.env` next to `docker-compose.yml`:

```env
# Public base URL for share links (optional; otherwise taken from the request)
# SHARING_PUBLIC_BASE_URL=https://your-domain.example

# Seq (optional)
SEQ_URL=http://seq:5341          # Seq address inside the compose network
SEQ_ADMIN_PASSWORD=              # UI password (min 8 chars); leave empty for anonymous access
SEQ_NO_AUTHENTICATION=true       # disable Seq authentication; set to false if SEQ_ADMIN_PASSWORD is provided
```

## Docker Deployment

```bash
cp .env.example .env    # fill in real values
docker compose up -d --build
```

Compose starts two services:

- **gitenberg-web** — the application, available on port `8080`;
- **seq** — log server (Serilog sink), UI at `http://localhost:8081`, log ingestion on port `5341`.

Persistent volumes:

- `./data` — SQLite database (`/app/data/gitenberg.db`);
- `./keys` — Data Protection keys (`/app/keys`);
- `./seq-data` — Seq data (`/data`).

> 💡 For production, put the container behind a TLS-terminating reverse proxy (nginx, Caddy, Traefik): the session cookie is marked `Secure` outside Development.

## GitHub Setup

You need a personal access token with access to the notes repository:

- **Fine-grained token** (recommended):
    1. Settings -> Developer Settings -> Personal access tokens -> Fine-grained token -> Generate new token
    2. Fill in the "Token name"
    3. Under Repository access, choose "Only select repositories"
    4. Permissions -> Add permissions -> select "Contents" — Read and write
    5. Click "Generate token"

- **Classic token**: Select scopes -> `repo`.

The token is entered once on the sign-in page, stored encrypted (Data Protection, AES) in SQLite, and never returned to the client in plain text.

## API

All endpoints except `/api/auth/*` and public share links require the cookie session obtained at sign-in (`POST /api/auth/login` with a GitHub token).

| Group | Methods | Description |
|---|---|---|
| `/api/auth` | `POST /login`, `POST /logout`, `GET /session` | GitHub-token sign-in, sign-out, session check. |
| `/api/notes` | `GET`, `POST`, `DELETE` | Note list/tree, create and update (upsert), delete. |
| `/api/notes/content` | `GET` | Note content by path. |
| `/api/notes/move` | `POST` | Move a note (rename / change folder). |
| `/api/notes/search` | `GET` | Full-text search (FTS5). |
| `/api/sync` | `POST`, `GET` | Force-flush the pending operation queue, sync status. |
| `/api/export/archive` | `GET` | ZIP archive of the entire notes repository (storage export). |

## Tests

```bash
dotnet test
```

Tests cover CRUD operation handling, the Notes/Search/Auth (sessions, sign-in) features, token encryption, and the global exception handler.

## Architecture Notes

- **Local-first**: a write always lands in the local queue first (`PendingNoteOps` in SQLite) and is overlaid onto read results — the UI is never blocked by the network while GitHub is updated in the background.
- **No orchestrator**: a single ASP.NET Core process serves the API and the web app static files — deployment stays as simple as possible.
- **Encryption at rest**: GitHub tokens are encrypted with ASP.NET Data Protection backed by file-stored keys; without the `keys` directory the database is useless to an attacker.
- **Frugal GitHub API usage**: the indexer downloads only files whose SHA changed; listings are cached.

## Security

- Sign-in succeeds only after the GitHub token is validated by the GitHub API itself; the internal user key is the GitHub account's numeric ID.
- Sessions are stored in the database only as a SHA-256 hash of the token; the cookie is HTTP-only and `Secure` outside Development.
- GitHub tokens are encrypted before being written to the database.

## License

[MIT](LICENSE)
