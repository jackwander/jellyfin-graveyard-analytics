// The hidden-viewers checklist (issue #1): it reflects the stored ids whatever their form,
// renders usernames as text, posts what is ticked, and — the one that loses data — never
// saves an empty list just because the user list failed to load.
import { JSDOM } from 'jsdom';
import { htmlPath, read, reporter, sleep } from './support.mjs';

const HTML = htmlPath(process.argv);
const EVIL = '<img src=x onerror="window.pwned=1">';
const A = 'aaaaaaaabbbbccccddddeeeeeeeeeeee';
const B = '11111111222233334444555555555555';
const { check, finish } = reporter();

async function page({ users, stored }) {
  const dom = new JSDOM(read(HTML), { runScripts: 'dangerously' });
  const { window } = dom;
  const posted = [];
  window.ApiClient = {
    getUrl: (u) => '/' + u,
    getJSON: () => Promise.resolve({ Items: [], TotalSize: '0 B' }),
    getPluginConfiguration: () => Promise.resolve({ HiddenUserIds: stored.slice() }),
    updatePluginConfiguration: (_id, settings) => { posted.push(settings); return Promise.resolve({}); },
    getUsers: users,
  };
  window.document.getElementById('GraveyardAnalyticsPage').dispatchEvent(new window.Event('viewshow'));
  await sleep(20);
  return { window, doc: window.document, posted };
}

// Stored dashed and uppercase: the box still has to come up ticked.
let p = await page({
  users: () => Promise.resolve([{ Id: A, Name: 'Kid' }, { Id: B, Name: EVIL }]),
  stored: ['AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'],
});
let boxes = [...p.doc.querySelectorAll('#hiddenUsers input')];
check('one box per user', boxes.length === 2, boxes.length);
check('stored id ticks its box regardless of dashes and case',
  boxes[0].checked && !boxes[1].checked, boxes.map((b) => b.checked).join());
check('username rendered as text, not parsed',
  !p.doc.querySelector('#hiddenUsers img') && p.doc.getElementById('hiddenUsers').textContent.includes(EVIL));

boxes[1].checked = true;
p.doc.getElementById('saveConfig').click();
await sleep(20);
check('save posts every ticked id',
  p.posted.length === 1 && JSON.stringify(p.posted[0].HiddenUserIds) === JSON.stringify([A, B]),
  JSON.stringify(p.posted));

// The user list failed to load: no boxes, and saving must leave the stored list alone.
p = await page({ users: () => Promise.reject(new Error('down')), stored: [A] });
check('no boxes when the user list fails', !p.doc.querySelector('#hiddenUsers input'));
check('the failed load is reported', !p.doc.getElementById('saveFailure').hidden);
p.doc.getElementById('saveConfig').click();
await sleep(20);
check('a save after a failed load keeps the stored hidden list',
  p.posted.length === 1 && JSON.stringify(p.posted[0].HiddenUserIds) === JSON.stringify([A]),
  JSON.stringify(p.posted));

finish();
