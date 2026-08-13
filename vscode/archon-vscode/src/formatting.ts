import * as vscode from 'vscode';
import { ArchonClient } from './client';

/**
 * Formats a T-SQL document by asking the host to run its loss-safe formatter and replacing the
 * whole document with the result. A single full-document edit rather than a set of smaller ones:
 * the formatter reflows layout throughout the file, so a minimal diff would not be materially
 * smaller and would cost more to compute for no benefit.
 */
export class SqlFormattingEditProvider implements vscode.DocumentFormattingEditProvider {
  constructor(
    private readonly client: () => ArchonClient | undefined,
    private readonly log: (message: string) => void
  ) {}

  async provideDocumentFormattingEdits(document: vscode.TextDocument): Promise<vscode.TextEdit[]> {
    const client = this.client();
    if (!client?.isRunning) {
      return [];
    }

    try {
      const reply = await client.formatFile(document.uri.fsPath, document.getText());
      if (!reply.changed) {
        return [];
      }
      const fullRange = document.validateRange(new vscode.Range(0, 0, document.lineCount, 0));
      return [vscode.TextEdit.replace(fullRange, reply.formatted)];
    } catch (error) {
      this.log(`could not format ${document.uri.fsPath}: ${error instanceof Error ? error.message : String(error)}`);
      return [];
    }
  }
}
