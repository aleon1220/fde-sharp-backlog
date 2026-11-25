#!/usr/bin/env bash
# ==============================================================================
# setup-github-project.sh — GitHub Project V2 Control Plane Bootstrap & Auto-Sync
# Issue #50: Configure GitHub Project V2 Custom Fields, Views, and Workflow Sync
#
# Supports:
#   --dry-run                  Validate .github/project-v2-schema.json and preview
#                              issue/PR metadata classification without mutations
#   --bootstrap                Create/link Project V2 and ensure custom fields:
#                              Status (Todo, In Progress, In Review, Done),
#                              Priority (P0, P1, P2, P3),
#                              Backlog Type (Type01: Complete, Type02: Schedule, Baseline/Audit),
#                              Area (CLI/TUI, AI Providers, SRE/DevOps, Prompt Library, Docs/GitHub),
#                              Target Milestone (M1, M2, M3),
#                              Effort (XS, S, M, L), and Sprint (Iteration)
#   --backfill                 Enroll and populate metadata for all repository
#                              issues across Milestones #1-#3
#   --sync-event <path>        Process a GitHub Actions issues / pull_request_target
#                              event payload, auto-enrolling items, populating
#                              custom fields, and transitioning linked issues
#                              (Closes #N / Fixes #N / Resolves #N) to
#                              In Progress / In Review / Done
#   --repo <owner/name>        Target repository (default: aleon1220/fde-sharp-backlog)
#   --project-number <number>  Explicit Project V2 number override
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SCHEMA_PATH="$REPO_ROOT/.github/project-v2-schema.json"

REPO_FULL_NAME="${REPO_FULL_NAME:-aleon1220/fde-sharp-backlog}"
PROJECT_NUMBER="${PROJECT_NUMBER_CONFIG:-}"
MODE="dry-run"
EVENT_PATH=""
RUN_BOOTSTRAP=false
RUN_BACKFILL=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run)
      MODE="dry-run"
      shift
      ;;
    --bootstrap)
      MODE="live"
      RUN_BOOTSTRAP=true
      shift
      ;;
    --backfill)
      MODE="live"
      RUN_BACKFILL=true
      shift
      ;;
    --sync-event)
      MODE="sync-event"
      EVENT_PATH="${2:-}"
      shift 2
      ;;
    --repo)
      REPO_FULL_NAME="${2:-}"
      shift 2
      ;;
    --project-number)
      PROJECT_NUMBER="${2:-}"
      shift 2
      ;;
    -h|--help)
      sed -n '2,28p' "$0"
      exit 0
      ;;
    *)
      echo "error: unknown option '$1'" >&2
      exit 1
      ;;
  esac
done

if [[ ! -f "$SCHEMA_PATH" ]]; then
  echo "error: Project V2 schema manifest not found at '$SCHEMA_PATH'" >&2
  exit 1
fi

OWNER="${REPO_FULL_NAME%%/*}"
REPO_NAME="${REPO_FULL_NAME##*/}"

classify_item_metadata() {
  local title="$1"
  local body="$2"
  local state="$3"
  local milestone_num="${4:-}"
  local milestone_title="${5:-}"
  local labels_csv="${6:-}"
  local is_pr="${7:-false}"
  local is_draft="${8:-false}"
  local is_merged="${9:-false}"

  local status="Todo"
  local state_upper
  state_upper="$(echo "$state" | tr '[:lower:]' '[:upper:]')"

  if [[ "$is_pr" == "true" ]]; then
    if [[ "$is_merged" == "true" || "$state_upper" == "MERGED" || "$state_upper" == "CLOSED" ]]; then
      status="Done"
    elif [[ "$is_draft" == "true" ]]; then
      status="In Progress"
    else
      status="In Review"
    fi
  else
    if [[ "$state_upper" == "CLOSED" ]]; then
      status="Done"
    else
      status="Todo"
    fi
  fi

  local backlog_type="Type02: Schedule"
  if [[ "$title" == *"[Baseline"* || "$labels_csv" == *"baseline"* || "$body" == *"Baseline/Audit"* ]]; then
    backlog_type="Baseline/Audit"
  elif [[ "$title" == *"(Type02)"* || "$body" == *"(Type02)"* || "$body" == *"Jira - schedule"* || "$labels_csv" == *"type02"* ]]; then
    backlog_type="Type02: Schedule"
  elif [[ "$title" == *"(Type01)"* || "$body" == *"(Type01)"* || "$body" == *"Jira - complete"* || "$body" == *"## Acceptance Criteria"* || "$labels_csv" == *"type01"* ]]; then
    backlog_type="Type01: Complete"
  fi

  local priority="P2"
  if [[ "$labels_csv" =~ [Pp]0 || "$body" =~ Priority[^P]*P0 || "$title" =~ [Pp]0 ]]; then
    priority="P0"
  elif [[ "$labels_csv" =~ [Pp]1 || "$body" =~ Priority[^P]*P1 || "$title" =~ [Pp]1 ]]; then
    priority="P1"
  elif [[ "$labels_csv" =~ [Pp]2 || "$body" =~ Priority[^P]*P2 || "$title" =~ [Pp]2 ]]; then
    priority="P2"
  elif [[ "$labels_csv" =~ [Pp]3 || "$body" =~ Priority[^P]*P3 || "$title" =~ [Pp]3 ]]; then
    priority="P3"
  elif [[ "$backlog_type" == "Baseline/Audit" ]]; then
    priority="P2"
  elif [[ "$title" == *"UAT"* || "$title" == *"Security"* || "$title" == *"Mask"* ]]; then
    priority="P0"
  elif [[ "$milestone_num" == "1" || "$milestone_num" == "2" ]]; then
    priority="P1"
  fi

  local area="Docs/GitHub"
  local combined="${title} ${body}"
  if [[ "$labels_csv" == *"area:cli"* ]]; then
    area="CLI/TUI"
  elif [[ "$labels_csv" == *"area:ai"* ]]; then
    area="AI Providers"
  elif [[ "$labels_csv" == *"area:sre"* || "$labels_csv" == *"area:devops"* ]]; then
    area="SRE/DevOps"
  elif [[ "$labels_csv" == *"area:prompt"* ]]; then
    area="Prompt Library"
  elif [[ "$labels_csv" == *"area:docs"* || "$labels_csv" == *"area:github"* ]]; then
    area="Docs/GitHub"
  elif [[ "$title" == *"[GitHub Project"* || "$title" == *"Issue Templates"* || "$title" == *"README"* ]]; then
    area="Docs/GitHub"
  elif [[ "$title" == *"fde-AI-prompts"* || "$title" == *"Prompt Scanner"* || "$title" == *"Prompt Generator"* || "$title" == *"Prompt Template"* || "$title" == *"Goal Expansion"* ]]; then
    area="Prompt Library"
  elif [[ "$title" == *"NuGet"* || "$title" == *"Binary"* || "$title" == *"Logging"* || "$title" == *"Podman"* || "$title" == *"Container"* || "$title" == *"Kubernetes"* || "$title" == *"Dependabot"* || "$title" == *"Branch Protection"* || "$title" == *"Secret"* ]]; then
    area="SRE/DevOps"
  elif [[ "$title" == *"Gemini"* || "$title" == *"Copilot"* || "$title" == *"OpenAI"* || "$title" == *"Provider"* || "$title" == *"BYOK"* || "$title" == *"Grounding"* || "$title" == *"MCP"* || "$title" == *"Evaluator"* ]]; then
    area="AI Providers"
  elif [[ "$combined" == *"CLI"* || "$combined" == *"TUI"* || "$combined" == *"REPL"* || "$combined" == *"Wizard"* || "$combined" == *"Clipboard"* || "$combined" == *"OSC 52"* ]]; then
    area="CLI/TUI"
  fi

  local target_milestone="M2"
  if [[ "$milestone_num" == "1" || "$milestone_title" == *"release-fde-version"* || "$body" == *"Milestone #1"* ]]; then
    target_milestone="M1"
  elif [[ "$milestone_num" == "2" || "$milestone_title" == *"v1.1.0"* ]]; then
    target_milestone="M2"
  elif [[ "$milestone_num" == "3" || "$milestone_title" == *"v2.0.0"* || "$title" == *"v2.0"* || "$body" == *"Milestone #3"* ]]; then
    target_milestone="M3"
  fi

  local effort="S"
  if [[ "$backlog_type" == "Baseline/Audit" ]]; then
    effort="XS"
  elif [[ "$target_milestone" == "M3" || "$title" == *"Epic"* || "$title" == *"v2.0"* ]]; then
    effort="L"
  elif [[ "$backlog_type" == "Type01: Complete" || ${#body} -gt 1400 ]]; then
    effort="M"
  fi

  jq -nc \
    --arg status "$status" \
    --arg priority "$priority" \
    --arg backlogType "$backlog_type" \
    --arg area "$area" \
    --arg targetMilestone "$target_milestone" \
    --arg effort "$effort" \
    '{status: $status, priority: $priority, backlogType: $backlogType, area: $area, targetMilestone: $targetMilestone, effort: $effort}'
}

extract_linked_issues() {
  local pr_title="$1"
  local pr_body="$2"
  {
    echo "$pr_body" | grep -oEi '(close[sd]?|fix(e[sd])?|resolve[sd]?)[[:space:]]+#[0-9]+' | grep -oE '[0-9]+' || true
    echo "$pr_title" | grep -oEi '(close[sd]?|fix(e[sd])?|resolve[sd]?)[[:space:]]+#[0-9]+' | grep -oE '[0-9]+' || true
    echo "$pr_title" | grep -oE '\(#[0-9]+\)' | grep -oE '[0-9]+' || true
  } | sort -n -u
}

if [[ "$MODE" == "dry-run" ]]; then
  echo "=== GitHub Project V2 Schema & Classification Dry-Run ($REPO_FULL_NAME) ==="
  jq -r '
    "Schema Version : " + .schemaVersion,
    "Default Title  : " + .project.defaultTitle,
    "Status Options : " + ([.statusField.options[].name] | join(", ")),
    "Custom Fields  : " + ([.customFields[] | .name + " [" + ([.options[].name] | join("|")) + "]"] | join("; ")),
    "Saved Views    : " + ([.views[] | .name + " (" + .layout + ", group=" + .groupBy + ", filter=" + .filter + ")"] | join("; "))
  ' "$SCHEMA_PATH"

  sample_meta="$(classify_item_metadata \
    "[Jira - complete (Type01)] [GitHub Project 1/3] Configure GitHub Project V2 Custom Fields, Views, and Automated Workflow Sync" \
    "**Priority:** P1 | **Persona:** Senior PM / DevOps\n## Acceptance Criteria" \
    "OPEN" "1" "release-fde-version" "enhancement" "false" "false" "false")"
  echo "Sample Issue #50 Classification: $sample_meta"
  linked_sample="$(extract_linked_issues "feat(project): configure Project V2 custom fields and project-sync.yml (#50)" "Closes #50" | tr '\n' ' ')"
  echo "Sample PR Linked Issues        : ${linked_sample}"
  echo "✓ Dry-run validation succeeded."
  exit 0
fi

resolve_or_create_project_id() {
  local repo_data
  repo_data="$(gh api graphql -f owner="$OWNER" -f name="$REPO_NAME" -f query='
    query($owner: String!, $name: String!) {
      repository(owner: $owner, name: $name) {
        id
        owner { id login }
        projectsV2(first: 10) {
          nodes { id number title url closed }
        }
      }
      user(login: $owner) {
        id
        projectsV2(first: 10) {
          nodes { id number title url closed }
        }
      }
    }')"

  local repo_id owner_id project_id
  repo_id="$(echo "$repo_data" | jq -r '.data.repository.id')"
  owner_id="$(echo "$repo_data" | jq -r '.data.repository.owner.id')"

  if [[ -n "$PROJECT_NUMBER" ]]; then
    project_id="$(echo "$repo_data" | jq -r --argjson num "$PROJECT_NUMBER" '
      ([.data.repository.projectsV2.nodes[]?, .data.user.projectsV2.nodes[]?] | map(select(.number == $num and .closed == false)) | first | .id) // empty')"
  else
    project_id="$(echo "$repo_data" | jq -r '
      ([.data.repository.projectsV2.nodes[]?, .data.user.projectsV2.nodes[]?] | map(select(.closed == false)) | first | .id) // empty')"
  fi

  if [[ -z "$project_id" ]]; then
    local default_title
    default_title="$(jq -r '.project.defaultTitle' "$SCHEMA_PATH")"
    echo "Creating GitHub Project V2 '$default_title' for $REPO_FULL_NAME..." >&2
    local created
    created="$(gh api graphql -f ownerId="$owner_id" -f title="$default_title" -f query='
      mutation($ownerId: ID!, $title: String!) {
        createProjectV2(input: {ownerId: $ownerId, title: $title}) {
          projectV2 { id number title url }
        }
      }')"
    project_id="$(echo "$created" | jq -r '.data.createProjectV2.projectV2.id')"
  fi

  gh api graphql -f projectId="$project_id" -f repositoryId="$repo_id" -f query='
    mutation($projectId: ID!, $repositoryId: ID!) {
      linkProjectV2ToRepository(input: {projectId: $projectId, repositoryId: $repositoryId}) {
        repository { id }
      }
    }' >/dev/null 2>&1 || true

  echo "$project_id"
}

ensure_project_fields() {
  local project_id="$1"
  local existing_fields
  existing_fields="$(gh api graphql -f projectId="$project_id" -f query='
    query($projectId: ID!) {
      node(id: $projectId) {
        ... on ProjectV2 {
          fields(first: 50) {
            nodes {
              ... on ProjectV2Field { id name dataType }
              ... on ProjectV2IterationField { id name dataType }
              ... on ProjectV2SingleSelectField {
                id
                name
                dataType
                options { id name }
              }
            }
          }
        }
      }
    }')"

  local status_field_id
  status_field_id="$(echo "$existing_fields" | jq -r '.data.node.fields.nodes[] | select(.name == "Status") | .id // empty')"
  if [[ -n "$status_field_id" ]]; then
    local status_options_json
    status_options_json="$(jq -c '.statusField.options' "$SCHEMA_PATH")"
    gh api graphql -f fieldId="$status_field_id" -f query="
      mutation {
        updateProjectV2SingleSelectField(input: {
          fieldId: \"$status_field_id\",
          options: $(echo "$status_options_json" | jq 'map({name: .name, color: .color, description: .description})' | sed 's/"name":/name:/g; s/"color": "\([A-Z]*\)"/color: \1/g; s/"description":/description:/g')
        }) {
          projectV2Field { ... on ProjectV2SingleSelectField { id name } }
        }
      }" >/dev/null 2>&1 || true
  fi

  local count
  count="$(jq '.customFields | length' "$SCHEMA_PATH")"
  for ((i = 0; i < count; i++)); do
    local field_name field_options_gql existing_id
    field_name="$(jq -r ".customFields[$i].name" "$SCHEMA_PATH")"
    existing_id="$(echo "$existing_fields" | jq -r --arg name "$field_name" '.data.node.fields.nodes[] | select(.name == $name) | .id // empty')"
    field_options_gql="$(jq -c ".customFields[$i].options | map({name: .name, color: .color, description: .description})" "$SCHEMA_PATH" | sed 's/"name":/name:/g; s/"color": "\([A-Z]*\)"/color: \1/g; s/"description":/description:/g')"

    if [[ -z "$existing_id" ]]; then
      echo "Creating custom single-select field '$field_name'..." >&2
      gh api graphql -f projectId="$project_id" -f name="$field_name" -f query="
        mutation {
          createProjectV2Field(input: {
            projectId: \"$project_id\",
            dataType: SINGLE_SELECT,
            name: \"$field_name\",
            singleSelectOptions: $field_options_gql
          }) {
            projectV2Field { ... on ProjectV2SingleSelectField { id name } }
          }
        }" >/dev/null
    fi
  done
}

sync_content_item_to_project() {
  local project_id="$1"
  local content_id="$2"
  local meta_json="$3"
  local status_override="${4:-}"

  local item_res item_id
  item_res="$(gh api graphql -f projectId="$project_id" -f contentId="$content_id" -f query='
    mutation($projectId: ID!, $contentId: ID!) {
      addProjectV2ItemById(input: {projectId: $projectId, contentId: $contentId}) {
        item { id }
      }
    }')"
  item_id="$(echo "$item_res" | jq -r '.data.addProjectV2ItemById.item.id')"

  local fields_data
  fields_data="$(gh api graphql -f projectId="$project_id" -f query='
    query($projectId: ID!) {
      node(id: $projectId) {
        ... on ProjectV2 {
          fields(first: 50) {
            nodes {
              ... on ProjectV2SingleSelectField {
                id
                name
                options { id name }
              }
            }
          }
        }
      }
    }')"

  local target_status
  target_status="${status_override:-$(echo "$meta_json" | jq -r '.status')}"

  declare -A field_map=(
    ["Status"]="$target_status"
    ["Priority"]="$(echo "$meta_json" | jq -r '.priority')"
    ["Backlog Type"]="$(echo "$meta_json" | jq -r '.backlogType')"
    ["Area"]="$(echo "$meta_json" | jq -r '.area')"
    ["Target Milestone"]="$(echo "$meta_json" | jq -r '.targetMilestone')"
    ["Effort"]="$(echo "$meta_json" | jq -r '.effort')"
  )

  for fname in "${!field_map[@]}"; do
    local opt_name="${field_map[$fname]}"
    local fid oid
    fid="$(echo "$fields_data" | jq -r --arg fn "$fname" '.data.node.fields.nodes[] | select(.name == $fn) | .id // empty')"
    oid="$(echo "$fields_data" | jq -r --arg fn "$fname" --arg on "$opt_name" '.data.node.fields.nodes[] | select(.name == $fn) | .options[]? | select(.name == $on) | .id // empty')"
    if [[ -n "$fid" && -n "$oid" ]]; then
      gh api graphql -f projectId="$project_id" -f itemId="$item_id" -f fieldId="$fid" -f optionId="$oid" -f query='
        mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
          updateProjectV2ItemFieldValue(input: {
            projectId: $projectId,
            itemId: $itemId,
            fieldId: $fieldId,
            value: { singleSelectOptionId: $optionId }
          }) {
            projectV2Item { id }
          }
        }' >/dev/null
    fi
  done
}

PROJECT_ID="$(resolve_or_create_project_id)"
ensure_project_fields "$PROJECT_ID"

if [[ "$RUN_BACKFILL" == "true" ]]; then
  echo "Backfilling repository issues into GitHub Project V2 ($PROJECT_ID)..."
  issues_json="$(gh issue list --repo "$REPO_FULL_NAME" --state all --limit 100 --json id,number,title,body,state,milestone,labels)"
  issue_count="$(echo "$issues_json" | jq 'length')"
  for ((idx = 0; idx < issue_count; idx++)); do
    row="$(echo "$issues_json" | jq -c ".[$idx]")"
    cid="$(echo "$row" | jq -r '.id')"
    inum="$(echo "$row" | jq -r '.number')"
    ititle="$(echo "$row" | jq -r '.title // ""')"
    ibody="$(echo "$row" | jq -r '.body // ""')"
    istate="$(echo "$row" | jq -r '.state // "OPEN"')"
    mnum="$(echo "$row" | jq -r '.milestone.number // ""')"
    mtitle="$(echo "$row" | jq -r '.milestone.title // ""')"
    lcsv="$(echo "$row" | jq -r '[.labels[]?.name] | join(",")')"
    meta="$(classify_item_metadata "$ititle" "$ibody" "$istate" "$mnum" "$mtitle" "$lcsv" "false" "false" "false")"
    echo "  -> Syncing Issue #$inum ($ititle): $meta"
    sync_content_item_to_project "$PROJECT_ID" "$cid" "$meta"
  done
fi

if [[ "$MODE" == "sync-event" ]]; then
  if [[ -z "$EVENT_PATH" || ! -f "$EVENT_PATH" ]]; then
    echo "error: --sync-event requires a valid GITHUB_EVENT_PATH JSON file" >&2
    exit 1
  fi

  has_pr="$(jq 'has("pull_request")' "$EVENT_PATH")"
  has_issue="$(jq 'has("issue")' "$EVENT_PATH")"

  if [[ "$has_issue" == "true" ]]; then
    cid="$(jq -r '.issue.node_id' "$EVENT_PATH")"
    inum="$(jq -r '.issue.number' "$EVENT_PATH")"
    ititle="$(jq -r '.issue.title // ""' "$EVENT_PATH")"
    ibody="$(jq -r '.issue.body // ""' "$EVENT_PATH")"
    istate="$(jq -r '.issue.state // "open"' "$EVENT_PATH")"
    mnum="$(jq -r '.issue.milestone.number // ""' "$EVENT_PATH")"
    mtitle="$(jq -r '.issue.milestone.title // ""' "$EVENT_PATH")"
    lcsv="$(jq -r '[.issue.labels[]?.name] | join(",")' "$EVENT_PATH")"
    meta="$(classify_item_metadata "$ititle" "$ibody" "$istate" "$mnum" "$mtitle" "$lcsv" "false" "false" "false")"
    echo "Syncing Issue #$inum to Project V2: $meta"
    sync_content_item_to_project "$PROJECT_ID" "$cid" "$meta"
  elif [[ "$has_pr" == "true" ]]; then
    cid="$(jq -r '.pull_request.node_id' "$EVENT_PATH")"
    prnum="$(jq -r '.pull_request.number' "$EVENT_PATH")"
    prtitle="$(jq -r '.pull_request.title // ""' "$EVENT_PATH")"
    prbody="$(jq -r '.pull_request.body // ""' "$EVENT_PATH")"
    prstate="$(jq -r '.pull_request.state // "open"' "$EVENT_PATH")"
    prdraft="$(jq -r '.pull_request.draft // false' "$EVENT_PATH")"
    prmerged="$(jq -r '.pull_request.merged // false' "$EVENT_PATH")"
    mnum="$(jq -r '.pull_request.milestone.number // ""' "$EVENT_PATH")"
    mtitle="$(jq -r '.pull_request.milestone.title // ""' "$EVENT_PATH")"
    lcsv="$(jq -r '[.pull_request.labels[]?.name] | join(",")' "$EVENT_PATH")"
    meta="$(classify_item_metadata "$prtitle" "$prbody" "$prstate" "$mnum" "$mtitle" "$lcsv" "true" "$prdraft" "$prmerged")"
    echo "Syncing PR #$prnum to Project V2: $meta"
    sync_content_item_to_project "$PROJECT_ID" "$cid" "$meta"

    linked_status="In Review"
    if [[ "$prmerged" == "true" ]]; then
      linked_status="Done"
    elif [[ "$prstate" == "closed" && "$prmerged" != "true" ]]; then
      linked_status="Todo"
    elif [[ "$prdraft" == "true" ]]; then
      linked_status="In Progress"
    fi

    mapfile -t linked_nums < <(extract_linked_issues "$prtitle" "$prbody")
    for linked_num in "${linked_nums[@]}"; do
      [[ -z "$linked_num" ]] && continue
      issue_node="$(gh issue view "$linked_num" --repo "$REPO_FULL_NAME" --json id,title,body,state,milestone,labels 2>/dev/null || true)"
      if [[ -n "$issue_node" ]]; then
        icid="$(echo "$issue_node" | jq -r '.id')"
        ititle="$(echo "$issue_node" | jq -r '.title // ""')"
        ibody="$(echo "$issue_node" | jq -r '.body // ""')"
        istate="$(echo "$issue_node" | jq -r '.state // "OPEN"')"
        imnum="$(echo "$issue_node" | jq -r '.milestone.number // ""')"
        imtitle="$(echo "$issue_node" | jq -r '.milestone.title // ""')"
        ilcsv="$(echo "$issue_node" | jq -r '[.labels[]?.name] | join(",")')"
        imeta="$(classify_item_metadata "$ititle" "$ibody" "$istate" "$imnum" "$imtitle" "$ilcsv" "false" "false" "false")"
        echo "Transitioning linked Issue #$linked_num -> Status='$linked_status'"
        sync_content_item_to_project "$PROJECT_ID" "$icid" "$imeta" "$linked_status"
      fi
    done
  fi
fi
