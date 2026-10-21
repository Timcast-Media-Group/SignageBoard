# SignageBoard

## What it is

SignageBoard is a notice board for a TV. Editors sign in to a mobile-friendly admin page at `/admin` to write notes, set their priority and expiry, publish or unpublish them, choose the board's text size and preview exactly what the TV will show. The TV opens a static page at `/display/` that polls a small live API (`GET /api/board`) every couple of seconds and redraws only when the board changes, using an ETag so unchanged polls cost almost nothing.

## Architecture

- **Admin:** Blazor Server (interactive server rendering) at `/admin`, protected by Microsoft Entra ID sign-in. Only members of one Entra security group (the `Editors` policy) can make changes.
- **Display:** a static HTML/JS page at `/display/` that polls `GET /api/board`. That endpoint is anonymous but gated twice: the client IP must be in an allowlist, and the request must carry a display key whose SHA-256 hash is configured on the server.
- **Storage:** SQLite at `/data/noticeboard.db` inside the container, on a named Docker volume. ASP.NET Core Data Protection keys live next to it in `/data/keys`.
- **TLS:** terminated at a reverse proxy on the host (Caddy, nginx, etc.). The container only listens on plain HTTP port 8080, published on `127.0.0.1` so it is never reachable directly from the network.

## Prerequisites

- A Linux host with Docker Engine and the Docker Compose plugin (`docker compose version`).
- A domain name you control, with a DNS record such as `board.<domain>` pointing at the host.
- A reverse proxy on the host that can obtain TLS certificates (examples below for Caddy and nginx).
- A Microsoft Entra ID tenant where you can create app registrations and security groups.

## Entra ID setup

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
6. **Create the editor group.** Go to **Identity → Groups → All groups → New group**. Type **Security**, name it something like `SignageBoard Editors`, and add the people who should be able to edit. Open the group and copy its **Object ID**; that is `Board__EditorGroupId`.
7. **Require assignment.** Go to **Identity → Applications → Enterprise applications**, open `SignageBoard`, then **Properties**, set **Assignment required?** to **Yes**, and save. Under **Users and groups**, add the editor group (assigning a group needs Entra ID P1 or higher; on the free tier, add the users individually). Anyone not assigned is stopped at Microsoft's sign-in page with `AADSTS50105`.
8. **Require MFA.** Either create a **Conditional Access** policy (Entra ID P1) that targets the `SignageBoard` app and requires multifactor authentication, or turn on **Security defaults** for the tenant (**Identity → Overview → Properties → Manage security defaults**).
9. **Copy the two IDs.** On the app registration's **Overview** page:
   - **Directory (tenant) ID** goes into `AzureAd__TenantId`.
   - **Application (client) ID** goes into `AzureAd__ClientId`.

   They look alike. Do not swap them: a swapped pair makes the app request metadata for a tenant that doesn't exist, and every sign-in fails with `IDX20803`.

## Generating the display key

The TV proves itself with a random key, sent in the `X-Display-Key` header. The server stores only the **uppercase hex SHA-256 hash of the key's UTF-8 bytes** (`Board__Display__KeyHash`) and compares hashes in constant time. Each recipe below prints a URL-safe base64 key (43 characters, safe to put in a URL unencoded) and its matching hash. Put the hash in `.env` and the key in the TV's start URL. Never put the key itself in any config file.

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

**PowerShell 7**

```powershell
$key = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($key)))
"Key:  $key"
"Hash: $hash"
```

**Linux / macOS (openssl)**

```sh
KEY=$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=\n')
HASH=$(printf '%s' "$KEY" | openssl dgst -sha256 | awk '{print $NF}' | tr 'a-f' 'A-F')
echo "Key:  $KEY"
echo "Hash: $HASH"
```

## Configuration

Copy the example file and fill it in. `.env` is git-ignored; never commit it.

```sh
cp .env.example .env
```

Settings use ASP.NET Core's environment variable naming: a double underscore (`__`) separates sections, so `Board__Display__KeyHash` is the setting `Board:Display:KeyHash`. **Arrays use numeric indexes:** `Board__Display__AllowedIps__0`, `Board__Display__AllowedIps__1`, and so on. `appsettings.json` ships placeholder values at index 0 of `AllowedIps` and `KnownProxies`, so always set `__0` for both rather than starting at `__1`.

| Variable | Example | Description |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Must be `Production` on a server. `docker-compose.yml` also sets it. |
| `AllowedHosts` | `board.example.com` | Host names the app answers to. Requests for any other host get `400 Bad Request - Invalid Hostname`. Separate several with `;`. |
| `AzureAd__Instance` | `https://login.microsoftonline.com/` | Entra sign-in authority. Keep the trailing slash. |
| `AzureAd__TenantId` | `00000000-…` | Directory (tenant) ID from the app registration's Overview page. |
| `AzureAd__ClientId` | `00000000-…` | Application (client) ID from the same page. |
| `AzureAd__CallbackPath` | `/signin-oidc` | Where Entra posts the ID token. Must match a registered redirect URI. |
| `AzureAd__SignedOutCallbackPath` | `/signout-callback-oidc` | Where Entra returns after sign-out. Must match a registered redirect URI. |
| `Board__EditorGroupId` | `00000000-…` | Object ID of the editor security group. Only members can use `/admin` and the write API. |
| `Board__Display__KeyHash` | 64 hex characters | Uppercase hex SHA-256 of the display key (see above). |
| `Board__Display__AllowedIps__0` | `203.0.113.10` | IP addresses allowed to fetch the board, as the app sees them after forwarded headers: normally the public IP of the network the TV is on. Add `__1`, `__2` for more. If no addresses are configured at all, the IP check is skipped and only the key protects the board. |
| `Board__Display__PollSeconds` | `10` | Validated setting (2–300). The display page currently uses its own interval (2 seconds, or `?poll=N` in its URL). |
| `Board__MaxNoteLength` | `280` | Maximum characters per note (1–2000). |
| `Board__MaxNotes` | `12` | Maximum number of published notes at once (1–100). |
| `ConnectionStrings__Board` | `Data Source=/data/noticeboard.db` | SQLite file. Keep it under `/data` so it lives on the volume. |
| `DataProtection__KeysPath` | `/data/keys` | Where sign-in and antiforgery encryption keys are stored. Keep it under `/data`. |
| `ForwardedHeaders__KnownProxies__0` | `172.17.0.1` | A single proxy address whose `X-Forwarded-For` / `X-Forwarded-Proto` headers are trusted. |
| `ForwardedHeaders__KnownNetworks__0` | `172.16.0.0/12` | A CIDR range of trusted proxies. `172.16.0.0/12` covers Docker's default bridge networks. |
| `Logging__LogLevel__Default` | `Information` | Default log level. |
| `Logging__LogLevel__Microsoft.AspNetCore` | `Warning` | Log level for ASP.NET Core's own messages. |

## Running

Build and start in the background:

```sh
docker compose up -d --build
```

Follow the logs:

```sh
docker compose logs -f signageboard
```

Check health. The app only answers to the host names in `AllowedHosts`, so either go through the proxy or send the Host header yourself:

```sh
curl https://board.<domain>/healthz
curl -H "Host: board.<domain>" http://127.0.0.1:8080/healthz
```

Both should print `Healthy`. A bare `curl http://127.0.0.1:8080/healthz` returns `400 Bad Request - Invalid Hostname`, which is expected.

## Reverse proxy

The proxy must pass the original `Host` header, set `X-Forwarded-For` and `X-Forwarded-Proto`, and allow WebSocket upgrades (Blazor Server's admin page runs over a WebSocket).

**Caddy** (sets `X-Forwarded-For`, `X-Forwarded-Proto` and `Host`, and handles WebSockets and certificates automatically):

```
board.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

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

**Trusted proxy addresses matter.** The app only believes `X-Forwarded-*` headers from addresses listed in `KnownProxies` or `KnownNetworks`, and those must match the proxy's address *as seen from inside the container*. That is not `127.0.0.1`: traffic to the published port arrives from the Compose network's gateway, which is usually something like `172.18.0.1`. Docker picks a different subnet per Compose project, which is why `KnownNetworks=172.16.0.0/12` is the practical setting. To see the exact address, run:

```sh
docker network inspect signageboard_default -f '{{(index .IPAM.Config 0).Gateway}}'
```

If this doesn't match, the app sees every TV request as coming from the gateway address, the IP allowlist rejects it, and the log shows `Display request rejected: IP 172.18.0.1`. Sign-in also breaks, because the app thinks requests are plain HTTP.

## Fire TV setup

1. **Allow sideloading.** On the Fire TV, open **Settings → My Fire TV → About** and select the device name seven times to unlock **Developer Options**. Then, in **Developer Options**, allow **Install unknown apps** for **Downloader**.
2. **Install Fully Kiosk Browser.** Install **Downloader** (by AFTVnews) from the Amazon Appstore. In it, enter the Fully Kiosk Browser APK download address from fully-kiosk.com, install the APK, then delete the APK when Downloader offers to.
3. **Set the start URL.** In Fully Kiosk's settings, under **Web Content Settings → Start URL**, enter `https://board.<domain>/display/?key=<KEY>`. The page saves the key in its local storage and removes it from the address bar, so later reloads don't need it.
4. **Lock it down.** In Fully Kiosk:
   - Enable **Device Management → Launch on Boot** and **Keep Screen On**.
   - Under **Web Content Settings → URL Whitelist**, allow only `https://board.<domain>/*`.
   - Enable **Kiosk Mode** with a PIN, so nobody can leave the app or open settings without it. Kiosk Mode and some protection features may require a Fully PLUS license.
   - Under **Remote Administration**, either turn it off or set a strong password.
5. **Stop the screensaver.** In the Fire TV's own **Settings → Display & Sounds → Screensaver**, set **Start Time** to **Never**.
6. **Isolate the TV.** Put it on its own network or VLAN (a guest or IoT network is fine) that can only reach the internet, not your other devices. The public IP that network uses is what goes in `Board__Display__AllowedIps__0`.

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

The `chown` keeps every file in the volume owned by the app's non-root user (UID 1654). Then copy the backup off the host, for example `rsync -a backups/ backup-host:/srv/backups/signageboard/` or `scp`. A backup that only lives on the same disk isn't one.

**Restoring.** Stop the app, replace the database, and start it again. This uses the app's own image, so nothing extra is pulled:

```sh
docker compose stop
docker run --rm --user 0 --entrypoint sh \
  -v signageboard_signageboard-data:/data \
  -v "$PWD/backups:/backup" \
  signageboard:latest -c "rm -f /data/noticeboard.db-wal /data/noticeboard.db-shm && cp /backup/noticeboard-YYYY-MM-DD-HHMM.db /data/noticeboard.db && chown -R 1654:1654 /data"
docker compose start
```

**Rotating the display key.**
1. Generate a new key and hash (see above).
2. Replace `Board__Display__KeyHash` in `.env` and run `docker compose up -d` to recreate the container with the new value.
3. On the TV, open the start URL once with the new key, `https://board.<domain>/display/?key=<NEW_KEY>`; it replaces the stored key.

The old key stops working as soon as the container restarts.

**Updating.**

```sh
git pull
docker compose up -d --build
```

**Data Protection keys** live in `/data/keys` on the volume. ASP.NET Core uses them to encrypt sign-in cookies and antiforgery tokens. If they're lost (for example the volume is deleted, or `DataProtection__KeysPath` points outside `/data`), every user is signed out and open admin pages fail their next form post. That's why the volume must persist across `docker compose down` and rebuilds; never run `docker compose down -v` unless you mean to wipe the board. The keys are stored unencrypted at rest, and the app logs a warning saying so on first start. Protect the volume and its backups accordingly.

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

User secrets load after `appsettings.Development.json` in Development and override it. `appsettings.Development.json` holds the non-secret local overrides:
- `AllowedHosts` set to `localhost`.
- `Board:Display:AllowedIps` set to the loopback addresses `::1` and `127.0.0.1`.
- A local SQLite file, `Data Source=noticeboard.db`.
- Data Protection keys in `App_Data/keys` (git-ignored).

Run with the https profile:

```sh
dotnet run --launch-profile https
```

Swagger UI is available at `https://localhost:7095/swagger`, and the OpenAPI document at `/openapi/v1.json`, in Development only. Neither is mapped in Production. Unhandled exceptions show the developer exception page in Development and the generic `/Error` page in Production.

## Troubleshooting

- **`IDX20803: Unable to obtain configuration from …/.well-known/openid-configuration`.** The app couldn't download the tenant's sign-in metadata. Usually `AzureAd__TenantId` is wrong, or the TenantId and ClientId are swapped. It also happens if the server can't make outbound HTTPS requests to `login.microsoftonline.com`.
- **`Bad Request - Invalid Hostname`.** The request's host isn't in `AllowedHosts`. Add the public host name, and make sure the proxy forwards the original `Host` header.
- **The display shows ACCESS DENIED, and the log says `Display request rejected: IP ::1` or the proxy's IP.**
  - In Development, `::1` must be in `AllowedIps`.
  - In Production, the forwarded headers aren't trusted. Fix `ForwardedHeaders__KnownProxies__0` / `KnownNetworks__0` so they include the proxy's address as seen by the container (see Reverse proxy). Then make sure `AllowedIps` holds the TV network's real public IP.
  - `Display request rejected: bad key` means the IP was accepted but the key doesn't match the configured hash.
- **403 or "access denied" on `/admin` after signing in.** The user isn't in the editor group, `Board__EditorGroupId` isn't that group's Object ID, or the app registration doesn't emit the `groups` claim (Token configuration, Security groups, Group ID).
- **`AADSTS50011`.** The redirect URI the app sent isn't registered. Add `https://<host>/signin-oidc` exactly, including the scheme. If the error shows an `http://` redirect URI, the forwarded headers aren't trusted.
- **`AADSTS50105`.** The user isn't assigned to the Enterprise application. Add them, or their group, under Users and groups.
- **`AADSTS700054`.** ID tokens aren't enabled on the app registration's Authentication page.
- **Users are signed out after every restart.** The Data Protection keys aren't persisting. Check that `DataProtection__KeysPath` is under `/data`, that the volume is mounted, and that `/data/keys` contains `key-*.xml` files that survive a restart.
- **Schema changes.** On startup the app creates missing tables and adds missing `BoardState` columns automatically. Any other schema change needs the database recreated (delete `noticeboard.db*` from the volume; this loses all notes) or a manual migration with `sqlite3`.
