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
}

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

    this.process.on('exit', (code) => {
      const failed = new Error('the analysis process exited');
      for (const pending of this.pending.values()) {
        pending.reject(failed);
      }
      this.pending.clear();
      this.process = undefined;
      if (!this.stopped) {
        this.log(`host exited unexpectedly with code ${code ?? 'unknown'}.`);
        this.onExit(code);
      }
    });
  }

  dispose(): void {
    this.stopped = true;
    if (!this.process) {
      return;
    }
    try {
      this.process.stdin.write(JSON.stringify({ id: 0, method: 'shutdown' }) + '\n');
    } catch {
      this.process.kill();
    }
    this.process = undefined;
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
    return this.send('analyzeWorkspace');
  }

  methodImpact(path: string, text: string | undefined, maxDepth: number): Promise<ImpactReply> {
    return this.send('methodImpact', text === undefined ? { path, maxDepth } : { path, text, maxDepth });
  }

  setSeverity(ruleId: string, severity: string): Promise<{ ruleId: string; severity: string }> {
    return this.send('setSeverity', { ruleId, severity });
  }

  reloadConfig(): Promise<InitializeReply> {
    return this.send('reloadConfig');
  }

  writeBaseline(): Promise<{ path: string; recorded: number }> {
    return this.send('writeBaseline');
  }

  invalidate(path: string): Promise<unknown> {
    return this.send('invalidate', { path });
  }

  private send<T>(method: string, params?: unknown): Promise<T> {
    const run = () =>
      new Promise<T>((resolve, reject) => {
        if (!this.process) {
          reject(new Error('the analysis process is not running'));
          return;
        }
        const id = this.nextId++;
        this.pending.set(id, {
          resolve: resolve as (value: unknown) => void,
          reject
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
      return;
    }
    this.pending.delete(reply.id);
    if (reply.ok) {
      pending.resolve(reply.result);
    } else {
      pending.reject(new Error(reply.error ?? 'the host reported an unspecified failure'));
    }
  }
}
