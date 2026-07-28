import * as child_process from 'child_process';
import * as path from 'path';
import { promisify } from 'util';

const execFile = promisify(child_process.execFile);

/** A git command that did not succeed. Carries the command so a log line can name what was run. */
export class GitError extends Error {
  constructor(
    message: string,
    public readonly command: string
  ) {
    super(message);
  }
}

const repositoryRoots = new Map<string, string | undefined>();

/**
 * Runs git in a directory and returns its standard output. Errors are raised rather than swallowed,
 * because each caller decides differently what an unavailable repository means: a hover shows
 * nothing, whereas an explicit request to enter review mode should say why it could not.
 */
export async function runGit(cwd: string, args: string[], maxBuffer = 10 * 1024 * 1024): Promise<string> {
  try {
    const { stdout } = await execFile('git', args, { cwd, maxBuffer });
    return stdout;
  } catch (error) {
    throw new GitError(
      error instanceof Error ? error.message : String(error),
      `git ${args.join(' ')}`
    );
  }
}

/**
 * Finds the repository containing a file, which is not always the workspace folder: a folder can
 * sit inside a repository, or contain several. The answer is cached per directory, since it only
 * changes when a repository is created or removed.
 */
export async function findRepositoryRoot(filePath: string): Promise<string | undefined> {
  const directory = path.dirname(filePath);
  if (repositoryRoots.has(directory)) {
    return repositoryRoots.get(directory);
  }

  let root: string | undefined;
  try {
    root = (await runGit(directory, ['rev-parse', '--show-toplevel'])).trim() || undefined;
  } catch {
    root = undefined;
  }
  repositoryRoots.set(directory, root);
  return root;
}

export function forgetRepositoryRoots(): void {
  repositoryRoots.clear();
}
