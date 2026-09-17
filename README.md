# Taikeron Launcher

Taikeron Launcher is the Windows entry point for the Taikeron desktop ecosystem.

## Goals

- Detect installed Taikeron applications.
- Read available releases from public manifests.
- Launch installed applications.
- Download, verify, replace and repair application code.
- Keep user data separated from replaceable application code.
- Let the user choose where applications, the Data Vault, maps and temporary downloads are stored.
- Back up the private Data Vault locally to another disk without requiring cloud storage.

## v0.2.0

The launcher now provides a `Stockage & sauvegardes` panel with configurable roots for:

- Taikeron applications;
- Data Vault;
- maps;
- temporary downloads;
- Data Vault backups.

Data Vault backups are versioned snapshots. Each snapshot can be verified file-by-file with SHA-256, includes an integrity manifest, and follows a configurable retention count. A removable backup disk may be absent: the launcher reports the backup as pending instead of treating that as a fatal application error.

Automatic backup checks run when the launcher starts and periodically while it remains open. A future standalone worker will make scheduled backups independent from the launcher UI process.

Changing a configured path does not silently move or delete existing user data. Safe migration between disks will be implemented as an explicit, verified operation.

## First application milestone

Taikeron Lab (TL) is the first managed application. Taikeron Map Builder (TMB) is prepared as the next application integration.
