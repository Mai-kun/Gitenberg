# Gitenberg

**English** | [Русский](README.en.md)

## Содержание

- [Возможности](#возможности)
  - [Заметки](#заметки)
  - [Хранение и синхронизация](#хранение-и-синхронизация)
  - [Поиск](#поиск)
  - [Telegram](#telegram)
- [Технологии](#технологии)
- [Структура проекта](#структура-проекта)
- [Быстрый старт (разработка)](#быстрый-старт-разработка)
- [Конфигурация](#конфигурация)
  - [Секция `ConnectionStrings`](#секция-connectionstrings)
  - [Секция `TelegramBot`](#секция-telegrambot)
  - [Секция `Search`](#секция-search)
  - [Docker (.env)](#docker-env)
- [Развёртывание через Docker](#развёртывание-через-docker)
- [Настройка Telegram-бота](#настройка-telegram-бота)
- [Настройка GitHub](#настройка-github)
- [API](#api)
- [Тесты](#тесты)
- [Архитектурные заметки](#архитектурные-заметки)
- [Безопасность](#безопасность)
- [Лицензия](#лицензия)

**Gitenberg** — это заметки в формате Markdown, которые живут прямо в вашем репозитории на GitHub, в обёртке удобного редактора внутри Telegram.

Проект состоит из двух частей:

- **Telegram Mini App** (vanilla JS-фронтенд) — проводник по заметкам, Markdown-редактор с превью, поиск и настройки;
- **ASP.NET Core backend** (.NET 10) — REST API, интеграция с GitHub Contents API, Telegram-бот и фоновые службы синхронизации и индексации.

Заметки хранятся как обычные `.md`-файлы в вашем репозитории: никаких проприетарных форматов и привязки к сервису. Вы всегда можете читать и править их прямо на GitHub, в IDE или любым другим инструментом, а Gitenberg лишь добавляет к ним удобный мобильный интерфейс и оффлайн-синхронизацию.

## Возможности

### Заметки
- 📝 **Markdown-редактор** на базе [EasyMDE](https://github.com/Ionaru/EasyMDE) с live-превью и подсветкой кода ([highlight.js](https://highlightjs.org/));
- 📁 **Папки** — иерархическая организация заметок, создание, перемещение и удаление как файлов, так и папок;
- 🏷️ **Callout-блоки** (вложенные, GitHub-совместимый синтаксис `> [!NOTE]`) — рендерятся в превью;
- ℹ️ **Свойства файла** — просмотр пути, размера, последнего коммита и SHA заметки;
- 🌐 **Двуязычный интерфейс** — русский и английский, переключается на лету.

### Хранение и синхронизация
- 🔐 **Ваш GitHub-репозиторий** — заметки коммитятся через GitHub Contents API ([Octokit](https://github.com/octokit/octokit.net)) от вашего имени, с осмысленными commit-сообщениями;
- 📴 **Offline-first синхронизация** — все изменения (создание, правка, удаление, перемещение) сначала пишутся в локальную очередь в SQLite, а затем фоново выгружаются на GitHub. Списки и содержимое отображают отложенные операции как «оверлей», поэтому UI всегда согласован с локальным состоянием;
- 🔄 **Ручная синхронизация** — кнопка с индикатором неуспешных операций в шапке приложения;
- 📦 **Экспорт хранилища** — в настройках можно скачать ZIP-архив всего репозитория (все заметки и вложения), формируется на лету через GitHub API.

### Поиск
- 🔍 **Полнотекстовый поиск** по всем заметкам на SQLite **FTS5**;
- ⚡ **Инкрементальная индексация** — фоновая служба сравнивает SHA файлов и скачивает только изменившиеся заметки (минимум обращений к GitHub API), интервал настраивается.

### Telegram
- 🤖 **Telegram-бот** с командой `/start` и кнопкой запуска Mini App;
- ✅ **Аутентификация через `initData`** — запросы API валидируются по подписи Telegram (HMAC), GitHub-токен пользователя шифруется через ASP.NET Data Protection.

### Наблюдаемость
- 📊 **Централизованное логирование в [Seq](https://datalust.co/seq)** — структурированные логи приложения и HTTP-запросов (Serilog), UI Seq поднимается вместе с приложением через docker compose.

## Технологии

| Слой | Технологии |
|---|---|
| Backend | .NET 10, ASP.NET Core Minimal API, Entity Framework Core 10 (SQLite) |
| Интеграции | Octokit (GitHub API), Telegram.Bot 22 |
| Поиск | SQLite FTS5 |
| Фронтенд | Vanilla JS (ES-модули), EasyMDE, highlight.js, Telegram WebApp SDK |
| Тесты | xUnit |
| Инфраструктура | Docker, docker compose, ASP.NET Data Protection, Serilog + Seq |

## Структура проекта

```
Gitenberg/
├── Gitenberg.Web/              # Основное приложение (backend + frontend)
│   ├── Features/
│   │   ├── Notes/              # CRUD заметок: /api/notes
│   │   ├── Registration/       # Привязка GitHub-репозитория: /api/register
│   │   ├── Search/             # FTS5-индексатор и поиск: /api/notes/search
│   │   ├── Sync/               # Оффлайн-очередь операций: /api/sync
│   │   └── TelegramBot/        # Бот, вебхук, аутентификация Telegram
│   ├── Services/               # GitHubService, TokenEncryptionService
│   ├── Database/               # AppDbContext (EF Core / SQLite)
│   ├── Models/                 # Доменные модели
│   ├── DTOs/                   # Контракты запросов
│   ├── Infrastructure/         # Глобальный обработчик исключений
│   └── wwwroot/                # Mini App: editor, vault, api, i18n
├── Gitenberg.Tests/            # Модульные тесты (xUnit)
├── docker-compose.yml          # Продакшен-развёртывание
└── .env.example                # Шаблон переменных окружения
```

## Быстрый старт (разработка)

Требуется [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/Mai-kun/Gitenberg.git
cd Gitenberg

# 1. Настройте конфигурацию бота в Gitenberg.Web/appsettings.Development.json
#    или передайте переменные окружения (см. раздел «Конфигурация»).

# 2. Запустите приложение
dotnet run --project Gitenberg.Web
```

После запуска:

- Mini App доступен на корне приложения (например, `https://localhost:7119` или `http://localhost:5159`);
- SQLite-база (`gitenberg.db`) создаётся автоматически при первом старте, вместе с FTS-таблицей и таблицей очереди синхронизации.

Для локальной разработки вебхука удобно использовать туннель (например, [localtunnel](https://localtunnel.me) или ngrok) — полученный HTTPS-адрес укажите в `TelegramBot:HostAddress`.

## Конфигурация

Все параметры читаются из стандартной конфигурации ASP.NET Core (`appsettings*.json` + переменные окружения вида `TelegramBot__BotToken`).

### Секция `ConnectionStrings`

| Параметр | По умолчанию | Описание |
|---|---|---|
| `DefaultConnection` | `Data Source=gitenberg.db` | Строка подключения к SQLite. |

### Секция `TelegramBot`

| Параметр | Описание |
|---|---|
| `BotToken` | Токен бота от [@BotFather](https://t.me/BotFather). Если пуст — бот и вебхук не регистрируются (приложение работает в режиме API/фронтенда). |
| `HostAddress` | Публичный HTTPS-адрес развёртывания; используется в кнопке «Открыть заметки» у бота и для регистрации вебхука. |
| `SecretToken` | Секретный токен вебхука (`secret_token` в `setWebhook`) для проверки, что запросы приходят от Telegram. |

### Секция `Search`

| Параметр | По умолчанию | Описание |
|---|---|---|
| `IndexingIntervalMinutes` | `60` | Период фоновой переиндексации заметок всех пользователей в минутах. |

### Секция `Serilog` (логирование)

Логи пишутся в консоль и в Seq (sink `Serilog.Sinks.Seq`). Адрес Seq-сервера задаётся в `appsettings.json` (`Serilog:WriteTo:Seq:Args:serverUrl`) или переменной окружения `Serilog__WriteTo__Seq__Args__serverUrl` (в compose по умолчанию `http://seq:5341`). Если Seq недоступен, приложение продолжает работать, логи пишутся в консоль.

### Docker (.env)

Для `docker compose` скопируйте `.env.example` в `.env` рядом с `docker-compose.yml`:

```env
TELEGRAM_BOT_TOKEN=YOUR_BOT_TOKEN
TELEGRAM_HOST_ADDRESS=https://your-domain.example
TELEGRAM_SECRET_TOKEN=your_secret_token_here

# Seq (опционально)
SEQ_URL=http://seq:5341          # адрес Seq внутри compose-сети
SEQ_ADMIN_PASSWORD=              # пароль (мин. 8 символов) для UI Seq; пусто — доступ без аутентификации
SEQ_NO_AUTHENTICATION=true       # отключить аутентификацию Seq; выставьте false, если задан SEQ_ADMIN_PASSWORD
```

## Развёртывание через Docker

```bash
cp .env.example .env    # заполните реальными значениями
docker compose up -d --build
```

compose поднимает два сервиса:

- **gitenberg-web** — приложение, доступно на порту `8080`;
- **seq** — сервер логов (Serilog-приёмник), UI доступен на `http://localhost:8081`, приём логов — порт `5341`.

Персистентные тома:

- `./data` — база SQLite (`/app/data/gitenberg.db`);
- `./keys` — ключи Data Protection (`/app/keys`);
- `./seq-data` — данные Seq (`/data`).

> ⚠️ Telegram Mini App требует **HTTPS**. Поместите контейнер за обратным прокси с TLS (nginx, Caddy, Traefik) и укажите этот адрес в `TELEGRAM_HOST_ADDRESS`.

## Настройка Telegram-бота

1. Создайте бота у [@BotFather](https://t.me/BotFather) и получите токен → `TelegramBot:BotToken`.
2. Опубликуйте приложение по HTTPS-адресу → `TelegramBot:HostAddress`.
3. Через BotFather привяжите Mini App: `/setmenubutton` → выберите бота → укажите `HostAddress`.
4. При старте приложение автоматически зарегистрирует вебхук (`setWebhook`) с `secret_token`.
5. Пользователь отправляет боту `/start`, нажимает кнопку «Открыть заметки», подключает свой GitHub-репозиторий — и работает с заметками.

## Настройка GitHub

Для работы нужен персональный access token с доступом к репозиторию заметок:

- **Fine-grained token** (рекомендуется): 
    1. Settings -> Development Settings -> Personal access tokens -> Fine-grained token -> Generate new token
    2. Заполнить «Token name» 
    3. В Repository access указать «Only select repositories»
    4. Permissions -> Add permissions -> выбрать «Contents» — Read and write
    5. Выбрать «Generate token»

- **Classic token**: Select scopes -> `repo`.

Токен вводится один раз при регистрации в Mini App, хранится зашифрованным (Data Protection, AES) в SQLite и никогда не возвращается клиенту в открытом виде.

## API

Все эндпоинты требуют аутентификации Telegram (`initData` в заголовке/параметрах запроса, кроме вебхука бота).

| Группа | Методы | Описание |
|---|---|---|
| `/api/register` | `POST` | Привязка GitHub-репозитория к пользователю Telegram. |
| `/api/notes` | `GET`, `POST`, `DELETE` | Список/дерево заметок, создание и обновление (upsert), удаление. |
| `/api/notes/content` | `GET` | Содержимое заметки по пути. |
| `/api/notes/move` | `POST` | Перемещение заметки (переименование/смена папки). |
| `/api/notes/search` | `GET` | Полнотекстовый поиск (FTS5). |
| `/api/sync` | `POST`, `GET` | Принудительный сброс очереди отложенных операций, статус синхронизации. |
| `/api/export/archive` | `GET` | ZIP-архив всего репозитория заметок (экспорт хранилища). |
| `/api/bot/*` | `POST` | Вебхук Telegram (защищён `secret_token`). |

## Тесты

```bash
dotnet test
```

Тесты покрывают обработку операций CRUD, фичи Notes/Registration/Search/TelegramBot, шифрование токенов и глобальный обработчик исключений.

## Архитектурные заметки

- **Local-first**: запись всегда сначала попадает в локальную очередь (`PendingNoteOps` в SQLite) и оверлеем применяется к результатам чтения — UI не блокируется сетью, а GitHub обновляется в фоне.
- **Без контейнера-оркестратора**: один процесс ASP.NET Core обслуживает и API, и статику Mini App, и бота — деплой максимально простой.
- **Шифрование на диске**: GitHub-токены шифруются ASP.NET Data Protection с файловым хранилищем ключей; без каталога `keys` база бесполезна для атакующего.
- **Экономный расход GitHub API**: индексатор скачивает только файлы с изменившимся SHA; листинг кэшируется.

## Безопасность

- Валидация `initData` Telegram по HMAC-SHA256 с токеном бота — подделать личность пользователя извне нельзя.
- `secret_token` вебхука отсекает запросы, не исходящие от Telegram.
- GitHub-токены шифруются перед записью в БД.

## Лицензия

[MIT](LICENSE)
