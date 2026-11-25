# PenguinPanel Agentless Windows Recovery

PenguinPanel can reset a customer's Windows/RDP password without installing an
EXE, Windows service, WinRM endpoint, or inbound management port inside the VPS.

## Architecture

```text
Hyper-V host
    |
    | Hyper-V PowerShell Direct / VM bus
    v
Windows VPS
    |- Administrator (customer)
    `- PenguinRecovery (private PenguinPanel account)
```

The recovery account is created automatically when PenguinPanel provisions a
Windows guest with PowerShell Direct enabled.

Its password:

- is random and unique per VPS;
- is encrypted with PenguinPanel's existing `Security:DataProtectionKey`;
- is never returned to the browser, WHMCS, customer email, or client;
- is rotated after every successful emergency password reset.

## Configuration

```json
"Security": {
  "AgentlessRecoveryEnabled": true,
  "RecoveryAccountUsername": "PenguinRecovery"
}
```

`PenguinRecovery` is hidden from the normal Windows sign-in user list and is
added to the built-in local Administrators group using the Administrators SID,
so localized Windows group names are handled correctly.

## New VPS provisioning

During provisioning PenguinPanel:

1. signs into the template through PowerShell Direct;
2. configures networking/RDP/firewall;
3. creates or repairs the private recovery account;
4. verifies that PowerShell Direct works with the recovery credential;
5. stores the recovery secret encrypted;
6. then changes the customer's Windows password.

If recovery initialization fails, VPS creation is not destroyed. A warning is
recorded and an administrator can repair recovery later.

## Existing VPSes

Open:

`Server -> Password Reset`

and click:

`Initialize / repair agentless recovery`

PenguinPanel first uses an existing valid recovery credential if one is already
stored. Otherwise it uses the currently saved Windows/RDP credential for the
one-time bootstrap.

If neither credential works, automatic recovery cannot be bootstrapped from
outside Windows. Use normal administrator intervention or the optional legacy
Guest Agent if it was already installed.

## Emergency password reset

The normal flow is:

```text
Email verification code
        |
        v
PenguinPanel authenticates as PenguinRecovery
        |
        v
PowerShell Direct over Hyper-V VM bus
        |
        v
Reset/enable customer's Windows account
        |
        +--> optional RDP session logoff
        +--> optional TermService restart
        |
        v
Rotate PenguinRecovery password
        |
        v
Verify rotated recovery credential
        |
        v
Update encrypted secrets in PenguinPanel
```

The customer password stored by PenguinPanel is updated only after the
agentless PowerShell Direct operation completes.

## Security notes

A customer with local Administrator rights owns their guest OS and can
deliberately delete or modify the recovery account. If that happens, agentless
recovery will fail and administrator intervention is required.

The legacy PenguinGuestAgent integration remains available as an optional
fallback for older VPSes and legacy commands, but it is no longer required for
normal emergency password recovery.
