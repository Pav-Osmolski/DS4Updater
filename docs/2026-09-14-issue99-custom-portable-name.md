# DS4Windows #99 — custom portable executable updates

> **Historical record.** This preserves the source, tests and limitations recorded at the time; it is not current setup guidance. See the [documentation index](README.md).


Source fix for [DS4Windows issue #99](https://github.com/hbashton/DS4Windows/issues/99).
This fix is prepared for updater **2.0.7**; publication is coordinated separately.

The old transaction added the configured alias while also reinstalling the
default `DS4Windows.exe`. The corrected transaction installs the apphost under
the selected name and records that transformed ownership, without changing the
downloaded archive or its manifest. Canonical `DS4Windows.dll`, runtimeconfig and
dependency files remain because the apphost still targets that managed assembly.
Alias sidecars are refreshed as well.

Owned default executables and obsolete owned aliases use the normal backup,
delete, verification and rollback path. Unowned collisions reject before live
mutation; unowned historical files are preserved. The setting file is never
rewritten and an existing setting is held read-only through apply/rollback.
An initially missing setting must remain missing throughout the transaction.

The external `--portable-safe-v1 --launchExe` contract remains compatible with
existing DS4Windows. The launcher resolves the saved name and binds both the
initiating image and selected destination into the immutable worker request.
`--originalExe` is internal-worker-only. Pre-update identity checks use the old
image; staged checks use the canonical package; final verification and Open use
the selected destination. Name changes and clearing a name therefore work even
when the destination does not exist yet. Exact-root process checks watch old,
new and default images without stopping any process; a live initiating PID and
start time must match the original exact executable, not just any adjacent image.

## Verification

- Final x64 Release suite: **358 passed, zero failed, one explicit skip** out of
  359 tests. The skip is the historical RC4.5.1 ZIP fixture, whose archive is not
  present; it is not a skipped unit regression.
- Actual published RC4.6.1 ZIP fixture enabled with `DS4UPDATER_RC461_PACKAGE`:
  `137650321` bytes, SHA-256
  `0B5B05E491AA01F6EAC58A33B1742FEA62481F7ABDBBC5EBAAE64BE7E7604541`.
  Two complete custom-name transactions verify all 553 archive entries,
  transformed ownership, alias/sidecar hashes, required canonical dependencies,
  PE identity `5.0.7.0` / `VIIPERRC4.6.1`, absence of the default executable,
  removal of the previous owned alias and preservation of synthetic user data.
- Unit regressions cover missing/present default EXEs, late-failure rollback,
  consecutive updates, changed/cleared names, unsafe and unowned collisions,
  locked/concurrently created settings, case-insensitive Windows names,
  worker-record tampering, and exact original/destination process identities.
- Independent review checked the worker/coordinator/transaction contract and
  the matching DS4Windows protected-setup-staging compatibility change.

Evidence: `_results/issue99-final/issue99-final.trx`. All filesystem transactions
target generated disposable fixtures. No DS4Windows, broker, installer or updater
executable is launched, and no production folder or real user profile is changed.
This does not claim a live GUI update/reboot test or public delivery.

Delivery must coordinate with the DS4Windows custom-only manifest staging fix.
Older DS4Windows builds expect the canonical executable in their installed
ownership manifest when staging infrastructure setup. Do not independently
publish this updater and claim those older application builds also contain the
staging fix. A release must either supply the compatible application package or
explicitly gate the new installed-layout behavior to a compatible release.

Updater 2.0.7 enforces that boundary in the production worker before downloading
or preparing the ZIP: a custom-named destination requires the verified target
Windows binary version **5.0.8.0** (RC4.6.2) or later. Older target requests fail
without live changes, preserving their existing layout. Canonical-name updates,
including clearing a custom name, retain the existing historical update path.
Older initiating applications can still update to compatible newer targets;
future versions remain supported through verified release build records. The
staged PE identity must match that record, so a cached older archive cannot
bypass this gate. The RC4.6.1 fixture above covers low-level transaction mechanics,
not permission for the production coordinator to install its custom-only layout.
