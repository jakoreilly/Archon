import * as vscode from 'vscode';
import { ArchonClient, CallerInfo, MethodImpactInfo } from './client';

/**
 * Shows how far each method reaches, above the method itself.
 *
 * Lenses are served from a cache and never wait: a request with nothing cached returns no lenses and
 * starts a background query that fires the change event when it lands. A code lens provider that
 * blocks makes the whole editor feel slow, since VS Code asks for lenses on every scroll and edit.
 */
export class ImpactLensProvider implements vscode.CodeLensProvider {
  private readonly changed = new vscode.EventEmitter<void>();

  /**
   * One entry per file, replaced when the file changes. Keying by document version instead would
   * add an entry per keystroke and never drop the old ones, so a long editing session would hold
   * every intermediate answer it had ever received.
   */
  private readonly cache = new Map<string, { version: number; methods: MethodImpactInfo[] }>();
  private readonly inFlight = new Set<string>();

  public readonly onDidChangeCodeLenses = this.changed.event;

  constructor(
    private readonly client: () => ArchonClient | undefined,
    private readonly log: (message: string) => void
  ) {}

  public dispose(): void {
    this.changed.dispose();
  }

  public invalidateAll(): void {
    this.cache.clear();
    this.changed.fire();
  }

  public provideCodeLenses(document: vscode.TextDocument): vscode.CodeLens[] {
    if (!this.enabled() || document.languageId !== 'csharp' || document.uri.scheme !== 'file') {
      return [];
    }

    const key = document.uri.toString();
    const cached = this.cache.get(key);
    if (!cached || cached.version !== document.version) {
      void this.load(document, key);
      return cached ? this.lensesFrom(cached.methods) : [];
    }
    return this.lensesFrom(cached.methods);
  }

  /**
   * Builds lenses from an answer. While a fresh one is being fetched the previous answer is still
   * shown, because clearing the lenses on every keystroke makes them flicker in and out of the
   * gutter as the file is typed into.
   */
  private lensesFrom(methods: MethodImpactInfo[]): vscode.CodeLens[] {
    const threshold = vscode.workspace
      .getConfiguration('archon')
      .get<number>('impact.minimumReferences', 1);

    return methods
      .filter((method) => method.referenceCount >= threshold)
      .map((method) => {
        const range = new vscode.Range(method.line, method.column, method.line, method.column);
        return new vscode.CodeLens(range, {
          title: describeImpact(method),
          command: 'archon.showCallers',
          arguments: [method]
        });
      });
  }

  private enabled(): boolean {
    return vscode.workspace.getConfiguration('archon').get<boolean>('impact.enabled', true);
  }

  /** Drops one file's answer, for a change that came from outside the editor. */
  public invalidate(uri: vscode.Uri): void {
    if (this.cache.delete(uri.toString())) {
      this.changed.fire();
    }
  }

  public forget(uri: vscode.Uri): void {
    this.cache.delete(uri.toString());
  }

  /**
   * The cached impact of the method declared on one line, for a hover to fold in alongside blame —
   * without this, reach and history read as two unrelated features rather than one answer to "what
   * happens if I touch this line". Answers from cache only: a hover that blocked on a fresh workspace
   * query would make pointing at a line feel slow.
   */
  public methodAt(uri: vscode.Uri, line: number): MethodImpactInfo | undefined {
    return this.cache.get(uri.toString())?.methods.find((method) => method.line === line);
  }

  private async load(document: vscode.TextDocument, key: string): Promise<void> {
    const client = this.client();
    if (!client?.isRunning || this.inFlight.has(key)) {
      return;
    }
    this.inFlight.add(key);
    const version = document.version;
    try {
      const settings = vscode.workspace.getConfiguration('archon');
      const reply = await client.methodImpact(
        document.uri.fsPath,
        document.isDirty ? document.getText() : undefined,
        settings.get<number>('impact.maxDepth', 6)
      );
      this.cache.set(key, { version, methods: reply.methods });
    } catch (error) {
      this.log(`could not measure impact for ${document.uri.fsPath}: ${error instanceof Error ? error.message : String(error)}`);
      this.cache.set(key, { version, methods: [] });
    } finally {
      this.inFlight.delete(key);
      this.changed.fire();
    }
  }
}

/**
 * Offers the call sites reaching a method. The list is what the graph actually found, so selecting an
 * entry navigates to a real line rather than to a reference the editor is asked to look up again.
 */
export async function showCallers(method: MethodImpactInfo): Promise<void> {
  if (method.callers.length === 0) {
    vscode.window.showInformationMessage(
      `Archon found no calls to ${method.methodName}. It may be reached through an interface, a container or reflection, which this analysis cannot see.`
    );
    return;
  }

  const picked = await vscode.window.showQuickPick(
    method.callers.map((caller) => ({
      label: `${caller.methodName}`,
      description: `${vscode.workspace.asRelativePath(vscode.Uri.file(caller.file))}:${caller.line + 1}`,
      caller
    })),
    {
      title: `Calls to ${method.methodName} — matched by name and argument count`,
      matchOnDescription: true
    }
  );
  if (!picked) {
    return;
  }
  await reveal(picked.caller);
}

/**
 * Every number is qualified. `~` marks counts matched by name and argument count rather than by
 * resolved symbol, and `≥` marks a test count cut off by the search depth, so a lower bound is
 * never mistaken for the total. Shared by the code lens label and the hover that folds it in.
 */
export function describeImpact(method: MethodImpactInfo): string {
  const references = `~${method.referenceCount} ${method.referenceCount === 1 ? 'caller' : 'callers'}`;
  const projects = method.projectCount > 1 ? ` in ${method.projectCount} projects` : '';
  const tests = `${method.depthBounded ? '≥' : ''}${method.coveringTestCount} covering ${
    method.coveringTestCount === 1 ? 'test' : 'tests'
  }`;
  return `${references}${projects} · ${tests}`;
}

async function reveal(caller: CallerInfo): Promise<void> {
  const document = await vscode.workspace.openTextDocument(vscode.Uri.file(caller.file));
  const editor = await vscode.window.showTextDocument(document);
  const position = new vscode.Position(caller.line, caller.column);
  editor.selection = new vscode.Selection(position, position);
  editor.revealRange(new vscode.Range(position, position), vscode.TextEditorRevealType.InCenter);
}
