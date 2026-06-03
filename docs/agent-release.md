# MarkMello Applicate Release Workflow

This document is the repo-local release playbook for agent sessions. Read it before updating a GitHub Release, moving an Applicate tag, or preparing release notes.

## Release Surfaces

| Surface | Purpose |
| --- | --- |
| `FORK_CHANGES.md` | Canonical repository changelog for Applicate release scope. |
| `.github/workflows/release-windows.yml` | GitHub Actions release pipeline. Tag push builds Windows installers and uploads release assets. |
| Git tag, for example `v0.3.3-applicate` | Release trigger and source revision for packaged builds. |
| GitHub Release body | Public release notes, downloads, SHA256 hashes, verification notes, and signing status. |
| `packaging/README.md` | Packaging baseline and asset naming reference. |

## Before Touching a Release

1. Confirm the intended operation.

```powershell
git status --short --branch -uall
git log --oneline --decorate -12
git tag --list --sort=-creatordate | Select-Object -First 10
```

2. If the working tree is dirty, treat those changes as user-owned unless you made them in the current task. Do not stage unrelated files.

3. Inspect the current release and workflow state.

```powershell
gh release view v0.3.3-applicate --repo applicate2628/MarkMello --json tagName,targetCommitish,isDraft,isPrerelease,name,publishedAt,url,assets
gh run list --repo applicate2628/MarkMello --workflow release-windows.yml --limit 5 --json databaseId,displayTitle,status,conclusion,event,headSha,createdAt,url
```

Do not request `isLatest` from `gh release view`; the installed GitHub CLI used here does not expose that JSON field.

## Local Verification Gate

Run the normal MarkMello checks before pushing a release tag:

```powershell
npm --prefix src\MarkMello.Applicate.Desktop run check:renderer
npm --prefix src\MarkMello.Applicate.Desktop run build:renderer
npm --prefix src\MarkMello.Applicate.Desktop run test:renderer
dotnet build MarkMello.sln --no-restore
dotnet test MarkMello.sln --no-restore --no-build -m:1 -- xunit.parallelizeTestCollections=false
git diff --check
```

Run the publication safety scan before any push that updates public release state:

```powershell
$scan = ".agents\skills\lead\scripts\check-publication-safety.ps1"
if (-not (Test-Path -LiteralPath $scan)) {
  $scan = Join-Path $env:USERPROFILE ".codex\skills\lead\scripts\check-publication-safety.ps1"
}
powershell -ExecutionPolicy Bypass -File $scan
```

If `npm run build:renderer` changes `RendererWeb/assets/renderer.js`, inspect why before continuing. The source `renderer.ts` and generated bundle should be committed together when renderer code changes.

## Updating the Same Release Tag

Use this path when the user explicitly says to update the same release.

1. Commit only the intended changes. Keep release notes in a separate docs commit if practical.

2. Move the local tag to `HEAD`.

```powershell
git tag -fa v0.3.3-applicate -m "MarkMello Applicate v0.3.3" HEAD
```

3. Push `main`, then force-update the tag.

```powershell
git push origin main
git push origin refs/tags/v0.3.3-applicate --force
```

4. Verify both the annotated tag object and peeled commit. Quote `^{}` in PowerShell.

```powershell
git show-ref --tags -d | Select-String "v0.3.3-applicate"
git rev-parse "v0.3.3-applicate^{}"
git ls-remote --tags origin "v0.3.3-applicate*"
```

Do not run `git rev-parse v0.3.3-applicate^{}` unquoted in PowerShell. It can be parsed incorrectly and produce confusing `-encodedCommand` / `fatal: ambiguous argument` noise.

## Waiting for GitHub Actions

After pushing the tag, find the latest release workflow run and watch it to completion:

```powershell
gh run list --repo applicate2628/MarkMello --workflow release-windows.yml --limit 5 --json databaseId,displayTitle,status,conclusion,event,headSha,createdAt,url
gh run watch <run-id> --repo applicate2628/MarkMello --exit-status --interval 15
```

Do not passively stop while the workflow is running. If a watch command times out, query the same run ID before deciding what happened.

## Updating GitHub Release Notes

Use a file under `.scratch/` for the public release body. Do not pass a long body inline on the command line.

```powershell
gh release view v0.3.3-applicate --repo applicate2628/MarkMello --json body | ConvertFrom-Json | Select-Object -ExpandProperty body
```

After the workflow uploads assets, collect the new hashes from the asset `digest` fields:

```powershell
gh release view v0.3.3-applicate --repo applicate2628/MarkMello --json tagName,targetCommitish,isDraft,isPrerelease,name,publishedAt,url,assets
```

Then update the release body and target commit explicitly:

```powershell
gh release edit v0.3.3-applicate `
  --repo applicate2628/MarkMello `
  --title "MarkMello Applicate v0.3.3 (Stable)" `
  --notes-file .scratch\release-v0.3.3-applicate-notes.md `
  --target <peeled-head-sha> `
  --latest `
  --verify-tag
```

The `--target` flag matters when editing an existing release. Asset uploads can refresh correctly while `targetCommitish` still shows the old release creation target unless it is explicitly edited.

## Final Verification

Before reporting completion, verify the public state and local state:

```powershell
gh run view <run-id> --repo applicate2628/MarkMello --json status,conclusion,headSha,url
gh release view v0.3.3-applicate --repo applicate2628/MarkMello --json targetCommitish,isDraft,isPrerelease,url,name,assets
git status --short --branch -uall
```

Confirm:

- workflow conclusion is `success`;
- release `targetCommitish` equals the intended peeled tag commit;
- release is not draft and not prerelease for stable Applicate releases;
- release body hashes match the latest asset digests;
- local checkout has no unintended staged or dirty files;
- any remaining dirty files are explicitly named as user-owned or out of scope.

## Common Failure Modes

| Symptom | Cause | Correct action |
| --- | --- | --- |
| `Unknown JSON field: "isLatest"` | This `gh` version does not expose `isLatest`. | Query `isDraft`, `isPrerelease`, `targetCommitish`, `url`, and release metadata instead. |
| `-encodedCommand` / `fatal: ambiguous argument` after tag peel | PowerShell parsed unquoted `^{}`. | Use `git rev-parse "v0.3.3-applicate^{}"`. |
| New assets uploaded but release points at old commit | Existing GitHub Release target was not edited. | Run `gh release edit ... --target <peeled-head-sha>`. |
| Renderer tests fail on an obsolete performance assertion | Test contract no longer matches verified behavior. | Update the narrow stale test only after user/runtime evidence confirms the behavior is correct. |
| Release body has old hashes | Notes were edited before asset upload finished. | Re-read `gh release view ... --json assets` after the workflow succeeds and update SHA256 values. |

## Terms and Abbreviations

- Applicate: fork-specific MarkMello desktop overlay.
- GitHub Actions: GitHub-hosted continuous integration and release workflow runner.
- GitHub CLI (`gh`): command-line tool used to inspect runs and edit releases.
- HEAD: the currently checked-out Git commit.
- JSON: JavaScript Object Notation; structured data format returned by `gh --json`.
- Release body: public Markdown notes shown on a GitHub Release page.
- SHA256: cryptographic hash used here as the installer checksum.
- Tag peel: resolving an annotated Git tag to the commit it points at, for example with `git rev-parse "v0.3.3-applicate^{}"`.
