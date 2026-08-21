# Myrtille setup notes for PenguinPanel

PenguinPanel can embed/open Myrtille in the Console tab. Myrtille itself must be installed once on a Windows/IIS gateway server.

For the current setup, Myrtille is expected here:

```text
https://remotedesktop.penguinhosting.host:8110/
```

If you kept Myrtille on another port, change `WebRdp:BaseUrl` in both `appsettings.json` and `appsettings.Development.json`.

The customer VMs do not need Myrtille installed. They only need Windows Remote Desktop enabled.

## Flow

```text
User browser -> PenguinPanel iframe/new tab -> Myrtille gateway -> RDP -> VM
```

## PenguinPanel config

Set this in both `appsettings.json` and `appsettings.Development.json`:

```json
"WebRdp": {
  "Enabled": true,
  "Provider": "Myrtille",
  "BaseUrl": "https://remotedesktop.penguinhosting.host:8110",
  "EmbedInConsole": true,
  "PreferPerVmWebConsoleUrl": true,
  "AutoConnectInConsole": true,
  "PathTemplate": "/?server={address}&user={username}",
  "AutoConnectTemplate": "/?__EVENTTARGET=&__EVENTARGUMENT=&server={address}&user={username}&password={password}&connect=Connect%21"
}
```

`AutoConnectInConsole=true` makes the Console tab use the saved Access tab RDP address, username and password automatically.

Security note: Myrtille auto-connect needs the password in the generated URL. That can be visible in browser history, logs, screenshots, proxy logs, and browser dev tools. If you do not want that, set:

```json
"AutoConnectInConsole": false
```

Then the Console tab will open the Myrtille login form with only server/user filled in.

## VM Access tab

Preferred internal setup:

```text
IP address: 192.168.119.100
RDP port: 3389
Username: Administrator
Password: VM password
Browser console URL: leave empty
```

Public NAT test setup:

```text
IP address: 81.16.177.72
RDP port: 1337
Username: Administrator
Password: VM password
Browser console URL: leave empty
```

## Blank screen checklist

If Myrtille opens and shows its toolbar but the desktop remains blank:

1. Test with normal Microsoft Remote Desktop from the Myrtille server.
2. Try `Security: AUTO`, then `Security: NLA` in Myrtille.
3. Try username formats such as `Administrator`, `.\Administrator`, or `VMNAME\Administrator`.
4. Check `C:\Program Files (x86)\Myrtille\log`.
5. Check Windows Event Viewer > Application for Myrtille/.NET/FreeRDP errors.
6. Make sure `Myrtille.Services` is running.

The file `wfreerdp-client.dll` can be normal for newer Myrtille builds. Do not rename it.
