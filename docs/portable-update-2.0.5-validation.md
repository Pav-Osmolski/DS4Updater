# Portable updater 2.0.5 — local validation, 2026-09-09

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](README.md).


This is a source/build checkpoint, not a publication record. The public updater
was still 2.0.4 when checked during this work. No installed DS4Windows, VIIPER,
controller session, or real user portable directory was updated by these tests.

## Implemented path

The portable caller passes an exact release tag, its executable name, and its
PID plus process-start identity. The updater creates a verified single-file
worker under that portable folder's `Updates` directory. The worker validates
the request independently; it does not run the legacy process-killing path.

Release selection requires the intended published release, a unique matching
x64 ZIP, its declared length and SHA-256, and consistent release/PE versions.
Downloaded contents are bounded and staged before replacement. Both the
existing and incoming ownership manifests are required. User profiles, XML,
pairing data, keys, plugins, logs, and unknown files are not package-owned.

The updater waits for exact-folder applications to exit without killing them.
It preflights affected files, journals backups, performs checked replacements,
and rolls back its own successful mutations on failure. Concurrent user changes
are not silently overwritten during rollback. An incomplete recovery retains
its evidence and blocks a new transaction; this is not a claim of power-loss
atomicity for an entire directory.

Success offers an explicit Open DS4Windows button. Failure never launches a
partly updated app. Double-clicking an updater in a portable or worker folder
without the safe request fails safely instead of falling into legacy behavior.

## Evidence

- The full updater suite passed **240 / 240**, no skips, after independent
  review corrections, with `DS4UPDATER_RC451_PACKAGE` pointing to the unchanged
  released RC4.5.1 portable ZIP. The normal suite without that opt-in path skips
  only the real-release fixture.
- Self-contained compressed win-x64 single-file build succeeded with the same
  compression and platform settings used by the release workflow.
- Local output: `isolated_results/updater-2.0.5/publish-final/DS4Updater.exe`.
- File version: `2.0.5`; length: `71,723,955` bytes.
- SHA-256: `78DFD54946FED89269BFA349BE0768CED563F19414A76C3C84FB14C081A21259`.
- Native Windows PE version lookup now supports long paths for current,
  staged, and custom-named executables; test staging commonly exceeds MAX_PATH.
- Existing legacy async warnings remain; the single-file Assembly.Location
  warning corresponds to an intentional single-file admission check.

Tests use synthetic archives, fake process inventories, controlled clocks and
HTTP responses, and isolated temporary directories. They cover damaged ZIPs,
path/reparse rejection, manifest and checksum validation, process identity,
file locks, rollback/recovery, redirects/deadlines, entry-point selection,
version downgrade rejection, and launch admission. The additional real-release
fixture stages and applies the SHA-pinned RC4.5.1 archive into a new temporary
target, compares every installed payload hash, and preserves synthetic profiles,
pairing records, keys, plugins and logs. No application is executed.

## Delivery boundaries

This build is not publicly deployed. Portable auto-updates must remain gated
until the safe updater is published and the DS4Windows bootstrap is delivered.
The safe portable path currently targets x64 packages. Named future release
tags must be added to the verified release-version mapping before publication;
unknown names are rejected rather than guessed. A launched updater-worker
end-to-end acceptance remains distinct from the verified real-ZIP filesystem
transaction and fake-process orchestration tests.

## RC4.5.2 publication preparation

The verified named-release mapping now includes `VIIPERRC4.5.2` as Windows file
version `5.0.5.2`. Additional cases cover forward-only channel ordering, rejection
of RC4.5.1 binaries or metadata under the new tag, and exact RC4.5.2 asset URLs
and names. Unknown future named releases remain rejected.

- Full suite: **245 / 245 passed, zero skips**, including the unchanged RC4.5.1
  archive at `C:\Users\hbash\Desktop\DS4Windows-RC4.5.1-Publish-2026-09-09\release-assets\DS4Windows_VIIPER_x64.zip`.
- Fixture length: `134,922,601` bytes; SHA-256:
  `985B7DA39AB682FAE9ED27EC4B1622F58C240911454CFFC29CD661EC13BBA109`.
- Test report: `isolated_results/updater-2.0.5/rc452-tests/updater-2.0.5-rc452.trx`.
- Compressed self-contained win-x64 single-file output:
  `isolated_results/updater-2.0.5/publish-rc452/DS4Updater.exe`.
- File version: `2.0.5`; length: `71,723,991` bytes; SHA-256:
  `ED2BF12031747502C8D8283689438ACF224DC2EE91FCBD76E0429EBD7C06A65A`.
- The workflow's existing win-x86 build lane also passed with the same publish
  flags and one versioned executable under `publish-rc452-x86`. The safe portable
  package path still selects x64 DS4Windows archives.
- This local build predates the release commit. Its ProductVersion includes the
  current checkout hash, so it must not be described as a build from a future
  release commit. Rebuild and record final hashes after the release commit.

Validation commands from the updater repository:

```powershell
$env:DS4UPDATER_RC451_PACKAGE = 'C:\Users\hbash\Desktop\DS4Windows-RC4.5.1-Publish-2026-09-09\release-assets\DS4Windows_VIIPER_x64.zip'
dotnet test .\Updater2.Tests\DS4Updater.Tests.csproj -c Release --logger 'trx;LogFileName=updater-2.0.5-rc452.trx' --results-directory .\isolated_results\updater-2.0.5\rc452-tests
dotnet publish .\Updater2\DS4Updater.csproj -c Release -r win-x64 --self-contained true /p:Platform=x64 /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true -o .\isolated_results\updater-2.0.5\publish-rc452
```

The existing workflow already used the required compressed single-file flags.
Its test job now downloads the immutable fixture and checks the pinned SHA-256
before running the complete suite. Asset preparation rejects extra output files,
a mismatched PE file version, or a release tag other than the exact project
version prefixed with `v`. Upload no longer uses `--clobber`; an existing asset
name causes a safe failure rather than replacing published bytes. Therefore a
release with manually pre-uploaded assets must account for the existing
`release: published` upload job. These workflow changes have not run on GitHub.

No commit, push, tag, publication, installed-app update, or physical-controller
session was performed in this preparation. The launched-worker end-to-end
acceptance gap described above remains open.
