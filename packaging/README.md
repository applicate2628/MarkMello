# Packaging Baseline

This folder captures the first release baseline from `ADR-0004`.

## Release matrix

Applicate fork Windows assets:

- `MarkMello.Applicate-setup-win-x64.exe`
- `MarkMello.Applicate-setup-win-arm64.exe`

The GitHub Actions release workflow currently publishes the Applicate Windows
installer assets only. Applicate-branded macOS packaging is intentionally
disabled until the fork has macOS-specific app bundle metadata and asset names.

Upstream baseline assets:

- `MarkMello-setup-win-x64.exe`
- `MarkMello-setup-win-arm64.exe`
- `MarkMello-macos-arm64.dmg`
- `MarkMello-macos-x64.dmg`
- `MarkMello-linux-x86_64.AppImage`

## Windows

- `windows/MarkMello.iss` is the per-user Inno Setup installer.
- `windows/build-installer.ps1` compiles the installer from a published app folder.
- `windows/MarkMello.Applicate.iss` is the Applicate fork installer metadata.
- `windows/build-applicate-installer.ps1` compiles the Applicate fork installer
  from a published `MarkMello.Applicate` app folder.
- `windows/sign-files.ps1` signs published binaries and installers when a PFX certificate is provided.
- `windows/markmello-installer.ico` is the installer icon generated from the shared master icon.
- The installer registers MarkMello as an available `.md` handler and adds `Open with MarkMello`-style shell integration without forcing a system-wide default.
- `.github/workflows/release-windows.yml` is the desktop GitHub Releases pipeline for Windows and macOS assets.

GitHub Actions flow:

- pushing a `v*` tag runs verify -> draft release creation -> Windows installers + macOS DMGs -> asset upload -> publish
- tags with prerelease suffixes like `v0.6.0-beta.1` stay GitHub prereleases and are not marked as latest
- `workflow_dispatch` can create or refresh a release manually and optionally keep it as a draft after upload

Example:

```powershell
dotnet publish .\src\MarkMello.Desktop\MarkMello.Desktop.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false `
  -p:MarkMelloReleaseOwner=dartdavros `
  -p:MarkMelloReleaseRepo=MarkMello `
  -o .\publish\win-x64

.\packaging\windows\build-installer.ps1 `
  -PublishDir .\publish\win-x64 `
  -RuntimeId win-x64 `
  -Version 0.6.0
```

Applicate fork example:

```powershell
npm --prefix .\src\MarkMello.Applicate.Desktop install
npm --prefix .\src\MarkMello.Applicate.Desktop run build:renderer

dotnet publish .\src\MarkMello.Applicate.Desktop\MarkMello.Applicate.Desktop.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false `
  -p:MarkMelloReleaseOwner=applicate2628 `
  -p:MarkMelloReleaseRepo=MarkMello `
  -o .\publish\applicate-win-x64

.\packaging\windows\build-applicate-installer.ps1 `
  -PublishDir .\publish\applicate-win-x64 `
  -RuntimeId win-x64 `
  -Version 0.6.0 `
  -OutputDir .\artifacts\applicate-win-x64
```

The Applicate installer installs as `MarkMello Applicate`, uses
`MarkMello.Applicate.exe`, and has a fork-specific AppId and ProgId so it does
not reuse the upstream MarkMello installer identity.

The Applicate fork currently targets .NET SDK 10. Release workflows and local
packaging hosts must install .NET 10, not the older upstream .NET 9 baseline.

Local installer builds require Inno Setup 6. On Windows it can be installed with:

```powershell
winget install --id JRSoftware.InnoSetup --source winget
```

## macOS

- `macos/Info.plist` is a template for the signed `.app` bundle metadata.
- `macos/MarkMello.icns` is the bundle icon generated from the shared master icon.
- `macos/build-app-bundle.sh` assembles an unsigned `.app` bundle from a `dotnet publish` folder.
- `macos/build-dmg.sh` wraps that bundle into an unsigned `.dmg` for GitHub Releases.
- The template declares Markdown document handling through bundle metadata, not installer hacks.
- Replace `$(MARKMELLO_BUNDLE_ID)`, `$(MARKMELLO_VERSION)`, and `$(MARKMELLO_BUILD_NUMBER)` in the release pipeline before building the DMG.
- Without an Apple Developer account, the macOS release stays unsigned and non-notarized. Users should expect Gatekeeper to require an explicit first-run approval after download.

## Linux

- `linux/markmello.desktop` is the desktop entry baseline for AppImage packaging.
- `linux/markmello.png` is the launcher icon generated from the shared master icon.
- The desktop entry advertises Markdown support through `MimeType=text/markdown;` and includes AppImage-specific metadata keys.
- The first packaging pass intentionally stays AppImage-first instead of adding distro-specific installers.

## Shared icon source

- `packaging/assets/base-803-crop.png` is the icon master tracked in the repository.
- `packaging/generate-icons.py` regenerates Windows `.ico`, macOS `.icns`, and Linux `.png` assets from that master.
- The generator preserves the original artwork framing and only resizes it for platform-specific formats.

Example:

```powershell
py .\packaging\generate-icons.py
```

## Update source

The in-app `Settings -> Updates` flow reads GitHub Releases coordinates from the desktop build metadata:

- `MarkMelloReleaseOwner`
- `MarkMelloReleaseRepo`
- `MarkMelloReleaseAssetPrefix`

Those values default in the selected desktop project (`src/MarkMello.Desktop/MarkMello.Desktop.csproj` for upstream builds, `src/MarkMello.Applicate.Desktop/MarkMello.Applicate.Desktop.csproj` for Applicate builds), and can be overridden in CI or local release builds with MSBuild properties. Applicate uses `MarkMello.Applicate` as its update asset prefix.

## Signing and notarization

This baseline prepares the repository for signed distribution, but actual signing credentials stay out of source control:

- Windows signing should be injected into the release pipeline when calling `signtool`.
- macOS signing and notarization should be added later, once Apple Developer ID credentials are available.

### GitHub Actions secrets for Windows signing

If you want the Windows workflow to sign artifacts, configure:

- `WINDOWS_SIGNING_CERT_BASE64` — base64-encoded `.pfx`
- `WINDOWS_SIGNING_CERT_PASSWORD` — password for that certificate

## Terms and Abbreviations

- `AppId`: Inno Setup application identifier used to distinguish installed apps.
- `Applicate`: the fork-specific MarkMello overlay.
- `AppImage`: portable Linux application packaging format.
- `DMG`: macOS disk image distribution format.
- `Inno Setup`: Windows installer generator used by this repository.
- `PFX`: certificate container format used for Windows code signing.
- `ProgId`: Windows file-association program identifier.
- `runtime ID`: .NET target runtime such as `win-x64` or `win-arm64`.
