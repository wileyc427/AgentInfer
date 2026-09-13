---
name: release
description: Cut an AgentInfer release to nuget.org. Use when publishing a new version — covers choosing the number, the checks that must pass first, tagging, what the tag-triggered workflow does, and what to do when a publish half-succeeds. nuget.org versions are immutable, so read this before tagging rather than after.
---

# Cut a release

**nuget.org cannot be overwritten.** A published version can be delisted, never
replaced. Everything here exists because the tag is the point of no return.

## 1. Choose the number

Below 1.0, so: a breaking change moves the minor, everything else the patch.
`<Version>` lives in `Directory.Build.props` and applies to all three packages.

It should already have moved — `.github/version-guard.sh` requires it in the
same commit as the change it describes. If you are raising it now, the change
that needed it went in without one, which is worth understanding before
shipping.

## 2. Check before tagging

Run the `verify` skill, then confirm `main` is green:

```bash
git checkout main && git pull
.github/version-guard.sh origin/main~1
```

The version in `Directory.Build.props` is what the tag must name.

## 3. Tag

```bash
git tag v0.3.0          # v + the exact <Version>, no suffix
git push origin v0.3.0
```

The workflow refuses to publish when the tag and `<Version>` disagree, before
anything is minted. A tag pointing at the wrong commit is the one mistake this
does not catch — check `git log -1 v0.3.0` before pushing it.

## 4. What the tag runs

Version guard, the full build matrix, `pack`, `publish` to GitHub Packages, then
`release`:

1. waits for approval on the `nuget.org` environment
2. checks the tag against `<Version>`
3. exchanges a GitHub OIDC token for a short-lived nuget.org key — there is no
   stored credential, and `NUGET_USER` is a repository variable
4. pushes all three packages and their symbols

## 5. Confirm

The registration index lags a new package by minutes. The flat container is the
endpoint restore actually uses and updates first:

```bash
curl -s https://api.nuget.org/v3-flatcontainer/agentinfer/index.json
```

Then download and open what shipped — the same discipline as packing locally:

```bash
curl -sO https://api.nuget.org/v3-flatcontainer/agentinfer/0.3.0/agentinfer.0.3.0.nupkg
unzip -l agentinfer.0.3.0.nupkg
```

## When a publish half-succeeds

Trusted publishing policies are scoped to package ids. A policy naming only
`AgentInfer` publishes one package and rejects the other two.

The push uses `--skip-duplicate`, so fix the policy and re-run the job: what
landed is skipped, what did not is pushed. What you **cannot** do is change what
a published version contains. If the content is wrong, ship the next patch.
