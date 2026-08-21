# Guacamole console setup for PenguinPanel

PenguinPanel supports two Guacamole modes.

## Mode 1: Saved Guacamole connection ID

Use this when you manually create every connection inside Guacamole.

1. Open the connection in Guacamole.
2. Copy the value after `/#/client/`.
3. Paste that value into PenguinPanel → VM → Access → Guacamole console URL / connection ID.

Example URL:

```text
http://192.168.119.157:8080/guacamole/#/client/MQBjAHBvc3RncmVzcWw
```

Connection ID:

```text
MQBjAHBvc3RncmVzcWw
```

The ID is not the name `Woof`. It is Guacamole's encoded connection identifier.

## Mode 2: JSON auto-login, recommended for clients

This mode avoids the Guacamole login page. PenguinPanel generates a temporary Guacamole session from the VM Access tab RDP details.

Apache Guacamole calls this encrypted JSON authentication. Guacamole accepts JSON data that has been signed with HMAC-SHA256 and encrypted with AES-CBC using a shared 128-bit key.

### 1. Generate a secret on the Ubuntu Guacamole VM

```bash
openssl rand -hex 16
```

Save the generated 32-character hex value.

### 2. Enable JSON auth in Docker Compose

Edit `~/guacamole/docker-compose.yml` and add this under the `guacamole:` container environment section:

```yaml
      JSON_ENABLED: "true"
      JSON_SECRET_KEY: "PASTE_THE_32_HEX_SECRET_HERE"
```

Then recreate Guacamole:

```bash
cd ~/guacamole
sudo docker compose up -d
sudo docker compose logs --tail=100 guacamole
```

### 3. Enable JSON auth in PenguinPanel

In `appsettings.json` and `appsettings.Development.json`:

```json
"WebRdp": {
  "Enabled": true,
  "Provider": "Guacamole",
  "BaseUrl": "https://remotedesktop.penguinhosting.host/guacamole",
  "EmbedInConsole": true,
  "PreferPerVmWebConsoleUrl": false,
  "Security": "nla",
  "JsonAuth": {
    "Enabled": true,
    "SecretKey": "PASTE_THE_SAME_32_HEX_SECRET_HERE",
    "TokenLifetimeMinutes": 60,
    "Security": "nla",
    "IgnoreCertificate": true,
    "ResizeMethod": "display-update"
  }
}
```

For local testing, `BaseUrl` can be:

```text
http://192.168.119.157:8080/guacamole
```

For real clients, use a public HTTPS domain through your reverse proxy:

```text
https://remotedesktop.penguinhosting.host/guacamole
```

### 4. Leave the per-VM Guacamole field empty

With JSON auto-login enabled, you do not need to manually create Guacamole connections or paste connection IDs.

PenguinPanel uses the VM Access tab details:

- IP / hostname
- RDP port
- username
- password

Then it creates a temporary Guacamole RDP session automatically.

## Important notes

- Keep the JSON secret private. Anyone with the secret can generate valid Guacamole sessions.
- Use HTTPS before allowing clients to use the console publicly.
- The VM must still have Windows RDP enabled.
- Guacamole must be able to reach the VM RDP endpoint.


## Guacamole stable JSON connection fix

This build uses a stable temporary JSON-auth connection id (`penguin-console`) for all generated Guacamole sessions. The RDP target, username and password still come from each VM Access tab, but the Guacamole route no longer depends on the VM name. This prevents cases where one VM works and another shows `The requested connection does not exist`.

For JSON auto-login, leave the per-VM Guacamole console URL / connection ID field empty.
