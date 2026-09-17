# Taikeron Launcher

Taikeron Launcher is the Windows entry point and local source of truth for the Taikeron desktop ecosystem.

## Goals

- Detect installed Taikeron applications.
- Read available releases from public manifests.
- Verify installed application code against public file-integrity manifests.
- Distinguish `outdated` from `corrupted`.
- Block launch when installed code is known to be altered.
- Download, verify, fully replace and repair application code.
- Keep user data separated from replaceable application code.
- Let the user choose where applications, the Data Vault, maps and temporary downloads are stored.
- Back up the private Data Vault locally to another disk without requiring cloud storage.

## v0.4.0 — Launcher as source of truth

On every launcher start, Taikeron Lab is checked against its official integrity manifest. The launcher verifies the expected file sizes and SHA-256 hashes locally. No Data Vault content is uploaded for this check.

A TL installation can therefore be:

- intact and up to date;
- intact but outdated;
- legacy / not yet certifiable because no historical integrity manifest exists;
- corrupted, in which case launch is blocked and a clean repair is offered.

WDS now generates integrity manifests directly from the `win-unpacked` directory of the release being published. A channel pointer is published as `releases/tl/integrity-stable.json`, while each release is also archived under `releases/tl/integrity/<version>/windows-x64.json`.

The external update worker stages the verified official installer, closes TL, protects local maps, removes the old code directory completely, installs the new package into the target directory, checks `Taikeron Lab.exe` and `resources/app.asar`, and then lets the launcher run the final integrity check before TL is relaunched.

## Private Data Vault

The `Stockage & sauvegardes` panel provides configurable roots for:

- Taikeron applications;
- Data Vault;
- maps;
- temporary downloads;
- Data Vault backups.

Data Vault backups are versioned snapshots. Each snapshot can be verified file-by-file with SHA-256, includes an integrity manifest, and follows a configurable retention count. A removable backup disk may be absent: the launcher reports the backup as pending instead of treating that as a fatal application error.

Changing a configured path does not silently move or delete existing user data. Safe migration between disks is an explicit, verified operation.

## Applications

Taikeron Lab (TL) is the first managed application. Taikeron Map Builder (TMB) is prepared as the next application integration.
