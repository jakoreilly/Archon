import * as vscode from 'vscode';
import { RuleInfo } from './client';

type Node = CategoryNode | RuleNode;

class CategoryNode {
  readonly kind = 'category';
  constructor(readonly name: string, readonly rules: RuleInfo[]) {}
}

class RuleNode {
  readonly kind = 'rule';
  constructor(readonly rule: RuleInfo) {}
}

/**
 * Lists every registered rule grouped by category, with its effective severity, and offers the
 * enable and disable actions. Making the whole rule set visible and switchable in one place is
 * what allows many rules to be shipped together: anything noisy can be turned off where it is
 * seen, rather than by finding and uninstalling a separate component.
 */
export class RulesTreeProvider implements vscode.TreeDataProvider<Node> {
  private readonly changed = new vscode.EventEmitter<Node | undefined>();
  readonly onDidChangeTreeData = this.changed.event;

  private rules: RuleInfo[] = [];
  private findingCounts = new Map<string, number>();

  setRules(rules: RuleInfo[]): void {
    this.rules = rules;
    this.changed.fire(undefined);
  }

  setFindingCounts(counts: Map<string, number>): void {
    this.findingCounts = counts;
    this.changed.fire(undefined);
  }

  getChildren(element?: Node): Node[] {
    if (!element) {
      const categories = [...new Set(this.rules.map((r) => r.category))].sort();
      return categories.map(
        (name) =>
          new CategoryNode(
            name,
            this.rules.filter((r) => r.category === name).sort((a, b) => a.id.localeCompare(b.id))
          )
      );
    }
    return element.kind === 'category' ? element.rules.map((rule) => new RuleNode(rule)) : [];
  }

  getTreeItem(element: Node): vscode.TreeItem {
    if (element.kind === 'category') {
      const enabled = element.rules.filter((r) => r.severity !== 'off').length;
      const item = new vscode.TreeItem(element.name, vscode.TreeItemCollapsibleState.Expanded);
      item.description = `${enabled}/${element.rules.length} on`;
      item.contextValue = 'archon.category';
      item.iconPath = new vscode.ThemeIcon('folder');
      return item;
    }

    const rule = element.rule;
    const disabled = rule.severity === 'off';
    const item = new vscode.TreeItem(`${rule.id}  ${rule.title}`, vscode.TreeItemCollapsibleState.None);
    const count = this.findingCounts.get(rule.id);
    item.description = disabled
      ? 'off'
      : count === undefined
        ? rule.severity
        : `${rule.severity} · ${count} found`;
    item.tooltip = new vscode.MarkdownString(
      [
        `**${rule.id} — ${rule.title}**`,
        '',
        rule.description,
        '',
        `- scope: \`${rule.scope}\``,
        `- language: \`${rule.language}\``,
        `- default severity: \`${rule.defaultSeverity}\``,
        `- effective severity: \`${rule.severity}\``,
        `- pack: \`${rule.pack}\``,
        '',
        `Suppress one occurrence with \`// archon-ignore[${rule.id}] reason\`.`
      ].join('\n')
    );
    item.contextValue = disabled ? 'archon.rule.disabled' : 'archon.rule.enabled';
    item.iconPath = new vscode.ThemeIcon(disabled ? 'circle-slash' : this.iconFor(rule.severity));
    item.command = {
      command: 'archon.explainRule',
      title: 'Describe',
      arguments: [element]
    };
    return item;
  }

  private iconFor(severity: string): string {
    switch (severity) {
      case 'error':
        return 'error';
      case 'warning':
        return 'warning';
      case 'information':
        return 'info';
      default:
        return 'lightbulb';
    }
  }
}

export { CategoryNode, RuleNode, Node };
