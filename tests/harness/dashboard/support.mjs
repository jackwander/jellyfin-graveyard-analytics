// Shared plumbing for the three dashboard harnesses.
//
// Phase 7 rewrote dashboard.html: one table instead of three, a per-tab column descriptor,
// module-scoped state behind a single `window.GraveyardDashboard` seam, and the `hidden`
// attribute instead of inline `style.display`. These harnesses still accept a path argument
// so they can be pointed at an older revision — that is how the checks were shown to be
// non-vacuous — so the differences are absorbed here rather than duplicated three times.
//
// `modern` tells a test which file it is driving, for the few checks that only exist on one
// side of the rewrite.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));

export function htmlPath(argv) {
  return argv[2]
    ? resolve(process.cwd(), argv[2])
    : resolve(HERE, '../../../JellyfinGraveyardAnalytics/WebUI/dashboard.html');
}

export function read(path) {
  return readFileSync(path, 'utf8');
}

// The pre-Phase-7 file wrote the total card with `innerText`, which jsdom does not implement —
// so the assignment landed on an expando and the old assertions were reading back their own
// input rather than the DOM. Prefer the expando when it exists so those revisions still read,
// and fall back to textContent, which is what the current file actually sets.
export const text = (el) => (el.innerText !== undefined ? el.innerText : el.textContent);

export const hidden = (el) => el.hidden === true || el.style.display === 'none';

export function adapt(window) {
  const doc = window.document;
  const seam = window.GraveyardDashboard;
  let tab = 'morgue';

  if (seam) {
    return {
      modern: true,
      supports: { unifiedVisitors: true, coverageBanner: true, totals: true },
      setTab(name) { tab = name; seam.setTab(name); },
      switchTab(name) { tab = name; return seam.switchTab(name); },
      renderMediaTable: (items, context) => seam.renderMediaTable(items, context),
      renderVisitorTable: (data) => seam.renderVisitorTable(data),
      renderCoverageBanner: (data) => seam.renderCoverageBanner(data),
      renderTotals: (data) => seam.renderTotals(data),
      mediaBody: () => doc.getElementById('tableBody'),
      visitorBody: () => doc.getElementById('tableBody'),
    };
  }

  // Pre-Phase-7: six functions on `window`, a mutable `window.currentTab`, three tables.
  //
  // Older still than that, the page had *two* visitor tables and sniffed the payload to pick
  // between them (finding 13, fixed in Phase 2 item 7), and no coverage banner at all. So a run
  // against a pre-Phase-2 revision can only exercise what that revision had; `supports` says
  // which sections to skip rather than crashing on a null lookup, which is what both this
  // harness and its predecessor did against 71a01f7.
  return {
    modern: false,
    supports: {
      unifiedVisitors: !!doc.getElementById('visitorTableBody'),
      coverageBanner: typeof window.renderCoverageBanner === 'function',
      totals: typeof window.renderTotals === 'function',
    },
    setTab(name) { tab = name; window.currentTab = name; },
    switchTab(name) { tab = name; return window.switchTab(name); },
    renderMediaTable: (items, context) => window.renderMediaTable(items, context),
    renderVisitorTable: (data) => window.renderVisitorTable(data),
    renderCoverageBanner: (data) => window.renderCoverageBanner(data),
    renderTotals: (data) => window.renderTotals(data),
    mediaBody: () => doc.getElementById(tab === 'morgue' ? 'morgueTableBody' : 'leastWatchedTableBody'),
    visitorBody: () => doc.getElementById('visitorTableBody'),
  };
}

// Header labels for whichever table the given tbody belongs to. Located by label rather than
// by index everywhere it is used, so moving a column fails loudly instead of quietly passing
// on whatever cell now sits in that position.
export function headers(tbody) {
  return [...tbody.closest('table').querySelectorAll('thead th')].map((th) => th.textContent.trim());
}

export function reporter() {
  const results = [];
  return {
    check: (name, ok, detail) => results.push({ name, ok, detail }),
    // Only ever reached on an old revision that lacks the feature under test. A skip is
    // printed rather than silently dropped, so a run cannot look complete when it is not.
    skip: (name, why) => results.push({ name, skipped: why }),
    finish(suffix = '') {
      let failed = 0;
      let skipped = 0;
      for (const r of results) {
        if (r.skipped) { skipped++; console.log(`SKIP  ${r.name}   (${r.skipped})`); continue; }
        if (!r.ok) failed++;
        console.log(`${r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.ok ? '' : '   <-- ' + r.detail}`);
      }
      const ran = results.length - skipped;
      console.log(`\n${ran - failed}/${ran} passed${skipped ? `, ${skipped} skipped` : ''}${suffix}`);
      process.exit(failed ? 1 : 0);
    },
  };
}

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
