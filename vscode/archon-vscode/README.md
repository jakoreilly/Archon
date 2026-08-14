# Archon for VS Code

Architecture, service-lifetime and T-SQL rules for C# and SQL, reported in the Problems panel and
controlled from a rule list in the Explorer, alongside three things a rule cannot tell you: how far
a method reaches, why a line exists, and what this branch changed.

One extension covers all of it, and one analysis process serves every rule: a save re-reads and
re-parses only the file that changed, and every rule shares that single parse.

Analysis is syntax-only. No project is loaded, no build is required and no target framework has to
be installed, so a codebase that does not currently compile is still fully analysable.

## At a glance

- **Rules for C# and T-SQL** — architecture, service lifetime and SQL, in the Problems panel.
- **A rule list you can reach** — every rule with its severity and current finding count, switched
  off or re-graded in place rather than in a settings file.
- **A baseline** — accept today's findings in one step, after which only new ones fail a check, so a
  rule can be adopted on a large codebase the day it is written.
- **Impact** — roughly how many callers and covering tests a method has, above the method.
- **History** — the commit that last changed a line, on hover.
- **Review mode** — everything this branch did not change, dimmed.

## Requirements

The .NET 10 runtime, available as `dotnet` on `PATH`. The extension bundles its own analysis
binary, so nothing else needs installing. Git is needed only for the two surfaces that read history.

## What appears where

- **Problems panel** — findings, with the rule id as the diagnostic code.
- **Archon Rules**, in the Explorer — every rule grouped by category, with its severity and current
  finding count. Switch one off in place, give it a different severity, or open a description.
- **Above each C# method** — roughly how many callers it has, across how many projects, and how many
  tests reach it. Select the line to list the call sites and jump to one.
- **On hover** — the commit that last changed the line, with its author, age and message, and a link
  to the first issue key it mentions.
- **Status bar** — how many rules are on. Select it to open the log. In review mode, a second entry
  reports the current comparison.

## Reviewing a change

**Archon: Review Changes** dims everything the file has in common with the base ref, leaving only
what this branch changed, and summarises each changed run above itself. `Alt+Down` and `Alt+Up` move
between them.

The base ref is the merge base with the upstream branch, so the comparison shows what this branch
did rather than everything that happened upstream since. **Archon: Review Changes Against…** takes
any branch, tag or commit instead.

Review mode stays on until switched off and covers files opened afterwards, so it does not need
re-entering on each file during a review. While it is on, the status bar also reports how many of the
active file's findings fall inside the change.

A file with no changes is left undimmed rather than faded entirely, since an evenly faded file with
no explanation reads as a fault rather than as "nothing changed here".

## What the impact line does and does not know

Calls are matched by name and argument count. No project is loaded and nothing is compiled, so there
are no resolved symbols to match against.

- Counts are prefixed `~`, because overloads sharing a name and argument count are counted together.
- A test count is prefixed `≥` when the search depth cut it off, so a lower bound is never read as a
  total. `archon.impact.maxDepth` raises the limit.
- A method reached only through an interface, a container or reflection shows no callers. That is a
  limit of the analysis, not evidence the method is unused.

The call trace diagram carries the same ambiguity, but compounded: it follows callers of callers, so
a single common name matched loosely is enough to drag most of a codebase into the picture. It
therefore stops at any node whose name and argument count match more than one member, drawing it
dashed and suffixed `…`. Such a node has callers of its own that were deliberately not followed —
which of the members sharing that name they reach cannot be known without a compilation. The
diagram's direct callers of the traced method always agree with the **Show Callers** list.

## When it runs

Saving a `.cs` or `.sql` file re-analyses that file with the rules a single file can decide.

Rules needing the whole workspace, such as the service-lifetime rules, run on
**Archon: Analyse Whole Workspace**, because a registration and the constructor it affects are
usually in different files.

`archon.analyseOn` changes this: `save` (the default), `type` to analyse after a pause in typing,
or `manual` for nothing automatic.

## Commands

| Command | What it does |
|---|---|
| Archon: Analyse Whole Workspace | Runs every rule across every file |
| Archon: Analyse Active File | Analyses the current file on request |
| Archon: Accept Current Findings As Baseline | Accepts today's findings so only new ones fail |
| Archon: Reload Configuration And Rules | Re-reads `.archon.json` and any external rule packs |
| Archon: Set Rule Severity… | Changes one rule for this session |
| Archon: Describe Rule | Opens a description, with how to suppress or disable it |
| Archon: Review Changes (Dim Everything Unchanged) | Enters review mode; right-click a C# or SQL file to reach it without the Explorer panel |
| Archon: Why Is This Here? (Show Commit) | Shows the commit that last changed the cursor's line — the same lookup as the hover, on demand |
| Archon: Show Callers | Shows the callers of the method under the cursor — the same lookup as the code lens, on demand |
| Archon: Show Log | Opens the log |

**Analyse Active File**, **Review Changes**, **Why Is This Here?** and **Show Callers** (C# only,
on a method declaration) are also on the editor's right-click menu, so they don't require the
command palette, an editor-title icon, or the Explorer panel to reach.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `archon.hostPath` | *(empty)* | Path to `archon-host.dll`. Empty uses the bundled copy. |
| `archon.analyseOn` | `save` | `save`, `type` or `manual`. |
| `archon.debounceMilliseconds` | `400` | Pause before analysing, when `analyseOn` is `type`. |
| `archon.analyseWorkspaceOnStartup` | `false` | Run a full pass on start. Off because a large workspace takes noticeably longer than one file. |

Rule severities live in `.archon.json` at the repository root, not in editor settings, so the
editor and any build check agree. Changes made from the rule list apply to the session; the log
records the configuration entry that would make one permanent.

## Your own rules

`rulePacks` in `.archon.json` names .NET assemblies to load, each exposing an `IRulePack`. A rule
from one behaves exactly like a built-in: it appears in the rule list, takes a severity by id or by
category, honours an ignore comment, and goes into the baseline — none of which the rule implements
itself.

```json
{ "rulePacks": ["tools/TeamRules/bin/Release/net10.0/TeamRules.dll"] }
```

A pack that fails to load is reported in the log and skipped, rather than stopping analysis. There
is a worked example under `samples/` in
[the repository](https://github.com/jakoreilly/Archon#rules-outside-this-repository), which builds
and runs alongside everything else.

## Suppressing a finding

```csharp
services.AddSingleton<ICache, Cache>();  // archon-ignore[AR0002] single-threaded startup only
```

The marker applies to its own line and the line below it. `// archon-ignore-file[AR0002]` covers a
whole file.

## Nothing appears

- **No `.archon.json`** — every rule except layering still runs at its default severity. Layering
  stays silent until layers are declared, since it has nothing to check against.
- **Findings are in the baseline** — they are excluded from the Problems panel by design. The log
  reports how many were accepted; `--no-baseline` on the command line shows them all.
- **A lifetime finding is missing** — those rules need the whole workspace. Run
  **Archon: Analyse Whole Workspace**.
- **A rule is off** — check the rule list, where a disabled rule shows `off`.
- **The process did not start** — the status bar says so, and the log gives the reason. Usually
  `dotnet` is not on `PATH`.

## Installing without the marketplace

The packaged `archon-analysis-0.4.3.vsix` is self-contained and can be shared directly. Install it
with `code --install-extension archon-analysis-0.4.3.vsix`, or through
**Extensions: Install from VSIX…** in the
Command Palette. Neither Node.js nor the .NET SDK is needed to install it — only the .NET runtime
to run it.

## Licence

MIT.
