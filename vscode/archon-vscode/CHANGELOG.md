# Changelog

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
