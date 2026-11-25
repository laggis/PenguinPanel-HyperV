# PenguinPanel Step 3.2.0 — Real Full Backup & Safe Restore

## Why checkpoints are not backups

Hyper-V checkpoints are useful short-term rollback points, but checkpoint
AVHDX files remain part of the live VM storage chain.

Step 3.2 creates independent VHD/VHDX copies in a separate backup root.

## Configuration

```json
"Backup": {
  "Root": "D:\\PenguinBackups",
  "MinimumFreeSpaceAfterBackupGb": 100,
  "MinimumFreeSpaceAfterBackupPercent": 5,
  "MaxBackupsPerVm": 5
}
```

`Backup:Root` must not be inside any configured
`Provisioning:VmStorageRoots`. Keeping backup VHDX files outside VM roots also
prevents the Consistency Scanner from treating them as unattached VM disks.

Step 3.2.0 currently requires a local Windows drive path. Network/UNC backup
targets can be added later after testing the local backup engine.

## Create full backup

```text
check ownership/permission
-> inspect VM + disks
-> estimate required backup storage
-> enforce free-space reserve
-> stop VM if running
-> copy each standalone disk
-> if source is AVHDX/differencing:
      Convert-VHD -> standalone dynamic VHDX
-> write manifest.json
-> verify files
-> mark DB backup complete
-> start VM again if it was running
-> audit
```

The backup list stores:

```text
name
type
status
size
disk count
backup folder
manifest path
creator
created/completed time
last error
```

## Restore

Full restore requires typing:

```text
RESTORE
```

Restore performs:

```text
validate selected backup
-> verify all backup disks
-> verify current controller/disk layout
-> BLOCK if Hyper-V checkpoints exist
-> stop VM
-> create automatic pre-restore safety backup
-> stage all selected backup disks
-> transactional file swap
-> if swap fails:
      restore original disk files immediately
-> restart VM if it was running
-> audit
```

The selected backup remains unchanged after restore.

The automatic pre-restore safety backup is also visible in the normal backup
list with type `Safety`.

## Delete backup

Delete removes only the selected backup folder and its `vm_backups` row.

Before deletion PenguinPanel checks that the folder is under the configured
backup root and that no active VM disk lives inside it.

## Quick Snapshots

Hyper-V checkpoints remain available as a client-facing `Quick Snapshots` tool. They are ideal before configuration changes, updates or script testing, while Full Backups remain the independent disaster-recovery option.

Full backup restore is blocked while checkpoints exist. This prevents
PenguinPanel from overwriting an active AVHDX chain.

## Next

Step 3.2.1 can build scheduled/automatic backup policies and retention on top of
this same backup format after the manual create/restore path has been tested on
a disposable VPS.


### Friendly names

Clients can give both Full Backups and Quick Snapshots meaningful names, for
example:

```text
Before FiveM update
Working server.cfg
Before Windows Update
Before installing inventory script
```

If a name is left blank PenguinPanel creates a dated name automatically.

Quick Snapshot names are unique per VM (case-insensitive). Duplicate names are
blocked so restore/delete actions cannot become ambiguous.


### Restored VHDX permissions

A Hyper-V VM disk carries a VM-specific filesystem ACL for:

```text
NT VIRTUAL MACHINE\<VM-ID>
```

A copied backup VHDX does not necessarily retain that ACL when it is staged in
the backup folder.

Step 3.2.0.4 repairs this as part of the transactional restore:

```text
swap restored disk into place
-> copy ACL from original active VHDX
-> explicitly grant NT VIRTUAL MACHINE\<VM-ID>:F
-> only then delete rollback VHDX
```

If ACL repair fails, the normal restore catch path restores the original disk
from the rollback file instead of leaving the VM with an inaccessible VHDX.


## Step 3.2.1 — Advanced Quick Restore Points

Quick Restore Points are now the normal short-term rollback tool.

Configuration:

```json
"QuickSnapshots": {
  "MaxPerVm": 3,
  "MaxAgeDays": 7,
  "SafetyRetentionHours": 24,
  "WarningStorageGb": 20,
  "PreferProduction": true
}
```

### Storage behavior

PenguinPanel calculates the unique AVHDX files referenced by the current
Hyper-V disk/checkpoint chains and shows the combined checkpoint-storage
footprint.

`WarningStorageGb` is a warning threshold only. No snapshot is silently deleted.

### Retention

Normal user restore points are considered expired after `MaxAgeDays` unless
they are pinned.

Temporary Safety points created during restore use
`SafetyRetentionHours`.

Pinned points are never included in age cleanup.

Cleanup requires an explicit `Clean expired` action.

### Production checkpoints

When `PreferProduction` is true, PenguinPanel temporarily selects Hyper-V's
Production checkpoint mode while creating the restore point and restores the
VM's previous checkpoint configuration afterward.

Production mode is preferred for server workloads; Hyper-V may fall back to a
standard checkpoint when the VM is configured for `Production`.

### Safer lightweight restore

Quick Restore now follows the Hyper-V "Create Checkpoint and Apply" idea:

```text
current VM state
-> create temporary Safety Restore Point
-> stop VM when required
-> apply selected Restore Point
-> start VM again if it was running before
-> keep Safety Restore Point for configured retention
```

This avoids creating another complete 25–200 GB VHDX backup for normal
short-term rollback.

Full Backup restore still uses an independent pre-restore full Safety Backup
because it physically replaces the active VHDX and is a different disaster-
recovery workflow.


### One permanent restore point

Each VPS can have exactly one normal Quick Restore Point marked permanent.

```text
Restore Point A -> Permanent

later:

Restore Point B -> Make permanent

result:

Restore Point A -> normal retention
Restore Point B -> Permanent
```

The switch is atomic in the PenguinPanel database: the previous permanent flag
is cleared and the selected point is promoted in one transaction.

The permanent restore point:

- does not expire;
- is excluded from `Clean expired`;
- cannot be deleted until permanent protection is removed.

Temporary Safety Restore Points cannot be made permanent. They remain governed
by `SafetyRetentionHours`.


## Step 3.2.1.3 — Smart Full Backup Storage

Full Backups can now use an approved pool of backup roots instead of one fixed
folder.

```json
"Backup": {
  "Root": "D:\\PenguinBackups",
  "Roots": [
    "D:\\PenguinBackups",
    "C:\\PenguinBackups"
  ],
  "StorageSelectionMode": "MostFreeSpace",
  "MinimumFreeSpaceAfterBackupGb": 100,
  "MinimumFreeSpaceAfterBackupPercent": 5,
  "MaxBackupsPerVm": 5
}
```

`Root` remains for backwards compatibility and is treated as the default.
If `Roots` is omitted, PenguinPanel behaves exactly like the older single-root
backup system.

### Selection

Before the VM is stopped, PenguinPanel inspects the source VHD/VHDX chain and
estimates how much space the Full Backup may need.

Eligible roots must have:

```text
free space >= estimated backup bytes + safety reserve
```

The reserve is:

```text
max(MinimumFreeSpaceAfterBackupGb, MinimumFreeSpaceAfterBackupPercent)
```

Supported modes:

```text
MostFreeSpace   default; largest absolute free-space cushion
MostFreePercent choose the volume with the highest free percentage
DefaultFirst    prefer Backup:Root when it can safely fit
```

The automatic pre-restore Full Safety Backup uses the same selector.

### Path safety

Backup roots must be sibling/separate folders from VM storage roots. For
example:

```text
D:\\Vms             VM storage
D:\\PenguinBackups  backup storage   OK
```

A backup root cannot be inside a VM root, and it also cannot be a parent of a VM
root.

Smart storage balances capacity; it does not make a backup resilient to a
physical drive failure when the backup and VM happen to live on the same
physical disk. Off-host/NAS storage remains a future disaster-recovery option.
