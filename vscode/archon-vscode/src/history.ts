/** The commit a line last changed in. */
export interface LineHistory {
  shortHash: string;
  fullHash: string;
  author: string;
  authorTime: number;
  subject: string;
  body: string;
}

const UNCOMMITTED = /^0{40}$/;

/**
 * Reads the commit metadata for a single line from `git blame --porcelain` output.
 *
 * Returns undefined for an all-zero hash, which is how git reports a line that is not committed
 * yet. That is the common case while editing, and it is not an error.
 */
export function parseBlame(porcelain: string): { fullHash: string; author: string; authorTime: number } | undefined {
  const hash = porcelain.match(/^([0-9a-f]{40})/);
  if (!hash || UNCOMMITTED.test(hash[1])) {
    return undefined;
  }
  return {
    fullHash: hash[1],
    author: porcelain.match(/^author (.*)$/m)?.[1] ?? 'unknown',
    authorTime: parseInt(porcelain.match(/^author-time (\d+)$/m)?.[1] ?? '0', 10)
  };
}

/** Splits a commit message into its first line and the rest. */
export function splitMessage(message: string): { subject: string; body: string } {
  const lines = message.split('\n');
  return { subject: lines[0] ?? '', body: lines.slice(1).join('\n').trim() };
}

/**
 * Finds the first issue reference in commit text. The pattern is configurable because issue keys are
 * a local convention, and a pattern that does not compile yields no reference rather than failing —
 * a hover is not the place to report a malformed setting.
 */
export function findIssueKey(text: string, pattern: string): string | undefined {
  try {
    return text.match(new RegExp(pattern))?.[0];
  } catch {
    return undefined;
  }
}

/**
 * Builds a link to an issue from a template containing `{key}`. Any tracker can be addressed this
 * way, so no particular one is assumed or named.
 */
export function issueUrl(template: string, key: string): string | undefined {
  if (!template.includes('{key}')) {
    return undefined;
  }
  return template.replace(/\{key\}/g, encodeURIComponent(key));
}

/**
 * Escapes text so commit messages render as written. Commit text comes from whoever wrote the
 * commit, so it is treated as content to display and never as markup to interpret.
 */
export function escapeMarkdown(text: string): string {
  return text.replace(/[\\`*_{}[\]()#+\-.!<>|~]/g, (character) => `\\${character}`);
}

/** Describes how long ago something happened, in the largest unit that still reads naturally. */
export function describeAge(authorTime: number, now: number): string {
  const seconds = Math.max(0, Math.floor(now / 1000) - authorTime);
  const units: [number, string][] = [
    [60 * 60 * 24 * 365, 'year'],
    [60 * 60 * 24 * 30, 'month'],
    [60 * 60 * 24 * 7, 'week'],
    [60 * 60 * 24, 'day'],
    [60 * 60, 'hour'],
    [60, 'minute']
  ];
  for (const [size, name] of units) {
    const count = Math.floor(seconds / size);
    if (count >= 1) {
      return `${count} ${name}${count === 1 ? '' : 's'} ago`;
    }
  }
  return 'just now';
}
