# Changelog

## Unreleased

- Quick fixes come from the engine. A finding whose rule is certain of the rewrite now carries it,
  and the editor applies that rather than re-deriving one from the diagnostic's message text. The
  same fix is what `archon check --fix` applies, so the two cannot disagree. Fixes are offered for
  `AR0014`, `AR0015`, `AR0020`, `AR0022`, `AR0090` and — new to the editor — `SQ0021` in SQL files.
- The workspace pass logs how many baseline entries no longer match anything, so a baseline that
  has outlived its findings is noticed; `archon baseline --prune` drops them.
- Fewer false positives: `AR0070` no longer flags an unused parameter on a public method of a type
  that lists an interface, and `AR0075` no longer flags public fields on a private nested class.
- `.archon.json` may carry `overrides`: blocks of rule severities and options applied only to files
  matching their globs. The host resolves each finding's severity against its own path, so a rule
  switched off for `tests/**` is silent there and reported everywhere else.

## 0.2.1

- Documents writing your own rules. `rulePacks` was mentioned in passing and nowhere explained, so
  the first thing anyone extending Archon had to do was read the loader. There is now a worked
  example under `samples/` in the repository, which builds and runs alongside everything else.

No change to analysis.

## 0.2.0

Correctness:

- Files changed outside the editor are noticed. Switching branches, rebasing or running a code
  generator previously left every finding and every caller count describing the tree that was
  there when the process started.
- The analysis process is now waited for when the editor closes, and ended if it does not stop. One
  left running holds its own files open, which is enough to make reinstalling the extension fail.
- A missing `dotnet` is reported instead of raising an unhandled error — the one failure the
  extension is most likely to meet.
- A request that never answers is abandoned rather than holding every later request behind it.
- A finding fixed in a file other than the one saved is now cleared from the Problems panel, rather
  than remaining until the next whole-workspace pass.
- Accepting a baseline survives fixing one of several identical findings. Findings are anchored to
  the text of their line, so removing one no longer renumbers the rest and reports them as new.
  **Existing baselines should be rewritten**, since the identities they hold have changed.
- The impact line counts calls made from constructors, property accessors, indexers and local
  functions, and treats `new T(...)` as reaching T's constructor. Code that injects its
  dependencies makes most of its calls from constructors, and those were invisible.
- With `analyseOn: type`, editing one file no longer cancels the pending analysis of another.
- A mistyped base ref in review mode says so, instead of showing every file as untracked.
- A multi-root workspace says which folders are not being analysed rather than being silent.

Performance:

- The workspace file list is discovered once and held, instead of on every request. Each impact
  query walked the whole tree twice, which on a large repository cost far more than the warm call
  graph behind it saved.
- Files are attributed to projects by walking upwards from each file rather than by scanning the
  file list once per project.
- Covering-test counts are cached per graph, so a file of fifty methods no longer walks the caller
  graph fifty times.
- The parse cache is bounded and evicts the least recently used file, rather than growing for as
  long as the process runs. Unsaved editor text is never evicted.
- Changing a rule's severity no longer discards the call graph, which is derived from file content
  and cannot be affected by it.
- Editor text is compared rather than hashed on each keystroke.
- `git` runs without optional locks, so review mode cannot contend with a terminal for the index.

## 0.1.0

First release.

- C# and T-SQL rules reported in the Problems panel, with the rule id as the diagnostic code.
- **Archon Rules** in the Explorer: every rule grouped by category, with its severity and current
  finding count, switchable in place.
- An impact line above each C# method, reporting roughly how many callers it has, across how many
  projects, and how many tests reach it.
- Commit history on hover, with the author, age and message of the change that last touched the
  line, and a link to the first issue key it mentions.
- Review mode, which dims everything a file has in common with the base ref so only what this
  branch changed stays lit, and moves between changed runs with `Alt+Down` and `Alt+Up`.
- A baseline, so existing findings can be accepted in one step and only new ones fail a check.
- Analysis is syntax-only: no project is loaded and no build is required, so a codebase that does
  not currently compile is still fully analysable.
