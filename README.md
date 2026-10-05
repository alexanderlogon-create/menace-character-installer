# MENACE Character Installer

Source snapshot of **Windows x64 preview 0.3.2**, published by AlexLogon for review of the optional installer on [Waybackers — Nexus Mods](https://www.nexusmods.com/menace/mods/262).

The installer previews the 22 bundled Waybackers squad leaders, lets the user select a subset, checks prerequisites, and installs that selection into a chosen MENACE folder. It also imports supported Jiangyu and Custom Leaders character-pack ZIPs. The interface supports Russian and English.

## Build

Requirements: Windows x64 and the **Microsoft .NET 10 SDK**. The release was built with SDK **10.0.401**. NuGet restores the package versions declared in the project files.

Clone this repository and run from its root in PowerShell:

```powershell
dotnet publish installer/MenaceCharacterInstaller.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

The result is `publish/MenaceCharacterInstaller.exe`. All required embedded character resources and the application icon are included here; no proprietary game DLL or developer SDK installation is needed to compile the application. MENACE and its prerequisite mods are needed to install characters into the game.

This publishes a self-contained .NET 10 WPF application. The game-side MelonLoader dependency uses .NET 6 Desktop Runtime separately.

## Review map

| Path | Purpose |
| --- | --- |
| `installer/MenaceCharacterInstaller.App/App.xaml.cs` | Startup, language settings and content verification mode |
| `installer/MenaceCharacterInstaller.App/MainWindow.xaml` and `.xaml.cs` | WPF interface, selection, dependency actions and installation |
| `installer/MenaceCharacterInstaller.App/BundledContent.cs` | Extracts embedded content to LocalApplicationData |
| `installer/MenaceCharacterInstaller.Core/Catalog.cs` | Catalog, download integrity and file transactions |
| `installer/MenaceCharacterInstaller.Core/GameInstall.cs` | Game discovery, inspection, duplicate checks and backups |
| `installer/MenaceCharacterInstaller.Core/Installer.cs` | Prerequisite detection, pinned dependency downloads and pack installation |
| `installer/MenaceCharacterInstaller.Core/DependencySetup.cs` | Individual dependency installation and Microsoft runtime setup |
| `installer/MenaceCharacterInstaller.Core/PackImport.cs` | Reads supported external character packs |
| `installer/MenaceCharacterInstaller.Core/BundlePortraits.cs` | Extracts portrait previews from imported assets |
| `installer/MenaceCharacterInstaller.Core/Localize.cs` | Russian and English interface strings |
| `installer/content/catalog.json` | Character metadata, package checksums and declared requirements |
| `installer/content/runtime.json` | Official Microsoft runtime download and checksum |
| `installer/content/packages` | Per-character and shared Waybackers data used by the released EXE |
| `installer/content/licenses` | Third-party license texts and notices |

## Installer behavior

- Finds supported Steam installations or uses the folder selected by the user.
- Checks MelonLoader, Jiangyu Loader, .NET 6 Desktop x64 and the selected pack's declared dependencies.
- Downloads pinned MelonLoader 0.7.3 and Jiangyu 1.4.7 files from their official GitHub releases, verifies SHA-256 checksums, and installs them when requested.
- If needed, downloads Microsoft's .NET 6 Desktop Runtime setup from Microsoft and starts its interactive installer with the standard Windows elevation prompt. It does not automatically restart Windows.
- **All Leaders Pickable is required by the bundled Waybackers catalog.** Its Download button opens [Nexus mod 16](https://www.nexusmods.com/menace/mods/16); the user downloads it there and selects the ZIP/DLL for installation. That mod is not bundled or automatically downloaded.
- Extracts bundled resources and stores settings, downloads, logs and backups beneath `%LOCALAPPDATA%\MenaceCharacterInstaller`.
- Installs character files into the selected game folder, preserves backups and offers rollback. Changing a selection can remove previously installed members of that same pack; keep existing campaign characters selected.
- Does not request Nexus credentials or send telemetry.

## Release 0.3.2

Fixes candidate-pool registration: selected infantry leaders and pilots are appended to the appropriate dossier pools. Shared dossier registrations no longer link all imported characters into a mandatory selection group. Native recruitment on the local game could not be retested because the game exited before Unity startup, including with mods disabled.

Automated verification: 56 pack assembly/rollback checks, 7 archive-import/recruitment checks, 28 dependency checks; shipped EXE content verification confirms 22 characters and 22 portraits.

Build the current main branch or tag v0.3.2 with the command above. The original v0.3.1 tag remains available for the earlier Nexus review.

| Artifact | SHA-256 |
| --- | --- |
| MENACE-Character-Installer-0.3.2-win-x64.zip | B3E86611ECB6B968DD9BF143EF66807C88A9F2F3F2EBF72B4184ABE79E8F8FD6 |
| EXE inside that ZIP | 16B06D1BAB2E2FA73E45C1493FAAB1C872A47A600C06F4CCEDA4081BAAC83A11 |

## Previous release 0.3.1

Nexus optional file: **MENACE Character Installer - Windows x64 Preview**, version **0.3.1**, file ID **1091**.

| Released artifact | SHA-256 |
| --- | --- |
| `MENACE-Character-Installer-0.3.1-win-x64.zip` | `D7522B609CEA7DFD47F9778F9CF29D2FD6303969BEA3031462C4612C5AF96912` |
| EXE inside that ZIP | `941E9A95DDAD4BB0990CB4068701B1E51675AC27D8DE747D7C390305CC135F34` |

The v0.3.1 tag contains the application code and embedded payload used for that previous release. Compiler, SDK and build-path differences can change a rebuilt executable's hash. The listed hashes identify the uploaded artifacts, not a promise of byte-identical builds.

Nexus quarantined the optional executable archive for review. Its specific automated detection reason is not known. This repository provides source and build instructions for that review; the quarantined binary is left on Nexus for staff inspection.

## Credits and scope

Waybackers artwork, voices and pack content: **AlexLogon**, from the published Waybackers 1.0.1 pack. Third-party libraries retain the licenses in `installer/content/licenses`. MENACE and its trademarks belong to their respective owners. This is an unofficial community tool.

This repository excludes personal Studio projects, saves, credentials, developer build caches and the SDK. It includes the complete embedded release payload so it can be compiled without a private asset download.
