// Minimal i18n: RU/EN dictionaries, first-run auto-detection and helpers to
// translate static markup via data-i18n* attributes.

const STORAGE_KEY = 'gitenberg.lang';

const DICT = {
  ru: {
    explorerTitle: 'Заметки',
    rootCrumb: 'Корень',
    searchPlaceholder: 'Поиск по заметкам…',
    searchHint: 'Поисковый индекс обновляется примерно раз в час — новые заметки могут появиться не сразу.',
    loading: 'Загрузка…',
    emptyFolder: 'Папка пуста. Создайте заметку кнопкой «+».',
    emptySearch: 'Ничего не найдено.',
    registerFailed: 'Не удалось подключить GitHub',
    registerHint: 'Подключите репозиторий GitHub, чтобы хранить заметки в формате Markdown.',
    regToken: 'GitHub Token',
    regOwner: 'Владелец репозитория',
    regRepo: 'Название репозитория',
    inboxPathLabel: 'Папка для заметок',
    inboxPathPlaceholder: 'inbox',
    attachmentsPathLabel: 'Папка для фото',
    attachmentsPathPlaceholder: 'inbox/attachments',
    fillAllFields: 'Заполните все поля.',
    confirmDelete: (name) => `Удалить заметку «${name}»?\n\nФайл будет удалён из репозитория GitHub.`,
    confirmDeleteFolder: (name) => `Удалить папку «${name}»?\n\nВсе файлы внутри будут удалены из репозитория GitHub.`,
    confirmDiscard: 'Есть несохранённые изменения. Выйти без сохранения?',
    saved: 'Заметка сохранена',
    moved: 'Заметка перемещена',
    deleted: 'Заметка удалена',
    deletedFolder: 'Папка удалена',
    deleting: 'Удаление…',
    errorPrefix: 'Ошибка',
    registerBtnBusy: 'Подключение…',
    registerBtnIdle: 'Подключить GitHub',
    saveBtnBusy: 'Сохранение…',
    saveBtnIdle: 'Сохранить',
    invalidPath: 'Укажите имя файла (например, notes/idea.md).',
    invalidName: 'Укажите корректное имя.',
    tooLarge: 'Файл слишком большой (> 1 МБ) — содержимое недоступно для редактирования.',
    loadFailed: 'Не удалось загрузить данные',
    telegramIdInvalid: 'Укажите корректный Telegram ID (положительное число).',
    devBarLabel: 'Telegram ID для теста',
    devBarHint: 'Укажите тестовый Telegram ID в панели сверху.',
    folderItemsCount: (n) => `${n} ${plural(n, 'элемент', 'элемента', 'элементов')}`,
    noteNotFound: (name) => `Заметка не найдена: ${name}`,
    infoTitle: 'Свойства',
    infoTypeLabel: 'Тип',
    infoTypeFile: 'Файл',
    infoTypeFolder: 'Папка',
    infoTypeName: 'Имя',
    infoTypePath: 'Путь',
    infoTypeSize: 'Размер',
    infoTypeContains: 'Внутри',
    infoTypeTags: 'Теги',
    openOnGitHub: 'Открыть на GitHub',
    moveRenameAction: 'Переместить / переименовать',
    moveTitle: 'Переместить / переименовать',
    moveFolderLabel: 'Папка',
    moveRootFolder: 'Корень',
    moveNewFolderOption: '➕ Новая папка…',
    moveNewFolderLabel: 'Новая папка',
    moveNewFolderPlaceholder: 'например, notes/archive',
    moveNameLabel: 'Имя',
    moveAction: 'Переместить',
    cancel: 'Отмена',
    close: 'Закрыть',
    linkPickerTitle: 'Вставить ссылку',
    linkPickerSearch: 'Поиск заметки…',
    linkPickerNoNotes: 'Заметки не найдены',
    linkPickerNotes: 'Заметки',
    linkPickerExternal: 'Внешняя ссылка (URL)',
    linkPickerInsertUrl: 'Вставить URL',
    linkInvalid: 'Выберите заметку или укажите URL.',
    createNote: 'Создать заметку',
    back: 'Назад',
    deleteNote: 'Удалить заметку',
    editorFileLabel: 'Файл',
    editorPlaceholder: 'Пишите заметку в Markdown…',
    newNoteTitle: 'Новая заметка',
    previewBanner: 'Режим просмотра — заметка открыта только для чтения.',
    editAction: 'Редактировать',
    searchResultsTitle: 'Результаты поиска',
    clearSearch: 'Очистить поиск',
    kanbanAddCard: 'Добавить',
    kanbanCardTitle: 'Задача',
    kanbanCardText: 'Название',
    kanbanCardDesc: 'Описание (не показывается на доске)',
    kanbanHasDesc: 'У задачи есть описание — нажмите, чтобы открыть',
    saveAction: 'Сохранить',
    saveAction: 'Сохранить',
    deleteAction: 'Удалить',
    settingsTitle: 'Настройки',
    settingsToken: 'Новый GitHub Token',
    settingsTokenHint: 'Оставьте пустым, чтобы сохранить текущий',
    autosaveLabel: 'Автосохранение заметки при выходе (без кнопки «Сохранить»)',
    autosyncLabel: 'Автосинхронизация с GitHub по таймеру',
    autosyncHint: 'Все изменения сначала сохраняются локально и не отправляются на GitHub сразу. При включённой автосинхронизации они отправляются автоматически каждые N минут. Кнопка ↻ в списке заметок отправляет их немедленно, счётчик на ней показывает, сколько изменений ждёт отправки.',
    autosyncIntervalLabel: 'Интервал (минут)',
    syncNow: 'Синхронизировать с GitHub',
    syncPendingHint: 'Локальных изменений, ожидающих синхронизации',
    syncing: 'Синхронизация с GitHub…',
    syncDone: (n) => `Синхронизировано: ${n} ${plural(n, 'изменение', 'изменения', 'изменений')}`,
    syncDonePartial: (applied, remaining) => `Синхронизировано ${applied}, осталось ${remaining} — попробуйте позже`,
    langSwitchTitle: 'Switch language',
    dataviewLoading: 'Dataview: выполняется запрос…',
    dataviewEmpty: 'Пустой результат.',
    dataviewUnsupported: 'Dataview: поддерживаются запросы LIST и TABLE.',
    mdHelpTitle: 'Возможности Markdown',
    mdHelpIntro: 'Ровно то, что умеет предпросмотр в этом приложении:',
    mdHelpUnsupported: 'Не поддерживается: сноски, теги-параметры YAML (кроме kanban), формулы.',
    mdHelpDocs: 'Подробнее в официальной документации:',
    mdHelpDocsMarkdown: 'Markdown Guide',
    mdHelpDocsObsidian: 'Синтаксис Obsidian',
    toolbarGuide: 'Возможности Markdown',
    toolbarLink: 'Вставить ссылку на заметку или URL',
    toolbarWikilink: 'Вставить [[вики-ссылку]]',
    helpRows: [
      ['Заголовки', '# H1 … ###### H6'],
      ['Жирный / курсив', '**жирный**, *курсив*, ***оба***'],
      ['Зачёркнутый', '~~текст~~'],
      ['Выделение', '==текст== — как в Obsidian'],
      ['Списки', '- пункт, 1. пункт, вложенность отступом'],
      ['Чек-боксы', '- [ ] задача, - [x] сделано'],
      ['Цитата', '> текст'],
      ['Код', '`строка` и блоки ``` … ```'],
      ['Ссылки', '[текст](https://…) или пикер через кнопку ссылки'],
      ['Вики-ссылки', '[[заметка]] или [[путь/заметка|текст]] — открывается в редакторе'],
      ['Теги', '#тег — клик ищет заметки с тегом'],
      ['Картинки', '![описание](https://…png)'],
      ['Таблицы', 'GFM-таблицы с | и ---'],
      ['Разделитель', '--- на отдельной строке'],
      ['Inline-поля (Dataview)', 'ключ:: значение'],
      ['Dataview-запросы', '```dataview … ``` — LIST и TABLE с FROM #тег / FROM "папка"'],
      ['Kanban-доска', '```kanban … ``` или frontmatter kanban-plugin: board — колонки ## и карточки - [ ]'],
    ],
  },

  en: {
    explorerTitle: 'Notes',
    rootCrumb: 'Root',
    searchPlaceholder: 'Search notes…',
    searchHint: 'The search index refreshes about once an hour — new notes may not appear right away.',
    loading: 'Loading…',
    emptyFolder: 'Folder is empty. Create a note with the "+" button.',
    emptySearch: 'Nothing found.',
    registerFailed: 'Could not connect GitHub',
    registerHint: 'Connect a GitHub repository to store your Markdown notes.',
    regToken: 'GitHub Token',
    regOwner: 'Repository owner',
    regRepo: 'Repository name',
    inboxPathLabel: 'Notes folder',
    inboxPathPlaceholder: 'inbox',
    attachmentsPathLabel: 'Photos folder',
    attachmentsPathPlaceholder: 'inbox/attachments',
    fillAllFields: 'Fill in all the fields.',
    confirmDelete: (name) => `Delete note "${name}"?\n\nThe file will be removed from the GitHub repository.`,
    confirmDeleteFolder: (name) => `Delete folder "${name}"?\n\nAll files inside will be removed from the GitHub repository.`,
    confirmDiscard: 'You have unsaved changes. Leave without saving?',
    saved: 'Note saved',
    moved: 'Note moved',
    deleted: 'Note deleted',
    deletedFolder: 'Folder deleted',
    deleting: 'Deleting…',
    errorPrefix: 'Error',
    registerBtnBusy: 'Connecting…',
    registerBtnIdle: 'Connect GitHub',
    saveBtnBusy: 'Saving…',
    saveBtnIdle: 'Save',
    invalidPath: 'Enter a file name (e.g. notes/idea.md).',
    invalidName: 'Enter a valid name.',
    tooLarge: 'File is too large (> 1 MB) — its content is not available for editing.',
    loadFailed: 'Failed to load data',
    telegramIdInvalid: 'Enter a valid Telegram ID (positive number).',
    devBarLabel: 'Telegram ID for testing',
    devBarHint: 'Set a test Telegram ID in the bar above.',
    folderItemsCount: (n) => `${n} ${plural(n, 'item', 'items', 'items')}`,
    noteNotFound: (name) => `Note not found: ${name}`,
    infoTitle: 'Properties',
    infoTypeLabel: 'Type',
    infoTypeFile: 'File',
    infoTypeFolder: 'Folder',
    infoTypeName: 'Name',
    infoTypePath: 'Path',
    infoTypeSize: 'Size',
    infoTypeContains: 'Contains',
    infoTypeTags: 'Tags',
    openOnGitHub: 'Open on GitHub',
    moveRenameAction: 'Move / rename',
    moveTitle: 'Move / rename',
    moveFolderLabel: 'Folder',
    moveRootFolder: 'Root',
    moveNewFolderOption: '➕ New folder…',
    moveNewFolderLabel: 'New folder',
    moveNewFolderPlaceholder: 'e.g. notes/archive',
    moveNameLabel: 'Name',
    moveAction: 'Move',
    cancel: 'Cancel',
    close: 'Close',
    linkPickerTitle: 'Insert link',
    linkPickerSearch: 'Search notes…',
    linkPickerNoNotes: 'No notes found',
    linkPickerNotes: 'Notes',
    linkPickerExternal: 'External link (URL)',
    linkPickerInsertUrl: 'Insert URL',
    linkInvalid: 'Pick a note or provide a URL.',
    createNote: 'Create note',
    back: 'Back',
    deleteNote: 'Delete note',
    editorFileLabel: 'File',
    editorPlaceholder: 'Write your note in Markdown…',
    newNoteTitle: 'New note',
    previewBanner: 'Preview mode — the note is read-only.',
    editAction: 'Edit',
    searchResultsTitle: 'Search results',
    clearSearch: 'Clear search',
    kanbanAddCard: 'Add',
    kanbanCardTitle: 'Task',
    kanbanCardText: 'Title',
    kanbanCardDesc: 'Description (not shown on the board)',
    kanbanHasDesc: 'This task has a description — click to open',
    saveAction: 'Save',
    saveAction: 'Save',
    deleteAction: 'Delete',
    settingsTitle: 'Settings',
    settingsToken: 'New GitHub Token',
    settingsTokenHint: 'Leave empty to keep the current one',
    autosaveLabel: 'Autosave the note on exit (without the Save button)',
    autosyncLabel: 'Auto-sync with GitHub on a timer',
    autosyncHint: 'Changes are saved locally first and are not pushed to GitHub immediately. With auto-sync enabled they are pushed automatically every N minutes. The ↻ button in the notes list pushes them right away; its badge shows how many changes are waiting.',
    autosyncIntervalLabel: 'Interval (minutes)',
    syncNow: 'Sync with GitHub',
    syncPendingHint: 'Local changes waiting to be synced',
    syncing: 'Syncing with GitHub…',
    syncDone: (n) => `Synced ${n} ${n === 1 ? 'change' : 'changes'}`,
    syncDonePartial: (applied, remaining) => `Synced ${applied}, ${remaining} left — try again later`,
    langSwitchTitle: 'Switch language',
    dataviewLoading: 'Dataview: running query…',
    dataviewEmpty: 'Empty result.',
    dataviewUnsupported: 'Dataview: only LIST and TABLE queries are supported.',
    mdHelpTitle: 'Markdown capabilities',
    mdHelpIntro: 'Exactly what the preview in this app supports:',
    mdHelpUnsupported: 'Not supported: footnotes, YAML tags (except kanban), formulas.',
    mdHelpDocs: 'Read more in the official documentation:',
    mdHelpDocsMarkdown: 'Markdown Guide',
    mdHelpDocsObsidian: 'Obsidian syntax',
    toolbarGuide: 'Markdown capabilities',
    toolbarLink: 'Insert a note link or URL',
    toolbarWikilink: 'Insert a [[wikilink]]',
    helpRows: [
      ['Headings', '# H1 … ###### H6'],
      ['Bold / italic', '**bold**, *italic*, ***both***'],
      ['Strikethrough', '~~text~~'],
      ['Highlight', '==text== — like in Obsidian'],
      ['Lists', '- item, 1. item, nesting via indent'],
      ['Checkboxes', '- [ ] task, - [x] done'],
      ['Quote', '> text'],
      ['Code', '`inline` and ``` … ``` blocks'],
      ['Links', '[text](https://…) or the link-picker toolbar button'],
      ['Wikilinks', '[[note]] or [[path/note|label]] — opens in the editor'],
      ['Tags', '#tag — click to search notes with the tag'],
      ['Images', '![alt](https://…png)'],
      ['Tables', 'GFM tables with | and ---'],
      ['Divider', '--- on its own line'],
      ['Inline fields (Dataview)', 'key:: value'],
      ['Dataview queries', '```dataview … ``` — LIST and TABLE with FROM #tag / FROM "folder"'],
      ['Kanban board', '```kanban … ``` or frontmatter kanban-plugin: board — ## columns and - [ ] cards'],
    ],
  },
};

function plural(n, one, few, many) {
  const mod100 = Math.abs(n) % 100;
  const mod10 = mod100 % 10;
  if (mod100 >= 11 && mod100 <= 14) return many;
  if (mod10 === 1) return one;
  if (mod10 >= 2 && mod10 <= 4) return few;
  return many;
}

let lang = detectLanguage();

function detectLanguage() {
  const stored = localStorage.getItem(STORAGE_KEY);
  if (stored === 'ru' || stored === 'en') return stored;
  const tgLang = window.Telegram?.WebApp?.initDataUnsafe?.user?.language_code;
  const candidate = typeof tgLang === 'string' && tgLang ? tgLang : navigator.language || '';
  return candidate.toLowerCase().startsWith('ru') ? 'ru' : 'en';
}

export function initI18n() {
  document.documentElement.lang = lang;
  applyStatic();
}

export function getLang() {
  return lang;
}

export function t(key, ...args) {
  const entry = DICT[lang][key] ?? DICT.ru[key] ?? key;
  // Function-valued entries are returned as-is so callers can pass
  // parameters (e.g. STRINGS.folderItemsCount(n) or STRINGS.confirmDelete(name)).
  return entry;
}

export function setLang(next) {
  if (next !== 'ru' && next !== 'en') return;
  lang = next;
  localStorage.setItem(STORAGE_KEY, next);
  document.documentElement.lang = lang;
  applyStatic();
  document.dispatchEvent(new CustomEvent('language-changed', { detail: { lang } }));
}

// Translate static markup: data-i18n (textContent), data-i18n-placeholder,
// data-i18n-title attributes.
export function applyStatic() {
  document.querySelectorAll('[data-i18n]').forEach((el) => {
    el.textContent = t(el.dataset.i18n);
  });
  document.querySelectorAll('[data-i18n-placeholder]').forEach((el) => {
    el.placeholder = t(el.dataset.i18nPlaceholder);
  });
  document.querySelectorAll('[data-i18n-title]').forEach((el) => {
    el.title = t(el.dataset.i18nTitle);
    el.setAttribute('aria-label', t(el.dataset.i18nTitle));
  });
}
