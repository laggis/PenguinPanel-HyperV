# PenguinPanel Step 3.3.0 — Smart Reinstall Engine

Smart Reinstall upgrades the existing VHD/VHDX reinstall path.

## Preflight

Before replacing a disk it verifies:

- template exists and is VHD/VHDX;
- template is standalone, not differencing;
- no Hyper-V Quick Restore Points/checkpoints exist;
- template virtual size does not exceed the purchased VPS disk;
- VM storage can fit the template copy while retaining the normal provisioning reserve.

## Disk safety

PenguinPanel copies/converts the golden disk, resizes it to the customer's
existing virtual disk size, grants `NT VIRTUAL MACHINE\<VM-ID>` access, switches
the VM to it, and preserves the previous OS disk as a timestamped rollback disk.

If the fresh disk cannot start, the original rollback disk is automatically
reattached.

## Optional agentless Windows setup

Production `appsettings.json` can contain:

```json
"Reinstall": {
  "ConfigureWindowsGuest": true,
  "TemplateUsername": "Administrator",
  "TemplatePassword": "GOLDEN-TEMPLATE-PASSWORD",
  "GenerateNewWindowsPassword": true,
  "PowerShellDirectTimeoutSeconds": 240
}
```

Use the same temporary Administrator credential across your Windows Server
2019/2022/2025 golden images if you want the simplest setup.

After boot, PowerShell Direct can restore:

- static IPv4, gateway and DNS;
- the unique RDP listener port;
- guest Windows Firewall;
- C: partition expansion;
- PenguinRecovery;
- a newly generated customer Windows password;
- host WinNAT/firewall mappings.

No guest EXE is required.

If `TemplatePassword` is empty, the disk reinstall still works but Windows
guest automation is skipped with a warning.

## Rollback retention

The old OS VHDX is intentionally retained in 3.3.0. A later 3.3.2 step should
manage expiry/deletion so rollback disks do not accumulate forever.


## Step 3.3.0.1 — PowerShell parser hardening

The first 3.3.0 real-host test exposed two parser issues:

1. Windows PowerShell 5.1 did not accept several generated multiline string
   concatenations in the reinstall command.
2. `RunAsync` embedded the generated command directly inside its own
   `try/catch`. A syntax error in the generated command therefore caused the
   outer wrapper to fail parsing too, producing many misleading secondary
   errors.

The reinstall messages now use PowerShell's `-f` format operator.

`RunAsync` now transports the generated command as UTF-8 Base64:

```text
C# generated script
-> UTF-8 Base64
-> stable PowerShell wrapper
-> decode
-> [ScriptBlock]::Create(...)
-> execute inside try/catch
```

A future inner syntax error is isolated from the wrapper and should be returned
as the actual parser exception instead of corrupting the runner itself.


## Step 3.3.0.2 — AVHDX merge guard

Hyper-V can briefly report zero snapshots after a restore point is deleted while
the VM is still attached to an `.avhdx` differencing disk and the background
merge is finishing.

That creates a race if reinstall trusts only `Get-VMSnapshot`.

PenguinPanel now requires both:

```text
Get-VMSnapshot count == 0
AND
active primary disk is standalone VHD/VHDX
```

Preflight gives a just-deleted merge a short grace period and refreshes the
active disk path. If it remains AVHDX/differencing, reinstall is blocked without
touching the VM.

The destructive reinstall path repeats the check immediately before disk
replacement and again after stopping the VM.

If the post-stop check detects a merge and the VPS was running before the
attempt, PenguinPanel starts it again before returning the safe retry message.


## Step 3.3.0.3 — Template bootstrap + network self-heal

The first successful Smart Reinstall test showed the disk swap could complete
while Windows guest setup was skipped when `Reinstall:TemplatePassword` was
empty. The fresh Windows image therefore kept:

```text
DHCP / automatic IPv4
RDP TCP 3389
template/default Windows Firewall
```

### Encrypted per-template bootstrap credential

OS Templates now stores an optional Windows bootstrap username/password per
template. The password is encrypted through PenguinPanel `SecretProtector` and
is never returned to clients.

Example:

```text
Windows Server 2019 - FiveM Ready
Bootstrap username: Administrator
Bootstrap password: [encrypted]

Windows Server 2022 - FiveM Ready
Bootstrap username: Administrator
Bootstrap password: [encrypted]
```

This allows different golden images to have different temporary credentials.

The old `Reinstall:TemplateUsername` / `Reinstall:TemplatePassword` settings
remain as a global fallback for compatibility.

If `Reinstall:ConfigureWindowsGuest=true` and no credential can be resolved,
preflight BLOCKS the reinstall before the active VPS disk is changed.

### Host WinNAT / host Windows Firewall

Host networking does not require a Windows guest password.

Smart Reinstall now applies and read-back verifies the saved host
WinNAT/Windows Firewall profile before the destructive disk replacement begins.
It applies the profile again after the fresh guest RDP listener has been
finalized.

`ApplyNetworkPortProfileAsync` already verifies the actual static mapping and
the host firewall rule after creation, so a missing Hyper-V-host rule is no
longer silently treated as a successful Smart Reinstall.

### Fresh guest flow

With a valid template bootstrap credential:

```text
fresh template boots
-> PowerShell Direct
-> static internal IPv4 / gateway / DNS
-> customer's saved RDP port
-> guest Windows Firewall
-> C: expansion
-> PenguinRecovery
-> new customer Windows password
-> verify new credential + RDP
-> reapply/verify host WinNAT + host firewall
```


## Step 3.3.0.4 — Client-selected Windows password

The self-service Reinstall page now asks the customer for:

```text
New Windows / RDP password
Confirm password
```

This is the FINAL customer credential, not the golden-image bootstrap
credential.

The flow is:

```text
encrypted per-template bootstrap credential
-> boot/configure fresh Windows using PowerShell Direct
-> restore IP/DNS/RDP/firewall/recovery
-> change Administrator password to customer-selected password
-> verify PowerShell Direct with the new password
-> verify RDP listener
-> save final credential in Access
```

The selected password:

- must be at least 8 characters;
- must match the confirmation field;
- is never added to an audit-log message;
- is cleared from the Razor form immediately after the destructive confirmation;
- is saved to the normal encrypted VM Access credential only after the Windows
  password change succeeds.

The template bootstrap password remains private to PenguinPanel/admin and is
never requested from the customer.


## Step 3.3.0.5 — Optional password + credential email

The client-selected Windows password fields are now optional.

```text
both fields blank
-> generate a fresh Windows/RDP password

both fields filled and matching
-> use the customer's chosen password

only one field / mismatch / < 8 chars
-> block reinstall
```

A blank choice always generates a NEW customer password. PenguinPanel never
uses the hidden golden-template bootstrap password as the customer's final
login.

After Windows guest configuration succeeds, PenguinPanel emails each assigned
account that has an email address.

The styled email contains:

```text
Server
Template
VM state
Remote Desktop address
Windows username
New Windows/RDP password
Password source (generated or selected)
```

The password is still saved in the normal encrypted VM Access record.

Password values are deliberately excluded from audit log details. Email audit
entries record the destination/subject only.

If outbound email is disabled or the assigned account has no email address,
the credential remains available in PenguinPanel -> Access.


## Step 3.3.0.7 — Live progress + transactional guest rollback

The old Reinstall UI set 45% immediately before calling the entire Smart
Reinstall backend, so the page could look frozen while Windows was already at
the sign-in screen.

The backend now reports real stages to the client UI:

```text
12% preflight
18% saved network/access profile
23% host WinNAT + firewall verification
32% copy/attach fresh template disk
48% fresh Windows started
52% PowerShell Direct/bootstrap login
60% static IPv4/gateway/DNS
68% RDP listener port
74% guest Windows Firewall
79% C: expansion
83% PenguinRecovery
88% final customer password
92% final credential verification
95% guest ready
97% host NAT/firewall final verification
99% email + state save
100% complete
```

A running VM at 48-52% is expected: Windows must boot before PowerShell Direct
can configure the guest.

PowerShell Direct now fails quickly after repeated explicit invalid-credential
errors and tells the admin to check the encrypted bootstrap password on the OS
Template.

Critical guest configuration failure is transactional:

```text
fresh Windows boots
-> bootstrap/network/RDP/firewall/password setup fails
-> stop fresh VM
-> reattach previous reinstall rollback VHDX
-> repair Hyper-V ACL
-> start previous OS
-> return failure
```

The panel therefore no longer calls a reinstall successful when the fresh OS is
left on DHCP / RDP 3389 because guest configuration failed.


## Step 3.3.0.9 — Automatic bootstrap capture

A template copied from a managed VPS contains the source VPS's current Windows
local-account password.

When `Create template from selected VM` is used and no explicit bootstrap
password is entered, PenguinPanel now reads the source VM's encrypted Access
credential and stores that same username/password as the new template's
encrypted Smart Reinstall bootstrap credential.

```text
Managed source VPS
Access:
  Username = Administrator
  Password = [encrypted]

Create template from VM
  Bootstrap password field left blank

Result:
  VHDX contains the same Windows password
  Template bootstrap credential = copied from encrypted Access
```

The password value is never written to the audit log.

This automatic behavior does NOT apply to an arbitrary existing VHD/VHDX file.
For an existing file, PenguinPanel has no trustworthy way to infer its Windows
password; the administrator must enter the exact credential baked into that
image.

The template list now says `Credential saved / Unverified` instead of `Ready`,
because merely storing a password does not prove that the golden image accepts
it. A future template-validation feature can perform an isolated boot test.


## Step 3.3.0.10 — RDP customer hostname

RDP customer display is now separated from the node/public IPv4 identity.

Configure:

```json
"Provisioning": {
  "PublicAddress": "81.16.177.72",
  "PublicHostname": "node1.penguinhosting.host",
  "RdpCustomerHostname": "remotedesktop.penguinhosting.host"
}
```

Customer-facing RDP becomes:

```text
remotedesktop.penguinhosting.host:22006
```

while WinNAT and FiveM continue using:

```text
81.16.177.72
81.16.177.72:35006
```

`RdpCustomerHostname` has the highest priority for RDP display/email.
For backward compatibility, if it is blank PenguinPanel falls back to
`Provisioning:PublicHostname`, then the per-VM/existing hostname, then the
public IPv4 address.

Existing VPS Access pages also use the configured customer hostname immediately;
a reinstall is not required just to update the displayed RDP address.


## Step 3.3.0.11 — Customer-friendly reinstall completion

Successful Smart Reinstall no longer exposes implementation details such as:

```text
C:\Virtual Machines\...
rollback VHDX filenames
PowerShell/network implementation details
PenguinRecovery internals
```

The customer success banner now says, in effect:

```text
Reinstall complete!
<Template> is ready to use.

Remote Desktop, network, Windows Firewall,
disk and Windows login were configured automatically.

Open Access to view your login details.
```

Full disk paths and rollback information remain available in PenguinPanel
Activity/Audit for administrators.

The selected template's client-facing `After reinstall` note is no longer
duplicated inside the top green success banner; it stays in its dedicated card
on the Reinstall page.


## Step 3.3.0.12 — In-page reinstall confirmation

The native browser `confirm()` dialog has been removed from self-service Smart
Reinstall.

PenguinPanel still performs the full technical preflight first, but successful
preflight now opens a simple in-page destructive-action card:

```text
Are you sure you want to reinstall this server?

This will permanently erase all files and data currently stored on this VPS
and install <selected template>.

Make sure you have a backup of anything important before continuing.

[ Yes, reinstall my server ]  [ Cancel ]
```

The customer no longer sees technical values such as disk virtual size, storage
reserve, checkpoint count, or guest-bootstrap state in the confirmation.

If the client entered a Windows password, it is copied only into component
memory for the pending action and immediately cleared from the visible password
fields. Cancel clears the pending password from memory.

Backend Smart Reinstall performs its own preflight again before destructive
work, so the friendly confirmation does not weaken the safety checks.


## Step 3.3.1 — Real template validation

OS Templates now has a real boot-validation workflow.

Validation does NOT modify the golden VHD/VHDX. PenguinPanel creates a temporary
Hyper-V differencing child disk and boots a disposable VM from that child.

```text
Golden VHDX
    |
    +-- temporary differencing disk
            |
            +-- PenguinTemplateTest-xxxxxxxxxxxx
```

The validation VM:

```text
has no network adapter
uses 2 GB RAM by default
uses 2 vCPU by default
has checkpoints disabled
is deleted after validation
```

### Checks

PenguinPanel validates:

```text
template file exists
VHD/VHDX is standalone (not differencing)
configured Hyper-V generation can boot it
Windows reaches PowerShell Direct
saved bootstrap username/password actually works
Windows product/version/build is detected
Windows architecture is detected
Remote Desktop Services (TermService) exists
temporary VM/disk can be cleaned up
```

A successful template stores:

```text
Validated
Windows product
Windows version/build
architecture
PowerShell Direct status
RDP service status
virtual disk size
physical/stored disk size
validation timestamp
template file last-write timestamp
```

### Changed-file protection

A template is only considered currently validated when the VHD/VHDX still has
the same file size and last-write timestamp as it had during validation.

Replacing/editing the VHDX at the same path therefore causes PenguinPanel to
treat it as changed until validation runs again.

### Client safety

With the default setting:

```json
"TemplateValidation": {
  "RequireValidatedForClients": true
}
```

normal clients only see enabled templates that are both:

```text
Validated
AND
still current
```

Admins can still see and test unvalidated templates.

### Validation settings

```json
"TemplateValidation": {
  "RequireValidatedForClients": true,
  "WorkingRoot": "C:\\ProgramData\\PenguinPanel\\TemplateValidation",
  "MemoryMb": 2048,
  "ProcessorCount": 2,
  "BootTimeoutSeconds": 180,
  "EnableSecureBootForGeneration2": true
}
```

Generation is saved per template. Templates created from an existing managed VM
inherit that VM's actual Hyper-V generation automatically.


## Step 3.3.2 — Reinstall Rollback Manager

Successful Smart Reinstall now registers the previous OS VHD/VHDX as a managed
rollback instead of leaving it as an unmanaged file.

Default behavior:

```text
successful reinstall
-> previous OS protected for 24 hours
-> Reinstall tab shows rollback card
-> second reinstall is blocked while rollback exists
```

The rollback record stores:

```text
VM/template identity
rollback disk path
current reinstalled disk path
virtual/stored disk size
created/expiry time
old Windows Access credential (encrypted)
```

The old password is encrypted with PenguinPanel SecretProtector and is cleared
from rollback metadata after restore/delete/expiry.

### Restore previous OS

Clients can restore the previous OS by default.

Restore performs:

```text
verify rollback VHD/VHDX
block if Hyper-V checkpoints exist
verify the current primary disk still matches this reinstall
stop VM
attach previous OS
repair Hyper-V VHD ACL
start previous OS
if previous OS cannot start -> reattach current OS
after successful boot -> delete replaced current OS disk
restore old Access credential
refresh host NAT/firewall
refresh guest IP/RDP/firewall when old credential works
recreate PenguinRecovery
mark rollback restored
```

### Admin controls

Admins can retain the rollback for:

```text
24 hours
3 days
7 days
```

or delete it immediately.

`ReinstallRollback:MaxRetentionDays` limits the retention request.

### Expiry cleanup

Expired rollback files are cleaned automatically by an in-process background
worker. The worker performs a cleanup shortly after PenguinPanel starts and
then repeats at the configured interval. Cleanup is also still performed:

```text
after panel startup
or
every 15 minutes (default)
or
when the Reinstall tab is loaded
or
before another Smart Reinstall/rollback restore
```

Cleanup is serialized with provisioning and restore operations. It only selects
database records whose status is `available` and whose expiry is in the past.
The existing PowerShell safety checks refuse unexpected filenames or attached
VHD/VHDX files. Missing files are treated as already cleaned. A failed deletion
keeps the record available, saves the error in the database and retries it on
the next scheduled pass.

The worker runs inside PenguinPanel, so no separate Windows service or Task
Scheduler job is required. Restart PenguinPanel after installing the update.

### Configuration

```json
"ReinstallRollback": {
  "RetentionHours": 24,
  "MaxRetentionDays": 7,
  "AllowClientRestore": true,
  "CleanupEnabled": true,
  "CleanupStartupDelaySeconds": 20,
  "CleanupIntervalMinutes": 15,
  "CleanupBatchSize": 500
}
```

`CleanupBatchSize` is limited to 1-2000 records per pass. The retention period
is not shortened by storage pressure: an unexpired rollback remains protected.
Smart Reinstall's existing free-space preflight continues to preserve the
configured VM-storage safety reserve before creating another OS disk.
