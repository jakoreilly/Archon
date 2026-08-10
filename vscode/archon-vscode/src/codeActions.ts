import * as vscode from 'vscode';

const COUNT_TO_ANY = 'AR0020';
const REDUNDANT_MATERIALISATION = 'AR0022';

/**
 * Quick fixes for the two AR0020/AR0022 shapes that can be rewritten from their diagnostic text
 * alone, with no symbol resolution: the engine is syntax-only, so a fix is offered only where the
 * rule's own detection already guarantees the rewrite is safe, and withheld otherwise rather than
 * guessed at.
 */
export class PerfHintCodeActionProvider implements vscode.CodeActionProvider {
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
      const action = this.actionFor(document, diagnostic);
      if (action) {
        actions.push(action);
      }
    }
    return actions;
  }

  private actionFor(document: vscode.TextDocument, diagnostic: vscode.Diagnostic): vscode.CodeAction | undefined {
    // diagnostic.code is a plain rule id, or { value, target } once snippets.uriTemplate links it.
    const code =
      typeof diagnostic.code === 'object' && diagnostic.code !== null ? String(diagnostic.code.value) : diagnostic.code;
    switch (code) {
      case COUNT_TO_ANY:
        return this.fixCountToAny(document, diagnostic);
      case REDUNDANT_MATERIALISATION:
        return this.fixRedundantMaterialisation(document, diagnostic);
      default:
        return undefined;
    }
  }

  private fixCountToAny(document: vscode.TextDocument, diagnostic: vscode.Diagnostic): vscode.CodeAction | undefined {
    const text = document.getText(diagnostic.range);
    const negate = diagnostic.message.includes("'!sequence.Any()'");
    const replacement = countToAnyReplacement(text, negate);
    if (replacement === undefined) {
      return undefined;
    }
    return this.buildAction(
      document,
      diagnostic,
      `Replace with '${negate ? '!' : ''}...Any()'`,
      replacement
    );
  }

  private fixRedundantMaterialisation(document: vscode.TextDocument, diagnostic: vscode.Diagnostic): vscode.CodeAction | undefined {
    const text = document.getText(diagnostic.range);
    const match = /\.(ToList|ToArray)\(\)/.exec(text);
    if (!match) {
      return undefined;
    }
    const replacement = text.slice(0, match.index) + text.slice(match.index + match[0].length);
    return this.buildAction(document, diagnostic, `Drop '.${match[1]}()'`, replacement);
  }

  private buildAction(
    document: vscode.TextDocument,
    diagnostic: vscode.Diagnostic,
    title: string,
    replacement: string
  ): vscode.CodeAction {
    const action = new vscode.CodeAction(title, vscode.CodeActionKind.QuickFix);
    action.edit = new vscode.WorkspaceEdit();
    action.edit.replace(document.uri, diagnostic.range, replacement);
    action.diagnostics = [diagnostic];
    action.isPreferred = true;
    return action;
  }
}

/**
 * Handles only `target.Count() <op> literal` and its reverse. Anything else AR0020 might one day
 * match (a member access rather than an invocation, say) is left without a fix rather than risking
 * a rewrite this text-only pass cannot verify.
 */
function countToAnyReplacement(text: string, negate: boolean): string | undefined {
  const forward = /^([\s\S]*)\.Count\(\)\s*(?:==|!=|>=|<=|>|<)\s*\d+$/.exec(text);
  if (forward) {
    return (negate ? '!' : '') + forward[1] + '.Any()';
  }
  const reversed = /^\d+\s*(?:==|!=|>=|<=|>|<)\s*([\s\S]*)\.Count\(\)$/.exec(text);
  if (reversed) {
    return (negate ? '!' : '') + reversed[1] + '.Any()';
  }
  return undefined;
}
