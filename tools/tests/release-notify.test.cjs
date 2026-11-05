const { test } = require('node:test');
const assert = require('node:assert/strict');
const notify = require('../../.github/scripts/release-notify.cjs');
function fixture({ comments = [], state = 'closed', state_reason = null, draft = false, pr = false } = {}) {
  const writes = [];
  return { writes, context: { repo: { owner: 'test', repo: 'test' } }, tag: 'v1.5.6', fixes: { '1.5.6': [137] }, log() {}, github: {
    paginate: async () => comments,
    rest: { repos: { getReleaseByTag: async () => ({ data: { draft, html_url: 'https://example.test/release' } }) }, issues: {
      get: async a => ({ data: { number: a.issue_number, state, state_reason, ...(pr ? { pull_request: {} } : {}) } }),
      listComments() {}, createComment: async a => writes.push(['comment', a]), addLabels: async a => writes.push(['label', a]),
      removeLabel: async () => { const e = new Error(); e.status = 404; throw e; }, update: async a => writes.push(['close', a])
    } }
  } };
}
test('closed issue receives release note without reopening', async () => { const f = fixture(); await notify(f); assert.deepEqual(f.writes.map(w => w[0]), ['comment', 'label']); });
test('open issue is notified, labelled and closed', async () => { const f = fixture({ state: 'open' }); await notify(f); assert.deepEqual(f.writes.map(w => w[0]), ['comment', 'label', 'close']); });
test('retry skips prior bot comment and finishes labels', async () => { const f = fixture({ comments: [{ user: { login: 'github-actions[bot]' }, body: '<!-- selene-release:v1.5.6 -->' }] }); await notify(f); assert.deepEqual(f.writes.map(w => w[0]), ['label']); });
test('untrusted marker does not suppress notification', async () => { const f = fixture({ comments: [{ user: { login: 'reporter' }, body: '<!-- selene-release:v1.5.6 -->' }] }); await notify(f); assert.equal(f.writes[0][0], 'comment'); });
test('dry run performs no writes', async () => { const f = fixture(); await notify({ ...f, dryRun: true }); assert.equal(f.writes.length, 0); });
test('reopened regression stays open', async () => { const f = fixture({ state: 'open', state_reason: 'reopened' }); await notify(f); assert.equal(f.writes.some(w => w[0] === 'close'), false); });
for (const [name, change] of [['draft', { draft: true }], ['PR', { pr: true }]]) test(`rejects ${name} before writes`, async () => { const f = fixture(change); await assert.rejects(notify(f)); assert.equal(f.writes.length, 0); });
test('missing map refuses to guess from labels', async () => { const f = fixture(); await assert.rejects(notify({ ...f, fixes: {} })); assert.equal(f.writes.length, 0); });
