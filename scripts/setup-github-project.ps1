<#
.SYNOPSIS
    GitHub Project V2 Control Plane Bootstrap, Field Provisioning, and Auto-Sync (Issue #50).

.DESCRIPTION
    Validates .github/project-v2-schema.json, creates/links the repository's GitHub Project V2
    board, configures custom single-select fields (Priority, Backlog Type, Area, Target Milestone,
    Effort) and Status options (Todo, In Progress, In Review, Done), and backfills or syncs
    issues and pull requests across Milestones #1-#3.

.PARAMETER DryRun
    Validates the schema manifest and runs a local classification preview without mutating GitHub.

.PARAMETER Bootstrap
    Ensures the GitHub Project V2 board exists, is linked to the repository, and contains all
    custom fields and options defined in .github/project-v2-schema.json.

.PARAMETER Backfill
    Enrolls and populates metadata for all repository issues across Milestones #1-#3.

.PARAMETER Repo
    Target repository in owner/name format (default: aleon1220/fde-sharp-backlog).

.PARAMETER ProjectNumber
    Optional explicit GitHub Project V2 number override.
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$Bootstrap,
    [switch]$Backfill,
    [string]$Repo = "aleon1220/fde-sharp-backlog",
    [int]$ProjectNumber = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$SchemaPath = Join-Path $RepoRoot ".github\project-v2-schema.json"

if (-not (Test-Path -LiteralPath $SchemaPath)) {
    throw "Project V2 schema manifest not found at '$SchemaPath'."
}

$Schema = Get-Content -LiteralPath $SchemaPath -Raw | ConvertFrom-Json

function Get-ItemClassification {
    param(
        [string]$Title,
        [string]$Body,
        [string]$State = "OPEN",
        [int]$MilestoneNumber = 0,
        [string]$MilestoneTitle = "",
        [string[]]$Labels = @(),
        [bool]$IsPullRequest = $false,
        [bool]$IsDraft = $false,
        [bool]$IsMerged = $false
    )

    $status = "Todo"
    $upperState = $State.ToUpperInvariant()
    if ($IsPullRequest) {
        if ($IsMerged -or $upperState -eq "MERGED" -or $upperState -eq "CLOSED") {
            $status = "Done"
        } elseif ($IsDraft) {
            $status = "In Progress"
        } else {
            $status = "In Review"
        }
    } elseif ($upperState -eq "CLOSED") {
        $status = "Done"
    }

    $labelsJoined = ($Labels -join ",").ToLowerInvariant()
    $backlogType = "Type02: Schedule"
    if ($Title -match "\[Baseline" -or $labelsJoined -match "baseline" -or $Body -match "Baseline/Audit") {
        $backlogType = "Baseline/Audit"
    } elseif ($Title -match "\(Type02\)" -or $Body -match "\(Type02\)" -or $Body -match "Jira - schedule" -or $labelsJoined -match "type02") {
        $backlogType = "Type02: Schedule"
    } elseif ($Title -match "\(Type01\)" -or $Body -match "\(Type01\)" -or $Body -match "Jira - complete" -or $Body -match "## Acceptance Criteria" -or $labelsJoined -match "type01") {
        $backlogType = "Type01: Complete"
    }

    $priority = "P2"
    if ($labelsJoined -match "\b(p[0-3])\b") {
        $priority = $Matches[1].ToUpperInvariant()
    } elseif ($Body -match "(?:Priority\s*\**:\**\s*|\[|\b)(P[0-3])\b") {
        $priority = $Matches[1].ToUpperInvariant()
    } elseif ($Title -match "\b(P[0-3])\b") {
        $priority = $Matches[1].ToUpperInvariant()
    } elseif ($backlogType -eq "Baseline/Audit") {
        $priority = "P2"
    } elseif ($Title -match "UAT|Security|Mask") {
        $priority = "P0"
    } elseif ($MilestoneNumber -in @(1, 2)) {
        $priority = "P1"
    }

    $area = "Docs/GitHub"
    $combined = "$Title`n$Body"
    if ($labelsJoined -match "area:cli") {
        $area = "CLI/TUI"
    } elseif ($labelsJoined -match "area:ai") {
        $area = "AI Providers"
    } elseif ($labelsJoined -match "area:sre|area:devops") {
        $area = "SRE/DevOps"
    } elseif ($labelsJoined -match "area:prompt") {
        $area = "Prompt Library"
    } elseif ($labelsJoined -match "area:docs|area:github") {
        $area = "Docs/GitHub"
    } elseif ($Title -match "\[GitHub Project|Issue Templates|README") {
        $area = "Docs/GitHub"
    } elseif ($Title -match "fde-AI-prompts|Prompt Scanner|Prompt Generator|Prompt Template|Goal Expansion") {
        $area = "Prompt Library"
    } elseif ($Title -match "NuGet|Binary|Logging|Podman|Container|Kubernetes|Dependabot|Branch Protection|Secret|CI/CD") {
        $area = "SRE/DevOps"
    } elseif ($Title -match "Gemini|Copilot|OpenAI|Provider|BYOK|Grounding|MCP|Evaluator") {
        $area = "AI Providers"
    } elseif ($combined -match "CLI|TUI|REPL|Wizard|Clipboard|OSC 52") {
        $area = "CLI/TUI"
    }

    $targetMilestone = "M2"
    if ($MilestoneNumber -eq 1 -or $MilestoneTitle -match "release-fde-version") {
        $targetMilestone = "M1"
    } elseif ($MilestoneNumber -eq 2 -or $MilestoneTitle -match "v1\.1\.0") {
        $targetMilestone = "M2"
    } elseif ($MilestoneNumber -eq 3 -or $MilestoneTitle -match "v2\.0\.0") {
        $targetMilestone = "M3"
    } elseif ($Title -match "v2\.0" -or $Body -match "Milestone #3") {
        $targetMilestone = "M3"
    } elseif ($Body -match "Milestone #1" -or $Body -match "release-fde-version") {
        $targetMilestone = "M1"
    }

    $effort = "S"
    if ($labelsJoined -match "\beffort:xs\b") {
        $effort = "XS"
    } elseif ($labelsJoined -match "\beffort:s\b") {
        $effort = "S"
    } elseif ($labelsJoined -match "\beffort:m\b") {
        $effort = "M"
    } elseif ($labelsJoined -match "\beffort:l\b") {
        $effort = "L"
    } elseif ($backlogType -eq "Baseline/Audit") {
        $effort = "XS"
    } elseif ($targetMilestone -eq "M3" -or $Title -match "Epic|v2\.0") {
        $effort = "L"
    } elseif ($backlogType -eq "Type01: Complete" -or $Body.Length -gt 1400) {
        $effort = "M"
    }

    return [PSCustomObject]@{
        Status          = $status
        Priority        = $priority
        BacklogType     = $backlogType
        Area            = $area
        TargetMilestone = $targetMilestone
        Effort          = $effort
    }
}

if ($DryRun -or (-not $Bootstrap -and -not $Backfill)) {
    Write-Host "=== GitHub Project V2 Schema & Classification Dry-Run ($Repo) ===" -ForegroundColor Cyan
    Write-Host ("Schema Version : {0}" -f $Schema.schemaVersion)
    Write-Host ("Default Title  : {0}" -f $Schema.project.defaultTitle)
    Write-Host ("Status Options : {0}" -f (($Schema.statusField.options | ForEach-Object { $_.name }) -join ", "))
    $fieldSummaries = $Schema.customFields | ForEach-Object {
        "{0} [{1}]" -f $_.name, (($_.options | ForEach-Object { $_.name }) -join "|")
    }
    Write-Host ("Custom Fields  : {0}" -f ($fieldSummaries -join "; "))
    $viewSummaries = $Schema.views | ForEach-Object {
        "{0} ({1}, group={2}, filter={3})" -f $_.name, $_.layout, $_.groupBy, $_.filter
    }
    Write-Host ("Saved Views    : {0}" -f ($viewSummaries -join "; "))

    $sample = Get-ItemClassification `
        -Title "[Jira - complete (Type01)] [GitHub Project 1/3] Configure GitHub Project V2 Custom Fields, Views, and Automated Workflow Sync" `
        -Body "**Priority:** P1 | **Persona:** Senior PM / DevOps`n## Acceptance Criteria" `
        -State "OPEN" `
        -MilestoneNumber 1 `
        -MilestoneTitle "release-fde-version"
    Write-Host ("Sample Issue #50 Classification: {0}" -f ($sample | ConvertTo-Json -Compress))
    Write-Host "✓ Dry-run validation succeeded." -ForegroundColor Green
    exit 0
}

$bashCmd = Get-Command bash -ErrorAction SilentlyContinue
if (-not $bashCmd) {
    throw "bash is required to execute live GraphQL mutations via scripts/setup-github-project.sh."
}

$forwardArgs = @("--repo", $Repo)
if ($ProjectNumber -gt 0) {
    $forwardArgs += @("--project-number", "$ProjectNumber")
}
if ($Bootstrap) {
    $forwardArgs += "--bootstrap"
}
if ($Backfill) {
    $forwardArgs += "--backfill"
}

& bash (Join-Path $PSScriptRoot "setup-github-project.sh") @forwardArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
