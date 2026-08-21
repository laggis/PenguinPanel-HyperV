# PenguinPanel Guest Agent

The Guest Agent is the safe way to perform emergency Windows/RDP password resets from PenguinPanel.

It works like this:

1. Admin opens a VM in PenguinPanel.
2. Go to **Access → Emergency Guest Agent reset**.
3. Click **Generate / rotate agent token**.
4. Copy the token. It is only shown once.
5. Install `PenguinGuestAgent` inside the matching Windows VM or inside your Windows VM template.
6. Put the panel URL and token in the agent `appsettings.json`.
7. Start the agent. The panel should show the agent as online after the next heartbeat.

## Build the agent

From the panel project root:

```powershell
dotnet publish .\PenguinGuestAgent\PenguinGuestAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o C:\PenguinGuestAgentBuild
```

Then copy the published output into the VM as:

```text
C:\PenguinGuestAgent
```

Edit:

```text
C:\PenguinGuestAgent\appsettings.json
```

Example:

```json
{
  "PanelUrl": "https://vps.example.com",
  "AgentToken": "pga_token_from_panel",
  "PollSeconds": 15
}
```

## Install as Windows service

Run PowerShell as Administrator inside the VM:

```powershell
cd C:\PenguinGuestAgent
.\install-service.ps1
```

Or install it manually:

```powershell
New-Service `
  -Name "PenguinGuestAgent" `
  -BinaryPathName "C:\PenguinGuestAgent\PenguinGuestAgent.exe" `
  -DisplayName "Penguin Guest Agent" `
  -Description "PenguinPanel secure VM guest agent" `
  -StartupType Automatic

Start-Service PenguinGuestAgent
```

The agent is now service-aware. Older builds were plain console apps, which could cause Windows service error `1053`.

To update config later:

```powershell
Stop-Service PenguinGuestAgent
notepad C:\PenguinGuestAgent\appsettings.json
Start-Service PenguinGuestAgent
```

To test it in a normal console before installing as a service:

```powershell
cd C:\PenguinGuestAgent
.\PenguinGuestAgent.exe
```


## Emergency password visibility

When you click **Generate emergency password**, PenguinPanel now shows a temporary "Emergency password ready" box with:

- Username
- Password preview
- Show/hide button
- Copy password button

The password is only kept in the current page state. It is cleared after **Queue emergency reset** or when the page reloads. Copy it before queueing the reset if the client needs to log in with it.

## Important security notes

- Use a **unique token per VM**.
- Rotate the token if you clone a VM from a template.
- The token is stored hashed in PenguinPanel. The plain token is only shown once.
- Commands are queued by the panel and pulled by the agent. You do not need inbound ports open to the VM for the agent.
- The emergency password is encrypted in the panel database command queue using the existing `Security:DataProtectionKey`.
- Do not publish a VM template with a customer-specific token already inside it.

## What the emergency reset can do

The reset command can:

- Change the local Windows password for the selected user.
- Enable the selected local user if it was disabled.
- Sign out active RDP sessions.
- Restart the Remote Desktop service.
- Optionally disable Windows Remote Desktop firewall rules after reset.
- Optionally disable other local users in the local Administrators group.

If the VM is offline, the command stays pending and runs when the VM boots and the agent checks in.

## Emergency reset email verification

Emergency Windows/RDP password resets now support an extra email verification step.

Flow:

1. Generate/copy the emergency password in the VM Access tab.
2. Click **Email reset code**.
3. Enter the 6-digit code sent to the signed-in panel user's email address.
4. Leave **Email the new password to my panel account email** enabled if the password should be emailed too.
5. Click **Queue emergency reset**.

Required configuration:

```json
"Security": {
  "EmergencyResetRequireEmailCode": true,
  "EmergencyResetCodeMinutes": 10,
  "EmergencyResetCodeCooldownSeconds": 60
},
"Smtp": {
  "Enabled": true
}
```

The reset code is stored only in memory, expires automatically, and is consumed after one successful reset. The new password is sent only to the signed-in panel user's own account email, not to every assigned VM user.


## Guest DNS settings

The Network tab can queue a `set-dns-servers` command for PenguinGuestAgent. The command runs inside the Windows VM and uses `Set-DnsClientServerAddress` to set Preferred/Alternate DNS on the chosen interface alias, or on all active physical/Hyper-V adapters when the alias is left empty. Rebuild and redeploy the Guest Agent after applying this update.
