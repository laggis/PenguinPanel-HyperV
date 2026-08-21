# PenguinPanel + WHMCS Integration
---

## What the project does

PenguinPanel provides a web control panel and WHMCS provisioning API for Windows Hyper-V VPS hosting.

Current integration features include:

- WHMCS → PenguinPanel → Hyper-V VPS provisioning
- Start, stop, suspend, unsuspend and terminate support
- Safe failed-provision retry when no Hyper-V VM exists
- Windows guest setup through Hyper-V PowerShell Direct
- Automatic internal IPv4 allocation
- Automatic public RDP port allocation
- Optional FiveM TCP + UDP port mapping
- Host listener / WinNAT / reserved-port collision protection
- Smart VM storage placement using live disk free space
- Manual per-service storage override before provisioning
- CPU and RAM upgrades/downgrades from WHMCS
- VHD/VHDX disk growth from WHMCS
- Automatic Windows `C:` partition expansion when possible
- Disk shrinking deliberately blocked
- Customer-friendly PenguinPanel usernames
- Customer-friendly VM names based on WHMCS first name
- Hyper-V + database collision-safe VM naming
- PenguinHosting-styled HTML email notifications
- WHMCS welcome and fixed-term termination email templates
- Guacamole web RDP integration
- Safe cleanup of WHMCS-created PenguinPanel accounts after their final VPS is terminated
- API rate limiting, HTTPS requirement and optional source-IP allowlisting

---

## Repository layout

```text
.
├── PenguinPanel/
│   ├── Components/
│   ├── Services/
│   ├── wwwroot/
│   ├── Program.cs
│   ├── HyperVPanel.csproj
│   └── appsettings.example.json
│
├── WHMCS/
│   ├── modules/
│   │   ├── servers/penguinpanel/
│   │   └── addons/penguinpanel/
│   └── email-templates/
│
├── database/
│   └── schema.sql
│
├── release-packages/
├── README.md
├── WHMCS-PRODUCT-SETUP.md
├── PATCH-HISTORY.md
├── PRIVATE-BACKUP-CHECKLIST.md
├── FILE-MANIFEST.sha256
└── .gitignore
```

---

# PenguinPanel configuration

Copy:

```text
PenguinPanel/appsettings.example.json
```

to:

```text
PenguinPanel/appsettings.json
```

and replace every `CHANGE_ME...` value before starting the panel.

## ConnectionStrings

```json
"ConnectionStrings": {
  "PanelDb": "Server=127.0.0.1;Port=3307;Database=hyperv_panel;User ID=hyperv_panel;Password=CHANGE_ME;SslMode=Preferred;Allow User Variables=True;"
}
```

`PanelDb` is the MariaDB/MySQL connection used by PenguinPanel.

The production database/password must never be committed to GitHub.

---

## Security

```json
"Security": {
  "DataProtectionKey": "CHANGE_ME_LONG_RANDOM_DATA_PROTECTION_KEY",
  "MaxFailedLoginAttempts": 5,
  "LoginLockoutMinutes": 15,
  "EmergencyResetRequireEmailCode": true,
  "EmergencyResetCodeMinutes": 10,
  "EmergencyResetCodeCooldownSeconds": 60
}
```

Recommended behavior:

- keep login lockout enabled;
- use a long random Data Protection key;
- keep email verification enabled for emergency RDP password reset.

---

## Panel URL

```json
"Panel": {
  "PublicUrl": "https://vps.penguinhosting.host/"
}
```

This is the normal public PenguinPanel URL used in emails and links.

---

## SMTP

```json
"Smtp": {
  "Enabled": true,
  "Host": "mailserver.penguinhosting.host",
  "Port": 587,
  "EnableSsl": false,
  "Username": "noreply@penguinhosting.host",
  "Password": "CHANGE_ME_SMTP_PASSWORD",
  "FromEmail": "noreply@penguinhosting.host",
  "FromName": "PenguinPanel",
  "TimeoutSeconds": 15
}
```

PenguinPanel-generated HTML mail uses the shared PenguinHosting theme.

Current theme palette:

| Purpose | Color |
|---|---|
| Main background/header | `#081220` |
| Card | `#12243d` |
| Inner card | `#0d1b2f` |
| Border | `#25476f` |
| Accent | `#7dd3fc` |
| Main text | `#ffffff` |
| Body text | `#dbeafe` |
| Muted text | `#9fb4d0` / `#637b9d` |
| Primary button | `#2563eb` |

The shared renderer applies to account, password reset, VPS-ready, RDP reset, power/action and other PenguinPanel notifications.

---

# Web RDP / Guacamole

```json
"WebRdp": {
  "Enabled": true,
  "Provider": "Guacamole",
  "BaseUrl": "https://remotedesktop.penguinhosting.host",
  "EmbedInConsole": true,
  "PreferPerVmWebConsoleUrl": false,
  "PathTemplate": "/#/client/{connectionId}",
  "Security": "nla",
  "JsonAuth": {
    "Enabled": true,
    "SecretKey": "CHANGE_ME_GUACAMOLE_JSON_AUTH_SECRET",
    "UseApiToken": true,
    "TokenLifetimeMinutes": 60,
    "Security": "nla",
    "IgnoreCertificate": true,
    "ResizeMethod": "display-update",
    "CacheSeconds": 120,
    "RequestTimeoutSeconds": 3
  }
}
```

Never commit the real Guacamole JSON auth secret.

---

# Hyper-V networking

## Network profile

```json
"NetworkProfiles": {
  "NatName": "HPanel-NAT",
  "NatBindAddress": "0.0.0.0",
  "DefaultExternalIp": "0.0.0.0/0",
  "Gateway": "192.168.119.1"
}
```

Current intended NAT/switch name:

```text
HPanel-NAT
```

---

## Provisioning network settings

Recommended baseline:

```json
"Provisioning": {
  "PublicAddress": "81.16.177.72",
  "PublicHostname": "node1.penguinhosting.host",

  "SwitchName": "HPanel-NAT",
  "NatName": "HPanel-NAT",

  "InternalIpStart": "192.168.119.50",
  "InternalIpEnd": "192.168.119.220",
  "ReservedInternalIps": "192.168.119.1;192.168.119.40",

  "Gateway": "192.168.119.1",
  "PreferredDnsServer": "1.1.1.1",
  "AlternateDnsServer": "1.0.0.1",

  "RdpPortStart": 22000,
  "RdpPortEnd": 22999,

  "FiveMPortStart": 35000,
  "FiveMPortEnd": 35999,
  "DefaultFiveMInternalPort": 30120
}
```

### `PublicAddress`

The real public IPv4 used for NAT/firewall work.

Current example:

```text
81.16.177.72
```

### `PublicHostname`

Optional customer-facing hostname used by PenguinPanel's own VPS-ready notification display.

Example:

```text
node1.penguinhosting.host
```

Networking still uses `PublicAddress`.

### WHMCS RDP hostname is separate

The WHMCS server module has its own setting:

```text
RDP Customer Hostname
```

Current intended value:

```text
remotedesktop.penguinhosting.host
```

This allows WHMCS/client/email display to show:

```text
remotedesktop.penguinhosting.host:22000
```

while FiveM can continue to be presented as the real public IP + port.

If native RDP is used through this hostname, it must resolve directly to the Hyper-V host/public IP. A standard Cloudflare-proxied hostname should not be used for arbitrary raw RDP ports.

---

# Port allocation and collision protection

Recommended PenguinPanel-owned public ranges:

```text
22000-22999  -> RDP
35000-35999  -> FiveM
```

In WHMCS products leave these at `0`:

```text
Public RDP Port:   0
Public FiveM Port: 0
```

`0` means PenguinPanel automatically allocates a collision-safe port.

FiveM inside the VM normally remains:

```text
30120
```

Example:

```text
Public FiveM: 81.16.177.72:35004
Guest FiveM:  192.168.119.55:30120
```

The allocator checks:

- PenguinPanel's existing allocations;
- WinNAT mappings;
- actual TCP listeners on the Hyper-V host;
- actual UDP endpoints on the Hyper-V host;
- manually reserved external ports.

## Reserved host / TCAdmin ports

Example:

```json
"ReservedExternalPorts": "80;443;3306;3307;8443;8822;9998;30120;30124;40105"
```

Add any TCAdmin allocation range here if PenguinPanel must never allocate from it even while the TCAdmin service is stopped.

Example:

```json
"ReservedExternalPorts": "80;443;30120;30124;30000-32000"
```

---

# Smart VM storage

Approved roots:

```json
"VmStorageRoot": "D:\\Vms",
"VmStorageRoots": [
  "D:\\Vms",
  "C:\\Virtual Machines"
]
```

Automatic placement:

```json
"StorageSelectionMode": "MostFreeSpace",
"MinimumFreeSpaceAfterProvisionGb": 100,
"MinimumFreeSpaceAfterProvisionPercent": 5
```

The effective safety reserve is the larger of:

```text
100 GB
or
5% of the physical volume
```

Automatic storage selection:

1. reads each approved storage root;
2. checks live free/total space;
3. checks requested VPS disk size;
4. keeps the configured safety reserve;
5. chooses a safe root with the most free space.

Manual storage selection is also validated at provisioning time.

A manually selected root is refused if it cannot safely fit the requested VHD/VHDX.

### WHMCS storage behavior

For normal products use:

```text
VM Storage Root: Automatic / blank
```

Before provisioning, the WHMCS service admin page can show the approved roots and current free space so an administrator can override the disk.

For this workflow keep automatic provisioning disabled until the administrator has selected storage if a manual per-order choice is required.

---

# Windows guest configuration

Recommended WHMCS settings:

```text
Hyper-V Generation:        2
Secure Boot:               Enabled
Start After Provision:     Enabled
Configure Windows Guest:   Enabled
Windows Username:          Administrator
FiveM Internal Port:       30120
Public RDP Port:           0
Public FiveM Port:         0
```

`Template Administrator Password` must contain the current Administrator password inside the golden Windows VHD/VHDX when automatic Windows guest configuration is enabled.

PenguinPanel can configure:

- internal IPv4;
- gateway and DNS;
- Windows Administrator password;
- Windows RDP listener port;
- Windows Firewall;
- public RDP WinNAT mapping;
- FiveM TCP/UDP mapping.

If Windows refuses to restart `TermService` during first boot, the current RDP sync routine can reboot the Hyper-V VM once, wait for PowerShell Direct to return and verify the requested RDP listener.

---

# VM naming

Default behavior for a new WHMCS VPS:

```text
WHMCS client first name -> VM name
```

Examples:

```text
Daniel
Test
Alex
```

Collision behavior:

```text
Test
Test-887
Test-887-2
Test-887-3
```

The allocator checks both:

- existing Hyper-V VM names;
- the WHMCS service-mapping database.

It never overwrites another VM just to obtain the friendly name.

If no usable first name exists, the fallback is:

```text
PH-VPS-<WHMCS service ID>
```

Existing VMs are not automatically renamed.

---

# PenguinPanel customer usernames

New WHMCS-created PenguinPanel accounts do not deliberately use customer email addresses as usernames.

Current preference order:

1. WHMCS client first name;
2. existing sensible non-email WHMCS service username;
3. `penguin-<clientId>` fallback.

If the requested username already exists, PenguinPanel generates a unique client-specific variant.

WHMCS-created accounts are reused when the same customer receives additional VPSes.

Existing accounts are not silently renamed.

---

# WHMCS server connection

Create a WHMCS server with:

```text
Module:   PenguinPanel Hyper-V
Hostname: vps.penguinhosting.host
SSL:      Enabled
Port:     443
```

Use the PenguinPanel WHMCS API key as the module credential.

Create a dedicated server group, for example:

```text
PenguinPanel VPS
└── Virtual-Server
```

Assign VPS products to that group.

---

# WHMCS module settings

Current v1.2.3 product settings include:

| Setting | Recommended value |
|---|---|
| OS Template | Enabled PenguinPanel template ID |
| CPU Cores | e.g. `4` |
| Memory (MB) | e.g. `4096` |
| Disk (GB) | e.g. `127` / product requirement |
| Hyper-V Generation | `2` |
| Secure Boot | Enabled |
| Start After Provision | Enabled |
| Configure Windows Guest | Enabled |
| Windows Username | `Administrator` |
| Template Administrator Password | Golden-template Administrator password |
| VM Storage Root | Automatic / blank |
| Enable FiveM Mapping | Enabled when needed |
| FiveM Internal Port | `30120` |
| Public RDP Port | `0` |
| Public FiveM Port | `0` |
| RDP Customer Hostname | `remotedesktop.penguinhosting.host` |

The old `Fallback VM Name Prefix` UI slot was replaced by `RDP Customer Hostname` in v1.2.3. If an upgraded WHMCS product still shows `PH-VPS-` in that field, replace it with:

```text
remotedesktop.penguinhosting.host
```

and save the product.

---

# WHMCS service/client data

The provisioning module stores values such as:

- PenguinPanel VM name
- Windows username
- internal IPv4
- public IPv4
- RDP host
- RDP port
- FiveM public port
- FiveM internal port
- selected/provisioned storage root
- CPU
- RAM
- disk

The current WHMCS module also mirrors customer-email values into hidden/admin-only Product Custom Fields so Product/Service email templates can use merge fields such as:

```text
{$service_custom_field_penguinpanelvmname}
{$service_custom_field_rdphost}
{$service_custom_field_rdpport}
{$service_custom_field_windowsusername}
{$service_custom_field_internalipv4}
{$service_custom_field_publicipv4}
{$service_custom_field_fivemport}
{$service_custom_field_fiveminternalport}
```

---

# WHMCS emails

Included under:

```text
WHMCS/email-templates/
```

Current templates include:

- Swedish VPS welcome email;
- Swedish fixed-term termination email.

Intended endpoint display:

```text
RDP:
remotedesktop.penguinhosting.host:<RDP port>

FiveM:
<PublicAddress>:<FiveM public port>

PenguinPanel:
https://vps.penguinhosting.host/
```

The WHMCS product Welcome Email must be the custom PenguinPanel VPS email, not WHMCS's legacy Hosting Account Welcome Email, otherwise WHMCS may generate cPanel-style `:2082` links.

---

# WHMCS resource upgrades

The server module implements WHMCS `ChangePackage`.

Recommended configurable option names:

```text
CPU Cores
RAM
Disk GB
```

Example values:

```text
CPU Cores:
2
4
6
8

RAM:
4096MB
8192MB
16384MB
32768MB

Disk GB:
127
256
512
```

Supported behavior:

| Resource | Increase | Decrease |
|---|---|---|
| CPU | Yes | Yes |
| RAM | Yes | Yes |
| VHD/VHDX | Yes | **No** |

Disk shrinking is intentionally blocked.

Before VHD/VHDX expansion, PenguinPanel checks the storage safety reserve.

When possible, it uses PowerShell Direct after disk expansion to extend the Windows `C:` partition.

An active `.avhdx` checkpoint/differencing disk blocks automatic disk growth.

---

# VPS products: 1-5 month prepaid plans

Current intended sales model is **pay the selected period up front**.

Use **One Time** billing, not Monthly recurring billing.

Example at 150 SEK/month:

| Product | Payment Type | Price paid now | Fixed term |
|---|---|---:|---:|
| VPS - 1 Månad | One Time | 150 SEK | 30 days |
| VPS - 2 Månad | One Time | 300 SEK | 60 days |
| VPS - 3 Månad | One Time | 450 SEK | 90 days |
| VPS - 4 Månad | One Time | 600 SEK | 120 days |
| VPS - 5 Månad | One Time | 750 SEK | 150 days |

For all of these:

```text
Recurring Cycles Limit: 0
Termination Email: PenguinPanel VPS - Avslutningsmail
```

`Auto Terminate/Fixed Term` is a literal number of days after activation.

It is not a "days overdue" setting.

Non-payment suspension/termination is controlled separately by WHMCS automation.

---

# Termination behavior

Always use the module's **Terminate** operation before simply deleting a WHMCS service record.

A plain WHMCS service deletion does not necessarily execute provisioning cleanup.

PenguinPanel termination is intended to clean up:

- Hyper-V VM;
- managed VM storage;
- WinNAT mappings;
- related firewall/network data;
- PenguinPanel service mapping.

WHMCS-created PenguinPanel user cleanup is conservative:

- the user must be safe to delete;
- they must not own another active VPS;
- manual/reused accounts should not be removed accidentally.

---

# WHMCS provisioning mode

For manual control/testing use:

```text
Do not automatically setup this product
```

Then the administrator can:

1. accept/create the order;
2. review the service;
3. choose storage if required;
4. press **Module Commands -> Create**.

Once the product is production-ready, automatic provisioning can be enabled after payment if no manual storage decision is required.

---

# WHMCS API

Example safe configuration:

```json
"WhmcsApi": {
  "Enabled": true,
  "RequireHttps": true,
  "ApiKeySha256": "GENERATE_WITH_Configure-WhmcsApi.ps1",
  "AllowedIps": [],
  "TrustForwardedFor": false,
  "MaxRequestsPerMinute": 120
}
```

Generate the API key/hash using the included configuration script.

For production, consider restricting:

```text
AllowedIps
```

to the real source IP PenguinPanel sees from the WHMCS server.

Do not guess the address; verify the actual source first or WHMCS can be locked out.

---

# Build / restore PenguinPanel

1. Copy `PenguinPanel/` to the Windows Hyper-V server, for example:

   ```text
   C:\Discordbots\scripts\hyperv-panel
   ```

2. Copy:

   ```text
   appsettings.example.json
   ```

   to:

   ```text
   appsettings.json
   ```

3. Fill all secrets and verify:
   - database connection;
   - SMTP;
   - Guacamole auth;
   - Panel URL;
   - NAT/switch;
   - public IP/hostname;
   - internal IP range;
   - reserved ports;
   - RDP/FiveM ranges;
   - VM storage roots;
   - WHMCS API.

4. Build:

   ```powershell
   cd C:\Discordbots\scripts\hyperv-panel
   dotnet build
   ```

5. Start/restart PenguinPanel.

---

# Restore WHMCS

Upload:

```text
WHMCS/modules/servers/penguinpanel/
```

to:

```text
/public_html/modules/servers/penguinpanel/
```

and:

```text
WHMCS/modules/addons/penguinpanel/
```

to:

```text
/public_html/modules/addons/penguinpanel/
```

Then:

1. configure the PenguinPanel WHMCS server;
2. test the server connection;
3. create/select the `PenguinPanel VPS` server group;
4. assign the VPS product to the server group;
5. select a valid OS template;
6. configure the module settings;
7. select the custom Welcome Email;
8. configure product pricing/fixed term;
9. test with a disposable client/order before enabling automatic production provisioning.

---

# Production backup checklist

This GitHub repository is intentionally **not** a full disaster-recovery backup.

Keep a separate encrypted/private backup of:

- production `appsettings.json`;
- MariaDB database dump;
- WHMCS/PenguinPanel API credentials;
- SMTP credentials;
- Guacamole JSON auth secret;
- Windows golden VHD/VHDX templates;
- production VM disks if required by your backup policy;
- TLS/private keys where applicable.

See:

```text
PRIVATE-BACKUP-CHECKLIST.md
```

for the dedicated checklist.

---

# GitHub safety

Do **not** commit:

```text
appsettings.json
*.vhd
*.vhdx
*.avhdx
database dumps containing customer data
API keys
SMTP passwords
Guacamole secrets
production private keys
```

The repository includes `.gitignore` rules and a sanitized `appsettings.example.json`, but always review changes before pushing.

---

# Validation after restore

Recommended quick test:

```text
1. dotnet build succeeds
2. PenguinPanel starts
3. WHMCS Test Connection succeeds
4. /api/whmcs/catalog returns templates/storage
5. disposable VPS Create succeeds
6. Windows RDP listener matches allocated public RDP port
7. WinNAT RDP mapping exists
8. FiveM TCP + UDP mapping exists when enabled
9. welcome email contains VM/RDP/Windows data
10. suspend/unsuspend works
11. ChangePackage CPU/RAM test works
12. Terminate removes the disposable VPS
```

---

# Patch history

See:

```text
PATCH-HISTORY.md
```

for the condensed progression from Step 2.1 through Step 2.9.1.

---

## Notes

This snapshot is intended to be a known restore point for the working PenguinPanel/WHMCS project.

When new features are added, update:

- `README.md`
- `VERSION.txt`
- `PATCH-HISTORY.md`
- `WHMCS-PRODUCT-SETUP.md`
- `FILE-MANIFEST.sha256`

before creating the next release backup.
