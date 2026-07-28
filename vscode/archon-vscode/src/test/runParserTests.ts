import { changedLines, parseDiffHunks } from '../diff';
import { describeAge, escapeMarkdown, findIssueKey, issueUrl, parseBlame, splitMessage } from '../history';

let checks = 0;
const failures: string[] = [];

function check(description: string, condition: boolean): void {
  checks++;
  if (!condition) {
    failures.push(description);
  }
}

function equal<T>(description: string, actual: T, expected: T): void {
  check(`${description} (expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)})`, actual === expected);
}

const hunks = parseDiffHunks(
  ['diff --git a/x.cs b/x.cs', '@@ -1,2 +1,3 @@', '@@ -40 +41,0 @@', '@@ -80,0 +90,5 @@'].join('\n')
);

equal('three hunk headers are read', hunks.length, 3);
equal('a hunk with a count maps onto the current file', hunks[0].startLine, 0);
equal('a hunk with a count keeps its length', hunks[0].lineCount, 3);
equal('an omitted removed count means one line', hunks[0].removedLines, 2);
equal('a pure deletion is anchored to the line it followed', hunks[1].startLine, 41);
equal('a pure deletion has no length in the current file', hunks[1].lineCount, 0);
equal('a pure deletion still records what was removed', hunks[1].removedLines, 1);
equal('an omitted new count means one line', parseDiffHunks('@@ -3 +7 @@').at(0)?.lineCount, 1);
equal('a pure addition records no removals', hunks[2].removedLines, 0);
equal('nothing is read from an empty diff', parseDiffHunks('').length, 0);
equal('text resembling a header but not at line start is ignored', parseDiffHunks('x @@ -1 +1 @@').length, 0);

const lines = changedLines(hunks);
equal('changed lines cover a hunk fully', [0, 1, 2].every((l) => lines.has(l)), true);
equal('a pure deletion contributes no changed line', lines.has(41), false);
equal('a later hunk starts where git says it does', hunks[2].startLine, 89);
equal('changed lines cover a later hunk to its last line', lines.has(93), true);
equal('a line past a hunk is not changed', lines.has(94), false);
equal('a line before a hunk is not changed', lines.has(88), false);

const porcelain = [
  '9f3c1d4a5b6c7d8e9f0a1b2c3d4e5f6071829304 12 12 1',
  'author Some Person',
  'author-mail <person@example.invalid>',
  'author-time 1700000000',
  'summary a subject line'
].join('\n');

equal('a committed line yields its hash', parseBlame(porcelain)?.fullHash.slice(0, 8), '9f3c1d4a');
equal('a committed line yields its author', parseBlame(porcelain)?.author, 'Some Person');
equal('a committed line yields its time', parseBlame(porcelain)?.authorTime, 1700000000);
equal('an uncommitted line yields nothing', parseBlame(`${'0'.repeat(40)} 1 1 1\nauthor Not Committed Yet`), undefined);
equal('output without a hash yields nothing', parseBlame('fatal: no such path'), undefined);
equal('a missing author falls back rather than failing', parseBlame(`${'a'.repeat(40)} 1 1 1`)?.author, 'unknown');

const message = splitMessage('Fix the thing\n\nBecause the other thing broke.\n');
equal('the subject is the first line', message.subject, 'Fix the thing');
equal('the body is everything after it, trimmed', message.body, 'Because the other thing broke.');
equal('a one-line message has no body', splitMessage('Only this').body, '');

const pattern = '[A-Z][A-Z0-9]+-\\d+';
equal('an issue key is found in a subject', findIssueKey('PROJ-42 fix the thing', pattern), 'PROJ-42');
equal('the first key wins', findIssueKey('see PROJ-42 and PROJ-99', pattern), 'PROJ-42');
equal('no key is found where there is none', findIssueKey('fix the thing', pattern), undefined);
equal('a pattern that does not compile yields no key', findIssueKey('PROJ-42', '([unclosed'), undefined);

equal('a template is filled in', issueUrl('https://tracker.example/browse/{key}', 'PROJ-42'), 'https://tracker.example/browse/PROJ-42');
equal('a key is encoded into the link', issueUrl('https://x/{key}', 'A B'), 'https://x/A%20B');
equal('a template without a placeholder yields no link', issueUrl('https://tracker.example', 'PROJ-42'), undefined);
equal('an empty template yields no link', issueUrl('', 'PROJ-42'), undefined);

equal('markdown in commit text is escaped', escapeMarkdown('a *bold* [link](x)'), 'a \\*bold\\* \\[link\\]\\(x\\)');
equal('a backtick is escaped', escapeMarkdown('`code`'), '\\`code\\`');
equal('plain text is unchanged', escapeMarkdown('plain text'), 'plain text');

const now = 1700000000000;
equal('a recent change reads as minutes', describeAge(1700000000 - 120, now), '2 minutes ago');
equal('a singular unit is not pluralised', describeAge(1700000000 - 60 * 60, now), '1 hour ago');
equal('days are preferred over hours', describeAge(1700000000 - 60 * 60 * 24 * 3, now), '3 days ago');
equal('years are the largest unit', describeAge(1700000000 - 60 * 60 * 24 * 800, now), '2 years ago');
equal('a change in the same moment reads as such', describeAge(1700000000, now), 'just now');
equal('a clock skewed into the future does not report a negative age', describeAge(1700000000 + 5000, now), 'just now');

if (failures.length > 0) {
  console.error(`${failures.length} of ${checks} assertions failed:`);
  for (const failure of failures) {
    console.error(`  ${failure}`);
  }
  process.exit(1);
}
console.log(`All ${checks} assertions passed.`);
