module.exports = async function notify({ github, context, tag, fixes, dryRun = false, log = console.log }) {
  if (!/^v\d+\.\d+\.\d+$/.test(tag || '')) throw new Error('Expected a stable release tag like v1.5.6');
  const version = tag.slice(1);
  const numbers = fixes[version];
  if (!Array.isArray(numbers) || numbers.some(n => !Number.isSafeInteger(n) || n < 1) || new Set(numbers).size !== numbers.length)
    throw new Error(`Missing or invalid issue mapping for ${version}`);
  const repo = context.repo;
  const { data: release } = await github.rest.repos.getReleaseByTag({ ...repo, tag });
  if (release.draft) throw new Error('Cannot notify about a draft release');
  const marker = `<!-- selene-release:${tag} -->`;
  // Validate the whole mapping before posting anything (PRs are also returned by issues.get).
  const issues = [];
  for (const number of numbers) {
    const { data } = await github.rest.issues.get({ ...repo, issue_number: number });
    if (data.pull_request) throw new Error(`#${number} is a pull request, not an issue`);
    issues.push(data);
  }
  for (const issue of issues) {
    const args = { ...repo, issue_number: issue.number };
    const comments = await github.paginate(github.rest.issues.listComments, { ...args, per_page: 100 });
    const sent = comments.some(c => c.user?.login === 'github-actions[bot]' && c.body?.includes(marker));
    log(`${dryRun ? 'Would process' : 'Processing'} #${issue.number}: ${sent ? 'notification already sent' : 'release notification needed'}`);
    if (dryRun) continue;
    if (!sent) await github.rest.issues.createComment({ ...args, body: `Out in Selene ${version}: ${release.html_url}. If it still happens after updating, reply here.\n\n${marker}` });
    await github.rest.issues.addLabels({ ...args, labels: ['released'] });
    try { await github.rest.issues.removeLabel({ ...args, name: 'fixed in next' }); }
    catch (error) { if (error.status !== 404) throw error; }
    if (issue.state === 'open' && issue.state_reason !== 'reopened')
      await github.rest.issues.update({ ...args, state: 'closed', state_reason: 'completed' });
  }
};
