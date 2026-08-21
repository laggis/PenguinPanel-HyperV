# 🐧 PenguinPanel

> A Hyper-V VPS control panel with WHMCS provisioning, Windows guest configuration, smart storage, RDP/FiveM port management, customer controls, and automated cleanup.

---

## 👋 Start here

This README is written for **Future You, a friend, or another administrator who did not build PenguinPanel with us**.

If the server disappeared tomorrow and you had to rebuild the project from this repository, start at **Fresh installation** and follow the steps in order.

If PenguinPanel is already installed and you only need a setting, jump to **Configuration reference**.

> [!IMPORTANT]
> This GitHub backup intentionally does **not** contain production passwords, API keys, customer data, VHDX templates, or VM disks.  
> You need the separate private backup described in `PRIVATE-BACKUP-CHECKLIST.md` for a full disaster recovery.

---

## 📚 Table of contents

1. [What PenguinPanel does](#-what-penguinpanel-does)
2. [How everything connects](#-how-everything-connects)
3. [What you need](#-what-you-need)
4. [Quick setup checklist](#-quick-setup-checklist)
5. [Fresh installation](#-fresh-installation)
6. [Configure Hyper-V networking](#-configure-hyper-v-networking)
7. [Configure PenguinPanel](#-configure-penguinpanel)
8. [Configure the WHMCS API](#-configure-the-whmcs-api)
9. [Install the WHMCS modules](#-install-the-whmcs-modules)
10. [Create a WHMCS VPS product](#-create-a-whmcs-vps-product)
11. [Prepaid 1-5 month products](#-prepaid-1-5-month-products)
12. [Test your first VPS](#-test-your-first-vps)
13. [Resource upgrades](#-resource-upgrades)
14. [How termination works](#-how-termination-works)
15. [Email setup](#-email-setup)
16. [Troubleshooting](#-troubleshooting)
17. [Backup and restore](#-backup-and-restore)
18. [Configuration reference](#-configuration-reference)
19. [Repository layout](#-repository-layout)

---

# ✨ What PenguinPanel does

PenguinPanel sits between **WHMCS** and **Microsoft Hyper-V**.

A normal VPS order can flow like this:

```text
Customer
   │
   ▼
WHMCS
   │  PenguinPanel API
   ▼
PenguinPanel
   │
   ├── Creates Hyper-V VM
   ├── Selects safe storage
   ├── Assigns internal IPv4
   ├── Configures Windows
   ├── Changes RDP port
   ├── Creates WinNAT mapping
   ├── Opens required firewall rules
   ├── Adds FiveM mapping if enabled
   └── Creates/assigns PenguinPanel customer account
   │
   ▼
Customer receives VPS details
```

Current features include:

- WHMCS → PenguinPanel → Hyper-V provisioning
- start, stop, suspend, unsuspend and terminate
- Windows guest configuration with PowerShell Direct
- automatic internal IPv4 allocation
- automatic public RDP port allocation
- optional FiveM TCP + UDP mapping
- port collision protection
- smart storage selection
- manual storage selection from WHMCS
- CPU and RAM resize support
- VHD/VHDX growth
- automatic Windows `C:` expansion when possible
- friendly VM names
- privacy-friendly PenguinPanel usernames
- Guacamole web RDP
- branded HTML emails
- safe cleanup when a VPS is terminated
- API rate limiting and optional source-IP restrictions

---

# 🧭 How everything connects

A typical production layout looks like this:

```text
Internet
   │
   ├── https://vps.example.com
   │         │
   │         ▼
   │    PenguinPanel
   │         │
   │         ▼
   │      Hyper-V
   │
   ├── https://billing.example.com
   │         │
   │         ▼
   │       WHMCS
   │         │
   │         └──── PenguinPanel API ────► PenguinPanel
   │
   └── rdp.example.com:<allocated port>
             │
             ▼
        Hyper-V host
             │
             ▼
           WinNAT
             │
             ▼
       Customer Windows VM
```

Current PenguinHosting deployment examples are:

```text
PenguinPanel:
https://vps.penguinhosting.host/

Web RDP / Guacamole:
https://remotedesktop.penguinhosting.host

Native customer RDP display:
remotedesktop.penguinhosting.host:<allocated port>

Public IPv4:
81.16.177.72
```

Friends installing their own copy should replace the domains/IP with their own values.

---

# ✅ What you need

## Hyper-V host

You need a Windows machine capable of running Hyper-V.

Current production development has been done around a Windows Server + Hyper-V setup.

Required/expected components:

| Component | Why it is needed |
|---|---|
| Hyper-V | Runs customer VPSes |
| .NET 10 SDK | Builds the current `net10.0` project |
| MariaDB or MySQL | Stores PenguinPanel data |
| PowerShell | Hyper-V, WinNAT and guest automation |
| Internal Hyper-V switch | Connects VMs to the private NAT network |
| WinNAT | Maps public RDP/FiveM ports to VMs |
| HTTPS/reverse proxy | Recommended for the public panel/API |

## WHMCS server

You need a working WHMCS installation that can reach the PenguinPanel API over HTTPS.

## Optional components

| Component | Needed when |
|---|---|
| Apache Guacamole | You want browser-based RDP |
| SMTP server | PenguinPanel should send account/VPS emails |
| DNS hostname for RDP | You want customers to use a friendly RDP hostname |

---

# 🚀 Quick setup checklist

For a completely fresh installation, the short version is:

```text
[ ] 1. Install Hyper-V and .NET 10 SDK
[ ] 2. Install MariaDB/MySQL
[ ] 3. Copy the PenguinPanel source
[ ] 4. Import database/schema.sql
[ ] 5. Create appsettings.json from appsettings.example.json
[ ] 6. Configure the Hyper-V internal switch + WinNAT
[ ] 7. Configure storage, IP ranges and port ranges
[ ] 8. Build PenguinPanel
[ ] 9. Start PenguinPanel and verify admin access
[ ] 10. Generate the WHMCS API key
[ ] 11. Upload the WHMCS modules
[ ] 12. Add PenguinPanel as a WHMCS server
[ ] 13. Create/configure a VPS product
[ ] 14. Create one disposable test VPS
[ ] 15. Test RDP, FiveM mapping, resize and termination
```

Do not enable automatic production provisioning until the disposable VPS test passes.

---

# 🛠 Fresh installation

## Step 1 — Copy PenguinPanel

Copy:

```text
PenguinPanel/
```

to the Hyper-V server.

Example development location:

```text
C:\Discordbots\scripts\hyperv-panel
```

You may use another folder. Nothing depends on that exact development path.

---

## Step 2 — Verify .NET

The current project targets:

```xml
<TargetFramework>net10.0</TargetFramework>
```

Check the installed SDK:

```powershell
dotnet --info
```

Then test restore/build:

```powershell
cd C:\Discordbots\scripts\hyperv-panel
dotnet restore
dotnet build -c Release
```

The repository also contains:

```text
Build-Step1.ps1
```

which runs restore + Release build.

Example:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Build-Step1.ps1
```

---

## Step 3 — Create the database

The schema-only database backup is:

```text
database/schema.sql
```

It creates:

```text
hyperv_panel
```

and the required base tables.

Import it using the MariaDB/MySQL tool you normally use, for example:

- HeidiSQL
- phpMyAdmin
- MySQL/MariaDB command line

> [!NOTE]
> `AppDatabase.cs` can also create/migrate newer application tables at startup. The included schema is still useful for a clean restore and documentation.

Create a dedicated database account instead of using the MariaDB/MySQL root account from PenguinPanel.

Example connection format:

```text
Server=127.0.0.1;
Port=3307;
Database=hyperv_panel;
User ID=hyperv_panel;
Password=<YOUR_PASSWORD>;
SslMode=Preferred;
Allow User Variables=True;
```

---

## Step 4 — Create `appsettings.json`

Copy:

```text
appsettings.example.json
```

to:

```text
appsettings.json
```

Never edit the example file with production secrets.

The real file should stay private.

At minimum configure:

```text
ConnectionStrings
Security
FirstRunAdmin
Panel
Smtp
WebRdp
NetworkProfiles
Provisioning
WhmcsApi
```

A detailed explanation of each section is available later in this README.

---

## Step 5 — Set the first-run administrator password

In your private `appsettings.json`:

```json
"FirstRunAdmin": {
  "Password": "<STRONG_FIRST_RUN_PASSWORD>"
}
```

Also configure a long random Data Protection key:

```json
"Security": {
  "DataProtectionKey": "<LONG_RANDOM_VALUE>"
}
```

Do not commit either value.

---

# 🌐 Configure Hyper-V networking

PenguinPanel expects an internal Hyper-V network and WinNAT.

The current layout is:

```text
Switch name:       HPanel-NAT
NAT name:          HPanel-NAT
Network:           192.168.119.0/24
Gateway/host:      192.168.119.1
VM allocation:     192.168.119.50 - 192.168.119.220
```

## First check what already exists

Run:

```powershell
Get-VMSwitch
Get-NetNat
Get-NetIPAddress -AddressFamily IPv4
```

If `HPanel-NAT` already exists and is working, **do not create a second copy**.

## Example fresh NAT setup

Only use this example when creating a new isolated PenguinPanel NAT and the addresses do not conflict with another network on the host:

```powershell
New-VMSwitch -SwitchName "HPanel-NAT" -SwitchType Internal

New-NetIPAddress `
    -IPAddress 192.168.119.1 `
    -PrefixLength 24 `
    -InterfaceAlias "vEthernet (HPanel-NAT)"

New-NetNat `
    -Name "HPanel-NAT" `
    -InternalIPInterfaceAddressPrefix "192.168.119.0/24"
```

Verify:

```powershell
Get-VMSwitch -Name "HPanel-NAT"
Get-NetNat -Name "HPanel-NAT"
Get-NetIPAddress -InterfaceAlias "vEthernet (HPanel-NAT)"
```

> [!CAUTION]
> Do not copy these commands blindly onto a host that already has networking/NAT configured. Check for address conflicts first.

---

# ⚙️ Configure PenguinPanel

The most important section is:

```json
"Provisioning": {
  "PublicAddress": "<PUBLIC_IPV4>",
  "PublicHostname": "<OPTIONAL_PUBLIC_HOSTNAME>",

  "VmStorageRoot": "D:\\Vms",
  "VmStorageRoots": [
    "D:\\Vms",
    "C:\\Virtual Machines"
  ],

  "StorageSelectionMode": "MostFreeSpace",
  "MinimumFreeSpaceAfterProvisionGb": 100,
  "MinimumFreeSpaceAfterProvisionPercent": 5,

  "SwitchName": "HPanel-NAT",
  "NatName": "HPanel-NAT",

  "InternalIpStart": "192.168.119.50",
  "InternalIpEnd": "192.168.119.220",
  "ReservedInternalIps": "192.168.119.1",

  "ReservedExternalPorts": "80;443",

  "Gateway": "192.168.119.1",
  "PreferredDnsServer": "1.1.1.1",
  "AlternateDnsServer": "1.0.0.1",

  "RdpPortStart": 22000,
  "RdpPortEnd": 22999,

  "FiveMPortStart": 35000,
  "FiveMPortEnd": 35999,
  "DefaultFiveMInternalPort": 30120,

  "DefaultGeneration": 2,
  "DefaultSecureBoot": true
}
```

## Storage

Current approved roots:

```text
D:\Vms
C:\Virtual Machines
```

Automatic mode:

```text
StorageSelectionMode = MostFreeSpace
```

PenguinPanel checks available space before provisioning.

Current safety reserve:

```text
at least 100 GB
or
at least 5% of the disk

whichever reserve is larger
```

If neither storage root can safely fit the requested VPS, provisioning should be refused instead of filling the host drive.

---

## Internal IPs

Example:

```text
Gateway:            192.168.119.1
First VM IP:        192.168.119.50
Last VM IP:         192.168.119.220
```

Add infrastructure addresses to:

```text
ReservedInternalIps
```

so PenguinPanel never assigns them to a customer VM.

Example:

```json
"ReservedInternalIps": "192.168.119.1;192.168.119.40"
```

---

## Public RDP ports

Recommended dedicated range:

```text
22000-22999
```

Do not manually give every VPS a port.

In WHMCS use:

```text
Public RDP Port = 0
```

`0` means **allocate a free public port automatically**.

---

## FiveM ports

Recommended dedicated public range:

```text
35000-35999
```

The guest can still use the normal FiveM port:

```text
30120
```

Example:

```text
Customer connects to:
81.16.177.72:35004

PenguinPanel maps that to:
192.168.119.55:30120
```

In WHMCS use:

```text
FiveM Internal Port = 30120
Public FiveM Port   = 0
```

---

## Reserved external ports

This is important when PenguinPanel shares a host with other services.

Example current deployment:

```json
"ReservedExternalPorts": "80;443;3306;3307;8443;8822;9998;30120;30124;40105"
```

You can reserve a whole range too:

```text
30000-32000
```

Use this for ports PenguinPanel must never allocate, even when the other service is temporarily stopped.

The allocator also checks:

```text
PenguinPanel allocations
WinNAT mappings
host TCP listeners
host UDP endpoints
reserved ports/ranges
```

---

# 🔑 Configure the WHMCS API

The repository includes:

```text
Configure-WhmcsApi.ps1
```

It generates a random API key, stores only its SHA-256 hash in `appsettings.json`, and prints the real key once.

Run from the PenguinPanel folder:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Configure-WhmcsApi.ps1
```

You will see something like:

```text
API key: <SAVE_THIS_VALUE>
SHA256 : <HASH_STORED_IN_APPSETTINGS>
```

## Save the API key immediately

The plaintext key is **not** stored in `appsettings.json`.

You need the real key when configuring the WHMCS server.

## Optional source-IP restriction

When you know the real source IP PenguinPanel sees from the WHMCS server:

```powershell
.\Configure-WhmcsApi.ps1 -AllowedIp "203.0.113.10"
```

Do not guess the IP. A wrong allowlist can lock WHMCS out of PenguinPanel.

Recommended production behavior:

```json
"WhmcsApi": {
  "Enabled": true,
  "RequireHttps": true,
  "AllowedIps": [],
  "TrustForwardedFor": false,
  "MaxRequestsPerMinute": 120
}
```

Start with an empty allowlist while testing, then restrict it after verifying the real source IP.

Restart PenguinPanel after changing the API configuration.

---

# 🌍 Make PenguinPanel reachable

Your WHMCS server must be able to reach PenguinPanel.

Current example:

```text
https://vps.penguinhosting.host/
```

Use your existing IIS/reverse-proxy/TLS setup to expose PenguinPanel over HTTPS.

This repository does **not** contain production TLS certificates or private keys.

Verify from another machine:

```text
https://your-panel-hostname/
```

before configuring WHMCS.

---

# 🧩 Install the WHMCS modules

## Server module

Upload:

```text
WHMCS/modules/servers/penguinpanel/
```

to:

```text
/public_html/modules/servers/penguinpanel/
```

## Addon module

Upload:

```text
WHMCS/modules/addons/penguinpanel/
```

to:

```text
/public_html/modules/addons/penguinpanel/
```

Activate/configure the addon if required by the current module version.

---

# 🖥 Add PenguinPanel as a WHMCS server

In WHMCS, create a server using:

```text
Module:   PenguinPanel Hyper-V
Hostname: vps.penguinhosting.host
SSL:      Enabled
Port:     443
```

For another deployment, replace the hostname with that installation's PenguinPanel hostname.

Store the **plaintext API key generated by `Configure-WhmcsApi.ps1`** in the module credential/password field.

Press:

```text
Test Connection
```

Do not continue until this works.

Create a server group such as:

```text
PenguinPanel VPS
└── Virtual-Server
```

Assign your VPS products to that group.

---

# 📦 Create a WHMCS VPS product

A normal product currently uses settings like:

| Setting | Example |
|---|---|
| OS Template | Valid PenguinPanel template ID |
| CPU Cores | `4` |
| Memory | `4096 MB` |
| Disk | `127 GB` |
| Hyper-V Generation | `2` |
| Secure Boot | Enabled |
| Start After Provision | Enabled |
| Configure Windows Guest | Enabled |
| Windows Username | `Administrator` |
| Template Administrator Password | Password inside the golden image |
| VM Storage Root | Automatic / blank |
| Enable FiveM Mapping | Enabled when sold |
| FiveM Internal Port | `30120` |
| Public RDP Port | `0` |
| Public FiveM Port | `0` |
| RDP Customer Hostname | `remotedesktop.penguinhosting.host` |

## Important values

### Public ports should normally be `0`

```text
Public RDP Port   = 0
Public FiveM Port = 0
```

That lets PenguinPanel pick a safe port.

### Storage should normally be automatic

```text
VM Storage Root = blank / Automatic
```

PenguinPanel can then choose the approved drive with safe free space.

### The Windows template password matters

If:

```text
Configure Windows Guest = Enabled
```

then:

```text
Template Administrator Password
```

must match the Administrator password inside the golden Windows template.

Treat this value as sensitive.

### RDP customer hostname

Current intended value:

```text
remotedesktop.penguinhosting.host
```

If upgrading an older WHMCS product and this field contains:

```text
PH-VPS-
```

replace it with the RDP hostname and save.

The old `Fallback VM Name Prefix` slot became `RDP Customer Hostname` in v1.2.3.

---

# 💳 Prepaid 1-5 month products

The current business model is:

> **The customer pays the entire selected period up front.**

This means the products should use:

```text
Payment Type = One Time
Recurring Cycles Limit = 0
```

At 150 SEK per month:

| Product | Customer pays now | Fixed term |
|---|---:|---:|
| VPS - 1 Månad | 150 SEK | 30 days |
| VPS - 2 Månad | 300 SEK | 60 days |
| VPS - 3 Månad | 450 SEK | 90 days |
| VPS - 4 Månad | 600 SEK | 120 days |
| VPS - 5 Månad | 750 SEK | 150 days |

For every product:

```text
Recurring Cycles Limit = 0
Termination Email = PenguinPanel VPS - Avslutningsmail
```

`Auto Terminate / Fixed Term` is the number of literal days after activation.

It is **not** the number of days an invoice may remain unpaid.

WHMCS's separate automation settings handle overdue invoices, suspension, and non-payment termination.

---

# 🧪 Test your first VPS

Before opening the product to customers, create a **disposable test client/order**.

For early testing, the safest WHMCS provisioning setting is:

```text
Do not automatically setup this product
```

Then you can inspect everything before pressing:

```text
Module Commands → Create
```

## After Create, verify these items

### 1. Hyper-V VM exists

```powershell
Get-VM
```

The friendly VM name should normally be based on the WHMCS customer's first name.

If the name already exists, PenguinPanel should create a unique form such as:

```text
Test
Test-887
Test-887-2
```

### 2. VM has the expected internal IPv4

Verify in PenguinPanel/WHMCS and inside the Windows guest.

Expected example:

```text
192.168.119.50-220
```

### 3. Public RDP mapping exists

On the Hyper-V host:

```powershell
Get-NetNatStaticMapping -NatName "HPanel-NAT"
```

You should see an allocated public RDP port mapped to the VM's configured RDP port.

### 4. Windows is actually listening on the new RDP port

Inside the VM, confirm the RDP listener was changed.

If Windows does not allow `TermService` to restart during provisioning, PenguinPanel's current fallback can reboot the VM once, wait for PowerShell Direct, and verify the new listener.

### 5. Native RDP works

Example customer endpoint:

```text
remotedesktop.penguinhosting.host:220xx
```

If using a hostname for raw/native RDP, DNS must resolve directly to the Hyper-V host/public IP.

A normal Cloudflare proxy/Tunnel does not proxy arbitrary native RDP ports.

### 6. FiveM mapping works

When FiveM is enabled, verify both TCP and UDP mapping.

Customer example:

```text
81.16.177.72:350xx
```

Guest example:

```text
192.168.119.xx:30120
```

### 7. Welcome email has useful values

Verify that the email contains the expected:

```text
VM name
RDP hostname
RDP port
Windows username
public IPv4
FiveM port when enabled
PenguinPanel link
```

### 8. Start/stop works

Test the customer/admin power controls.

### 9. Suspend/unsuspend works

Test both module actions.

### 10. Terminate the disposable VPS

This is an important test.

Verify that termination cleans up:

```text
Hyper-V VM
managed VM storage
WinNAT mappings
firewall/network data
WHMCS service mapping
```

Do this before allowing real automatic provisioning.

---

# 📈 Resource upgrades

The module implements WHMCS `ChangePackage`.

Recommended configurable option names are exactly:

```text
CPU Cores
RAM
Disk GB
```

Example options:

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
| CPU | ✅ | ✅ |
| RAM | ✅ | ✅ |
| VHD/VHDX | ✅ | ❌ |

## Why disk shrinking is blocked

Shrinking a virtual disk safely is much more dangerous than growing it.

PenguinPanel deliberately refuses disk shrink requests.

## Disk growth

Before growing the disk, PenguinPanel checks the storage safety reserve.

When possible, it then uses PowerShell Direct to extend Windows `C:`.

An active `.avhdx` checkpoint/differencing disk blocks automatic disk growth.

---

# 🗑 How termination works

Use WHMCS/PenguinPanel's **Terminate** action.

Do not simply delete the WHMCS service record first.

A plain WHMCS record deletion does not guarantee that the provisioning module gets a chance to remove infrastructure.

Termination is designed to clean up:

```text
Hyper-V VM
managed storage
WinNAT mappings
related network/firewall data
PenguinPanel ↔ WHMCS service mapping
```

## PenguinPanel account cleanup

WHMCS-created customer accounts are removed conservatively.

The account should only be deleted when it is safe and the customer no longer owns another active VPS.

Manually created/reused accounts should not be removed accidentally.

---

# 📧 Email setup

## PenguinPanel SMTP

Example:

```json
"Smtp": {
  "Enabled": true,
  "Host": "mailserver.example.com",
  "Port": 587,
  "EnableSsl": false,
  "Username": "noreply@example.com",
  "Password": "<SMTP_PASSWORD>",
  "FromEmail": "noreply@example.com",
  "FromName": "PenguinPanel",
  "TimeoutSeconds": 15
}
```

Current PenguinPanel-generated emails use the shared dark PenguinHosting theme.

## Included WHMCS templates

See:

```text
WHMCS/email-templates/
```

The backup includes the current Swedish:

```text
VPS welcome email
VPS fixed-term termination email
```

Use the custom PenguinPanel VPS welcome email instead of WHMCS's old generic Hosting Account Welcome Email.

The generic hosting email may generate irrelevant cPanel-style links.

## Custom fields used by WHMCS emails

The current module syncs hidden/admin-only Product Custom Fields for values such as:

```text
PenguinPanel VM Name
RDP Host
RDP Port
Windows Username
Internal IPv4
Public IPv4
FiveM Port
FiveM Internal Port
```

Example merge fields include:

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

# 🖥 Web RDP / Guacamole

Guacamole is optional.

Example:

```json
"WebRdp": {
  "Enabled": true,
  "Provider": "Guacamole",
  "BaseUrl": "https://remotedesktop.example.com",
  "EmbedInConsole": true,
  "PreferPerVmWebConsoleUrl": false,
  "PathTemplate": "/#/client/{connectionId}",
  "Security": "nla",
  "JsonAuth": {
    "Enabled": true,
    "SecretKey": "<GUACAMOLE_JSON_AUTH_SECRET>",
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

Never commit:

```text
SecretKey
```

to GitHub.

---

# 🆘 Troubleshooting

This section is intentionally written as **“what you see → what to check”**.

| Problem | Check first |
|---|---|
| WHMCS `Test Connection` fails | PenguinPanel URL, HTTPS/443, API key, `RequireHttps`, IP allowlist |
| WHMCS can open the panel but API still fails | Wrong API key or wrong source IP in `AllowedIps` |
| Create fails before VM exists | Read PenguinPanel error/log; retry is safe when no VM was created |
| VM exists but Windows configuration fails | Template Administrator password, PowerShell Direct availability, guest boot state |
| RDP public mapping exists but RDP does not connect | Windows listener port, Windows Firewall, DNS, WinNAT mapping |
| RDP hostname works in browser but native RDP does not | Hostname may be proxied instead of resolving directly to the Hyper-V public IP |
| FiveM cannot connect | TCP + UDP WinNAT mapping, host/guest firewall, internal port `30120` |
| PenguinPanel refuses a storage root | Requested disk + configured safety reserve cannot fit |
| Wrong VM gets selected/name collision | Current allocator should check Hyper-V + DB and append service ID/suffix |
| Email fields are blank | Ensure current WHMCS module is installed and service custom fields were synced |
| Disk downgrade fails | Expected: disk shrinking is intentionally blocked |
| Disk upgrade fails with `.avhdx` | Remove/merge the active checkpoint correctly before retrying |
| WHMCS product shows `PH-VPS-` as RDP hostname | Change `RDP Customer Hostname` to the intended RDP domain and save |
| WHMCS shows 150 SEK for a 4-month plan | Product is probably still Recurring/Monthly; prepaid plans must be One Time at 600 SEK |
| Terminate did not run | Confirm WHMCS automation/cron and that the service was Terminated, not merely deleted |
| Public port collision occurs | Check reserved range, WinNAT mappings, TCP listeners and UDP endpoints |

## Useful host commands

Hyper-V:

```powershell
Get-VM
Get-VMSwitch
```

NAT:

```powershell
Get-NetNat
Get-NetNatStaticMapping
```

Listening TCP ports:

```powershell
Get-NetTCPConnection -State Listen
```

UDP endpoints:

```powershell
Get-NetUDPEndpoint
```

Build:

```powershell
dotnet build
```

---

# 💾 Backup and restore

There are **two different backups** to think about.

## GitHub-safe backup

This repository may contain:

```text
source code
sanitized appsettings example
database schema
WHMCS modules
email templates
documentation
checksums
```

It should **not** contain real secrets or customer data.

## Private disaster-recovery backup

Keep a separate encrypted/private copy of:

```text
production appsettings.json
MariaDB/MySQL data dump
PenguinPanel/WHMCS API credential
SMTP password
Guacamole JSON auth secret
golden Windows VHD/VHDX templates
customer VM disks if your backup policy requires them
TLS/private keys where applicable
```

See:

```text
PRIVATE-BACKUP-CHECKLIST.md
```

---

## Restore order

If rebuilding from nothing, restore in this order:

```text
1. Windows + Hyper-V
2. Hyper-V internal switch / WinNAT
3. MariaDB/MySQL
4. PenguinPanel source
5. PenguinPanel production appsettings.json
6. PenguinPanel database
7. Windows VHDX templates
8. Build/start PenguinPanel
9. HTTPS/reverse proxy
10. Guacamole if used
11. WHMCS server/addon modules
12. WHMCS server connection
13. Test Connection
14. Disposable provisioning test
15. Production provisioning
```

---

# 🔧 Configuration reference

This section is for quickly remembering **what a setting means** after the system is already running.

## `ConnectionStrings:PanelDb`

MariaDB/MySQL connection used by PenguinPanel.

```json
"ConnectionStrings": {
  "PanelDb": "Server=127.0.0.1;Port=3307;Database=hyperv_panel;User ID=hyperv_panel;Password=<PASSWORD>;SslMode=Preferred;Allow User Variables=True;"
}
```

---

## `Security`

```json
"Security": {
  "DataProtectionKey": "<LONG_RANDOM_KEY>",
  "MaxFailedLoginAttempts": 5,
  "LoginLockoutMinutes": 15,
  "EmergencyResetRequireEmailCode": true,
  "EmergencyResetCodeMinutes": 10,
  "EmergencyResetCodeCooldownSeconds": 60
}
```

Do not change the production Data Protection key casually.

---

## `Panel:PublicUrl`

Customer/admin-facing PenguinPanel URL.

Example:

```text
https://vps.example.com/
```

---

## `Provisioning:PublicAddress`

The real public IPv4 used for NAT/networking.

Example current deployment:

```text
81.16.177.72
```

---

## `Provisioning:PublicHostname`

Optional friendly hostname used by PenguinPanel when displaying an endpoint.

Networking still uses `PublicAddress`.

This is **not necessarily the same thing** as the WHMCS RDP Customer Hostname.

---

## WHMCS `RDP Customer Hostname`

Customer-facing native RDP hostname.

Current intended value:

```text
remotedesktop.penguinhosting.host
```

Example display:

```text
remotedesktop.penguinhosting.host:22014
```

FiveM is intentionally displayed using:

```text
public IPv4 + FiveM port
```

---

## `VmStorageRoots`

Approved VM locations only.

Current example:

```text
D:\Vms
C:\Virtual Machines
```

WHMCS/manual storage selection cannot safely select a path outside the configured approved roots.

---

## `StorageSelectionMode`

Current mode:

```text
MostFreeSpace
```

PenguinPanel resolves automatic storage live during Create.

---

## `MinimumFreeSpaceAfterProvisionGb`

Current:

```text
100
```

---

## `MinimumFreeSpaceAfterProvisionPercent`

Current:

```text
5
```

The effective reserve is the larger of the GB or percentage setting.

---

## `InternalIpStart` / `InternalIpEnd`

Current example:

```text
192.168.119.50
192.168.119.220
```

---

## `ReservedInternalIps`

Addresses PenguinPanel must never give to a VM.

Example:

```text
192.168.119.1;192.168.119.40
```

---

## `ReservedExternalPorts`

Ports/ranges PenguinPanel must never allocate publicly.

Example:

```text
80;443;3306;3307;8443;8822;9998;30120;30124;40105
```

Ranges are also supported where configured by the current allocator.

---

## RDP range

```text
22000-22999
```

Keep product `Public RDP Port` set to:

```text
0
```

for automatic allocation.

---

## FiveM range

```text
35000-35999
```

Keep:

```text
FiveM Internal Port = 30120
Public FiveM Port   = 0
```

for the standard automatic mapping setup.

---

# 👤 Friendly naming

## VM name

Default:

```text
WHMCS first name
```

Collision fallback:

```text
FirstName
FirstName-<serviceId>
FirstName-<serviceId>-2
...
```

Final fallback:

```text
PH-VPS-<serviceId>
```

The allocator checks both Hyper-V and the PenguinPanel/WHMCS mapping database.

Existing VMs are not silently renamed.

## PenguinPanel username

New WHMCS-created customer usernames prefer the customer's first name instead of exposing their email address.

If necessary, PenguinPanel generates a unique client-specific variant.

Existing accounts are not silently renamed.

---

# 🔒 GitHub safety

Never commit production:

```text
appsettings.json
*.vhd
*.vhdx
*.avhdx
database dumps containing customer data
API keys
SMTP passwords
Guacamole secrets
private TLS keys
```

Before pushing:

```bash
git status
git diff
```

Review what is actually going to GitHub.

The included `.gitignore` helps, but it is not a replacement for checking the commit yourself.

---

# 📂 Repository layout

```text
.
├── PenguinPanel/
│   ├── Components/
│   ├── Services/
│   ├── wwwroot/
│   ├── Build-Step1.ps1
│   ├── Configure-WhmcsApi.ps1
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

# 🧾 Other useful documents

| File | Purpose |
|---|---|
| `WHMCS-PRODUCT-SETUP.md` | WHMCS-specific product notes |
| `PATCH-HISTORY.md` | Condensed history of the integration changes |
| `PRIVATE-BACKUP-CHECKLIST.md` | Secrets/data that belong in a private backup |
| `FILE-MANIFEST.sha256` | File-integrity checksums |
| `database/schema.sql` | Schema-only database backup |
| `PenguinPanel/appsettings.example.json` | Sanitized configuration example |

---

# ✅ Post-restore acceptance test

A rebuild is not considered finished until all of these work:

```text
[ ] dotnet build succeeds
[ ] PenguinPanel starts
[ ] Admin login works
[ ] WHMCS Test Connection succeeds
[ ] Catalog/templates/storage load
[ ] Disposable Create succeeds
[ ] Correct VM storage root is used
[ ] Windows guest gets the expected IPv4
[ ] Windows RDP listener changes
[ ] RDP WinNAT mapping exists
[ ] Native RDP works
[ ] FiveM TCP + UDP work when enabled
[ ] Welcome email contains the correct details
[ ] Start/stop works
[ ] Suspend/unsuspend works
[ ] CPU/RAM ChangePackage works
[ ] Disk growth works on a disposable VM
[ ] Terminate removes the disposable VM/mappings/storage
```

---

# 🐧 Final note

The most important rule when maintaining PenguinPanel is:

> **Make the safe/default path automatic, but refuse dangerous actions instead of guessing.**

Examples already implemented:

```text
Port in use?        Pick another port.
Disk too full?      Refuse provisioning.
VM name exists?     Generate a safe unique name.
Disk shrink?        Refuse it.
Active AVHDX?       Refuse automatic disk growth.
Termination?        Clean up infrastructure deliberately.
Secrets?            Keep them out of GitHub.
```

When adding a new feature, update:

```text
README.md
VERSION.txt
PATCH-HISTORY.md
WHMCS-PRODUCT-SETUP.md
FILE-MANIFEST.sha256
```

and create a new GitHub-safe backup snapshot.
