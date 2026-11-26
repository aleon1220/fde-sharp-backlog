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
  local state_upper title_lower body_lower labels_lower mtitle_lower
  state_upper="$(printf '%s' "$state" | tr '[:lower:]' '[:upper:]')"
  title_lower="$(printf '%s' "$title" | tr '[:upper:]' '[:lower:]')"
  body_lower="$(printf '%s' "$body" | tr '[:upper:]' '[:lower:]')"
  labels_lower="$(printf '%s' "$labels_csv" | tr '[:upper:]' '[:lower:]')"
  mtitle_lower="$(printf '%s' "$milestone_title" | tr '[:upper:]' '[:lower:]')"

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
  if [[ "$title_lower" == *"[baseline"* || "$labels_lower" == *"baseline"* || "$body_lower" == *"baseline/audit"* ]]; then
    backlog_type="Baseline/Audit"
  elif [[ "$title_lower" == *"(type02)"* || "$body_lower" == *"(type02)"* || "$body_lower" == *"jira - schedule"* || "$labels_lower" == *"type02"* ]]; then
    backlog_type="Type02: Schedule"
  elif [[ "$title_lower" == *"(type01)"* || "$body_lower" == *"(type01)"* || "$body_lower" == *"jira - complete"* || "$body_lower" == *"## acceptance criteria"* || "$labels_lower" == *"type01"* ]]; then
    backlog_type="Type01: Complete"
  fi

  local priority=""
  if [[ "$labels_lower" =~ (^|[^[:alnum:]_])(p[0-3])([^[:alnum:]_]|$) ]]; then
    priority="$(printf '%s' "${BASH_REMATCH[2]}" | tr '[:lower:]' '[:upper:]')"
  elif [[ "$body_lower" =~ priority[^[:cntrl:]]*(p[0-3])([^[:alnum:]_]|$) ]]; then
    priority="$(printf '%s' "${BASH_REMATCH[1]}" | tr '[:lower:]' '[:upper:]')"
  elif [[ "$body_lower" =~ (^|[^[:alnum:]_])(p[0-3])([^[:alnum:]_]|$) ]]; then
    priority="$(printf '%s' "${BASH_REMATCH[2]}" | tr '[:lower:]' '[:upper:]')"
  elif [[ "$title_lower" =~ (^|[^[:alnum:]_])(p[0-3])([^[:alnum:]_]|$) ]]; then
    priority="$(printf '%s' "${BASH_REMATCH[2]}" | tr '[:lower:]' '[:upper:]')"
  elif [[ "$backlog_type" == "Baseline/Audit" ]]; then
    priority="P2"
  elif [[ "$title_lower" == *"uat"* || "$title_lower" == *"security"* || "$title_lower" == *"mask"* ]]; then
    priority="P0"
  elif [[ "$milestone_num" == "1" || "$milestone_num" == "2" ]]; then
    priority="P1"
  else
    priority="P2"
  fi

  local area="Docs/GitHub"
  local combined_lower="${title_lower} ${body_lower}"
  if [[ "$labels_lower" == *"area:cli"* ]]; then
    area="CLI/TUI"
  elif [[ "$labels_lower" == *"area:ai"* ]]; then
    area="AI Providers"
  elif [[ "$labels_lower" == *"area:sre"* || "$labels_lower" == *"area:devops"* ]]; then
    area="SRE/DevOps"
  elif [[ "$labels_lower" == *"area:prompt"* ]]; then
    area="Prompt Library"
  elif [[ "$labels_lower" == *"area:docs"* || "$labels_lower" == *"area:github"* ]]; then
    area="Docs/GitHub"
  elif [[ "$title_lower" == *"[github project"* || "$title_lower" == *"issue templates"* || "$title_lower" == *"readme"* ]]; then
    area="Docs/GitHub"
  elif [[ "$title_lower" == *"fde-ai-prompts"* || "$title_lower" == *"prompt scanner"* || "$title_lower" == *"prompt generator"* || "$title_lower" == *"prompt template"* || "$title_lower" == *"goal expansion"* ]]; then
    area="Prompt Library"
  elif [[ "$title_lower" == *"nuget"* || "$title_lower" == *"binary"* || "$title_lower" == *"logging"* || "$title_lower" == *"podman"* || "$title_lower" == *"container"* || "$title_lower" == *"kubernetes"* || "$title_lower" == *"dependabot"* || "$title_lower" == *"branch protection"* || "$title_lower" == *"secret"* || "$title_lower" == *"ci/cd"* ]]; then
    area="SRE/DevOps"
  elif [[ "$title_lower" == *"gemini"* || "$title_lower" == *"copilot"* || "$title_lower" == *"openai"* || "$title_lower" == *"provider"* || "$title_lower" == *"byok"* || "$title_lower" == *"grounding"* || "$title_lower" == *"mcp"* || "$title_lower" == *"evaluator"* ]]; then
    area="AI Providers"
  elif [[ "$combined_lower" == *"cli"* || "$combined_lower" == *"tui"* || "$combined_lower" == *"repl"* || "$combined_lower" == *"wizard"* || "$combined_lower" == *"clipboard"* || "$combined_lower" == *"osc 52"* ]]; then
    area="CLI/TUI"
  fi

  local target_milestone="M2"
  if [[ "$milestone_num" == "1" || "$mtitle_lower" == *"release-fde-version"* ]]; then
    target_milestone="M1"
  elif [[ "$milestone_num" == "2" || "$mtitle_lower" == *"v1.1.0"* ]]; then
    target_milestone="M2"
  elif [[ "$milestone_num" == "3" || "$mtitle_lower" == *"v2.0.0"* ]]; then
    target_milestone="M3"
  elif [[ "$title_lower" == *"v2.0"* || "$body_lower" == *"milestone #3"* ]]; then
    target_milestone="M3"
  elif [[ "$body_lower" == *"milestone #1"* || "$body_lower" == *"release-fde-version"* ]]; then
    target_milestone="M1"
  fi

  local effort="S"
  if [[ "$labels_lower" == *"effort:xs"* ]]; then
    effort="XS"
  elif [[ "$labels_lower" == *"effort:s"* ]]; then
    effort="S"
  elif [[ "$labels_lower" == *"effort:m"* ]]; then
    effort="M"
  elif [[ "$labels_lower" == *"effort:l"* ]]; then
    effort="L"
  elif [[ "$backlog_type" == "Baseline/Audit" ]]; then
    effort="XS"
  elif [[ "$target_milestone" == "M3" || "$title_lower" == *"epic"* || "$title_lower" == *"v2.0"* ]]; then
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
    printf '%s\n' "$pr_body" | grep -oEi '(close[sd]?|fix(e[sd])?|resolve[sd]?)[[:space:]]+#[0-9]+' | grep -oE '[0-9]+' || true
    printf '%s\n' "$pr_title" | grep -oEi '(close[sd]?|fix(e[sd])?|resolve[sd]?)[[:space:]]+#[0-9]+' | grep -oE '[0-9]+' || true
    printf '%s\n' "$pr_title" | grep -oE '\(#[0-9]+\)' | grep -oE '[0-9]+' || true
  } | sort -n -u
}

if [[ "$MODE" == "dry-run" ]]; then
  echo "=== GitHub Project V2 Schema & Classification Dry-Run ($REPO_FULL_NAME) ==="
  jq -r '
    "Schema Version : " + .schemaVersion,
    "Default Title  : " + .project.defaultTitle,
    "Status Options : " + ([.statusField.options[].name] | join(", ")),
    "Custom Fields  : " + ([.customFields[] | .name + " [" + ([.options[].name] | join("|")) + "]"] | join("; ")),
    "Iteration Field: " + .iterationField.name + " (" + (.iterationField.durationDays | tostring) + " days)",
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

graphql_exec() {
  local query="$1"
  local vars_json="${2:-}"
  if [[ -z "$vars_json" ]]; then
    vars_json="{}"
  fi
  jq -nc --arg query "$query" --argjson variables "$vars_json" '{query: $query, variables: $variables}' | gh api graphql --input -
}

PROJECT_METADATA_CACHE=""

fetch_project_metadata() {
  local project_id="$1"
  local force_refresh="${2:-false}"
  if [[ "$force_refresh" == "true" || -z "$PROJECT_METADATA_CACHE" ]]; then
    local q vars
    q='query($projectId: ID!) {
      node(id: $projectId) {
        ... on ProjectV2 {
          fields(first: 50) {
            nodes {
              ... on ProjectV2FieldCommon { id name dataType }
              ... on ProjectV2SingleSelectField {
                id
                name
                dataType
                options { id name color description }
              }
            }
          }
          views(first: 30) {
            nodes { id name layout filter }
          }
        }
      }
    }'
    vars="$(jq -nc --arg projectId "$project_id" '{projectId: $projectId}')"
    PROJECT_METADATA_CACHE="$(graphql_exec "$q" "$vars")"
  fi
  printf '%s' "$PROJECT_METADATA_CACHE"
}

resolve_or_create_project_id() {
  local repo_data q vars
  if [[ -n "$PROJECT_NUMBER" ]]; then
    q='query($owner: String!, $name: String!, $projectNumber: Int!) {
      repository(owner: $owner, name: $name) {
        id
        owner { id login }
        projectsV2(first: 20) {
          nodes { id number title url closed }
        }
      }
      repositoryOwner(login: $owner) {
        id
        ... on User {
          projectV2(number: $projectNumber) { id number title url closed }
          projectsV2(first: 20) { nodes { id number title url closed } }
        }
        ... on Organization {
          projectV2(number: $projectNumber) { id number title url closed }
          projectsV2(first: 20) { nodes { id number title url closed } }
        }
      }
    }'
    vars="$(jq -nc --arg owner "$OWNER" --arg name "$REPO_NAME" --argjson projectNumber "$PROJECT_NUMBER" '{owner: $owner, name: $name, projectNumber: $projectNumber}')"
  else
    q='query($owner: String!, $name: String!) {
      repository(owner: $owner, name: $name) {
        id
        owner { id login }
        projectsV2(first: 20) {
          nodes { id number title url closed }
        }
      }
      repositoryOwner(login: $owner) {
        id
        ... on User {
          projectsV2(first: 20) { nodes { id number title url closed } }
        }
        ... on Organization {
          projectsV2(first: 20) { nodes { id number title url closed } }
        }
      }
    }'
    vars="$(jq -nc --arg owner "$OWNER" --arg name "$REPO_NAME" '{owner: $owner, name: $name}')"
  fi

  repo_data="$(graphql_exec "$q" "$vars")"

  local repo_id owner_id project_id
  repo_id="$(printf '%s' "$repo_data" | jq -r '.data.repository.id')"
  owner_id="$(printf '%s' "$repo_data" | jq -r '.data.repositoryOwner.id // .data.repository.owner.id')"

  if [[ -n "$PROJECT_NUMBER" ]]; then
    project_id="$(printf '%s' "$repo_data" | jq -r --argjson num "$PROJECT_NUMBER" '
      ([.data.repositoryOwner.projectV2, .data.repository.projectsV2.nodes[]?, .data.repositoryOwner.projectsV2.nodes[]?]
       | map(select(. != null and .number == $num and .closed == false))
       | first | .id) // empty')"
  else
    project_id="$(printf '%s' "$repo_data" | jq -r '
      ([.data.repository.projectsV2.nodes[]?, .data.repositoryOwner.projectsV2.nodes[]?]
       | map(select(. != null and .closed == false))
       | first | .id) // empty')"
  fi

  if [[ -z "$project_id" ]]; then
    local default_title create_q create_vars created
    default_title="$(jq -r '.project.defaultTitle' "$SCHEMA_PATH")"
    echo "Creating GitHub Project V2 '$default_title' for $REPO_FULL_NAME..." >&2
    create_q='mutation($ownerId: ID!, $title: String!) {
      createProjectV2(input: {ownerId: $ownerId, title: $title}) {
        projectV2 { id number title url }
      }
    }'
    create_vars="$(jq -nc --arg ownerId "$owner_id" --arg title "$default_title" '{ownerId: $ownerId, title: $title}')"
    created="$(graphql_exec "$create_q" "$create_vars")"
    project_id="$(printf '%s' "$created" | jq -r '.data.createProjectV2.projectV2.id')"
  fi

  local link_q link_vars
  link_q='mutation($projectId: ID!, $repositoryId: ID!) {
    linkProjectV2ToRepository(input: {projectId: $projectId, repositoryId: $repositoryId}) {
      repository { id }
    }
  }'
  link_vars="$(jq -nc --arg projectId "$project_id" --arg repositoryId "$repo_id" '{projectId: $projectId, repositoryId: $repositoryId}')"
  graphql_exec "$link_q" "$link_vars" >/dev/null 2>&1 || true

  printf '%s\n' "$project_id"
}

sync_single_select_options() {
  local field_id="$1"
  local field_name="$2"
  local existing_options_json="$3"
  local desired_options_json="$4"

  local missing_count
  missing_count="$(jq -n --argjson existing "$existing_options_json" --argjson desired "$desired_options_json" '
    ([$desired[].name] - [$existing[].name]) | length')"

  if [[ "$missing_count" -eq 0 ]]; then
    return 0
  fi

  local merged_options update_q update_vars
  merged_options="$(jq -n --argjson existing "$existing_options_json" --argjson desired "$desired_options_json" '
    ($desired | map(
      . as $d |
      ($existing | map(select(.name == $d.name)) | first) as $match |
      if $match != null then
        {id: $match.id, name: $d.name, color: $d.color, description: $d.description}
      else
        {name: $d.name, color: $d.color, description: $d.description}
      end
    )) +
    ($existing | map(
      . as $e |
      select(($desired | map(.name) | index($e.name)) == null) |
      {id: $e.id, name: $e.name, color: ($e.color // "GRAY"), description: ($e.description // "")}
    ))')"

  echo "Updating single-select options for '$field_name'..." >&2
  update_q='mutation($fieldId: ID!, $options: [ProjectV2SingleSelectFieldOptionInput!]!) {
    updateProjectV2Field(input: {
      fieldId: $fieldId,
      singleSelectOptions: $options
    }) {
      projectV2Field { ... on ProjectV2SingleSelectField { id name } }
    }
  }'
  update_vars="$(jq -nc --arg fieldId "$field_id" --argjson options "$merged_options" '{fieldId: $fieldId, options: $options}')"
  graphql_exec "$update_q" "$update_vars" >/dev/null
}

ensure_project_fields() {
  local project_id="$1"
  local existing_fields
  existing_fields="$(fetch_project_metadata "$project_id" "true")"

  local status_field_id existing_status_options desired_status_options
  status_field_id="$(printf '%s' "$existing_fields" | jq -r '.data.node.fields.nodes[] | select(.name == "Status") | .id // empty')"
  if [[ -n "$status_field_id" ]]; then
    existing_status_options="$(printf '%s' "$existing_fields" | jq -c '.data.node.fields.nodes[] | select(.name == "Status") | (.options // [])')"
    desired_status_options="$(jq -c '.statusField.options | map({name: .name, color: .color, description: .description})' "$SCHEMA_PATH")"
    sync_single_select_options "$status_field_id" "Status" "$existing_status_options" "$desired_status_options"
  fi

  local count
  count="$(jq '.customFields | length' "$SCHEMA_PATH")"
  for ((i = 0; i < count; i++)); do
    local field_name desired_options existing_id existing_options
    field_name="$(jq -r ".customFields[$i].name" "$SCHEMA_PATH")"
    desired_options="$(jq -c ".customFields[$i].options | map({name: .name, color: .color, description: .description})" "$SCHEMA_PATH")"
    existing_id="$(printf '%s' "$existing_fields" | jq -r --arg name "$field_name" '.data.node.fields.nodes[] | select(.name == $name) | .id // empty')"

    if [[ -z "$existing_id" ]]; then
      echo "Creating custom single-select field '$field_name'..." >&2
      local create_q create_vars
      create_q='mutation($projectId: ID!, $name: String!, $options: [ProjectV2SingleSelectFieldOptionInput!]!) {
        createProjectV2Field(input: {
          projectId: $projectId,
          dataType: SINGLE_SELECT,
          name: $name,
          singleSelectOptions: $options
        }) {
          projectV2Field { ... on ProjectV2SingleSelectField { id name } }
        }
      }'
      create_vars="$(jq -nc --arg projectId "$project_id" --arg name "$field_name" --argjson options "$desired_options" '{projectId: $projectId, name: $name, options: $options}')"
      graphql_exec "$create_q" "$create_vars" >/dev/null
    else
      existing_options="$(printf '%s' "$existing_fields" | jq -c --arg name "$field_name" '.data.node.fields.nodes[] | select(.name == $name) | (.options // [])')"
      sync_single_select_options "$existing_id" "$field_name" "$existing_options" "$desired_options"
    fi
  done

  local iter_name iter_duration iter_existing_id
  iter_name="$(jq -r '.iterationField.name // empty' "$SCHEMA_PATH")"
  iter_duration="$(jq -r '.iterationField.durationDays // 14' "$SCHEMA_PATH")"
  if [[ -n "$iter_name" ]]; then
    iter_existing_id="$(printf '%s' "$existing_fields" | jq -r --arg name "$iter_name" '.data.node.fields.nodes[] | select(.name == $name) | .id // empty')"
    if [[ -z "$iter_existing_id" ]]; then
      echo "Creating iteration field '$iter_name' (${iter_duration}d)..." >&2
      local start_date iter_q iter_vars
      start_date="$(date -u +%Y-%m-%d)"
      iter_q='mutation($projectId: ID!, $name: String!, $config: ProjectV2IterationFieldConfigurationInput!) {
        createProjectV2Field(input: {
          projectId: $projectId,
          dataType: ITERATION,
          name: $name,
          iterationConfiguration: $config
        }) {
          projectV2Field { ... on ProjectV2IterationField { id name } }
        }
      }'
      iter_vars="$(jq -nc --arg projectId "$project_id" --arg name "$iter_name" --arg startDate "$start_date" --argjson duration "$iter_duration" '{
        projectId: $projectId,
        name: $name,
        config: {
          startDate: $startDate,
          duration: $duration,
          iterations: [
            { title: "Sprint 1", startDate: $startDate, duration: $duration }
          ]
        }
      }')"
      graphql_exec "$iter_q" "$iter_vars" >/dev/null
    fi
  fi

  # Refresh cache after ensuring fields
  fetch_project_metadata "$project_id" "true" >/dev/null
}

ensure_project_views() {
  local project_id="$1"
  local metadata view_count
  metadata="$(fetch_project_metadata "$project_id" "true")"
  view_count="$(jq '.views | length' "$SCHEMA_PATH")"

  for ((i = 0; i < view_count; i++)); do
    local vname vlayout vfilter existing_vid
    vname="$(jq -r ".views[$i].name" "$SCHEMA_PATH")"
    vlayout="$(jq -r ".views[$i].layout" "$SCHEMA_PATH")"
    vfilter="$(jq -r ".views[$i].filter // \"\"" "$SCHEMA_PATH")"
    existing_vid="$(printf '%s' "$metadata" | jq -r --arg name "$vname" '.data.node.views.nodes[] | select(.name == $name) | .id // empty')"

    if [[ -z "$existing_vid" ]]; then
      echo "Creating saved view '$vname' ($vlayout)..." >&2
      local create_vq create_vvars created_view new_vid visible_ids
      create_vq='mutation($projectId: ID!, $name: String!, $layout: ProjectV2ViewLayout!) {
        createProjectV2View(input: {
          projectId: $projectId,
          name: $name,
          layout: $layout
        }) {
          projectV2View { id name }
        }
      }'
      create_vvars="$(jq -nc --arg projectId "$project_id" --arg name "$vname" --arg layout "$vlayout" '{projectId: $projectId, name: $name, layout: $layout}')"
      created_view="$(graphql_exec "$create_vq" "$create_vvars")"
      new_vid="$(printf '%s' "$created_view" | jq -r '.data.createProjectV2View.projectV2View.id // empty')"

      if [[ -n "$new_vid" ]]; then
        visible_ids="$(jq -nc --argjson meta "$metadata" --argjson wanted "$(jq -c ".views[$i].visibleFields // []" "$SCHEMA_PATH")" '
          [ $wanted[] as $w | ($meta.data.node.fields.nodes[] | select(.name == $w) | .id) ]')"
        local visible_count
        visible_count="$(printf '%s' "$visible_ids" | jq 'length')"
        if [[ "$visible_count" -gt 0 ]]; then
          local update_vq update_vvars
          update_vq='mutation($viewId: ID!, $filter: String, $visibleFieldIds: [ID!]) {
            updateProjectV2View(input: {
              viewId: $viewId,
              filter: $filter,
              configuration: { visibleFieldIds: $visibleFieldIds }
            }) {
              projectV2View { id name }
            }
          }'
          update_vvars="$(jq -nc --arg viewId "$new_vid" --arg filter "$vfilter" --argjson visibleFieldIds "$visible_ids" '{viewId: $viewId, filter: $filter, visibleFieldIds: $visibleFieldIds}')"
          if ! graphql_exec "$update_vq" "$update_vvars" >/dev/null 2>&1; then
            if [[ -n "$vfilter" ]]; then
              local fallback_vq fallback_vvars
              fallback_vq='mutation($viewId: ID!, $filter: String!) {
                updateProjectV2View(input: { viewId: $viewId, filter: $filter }) {
                  projectV2View { id name }
                }
              }'
              fallback_vvars="$(jq -nc --arg viewId "$new_vid" --arg filter "$vfilter" '{viewId: $viewId, filter: $filter}')"
              graphql_exec "$fallback_vq" "$fallback_vvars" >/dev/null 2>&1 || echo "warning: could not apply filter '$vfilter' to view '$vname'" >&2
            fi
          fi
        elif [[ -n "$vfilter" ]]; then
          local fallback_vq fallback_vvars
          fallback_vq='mutation($viewId: ID!, $filter: String!) {
            updateProjectV2View(input: { viewId: $viewId, filter: $filter }) {
              projectV2View { id name }
            }
          }'
          fallback_vvars="$(jq -nc --arg viewId "$new_vid" --arg filter "$vfilter" '{viewId: $viewId, filter: $filter}')"
          graphql_exec "$fallback_vq" "$fallback_vvars" >/dev/null 2>&1 || echo "warning: could not apply filter '$vfilter' to view '$vname'" >&2
        fi
      fi
    fi
  done
}

sync_content_item_to_project() {
  local project_id="$1"
  local content_id="$2"
  local meta_json="$3"
  local status_override="${4:-}"

  local add_q add_vars item_res item_id
  add_q='mutation($projectId: ID!, $contentId: ID!) {
    addProjectV2ItemById(input: {projectId: $projectId, contentId: $contentId}) {
      item { id }
    }
  }'
  add_vars="$(jq -nc --arg projectId "$project_id" --arg contentId "$content_id" '{projectId: $projectId, contentId: $contentId}')"
  item_res="$(graphql_exec "$add_q" "$add_vars")"
  item_id="$(printf '%s' "$item_res" | jq -r '.data.addProjectV2ItemById.item.id')"

  local fields_data
  fields_data="$(fetch_project_metadata "$project_id")"

  local target_status
  target_status="${status_override:-$(printf '%s' "$meta_json" | jq -r '.status')}"

  declare -A field_map=(
    ["Status"]="$target_status"
    ["Priority"]="$(printf '%s' "$meta_json" | jq -r '.priority')"
    ["Backlog Type"]="$(printf '%s' "$meta_json" | jq -r '.backlogType')"
    ["Area"]="$(printf '%s' "$meta_json" | jq -r '.area')"
    ["Target Milestone"]="$(printf '%s' "$meta_json" | jq -r '.targetMilestone')"
    ["Effort"]="$(printf '%s' "$meta_json" | jq -r '.effort')"
  )

  local update_item_q
  update_item_q='mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
    updateProjectV2ItemFieldValue(input: {
      projectId: $projectId,
      itemId: $itemId,
      fieldId: $fieldId,
      value: { singleSelectOptionId: $optionId }
    }) {
      projectV2Item { id }
    }
  }'

  for fname in "${!field_map[@]}"; do
    local opt_name="${field_map[$fname]}"
    local fid oid
    fid="$(printf '%s' "$fields_data" | jq -r --arg fn "$fname" '.data.node.fields.nodes[] | select(.name == $fn) | .id // empty')"
    oid="$(printf '%s' "$fields_data" | jq -r --arg fn "$fname" --arg on "$opt_name" '.data.node.fields.nodes[] | select(.name == $fn) | .options[]? | select(.name == $on) | .id // empty')"
    if [[ -n "$fid" && -n "$oid" ]]; then
      local update_item_vars
      update_item_vars="$(jq -nc --arg projectId "$project_id" --arg itemId "$item_id" --arg fieldId "$fid" --arg optionId "$oid" '{projectId: $projectId, itemId: $itemId, fieldId: $fieldId, optionId: $optionId}')"
      graphql_exec "$update_item_q" "$update_item_vars" >/dev/null
    fi
  done
}

PROJECT_ID="$(resolve_or_create_project_id)"
ensure_project_fields "$PROJECT_ID"
if [[ "$RUN_BOOTSTRAP" == "true" ]]; then
  ensure_project_views "$PROJECT_ID"
fi

if [[ "$RUN_BACKFILL" == "true" ]]; then
  echo "Backfilling repository issues into GitHub Project V2 ($PROJECT_ID)..."
  issues_json="$(gh issue list --repo "$REPO_FULL_NAME" --state all --limit 100 --json id,number,title,body,state,milestone,labels)"
  issue_count="$(printf '%s' "$issues_json" | jq 'length')"
  for ((idx = 0; idx < issue_count; idx++)); do
    row="$(printf '%s' "$issues_json" | jq -c ".[$idx]")"
    cid="$(printf '%s' "$row" | jq -r '.id')"
    inum="$(printf '%s' "$row" | jq -r '.number')"
    ititle="$(printf '%s' "$row" | jq -r '.title // ""')"
    ibody="$(printf '%s' "$row" | jq -r '.body // ""')"
    istate="$(printf '%s' "$row" | jq -r '.state // "OPEN"')"
    mnum="$(printf '%s' "$row" | jq -r '.milestone.number // ""')"
    mtitle="$(printf '%s' "$row" | jq -r '.milestone.title // ""')"
    lcsv="$(printf '%s' "$row" | jq -r '[.labels[]?.name] | join(",")')"
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
        icid="$(printf '%s' "$issue_node" | jq -r '.id')"
        ititle="$(printf '%s' "$issue_node" | jq -r '.title // ""')"
        ibody="$(printf '%s' "$issue_node" | jq -r '.body // ""')"
        istate="$(printf '%s' "$issue_node" | jq -r '.state // "OPEN"')"
        imnum="$(printf '%s' "$issue_node" | jq -r '.milestone.number // ""')"
        imtitle="$(printf '%s' "$issue_node" | jq -r '.milestone.title // ""')"
        ilcsv="$(printf '%s' "$issue_node" | jq -r '[.labels[]?.name] | join(",")')"
        imeta="$(classify_item_metadata "$ititle" "$ibody" "$istate" "$imnum" "$imtitle" "$ilcsv" "false" "false" "false")"
        echo "Transitioning linked Issue #$linked_num -> Status='$linked_status'"
        sync_content_item_to_project "$PROJECT_ID" "$icid" "$imeta" "$linked_status"
      fi
    done
  fi
fi
