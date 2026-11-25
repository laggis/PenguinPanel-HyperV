# PenguinPanel Native Windows RDP Console

## Default mode

```json
"Console": {
  "Mode": "NativeRdp"
}
```

This mode does not need Guacamole, an RDP gateway, a per-VM connection ID, or a
second console account.

The customer opens:

```text
Server -> Console -> Connect with Remote Desktop
```

PenguinPanel generates a small `.rdp` file containing the VPS public RDP
endpoint, username, NLA/CredSSP settings, clipboard redirection and dynamic
resolution settings.

The password is intentionally not embedded in the `.rdp` file. The user can
copy it from PenguinPanel and paste it into the normal Windows credential
prompt.

## Why it cannot literally render raw RDP inside the browser

Chrome, Edge and Firefox do not implement the Microsoft RDP protocol as a
browser transport. Rendering a Windows RDP session inside HTML requires a
translation/gateway layer such as Guacamole or Microsoft RDS web infrastructure.

PenguinPanel therefore treats native RDP as the simple/reliable default and
keeps browser-gateway support optional.

## Legacy browser mode

To deliberately use the old browser console:

```json
"Console": {
  "Mode": "Browser"
}
```

Then the existing `WebRdp` / Guacamole settings become active again.

This makes rollback possible without forcing every VPS to keep browser-console
configuration.
