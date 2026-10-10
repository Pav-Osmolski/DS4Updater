# Changelog

Release history for the Pav-Osmolski DS4Updater fork.

## [2.0.9](https://github.com/Pav-Osmolski/DS4Updater/releases/tag/v2.0.9)

- Target Pav-Osmolski/DS4Windows releases and validate their download URLs and build receipts.
- Retain repository, release identity, SHA-256, size and package ownership checks.
- Support managed installer updates and transactional portable updates, preserving profiles,
  custom executable names and the Lang translation layout.
- Support numeric app releases and schema 1 receipts, including DS4Windows 5.0.14.

RC4.6.7 requires one manual app upgrade because its own update checks still target upstream.

## Inherited release records

The following records preserve the original release descriptions and their
limitations. They are historical context, not current setup instructions.

<a id="inherited-release-206"></a>

### release-2.0.6

Original record: `docs/release-2.0.6.md`. Instructions and validation claims apply to that release.

### DS4Updater 2.0.6

#### Changes

- Named DS4Windows releases no longer need a new updater version for each release.
  The updater obtains the expected Windows binary version from the release's
  `RELEASE-BUILD.json`, verified against GitHub's asset SHA-256 and size.
- The build record must identify the exact repository, tag and release ID, and
  bind the selected portable ZIP to the same SHA-256 as GitHub's metadata. A
  record cannot override an established historical or numeric version identity.
- Release-channel ordering and Windows binary downgrade protection remain
  independent requirements. Staged, installed and manually reopened applications
  must match the verified release identity. A target changed during preparation
  is rejected before the portable transaction starts.
- Existing historical release mappings remain available only when a build record
  is absent. An invalid, ambiguous or tampered record never triggers fallback.
- The portable protocol, ownership manifest, profile preservation and broker
  requirements are unchanged. No new VIIPER release is required.

The safe portable path remains x64. Both x64 and x86 updater executables are
built by the existing release workflow; x86 does not gain a new portable-update
protocol. Existing DS4Windows clients using the verified updater bootstrap can
obtain this release through the normal stable updater channel.

Build records are publisher metadata authenticated by the GitHub asset digest;
this is not a claim that the JSON record or executable has a digital signature.

#### Local validation and publication boundary

The final full suite passed **313 / 313**, zero failures or skips, including the
immutable real-release ZIP fixture. Report:
`isolated_results/updater-2.0.6/final-tests/updater-2.0.6-final-full.trx`.
The regression matrix includes future RC4.5.4/RC4.6 records, malformed and
tampered records, legacy fallback, changed-target rejection, and the numeric
workflow's deterministic leading-`v` package-marker/version normalization.
Named release records still require four explicit Windows version components.

Validation uses synthetic HTTP/process fixtures and isolated filesystem targets,
including the unchanged published RC4.5.1 ZIP. It does not execute an installed
DS4Windows, updater worker or broker, and does not change a physical controller
session. A launched-worker end-to-end acceptance remains separate from these
tests and the verified real-ZIP filesystem transaction.

Run from the updater repository:

```powershell
$env:DS4UPDATER_RC451_PACKAGE = 'C:\Users\hbash\Desktop\DS4Windows-RC4.5.1-Publish-2026-09-09\release-assets\DS4Windows_VIIPER_x64.zip'
dotnet test .\Updater2.Tests\DS4Updater.Tests.csproj -c Release --logger 'trx;LogFileName=updater-2.0.6-full.trx' --results-directory .\isolated_results\updater-2.0.6\tests
dotnet publish .\Updater2\DS4Updater.csproj -c Release -r win-x64 --self-contained true /p:Platform=x64 /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true -o .\isolated_results\updater-2.0.6\publish-x64
```

The immutable test ZIP has SHA-256
`985B7DA39AB682FAE9ED27EC4B1622F58C240911454CFFC29CD661EC13BBA109`.
The same publish flags with `win-x86` and `Platform=x86` cover the second
workflow asset. Local build outputs are not publication evidence.

The repository's primary branch is `master`; this work descends from
`d2e9b3ae316c52b8320055e2acc063da42d05905` (`v2.0.5`). The existing workflow tests
the immutable ZIP and publishes one compressed, self-contained executable per
architecture. It uploads release assets only for a published release event, not
for `workflow_dispatch`, and never replaces an existing asset with `--clobber`.
Release coordination must verify both assets before promoting 2.0.6 to stable
latest; 2.0.5 remains immutable.

<a id="inherited-release-207"></a>

### release-2.0.7

Original record: `docs/release-2.0.7.md`. Instructions and validation claims apply to that release.

### DS4Updater 2.0.7

#### Changes

- Preserves the selected custom portable executable name without reinstalling a
  duplicate `DS4Windows.exe`. Required canonical managed-assembly dependencies
  remain, and aliases and ownership manifests are updated transactionally.
- Supports changed and cleared custom names by binding the initiating executable
  separately from the selected destination in the immutable worker request.
- Protects configuration bytes, rejects unowned collisions before mutation, and
  restores owned files on failure using the existing verified rollback path.
  A newly selected alias never adopts preexisting unowned files. A legacy
  custom apphost can be adopted only when it is the exact initiating executable
  bound to the worker, its PE identity matches the owned application assembly,
  and any unowned sidecars match their owned canonical counterparts. Read locks
  and exclusive preflight hash checks bind that evidence to the actual files.
- Gates custom-only layouts on the **verified target binary version 5.0.8.0 or
  newer**, first delivered by DS4Windows RC4.6.2. An older custom-target request
  fails before ZIP download, staging, process waiting, or live-file mutation.
  The existing layout is left intact. Canonical updates to older supported
  targets remain available; this is not a hardcoded release-tag allowlist.
- Retains 2.0.6's authenticated release-build-record checks, staged/final identity
  checks, exact-root process guard, and forward-release/downgrade protections.

The portable-safe-v1 bootstrap contract is unchanged. An old initiating app can
use this updater to reach RC4.6.2 or a compatible later release. A cached older
payload cannot pass the target's verified PE identity check. The supported safe
portable architecture remains x64; the x86 legacy asset is still produced.

#### Release coordination

Use tag `v2.0.7`, matching the existing stable `v2.0.6` naming convention. The
workflow produces exactly two compressed self-contained assets:

- `DS4Updater.exe` — win-x64
- `DS4Updater_x86.exe` — win-x86

The workflow builds on source push and uploads immutable release assets on a
published release event. It does not clobber existing assets. Verify the
workflow's exact commit, successful tests, file version, asset byte length, and
GitHub SHA-256 digest for both assets before making this the stable `/latest`
release. The DS4Windows release does not bundle this updater; its verified
bootstrap selects the stable updater release independently.

For draft-first coordination, preload the exact successful source-CI artifacts
into the `v2.0.7` draft and verify both assets before publication. The published
workflow then rebuilds the exact tag/event commit and accepts an existing asset
only when its unique exact name, uploaded state, size and GitHub SHA-256 match
the newly built file. Missing assets may be uploaded; duplicates or mismatches
fail without overwriting anything. An empty draft must never be promoted to
stable/latest while waiting for its binaries.

Publish and verify updater 2.0.7 before DS4Windows RC4.6.2. The compatibility gate
makes the interval safe: a custom-named old application requesting an older
target is refused without file changes until a compatible target is available.
DS4Windows RC4.6.2 must require updater 2.0.7 or later for the corrected behavior.

#### Validation

Unit and orchestration cases cover the exact compatibility boundary, older
initiating applications, canonical historical updates, changed/cleared names,
future verified versions, and an incompatible cached staged payload. The CI
workflow also verifies the SHA-256-pinned RC4.5.1 and RC4.6.1 archives. RC4.6.1 is
a low-level filesystem transaction regression; production custom updates to
that target are refused by the coordinator.

Before declaring RC4.6.2 integration complete, run the actual candidate/archive
transaction fixture with `DS4UPDATER_RC462_PACKAGE` and its independently verified
release SHA-256 in `DS4UPDATER_RC462_SHA256`. It verifies every payload file,
two consecutive selected names, sidecars, ownership, canonical dependency
retention, exact `5.0.8.0` / `VIIPERRC4.6.2` PE identity, and synthetic user-data
preservation. It does not execute an app, worker, broker, or installer.

Test reports and local publish outputs are retained under `_results/updater-2.0.7`.
Local builds are validation evidence, not evidence of public asset delivery.

Local pre-publication validation: **386 passed, zero failed, one explicit skip**
(the not-yet-built RC4.6.2 archive fixture), out of 387 tests. Both immutable
historical archive fixtures ran. The two architecture publishes each produced
one self-contained executable with version `2.0.7`. Seventeen pure PowerShell
release-identity cases passed, including no-overwrite/duplicate/mismatched-digest
rejection and the release workflow's exact source/tag guard.

<a id="inherited-release-208"></a>

### release-2.0.8

Original record: `docs/release-2.0.8.md`. Instructions and validation claims apply to that release.

### DS4Updater 2.0.8 — Updates That Stay in Place

This update makes updating DS4Windows easier, whether you use the installer or a portable folder.

- Fixes the **Unrecognized portable update option: -autolaunch** error when updating an installed copy.
- Installed copies now open the matching DS4Windows installer after verifying the download. Your existing app stays open while the update is prepared.
- Portable copies continue updating in their own folder, keeping profiles, settings and custom executable names.
- Canceled or failed downloads leave your existing installation unchanged.
- Checks the selected release and its files before opening Setup or applying a portable update.

DS4Windows RC4.6.6 uses this updater automatically. If an older installed version cannot update itself, run the complete RC4.6.6 installer once to get the corrected update path.

#### Release coordination

Tag: `v2.0.8`. Publish the exact successful source-CI artifacts for x64 and x86 to a draft, verify their digests and versions, then publish as the stable updater before DS4Windows RC4.6.6. The published-event workflow independently rebuilds and checks immutable asset identity; it must not overwrite a mismatch.

The new managed-safe-v1 handoff accepts only the registered installation and uses the verified matching AIO installer. Existing portable-safe-v1 handoffs remain compatible. No installer is executed by the automated update tests.
