import * as path from 'path';
import * as vscode from 'vscode';
import { AnalysisReply, ArchonClient, FindingInfo, MethodImpactInfo, RuleInfo } from './client';
import { DiffHunk } from './diff';
import { FocusLensProvider, FocusMode } from './focus';
import { forgetRepositoryRoots } from './git';
import { HistoryHoverProvider } from './historyHover';
import { ImpactLensProvider, showCallers } from './impactLens';
import { Node, RuleNode, RulesTreeProvider } from './rulesTree';

const SUPPORTED_LANGUAGES = ['csharp', 'sql'];
const SEVERITY_CHOICES = ['error', 'warning', 'information', 'hint', 'off'];

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
let debounce: NodeJS.Timeout | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  output = vscode.window.createOutputChannel('Archon');
  diagnostics = vscode.languages.createDiagnosticCollection('archon');
  status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  status.command = 'archon.showOutput';
  reviewStatus = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 99);
  reviewStatus.command = 'archon.toggleReviewMode';
  tree = new RulesTreeProvider();
  focus = new FocusMode(log, updateReviewStatus);
  impactLens = new ImpactLensProvider(() => client, log);
  history = new HistoryHoverProvider(log);

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
    vscode.commands.registerCommand('archon.analyzeWorkspace', analyzeWorkspace),
    vscode.commands.registerCommand('archon.analyzeActiveFile', () => {
      const editor = vscode.window.activeTextEditor;
      if (editor) {
        void analyzeDocument(editor.document);
      }
    }),
    vscode.commands.registerCommand('archon.writeBaseline', writeBaseline),
    vscode.commands.registerCommand('archon.reload', reload),
    vscode.commands.registerCommand('archon.showOutput', () => output.show(true)),
    vscode.commands.registerCommand('archon.enableRule', (node?: Node) => changeSeverity(node, undefined)),
    vscode.commands.registerCommand('archon.disableRule', (node?: Node) => changeSeverity(node, 'off')),
    vscode.commands.registerCommand('archon.setRuleSeverity', (node?: Node) => changeSeverity(node)),
    vscode.commands.registerCommand('archon.explainRule', explainRule),
    vscode.commands.registerCommand('archon.toggleReviewMode', () => focus.toggle()),
    vscode.commands.registerCommand('archon.setReviewBaseRef', setReviewBaseRef),
    vscode.commands.registerCommand('archon.nextChange', () => focus.jump(1)),
    vscode.commands.registerCommand('archon.previousChange', () => focus.jump(-1)),
    vscode.commands.registerCommand('archon.copyHunkReference', copyHunkReference),
    vscode.commands.registerCommand('archon.showCallers', (method: MethodImpactInfo) => showCallers(method)),
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
      history.forget(document.uri);
      focus.forget(document.uri);
    }),
    vscode.window.onDidChangeVisibleTextEditors((editors) => {
      for (const editor of editors) {
        focus.paintVisible(editor);
      }
    }),
    vscode.window.onDidChangeActiveTextEditor(() => updateReviewStatus()),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration('archon.hostPath')) {
        void reload();
      }
      if (event.affectsConfiguration('archon.impact')) {
        impactLens.invalidateAll();
      }
    })
  );

  await startHost(context);
}

export function deactivate(): void {
  client?.dispose();
  client = undefined;
  history.clear();
  forgetRepositoryRoots();
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
  const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  if (!root) {
    setStatus('$(circle-slash) Archon: no folder open');
    return;
  }

  const hostPath = resolveHostPath(context);
  client = new ArchonClient(hostPath, (message) => log(message), () => {
    setStatus('$(error) Archon: process stopped');
  });

  try {
    client.start();
    const reply = await client.initialize(root);
    rules = reply.rules;
    tree.setRules(rules);

    log(`initialised at ${reply.root}`);
    log(reply.configPath ? `configuration: ${reply.configPath}` : 'no .archon.json found; using default severities.');
    log(`baseline: ${reply.baselineCount} accepted finding(s) from ${reply.baselinePath}`);
    for (const message of reply.messages) {
      log(message);
    }
    setStatus(`$(shield) Archon: ${rules.filter((r) => r.severity !== 'off').length}/${rules.length} rules`);

    if (vscode.workspace.getConfiguration('archon').get<boolean>('analyseWorkspaceOnStartup', false)) {
      await analyzeWorkspace();
    } else if (vscode.window.activeTextEditor) {
      await analyzeDocument(vscode.window.activeTextEditor.document);
    }
  } catch (error) {
    setStatus('$(error) Archon: failed to start');
    log(`could not start the analysis process at ${hostPath}: ${describe(error)}`);
    log('Check that the .NET runtime is installed and on PATH, or set archon.hostPath.');
  }
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
  if (debounce) {
    clearTimeout(debounce);
  }
  debounce = setTimeout(
    () => void analyzeDocument(document),
    settings.get<number>('debounceMilliseconds', 400)
  );
}

async function analyzeDocument(document: vscode.TextDocument): Promise<void> {
  if (!client?.isRunning || !isSupported(document)) {
    return;
  }
  try {
    const reply = await client.analyzeFile(
      document.uri.fsPath,
      document.isDirty ? document.getText() : undefined
    );
    applyForFile(document.uri, reply);
    reportSkipped(reply);
  } catch (error) {
    log(`could not analyse ${document.uri.fsPath}: ${describe(error)}`);
  }
}

async function analyzeWorkspace(): Promise<void> {
  if (!client?.isRunning) {
    return;
  }
  await vscode.window.withProgress(
    { location: vscode.ProgressLocation.Window, title: 'Archon: analysing workspace' },
    async () => {
      try {
        const reply = await client!.analyzeWorkspace();
        applyForWorkspace(reply);
        reportSkipped(reply);
        log(
          `workspace pass: ${reply.findings.length} finding(s) across ${reply.filesAnalysed} file(s) in ${reply.elapsedMilliseconds} ms` +
            (reply.baselinedCount > 0 ? `, ${reply.baselinedCount} baselined and not counted` : '')
        );
      } catch (error) {
        log(`workspace analysis failed: ${describe(error)}`);
      }
    }
  );
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
    tree.setRules(rules);
    for (const message of reply.messages) {
      log(message);
    }
    setStatus(`$(shield) Archon: ${rules.filter((r) => r.severity !== 'off').length}/${rules.length} rules`);
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

  try {
    await client.setSeverity(rule.id, target);
    const updated = await client.listRules();
    rules = updated.rules;
    tree.setRules(rules);
    setStatus(`$(shield) Archon: ${rules.filter((r) => r.severity !== 'off').length}/${rules.length} rules`);
    log(`${rule.id} set to ${target} for this session. Add it to .archon.json to make it permanent.`);
    if (vscode.window.activeTextEditor) {
      await analyzeDocument(vscode.window.activeTextEditor.document);
    }
  } catch (error) {
    log(`could not change ${rule.id}: ${describe(error)}`);
  }
}

async function explainRule(node?: Node): Promise<void> {
  const rule = await resolveRule(node);
  if (!rule) {
    return;
  }
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
  diagnostics.delete(uri);
  findingsByFile.delete(uri.fsPath);
  applyByFile(reply.findings);
  updateCounts(reply.findings);
  updateReviewStatus();
}

function applyForWorkspace(reply: AnalysisReply): void {
  diagnostics.clear();
  findingsByFile = new Map<string, FindingInfo[]>();
  applyByFile(reply.findings);
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

function toDiagnostic(finding: FindingInfo): vscode.Diagnostic {
  const range = new vscode.Range(
    Math.max(0, finding.startLine),
    Math.max(0, finding.startColumn),
    Math.max(0, finding.endLine),
    Math.max(0, finding.endColumn)
  );
  const diagnostic = new vscode.Diagnostic(range, finding.message, toSeverity(finding.severity));
  diagnostic.source = 'archon';
  diagnostic.code = finding.ruleId;
  return diagnostic;
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

function setStatus(text: string): void {
  status.text = text;
  status.tooltip = 'Archon — select to open the log';
  status.show();
}

function log(message: string): void {
  output.appendLine(`[${new Date().toISOString()}] ${message}`);
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
