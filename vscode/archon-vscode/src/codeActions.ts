import * as vscode from 'vscode';
import { FindingInfo } from './client';
import { ruleIdOf } from './diagnosticCode';

/**
 * Quick fixes for findings whose rule supplied a rewrite. The rewrite is decided by the engine
 * alongside the detection — a rule offers one only where what it matched already guarantees the
 * edit is safe — so this provider has nothing to work out: it turns the finding's edits into a
 * WorkspaceEdit. The same fix is what `archon check --fix` applies on the command line, which is
 * how a quick fix taken in the editor and a fix applied in CI cannot disagree.
 */
export class FixCodeActionProvider implements vscode.CodeActionProvider {
  public static readonly providedCodeActionKinds = [vscode.CodeActionKind.QuickFix];

  constructor(private readonly findingsFor: (uri: vscode.Uri) => FindingInfo[]) {}

  public provideCodeActions(
    document: vscode.TextDocument,
    _range: vscode.Range | vscode.Selection,
    context: vscode.CodeActionContext
  ): vscode.CodeAction[] {
    const actions: vscode.CodeAction[] = [];
    const findings = this.findingsFor(document.uri);
    for (const diagnostic of context.diagnostics) {
      if (diagnostic.source !== 'archon') {
        continue;
      }
      const finding = findingFor(diagnostic, findings);
      if (!finding?.fix) {
        continue;
      }
      actions.push(buildAction(document, diagnostic, finding.fix.title, finding.fix.edits));
    }
    return actions;
  }
}

/**
 * Recovers the finding a diagnostic was raised from. A diagnostic carries no reference back, so
 * the match is on what both share: rule id, start position and message. Two findings of one rule
 * with the same message at the same position are the same finding.
 */
export function findingFor(diagnostic: vscode.Diagnostic, findings: FindingInfo[]): FindingInfo | undefined {
  const ruleId = ruleIdOf(diagnostic);
  return findings.find(
    (f) =>
      f.ruleId === ruleId &&
      f.startLine === diagnostic.range.start.line &&
      f.startColumn === diagnostic.range.start.character &&
      f.message === diagnostic.message
  );
}

function buildAction(
  document: vscode.TextDocument,
  diagnostic: vscode.Diagnostic,
  title: string,
  edits: { startLine: number; startColumn: number; endLine: number; endColumn: number; newText: string }[]
): vscode.CodeAction {
  const action = new vscode.CodeAction(title, vscode.CodeActionKind.QuickFix);
  action.edit = new vscode.WorkspaceEdit();
  for (const edit of edits) {
    action.edit.replace(
      document.uri,
      new vscode.Range(edit.startLine, edit.startColumn, edit.endLine, edit.endColumn),
      edit.newText
    );
  }
  action.diagnostics = [diagnostic];
  action.isPreferred = true;
  return action;
}
