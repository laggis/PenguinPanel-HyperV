# Step 3.0.6 — Hyper-V Basic Console

The VMPanel-V2 reference implementation uses VMware Workstation's built-in VNC
server (`RemoteDisplay.vnc.*`) and noVNC. Hyper-V does not have that VMX/VNC
feature.

PenguinPanel's Hyper-V equivalent uses Microsoft's virtualization WMI provider:

- `GetVirtualSystemThumbnailImage` — host-side VM framebuffer
- `Msvm_Keyboard` — TypeText / TypeKey / Ctrl+Alt+Delete
- `Msvm_SyntheticMouse` — absolute cursor positioning and clicks

## Why this is a true emergency console

The display path does not depend on:

- guest IPv4 configuration
- gateway/DNS
- WinNAT
- guest or host RDP firewall
- TermService/RDP
- WinRM
- PowerShell Direct
- PenguinGuestAgent
- Guacamole
- VNC
- a second customer login

The user still signs into Windows using the normal Windows/RDP account.

## Performance

This is intentionally a **basic/session console**, not a replacement for RDP's
full desktop performance. PenguinPanel polls the Hyper-V framebuffer while the
Console tab is open.

Default settings:

```json
"Console": {
  "Mode": "HyperVBasic",
  "HyperV": {
    "RefreshMs": 500,
    "MaxWidth": 800,
    "MaxHeight": 600,
    "FlipVertical": false
  }
}
```

Use native RDP after Windows has booted for high-frame-rate desktop work.

## Input

Click the console image to focus it. Normal printable keyboard characters,
Enter, Tab, Backspace, Escape, Delete, arrows, Home/End, Page Up/Down and F1-F12
are forwarded.

The toolbar contains a dedicated Ctrl+Alt+Delete action.

Left and right mouse clicks are sent through Hyper-V's synthetic mouse.

## First-host test

This code must be tested on the actual Windows Hyper-V host because the WMI
framebuffer behavior is supplied by VMMS.

If the picture is vertically inverted on the host, set:

```json
"FlipVertical": true
```

and restart PenguinPanel.

## Why this differs from VMPanel-V2

The reference project supplied for comparison is VMware Workstation based. It
turns on VMware's built-in VNC display in the `.vmx` file and uses noVNC in the
browser.

Hyper-V has no equivalent `RemoteDisplay.vnc.enabled` switch. PenguinPanel uses
Microsoft's Hyper-V WMI provider instead:

```text
GetVirtualSystemThumbnailImage -> current VM framebuffer (RGB565)
Msvm_Keyboard                 -> keyboard input
Msvm_SyntheticMouse           -> mouse input
```

No VNC listener or extra public/private console port is opened.

## Basic-console performance

This is intentionally an emergency/basic console, not a replacement for RDP
video performance. The default is an 800x600 framebuffer approximately once per
second, only while someone has the Console tab open.

Use it for:

- firmware/boot progress;
- Windows startup/recovery;
- Windows login;
- diagnosing broken networking/RDP;
- emergency keyboard/mouse access.

After Windows is healthy, normal RDP remains the smoother full-desktop option.

## Early-boot display behavior

Frame capture does not depend on `Msvm_SyntheticDisplayController`. That
controller may not be active until guest display services load. PenguinPanel
calls `GetVirtualSystemThumbnailImage` directly and treats video-head resolution
as optional metadata, so firmware/startup capture can still be attempted.


## Step 3.0.7 performance tuning

The balanced default refresh is now:

```json
"RefreshMs": 500
```

which targets roughly two framebuffer updates per second before WMI capture
overhead.

The Console service also caches the discovered VM WMI path and Hyper-V
VirtualSystemManagementService path instead of re-discovering them on every
frame.

When Hyper-V returns exactly the same framebuffer pixels as the previous poll,
PenguinPanel reuses the existing image `DataUri`. Blazor therefore does not need
to resend/redecode the large BMP `src` value just to update the heartbeat time.

`RefreshMs` accepts values down to `250` ms if you want to experiment with a
faster basic console. This is intentionally not the default because several
simultaneously-open console tabs can otherwise create unnecessary WMI, CPU and
network load.

The VM framebuffer itself now uses rounded clipping so it visually matches the
PenguinPanel console card.


## Step 3.0.10 fast frames and keyboard chords

Changed frames are now encoded as PNG with `CompressionLevel.Fastest` before being sent through Blazor. This avoids transmitting the much larger uncompressed BMP payload.

`RefreshMs` is now a target cycle interval: if WMI frame capture takes part of the interval, PenguinPanel waits only for the remainder. The balanced default is 350 ms.

The focused console forwards Ctrl/Alt/Shift key chords that the browser delivers. Browser/OS-reserved shortcuts cannot be made reliable with an iframe, so the toolbar provides host-side Send keys actions for Ctrl+Esc, Alt+Tab, Alt+F4 and the Windows key. Ctrl+Alt+Del retains its dedicated button.
