# Client setup and release contract

Use the official Mushroom Launcher with an existing, lawfully obtained client.
This fork changes server behavior; it does not replace the original game executable
or introduce a second launcher protocol.

## Workspace ownership

```text
MapleStory2\
  client\                         one local game installation
    Data\
    Custom\
    Config\
    x64\MapleStory2.exe
    x64\AgarciumClient.dll
    x64\NxCharacter64.dll
    x64\NxCharacter64.dll.bak
    Mushroom Launcher.lnk
  server\                         this Git checkout, .env, scripts and server sources
  release\                        generated source-only setup kits and future release artifacts
```

Keep original client support libraries and custom assets with the client. The
metadata importer reads the same `client\Data` directory without modifying it.
Preserve the existing Compose project name and its MySQL, Web-data, and navigation
volumes when moving the server checkout.

Mushroom itself remains in its normal **per-user Squirrel installation**:

```text
%LOCALAPPDATA%\mushroom_launcher\Mushroom Launcher.exe
%APPDATA%\Mushroom Launcher\app-config.json
%APPDATA%\Mushroom Launcher\mods\
```

Do not move the installed launcher into the client directory or hardcode an
`app-2.x.y` target. Squirrel manages versioned application directories and updates.
The client-local shortcut targets the stable installed executable.

## Install on another Windows PC

### Private-pilot Windows setup

**The Windows preview is not cleared for distribution.** See the
[installer verification status](#installer-verification-status) before building
or running it. The manual setup below remains the supported alternative.

The MapleTime MS2 setup executable is a **bootstrap installer**, not a bundled
game download. It selects an existing x64/NA client, installs or reuses official
Mushroom, checks the Microsoft x64 runtime, and adds the Azure profile. Game files
and existing profiles/credentials are not copied or replaced.

The wizard requires the tested `x64\MapleStory2.exe` fingerprint below. Missing
prerequisites are downloaded directly from their publishers and checked against
the hashes in `installer\distribution.json`. Microsoft runtime installation may
request administrator approval; the helper itself installs per user and does not
mark the game to run as administrator. If the runtime requires a restart, setup
stops before configuring the client; restart Windows and run setup again.

Use the Start-menu **MapleTime MS2** shortcut (**MapleTime MS2 Preview** for preview
builds), select **MapleTime MS2 Azure** in
Mushroom, and create your account at the separate HTTPS registration page.
Only operator-approved networks can access this pilot. Uninstalling the setup
removes its own helpers/shortcuts, not Mushroom, game files, profiles or accounts.

Current builds are unsigned. Check the setup's SHA-256 and keep antivirus
protection enabled. The observed MSVC 14 x64 dependency is covered, but complete
clean-PC and in-game acceptance remains a separate release gate.

### Manual setup

1. Supply the supported original client, including its `Data` and `x64`
   directories. Do not download a random replacement executable or copy another
   user's configuration/credentials.
2. Install [official Mushroom Launcher 2.0.3](https://github.com/shuabritze/mushroom-launcher/releases/tag/v2.0.3).
   The verified installer is
   [Mushroom.Launcher-2.0.3.Setup.exe](https://github.com/shuabritze/mushroom-launcher/releases/download/v2.0.3/Mushroom.Launcher-2.0.3.Setup.exe).
3. Close Mushroom, configure the actual client root, then open the new
   `Mushroom Launcher.lnk` inside that root.
4. Register through the operator's registration website, then launch and sign in
   manually. For this local installation the website is
   `http://localhost:4000/account`; this is **not** an address for remote players.

From the server checkout:

```powershell
.\scripts\configure_client.ps1 -ClientPath "D:\MapleStory\MapleStory2\client"
```

From an extracted setup kit:

```powershell
.\Configure-Client.ps1 -ClientPath "C:\Games\MapleStory2\client"
```

For a server on another machine, supply its real Login endpoint:

```powershell
.\Configure-Client.ps1 -ClientPath "C:\Games\MapleStory2\client" `
    -LoginHost "game.example.org" -LoginPort 20001 -ServerName "My Maple2 Server"
```

`game.example.org` is an example, not a service supplied by this repository.
The script preserves existing profiles and their optional credentials, adds a
credential-free profile only when necessary, disables autologin/console, and
updates `clientPath` atomically. It never downloads, patches, or starts the game.
Use Mushroom's UI to remove saved credentials when they are no longer wanted.

Mushroom stores optional credentials as ordinary JSON and passes them in process
arguments. Never include its user-data directory in a release. Autologin also
disables manual client login fields, so leave it off for the documented flow.
New registration passwords are 8-16 characters to fit the tested native field.

The current launcher uses Electron 35 and requires 64-bit Windows 10 or newer.
Selecting an existing client does not need the server SDK, MySQL, or Docker.
Mushroom's optional Steam downloader separately needs a .NET 9-or-newer runtime;
this setup kit does not invoke it. A complete VC++/DirectX prerequisite matrix
still needs verification on a clean PC; do not remove bundled runtime libraries
based only on what is installed on the development workstation.

Installer verification, using a download obtained directly from the author:

```powershell
Get-FileHash .\Mushroom.Launcher-2.0.3.Setup.exe -Algorithm SHA256
```

Expected size: `384726016` bytes. Expected SHA-256:

```text
5dba751024cca4e220eb4931bff39865c4d43b60c8fb5932231998262fb636fd
```

Squirrel supports `--silent` for installation without a first application launch.
Installers and update feeds remain upstream-owned; no portable Windows ZIP is
published for Mushroom 2.0.3.

## What is upstream, and what differs here

| Surface | Upstream contract and this fork |
|---|---|
| Launcher | The official wiki links `shuabritze/mushroom-launcher`. We use that installed application, not a renamed game executable or portable repack. |
| Game entrypoint | Mushroom appends `x64\MapleStory2.exe` to the selected client root. Do not select the `x64` directory itself. |
| Native add-ons | Current Mushroom installs `AgarciumClient.dll` and the `NxCharacter64.dll` proxy during launch preflight. The older README reference to `Maple2.dll` does not describe the v2 implementation. |
| Original Nx library | The proxy loads the genuine `NxCharacter64.dll.bak`. **It is a runtime dependency, not disposable backup clutter.** Never recreate it from the replacement proxy. |
| Launch arguments | `--nxapp=nxl --ip=<Login host> --port=<Login port>` matches current source. Console, credentials, token, title, mods, and autologin are optional. Mushroom does not set a child `cwd`; its child inherits the shortcut's working directory. |
| Authentication | Upstream auto-registers unknown logins. This fork intentionally requires explicit Web `/account` registration and verified passwords in every build. Do not restore Debug or blank-password bypasses. |
| Server topology | We retain an instanced channel 0 and normal channel 1. Login is TCP 20001; instanced Game is 20002; normal Game is 20003. A launcher profile uses Login, not a Game port. |
| Client data | The wiki places customized `Server.m2d/.m2h` and optional fixed/translated `Xml.m2d/.m2h` in `Data`. They are separate from the launcher's native DLL patches. We retain the tested data rather than downloading over it automatically. |
| Local test launch | Earlier diagnostics launched the existing client directly from an isolated copy. That is a diagnostic technique, not the distribution/install workflow. The organized workspace has one canonical client. |
| Metadata and operations | Explicit read-only ingestion, database target guards, persistent volumes, and ordered Compose scripts are maintained fork improvements. They do not change the client installation format. |

The inspected local mod directory is empty; the active client extension layer is
the upstream native patcher/proxy plus the customized data archives. Server-side
fixes remain in the server repository, not hidden in a replacement client EXE.

### Tested compatibility set

The upstream downloader pins Steam app `560380`, depot `560381`, manifest
`3190888022545443868`. The server uses protocol `12`, locale `NA`.
The local executable reports `20.8.1.0316`; this is an observed file version,
**not an independently certified upstream version string**.

These fingerprints identify the retained, tested local set. Its data-release
provenance is not inferred from a matching filename or a moving `latest` tag.

| File | SHA-256 |
|---|---|
| `x64\MapleStory2.exe` | `8123200cdffbeaf98264359ded1330d3f2c1a03fdef8332b7347b233c2355182` |
| `x64\AgarciumClient.dll` | `1905174f76c6dd3dcfb4b3aab0a101ee80d8ada4113abac9b396a8523f469885` |
| `x64\NxCharacter64.dll` | `1a6fdd18bd596a8601bcf8c883ed5fe885d0a64b823db2496719b9c9d7fa9754` |
| `x64\NxCharacter64.dll.bak` | `e742ca44b24aad0787d73c5da530eed16fa945bd539b1ab00ed0408ab69d0949` |
| `Data\Xml.m2d` | `5fb4aad6323c584f142f31094320ab506e55db9722b2d4b0d8ebab2f2c2a1ca6` |
| `Data\Xml.m2h` | `6c104c74bab195bf857643d8e0d8a3ab55e6b2c720d30995b62d39f61818f14e` |
| `Data\Server.m2d` | `59f84156c34530a19e07f1c31e1eff38ef9fe900875a4ce9460f6b7584678b33` |
| `Data\Server.m2h` | `8bd1d0084c35f6fc71dfc7ad9607054f1df8f9f738c7cdbd8d49db5298629097` |

The two installed native patches already match the official 2.0.3 installer.
Workspace reorganization did not rewrite them, the original EXEs, or `Config.ini`.
Mushroom can update its bundled patches during a later Launch; revalidate the
compatibility set when updating the launcher or client data.

Do not fetch `latest/download/Server.m2d` blindly: the XML repository's latest
release at review time was an Xml-only nightly. Its complete `v1.3.2` release is
a separate artifact set, not automatically the set installed here.

## Build and distribute the setup kit

From a clean, committed server checkout:

```powershell
.\scripts\build_client_release.ps1
```

This creates a revision-named directory, ZIP, and SHA-256 sidecar in sibling
`release`. Its explicit allowlist is:

- `Configure-Client.ps1`
- `README.md` (this document)
- `LICENSE`
- `SOURCE.txt` (the exact source revision)

The kit contains **no game archives, EXEs, native patch DLLs, original Nx backup,
upstream installer, Web uploads, `.env`, credentials, or user profiles**.
It is a source-only configuration kit, not a full game installer. Other PCs obtain
Mushroom directly from its author and provide their own lawful base client.

Launcher and Agarcium source are MIT, but this does not license the original game.
The upstream installer also bundles a GPL-v2 modified downloader whose exact
corresponding-source mapping was not established in this review. Link the
official installer instead of repackaging it as an MIT-only payload. No general
redistribution permission was established for the proprietary game or the XML
repository's generated data archives.

This server and the authored setup scripts are AGPL-v3. Retain the license and
make the deployed modified server's corresponding source available to remote
users. A future full-game installer still needs a separate reviewed payload/license
manifest and code-signing process.

### Build the Windows bootstrap

Install Inno Setup 7 and the build-only image dependency, then run:

```powershell
python -m pip install -r .\installer\requirements.txt
.\scripts\build_client_installer.ps1 `
    -InnoCompiler "C:\Program Files\Inno Setup 7\ISCC.exe" -Preview
```

`-Preview` labels an uncommitted local build explicitly. Without it, the builder
requires a clean checkout, just like the existing source-only ZIP builder.
Neither command commits, publishes or changes the selected game installation.
The executable, SHA-256, build manifest and exact authored-source ZIP are written
to sibling `release`. The installer also carries that source ZIP; private settings,
game archives and upstream installers are not embedded.

The compiler uses MS2 artwork already approved for this project. Asset and
prerequisite hashes are pinned in `installer\distribution.json`; Microsoft runtime
14.44.35211 was checked for a valid Microsoft Authenticode signature when pinned.
Publisher downloads keep their own licenses; the authored bootstrap is AGPL-3.0.

For source checks and an isolated compiled install/reinstall/uninstall exercise:

```powershell
# Source, compiler-version, runtime-version and archive contracts; no EXE runs.
.\scripts\test_client_installer.ps1

# Optional native lifecycle; only after resolving the antivirus gate below.
.\scripts\test_client_installer.ps1 `
    -InnoCompiler "C:\Program Files\Inno Setup 7\ISCC.exe"
```

The smoke build uses inert client/launcher fixtures, private compiler definitions
and a separate uninstall identity and shortcut names. It does not execute a game,
install publisher prerequisites or modify real profiles. It checks wrong roots,
incompatible executables, overlapping/nonempty helper folders, malformed settings,
profile preservation, shortcuts, reinstall, failed prerequisites and uninstall.
Native execution requires a completed Windows Defender scan; a skipped scan is
not accepted. Failures retain their fixture path and logs for inspection.

Without `-InnoCompiler`, compiler behavior is simulated with inert text output;
these checks do not establish native installer readiness. `-InstallerPath` can
also verify a built EXE's checksum, manifest and exact-source archive without
executing it. No test disables antivirus protection or restores quarantined files.

### Installer verification status

The unsigned preview built on 2026-09-12, and source/build contracts passed on
Windows PowerShell 5.1 and PowerShell 7. A real installation confirmed the exact
helper-only payload, embedded source and correct Mushroom shortcuts. Existing
profiles and credentials were preserved; JSON formatting was normalized.
The separately scanned, unflagged uninstaller removed its helpers, both shortcuts
and uninstall registration while retaining Mushroom, settings and client files.
No game was launched.

**Distribution is blocked:** Windows Defender subsequently quarantined the
real preview, as well as synthetic test builds, as
`Trojan:Win32/Bearfoos.A!ml`. This happened after initial scans reported no threats.
The affected real preview has source fingerprint prefix `d4e0934d4188`.
Its runnable EXE is withheld; build metadata and exact source are retained.
The detection has not been established as a false positive.

Repeated native acceptance, including reinstall, is therefore not a completed
gate. One earlier fixture also left its desktop shortcut behind; tests now release
their shortcut COM objects before uninstall, but the full updated lifecycle has
not passed. Antivirus/publisher review, an appropriate signing process and a full
clean-PC lifecycle are required before release. Do not add exclusions, disable
protection, restore quarantine or treat a SHA-256 match as a safety verdict.

### Remote-release gates

The existing setup ZIP remains source-only; it does not publish a full game installer.
Before inviting other computers:

1. Set real client-reachable IPv4 values for `LOGIN_IP` and `GAME_IP`; expose the
   configured Login/Game TCP ports through firewall/NAT. For LAN use,
   `CLIENT_BIND_IP=0.0.0.0` is a bind setting, not an advertised address.
2. Keep MySQL/gRPC private. Supply a real registration website behind HTTPS.
3. Make the game Web/UGC endpoint reachable and test it separately. Current
   `WEB_IP`/`WEB_PORT` constructs an HTTP URL; it is not an arbitrary HTTPS
   base-URL option.
4. Pin client, native add-ons, data archives, and server revision together.
   Back up player/uploads/navigation data and verify the update/rollback path.
5. Test a clean Windows machine through install, registration, manual login,
   character entry, instanced/normal transfers, Web/UGC, reconnect, and update.

### Website-to-game launch

The player entry point is `https://ms2.mapletime.dev`. The authored page now links
invited testers to the real HTTPS registration service, distinguishes the official
Mushroom launcher from a game-client download, and provides the pilot's manual
connection steps. The redesign was published on 2026-09-12 through website-only
[PR #9](https://github.com/gugarosa/Maple2/pull/9), revision
`f73db9073e932cb1042a7abd568dba0de49afd1c`. Live HTTPS asset, desktop/mobile,
light/dark and keyboard checks passed on `ms2.mapletime.dev`.

Set `PLAYER_WEBSITE_URL` on Web to link registration and its success page back to
the player setup guide. It is optional for local/custom servers and must be an
absolute HTTPS URL without credentials. The Azure Compose definition sets
`https://ms2.mapletime.dev/#getting-started`. This source change has not been
deployed to the current application release. It adds navigation, not shared MS1
accounts, cross-site password collection or automatic client login.

**A client download remains a separate release gate.** The old Steam store URL
for app `560380` redirected to Steam's homepage on 2026-09-12. Mushroom contains
Steam and third-party archive download options, but their presence is not proof
of current availability, client compatibility or redistribution permission.
Do not advertise a launcher download as the full game or link unreviewed mirrors.
The artwork permission does not establish permission to redistribute game files.

Before opening this journey to ordinary players, verify all of the following:

- The published website and registration hostname have working DNS/HTTPS, current
  instructions and functioning navigation back to setup.
- An approved client-acquisition route and antivirus-cleared installer supply the
  pinned compatibility set, with checksums, provenance and applicable licenses.
- A fresh player PC completes account creation, download/setup, manual login,
  character/world entry, transfers, assets and reconnect without copied operator
  settings. Include a second approved network before a public launch.
- A separately reviewed public-access rollout opens only required application
  ports while retaining private management, registration protections, backups,
  monitoring and the existing budget guard. The current `/32` pilot ingress and
  4-GiB capacity are not a public-player rollout.

Until then, leave public-launch claims and installer/client downloads disabled.
Do not loosen the private-pilot ingress template to make a website link appear
to work.

### Private Azure pilot (2026-09-12)

Access is limited to the operator-approved network, not the public Internet.
Create a separate account at
[`https://play.ms2.mapletime.dev/account`](https://play.ms2.mapletime.dev/account),
then select **MapleTime MS2 Azure** in Mushroom. Local accounts and characters were
not copied to Azure.

To add the credential-free profile while preserving the local one, close Mushroom
and run:

```powershell
.\scripts\configure_client.ps1 -ClientPath "D:\MapleStory\MapleStory2\client" `
    -LoginHost "20.226.79.46" -LoginPort 20001 -ServerName "MapleTime MS2 Azure"
```

Do not use the HTTPS registration URL or a Game-channel port as `LoginHost`.
Public v12 endpoints and native account authentication were checked from this
workstation; character/world entry remains a separate interactive check.
See [Azure operations](deploy/azure/README.md#running-application-pilot) for access,
capacity, budget and backup limits.

Mushroom's launcher updates use the upstream Electron update feed. Optional XML
mods use their own `mod.json` and file-hash URLs. Neither is this fork's account
API, server-discovery API, or a full-client update service; do not invent those
endpoints in a package.

## Troubleshooting

- **Shortcut target missing:** reinstall official Mushroom. Do not point the
  shortcut at the game executable and call it a launcher repair.
- **Old client path:** close Mushroom and rerun `configure_client.ps1` with the
  actual root. Changing the server's `.env` does not change Mushroom's settings.
- **Cannot type credentials:** disable autologin. Existing stored credentials
  can be removed in Mushroom; the setup script preserves them.
- **Missing Nx backup:** restore the genuine original library. Do not delete
  `.bak` as cleanup or copy the proxy into its place.
- **No playable channel immediately after startup:** allow the World channel
  monitor to register Game as active. Container health alone is not an end-to-end
  login check.

## Verified public sources (2026-09-11)

- [Official server prerequisites and client links](https://github.com/MS2Community/Maple2/wiki/Prerequisites)
- [Mushroom 2.0.3 release](https://github.com/shuabritze/mushroom-launcher/releases/tag/v2.0.3)
- [Pinned launch/preflight implementation](https://github.com/shuabritze/mushroom-launcher/blob/8709bd063a0e224378be43305535ba37dc74614b/src/app/launch-client.ts)
- [Pinned settings schema](https://github.com/shuabritze/mushroom-launcher/blob/8709bd063a0e224378be43305535ba37dc74614b/src/app/config.ts)
- [Squirrel packaging](https://github.com/shuabritze/mushroom-launcher/blob/8709bd063a0e224378be43305535ba37dc74614b/forge.config.ts)
- [Original Nx backup loading](https://github.com/shuabritze/Agarcium/blob/6935298ab34dc3644f7faba244fe5fba713627fc/NxCharacter64/dllmain.cpp)
- [Upstream Login/Game/Web address construction](https://github.com/MS2Community/Maple2/blob/7279d3b63fa7b6717426081722f7e76cd696b8f1/Maple2.Server.Core/Constants/Target.cs)
- [Complete data release](https://github.com/MS2Community/MapleStory2-XML/releases/tag/v1.3.2)
- [Xml-only nightly](https://github.com/MS2Community/MapleStory2-XML/releases/tag/nightly-2026-09-10)
