import * as vscode from 'vscode';
import { ArchonClient, MethodImpactInfo, TraceEdgeInfo } from './client';

/**
 * One panel, reused across requests. A trace is a "look at this, then close it" tool rather than a
 * persistent view, so keeping several open would only leave stale diagrams behind after an edit.
 */
let panel: vscode.WebviewPanel | undefined;

/**
 * Traces the caller chain reaching one method and renders it as a Mermaid diagram, out to the
 * configured depth and node count. Unlike `showCallers`, which lists only direct callers, this walks
 * callers of callers — the shape a "what actually reaches this method" question needs.
 */
export async function showCallTrace(
  client: () => ArchonClient | undefined,
  extensionUri: vscode.Uri,
  log: (message: string) => void,
  uri: vscode.Uri,
  method: MethodImpactInfo
): Promise<void> {
  const activeClient = client();
  if (!activeClient?.isRunning) {
    vscode.window.showWarningMessage('Archon: the analysis process is not running.');
    return;
  }

  const settings = vscode.workspace.getConfiguration('archon');
  const maxDepth = settings.get<number>('impact.maxDepth', 6);
  const maxNodes = settings.get<number>('trace.maxNodes', 60);
  const document = vscode.workspace.textDocuments.find((open) => open.uri.toString() === uri.toString());

  let reply;
  try {
    reply = await activeClient.methodTrace(
      uri.fsPath,
      document?.isDirty ? document.getText() : undefined,
      method.line,
      maxDepth,
      maxNodes
    );
  } catch (error) {
    vscode.window.showErrorMessage(
      `Archon: could not trace callers of ${method.methodName}: ${error instanceof Error ? error.message : String(error)}`
    );
    return;
  }

  if (!reply.found || !reply.edges) {
    vscode.window.showInformationMessage(`Archon: could not find ${method.methodName} to trace.`);
    return;
  }

  const rootKey = reply.rootKey ?? method.methodName;
  const rootName = reply.rootName ?? method.methodName;
  reveal(extensionUri, rootKey, rootName, reply.edges, reply.bounded ?? false);
  log(`traced ${reply.edges.length} call edge(s) reaching ${method.methodName} in ${reply.elapsedMilliseconds}ms`);
}

function reveal(extensionUri: vscode.Uri, rootKey: string, rootName: string, edges: TraceEdgeInfo[], bounded: boolean): void {
  if (!panel) {
    panel = vscode.window.createWebviewPanel('archonCallTrace', 'Archon: Call Trace', vscode.ViewColumn.Beside, {
      enableScripts: true,
      localResourceRoots: [vscode.Uri.joinPath(extensionUri, 'media')],
      retainContextWhenHidden: true
    });
    panel.onDidDispose(() => {
      panel = undefined;
    });
  } else {
    panel.reveal(vscode.ViewColumn.Beside);
  }

  panel.title = `Archon: Callers of ${rootName}`;
  panel.webview.html = renderHtml(panel.webview, extensionUri, rootKey, rootName, edges, bounded);
}

/** Mermaid node IDs must be identifiers; the graph key (`Name/Arity`) is close enough once sanitised. */
function nodeId(key: string): string {
  return `n_${key.replace(/[^A-Za-z0-9_]/g, '_')}`;
}

function escapeLabel(name: string): string {
  return name.replace(/"/g, '&quot;');
}

function buildDiagram(rootKey: string, edges: TraceEdgeInfo[]): string {
  const lines = ['graph TD'];
  const declared = new Set<string>();

  const declare = (key: string, name: string) => {
    if (declared.has(key)) {
      return;
    }
    declared.add(key);
    lines.push(`  ${nodeId(key)}["${escapeLabel(name)}"]`);
  };

  for (const edge of edges) {
    declare(edge.fromKey, edge.fromName);
    declare(edge.toKey, edge.toName);
    lines.push(`  ${nodeId(edge.fromKey)} --> ${nodeId(edge.toKey)}`);
  }

  lines.push(`  class ${nodeId(rootKey)} archonRoot`);
  lines.push('  classDef archonRoot fill:#5b8def,stroke:#2f5fc4,color:#ffffff,font-weight:bold;');
  return lines.join('\n');
}

function renderHtml(
  webview: vscode.Webview,
  extensionUri: vscode.Uri,
  rootKey: string,
  rootName: string,
  edges: TraceEdgeInfo[],
  bounded: boolean
): string {
  const script = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'mermaid.min.js'));
  const nonce = Array.from({ length: 32 }, () => Math.floor(Math.random() * 36).toString(36)).join('');

  if (edges.length === 0) {
    return `<!DOCTYPE html>
<html><body>
<p>Archon found no callers of <strong>${escapeLabel(rootName)}</strong> within the configured depth.</p>
</body></html>`;
  }

  const diagram = buildDiagram(rootKey, edges);
  const boundedNote = bounded
    ? `<p class="note">Cut off at the configured depth/node limit — this is part of the chain, not all of it.</p>`
    : '';

  return `<!DOCTYPE html>
<html>
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}' ${webview.cspSource};">
<style>
  body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); padding: 0 12px; }
  h2 { font-weight: 600; }
  .note { opacity: 0.75; font-size: 0.9em; }
  .mermaid { text-align: center; }
</style>
</head>
<body>
<h2>Callers reaching ${escapeLabel(rootName)}</h2>
<p class="note">Matched by name and argument count, not resolved symbols — an approximation, not an exact call graph.</p>
${boundedNote}
<pre class="mermaid">${diagram}</pre>
<script nonce="${nonce}" src="${script}"></script>
<script nonce="${nonce}">
  const theme = document.body.classList.contains('vscode-dark') || document.body.classList.contains('vscode-high-contrast')
    ? 'dark'
    : 'default';
  mermaid.initialize({ startOnLoad: true, theme, securityLevel: 'strict' });
</script>
</body>
</html>`;
}

