#!/usr/bin/env bash
#
# Does this change need a build?
#
# The matrix is eight runners — two operating systems, two configurations, a
# restore, a build, the test suite and both samples — and a change to a
# paragraph in a markdown file cannot alter any of it. This decides which
# changes are in that category.
#
# Deliberately consulted by a job rather than expressed as `paths-ignore:` on
# the trigger. The two are not interchangeable: a required check belonging to a
# workflow that never ran sits at "Expected — waiting for status" forever, so a
# filtered-out pull request never becomes mergeable. A job skipped by an `if:`
# reports the conclusion `skipped`, which branch protection accepts instead. The
# workflow therefore always triggers and the jobs decide for themselves — see
# the note on the matrix in the workflow, which has to go one step further.
#
#     .github/build-needed.sh              # against origin/main
#     .github/build-needed.sh HEAD~1       # against anything else
#
# Prints `true` or `false`, and says on stderr what it looked at.
#
set -euo pipefail

base="${1:-origin/main}"

# An allow-list of paths that DO need a build would be the other way to write
# this, and it fails in the direction that hurts: a file nobody thought about —
# a new props file, a new top-level directory — would default to not building.
# This list is the paths proven inert, and everything else builds. In
# particular Directory.Build.props, Directory.Build.targets,
# Directory.Packages.props, global.json, .editorconfig, AgentInfer.slnx and the
# workflow itself all change what the build does, and none of them lives under
# src/, tests/ or samples/.
inert() {
  local status="$1" path="$2"

  # A deletion is never inert. README.md is packed into the nupkg, so removing
  # it fails `dotnet pack` on a change that otherwise looks like prose; the same
  # is true of anything else here that some target happens to reference.
  if [ "$status" = "D" ]; then
    return 1
  fi

  case "$path" in
    *.md)                     return 0 ;;  # every doc, readme and skill file
    docs/*)                   return 0 ;;
    .claude/*)                return 0 ;;

    # The documentation site and its workflow, which share nothing with the
    # library. site.yml's own triggers are the complement of this list, so a
    # path belongs to exactly one of the two builds.
    site/*)                        return 0 ;;
    .github/workflows/site.yml)    return 0 ;;

    .github/ISSUE_TEMPLATE/*) return 0 ;;
    LICENSE|.gitignore)       return 0 ;;
  esac

  return 1
}

if ! git rev-parse --verify --quiet "$base^{commit}" >/dev/null; then
  echo "build-needed: cannot resolve '$base'. Building, because the alternative" >&2
  echo "is skipping the matrix on a question we could not answer." >&2
  echo true
  exit 0
fi

# Three dots, matching version-guard.sh: what this branch added since it left
# the base, not what the base has done since. --no-renames so a file moving out
# of src/ arrives as a deletion, which the rule above always builds.
changed=$(git diff --name-status --no-renames "$base...HEAD")

needed=()
while IFS=$'\t' read -r status path _; do
  [ -n "${path:-}" ] || continue
  if ! inert "$status" "$path"; then
    needed+=("$path")
  fi
done <<< "$changed"

if [ ${#needed[@]} -eq 0 ]; then
  echo "build-needed: nothing outside documentation changed since $base." >&2
  echo false
  exit 0
fi

echo "build-needed: these need a build:" >&2
printf '  %s\n' "${needed[@]}" >&2
echo true
