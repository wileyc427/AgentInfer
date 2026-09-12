#!/usr/bin/env bash
#
# A change to what ships must move the version.
#
# The failure this exists for is silent. `dotnet nuget push --skip-duplicate`
# treats an already-published version as success, so a commit that changes the
# library without raising <Version> publishes nothing, reports green, and leaves
# every consumer restoring the previous assembly. What that looks like
# downstream is a generator that inexplicably does not know about a feature that
# is demonstrably in main.
#
# Checked here rather than against the feed on purpose. "Is this version already
# published" needs a credential, answers differently for a fork, and depends on
# a registry URL shape that is not ours. "Did the source change without the
# version moving" needs neither, is the same question one commit earlier, and
# runs on a laptop:
#
#     .github/version-guard.sh              # against origin/main
#     .github/version-guard.sh HEAD~1       # against anything else
#
set -euo pipefail

base="${1:-origin/main}"

# What counts as shippable. src/ is the package; Directory.Packages.props sets
# the dependency versions that go into the nuspec. tests/ and samples/ do not
# ship, and requiring a bump for a test would train people to bump mechanically
# — which is the habit this check exists to prevent, not to create.
#
# Directory.Build.props is deliberately NOT watched, and that is a known gap
# rather than an oversight. It is where <Version> itself lives, so watching it
# means every bump is also a change demanding a bump, and every comment edit
# beside one demands a version nothing shipped for. The cost is that editing a
# package property there — <Authors>, a new global property — changes the nuspec
# without tripping this. That edit happens a few lines from <Version> and the
# comment above it says to raise it, which is the best available answer that
# does not make the guard cry wolf. A guard that cries wolf gets deleted.
watched=(src Directory.Packages.props)

if ! git rev-parse --verify --quiet "$base^{commit}" >/dev/null; then
  echo "version-guard: cannot resolve '$base'. Fetch it first, or pass another base." >&2
  exit 2
fi

# Three dots: compare against where this branch left the base, not against the
# base's current tip. Otherwise unrelated commits landing on main while a branch
# is open read as changes this branch made.
changed=$(git diff --name-only "$base...HEAD" -- "${watched[@]}")

if [ -z "$changed" ]; then
  echo "version-guard: nothing shippable changed. The version may stay where it is."
  exit 0
fi

version_in() {
  # $1 is a git revision, or "-" for the working tree.
  if [ "$1" = "-" ]; then
    cat Directory.Build.props
  else
    git show "$1:Directory.Build.props"
  fi | sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' | head -1
}

before=$(version_in "$base")
after=$(version_in -)

if [ -z "$after" ]; then
  echo "version-guard: no <Version> in Directory.Build.props. Every package would be 1.0.0." >&2
  exit 2
fi

if [ "$before" != "$after" ]; then
  echo "version-guard: $before -> $after. Publishing $after."
  exit 0
fi

cat >&2 <<MESSAGE
version-guard: these files changed and <Version> is still $after:

$(echo "$changed" | sed 's/^/  /')

Raise <Version> in Directory.Build.props, in this change rather than after it.
A version already on the feed is immutable, so publishing $after again is a
no-op that reports success — consumers keep restoring the old assembly and the
symptom arrives somewhere else entirely.

If what changed genuinely does not ship (a comment, a file nobody packs), say so
in the commit and move the file out of the watched set rather than raising the
version for it.
MESSAGE
exit 1
