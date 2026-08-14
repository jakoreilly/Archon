import * as vscode from 'vscode';
import { ArchonClient, MethodImpactInfo, TraceEdgeInfo } from './client';

/**
 * One panel, reused across requests. A trace is a "look at this, then close it" tool rather than a
 * persistent view, so keeping several open would only leave stale diagrams behind after an edit.
 */
let panel: vscode.WebviewPanel | undefined;

/** The name of whatever is currently shown, for the export dialog's suggested file name. */
let currentRootName = 'call-trace';

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
  currentRootName = rootName;

  if (!panel) {
    panel = vscode.window.createWebviewPanel('archonCallTrace', 'Archon: Call Trace', vscode.ViewColumn.Beside, {
      enableScripts: true,
      localResourceRoots: [vscode.Uri.joinPath(extensionUri, 'media')],
      retainContextWhenHidden: true
    });
    panel.onDidDispose(() => {
      panel = undefined;
    });
    panel.webview.onDidReceiveMessage((message) => void handleExport(message));
  } else {
    panel.reveal(vscode.ViewColumn.Beside);
  }

  panel.title = `Archon: Callers of ${rootName}`;
  panel.webview.html = renderHtml(panel.webview, extensionUri, rootKey, rootName, edges, bounded);
}

/**
 * Saves a diagram the webview has already rasterised or serialised — the export button hands over
 * finished SVG markup or a PNG data URL because a webview cannot reach the filesystem itself.
 */
async function handleExport(message: unknown): Promise<void> {
  if (typeof message !== 'object' || message === null || (message as { type?: unknown }).type !== 'export') {
    return;
  }
  const { format, data } = message as { format?: unknown; data?: unknown };
  if ((format !== 'svg' && format !== 'png') || typeof data !== 'string') {
    return;
  }

  const safeName = currentRootName.replace(/[^A-Za-z0-9_-]+/g, '_').replace(/^_+|_+$/g, '') || 'call-trace';
  const uri = await vscode.window.showSaveDialog({
    defaultUri: vscode.Uri.file(`${safeName}.${format}`),
    filters: format === 'svg' ? { 'SVG image': ['svg'] } : { 'PNG image': ['png'] }
  });
  if (!uri) {
    return;
  }

  const buffer =
    format === 'svg' ? Buffer.from(data, 'utf8') : Buffer.from(data.replace(/^data:image\/png;base64,/, ''), 'base64');
  await vscode.workspace.fs.writeFile(uri, buffer);
  void vscode.window.showInformationMessage(`Archon: saved ${uri.fsPath}`);
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
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}' ${webview.cspSource}; img-src ${webview.cspSource} data:;">
<style>
  body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); padding: 0 12px; }
  h2 { font-weight: 600; }
  .note { opacity: 0.75; font-size: 0.9em; }
  .toolbar { display: flex; gap: 6px; align-items: center; margin: 8px 0; flex-wrap: wrap; }
  .toolbar button {
    background: var(--vscode-button-secondaryBackground, #3a3d41);
    color: var(--vscode-button-secondaryForeground, #ffffff);
    border: none; border-radius: 2px; padding: 4px 10px; font-size: 0.9em; cursor: pointer;
  }
  .toolbar button:hover { background: var(--vscode-button-secondaryHoverBackground, #45494e); }
  .toolbar .spacer { flex: 1; }
  .toolbar .zoom-level { opacity: 0.75; font-size: 0.85em; min-width: 3.5em; text-align: center; }
  #viewport {
    border: 1px solid var(--vscode-panel-border); border-radius: 3px;
    height: 72vh; overflow: hidden; position: relative; cursor: grab;
  }
  #viewport.dragging { cursor: grabbing; }
  #stage { transform-origin: 0 0; width: max-content; }
  .mermaid { text-align: center; }
</style>
</head>
<body>
<h2>Callers reaching ${escapeLabel(rootName)}</h2>
<p class="note">Matched by name and argument count, not resolved symbols — an approximation, not an exact call graph.</p>
${boundedNote}
<div class="toolbar">
  <button id="zoom-out" title="Zoom out">−</button>
  <span id="zoom-level" class="zoom-level">100%</span>
  <button id="zoom-in" title="Zoom in">+</button>
  <button id="zoom-reset" title="Fit to view">Fit</button>
  <span class="spacer"></span>
  <button id="export-svg" title="Save as SVG">Export SVG</button>
  <button id="export-png" title="Save as PNG">Export PNG</button>
</div>
<div id="viewport">
  <div id="stage">
    <pre class="mermaid">${diagram}</pre>
  </div>
</div>
<script nonce="${nonce}" src="${script}"></script>
<script nonce="${nonce}">
(function () {
  const vscodeApi = acquireVsCodeApi();
  const theme = document.body.classList.contains('vscode-dark') || document.body.classList.contains('vscode-high-contrast')
    ? 'dark'
    : 'default';
  mermaid.initialize({ startOnLoad: false, theme, securityLevel: 'strict' });

  const viewport = document.getElementById('viewport');
  const stage = document.getElementById('stage');
  const zoomLevel = document.getElementById('zoom-level');
  let svgEl = null;
  let scale = 1;
  let panX = 0;
  let panY = 0;

  function applyTransform() {
    stage.style.transform = 'translate(' + panX + 'px, ' + panY + 'px) scale(' + scale + ')';
    zoomLevel.textContent = Math.round(scale * 100) + '%';
  }

  function clampScale(value) {
    return Math.min(4, Math.max(0.05, value));
  }

  function svgSize() {
    const box = svgEl.viewBox && svgEl.viewBox.baseVal;
    if (box && box.width && box.height) {
      return { width: box.width, height: box.height };
    }
    return { width: svgEl.width.baseVal.value, height: svgEl.height.baseVal.value };
  }

  function fitToViewport() {
    if (!svgEl) {
      return;
    }
    const size = svgSize();
    const vw = viewport.clientWidth - 24;
    const vh = viewport.clientHeight - 24;
    scale = clampScale(Math.min(1, vw / size.width, vh / size.height));
    panX = Math.max(12, (viewport.clientWidth - size.width * scale) / 2);
    panY = 12;
    applyTransform();
  }

  function zoomBy(factor, clientX, clientY) {
    const rect = viewport.getBoundingClientRect();
    const cx = clientX === undefined ? rect.width / 2 : clientX - rect.left;
    const cy = clientY === undefined ? rect.height / 2 : clientY - rect.top;
    const previous = scale;
    scale = clampScale(scale * factor);
    panX = cx - (cx - panX) * (scale / previous);
    panY = cy - (cy - panY) * (scale / previous);
    applyTransform();
  }

  mermaid.run({ querySelector: '.mermaid' }).then(function () {
    svgEl = stage.querySelector('svg');
    if (svgEl) {
      svgEl.style.maxWidth = 'none';
      fitToViewport();
    }
  });

  viewport.addEventListener('wheel', function (event) {
    event.preventDefault();
    zoomBy(event.deltaY < 0 ? 1.1 : 1 / 1.1, event.clientX, event.clientY);
  }, { passive: false });

  let dragging = false;
  let lastX = 0;
  let lastY = 0;
  viewport.addEventListener('mousedown', function (event) {
    dragging = true;
    lastX = event.clientX;
    lastY = event.clientY;
    viewport.classList.add('dragging');
  });
  window.addEventListener('mousemove', function (event) {
    if (!dragging) {
      return;
    }
    panX += event.clientX - lastX;
    panY += event.clientY - lastY;
    lastX = event.clientX;
    lastY = event.clientY;
    applyTransform();
  });
  window.addEventListener('mouseup', function () {
    dragging = false;
    viewport.classList.remove('dragging');
  });

  document.getElementById('zoom-in').addEventListener('click', function () { zoomBy(1.25); });
  document.getElementById('zoom-out').addEventListener('click', function () { zoomBy(1 / 1.25); });
  document.getElementById('zoom-reset').addEventListener('click', fitToViewport);

  function serialisedSvg() {
    const serializer = new XMLSerializer();
    let source = serializer.serializeToString(svgEl);
    if (!/^<svg[^>]+xmlns=/.test(source)) {
      source = source.replace('<svg', '<svg xmlns="http://www.w3.org/2000/svg"');
    }
    return source;
  }

  document.getElementById('export-svg').addEventListener('click', function () {
    if (!svgEl) {
      return;
    }
    vscodeApi.postMessage({ type: 'export', format: 'svg', data: serialisedSvg() });
  });

  document.getElementById('export-png').addEventListener('click', function () {
    if (!svgEl) {
      return;
    }
    const size = svgSize();
    const scaleFactor = 2;
    const canvas = document.createElement('canvas');
    canvas.width = Math.ceil(size.width * scaleFactor);
    canvas.height = Math.ceil(size.height * scaleFactor);
    const ctx = canvas.getContext('2d');
    const background = getComputedStyle(document.body).backgroundColor;
    const image = new Image();
    image.onload = function () {
      ctx.fillStyle = background;
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.drawImage(image, 0, 0, canvas.width, canvas.height);
      vscodeApi.postMessage({ type: 'export', format: 'png', data: canvas.toDataURL('image/png') });
    };
    image.src = 'data:image/svg+xml;base64,' + btoa(unescape(encodeURIComponent(serialisedSvg())));
  });
})();
</script>
</body>
</html>`;
}

