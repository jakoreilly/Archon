import * as vscode from 'vscode';

/**
 * A plain rule id, unless archon.snippets.uriTemplate is set and the rule maps to a library
 * pattern — in which case diagnostic.code is `{ value, target }` instead of a bare string. Shared
 * by every code action provider that needs to recover the rule id a diagnostic was raised for.
 */
export function ruleIdOf(diagnostic: vscode.Diagnostic): string | undefined {
  const code = diagnostic.code;
  if (typeof code === 'string') {
    return code;
  }
  if (typeof code === 'object' && code !== null && 'value' in code) {
    return String((code as { value: unknown }).value);
  }
  return undefined;
}
