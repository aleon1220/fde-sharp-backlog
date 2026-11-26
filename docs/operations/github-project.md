# GitHub Project V2 Control Plane & Automated Sync

Declarative Project V2 schema, deterministic issue/PR classification, and automated lifecycle synchronization.

## GitHub Project V2 Control Plane & Automated Sync ([Issue #50](https://github.com/aleon1220/fde-sharp-backlog/issues/50))

The repository defines a declarative GitHub Project V2 schema ([`.github/project-v2-schema.json`](../../.github/project-v2-schema.json)), deterministic C# classification service ([`GitHubProjectSyncService`](../../src/SmartBacklogAssistant/Services/GitHubProjectSyncService.cs)), cross-platform bootstrap scripts ([`scripts/setup-github-project.sh`](../../scripts/setup-github-project.sh), [`scripts/setup-github-project.ps1`](../../scripts/setup-github-project.ps1)), and automated lifecycle sync workflow ([`.github/workflows/project-sync.yml`](../../.github/workflows/project-sync.yml)):

* **Custom Single-Select & Iteration Fields:**
  * `Status`: `Todo` (default), `In Progress`, `In Review`, `Done`
  * `Priority`: `P0`, `P1`, `P2`, `P3`
  * `Backlog Type`: `Type01: Complete`, `Type02: Schedule`, `Baseline/Audit`
  * `Area`: `CLI/TUI`, `AI Providers`, `SRE/DevOps`, `Prompt Library`, `Docs/GitHub`
  * `Target Milestone`: `M1` (`release-fde-version`), `M2` (`v1.1.0-core-capabilities-and-polish`), `M3` (`v2.0.0-managed-fde-backlog`)
  * `Effort`: `XS`, `S`, `M`, `L` (+ `Sprint` 14-day iteration field)
* **Saved Project Views:**
  1. **Active Sprint Board** (`BOARD_LAYOUT`, grouped by `Status`, sorted by `Priority:asc`, filter `is:open`)
  2. **M1 Release & UAT Gate** (`TABLE_LAYOUT`, filtered to `"Target Milestone":M1`, grouped by `Status`, sorted by `Priority:asc`)
  3. **M2 Priority Triage** (`TABLE_LAYOUT`, filtered to `"Target Milestone":M2`, grouped by `Area`, sorted by `Priority:asc`)
  4. **v2.0 Roadmap** (`TABLE_LAYOUT`, filtered to `"Target Milestone":M3`, grouped by `Area`, sorted by `Priority:asc`)
* **Automated Enrollment & PR Lifecycle Transitions ([`.github/workflows/project-sync.yml`](../../.github/workflows/project-sync.yml)):**
  * Enrolls newly opened/updated issues and PRs (`actions/add-to-project@v1.0.2` + GraphQL `addProjectV2ItemById`) and populates inferred `Priority`, `Backlog Type`, `Area`, `Target Milestone`, and `Effort`.
  * Parses linked issue references (`Closes #<n>`, `Fixes #<n>`, `Resolves #<n>`, `(#<n>)`) on PR events and transitions linked issues automatically to `In Progress` (draft PR), `In Review` (open PR), or `Done` (merged PR).

```bash
# Dry-run schema validation & classification preview (no token required)
./scripts/setup-github-project.sh --dry-run
pwsh ./scripts/setup-github-project.ps1 -DryRun

# Bootstrap Project V2 custom fields & backfill existing Milestones #1-#3 issues (requires 'project' token scope)
GH_TOKEN="$PROJECT_TOKEN" ./scripts/setup-github-project.sh --bootstrap --backfill
```

See also: [environment-variables.md](../configuration/environment-variables.md) · [BACKLOG.md](../project/BACKLOG.md) · [PLAN.md](../project/PLAN.md) · [Issue #50](https://github.com/aleon1220/fde-sharp-backlog/issues/50)
