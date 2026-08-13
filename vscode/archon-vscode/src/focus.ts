import * as vscode from 'vscode';
import { DiffHunk, DiffResult, changedLines, clearRenameCache, fileDiff, resolveBaseRef } from './diff';
import { findRepositoryRoot } from './git';

interface FileFocus {
  hunks: DiffHunk[];
  reason?: string;
  detail?: string;
}

/**
 * Narrows attention to what a branch changed: everything a file has in common with the base ref is
 * dimmed, and each changed run is summarised above itself.
 *
 * The mode is a property of the session rather than of one file, so entering it once covers every
 * file opened afterwards. The original per-file toggle meant re-entering the mode on each file
 * during a review, which is the moment it is least wanted.
 *
 * A file with no changes is left undimmed rather than dimmed entirely. An evenly faded file with no
 * explanation reads as a rendering fault, not as "nothing was changed here".
 */
export class FocusMode {
  private readonly dimmed: vscode.TextEditorDecorationType;
  private readonly byUri = new Map<string, FileFocus>();
  private readonly lensChanged = new vscode.EventEmitter<void>();
  private readonly timers = new Map<string, NodeJS.Timeout>();
  private baseRef: string | undefined;
  private active = false;

  public readonly onDidChangeLenses = this.lensChanged.event;

  constructor(
    private readonly log: (message: string) => void,
    private readonly onStateChanged: () => void
  ) {
    this.dimmed = vscode.window.createTextEditorDecorationType({
      opacity: String(vscode.workspace.getConfiguration('archon').get<number>('focus.dimOpacity', 0.4)),
      isWholeLine: true
    });
  }

  public dispose(): void {
    for (const timer of this.timers.values()) {
      clearTimeout(timer);
    }
    this.timers.clear();
    this.dimmed.dispose();
    this.lensChanged.dispose();
  }

  public get isActive(): boolean {
    return this.active;
  }

  public get ref(): string | undefined {
    return this.baseRef;
  }

  public hunksFor(uri: vscode.Uri): DiffHunk[] | undefined {
    return this.active ? this.byUri.get(uri.toString())?.hunks : undefined;
  }

  /** The changed lines of a file, for deciding whether a finding sits inside the change. */
  public changedLinesFor(uri: vscode.Uri): Set<number> | undefined {
    const hunks = this.hunksFor(uri);
    return hunks ? changedLines(hunks) : undefined;
  }

  public async toggle(explicitRef?: string): Promise<void> {
    if (this.active && explicitRef === undefined) {
      this.leave();
      return;
    }
    await this.enter(explicitRef);
  }

  public leave(): void {
    this.active = false;
    this.baseRef = undefined;
    this.byUri.clear();
    clearRenameCache();
    for (const editor of vscode.window.visibleTextEditors) {
      editor.setDecorations(this.dimmed, []);
    }
    this.lensChanged.fire();
    this.onStateChanged();
    void vscode.commands.executeCommand('setContext', 'archon.focusActive', false);
  }

  private async enter(explicitRef?: string): Promise<void> {
    const anchor = vscode.window.activeTextEditor?.document.uri.fsPath
      ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (!anchor) {
      vscode.window.showInformationMessage('Archon: open a file inside a repository to review changes.');
      return;
    }

    const repositoryRoot = await findRepositoryRoot(anchor);
    if (!repositoryRoot) {
      vscode.window.showInformationMessage('Archon: this folder is not inside a git repository.');
      return;
    }

    this.baseRef = await resolveBaseRef(repositoryRoot, explicitRef);
    this.active = true;
    this.byUri.clear();
    void vscode.commands.executeCommand('setContext', 'archon.focusActive', true);
    this.log(`review mode comparing against ${this.baseRef}`);

    await Promise.all(vscode.window.visibleTextEditors.map((editor) => this.refresh(editor.document)));
  }

  /** Recomputes one file and repaints every editor showing it. */
  public async refresh(document: vscode.TextDocument): Promise<void> {
    if (!this.active || !this.baseRef || document.uri.scheme !== 'file') {
      return;
    }

    const repositoryRoot = await findRepositoryRoot(document.uri.fsPath);
    if (!repositoryRoot) {
      return;
    }

    const ignoreWhitespace = vscode.workspace.getConfiguration('archon').get<boolean>('focus.ignoreWhitespace', false);
    const result: DiffResult = await fileDiff(repositoryRoot, document.uri.fsPath, this.baseRef, { ignoreWhitespace });
    if (!this.active) {
      return;
    }

    this.byUri.set(document.uri.toString(), { hunks: result.hunks, reason: result.reason, detail: result.detail });
    this.paint(document);
    this.lensChanged.fire();
    this.onStateChanged();
  }

  /** Recomputes after a pause in typing, so a diff is not run on every keystroke. */
  public scheduleRefresh(document: vscode.TextDocument): void {
    if (!this.active || document.uri.scheme !== 'file') {
      return;
    }
    const key = document.uri.toString();
    const existing = this.timers.get(key);
    if (existing) {
      clearTimeout(existing);
    }
    this.timers.set(
      key,
      setTimeout(() => {
        this.timers.delete(key);
        void this.refresh(document);
      }, vscode.workspace.getConfiguration('archon').get<number>('focus.debounceMilliseconds', 400))
    );
  }

  public forget(uri: vscode.Uri): void {
    this.byUri.delete(uri.toString());
  }

  private paint(document: vscode.TextDocument): void {
    const focus = this.byUri.get(document.uri.toString());
    const editors = vscode.window.visibleTextEditors.filter(
      (editor) => editor.document.uri.toString() === document.uri.toString()
    );

    if (!focus || focus.hunks.length === 0) {
      for (const editor of editors) {
        editor.setDecorations(this.dimmed, []);
      }
      return;
    }

    const changed = changedLines(focus.hunks);
    const ranges: vscode.Range[] = [];
    for (let line = 0; line < document.lineCount; line++) {
      if (!changed.has(line)) {
        ranges.push(document.lineAt(line).range);
      }
    }
    for (const editor of editors) {
      editor.setDecorations(this.dimmed, ranges);
    }
  }

  /** Paints an editor that has just become visible, using what is already computed for its file. */
  public paintVisible(editor: vscode.TextEditor): void {
    if (!this.active) {
      return;
    }
    if (this.byUri.has(editor.document.uri.toString())) {
      this.paint(editor.document);
    } else {
      void this.refresh(editor.document);
    }
  }

  /** A one-line summary of the current state, for the status bar. */
  public describe(): string {
    const focus = vscode.window.activeTextEditor
      ? this.byUri.get(vscode.window.activeTextEditor.document.uri.toString())
      : undefined;

    if (!focus) {
      return `reviewing vs ${this.shortRef()}`;
    }
    if (focus.reason === 'binary') {
      return 'binary file — nothing to review';
    }
    if (focus.reason === 'untracked') {
      return `not tracked in ${this.shortRef()} — all of it is new`;
    }
    if (focus.reason === 'bad-ref') {
      return `cannot resolve ${this.shortRef()} — nothing to compare against`;
    }
    if (focus.reason === 'too-large') {
      return `the diff vs ${this.shortRef()} is too large to read`;
    }
    if (focus.reason === 'error') {
      return `git failed comparing against ${this.shortRef()}${focus.detail ? ` — ${focus.detail}` : ''}`;
    }
    if (focus.hunks.length === 0) {
      return `unchanged vs ${this.shortRef()}`;
    }
    const added = focus.hunks.reduce((total, hunk) => total + hunk.addedLines, 0);
    const removed = focus.hunks.reduce((total, hunk) => total + hunk.removedLines, 0);
    return `${focus.hunks.length} hunk(s) +${added} -${removed} vs ${this.shortRef()}`;
  }

  private shortRef(): string {
    const ref = this.baseRef ?? 'base';
    return /^[0-9a-f]{40}$/.test(ref) ? ref.slice(0, 8) : ref;
  }

  /** Moves the cursor to the next or previous changed run, wrapping at either end. */
  public jump(direction: 1 | -1): void {
    const editor = vscode.window.activeTextEditor;
    if (!editor) {
      return;
    }
    const hunks = this.hunksFor(editor.document.uri);
    if (!hunks || hunks.length === 0) {
      vscode.window.setStatusBarMessage('Archon: nothing changed in this file', 3000);
      return;
    }

    const starts = hunks.map((hunk) => hunk.startLine).sort((a, b) => a - b);
    const current = editor.selection.active.line;
    const target =
      direction === 1
        ? starts.find((line) => line > current) ?? starts[0]
        : [...starts].reverse().find((line) => line < current) ?? starts[starts.length - 1];

    const position = new vscode.Position(Math.min(target, Math.max(0, editor.document.lineCount - 1)), 0);
    editor.selection = new vscode.Selection(position, position);
    editor.revealRange(new vscode.Range(position, position), vscode.TextEditorRevealType.InCenter);
  }
}

/**
 * Summarises each changed run above itself. Registered for every file but silent unless review mode
 * is on, so it does not compete for space with the impact lens during ordinary editing.
 */
export class FocusLensProvider implements vscode.CodeLensProvider {
  public readonly onDidChangeCodeLenses: vscode.Event<void>;

  constructor(
    private readonly focus: FocusMode,
    /** Findings for a file, so a hunk's lens can say how many land inside it without a second pass
     * over the status bar's aggregate count. */
    private readonly getFindings: (uri: vscode.Uri) => readonly { startLine: number }[]
  ) {
    this.onDidChangeCodeLenses = focus.onDidChangeLenses;
  }

  public provideCodeLenses(document: vscode.TextDocument): vscode.CodeLens[] {
    const hunks = this.focus.hunksFor(document.uri);
    if (!hunks) {
      return [];
    }
    const findings = this.getFindings(document.uri);
    const lastLine = Math.max(0, document.lineCount - 1);
    return hunks.map((hunk) => {
      const line = Math.min(hunk.startLine, lastLine);
      const inside = findings.filter(
        (finding) => finding.startLine >= hunk.startLine && finding.startLine < hunk.startLine + hunk.lineCount
      ).length;
      const suffix = inside > 0 ? ` · ${inside} finding${inside === 1 ? '' : 's'}` : '';
      return new vscode.CodeLens(new vscode.Range(line, 0, line, 0), {
        title: `+${hunk.addedLines} -${hunk.removedLines} vs base${suffix}`,
        command: 'archon.copyHunkReference',
        arguments: [document.uri, hunk]
      });
    });
  }
}
