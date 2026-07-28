import { runGit } from './git';

/** A contiguous run of changed lines, expressed in the current version of the file. */
export interface DiffHunk {
  startLine: number;
  lineCount: number;
  addedLines: number;
  removedLines: number;
}

const HUNK_HEADER = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/gm;

/**
 * Parses `git diff --unified=0` output into hunks anchored to the current file. Zero context means a
 * hunk's range is exactly the changed lines, with no surrounding padding to subtract.
 *
 * A hunk with a new-line count of zero is a pure deletion. Git anchors it to the line it followed,
 * so it is kept with a zero length and anchored to the line now in that position. It contributes
 * nothing to the changed-line set, which is correct — there is no current line to show — but it
 * still appears in the hunk count, because a deletion is something a reviewer needs to know about.
 */
export function parseDiffHunks(diffText: string): DiffHunk[] {
  const hunks: DiffHunk[] = [];
  let match: RegExpExecArray | null;
  HUNK_HEADER.lastIndex = 0;

  while ((match = HUNK_HEADER.exec(diffText)) !== null) {
    const removedCount = match[2] === undefined ? 1 : parseInt(match[2], 10);
    const newStart = parseInt(match[3], 10);
    const newCount = match[4] === undefined ? 1 : parseInt(match[4], 10);

    hunks.push({
      startLine: Math.max(0, newCount === 0 ? newStart : newStart - 1),
      lineCount: newCount,
      addedLines: newCount,
      removedLines: removedCount
    });
  }
  return hunks;
}

/** Expands hunks into the set of changed line numbers in the current file. */
export function changedLines(hunks: DiffHunk[]): Set<number> {
  const lines = new Set<number>();
  for (const hunk of hunks) {
    for (let offset = 0; offset < hunk.lineCount; offset++) {
      lines.add(hunk.startLine + offset);
    }
  }
  return lines;
}

/**
 * Resolves what to compare against. An explicit ref is used as given. Otherwise the merge base with
 * the upstream branch is used, so the comparison shows what this branch changed rather than
 * everything that happened on the upstream branch since it was created.
 */
export async function resolveBaseRef(repositoryRoot: string, explicitRef?: string): Promise<string> {
  if (explicitRef) {
    return explicitRef;
  }

  let upstream: string | undefined;
  try {
    upstream = (await runGit(repositoryRoot, ['rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{u}'])).trim();
  } catch {
    upstream = await guessDefaultBranch(repositoryRoot);
  }

  if (!upstream) {
    return 'HEAD';
  }
  try {
    return (await runGit(repositoryRoot, ['merge-base', 'HEAD', upstream])).trim();
  } catch {
    return 'HEAD';
  }
}

/**
 * Finds the default branch by asking the remote what it points at, rather than assuming a name.
 * Repositories disagree on whether that is `main`, `master` or something else, and guessing wrongly
 * produces a diff against nothing.
 */
async function guessDefaultBranch(repositoryRoot: string): Promise<string | undefined> {
  try {
    const head = (await runGit(repositoryRoot, ['symbolic-ref', 'refs/remotes/origin/HEAD'])).trim();
    const name = head.split('/').pop();
    if (name) {
      return `origin/${name}`;
    }
  } catch {
    // No remote HEAD recorded; fall through to the local candidates below.
  }

  for (const candidate of ['origin/main', 'origin/master', 'main', 'master']) {
    try {
      await runGit(repositoryRoot, ['rev-parse', '--verify', '--quiet', candidate]);
      return candidate;
    } catch {
      continue;
    }
  }
  return undefined;
}

/** The reason a file has no diff to show, distinguished so each can be reported in its own words. */
export type NoDiffReason = 'binary' | 'untracked' | 'unchanged';

export interface DiffResult {
  hunks: DiffHunk[];
  reason?: NoDiffReason;
}

/**
 * Retrieves the hunks for one file against a base ref. A file git cannot diff is reported through
 * `reason` rather than by throwing, since none of those cases is an error: a new file, a binary file
 * and an unchanged file are all ordinary states.
 */
export async function fileDiff(repositoryRoot: string, filePath: string, baseRef: string): Promise<DiffResult> {
  let output: string;
  try {
    output = await runGit(repositoryRoot, ['diff', '--unified=0', '--find-renames', baseRef, '--', filePath]);
  } catch {
    return { hunks: [], reason: 'untracked' };
  }

  if (output.includes('Binary files')) {
    return { hunks: [], reason: 'binary' };
  }

  const hunks = parseDiffHunks(output);
  return hunks.length > 0 ? { hunks } : { hunks: [], reason: 'unchanged' };
}
