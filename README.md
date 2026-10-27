# SignageBoard

SignageBoard is a notice board for a TV. Editors sign in to a mobile-friendly admin page at `/admin` to write notes, set their priority and expiry, publish or unpublish them, choose the board's text size and preview exactly what the TV will show. Each note on the TV shows who posted it (first name) and when.

The TV is driven by a **Raspberry Pi** running Chromium in kiosk mode. It opens a static page at `/display/` that polls a small API (`GET /api/board`) every couple of seconds and redraws only when the board changes. An ETag makes unchanged polls almost free.

This guide takes you from nothing to a working board: Entra sign-in, the server (Docker + reverse proxy), and the Pi.

- [How the pieces fit](#how-the-pieces-fit)
- [Choose a deployment layout](#choose-a-deployment-layout)
- [Prerequisites](#prerequisites)
- [1. Set up Entra ID](#1-set-up-entra-id)
- [2. Generate the display key](#2-generate-the-display-key)
- [3. Configure the server](#3-configure-the-server)
- [4. Run the container](#4-run-the-container)
- [5. Put a reverse proxy in front](#5-put-a-reverse-proxy-in-front)
- [6. Set up the Raspberry Pi](#6-set-up-the-raspberry-pi)
- [7. Check it end to end](#7-check-it-end-to-end)
- [Operations](#operations)
- [Local development](#local-development)
- [Troubleshooting](#troubleshooting)
- [Appendix: Fire TV instead of a Pi](#appendix-fire-tv-instead-of-a-pi)

## How the pieces fit

```
 Editors' phones/laptops ──HTTPS──┐                         ┌───────────── server ─────────────┐
   (sign in with Entra ID)        │                         │                                  │
                                  ├──► board.example.com ──►│ reverse proxy (Caddy/nginx)      │
 Raspberry Pi + TV ──HTTPS────────┘     :443                │   TLS, X-Forwarded-For/Proto     │
   (Chromium kiosk, display key)                            │        │ http://127.0.0.1:8080   │
                                                            │        ▼                         │
                                                            │ Docker: signageboard container   │
                                                            │   /admin  Blazor Server (Entra)  │
                                                            │   /display static page           │
                                                            │   /api/board  (IP + key gated)   │
                                                            │   /data volume: SQLite + keys    │
                                                            └──────────────────────────────────┘
```

- **Admin** (`/admin`): Blazor Server, protected by Microsoft Entra ID. Only members of one Entra security group can make changes.
- **Display** (`/display/`): a static HTML/JS page. Its data endpoint, `GET /api/board`, needs no sign-in but is gated twice: the client IP must be on an allowlist, and the request must carry a display key whose SHA-256 hash is configured on the server. Anything else gets a plain `404`.
- **Storage:** SQLite at `/data/noticeboard.db` inside the container, on a named Docker volume, with ASP.NET Core Data Protection keys in `/data/keys`. Schema changes needed by new versions are applied automatically at startup.
- **TLS:** terminated by a reverse proxy on the server. The container only listens on plain HTTP port 8080, published on `127.0.0.1`, so nothing on the network can reach it except through the proxy.

The Pi needs no software from this repository. It is just a browser pointed at the server.

## Choose a deployment layout

The app and the Pi are the same either way. What changes is which addresses go into the config.

| | **A. Internet-facing server** (VPS / cloud VM) | **B. Server on the same LAN as the Pi** |
|---|---|---|
| Who can reach `/admin` | Editors from anywhere | Editors on the LAN or VPN only |
| DNS for `board.<domain>` | Public A record → server's public IP | Internal DNS (or public A record) → server's LAN IP, e.g. `192.168.1.220` |
| TLS certificate | Caddy/Let's Encrypt with the normal HTTP challenge | Let's Encrypt via **DNS challenge**, or a certificate from your internal CA that the Pi and editors' devices trust |
| `Board__Display__AllowedIps__0` | The **public** IP of the network the Pi sits on (the office's internet IP) | The Pi's **LAN** IP, e.g. `192.168.1.39` (reserve it in DHCP) |

Entra ID only allows `https://` redirect URIs for anything other than `localhost`, so even a LAN-only server needs a proper host name and a certificate the browsers trust. Entra never connects to your server itself (it only redirects the browser), so a private host name works fine.

## Prerequisites

**Server**
- Linux with Docker Engine and the Compose plugin (`docker compose version` works). x86-64 or 64-bit ARM; a Raspberry Pi 4/5 running 64-bit Raspberry Pi OS can host the server too.
- A host name such as `board.<domain>` that resolves to the server (see the layout table).
- A reverse proxy on the host that can do TLS (Caddy or nginx; examples below).
- Outbound HTTPS to `login.microsoftonline.com` (the app downloads Entra's sign-in metadata).

**Display**
- Raspberry Pi 4 or 5 (2 GB RAM or more), a good power supply, a microSD card, and an HDMI cable to the TV. Wired Ethernet is preferable to Wi-Fi.
- A 1920×1080 TV is ideal. The board renders on a fixed 1920×1080 stage and scales to fit, so other resolutions work but may be letterboxed.

**Identity**
- A Microsoft Entra ID tenant where you can create app registrations and security groups.

## 1. Set up Entra ID

The app signs users in with OpenID Connect and receives an **ID token** directly (`response_type=id_token`, `response_mode=form_post`). It does not redeem an authorization code and has **no client secret**, so do not create one.

1. **Create the app registration.** In the Microsoft Entra admin center go to **Identity → Applications → App registrations → New registration**. Name it `SignageBoard`, choose **Accounts in this organizational directory only (single tenant)**, and under **Redirect URI** pick platform **Web** with `https://board.<domain>/signin-oidc`. Click **Register**.
2. **Add the remaining redirect URIs.** Open **Authentication**. Under the **Web** platform's **Redirect URIs** make sure all of these are present:
   - `https://board.<domain>/signin-oidc`
   - `https://board.<domain>/signout-callback-oidc`
   - `https://localhost:7095/signin-oidc` (local development)
   - `https://localhost:7095/signout-callback-oidc` (local development)
3. **Set the front-channel logout URL** on the same page to `https://board.<domain>/signout-oidc`.
4. **Enable ID tokens.** Still on **Authentication**, under **Implicit grant and hybrid flows**, tick **ID tokens (used for implicit and hybrid flows)** and save. Leave **Access tokens** unticked. Without this, sign-in fails with `AADSTS700054`.
5. **Add the groups claim.** Open **Token configuration → Add groups claim**. Tick **Security groups**, and under **ID** choose **Group ID**. Save. If editors belong to a very large number of groups, choose **Groups assigned to the application** instead, so the token never overflows (Entra drops the claim entirely past its group limit).
6. **(Recommended) Add the first-name claim.** The TV shows the poster's first name. The app uses the `given_name` claim when present and otherwise takes the first word of the display name (`name`, handling `Last, First` too). To use the real first name, open **Token configuration → Add optional claim → ID**, tick **given_name**, and save; accept the prompt to add the Microsoft Graph `profile` permission if asked. Notes from accounts with neither claim show only the date and time.
7. **Create the editor group.** Go to **Identity → Groups → All groups → New group**. Type **Security**, name it something like `SignageBoard Editors`, and add the people who should be able to edit. Open the group and copy its **Object ID**; that is `Board__EditorGroupId`.
8. **Require assignment.** Go to **Identity → Applications → Enterprise applications**, open `SignageBoard`, then **Properties**, set **Assignment required?** to **Yes**, and save. Under **Users and groups**, add the editor group (assigning a group needs Entra ID P1 or higher; on the free tier, add the users individually). Anyone not assigned is stopped at Microsoft's sign-in page with `AADSTS50105`.
9. **Require MFA.** Either create a **Conditional Access** policy (Entra ID P1) that targets the `SignageBoard` app and requires multifactor authentication, or turn on **Security defaults** for the tenant (**Identity → Overview → Properties → Manage security defaults**).
10. **Copy the two IDs.** On the app registration's **Overview** page:
    - **Directory (tenant) ID** goes into `AzureAd__TenantId`.
    - **Application (client) ID** goes into `AzureAd__ClientId`.

    They look alike. Do not swap them: a swapped pair makes the app request metadata for a tenant that doesn't exist, and every sign-in fails with `IDX20803`.

## 2. Generate the display key

The Pi proves itself with a random key, sent in the `X-Display-Key` header. The server stores only the **uppercase hex SHA-256 hash of the key's UTF-8 bytes** (`Board__Display__KeyHash`) and compares hashes in constant time.

Each recipe prints a URL-safe base64 key (43 characters, safe in a URL unencoded) and its matching hash:

- the **hash** goes into the server's `.env`;
- the **key** goes onto the Pi (step 6) and nowhere else. Never put the key in the server's config.

**Linux / macOS / Raspberry Pi (openssl)**

```sh
KEY=$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=\n')
HASH=$(printf '%s' "$KEY" | openssl dgst -sha256 | awk '{print $NF}' | tr 'a-f' 'A-F')
echo "Key:  $KEY"
echo "Hash: $HASH"
```

**PowerShell 7**

```powershell
$key = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($key)))
"Key:  $key"
"Hash: $hash"
```

**Windows PowerShell 5.1**

```powershell
$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$key = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$sha = [System.Security.Cryptography.SHA256]::Create()
$hash = ($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($key)) | ForEach-Object { $_.ToString('X2') }) -join ''
"Key:  $key"
"Hash: $hash"
```

Running several displays? They can share one key, but giving each its own key isn't possible yet (there is a single `KeyHash`). They do each need their address in `AllowedIps`.

## 3. Configure the server

On the server, clone the repository and create `.env` from the example. `.env` is git-ignored and excluded from the Docker image; never commit it.

```sh
git clone <this-repo-url> signageboard
cd signageboard
cp .env.example .env
chmod 600 .env
```

Settings use ASP.NET Core's environment-variable naming: `__` separates sections, so `Board__Display__KeyHash` is the setting `Board:Display:KeyHash`. **Arrays use numeric indexes:** `Board__Display__AllowedIps__0`, `__1`, and so on. `appsettings.json` ships placeholder values at index 0 of `AllowedIps` and `KnownProxies`, so always set `__0` for both rather than starting at `__1`.

### What each value means

**Host and environment**

| Variable | Example | What to put here |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Always `Production` on the server (`docker-compose.yml` also sets it). |
| `AllowedHosts` | `board.example.com` | The host name editors **and the Pi** type into the browser. Any request for another host gets `400 Bad Request - Invalid Hostname`, including requests by bare IP address, so the Pi must use the host name too. Separate several names with `;`. |

**Editor sign-in (Entra ID)** — values from step 1.

| Variable | Example | What to put here |
|---|---|---|
| `AzureAd__Instance` | `https://login.microsoftonline.com/` | Leave as is (keep the trailing slash). |
| `AzureAd__TenantId` | `00000000-…` | Directory (tenant) ID. |
| `AzureAd__ClientId` | `00000000-…` | Application (client) ID. |
| `AzureAd__CallbackPath` | `/signin-oidc` | Leave as is. Must match a registered redirect URI. |
| `AzureAd__SignedOutCallbackPath` | `/signout-callback-oidc` | Leave as is. Must match a registered redirect URI. |
| `Board__EditorGroupId` | `00000000-…` | Object ID of the editor security group. |

**The Pi / display**

| Variable | Example | What to put here |
|---|---|---|
| `Board__Display__KeyHash` | 64 hex characters | The **hash** from step 2. The Pi gets the matching key. |
| `Board__Display__AllowedIps__0` | `203.0.113.10` or `192.168.1.39` | The Pi's address **as the app sees it after the proxy**. Layout A: the public IP of the Pi's network (on the Pi, run `curl -4 https://ifconfig.me`). Layout B: the Pi's LAN IP (`hostname -I` on the Pi), which you should reserve in your router's DHCP settings so it never changes. Add `__1`, `__2` for more displays. IPv4 and IPv6 are matched exactly; see [IPv6](#troubleshooting) if the Pi might connect over IPv6. |
| `Board__Display__PollSeconds` | `10` | Validated (2–300) but not currently read by the display page. The Pi's poll interval is set by `?poll=N` in its URL (default 2 seconds). |

**Board limits**

| Variable | Example | What to put here |
|---|---|---|
| `Board__MaxNoteLength` | `280` | Maximum characters per note (1–2000). Longer notes mean smaller text on the TV. |
| `Board__MaxNotes` | `12` | Maximum number of published notes at once (1–100). |

**Storage** — leave these alone unless you know why.

| Variable | Example | What to put here |
|---|---|---|
| `ConnectionStrings__Board` | `Data Source=/data/noticeboard.db` | SQLite file. Must be under `/data` so it lives on the volume. |
| `DataProtection__KeysPath` | `/data/keys` | Encryption keys for sign-in cookies. Must be under `/data`, or every editor is signed out on each restart. |

**Reverse proxy trust**

| Variable | Example | What to put here |
|---|---|---|
| `ForwardedHeaders__KnownProxies__0` | `172.17.0.1` | A single proxy address whose `X-Forwarded-For`/`X-Forwarded-Proto` headers are trusted. |
| `ForwardedHeaders__KnownNetworks__0` | `172.16.0.0/12` | A trusted proxy range. `172.16.0.0/12` covers Docker's bridge networks, which is where a proxy on the host appears to come from. Leave both as in the example when the proxy runs on the same server. |

These two matter more than they look: the app only learns the Pi's real IP (for `AllowedIps`) and that the request was HTTPS (for Entra sign-in) by trusting the proxy's headers. See [step 5](#5-put-a-reverse-proxy-in-front).

**Logging**

| Variable | Example | What to put here |
|---|---|---|
| `Logging__LogLevel__Default` | `Information` | Use `Debug` temporarily when troubleshooting. |
| `Logging__LogLevel__Microsoft.AspNetCore` | `Warning` | ASP.NET Core's own messages. |

### Worked examples

**Layout A** — server on a VPS at `board.example.com`, Pi in an office whose internet IP is `203.0.113.10`:

```sh
AllowedHosts=board.example.com
Board__Display__AllowedIps__0=203.0.113.10
```

**Layout B** — server at `192.168.1.220` on the office LAN, internal DNS name `board.office.example.com`, Pi reserved at `192.168.1.39`, plus a second Pi in the break room at `192.168.1.40`:

```sh
AllowedHosts=board.office.example.com
Board__Display__AllowedIps__0=192.168.1.39
Board__Display__AllowedIps__1=192.168.1.40
```

Everything else is the same in both layouts.

## 4. Run the container

Build and start in the background (the first build downloads the .NET SDK image and takes a few minutes):

```sh
docker compose up -d --build
```

Follow the logs:

```sh
docker compose logs -f signageboard
```

What `docker-compose.yml` does for you:

- builds the image from `Dockerfile` (multi-stage; the runtime image runs as a non-root user, UID 1654);
- loads `.env` and forces `ASPNETCORE_ENVIRONMENT=Production`;
- publishes the app on `127.0.0.1:8080` only, so it's reachable just from the reverse proxy on the same host;
- keeps the database and keys on the named volume `signageboard-data` (mounted at `/data`), which survives rebuilds and `docker compose down`;
- restarts the container automatically (`unless-stopped`), including after a server reboot;
- caps container logs at 3 × 10 MB.

Check health. The app only answers to the host names in `AllowedHosts`, so send the Host header yourself until the proxy is up:

```sh
curl -H "Host: board.example.com" http://127.0.0.1:8080/healthz
```

It should print `Healthy`. A bare `curl http://127.0.0.1:8080/healthz` returns `400 Bad Request - Invalid Hostname`, which is expected.

## 5. Put a reverse proxy in front

The proxy must pass the original `Host` header, set `X-Forwarded-For` and `X-Forwarded-Proto`, and allow WebSocket upgrades (the admin page runs over a WebSocket).

**Caddy** (sets the headers, handles WebSockets, and gets certificates automatically):

```
board.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

For **layout B**, Let's Encrypt can't reach a LAN-only server over HTTP, so use the DNS challenge with your DNS provider's Caddy plugin (for example Cloudflare):

```
board.office.example.com {
    tls {
        dns cloudflare {env.CLOUDFLARE_API_TOKEN}
    }
    reverse_proxy 127.0.0.1:8080
}
```

or `tls internal` / your own CA's certificate, in which case install that CA on the Pi (`sudo cp ca.crt /usr/local/share/ca-certificates/ && sudo update-ca-certificates`; Chromium also needs it in its NSS store) and on every editor's device.

**nginx:**

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 80;
    server_name board.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    http2 on;
    server_name board.example.com;

    ssl_certificate     /etc/letsencrypt/live/board.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/board.example.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
    }
}
```

**Trusted proxy addresses.** The app only believes `X-Forwarded-*` headers from addresses in `KnownProxies` or `KnownNetworks`, and those must match the proxy's address *as seen from inside the container*. That is not `127.0.0.1`: traffic to the published port arrives from the Compose network's gateway, usually something like `172.18.0.1`, and Docker picks a different subnet per Compose project. That's why `KnownNetworks=172.16.0.0/12` is the practical setting. To see the exact address:

```sh
docker network inspect signageboard_default -f '{{(index .IPAM.Config 0).Gateway}}'
```

If it doesn't match, the app sees every Pi request as coming from the gateway, the IP allowlist rejects it, and the log shows `Display request rejected: IP 172.18.0.1`. Sign-in also breaks, because the app thinks requests are plain HTTP.

Once the proxy is up, check from another machine:

```sh
curl https://board.example.com/healthz
```

## 6. Set up the Raspberry Pi

These steps use **Raspberry Pi OS (64-bit) with desktop** on a Pi 4 or 5, which runs the Wayland desktop with the `labwc` compositor by default.

### 6.1 Flash and first boot

1. In **Raspberry Pi Imager**, choose your Pi model, **Raspberry Pi OS (64-bit)** (the full desktop image, not Lite), and the SD card.
2. Click **Edit settings** before writing and set: a host name (e.g. `signage-pi`), a user and password, Wi-Fi if you're not using Ethernet, your **locale and time zone**, and **Enable SSH** (Services tab).
3. Boot the Pi connected to the TV, then SSH in and update:

   ```sh
   sudo apt update && sudo apt full-upgrade -y
   ```

### 6.2 Make it boot straight to a desktop that never sleeps

```sh
sudo raspi-config
```

- **System Options → Boot / Auto Login → Desktop Autologin**.
- **Display Options → Screen Blanking → No**.
- **Advanced Options → Wayland → labwc**, if it isn't already selected.

Then make sure the clock is synced:

```sh
timedatectl
```

`System clock synchronized: yes` is what you want. The Pi's clock matters: TLS certificate checks depend on it, and the display hides expired notes using the Pi's own clock between polls. The on-screen clock and the "sent" times on notes are always shown in US Eastern time, whatever the Pi's time zone.

### 6.3 Check the Pi can reach the board

From the Pi:

```sh
curl https://board.example.com/healthz
curl -4 https://ifconfig.me
hostname -I
```

The first must print `Healthy` (if it fails, fix DNS, TLS trust or firewalling first). The other two print the public and LAN addresses; the one that matches your layout must be in `Board__Display__AllowedIps__*` on the server.

### 6.4 Create the kiosk launcher

Create `~/kiosk.sh`, replacing the host name and key (the **key** from step 2, not the hash):

```sh
#!/bin/sh
BOARD="https://board.example.com"
KEY="PASTE_DISPLAY_KEY_HERE"

# Wait for the network and the server before opening the browser.
until curl -sf -o /dev/null "$BOARD/healthz"; do sleep 5; done

# Stop Chromium offering to "restore pages" after a power cut.
PREFS="$HOME/.config/chromium/Default/Preferences"
[ -f "$PREFS" ] && sed -i 's/"exited_cleanly":false/"exited_cleanly":true/; s/"exit_type":"[^"]*"/"exit_type":"Normal"/' "$PREFS"

CHROMIUM=$(command -v chromium || command -v chromium-browser)
exec "$CHROMIUM" \
  --kiosk \
  --noerrdialogs \
  --disable-infobars \
  --disable-session-crashed-bubble \
  --disable-features=Translate \
  --check-for-update-interval=31536000 \
  --password-store=basic \
  "$BOARD/display/?key=$KEY"
```

```sh
chmod 700 ~/kiosk.sh
```

The display page stores the key in the browser's local storage and immediately removes it from the address bar. Keeping it in the launcher means the Pi recovers on its own even if Chromium's profile is wiped. Anyone with access to the Pi's SD card can read the key, so treat the Pi as a trusted device and keep it physically secured.

Optional URL parameters: `?poll=N` sets the poll interval in seconds (1–300, default 2), e.g. `"$BOARD/display/?poll=5&key=$KEY"`.

### 6.5 Start it at login

Add the launcher to labwc's autostart:

```sh
mkdir -p ~/.config/labwc
echo "$HOME/kiosk.sh &" >> ~/.config/labwc/autostart
sudo reboot
```

After the reboot the Pi logs in, waits until the board is reachable, and opens it full screen. The mouse cursor is hidden by the page itself. To get out of kiosk mode with a keyboard attached, press `Alt+F4`.

If your Pi is on an older image using **Wayfire** instead, add to `~/.config/wayfire.ini`:

```ini
[autostart]
kiosk = /home/<user>/kiosk.sh
```

### 6.6 Keeping it running

- The display page reloads itself every day between 4:00 and 4:05 AM Eastern, which clears any memory creep and picks up new versions of the page.
- If the server is unreachable, the board keeps showing the last notes it had and prints `OFFLINE` in red in the corner. `ACCESS DENIED` means the server rejected the Pi (wrong IP or key); `NO DISPLAY KEY` means the URL had no key and none was stored.
- For an extra safety net, reboot the Pi weekly: `sudo crontab -e` and add `30 4 * * 0 /sbin/reboot`.
- Use the TV's own settings to turn off its auto-standby / eco power-off, or it may switch itself off after a few hours with no remote input.

## 7. Check it end to end

1. Open `https://board.<domain>/admin` on your phone and sign in as a member of the editor group.
2. Write a note and click **Publish**.
3. Within a couple of seconds the Pi's TV shows it, with your first name and the time under it.
4. Unpublish it and confirm it disappears from the TV.

## Operations

**Backups.** SQLite's `VACUUM INTO` writes a consistent copy even while the app is running. The volume is named `<project>_signageboard-data`, where the project is the folder name in lowercase; check with `docker volume ls`.

```sh
mkdir -p backups
docker run --rm \
  -v signageboard_signageboard-data:/data \
  -v "$PWD/backups:/backup" \
  alpine:3 sh -c "apk add --no-cache sqlite >/dev/null && sqlite3 /data/noticeboard.db \"VACUUM INTO '/backup/noticeboard.db'\" && chown -R 1654:1654 /data"
mv backups/noticeboard.db "backups/noticeboard-$(date +%F-%H%M).db"
```

The `chown` keeps every file in the volume owned by the app's non-root user (UID 1654). Then copy the backup off the host, for example with `rsync -a backups/ backup-host:/srv/backups/signageboard/`. A backup that only lives on the same disk isn't one.

**Restoring.** Stop the app, replace the database, and start it again. This uses the app's own image, so nothing extra is pulled:

```sh
docker compose stop
docker run --rm --user 0 --entrypoint sh \
  -v signageboard_signageboard-data:/data \
  -v "$PWD/backups:/backup" \
  signageboard:latest -c "rm -f /data/noticeboard.db-wal /data/noticeboard.db-shm && cp /backup/noticeboard-YYYY-MM-DD-HHMM.db /data/noticeboard.db && chown -R 1654:1654 /data"
docker compose start
```

**Updating.**

```sh
git pull
docker compose up -d --build
```

Database changes for new versions are applied automatically on startup. The Pi picks up the new display page at its next 4 AM reload; to get it straight away, reboot the Pi (`ssh signage-pi sudo reboot`).

**Rotating the display key.**
1. Generate a new key and hash (step 2).
2. Replace `Board__Display__KeyHash` in the server's `.env` and run `docker compose up -d` to recreate the container.
3. On the Pi, replace `KEY=` in `~/kiosk.sh` and reboot.

The old key stops working as soon as the container restarts.

**Adding another display.** Set up another Pi the same way with the same key, and add its address as the next `Board__Display__AllowedIps__N` (skip this if it shares a public IP already listed in layout A). Then `docker compose up -d`.

**Data Protection keys** live in `/data/keys` on the volume. ASP.NET Core uses them to encrypt sign-in cookies and antiforgery tokens. If they're lost (for example the volume is deleted, or `DataProtection__KeysPath` points outside `/data`), every editor is signed out and open admin pages fail their next form post. Never run `docker compose down -v` unless you mean to wipe the board. The keys are stored unencrypted at rest, and the app logs a warning saying so on first start; protect the volume and its backups accordingly.

## Local development

Keep tenant-specific values out of the repository by using user secrets:

```sh
cd SignageBoard.Web
dotnet user-secrets init
dotnet user-secrets set "AzureAd:TenantId" "<directory-tenant-id>"
dotnet user-secrets set "AzureAd:ClientId" "<application-client-id>"
dotnet user-secrets set "Board:EditorGroupId" "<editor-group-object-id>"
dotnet user-secrets set "Board:Display:KeyHash" "<uppercase-hex-hash>"
```

`appsettings.Development.json` (git-ignored, and excluded from the Docker image) holds non-secret local overrides:
- `AllowedHosts` set to `localhost` (add your machine's LAN IP to test from a Pi on your network).
- `Board:Display:AllowedIps` set to `::1` and `127.0.0.1` (plus the Pi's LAN IP if you're testing with one).
- A local SQLite file, `Data Source=noticeboard.db`.
- Data Protection keys in `App_Data/keys`.

User secrets load after `appsettings.Development.json` and override it.

Run with the https profile:

```sh
dotnet run --launch-profile https
```

The `https` profile also listens on `http://0.0.0.0:5229`, so a Pi on the same network can open `http://<your-pc-ip>:5229/display/?key=<KEY>` for testing. Swagger UI is at `https://localhost:7095/swagger` and the OpenAPI document at `/openapi/v1.json`, in Development only.

## Troubleshooting

**Server / sign-in**

- **`IDX20803: Unable to obtain configuration from …/.well-known/openid-configuration`.** The app couldn't download the tenant's sign-in metadata. Usually `AzureAd__TenantId` is wrong, or TenantId and ClientId are swapped. It also happens if the server can't make outbound HTTPS requests to `login.microsoftonline.com`.
- **`Bad Request - Invalid Hostname`.** The request's host isn't in `AllowedHosts`. Add the host name, use it (not an IP) in the Pi's URL, and make sure the proxy forwards the original `Host` header.
- **403 or "access denied" on `/admin` after signing in.** The user isn't in the editor group, `Board__EditorGroupId` isn't that group's Object ID, or the app registration doesn't emit the `groups` claim.
- **`AADSTS50011`.** The redirect URI the app sent isn't registered. Add `https://<host>/signin-oidc` exactly. If the error shows an `http://` redirect URI, the forwarded headers aren't trusted (see step 5).
- **`AADSTS50105`.** The user isn't assigned to the Enterprise application.
- **`AADSTS700054`.** ID tokens aren't enabled on the app registration's Authentication page.
- **Editors are signed out after every restart.** The Data Protection keys aren't persisting. Check that `DataProtection__KeysPath` is under `/data` and that `/data/keys` contains `key-*.xml` files that survive a restart.
- **Notes show a time but no name.** The account had neither a `given_name` nor a usable `name` claim when the note was created; add the optional claim (step 1.6). Notes created before the name feature existed also have no name.

**Raspberry Pi / display**

- **`ACCESS DENIED` on the TV, log says `Display request rejected: IP <address>`.** The server saw the Pi as `<address>`, which isn't in `AllowedIps`.
  - If `<address>` is a `172.x.x.x` Docker address, the proxy's headers aren't trusted; fix `ForwardedHeaders__*` (step 5).
  - If it's the Pi's real address, add it to `Board__Display__AllowedIps__*` and run `docker compose up -d`.
  - **IPv6:** if `<address>` is an IPv6 address, the Pi connected over IPv6. Either add that address, or (more robust, since IPv6 addresses often change) give `board.<domain>` only an A record and no AAAA record so the Pi always uses IPv4.
  - Layout A with a **dynamic** office IP: the allowlist breaks whenever the ISP changes it. Get a static IP, or use layout B.
- **`ACCESS DENIED`, log says `Display request rejected: bad key`.** The IP was accepted but the key doesn't match the hash. Check you put the key (not the hash) in `kiosk.sh`, and the hash (not the key) in `.env`.
- **`ACCESS DENIED` and nothing in the log.** The request had no key at all; check the `?key=` part of the URL in `kiosk.sh`.
- **`OFFLINE`.** The Pi can't reach `/api/board`. From the Pi, run `curl https://board.<domain>/healthz`; fix DNS, certificate trust or the network.
- **Black screen after boot.** The launcher is still waiting for `/healthz` to answer. SSH in and run the same `curl` to see why.
- **Chromium shows a certificate error.** Layout B with an internal CA: install the CA on the Pi (step 5). Or check the Pi's clock with `timedatectl`.
- **Screen goes blank after a while.** Turn off Screen Blanking in `raspi-config`, and the TV's own auto-standby.
- **Board is cropped or tiny.** Set the Pi's output to 1920×1080 in **Preferences → Screen Configuration**, and turn off overscan/"zoom" in the TV's picture settings (often called *Just Scan*, *Screen Fit* or *1:1*).

**Schema changes.** On startup the app creates missing tables and adds missing columns it knows about. Any other schema change needs the database recreated (delete `noticeboard.db*` from the volume; this loses all notes) or a manual migration with `sqlite3`.

## Appendix: Fire TV instead of a Pi

The board also runs on a Fire TV stick using Fully Kiosk Browser:

1. **Allow sideloading.** **Settings → My Fire TV → About**, select the device name seven times to unlock **Developer Options**, then allow **Install unknown apps** for **Downloader**.
2. **Install Fully Kiosk Browser** via the **Downloader** app (by AFTVnews), using the APK link from fully-kiosk.com.
3. **Start URL:** `https://board.<domain>/display/?key=<KEY>`.
4. **Lock it down:** enable **Launch on Boot** and **Keep Screen On**; set the **URL Whitelist** to `https://board.<domain>/*`; enable **Kiosk Mode** with a PIN (may need a Fully PLUS license); turn off **Remote Administration** or give it a strong password.
5. **Screensaver:** **Settings → Display & Sounds → Screensaver → Start Time → Never**.

The `AllowedIps` rules are the same as for the Pi.
