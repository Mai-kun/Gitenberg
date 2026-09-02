// EasyMDE wrapper: lazily creates a single editor instance on the #note-markdown
// textarea and reuses it for every note (re-initializing EasyMDE on the same
// element leaks DOM and codemirror instances).

let instance = null;

function create() {
  const textarea = document.getElementById('note-markdown');

  instance = new EasyMDE({
    element: textarea,
    autosave: false,
    autofocus: false,
    spellChecker: false,
    status: false,
    placeholder: 'Пишите заметку в Markdown…',
    toolbar: [
      'bold', 'italic', 'strikethrough', '|',
      'heading-1', 'heading-2', '|',
      'unordered-list', 'ordered-list', '|',
      'code', 'quote', 'link', '|',
      'preview', '|',
      {
        name: 'guide',
        action: 'https://www.markdownguide.org/basic-syntax/',
        title: 'Справка по Markdown',
        className: 'fa fa-question-circle',
      },
    ],
  });

  return instance;
}

export function get() {
  return instance ?? create();
}

export function getValue() {
  return instance ? instance.value() : '';
}

export function setValue(value) {
  get().value(value ?? '');
  instance.codemirror.refresh();
}

export function clear() {
  if (instance) {
    instance.value('');
    instance.codemirror.refresh();
  }
}
