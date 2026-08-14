import * as child_process from 'child_process';
import * as readline from 'readline';

export interface RuleInfo {
  id: string;
  title: string;
  category: string;
  description: string;
  scope: string;
  language: string;
  defaultSeverity: string;
  severity: string;
  pack: string;
  snippetId?: string;
  snippetTitle?: string;
  snippetWhy?: string;
}

export interface FindingInfo {
  ruleId: string;
  severity: string;
  category: string;
  kind: string | null;
  message: string;
  file: string;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
  fingerprint: string;
}

export interface AnalysisReply {
  scope: string | null;
  findings: FindingInfo[];
  baselinedCount: number;
  failedRules: { ruleId: string; reason: string }[];
  diagnostics: string[];
  filesAnalysed: number;
  elapsedMilliseconds: number;
}

export interface CallerInfo {
  methodName: string;
  file: string;
  line: number;
  column: number;
}

export interface MethodImpactInfo {
  methodName: string;
  arity: number;
  line: number;
  column: number;
  referenceCount: number;
  projectCount: number;
  coveringTestCount: number;
  depthBounded: boolean;
  callers: CallerInfo[];
}

export interface ImpactReply {
  scope: string;
  methods: MethodImpactInfo[];
  graphMethodCount: number;
  graphFileCount: number;
  elapsedMilliseconds: number;
}

export interface TraceEdgeInfo {
  fromKey: string;
  fromName: string;
  toKey: string;
  toName: string;
}

export interface TraceReply {
  found: boolean;
  rootKey?: string;
  rootName?: string;
  edges?: TraceEdgeInfo[];
  bounded?: boolean;
  /** Nodes the walk showed but declined to expand, their name matching several declarations. */
  ambiguousKeys?: string[];
  elapsedMilliseconds: number;
}

export interface FormatReply {
  path: string;
  formatted: string;
  changed: boolean;
  hasInlineComments: boolean;
}

export interface InitializeReply {
  root: string;
  configPath: string | null;
  baselinePath: string;
  baselineCount: number;
  rules: RuleInfo[];
  messages: string[];
}

interface Pending {
  resolve: (value: unknown) => void;
  reject: (reason: Error) => void;
  timer: NodeJS.Timeout;
}

/**
 * How long a request may take before it is abandoned. A request that never settles would otherwise
 * hold the queue behind it for the rest of the session, since the process answers one at a time.
 * A whole-workspace pass is given far longer than an interactive one, because on a large repository
 * it legitimately takes minutes.
 */
const REQUEST_TIMEOUT_MS = 60_000;
const WORKSPACE_TIMEOUT_MS = 900_000;

/** How long to wait for the process to stop on its own before ending it. */
const SHUTDOWN_GRACE_MS = 2_000;

/**
 * Owns the single analysis process and serialises requests onto it. Requests queue rather than
 * overlap, because the process answers one at a time; queueing here keeps that contract in one
 * place instead of spreading it across every caller.
 */
export class ArchonClient {
  private process: child_process.ChildProcessWithoutNullStreams | undefined;
  private readonly pending = new Map<number, Pending>();
  private queue: Promise<unknown> = Promise.resolve();
  private nextId = 1;
  private stopped = false;

  constructor(
    private readonly hostPath: string,
    private readonly log: (message: string) => void,
    private readonly onExit: (code: number | null) => void
  ) {}

  start(): void {
    this.stopped = false;
    this.process = child_process.spawn('dotnet', [this.hostPath], {
      stdio: ['pipe', 'pipe', 'pipe']
    });

    const reader = readline.createInterface({ input: this.process.stdout });
    reader.on('line', (line) => this.receive(line));

    this.process.stderr.on('data', (chunk: Buffer) => {
      this.log(`host stderr: ${chunk.toString().trimEnd()}`);
    });

    // Without this, a missing `dotnet` raises an unhandled 'error' event rather than reporting the
    // one failure most likely to happen. The same handler covers a stdin write to a dead process.
    this.process.on('error', (error: Error) => {
      this.failAll(new Error(`the analysis process could not be started: ${error.message}`));
      this.process = undefined;
      if (!this.stopped) {
        this.log(`could not run 'dotnet ${this.hostPath}': ${error.message}`);
        this.onExit(null);
      }
    });
    this.process.stdin.on('error', (error: Error) => {
      this.log(`could not write to the host: ${error.message}`);
    });

    this.process.on('exit', (code) => {
      this.failAll(new Error('the analysis process exited'));
      this.process = undefined;
      if (!this.stopped) {
        this.log(`host exited unexpectedly with code ${code ?? 'unknown'}.`);
        this.onExit(code);
      }
    });
  }

  /**
   * Asks the process to stop and waits for it, ending it if it does not. Returning immediately
   * would leave a process that is mid-analysis running after the editor has gone, still holding
   * open the very files an extension update needs to replace.
   */
  async dispose(): Promise<void> {
    this.stopped = true;
    const running = this.process;
    if (!running) {
      return;
    }
    this.process = undefined;

    const exited = new Promise<void>((resolve) => {
      running.once('exit', () => resolve());
      running.once('error', () => resolve());
    });

    try {
      running.stdin.write(JSON.stringify({ id: 0, method: 'shutdown' }) + '\n');
    } catch {
      running.kill();
      return;
    }

    let timer: NodeJS.Timeout | undefined;
    const expired = new Promise<'timeout'>((resolve) => {
      timer = setTimeout(() => resolve('timeout'), SHUTDOWN_GRACE_MS);
    });

    const outcome = await Promise.race([exited.then(() => 'exited' as const), expired]);
    if (timer) {
      clearTimeout(timer);
    }
    if (outcome === 'timeout') {
      this.log('the host did not stop when asked; ending it.');
      running.kill();
    }
    this.failAll(new Error('the analysis process was shut down'));
  }

  private failAll(reason: Error): void {
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(reason);
    }
    this.pending.clear();
  }

  get isRunning(): boolean {
    return this.process !== undefined;
  }

  initialize(root: string): Promise<InitializeReply> {
    return this.send<InitializeReply>('initialize', { root });
  }

  listRules(): Promise<{ rules: RuleInfo[] }> {
    return this.send('listRules');
  }

  analyzeFile(path: string, text?: string): Promise<AnalysisReply> {
    return this.send('analyzeFile', text === undefined ? { path } : { path, text });
  }

  analyzeWorkspace(): Promise<AnalysisReply> {
    return this.send('analyzeWorkspace', undefined, WORKSPACE_TIMEOUT_MS);
  }

  formatFile(path: string, text?: string): Promise<FormatReply> {
    return this.send('formatFile', text === undefined ? { path } : { path, text });
  }

  methodImpact(path: string, text: string | undefined, maxDepth: number): Promise<ImpactReply> {
    return this.send('methodImpact', text === undefined ? { path, maxDepth } : { path, text, maxDepth });
  }

  methodTrace(
    path: string,
    text: string | undefined,
    line: number,
    maxDepth: number,
    maxNodes: number
  ): Promise<TraceReply> {
    const base = { path, line, maxDepth, maxNodes };
    return this.send('methodTrace', text === undefined ? base : { ...base, text });
  }

  setSeverity(ruleId: string, severity: string): Promise<{ ruleId: string; severity: string }> {
    return this.send('setSeverity', { ruleId, severity });
  }

  reloadConfig(): Promise<InitializeReply> {
    return this.send('reloadConfig');
  }

  writeBaseline(): Promise<{ path: string; recorded: number }> {
    return this.send('writeBaseline', undefined, WORKSPACE_TIMEOUT_MS);
  }

  /**
   * Forgets cached content for files changed outside the editor. `structural` marks files that
   * appeared or disappeared, which also retires the discovered file set.
   */
  invalidate(paths: string[], structural = false): Promise<unknown> {
    return this.send('invalidate', { paths, structural });
  }

  private send<T>(method: string, params?: unknown, timeoutMs = REQUEST_TIMEOUT_MS): Promise<T> {
    const run = () =>
      new Promise<T>((resolve, reject) => {
        if (!this.process) {
          reject(new Error('the analysis process is not running'));
          return;
        }
        const id = this.nextId++;
        const timer = setTimeout(() => {
          this.pending.delete(id);
          this.log(`'${method}' did not answer within ${Math.round(timeoutMs / 1000)}s; abandoning it.`);
          reject(new Error(`'${method}' timed out`));
        }, timeoutMs);

        this.pending.set(id, {
          resolve: resolve as (value: unknown) => void,
          reject,
          timer
        });
        const payload = params === undefined ? { id, method } : { id, method, params };
        this.process.stdin.write(JSON.stringify(payload) + '\n');
      });

    const result = this.queue.then(run, run);
    this.queue = result.catch(() => undefined);
    return result;
  }

  private receive(line: string): void {
    if (!line.trim()) {
      return;
    }
    let reply: { id: number; ok: boolean; result?: unknown; error?: string };
    try {
      reply = JSON.parse(line);
    } catch {
      this.log(`could not parse a reply from the host: ${line}`);
      return;
    }
    const pending = this.pending.get(reply.id);
    if (!pending) {
      // Either a reply to a request already abandoned, or one that arrived twice. Both are safe
      // to drop; the caller has been told the request failed.
      return;
    }
    this.pending.delete(reply.id);
    clearTimeout(pending.timer);
    if (reply.ok) {
      pending.resolve(reply.result);
    } else {
      pending.reject(new Error(reply.error ?? 'the host reported an unspecified failure'));
    }
  }
}
