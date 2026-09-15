# Fiber Img App — Setup checklist

Manual steps for the **dev PC** and **operator PC**. Complete phases in order.

---

## Phase 0 — Accounts and folders

1. Confirm your GitHub account can create a **public** repository.
2. Work in `c:\dev\myapp` (this repo).
3. On the operator PC you will later allow MSIX sideloading (Phase 7).

---

## Phase 1 — Install tooling (dev PC)

### 1.1 Visual Studio 2026 workloads

1. Open **Visual Studio Installer** → **Modify** on VS 2026.
2. Enable workloads:
   - **.NET desktop development**
   - **Windows application development** (WinUI / Windows App SDK)
3. Under Individual components, ensure these are checked if listed:
   - **.NET 10.0 SDK**
   - **Windows App SDK C# Templates**
   - **MSIX Packaging Tools** / Windows 11 SDK (10.0.26100 or newer)
4. Apply changes and reboot if prompted.

### 1.2 Verify .NET 10

```powershell
dotnet --list-sdks
```

Expect a `10.x` entry.

### 1.3 WinUI `dotnet new` templates (optional CLI)

```powershell
dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates
dotnet new list winui
```

### 1.4 Git + GitHub CLI

```powershell
git --version
gh auth login
```

### 1.5 Developer Mode (dev PC)

Settings → **System** → **For developers** → turn on **Developer Mode** (helps sideload/debug packaged apps).

---

## Phase 2 — Create the public GitHub repo

1. Create a **public** repo named `ttu-fiber-img-app` (empty is fine if pushing this folder).
2. From `c:\dev\myapp`:

```powershell
git init
git add .
git commit -m "Initial Fiber Img App skeleton"
git branch -M main
git remote add origin https://github.com/Zaied-bayati/ttu-fiber-img-app.git
git push -u origin main
```

3. App Installer URIs already use `Zaied-bayati/ttu-fiber-img-app`. Optional: set repo **variable** `APPINSTALLER_BASE_URL` to  
   `https://github.com/Zaied-bayati/ttu-fiber-img-app/releases/latest/download/`

   If `origin` already exists with a wrong URL:
   `git remote set-url origin https://github.com/Zaied-bayati/ttu-fiber-img-app.git`

4. After Phase 3, add Actions **secrets**:
   - `MSIX_CERT_BASE64`
   - `MSIX_CERT_PASSWORD`

---

## Phase 3 — Self-signed certificate (both PCs)

Publisher identity is **`CN=TTU`** (must match `Package.appxmanifest`).

### 3.1 Create cert (dev PC, elevated PowerShell)

```powershell
cd c:\dev\myapp
.\packaging\scripts\New-SelfSignedMsixCert.ps1
```

This writes (gitignored):

- `packaging/certs/FiberImgApp.pfx` — signing private key
- `packaging/certs/FiberImgApp.cer` — public trust cert

Copy the printed Base64 into GitHub secret `MSIX_CERT_BASE64`. Put the PFX password in `MSIX_CERT_PASSWORD`.

### 3.2 Trust on dev PC

```powershell
.\packaging\scripts\Import-MsixTrustCert.ps1 -CerPath .\packaging\certs\FiberImgApp.cer
```

### 3.3 Trust on operator PC

Copy `FiberImgApp.cer` to the operator machine and run the same import script (elevated), or double-click the `.cer` → Install → **Local Machine** → **Trusted People**.

If install still fails with a publisher trust error, also place it in **Trusted Root Certification Authorities** (lab machines only).

---

## Phase 4 — Open and run the solution

1. Open `Ttu.FiberImgApp.sln` in Visual Studio 2026.
2. Set startup project to **Ttu.FiberImgApp**, platform **x64**.
3. Build and run (F5). You should see the skeleton shell with ZEN/Thorlabs stub status.
4. Optional local secrets:

```powershell
copy src\Ttu.FiberImgApp\appsettings.Local.json.example src\Ttu.FiberImgApp\appsettings.Local.json
# edit tokens — file is gitignored
```

Or place `%LocalAppData%\TTU\FiberImgApp\appsettings.Local.json`.

5. SQLite DB default path: `%LocalAppData%\TTU\FiberImgApp\fiberimg.db`  
   Logs: `%LocalAppData%\TTU\FiberImgApp\logs\`

### Package identity (already set)

| Field | Value |
| --- | --- |
| Name | `TTU.FiberImgApp` |
| Publisher | `CN=TTU` |
| Display name | Fiber Img App |
| Version | `0.1.0.0` (bump for each release) |

### Create a local MSIX (VS)

1. Right-click **Ttu.FiberImgApp** → **Package and Publish** → **Create App Packages…**
2. **Sideloading** + enable **automatic updates**
3. Installation URL: your Releases download URL (trailing slash)
4. Select the `.pfx` from `packaging/certs/`
5. After build, test install from the generated `.appinstaller`

---

## Phase 5 — Local update smoke test

1. Install the package once via `.appinstaller`.
2. Bump `Package.appxmanifest` Version (e.g. `0.1.1.0`).
3. Create packages again to the same Installation URL (or a local HTTPS/file share URL used in both App Installer files).
4. Launch the app and confirm App Installer detects the newer version.

---

## Phase 6 — GitHub Actions release

Workflow: `.github/workflows/release.yml`

1. Ensure secrets `MSIX_CERT_BASE64` and `MSIX_CERT_PASSWORD` exist.
2. Tag and push:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Or run **Release MSIX** via Actions → **workflow_dispatch** and enter `0.1.0.0`.

3. When the job finishes, open the repo **Releases** page. You should see:
   - `Ttu.FiberImgApp.msix`
   - `FiberImgApp.appinstaller`
   - `FiberImgApp.cer` (if present under `packaging/certs` in the repo — normally you attach the `.cer` manually or copy it into the workflow artifacts from a secure store; do not commit the `.pfx`)

> Tip: upload `FiberImgApp.cer` once as a Release asset or share it out-of-band with the operator. Prefer **not** committing `.pfx`.

4. Operator forever-after flow: you tag → Actions publishes → their app checks for updates on launch.

---

## Phase 7 — Operator PC

1. **Windows 11** → Settings → System → For developers (or Apps) → allow sideloading / install apps from any source as required by your edition.
2. Install **`FiberImgApp.cer`** into **Trusted People** (and Root if needed) using `Import-MsixTrustCert.ps1` or Certificate Manager.
3. Open the Release **`FiberImgApp.appinstaller`** URL in the browser (do not only copy a lone `.msix` if you want auto-updates). Example:

   `https://github.com/Zaied-bayati/ttu-fiber-img-app/releases/latest/download/FiberImgApp.appinstaller`

4. Complete App Installer UI → launch **Fiber Img App**.
5. After the next tagged release, relaunch the app and confirm the update prompt/apply.

### Operator update troubleshooting

| Symptom | Check |
| --- | --- |
| Publisher not trusted | `.cer` not imported / CN mismatch vs `CN=TTU` |
| No update offered | App was installed from raw `.msix` without App Installer association |
| 404 on update | Wrong `AppInstallerUri` or asset names don’t match template |
| Private network block | GitHub Releases must be reachable from the operator PC |

---

## Phase 8 — ZEN / Thorlabs readiness

### Thorlabs XA (BSC202 + NRT100)

1. Install **Thorlabs XA** from Thorlabs Motion Control downloads (includes USB drivers + SDK).
2. You do **not** need the XA GUI open while Fiber Img App runs — close XA/Kinesis first so the USB device is free.
3. In the app **Settings**:
   - Leave **Use simulator** on until XA is installed and verified.
   - Set channel **1** (default) for the NRT100 on BSC202.
   - Travel defaults **0–100 mm** (NRT100). Adjust min/max if needed.
   - Set velocity / acceleration, then **Save settings**.
4. **Connect** in Settings or Capture, then use manual jog / Begin Sampling.
5. Real XA binding: `XaThorlabsClient` loads SDK DLLs from `C:\Program Files\Thorlabs XA` (and a few fallback folders) when `UseSimulator` is false. If connect fails, check serial/channel and finish SDK wiring using Thorlabs XA examples on the lab PC.

### Zeiss ZEN API (Axiocam 820 mono)

Prerequisites: **ZEN Blue or ZEN core 3.11+**, **ZEN API Gateway** (install via ZEISS Microscopy Installer / ZMI), and a control token. The Axiocam 820 is selected inside a ZEN experiment — this app talks to the Gateway over gRPC (TLS), not USB directly.

1. Install ZEN (3.11+) and the **ZEN API Gateway**. Optionally install the ZEN API proto files (`C:\Users\Public\Documents\Carl Zeiss\ZEN API Proto Files`) for reference.
2. In ZEN, create and save a live/snap experiment that uses the **Axiocam 820 mono**. Note the experiment name **without** the `.czexp` extension.
3. Create an API **control token** in the Gateway / ZEN API tooling.
4. Trust the Gateway TLS certificate on this PC (or set `Zen:CertificatePath` to the CA `.pem`/`.crt`, or temporarily `Zen:AllowUntrustedCertificate` for lab bring-up only).
5. In Fiber Img App **Settings → Zeiss ZEN / Axiocam**:
   - Turn **off** "Use ZEN simulator".
   - Set host (usually `localhost`), port (default `50051`), experiment name, and paste the control token.
   - **Save settings**, then **restart the app** (DI picks simulator vs real client at startup).
6. On **Capture**: Connect camera → Start live. Double-click the preview (or Fullscreen live) for a zoomable viewer (scroll wheel / Zoom ± / Esc).
7. Sampling stills use the **latest live frame** encoded as PNG (no CZI dependency in this version).
8. If live fails with a busy / conflict error, stop Live in the ZEN UI so the Gateway can start live from this app.

Offline: leave **Use ZEN simulator** on — Capture shows a synthetic gray live stream for UI testing without Gateway.

---

## Architecture notes

- **Public repo** hosts source + Releases (App Installer can download without auth).
- Never commit secrets; use `appsettings.Local.json` (gitignored) or `%LocalAppData%\TTU\FiberImgApp\appsettings.Local.json`.
- Default saved images: `Documents\TTU\FiberImgApp\Sessions\yyyy-MM-dd_HH-mm-ss\`.
- Simulator mode lets you exercise Capture → Sample → Review without hardware.
