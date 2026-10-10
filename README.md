# DS4Updater

The companion updater for [Pav-Osmolski/DS4Windows](https://github.com/Pav-Osmolski/DS4Windows),
continuing [hbashton's updater](https://github.com/hbashton/DS4Updater) and earlier contributors.

## Use

DS4Windows downloads and launches its matching updater when needed. For manual
app updates, download from [DS4Windows Releases](https://github.com/Pav-Osmolski/DS4Windows/releases).
Standalone updater downloads are on [DS4Updater Releases](https://github.com/Pav-Osmolski/DS4Updater/releases):
`DS4Updater.exe` for x64 and `DS4Updater_x86.exe` for x86. The current DS4Windows
VIIPER packages require Windows x64; an x86 updater does not add x86 app support.

The updater supports installed and portable updates, preserving profiles and
custom portable executable names while checking package identity, size and hashes.
RC4.6.7 users need one manual app upgrade because that app still checks upstream.
Replacing the updater alone cannot change the old app's release source.

## Contribute

See [Documentation](docs/README.md) for build, test and compatibility details,
[CHANGELOG.md](CHANGELOG.md) for release history, and
[Issues](https://github.com/Pav-Osmolski/DS4Updater/issues) to report updater bugs.

Licensed under GPL-3.0. See [COPYING](COPYING).
