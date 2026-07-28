import * as vscode from 'vscode';
import { findRepositoryRoot, runGit } from './git';
import {
  LineHistory,
  describeAge,
  escapeMarkdown,
  findIssueKey,
  issueUrl,
  parseBlame,
  splitMessage
} from './history';

/**
 * Explains why a line exists, by showing the commit that last changed it.
 *
 * Only the hovered line is blamed, so the cost is the same in a large file as in a small one. Commit
 * messages are cached for the session because they never change; blame results are cached per
 * document revision, because an edit moves lines and invalidates their attribution.
 */
export class HistoryHoverProvider implements vscode.HoverProvider {
  private readonly messages = new Map<string, { subject: string; body: string }>();
  private readonly blames = new Map<string, LineHistory | undefined>();

  constructor(private readonly log: (message: string) => void) {}

  /** Drops cached blame for a file, leaving commit messages alone since those cannot go stale. */
  public forget(uri: vscode.Uri): void {
    const prefix = `${uri.toString()}::`;
    for (const key of [...this.blames.keys()]) {
      if (key.startsWith(prefix)) {
        this.blames.delete(key);
      }
    }
  }

  public clear(): void {
    this.messages.clear();
    this.blames.clear();
  }

  public async provideHover(
    document: vscode.TextDocument,
    position: vscode.Position
  ): Promise<vscode.Hover | undefined> {
    if (!this.enabled() || document.uri.scheme !== 'file') {
      return undefined;
    }

    const history = await this.historyFor(document, position.line);
    if (!history) {
      return undefined;
    }

    const settings = vscode.workspace.getConfiguration('archon');
    const markdown = new vscode.MarkdownString();
    markdown.isTrusted = false;

    if (document.isDirty) {
      markdown.appendMarkdown('Unsaved edits in this file — the line below may have moved.\n\n');
    }

    markdown.appendMarkdown(`**${escapeMarkdown(history.subject)}**\n\n`);
    markdown.appendMarkdown(
      `\`${history.shortHash}\` · ${escapeMarkdown(history.author)} · ${describeAge(history.authorTime, Date.now())}\n\n`
    );
    if (history.body) {
      markdown.appendMarkdown(`${escapeMarkdown(history.body)}\n\n`);
    }

    const key = findIssueKey(
      `${history.subject}\n${history.body}`,
      settings.get<string>('history.issuePattern', '[A-Z][A-Z0-9]+-\\d+')
    );
    if (key) {
      const url = issueUrl(settings.get<string>('history.issueUrl', ''), key);
      markdown.appendMarkdown(url ? `[${key}](${url})` : `Issue \`${key}\``);
    }

    return new vscode.Hover(markdown);
  }

  private enabled(): boolean {
    return vscode.workspace.getConfiguration('archon').get<boolean>('history.enabled', true);
  }

  private async historyFor(document: vscode.TextDocument, line: number): Promise<LineHistory | undefined> {
    const cacheKey = `${document.uri.toString()}::${document.version}::${line}`;
    if (this.blames.has(cacheKey)) {
      return this.blames.get(cacheKey);
    }
    const history = await this.blameLine(document.uri.fsPath, line + 1);
    this.blames.set(cacheKey, history);
    return history;
  }

  private async blameLine(filePath: string, oneBasedLine: number): Promise<LineHistory | undefined> {
    const repositoryRoot = await findRepositoryRoot(filePath);
    if (!repositoryRoot) {
      return undefined;
    }

    let blame: ReturnType<typeof parseBlame>;
    try {
      const porcelain = await runGit(
        repositoryRoot,
        ['blame', '--porcelain', '-L', `${oneBasedLine},${oneBasedLine}`, '--', filePath],
        1024 * 1024
      );
      blame = parseBlame(porcelain);
    } catch {
      return undefined;
    }
    if (!blame) {
      return undefined;
    }

    let message = this.messages.get(blame.fullHash);
    if (!message) {
      try {
        message = splitMessage(await runGit(repositoryRoot, ['show', '-s', '--format=%s%n%n%b', blame.fullHash]));
        this.messages.set(blame.fullHash, message);
      } catch (error) {
        this.log(`could not read commit ${blame.fullHash.slice(0, 8)}: ${error instanceof Error ? error.message : String(error)}`);
        return undefined;
      }
    }

    return {
      shortHash: blame.fullHash.slice(0, 8),
      fullHash: blame.fullHash,
      author: blame.author,
      authorTime: blame.authorTime,
      subject: message.subject,
      body: message.body
    };
  }
}
