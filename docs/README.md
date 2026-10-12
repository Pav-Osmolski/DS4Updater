# DS4Updater documentation

[Repository overview](../README.md) · [Release history](../CHANGELOG.md)

## Compatibility and use

DS4Updater supports compatible DS4Windows releases and schema 1 build receipts. Use the matching updater distributed with the app.

Managed installations use the matching verified Setup EXE. Portable updates
remain in their existing folder and preserve settings, profiles and custom
executable names. The updater verifies repository, release identity, download
ownership, size and SHA-256 before handing off or applying the package.

Quit games before updating. If an update fails, keep the diagnostic details and
use the full app installer or portable package from DS4Windows Releases.
Report updater problems with app/updater versions, installation type, selected
release and reproduction steps. Review logs for personal data first.

## Build and test

Use Windows with the .NET 8 SDK, from the repository root:

```powershell
dotnet test .\Updater2.Tests\DS4Updater.Tests.csproj -c Release
dotnet publish .\Updater2\DS4Updater.csproj -c Release -r win-x64 --self-contained true /p:Platform=x64 /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true
```

CI also checks real pinned package fixtures. Historical upstream fixtures are
test data, not update sources. Changes must keep the managed and portable handoff
contracts compatible with the matching app and preserve rollback behaviour.

## Release coordination

Publish both updater architectures under the updater's own version before
publishing an app that requires a newer updater. Do not reuse another release's
asset identity. DS4Windows app versions and updater versions are independent.
See [the app release process](https://github.com/hbashton/DS4Windows/blob/main/.github/workflows/release.yml).

## Historical validation

- [Portable updates, 2.0.5](portable-update-2.0.5-validation.md)
- [Custom portable executable name investigation](2026-09-14-issue99-custom-portable-name.md)

These records describe the source and fixtures tested at the time. Current
release notes live only in [CHANGELOG.md](../CHANGELOG.md).
