import * as child_process from 'child_process';
import * as path from 'path';
import { promisify } from 'util';

const execFile = promisify(child_process.execFile);

/** A git command that did not succeed. Carries the command so a log line can name what was run. */
export class GitError extends Error {
  constructor(
    message: string,
    public readonly command: string,
    /** True when git rejected the revision itself, rather than failing over the file. */
    public readonly unknownRevision = false,
    /** True when the output was too large to collect, so the failure says nothing about the file. */
    public readonly tooLarge = false
  ) {
    super(message);
  }
}

const UNKNOWN_REVISION = /unknown revision|bad revision|ambiguous argument|not a valid object name|fatal: bad object/i;

const repositoryRoots = new Map<string, string | undefined>();

/**
 * Runs git in a directory and returns its standard output. Errors are raised rather than swallowed,
 * because each caller decides differently what an unavailable repository means: a hover shows
 * nothing, whereas an explicit request to enter review mode should say why it could not.
 */
export async function runGit(cwd: string, args: string[], maxBuffer = 10 * 1024 * 1024): Promise<string> {
  try {
    const { stdout } = await execFile('git', args, {
      cwd,
      maxBuffer,
      // Review mode re-runs git as the file is typed into. Optional locks let those reads take the
      // index lock, where they can collide with whatever the user is doing in a terminal.
      env: { ...process.env, GIT_OPTIONAL_LOCKS: '0' }
    });
    return stdout;
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    throw new GitError(
      message,
      `git ${args.join(' ')}`,
      UNKNOWN_REVISION.test(message),
      /maxBuffer length exceeded/i.test(message)
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
