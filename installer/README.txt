MapleTime MS2 - Private pilot setup

This installer prepares official Mushroom Launcher to use an existing,
compatible MS2 client. It does not include or replace game files.

1. Choose the client root containing Data and x64, not the x64 folder.
2. Setup installs or reuses Mushroom and checks the Microsoft x64 runtime.
   A missing runtime may require administrator approval. No server SDK,
   database or Docker installation is needed on a player's PC.
3. Create a separate account at https://play.ms2.mapletime.dev/account.
4. Open MapleTime MS2 from the Start menu, select MapleTime MS2 Azure in
   Mushroom, and launch the game. Sign in with the account you registered.

The Azure pilot accepts only operator-approved networks. An installed
launcher is not a guarantee of public server access or complete gameplay.
The tested client executable is version 20.8.1.0316, x64/NA.

Existing Mushroom profiles, credentials, mods and client files are preserved.
Automatic login and the debug console remain disabled. Uninstalling this
setup removes its helper files and shortcuts, not Mushroom, client files,
server profiles or accounts.

If setup cannot configure the client, close Mushroom and check that its
app-config.json is valid and writable. Configure-Client.ps1 can also be run
manually; CLIENT_SETUP.md contains the full procedure. Never share launcher
settings, populated login screenshots, passwords, PINs or account tokens.

This preview is unsigned and not approved for distribution. Windows Defender
quarantined a local build as Trojan:Win32/Bearfoos.A!ml after an initial
no-threat scan. Antivirus review and complete native acceptance are pending.
Do not disable protection, create exclusions or restore quarantined files.
A matching SHA-256 identifies a build; it does not establish that it is safe.
Use the manual procedure in CLIENT_SETUP.md until the release gate is resolved.

This is an independent, non-commercial community project, not affiliated
with Nexon. MapleStory 2 artwork is copyright NEXON. Original game files
remain subject to their owners' terms. Mushroom and Microsoft installers
are obtained directly from their publishers and retain their own licenses.

The authored setup code is AGPL-3.0. SOURCE.zip contains its exact source
and artwork provenance; SOURCE.txt identifies this build.
