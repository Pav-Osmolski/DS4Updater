# DS4Updater 2.0.8 — Updates That Stay in Place

This update makes updating DS4Windows easier, whether you use the installer or a portable folder.

- Fixes the **Unrecognized portable update option: -autolaunch** error when updating an installed copy.
- Installed copies now open the matching DS4Windows installer after verifying the download. Your existing app stays open while the update is prepared.
- Portable copies continue updating in their own folder, keeping profiles, settings and custom executable names.
- Canceled or failed downloads leave your existing installation unchanged.
- Checks the selected release and its files before opening Setup or applying a portable update.

DS4Windows RC4.6.6 uses this updater automatically. If an older installed version cannot update itself, run the complete RC4.6.6 installer once to get the corrected update path.

## Release coordination

Tag: `v2.0.8`. Publish the exact successful source-CI artifacts for x64 and x86 to a draft, verify their digests and versions, then publish as the stable updater before DS4Windows RC4.6.6. The published-event workflow independently rebuilds and checks immutable asset identity; it must not overwrite a mismatch.

The new managed-safe-v1 handoff accepts only the registered installation and uses the verified matching AIO installer. Existing portable-safe-v1 handoffs remain compatible. No installer is executed by the automated update tests.
