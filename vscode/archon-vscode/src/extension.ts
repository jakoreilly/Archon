import * as path from 'path';
import * as vscode from 'vscode';
import { upsertRuleSeverity } from './archonConfigEdit';
import { AnalysisReply, ArchonClient, FindingInfo, MethodImpactInfo, RuleInfo } from './client';
import { PerfHintCodeActionProvider } from './codeActions';
import { DiffHunk } from './diff';
import { FocusLensProvider, FocusMode } from './focus';
import { forgetRepositoryRoots } from './git';
import { describeAge } from './history';
import { HistoryHoverProvider } from './historyHover';
import { ImpactLensProvider, showCallers } from './impactLens';
import { Node, RuleNode, RulesTreeProvider } from './rulesTree';
import { SuppressionCodeActionProvider } from './suppressionActions';

const SUPPORTED_LANGUAGES = ['csharp', 'sql'];
const SEVERITY_CHOICES = ['error', 'warning', 'information', 'hint', 'off'];
const CONFIG_FILE_NAME = '.archon.json';

let client: ArchonClient | undefined;
let diagnostics: vscode.DiagnosticCollection;
let output: vscode.OutputChannel;
let status: vscode.StatusBarItem;
let reviewStatus: vscode.StatusBarItem;
let tree: RulesTreeProvider;
let focus: FocusMode;
let impactLens: ImpactLensProvider;
let history: HistoryHoverProvider;
let rules: RuleInfo[] = [];
let findingsByFile = new Map<string, FindingInfo[]>();
let loggedInvalidSnippetsUriTemplate = false;

/** Where the host found `.archon.json`, or null until a config-carrying reply has been seen. */
let configPath: string | null = null;

/** Captured at activation so a command started well after startup — restart — can call it again. */
let extensionContext: vscode.ExtensionContext;

interface FileAnalysisState {
  version: number;
  timestamp: number;
  elapsedMilliseconds: number;
}

/**
 * The last analysis result for a file, keyed by path, so the status bar can tell "clean" apart
 * from "never looked at" for whichever file is active. Compared against the document's own
 * version rather than trusted forever, since an edit since the last pass means this no longer
 * describes what is on screen.
 */
const analysedFiles = new Map<string, FileAnalysisState>();

/** Requests in flight, so the status bar can show a spinner without a boolean racing itself when saves overlap. */
let pendingAnalyses = 0;

/**
 * One pending analysis per file. A single shared timer would let an edit to one file cancel the
 * analysis of another, so the file edited first would simply never be looked at.
 */
const debounces = new Map<string, NodeJS.Timeout>();

/**
 * Files the previous single-file pass reported on. A project-scope rule reports findings in files
 * other than the one saved, so when such a finding is fixed the reply simply stops mentioning it:
 * without remembering what was reported last time, there is nothing to tell the editor to clear.
 */
let reportedFiles = new Set<string>();

/** Paths changed on disk, collected so that a branch switch sends one message rather than thousands. */
const pendingInvalidations = new Set<string>();
let pendingStructural = false;
let invalidationTimer: NodeJS.Timeout | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  extensionContext = context;
  output = vscode.window.createOutputChannel('Archon');
  diagnostics = vscode.languages.createDiagnosticCollection('archon');
  status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  reviewStatus = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 99);
  reviewStatus.command = 'archon.toggleReviewMode';
  tree = new RulesTreeProvider();
  focus = new FocusMode(log, updateReviewStatus);
  impactLens = new ImpactLensProvider(() => client, log);
  history = new HistoryHoverProvider(log, impactLens);

  context.subscriptions.push(
    output,
    diagnostics,
    status,
    reviewStatus,
    focus,
    impactLens,
    vscode.window.registerTreeDataProvider('archon.rules', tree),
    vscode.languages.registerCodeLensProvider({ language: 'csharp', scheme: 'file' }, impactLens),
    vscode.languages.registerCodeLensProvider({ scheme: 'file' }, new FocusLensProvider(focus)),
    vscode.languages.registerHoverProvider({ scheme: 'file' }, history),
    vscode.languages.registerCodeActionsProvider(
      { language: 'csharp', scheme: 'file' },
      new PerfHintCodeActionProvider(),
      { providedCodeActionKinds: PerfHintCodeActionProvider.providedCodeActionKinds }
    ),
    vscode.languages.registerCodeActionsProvider(
      [
        { language: 'csharp', scheme: 'file' },
        { language: 'sql', scheme: 'file' }
      ],
      new SuppressionCodeActionProvider(),
      { providedCodeActionKinds: SuppressionCodeActionProvider.providedCodeActionKinds }
    ),
    vscode.commands.registerCommand('archon.analyzeWorkspace', analyzeWorkspace),
    vscode.commands.registerCommand('archon.analyzeActiveFile', () => {
      const editor = vscode.window.activeTextEditor;
      if (editor) {
        void analyzeDocument(editor.document);
      }
    }),
    vscode.commands.registerCommand('archon.analyzeFile', (uri?: vscode.Uri, uris?: vscode.Uri[]) =>
      analyzeFiles(uri, uris)
    ),
    vscode.commands.registerCommand('archon.writeBaseline', writeBaseline),
    vscode.commands.registerCommand('archon.reload', reload),
    vscode.commands.registerCommand('archon.showOutput', () => output.show(true)),
    vscode.commands.registerCommand('archon.openMenu', openMenu),
    vscode.commands.registerCommand('archon.restart', () => restart()),
    vscode.commands.registerCommand('archon.enableRule', (node?: Node) => changeSeverity(node, undefined)),
    vscode.commands.registerCommand('archon.disableRule', (node?: Node) => changeSeverity(node, 'off')),
    vscode.commands.registerCommand('archon.setRuleSeverity', (node?: Node) => changeSeverity(node)),
    vscode.commands.registerCommand('archon.setSeverityForRule', (ruleId: string, severity?: string) =>
      setSeverityForRule(ruleId, severity)
    ),
    vscode.commands.registerCommand('archon.explainRule', explainRule),
    vscode.commands.registerCommand('archon.toggleReviewMode', () => focus.toggle()),
    vscode.commands.registerCommand('archon.explainLine', explainLine),
    vscode.commands.registerCommand('archon.setReviewBaseRef', setReviewBaseRef),
    vscode.commands.registerCommand('archon.nextChange', () => focus.jump(1)),
    vscode.commands.registerCommand('archon.previousChange', () => focus.jump(-1)),
    vscode.commands.registerCommand('archon.copyHunkReference', copyHunkReference),
    vscode.commands.registerCommand('archon.showCallers', (method: MethodImpactInfo) => showCallers(method)),
    vscode.commands.registerCommand('archon.showCallersHere', showCallersHere),
    vscode.workspace.onDidSaveTextDocument((document) => {
      if (isSupported(document) && analysisTrigger() !== 'manual') {
        void analyzeDocument(document);
      }
      if (document.languageId === 'csharp') {
        impactLens.invalidateAll();
      }
      history.forget(document.uri);
      void focus.refresh(document);
    }),
    vscode.workspace.onDidChangeTextDocument((event) => {
      onDocumentChanged(event.document);
      focus.scheduleRefresh(event.document);
    }),
    vscode.workspace.onDidCloseTextDocument((document) => {
      diagnostics.delete(document.uri);
      findingsByFile.delete(document.uri.fsPath);
      analysedFiles.delete(document.uri.fsPath);
      history.forget(document.uri);
      focus.forget(document.uri);
      impactLens.forget(document.uri);
      const pending = debounces.get(document.uri.toString());
      if (pending) {
        clearTimeout(pending);
        debounces.delete(document.uri.toString());
      }
    }),
    vscode.window.onDidChangeVisibleTextEditors((editors) => {
      for (const editor of editors) {
        focus.paintVisible(editor);
      }
    }),
    vscode.window.onDidChangeActiveTextEditor(() => {
      updateReviewStatus();
      refreshStatusBar();
    }),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration('archon.hostPath')) {
        // A restart, not a reload: the path names a different executable, so the running process
        // — started from the old path — has nothing to reload that would pick the new one up.
        void restart();
      }
      if (event.affectsConfiguration('archon.impact')) {
        impactLens.invalidateAll();
      }
    })
  );

  context.subscriptions.push(...watchSourceFiles());

  await startHost(context);
}

/**
 * Watches for source files changing on disk rather than in the editor. Without this the host holds
 * whatever it read first for as long as it runs, so after switching branches every finding and
 * every caller count still describes the tree that was there before.
 */
function watchSourceFiles(): vscode.Disposable[] {
  const watcher = vscode.workspace.createFileSystemWatcher('**/*.{cs,sql}');
  return [
    watcher,
    watcher.onDidChange((uri) => noteFileChanged(uri, false)),
    watcher.onDidCreate((uri) => noteFileChanged(uri, true)),
    watcher.onDidDelete((uri) => noteFileChanged(uri, true)),
    // A project appearing or disappearing changes which files belong to which project, which the
    // lifetime and layering rules read.
    ...watchProjects()
  ];
}

function watchProjects(): vscode.Disposable[] {
  const watcher = vscode.workspace.createFileSystemWatcher('**/*.csproj');
  const structural = (uri: vscode.Uri) => noteFileChanged(uri, true);
  return [watcher, watcher.onDidCreate(structural), watcher.onDidDelete(structural)];
}

/**
 * Waited on by VS Code, so the analysis process is given the chance to stop before the editor goes.
 * A process left running holds its own files open, which is enough to make reinstalling the
 * extension fail.
 */
export async function deactivate(): Promise<void> {
  for (const timer of debounces.values()) {
    clearTimeout(timer);
  }
  debounces.clear();
  if (invalidationTimer) {
    clearTimeout(invalidationTimer);
  }

  const stopping = client?.dispose();
  client = undefined;
  history?.clear();
  forgetRepositoryRoots();
  await stopping;
}

async function setReviewBaseRef(): Promise<void> {
  const entered = await vscode.window.showInputBox({
    title: 'Review changes against',
    prompt: 'A branch, tag or commit. Leave empty to go back to the merge base with the upstream branch.',
    value: focus.ref ?? ''
  });
  if (entered === undefined) {
    return;
  }
  await focus.toggle(entered.trim() || undefined);
}

/**
 * The hover's blame lookup, surfaced as a command so "why is this here" is reachable from a
 * right-click and the command palette rather than only by holding still over a line.
 */
async function explainLine(): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) {
    return;
  }

  const line = editor.selection.active.line;
  const entry = await history.lineHistory(editor.document, line);
  if (!entry) {
    vscode.window.showInformationMessage('Archon: no commit history for this line — it may be unsaved, uncommitted, or outside a git repository.');
    return;
  }

  const detail = [
    `${entry.shortHash} · ${entry.author} · ${describeAge(entry.authorTime, Date.now())}`,
    entry.body
  ]
    .filter(Boolean)
    .join('\n\n');

  const choice = await vscode.window.showInformationMessage(entry.subject, { modal: true, detail }, 'Copy Commit Hash');
  if (choice === 'Copy Commit Hash') {
    await vscode.env.clipboard.writeText(entry.fullHash);
    vscode.window.setStatusBarMessage(`Copied ${entry.shortHash}`, 3000);
  }
}

/**
 * The impact lens's caller list, reachable from the cursor instead of only by clicking the code
 * lens above the method — the lens itself still needs a declaration to anchor to, so the lookup
 * is unchanged, just given a second door in.
 */
function showCallersHere(): void {
  const editor = vscode.window.activeTextEditor;
  if (!editor) {
    return;
  }

  const method = impactLens.methodAt(editor.document.uri, editor.selection.active.line);
  if (!method) {
    vscode.window.showInformationMessage('Archon: place the cursor on a C# method declaration to show its callers.');
    return;
  }
  void showCallers(method);
}

async function copyHunkReference(uri: vscode.Uri, hunk: DiffHunk): Promise<void> {
  const reference = `${vscode.workspace.asRelativePath(uri)}:${hunk.startLine + 1} (+${hunk.addedLines} -${hunk.removedLines})`;
  await vscode.env.clipboard.writeText(reference);
  vscode.window.setStatusBarMessage(`Copied ${reference}`, 3000);
}

/**
 * Reports the state of review mode, and how many of the findings in the active file fall inside the
 * change. That last number is the reason these live in one extension: it needs the diff and the
 * findings together, and neither surface could produce it alone.
 */
function updateReviewStatus(): void {
  if (!focus.isActive) {
    reviewStatus.hide();
    return;
  }

  const editor = vscode.window.activeTextEditor;
  let suffix = '';
  if (editor) {
    const changed = focus.changedLinesFor(editor.document.uri);
    const findings = findingsByFile.get(editor.document.uri.fsPath) ?? [];
    if (changed && changed.size > 0 && findings.length > 0) {
      const inside = findings.filter((finding) => finding.startLine >= 0 && changed.has(finding.startLine)).length;
      suffix = ` · ${inside} of ${findings.length} findings in the change`;
    }
  }

  reviewStatus.text = `$(git-compare) ${focus.describe()}${suffix}`;
  reviewStatus.tooltip = 'Archon review mode — select to leave';
  reviewStatus.show();
}

function resolveHostPath(context: vscode.ExtensionContext): string {
  const configured = vscode.workspace.getConfiguration('archon').get<string>('hostPath', '').trim();
  return configured.length > 0
    ? configured
    : context.asAbsolutePath(path.join('host', 'archon-host.dll'));
}

async function startHost(context: vscode.ExtensionContext): Promise<void> {
  const folders = vscode.workspace.workspaceFolders ?? [];
  const root = folders[0]?.uri.fsPath;
  if (!root) {
    setStatus('$(circle-slash) Archon: no folder open', 'archon.showOutput', 'Open a folder for Archon to analyse.');
    setRulesTreeEmpty(true);
    return;
  }
  if (folders.length > 1) {
    // One host serves one root. Saying so is better than silently analysing a third of a
    // multi-root workspace and leaving the other folders looking clean.
    log(
      `this workspace has ${folders.length} folders and Archon analyses the first only: ${root}. ` +
        `Not analysed: ${folders.slice(1).map((f) => f.uri.fsPath).join(', ')}.`
    );
  }

  const hostPath = resolveHostPath(context);
  client = new ArchonClient(hostPath, (message) => log(message), () => {
    refreshStatusBar();
  });

  try {
    client.start();
    const reply = await client.initialize(root);
    rules = reply.rules;
    configPath = reply.configPath;
    tree.setRules(rules);
    setRulesTreeEmpty(rules.length === 0);

    log(`initialised at ${reply.root}`);
    log(reply.configPath ? `configuration: ${reply.configPath}` : 'no .archon.json found; using default severities.');
    log(`baseline: ${reply.baselineCount} accepted finding(s) from ${reply.baselinePath}`);
    for (const message of reply.messages) {
      log(message);
    }
    refreshStatusBar();

    if (vscode.workspace.getConfiguration('archon').get<boolean>('analyseWorkspaceOnStartup', false)) {
      await analyzeWorkspace();
    } else if (vscode.window.activeTextEditor) {
      await analyzeDocument(vscode.window.activeTextEditor.document);
    }
  } catch (error) {
    setStatus(
      '$(error) Archon: failed to start — select to restart',
      'archon.restart',
      'Could not start the analysis process. Select to try again, or open the log for details.'
    );
    setRulesTreeEmpty(true);
    log(`could not start the analysis process at ${hostPath}: ${describe(error)}`);
    log('Check that the .NET runtime is installed and on PATH, or set archon.hostPath.');
  }
}

/**
 * Stops the current process, if any, and starts a fresh one against the same workspace. The only
 * way to recover from an unexpected exit, or to pick up a changed `archon.hostPath`: reloading
 * configuration asks the running process to re-read its settings, but a process started from the
 * old path has no way to relaunch itself from a new one.
 */
async function restart(): Promise<void> {
  log('restarting the analysis process.');
  const previous = client;
  client = undefined;
  if (previous) {
    await previous.dispose();
  }
  await startHost(extensionContext);
}

function isSupported(document: vscode.TextDocument): boolean {
  return SUPPORTED_LANGUAGES.includes(document.languageId) && document.uri.scheme === 'file';
}

function analysisTrigger(): string {
  return vscode.workspace.getConfiguration('archon').get<string>('analyseOn', 'save');
}

function onDocumentChanged(document: vscode.TextDocument): void {
  const settings = vscode.workspace.getConfiguration('archon');
  if (analysisTrigger() !== 'type' || !isSupported(document)) {
    return;
  }
  const key = document.uri.toString();
  const existing = debounces.get(key);
  if (existing) {
    clearTimeout(existing);
  }
  debounces.set(
    key,
    setTimeout(() => {
      debounces.delete(key);
      void analyzeDocument(document);
    }, settings.get<number>('debounceMilliseconds', 400))
  );
}

/**
 * Tells the host that files changed on disk. Changes are batched: switching branches rewrites
 * thousands of files at once, and one message covering all of them costs the same as one covering
 * a single file.
 */
function noteFileChanged(uri: vscode.Uri, structural: boolean): void {
  pendingInvalidations.add(uri.fsPath);
  pendingStructural = pendingStructural || structural;
  impactLens.invalidate(uri);

  if (invalidationTimer) {
    clearTimeout(invalidationTimer);
  }
  invalidationTimer = setTimeout(() => {
    invalidationTimer = undefined;
    const paths = [...pendingInvalidations];
    const structuralChange = pendingStructural;
    pendingInvalidations.clear();
    pendingStructural = false;

    if (!client?.isRunning || paths.length === 0) {
      return;
    }
    client.invalidate(paths, structuralChange).then(
      () => log(`re-read ${paths.length} file(s) changed outside the editor.`),
      (error: unknown) => log(`could not refresh changed files: ${describe(error)}`)
    );
  }, 300);
}

async function analyzeDocument(document: vscode.TextDocument): Promise<void> {
  if (!client?.isRunning || !isSupported(document)) {
    return;
  }
  beginAnalysing();
  try {
    const reply = await client.analyzeFile(
      document.uri.fsPath,
      document.isDirty ? document.getText() : undefined
    );
    applyForFile(document.uri, reply);
    analysedFiles.set(document.uri.fsPath, {
      version: document.version,
      timestamp: Date.now(),
      elapsedMilliseconds: reply.elapsedMilliseconds
    });
    reportSkipped(reply);
  } catch (error) {
    log(`could not analyse ${document.uri.fsPath}: ${describe(error)}`);
  } finally {
    endAnalysing();
  }
}

/**
 * Explorer context menu entry point. VS Code passes the right-clicked item as `uri` and, for a
 * multi-select, every selected item as `uris` — `uri` alone covers the single-selection case.
 */
async function analyzeFiles(uri?: vscode.Uri, uris?: vscode.Uri[]): Promise<void> {
  const targets = uris && uris.length > 0 ? uris : uri ? [uri] : [];
  for (const target of targets) {
    try {
      const document = await vscode.workspace.openTextDocument(target);
      if (isSupported(document)) {
        await analyzeDocument(document);
      }
    } catch (error) {
      log(`could not open ${target.fsPath}: ${describe(error)}`);
    }
  }
}

async function analyzeWorkspace(): Promise<void> {
  if (!client?.isRunning) {
    return;
  }
  beginAnalysing();
  await vscode.window.withProgress(
    { location: vscode.ProgressLocation.Window, title: 'Archon: analysing workspace', cancellable: true },
    async (progress, token) => {
      const startedAt = Date.now();
      const ticker = setInterval(() => {
        const elapsedSeconds = Math.round((Date.now() - startedAt) / 1000);
        progress.report({ message: `${elapsedSeconds}s elapsed…` });
      }, 1000);
      token.onCancellationRequested(() => {
        log('workspace analysis: no longer waiting — the analysis process keeps running in the background and results will still apply when it answers.');
      });

      try {
        const resultPromise = client!.analyzeWorkspace();
        const outcome = await Promise.race([
          resultPromise.then((reply) => ({ cancelled: false as const, reply })),
          new Promise<{ cancelled: true }>((resolve) => {
            token.onCancellationRequested(() => resolve({ cancelled: true }));
          })
        ]);

        if (outcome.cancelled) {
          void resultPromise.then(
            (reply) => {
              applyForWorkspace(reply);
              markCleanVisibleEditorsAnalysed(reply.elapsedMilliseconds);
              reportSkipped(reply);
              log(`workspace pass finished after cancellation was requested: ${reply.findings.length} finding(s).`);
            },
            (error) => log(`workspace analysis failed: ${describe(error)}`)
          );
          return;
        }

        const reply = outcome.reply;
        applyForWorkspace(reply);
        markCleanVisibleEditorsAnalysed(reply.elapsedMilliseconds);
        reportSkipped(reply);
        log(
          `workspace pass: ${reply.findings.length} finding(s) across ${reply.filesAnalysed} file(s) in ${reply.elapsedMilliseconds} ms` +
            (reply.baselinedCount > 0 ? `, ${reply.baselinedCount} baselined and not counted` : '')
        );
      } catch (error) {
        log(`workspace analysis failed: ${describe(error)}`);
      } finally {
        clearInterval(ticker);
        endAnalysing();
      }
    }
  );
}

/**
 * After a workspace pass, a currently visible editor with no unsaved changes was analysed at
 * exactly its current content — the pass reads from disk, which for a clean document is the same
 * thing. A dirty editor is left alone: the pass read what was on disk, not the buffer on screen,
 * so marking it analysed would claim a result for content that was never looked at.
 */
function markCleanVisibleEditorsAnalysed(elapsedMilliseconds: number): void {
  const timestamp = Date.now();
  for (const editor of vscode.window.visibleTextEditors) {
    if (!editor.document.isDirty && isSupported(editor.document)) {
      analysedFiles.set(editor.document.uri.fsPath, { version: editor.document.version, timestamp, elapsedMilliseconds });
    }
  }
}

function beginAnalysing(): void {
  pendingAnalyses++;
  refreshStatusBar();
}

function endAnalysing(): void {
  pendingAnalyses = Math.max(0, pendingAnalyses - 1);
  refreshStatusBar();
}

async function writeBaseline(): Promise<void> {
  if (!client?.isRunning) {
    return;
  }
  const confirmed = await vscode.window.showWarningMessage(
    'Accept every current finding as the baseline? They will still be listed, but will no longer fail a check — only new findings will.',
    { modal: true },
    'Accept'
  );
  if (confirmed !== 'Accept') {
    return;
  }
  try {
    const reply = await client.writeBaseline();
    log(`recorded ${reply.recorded} finding(s) in ${reply.path}`);
    vscode.window.showInformationMessage(`Archon accepted ${reply.recorded} finding(s) as the baseline.`);
    await analyzeWorkspace();
  } catch (error) {
    log(`could not write the baseline: ${describe(error)}`);
  }
}

async function reload(): Promise<void> {
  if (!client?.isRunning) {
    return;
  }
  try {
    const reply = await client.reloadConfig();
    rules = reply.rules;
    configPath = reply.configPath;
    tree.setRules(rules);
    setRulesTreeEmpty(rules.length === 0);
    for (const message of reply.messages) {
      log(message);
    }
    refreshStatusBar();
    log('configuration and rules reloaded.');
    if (vscode.window.activeTextEditor) {
      await analyzeDocument(vscode.window.activeTextEditor.document);
    }
  } catch (error) {
    log(`could not reload: ${describe(error)}`);
  }
}

async function changeSeverity(node: Node | undefined, severity?: string): Promise<void> {
  if (!client?.isRunning) {
    return;
  }
  const rule = await resolveRule(node);
  if (!rule) {
    return;
  }

  let target = severity;
  if (target === undefined && node instanceof RuleNode) {
    target = rule.defaultSeverity === 'off' ? 'information' : rule.defaultSeverity;
  }
  if (target === undefined) {
    target = await vscode.window.showQuickPick(SEVERITY_CHOICES, {
      title: `Severity for ${rule.id} — ${rule.title}`,
      placeHolder: `currently ${rule.severity}`
    });
  }
  if (!target) {
    return;
  }
  await applySeverity(rule, target, /* persist */ false);
}

/**
 * The severity quick fixes on a finding itself: the rule is already known, so only the severity
 * — supplied directly for "disable", or picked for "set severity…" — needs resolving. Distinct
 * from {@link changeSeverity}, which the rules tree drives and which resolves the rule too.
 */
async function setSeverityForRule(ruleId: string, severity?: string): Promise<void> {
  if (!client?.isRunning) {
    return;
  }
  const rule = rules.find((r) => r.id === ruleId);
  if (!rule) {
    return;
  }
  const target =
    severity ??
    (await vscode.window.showQuickPick(SEVERITY_CHOICES, {
      title: `Severity for ${rule.id} — ${rule.title}`,
      placeHolder: `currently ${rule.severity}`
    }));
  if (!target) {
    return;
  }
  await applySeverity(rule, target, /* persist */ true);
}

/**
 * Applies a severity to the running session and, when asked, writes it into `.archon.json` too —
 * closing the loop the tree's own severity picker leaves open today, where a change applies only
 * for the session and the log line just tells you to go and edit the file yourself.
 */
async function applySeverity(rule: RuleInfo, severity: string, persist: boolean): Promise<void> {
  if (!client?.isRunning) {
    return;
  }
  if (persist && !(await persistSeverity(rule.id, severity))) {
    vscode.window.showWarningMessage(
      `Archon could not safely edit ${CONFIG_FILE_NAME} for ${rule.id} — its shape wasn't one this edit trusts itself with. ` +
        `Add "${rule.id}": "${severity}" under "rules" by hand.`
    );
    persist = false;
  }

  try {
    await client.setSeverity(rule.id, severity);
    const updated = await client.listRules();
    rules = updated.rules;
    tree.setRules(rules);
    setRulesTreeEmpty(rules.length === 0);
    refreshStatusBar();
    log(
      persist
        ? `${rule.id} set to ${severity} and written to ${configPath ?? CONFIG_FILE_NAME}.`
        : `${rule.id} set to ${severity} for this session. Add it to .archon.json to make it permanent.`
    );
    if (vscode.window.activeTextEditor) {
      await analyzeDocument(vscode.window.activeTextEditor.document);
    }
  } catch (error) {
    log(`could not change ${rule.id}: ${describe(error)}`);
  }
}

/**
 * Writes a rule's severity into `.archon.json`, creating it at the workspace root when the host
 * found none. Edits the smallest span of text that has to change rather than reparsing and
 * rewriting the whole document, so a comment or an unrelated key survives untouched. Returns
 * `false` when the existing file's shape cannot be trusted enough to edit — a non-object root, or
 * a `rules` value that is not itself an object — leaving the file untouched either way.
 */
async function persistSeverity(ruleId: string, severity: string): Promise<boolean> {
  const folder = vscode.workspace.workspaceFolders?.[0];
  if (!folder) {
    return false;
  }
  const targetPath = configPath ?? path.join(folder.uri.fsPath, CONFIG_FILE_NAME);
  const uri = vscode.Uri.file(targetPath);

  let current: string | undefined;
  try {
    current = Buffer.from(await vscode.workspace.fs.readFile(uri)).toString('utf8');
  } catch {
    current = undefined;
  }

  const updated =
    current === undefined
      ? `{\n  "rules": {\n    "${ruleId}": "${severity}"\n  }\n}\n`
      : upsertRuleSeverity(current, ruleId, severity);
  if (updated === undefined) {
    return false;
  }

  await vscode.workspace.fs.writeFile(uri, Buffer.from(updated, 'utf8'));
  configPath ??= targetPath;
  return true;
}

async function explainRule(node?: Node): Promise<void> {
  const rule = await resolveRule(node);
  if (!rule) {
    return;
  }
  const pointer = rule.snippetId ? { snippetId: rule.snippetId, title: rule.snippetTitle ?? '', why: rule.snippetWhy ?? '' } : undefined;
  const document = await vscode.workspace.openTextDocument({
    language: 'markdown',
    content: [
      `# ${rule.id} — ${rule.title}`,
      '',
      rule.description,
      '',
      '| | |',
      '|---|---|',
      `| Category | \`${rule.category}\` |`,
      `| Scope | \`${rule.scope}\` |`,
      `| Language | \`${rule.language}\` |`,
      `| Default severity | \`${rule.defaultSeverity}\` |`,
      `| Effective severity | \`${rule.severity}\` |`,
      `| Rule pack | \`${rule.pack}\` |`,
      '',
      ...(pointer
        ? [
            '## The approved pattern',
            '',
            `**${pointer.snippetId} — ${pointer.title}**`,
            '',
            pointer.why,
            '',
            'This is the shape this rule is asking for. It comes from the team snippet library, not from',
            'the analyser: the rule detects, and the library says what to write instead.',
            ''
          ]
        : []),
      '## Suppressing one occurrence',
      '',
      '```',
      `// archon-ignore[${rule.id}] the reason this case is acceptable`,
      '```',
      '',
      'The marker applies to its own line and to the line below it.',
      '',
      '## Changing it everywhere',
      '',
      '```json',
      '{',
      '  "rules": {',
      `    "${rule.id}": "off"`,
      '  }',
      '}',
      '```',
      '',
      `Accepts \`${SEVERITY_CHOICES.join('`, `')}\`. A category name works as a key too, so`,
      `\`"${rule.category}": "off"\` disables every rule in that category.`
    ].join('\n')
  });
  await vscode.window.showTextDocument(document, { preview: true });
}

async function resolveRule(node?: Node): Promise<RuleInfo | undefined> {
  if (node instanceof RuleNode) {
    return node.rule;
  }
  const picked = await vscode.window.showQuickPick(
    rules.map((rule) => ({
      label: `${rule.id}  ${rule.title}`,
      description: rule.severity,
      detail: rule.description,
      rule
    })),
    { title: 'Archon rules', matchOnDetail: true }
  );
  return picked?.rule;
}

/**
 * Replaces diagnostics for every file the reply covers. A single-file request can still return
 * findings elsewhere, because a project-scope rule sees the whole project, so the saved file is
 * cleared explicitly and each reported file is then set from the reply.
 */
function applyForFile(uri: vscode.Uri, reply: AnalysisReply): void {
  const nowReported = new Set(reply.findings.map((finding) => finding.file));

  // Clear the saved file, and any file the last pass reported on that this one no longer does.
  // Those are findings that have just been fixed, and nothing else will ever retract them.
  diagnostics.delete(uri);
  findingsByFile.delete(uri.fsPath);
  for (const file of reportedFiles) {
    if (!nowReported.has(file)) {
      diagnostics.delete(vscode.Uri.file(file));
      findingsByFile.delete(file);
    }
  }

  applyByFile(reply.findings);
  reportedFiles = nowReported;
  updateCounts(reply.findings);
  updateReviewStatus();
}

function applyForWorkspace(reply: AnalysisReply): void {
  diagnostics.clear();
  findingsByFile = new Map<string, FindingInfo[]>();
  applyByFile(reply.findings);
  reportedFiles = new Set(reply.findings.map((finding) => finding.file));
  updateCounts(reply.findings);
  updateReviewStatus();
}

function applyByFile(findings: FindingInfo[]): void {
  const byFile = new Map<string, FindingInfo[]>();
  for (const finding of findings) {
    const existing = byFile.get(finding.file);
    if (existing) {
      existing.push(finding);
    } else {
      byFile.set(finding.file, [finding]);
    }
  }
  for (const [file, items] of byFile) {
    diagnostics.set(vscode.Uri.file(file), items.map(toDiagnostic));
    findingsByFile.set(file, items);
  }
}

function updateCounts(findings: FindingInfo[]): void {
  const counts = new Map<string, number>();
  for (const finding of findings) {
    counts.set(finding.ruleId, (counts.get(finding.ruleId) ?? 0) + 1);
  }
  tree.setFindingCounts(counts);
}

/**
 * Kinds a rule has already proven dead rather than merely suspect — the difference between "this
 * parameter is never read" and "this call might be worth a second look". Mapped to VS Code's own
 * tag for the concept, which renders as faded text in the editor with no UI of Archon's own to
 * build or maintain.
 */
const UNNECESSARY_KINDS = new Set(['UnusedParameter', 'UnusedLocalVariable']);

function toDiagnostic(finding: FindingInfo): vscode.Diagnostic {
  const range = new vscode.Range(
    Math.max(0, finding.startLine),
    Math.max(0, finding.startColumn),
    Math.max(0, finding.endLine),
    Math.max(0, finding.endColumn)
  );
  const diagnostic = new vscode.Diagnostic(range, finding.message, toSeverity(finding.severity));
  diagnostic.source = 'archon';
  diagnostic.code = diagnosticCodeFor(finding.ruleId);
  if (finding.kind && UNNECESSARY_KINDS.has(finding.kind)) {
    diagnostic.tags = [vscode.DiagnosticTag.Unnecessary];
  }
  return diagnostic;
}

/**
 * A plain rule id, unless archon.snippets.uriTemplate is set and the rule maps to a library
 * pattern — in which case the id becomes a link the Problems panel renders natively. An unmapped
 * rule (23 of 36 ids) or an empty template renders exactly as it does today.
 */
function diagnosticCodeFor(ruleId: string): string | { value: string; target: vscode.Uri } {
  const template = vscode.workspace.getConfiguration('archon').get<string>('snippets.uriTemplate', '');
  const pointer = rules.find((rule) => rule.id === ruleId);
  if (!template || !pointer?.snippetId) {
    return ruleId;
  }
  try {
    return { value: ruleId, target: vscode.Uri.parse(template.replace('{id}', pointer.snippetId), true) };
  } catch {
    if (!loggedInvalidSnippetsUriTemplate) {
      loggedInvalidSnippetsUriTemplate = true;
      log('snippets.uriTemplate is not a valid URI template; rule ids will not be linked.');
    }
    return ruleId;
  }
}

function toSeverity(severity: string): vscode.DiagnosticSeverity {
  switch (severity) {
    case 'error':
      return vscode.DiagnosticSeverity.Error;
    case 'warning':
      return vscode.DiagnosticSeverity.Warning;
    case 'information':
      return vscode.DiagnosticSeverity.Information;
    default:
      return vscode.DiagnosticSeverity.Hint;
  }
}

function reportSkipped(reply: AnalysisReply): void {
  for (const failure of reply.failedRules) {
    log(`rule ${failure.ruleId} did not run — ${failure.reason}`);
  }
  for (const diagnostic of reply.diagnostics) {
    log(diagnostic);
  }
}

/**
 * Reflects what Archon is doing right now, not just how it is configured: a rule count is the
 * same whether the active file was analysed a moment ago or has never been looked at, and those
 * two states used to render identically. Clicking always opens the same menu, except while the
 * process is down, when it restarts it directly — the one action worth reaching in a single click.
 */
function refreshStatusBar(): void {
  if (!client?.isRunning) {
    setStatus('$(error) Archon: stopped — select to restart', 'archon.restart', 'The analysis process is not running. Select to restart it.');
    return;
  }
  if (pendingAnalyses > 0) {
    setStatus('$(sync~spin) Archon: analysing…');
    return;
  }

  const enabled = rules.filter((r) => r.severity !== 'off').length;
  const ruleSummary = `${enabled}/${rules.length} rules enabled`;

  const editor = vscode.window.activeTextEditor;
  if (!editor || !isSupported(editor.document)) {
    setStatus(`$(shield) Archon: ${enabled}/${rules.length} rules`, undefined, `${ruleSummary} · select for more actions`);
    return;
  }

  const filePath = editor.document.uri.fsPath;
  const name = path.basename(filePath);
  const state = analysedFiles.get(filePath);

  if (!state || state.version !== editor.document.version) {
    setStatus(
      '$(circle-outline) Archon: not analysed',
      undefined,
      `No result yet for the current content of **${name}**.\n\n${ruleSummary} · select for more actions`
    );
    return;
  }

  const { icon, text } = summariseFindings(findingsByFile.get(filePath) ?? []);
  setStatus(
    `$(${icon}) Archon: ${text}`,
    undefined,
    new vscode.MarkdownString(
      `**${name}** — ${text}\n\n` +
        `Analysed in ${state.elapsedMilliseconds} ms at ${new Date(state.timestamp).toLocaleTimeString()}\n\n` +
        `${ruleSummary} · select for more actions`
    )
  );
}

/**
 * The worst severity present decides the icon; every severity present is named, so "1 error" does
 * not hide the three warnings sitting beside it.
 */
function summariseFindings(findings: FindingInfo[]): { icon: string; text: string } {
  if (findings.length === 0) {
    return { icon: 'check', text: 'clean' };
  }
  const bySeverity: { severity: string; icon: string; noun: string }[] = [
    { severity: 'error', icon: 'error', noun: 'error' },
    { severity: 'warning', icon: 'warning', noun: 'warning' },
    { severity: 'information', icon: 'info', noun: 'info' },
    { severity: 'hint', icon: 'info', noun: 'hint' }
  ];
  const counts = new Map<string, number>();
  for (const finding of findings) {
    counts.set(finding.severity, (counts.get(finding.severity) ?? 0) + 1);
  }
  const present = bySeverity.filter((entry) => counts.has(entry.severity));
  const text = present.map((entry) => `${counts.get(entry.severity)} ${plural(entry.noun, counts.get(entry.severity)!)}`).join(', ');
  return { icon: present[0]?.icon ?? 'info', text };
}

function plural(word: string, count: number): string {
  return count === 1 ? word : `${word}s`;
}

/**
 * The menu behind the status bar item — the answer to "must everything go through the command
 * palette". Every entry here already exists as a command; this just puts the ones reached for
 * daily, rather than occasionally, behind a single click.
 */
async function openMenu(): Promise<void> {
  interface MenuItem extends vscode.QuickPickItem {
    run: () => void | Thenable<void>;
  }

  if (!client?.isRunning) {
    const picked = await vscode.window.showQuickPick<MenuItem>(
      [
        { label: '$(debug-restart) Restart Analysis Process', run: () => restart() },
        { label: '$(output) Show Log', run: () => output.show(true) }
      ],
      { title: 'Archon — process not running' }
    );
    await picked?.run();
    return;
  }

  const trigger = analysisTrigger();
  const items: MenuItem[] = [
    {
      label: '$(search) Analyse Active File',
      run: () => {
        const editor = vscode.window.activeTextEditor;
        if (editor) {
          void analyzeDocument(editor.document);
        }
      }
    },
    { label: '$(search-fuzzy) Analyse Whole Workspace', run: analyzeWorkspace },
    {
      label: focus.isActive ? '$(git-compare) Leave Review Mode' : '$(git-compare) Review Changes',
      description: focus.isActive ? focus.describe() : 'dim everything unchanged against a base ref',
      run: () => focus.toggle()
    },
    {
      label: trigger === 'type' ? '$(pass-filled) Analyse While Typing: On' : '$(circle-large-outline) Analyse While Typing: Off',
      description: 'archon.analyseOn',
      run: () =>
        vscode.workspace
          .getConfiguration('archon')
          .update('analyseOn', trigger === 'type' ? 'save' : 'type', vscode.ConfigurationTarget.Workspace)
    },
    { label: '$(list-tree) Open Rules View', run: () => vscode.commands.executeCommand('archon.rules.focus') },
    { label: '$(pass) Accept Current Findings As Baseline', run: writeBaseline },
    { label: '$(refresh) Reload Configuration And Rules', run: reload },
    { label: '$(debug-restart) Restart Analysis Process', run: () => restart() },
    { label: '$(output) Show Log', run: () => output.show(true) }
  ];

  const picked = await vscode.window.showQuickPick(items, { title: 'Archon' });
  await picked?.run();
}

function setStatus(
  text: string,
  command: string = 'archon.openMenu',
  tooltip: string | vscode.MarkdownString = 'Archon — select for more actions'
): void {
  status.text = text;
  status.tooltip = tooltip;
  status.command = command;
  status.show();
}

function setRulesTreeEmpty(empty: boolean): void {
  void vscode.commands.executeCommand('setContext', 'archon.rulesEmpty', empty);
}

function log(message: string): void {
  output.appendLine(`[${new Date().toISOString()}] ${message}`);
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
