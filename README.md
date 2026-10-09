# DS4Updater

The companion updater for [Pav-Osmolski/DS4Windows](https://github.com/Pav-Osmolski/DS4Windows).
This fork continues the work of [hbashton/DS4Updater](https://github.com/hbashton/DS4Updater)
and earlier contributors.

## Download and use

DS4Windows downloads and launches its matching updater when an update is needed.
For manual app downloads, use the [DS4Windows releases page](https://github.com/Pav-Osmolski/DS4Windows/releases).
Updater releases will be available on the [DS4Updater releases page](https://github.com/Pav-Osmolski/DS4Updater/releases).
Choose `DS4Updater.exe` for x64 or `DS4Updater_x86.exe` for x86 when a manual updater download is required.

**Release status:** Fork update support is being prepared for **2.0.9**; it has
not been released yet. Until the coordinated app and updater releases are
available, download DS4Windows updates manually from this fork.

## Fork update support

Version **2.0.9** targets this fork's DS4Windows releases. It validates download
URLs and build receipts against `Pav-Osmolski/DS4Windows`, retaining SHA-256,
size, release identity, and package ownership checks. It supports both managed
installer updates and transactional portable updates that preserve profiles,
custom executable names, and the `Lang` translation layout.

DS4Windows also needs the corresponding update-source change. The already
published RC4.6.7 still checks upstream: updating DS4Updater alone cannot
change that release's update checks. Install the first DS4Windows release
containing the fork update-source change manually.

## Build and test

Use Windows and the .NET 8 SDK:

```powershell
dotnet test .\Updater2.Tests\DS4Updater.Tests.csproj -c Release
dotnet publish .\Updater2\DS4Updater.csproj -c Release -r win-x64 --self-contained true /p:Platform=x64 /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true
```

Real-package tests run in GitHub Actions with pinned historical upstream
fixtures and this fork's RC4.6.7 package. Those upstream fixtures are test data,
not update sources. Build artifacts include `DS4Updater.exe` for x64 and
`DS4Updater_x86.exe` for x86.

## Release coordination

Publish `v2.0.9` with both verified executable assets before releasing the
DS4Windows update-source change, which requires updater 2.0.9 or newer. Do not
replace assets on an existing upstream version or change DS4Windows RC4.6.7.

Licensed under GPL-3.0. See [COPYING](COPYING).
