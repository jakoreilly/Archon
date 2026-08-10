import * as vscode from 'vscode';
import { ruleIdOf } from './diagnosticCode';

const LINE_MARKER = 'archon-ignore';
const FILE_MARKER = 'archon-ignore-file';

/**
 * Offers to suppress an Archon finding, or to change the rule's severity, directly from the
 * lightbulb. The engine already honours `archon-ignore` uniformly across every rule, and a
 * severity change is one host call away — the only thing missing was a way to reach either
 * without leaving the editor for the README or a hand-edited configuration file.
 *
 * Registered for every language Archon analyses, unlike {@link PerfHintCodeActionProvider}, which
 * is C#-only because its rewrites are: suppression and severity apply identically to a SQL finding.
 */
export class SuppressionCodeActionProvider implements vscode.CodeActionProvider {
  public static readonly providedCodeActionKinds = [vscode.CodeActionKind.QuickFix];

  public provideCodeActions(
    document: vscode.TextDocument,
    _range: vscode.Range | vscode.Selection,
    context: vscode.CodeActionContext
  ): vscode.CodeAction[] {
    const actions: vscode.CodeAction[] = [];
    for (const diagnostic of context.diagnostics) {
      if (diagnostic.source !== 'archon') {
        continue;
      }
      const ruleId = ruleIdOf(diagnostic);
      if (!ruleId) {
        continue;
      }
      actions.push(
        this.suppressOnLine(document, diagnostic, ruleId),
        this.suppressInFile(document, diagnostic, ruleId),
        this.disableEverywhere(diagnostic, ruleId),
        this.pickSeverity(diagnostic, ruleId)
      );
    }
    return actions;
  }

  /**
   * Inserts the marker on a new line above the finding, matching its indentation. The marker
   * covers its own line and the line below it, so placing it above rather than appending to the
   * finding's own line never disturbs the code the finding is about.
   */
  private suppressOnLine(document: vscode.TextDocument, diagnostic: vscode.Diagnostic, ruleId: string): vscode.CodeAction {
    const line = diagnostic.range.start.line;
    const indent = /^[ \t]*/.exec(document.lineAt(line).text)?.[0] ?? '';
    const marker = `${indent}${commentPrefix(document.languageId)} ${LINE_MARKER}[${ruleId}] \n`;

    const action = new vscode.CodeAction(`Archon: suppress ${ruleId} on this line`, vscode.CodeActionKind.QuickFix);
    action.edit = new vscode.WorkspaceEdit();
    action.edit.insert(document.uri, new vscode.Position(line, 0), marker);
    action.diagnostics = [diagnostic];
    return action;
  }

  private suppressInFile(document: vscode.TextDocument, diagnostic: vscode.Diagnostic, ruleId: string): vscode.CodeAction {
    const marker = `${commentPrefix(document.languageId)} ${FILE_MARKER}[${ruleId}]\n`;

    const action = new vscode.CodeAction(`Archon: suppress ${ruleId} in this file`, vscode.CodeActionKind.QuickFix);
    action.edit = new vscode.WorkspaceEdit();
    action.edit.insert(document.uri, new vscode.Position(0, 0), marker);
    action.diagnostics = [diagnostic];
    return action;
  }

  private disableEverywhere(diagnostic: vscode.Diagnostic, ruleId: string): vscode.CodeAction {
    const action = new vscode.CodeAction(`Archon: set ${ruleId} to 'off' for this workspace`, vscode.CodeActionKind.QuickFix);
    action.command = { command: 'archon.setSeverityForRule', title: 'Set severity', arguments: [ruleId, 'off'] };
    action.diagnostics = [diagnostic];
    return action;
  }

  private pickSeverity(diagnostic: vscode.Diagnostic, ruleId: string): vscode.CodeAction {
    const action = new vscode.CodeAction(`Archon: set ${ruleId} severity for this workspace…`, vscode.CodeActionKind.QuickFix);
    action.command = { command: 'archon.setSeverityForRule', title: 'Set severity', arguments: [ruleId] };
    action.diagnostics = [diagnostic];
    return action;
  }
}

function commentPrefix(languageId: string): string {
  return languageId === 'sql' ? '--' : '//';
}
