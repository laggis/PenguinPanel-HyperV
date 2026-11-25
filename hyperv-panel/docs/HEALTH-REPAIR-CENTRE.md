# PenguinPanel Step 3.0 — Health & Repair Centre

The per-VPS Health tab is an admin-only diagnostic and self-healing surface.

## Checks

The first version checks:

- Hyper-V VM registration and state
- primary VHD/VHDX existence
- approved storage root + configured safety reserve
- expected internal IPv4 vs Hyper-V integration-service-reported addresses
- WinNAT object
- each expected RDP/FiveM/custom WinNAT mapping
- each expected Hyper-V-host Windows Firewall rule
- Hyper-V PowerShell Direct
- Windows RDP listener state and port
- each expected PenguinPanel automatic Windows guest firewall rule
- private agentless password recovery configuration / credential verification

## Repair scope

`Repair PenguinPanel-managed Problems` is intentionally conservative. It may:

- re-apply and verify host WinNAT mappings;
- recreate/verify host Windows Firewall rules;
- set/verify the Windows RDP listener to the saved internal RDP port;
- rebuild PenguinPanel automatic guest firewall rules;
- create/repair/rotate PenguinRecovery.

It does **not** automatically rewrite guest IP/gateway/DNS just because Hyper-V
integration services fail to report the expected address. Use the existing
Network -> Repair guest internet action for that explicit operation.

## Status levels

- Healthy — check matched the saved PenguinPanel state.
- Warning — degraded, skipped live verification, low storage, or something that
  can be legitimate but deserves review.
- Critical — a required managed component is missing/wrong, such as a NAT
  mapping, host firewall rule, virtual disk, or expected RDP listener.
- Skipped — live guest check cannot run while the VPS is stopped.

Step 3.1 will build the node-wide orphan/consistency scanner on top of the same
principles.
