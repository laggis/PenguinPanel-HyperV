# PenguinPanel Step 3.1 — Orphan & Consistency Scanner

## Step 3.1.0 philosophy

The first release is intentionally read-only.

It is much safer to prove that PenguinPanel understands the real node before
adding automatic deletion. A manually-created Hyper-V VM, a preserved reinstall
VHDX, or another administrator's NAT rule must never be removed simply because
it looks unfamiliar.

The scanner therefore uses three finding levels:

```text
Critical  managed state is internally contradictory or required state is missing
Warning   likely stale/degraded PenguinPanel-managed state
Review    may be intentional/manual; administrator decision required
```

## Data sources

One scan correlates:

- Hyper-V VMs, state, VM path and attached disks;
- PenguinPanel `vm_network_profiles`;
- configured WinNAT object and static mappings;
- host firewall rules identified as PenguinPanel-managed;
- VHD/VHDX files under approved VM storage roots;
- WHMCS `whmcs_services`;
- panel user-to-VM assignments;
- agentless `vm_recovery_accounts`.

Recovery secrets are **not decrypted or returned** by the scanner. It only
checks whether an enabled record has an encrypted secret present.

## Examples of Critical findings

- PenguinPanel profile exists but the Hyper-V VM is missing;
- duplicate internal IP;
- reserved internal IP allocated to a VPS;
- duplicate public protocol/port across VPSes;
- expected NAT mapping missing or pointing to the wrong target;
- expected Penguin host-firewall rule missing/wrong;
- public RDP 3389 leaked into a VPS profile;
- invalid or conflicting FiveM TCP/UDP mappings;
- active WHMCS service whose VM is missing;
- terminated WHMCS service whose VM still exists;
- Hyper-V attached disk path is missing.

## Examples of Warning findings

- orphan-looking NAT mapping in a PenguinPanel port range;
- stale Penguin-managed host firewall rule;
- user assignment points to a deleted VM;
- recovery record points to a deleted VM;
- managed VM does not have agentless recovery ready;
- RDP/FiveM public port is outside the current configured pool.

## Examples of Review findings

- Hyper-V VM without a PenguinPanel network profile;
- attached disk outside approved storage roots;
- VHD/VHDX under an approved VM root that is not attached to a current VM.

These may be legitimate manual resources and Step 3.1.0 never deletes them.

## Step 3.1.1

After 3.1.0 has been tested against the production node, a later Safe Cleanup
step can act only on categories proven to be safe, with confirmation and
revalidation immediately before deletion.

Likely candidates:

- stale PenguinPanel NAT mapping;
- stale PenguinPanel host firewall rule;
- user assignment whose VM no longer exists;
- recovery row whose VM no longer exists.

Unknown VMs/VHDX files will remain review-only unless an administrator explicitly
chooses a separate destructive workflow.


## Step 3.1.0.1 — root-cause suppression

The first production scan showed that a technically-correct scanner can still
be noisy. One missing VM could generate several child errors because its RDP,
FiveM, firewall and recovery state were naturally absent too.

3.1.0.1 changes this:

```text
Missing VM
  -> root finding
  -> no expected NAT/firewall checks for that missing VM
  -> no duplicate assignment/recovery child findings when profile/WHMCS state
     already explains the missing VM
```

WHMCS classification is now:

```text
status=error      + VM missing -> Warning (failed provisioning history)
status=error      + VM exists  -> Critical (partial failed provisioning)
status=terminated + VM missing -> Expected / no issue
status=terminated + VM exists  -> Critical
active-like       + VM missing -> Critical
```

Firewall checks no longer depend only on the latest display-name convention.
If the exact canonical `[Penguin VPS] ...` name is absent, the scanner searches
PenguinPanel-managed rules for the actual expected protocol/public port. A
healthy older rule becomes a legacy-naming Warning rather than a false Critical.

Step 3.1.0.1 remains completely read-only.


## Step 3.1.1 — Guarded Safe Cleanup

Safe Cleanup is deliberately narrower than the scanner.

### Workflow

```text
Preview
-> administrator selects actions
-> administrator types CLEANUP
-> PenguinPanel acquires provisioning lock
-> fresh scan + fresh cleanup plan
-> requested key must still exist in fresh safe plan
-> execute exact action
-> audit result
-> scan node again
```

The preview shown in the browser is never trusted as authorization.

### Supported automatic cleanup

- exact orphan WinNAT mapping when Penguin ownership has strong evidence;
- exact host Windows Firewall rule that is still PenguinPanel-managed;
- stale panel-management state for a missing VM when no non-terminated WHMCS
  service owns that VM;
- isolated user assignment whose VM no longer exists;
- isolated encrypted recovery row whose VM no longer exists.

Stale panel-management cleanup removes only active control state such as the
network profile, access secret, assignments, recovery/public-status and legacy
guest-agent state. WHMCS rows, audit/history data, Hyper-V resources and files
are preserved.

### Explicitly NOT automatic

- Hyper-V VM deletion;
- VHD/VHDX deletion;
- unknown/manual VMs;
- unattached disks;
- active/error WHMCS service decisions;
- ambiguous NAT entries whose ownership cannot be proven;
- duplicate IP/port repair.

These remain Review/manual workflows.
