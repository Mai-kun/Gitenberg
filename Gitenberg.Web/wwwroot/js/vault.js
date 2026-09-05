// Vault-wide index (folders + notes) built from cached server listings.
// Used by the move dialog (folder list), the link picker and dataview queries.

import * as api from './api.js';

const INDEX_TTL_MS = 60_000;

let indexPromise = null;
let indexedAt = 0;

export function invalidateVaultIndex() {
  indexPromise = null;
}

export function getVaultIndex() {
  if (indexPromise && Date.now() - indexedAt < INDEX_TTL_MS) return indexPromise;
  indexedAt = Date.now();
  indexPromise = buildIndex();
  indexPromise.catch(() => {
    indexPromise = null;
  });
  return indexPromise;
}

async function buildIndex() {
  const queue = [''];
  const notes = [];
  const folders = [];
  let budget = 60; // listings per rebuild — notes repos are small; stay bounded

  while (queue.length && budget > 0) {
    budget -= 1;
    const dir = queue.shift();
    let items;
    try {
      items = await api.listNotes(dir || undefined);
    } catch {
      continue;
    }
    if (!Array.isArray(items)) continue;
    for (const item of items) {
      if ((item.type || '').toLowerCase() === 'dir') {
        folders.push(item.path);
        queue.push(item.path);
      } else {
        notes.push({ path: item.path, name: item.name, dir });
      }
    }
  }

  folders.sort();
  return { notes, folders };
}
