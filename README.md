# LAN File Sharing GUI — Windows + IIS + ASP.NET Core

## 1. Overview

This guide documents the complete setup for a browser-based file-sharing application on a Windows PC for use over a local network.

### Features

- Browser-based file browsing
- File downloads
- File uploads
- Uploads restricted to `H:\FTP\Uploads`
- Read-only shared folders
- Per-file upload progress
- Upload percentage, speed and ETA
- Maximum individual upload size: **3 GB**
- Shared upload bandwidth target: **3 MB/s total**
- Shared download bandwidth target: **3 MB/s total**
- IIS hosting
- Optional IIS FTP access for clients such as WinSCP

### Example configuration

```text
Server IP:          192.168.10.50
Web GUI:            http://192.168.10.50:666
Development port:   5042
FTP port:           21

Storage root:       H:\FTP
Upload folder:      H:\FTP\Uploads

Project:            C:\LANFileServer\LanShareApp
Publish folder:     C:\LANFileServer\publish

Upload limit:       3 GB per file
Upload bandwidth:   3 MB/s total
Download bandwidth: 3 MB/s total
```

---

# 2. Storage Structure

Create this structure:

```text
H:\FTP
├── Documents
├── Photos
├── Projects
├── Software
└── Uploads
```

The application can browse/download files from the shared folders. Uploads are written to `Uploads`.

---

# 3. How the Application Works

```text
LAN Client Browser
       │
       │ HTTP
       ▼
192.168.10.50:666
       │
       ▼
      IIS
       │
       ▼
ASP.NET Core LanShareApp
       │
       ▼
     H:\FTP
       │
 ┌─────┴───────────────┐
 │                     │
Read/download       Upload
 │                     │
All folders        Uploads only
```

The browser GUI is the normal interface. FTP is optional and separate.

---

# 4. Requirements

The server PC needs:

- Windows 10/11
- Stable LAN IP
- .NET SDK/runtime
- IIS
- ASP.NET Core Hosting Bundle
- Administrator access
- A storage location such as `H:\FTP`

---

# 5. Install and Verify .NET

Open PowerShell:

```powershell
dotnet --info
```

Check installed SDKs:

```powershell
dotnet --list-sdks
```

Check installed runtimes:

```powershell
dotnet --list-runtimes
```

The project was developed with modern ASP.NET Core/.NET.

---

# 6. Install IIS

Open:

```text
Control Panel
→ Programs
→ Turn Windows features on or off
```

Enable:

```text
Internet Information Services
```

Also enable the IIS Management Console.

Open IIS Manager with:

```powershell
inetmgr
```

---

# 7. Install the ASP.NET Core Hosting Bundle

Install the ASP.NET Core Hosting Bundle matching the .NET/ASP.NET Core version used by the application.

The Hosting Bundle provides the ASP.NET Core Module required for IIS hosting.

After installation:

```powershell
iisreset
```

A Windows restart may also be used if required.

---

# 8. Create the Storage Folders

Run:

```powershell
New-Item -ItemType Directory -Path "H:\FTP" -Force
New-Item -ItemType Directory -Path "H:\FTP\Documents" -Force
New-Item -ItemType Directory -Path "H:\FTP\Photos" -Force
New-Item -ItemType Directory -Path "H:\FTP\Projects" -Force
New-Item -ItemType Directory -Path "H:\FTP\Software" -Force
New-Item -ItemType Directory -Path "H:\FTP\Uploads" -Force
```

Put files that users should download inside:

```text
Documents
Photos
Projects
Software
```

---

# 9. Create the ASP.NET Core Project

Open PowerShell:

```powershell
mkdir C:\LANFileServer
cd C:\LANFileServer
dotnet new web -n LanShareApp
cd C:\LANFileServer\LanShareApp
```

The main application is:

```text
C:\LANFileServer\LanShareApp\Program.cs
```

---

# 10. Configure the Storage Paths

In `Program.cs`, configure:

```csharp
const string RootPath = @"H:\FTP";
const string UploadPath = @"H:\FTP\Uploads";
const string MetadataPath = @"H:\FTP\.metadata.json";
```

If your storage location is different, change these paths.

---

# 11. Configure the 3 GB Upload Limit

The application should have:

```csharp
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit =
        3L * 1024 * 1024 * 1024;
});
```

Also configure:

```csharp
const long MaxUploadFileSize =
    3L * 1024 * 1024 * 1024;
```

The server checks every uploaded file against this value.

The browser also performs a 3 GB check before uploading.

---

# 12. Configure Bandwidth Limits

Use:

```csharp
const long UploadLimitBytesPerSecond =
    3L * 1024 * 1024;

const long DownloadLimitBytesPerSecond =
    3L * 1024 * 1024;
```

These are **shared application limits**, not per-user limits.

For example:

```text
User A ─┐
User B ─┼──► approximately 3 MB/s total upload
User C ─┘
```

Downloads have a separate approximately 3 MB/s total pool.

Uploads and downloads can therefore run simultaneously.

---

# 13. Test Before IIS

From:

```text
C:\LANFileServer\LanShareApp
```

run:

```powershell
dotnet run
```

Test locally using the URL printed by .NET, normally similar to:

```text
http://localhost:5000
```

For LAN testing:

```powershell
dotnet run --urls "http://0.0.0.0:5042"
```

Then another LAN computer can open:

```text
http://192.168.10.50:5042
```

---

# 14. Windows Firewall

For the production IIS port:

```powershell
New-NetFirewallRule `
    -DisplayName "Example LAN Share 666" `
    -Direction Inbound `
    -Protocol TCP `
    -LocalPort 8080 `
    -Action Allow
```

For temporary development testing:

```powershell
New-NetFirewallRule `
    -DisplayName "Example LAN Share 5042" `
    -Direction Inbound `
    -Protocol TCP `
    -LocalPort 5000 `
    -Action Allow
```

Only expose these ports to the LAN as appropriate.

---

# 15. Publish the Application

From:

```text
C:\LANFileServer\LanShareApp
```

run:

```powershell
dotnet publish -c Release -o C:\LANFileServer\publish
```

The IIS application will use:

```text
C:\LANFileServer\publish
```

---

# 16. Configure IIS Application Pool

Open:

```text
IIS Manager
→ Application Pools
→ Add Application Pool
```

Example:

```text
Name: LanSharePool
```

Set:

```text
.NET CLR Version: No Managed Code
Managed pipeline: Integrated
```

Create the pool.

ASP.NET Core is hosted through the ASP.NET Core Module, so `No Managed Code` is appropriate.

---

# 17. Create the IIS Website

In IIS Manager:

```text
Sites
→ Add Website
```

Use:

```text
Site name:
Example LAN Share

Physical path:
C:\LANFileServer\publish

Application pool:
LanSharePool
```

Binding:

```text
Type:       http
IP address: All Unassigned
Port:       8080
Host name:  leave empty
```

Click **OK**.

---

# 18. Access the IIS Website

On the server:

```text
http://localhost:8080
```

From another LAN PC:

```text
http://192.168.10.50:666
```

This is the normal URL users should bookmark.

---

# 19. Give IIS Storage Permissions

The IIS application pool needs access to:

```text
H:\FTP
```

If the application pool is:

```text
LanSharePool
```

the Windows identity is:

```text
IIS AppPool\LanSharePool
```

Right-click:

```text
H:\FTP
→ Properties
→ Security
→ Edit
→ Add
```

Enter:

```text
IIS AppPool\LanSharePool
```

Use **Check Names**.

For the simple setup, grant:

```text
Modify
Read & execute
List folder contents
Read
Write
```

This is especially important because the application writes:

```text
H:\FTP\.metadata.json
```

---

# 20. Recommended Permission Layout

For a more restrictive configuration:

```text
H:\FTP
├── Documents    Read
├── Photos       Read
├── Projects     Read
├── Software     Read
└── Uploads      Modify
```

For the simplest installation, `Modify` on `H:\FTP` is easier.

---

# 21. Configure IIS for 3 GB Requests

The published `web.config` must contain an IIS request limit of 3 GB.

Example:

```xml
<configuration>
  <system.webServer>
    <security>
      <requestFiltering>
        <requestLimits
          maxAllowedContentLength="3221225472" />
      </requestFiltering>
    </security>
  </system.webServer>
</configuration>
```

`3221225472` bytes is 3 GB.

This is required because IIS can reject a large request before ASP.NET Core receives it.

---

# 22. Upload Progress GUI

The upload GUI should upload selected files individually rather than putting every file into one large request.

The flow is:

```text
File 1
  ↓
Upload progress
  ↓
100%
  ↓
File 2
  ↓
Upload progress
  ↓
100%
  ↓
File 3
```

Each file displays:

- File name
- Progress bar
- Percentage
- Speed
- ETA
- Completion/error status

The existing `/api/upload` endpoint can be reused.

---

# 23. 3 GB Validation

The application validates the limit at multiple levels:

```text
Browser
   ↓
Client-side 3 GB check
   ↓
IIS
   ↓
3 GB request filtering
   ↓
ASP.NET Core
   ↓
3 GB server-side file check
```

Examples:

```text
2.8 GB → allowed
3.0 GB → allowed
3.1 GB → rejected
```

---

# 24. Test Uploads

Open:

```text
http://192.168.10.50:666
```

Select `Uploads`.

Enter the uploader name if required.

Select a small test file.

Click Upload.

Verify that:

1. Progress appears.
2. Percentage changes.
3. Speed is displayed.
4. ETA is displayed.
5. Upload completes.
6. The file appears under:

```text
H:\FTP\Uploads
```

---

# 25. Test Multiple Files

Select several files.

The application should process them sequentially:

```text
File A → 0% → 100%
File B → 0% → 100%
File C → 0% → 100%
```

This prevents multiple browser uploads from competing unnecessarily for the shared upload limit.

---

# 26. Download Testing

Browse to a shared file and download it.

The download path is conceptually:

```text
H:\FTP\File
    ↓
File stream
    ↓
Shared download limiter
    ↓
Browser
```

The target aggregate download rate is approximately:

```text
3 MB/s total
```

---

# 27. Important Bandwidth Limiting Note

The current upload limiter operates while the application copies the uploaded file to disk.

Therefore, it is an application-level transfer/write limiter rather than a perfect low-level network QoS mechanism.

The browser's progress events measure the HTTP upload side, while the application limiter controls its own transfer/write stage.

Consequently, the browser speed indicator may fluctuate.

This is normal.

---

# 28. Optional FTP Server

The browser GUI does not require FTP.

If traditional FTP is also desired, install the IIS FTP components:

```text
IIS
→ FTP Server
    FTP Service
    FTP Extensibility
```

Create an FTP site using:

```text
Port: 21
Root: H:\FTP
```

A client such as WinSCP can then connect to:

```text
Host: 192.168.10.50
Port: 21
Protocol: FTP
```

WinSCP does not need to run on the server.

The IIS FTP server is separate from the ASP.NET Core web GUI.

---

# 29. Web GUI vs FTP

| Feature | Web GUI | FTP |
|---|---:|---:|
| Browser interface | Yes | Modern browsers generally do not provide normal FTP browsing |
| Browse files | Yes | Yes |
| Download | Yes | Yes |
| Upload | Yes | Yes |
| Upload progress | Yes | Client dependent |
| 3 GB application validation | Yes | Separate FTP configuration |
| 3 MB/s application limiter | Yes | Not automatically the same |
| WinSCP | Not required | Yes |

For normal LAN users, use the web GUI.

---

# 30. Stable LAN IP

The example server IP is:

```text
192.168.10.50
```

It is preferable to reserve this address in the router's DHCP settings.

Then users can consistently use:

```text
http://192.168.10.50:666
```

If the server's DHCP address changes, users' bookmarks may stop working.

---

# 31. Troubleshooting

## Cannot open the website

From a client PC:

```powershell
ping 192.168.10.50
```

Then:

```powershell
Test-NetConnection 192.168.10.50 -Port 8080
```

Check:

- IIS site is started.
- Application pool is started.
- Port 8080 is bound.
- Windows Firewall allows port 8080.
- Server IP is correct.

---

## IIS returns 500

Check:

```text
IIS Manager
→ Sites
→ Example LAN Share
→ Basic Settings
```

Verify:

```text
C:\LANFileServer\publish
```

is the physical path.

Check the application pool and Windows Event Viewer.

---

## Upload returns 413

Check:

### ASP.NET Core

```csharp
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit =
        3L * 1024 * 1024 * 1024;
});
```

### IIS

```xml
<requestLimits
    maxAllowedContentLength="3221225472" />
```

Then restart IIS:

```powershell
iisreset
```

---

## File appears but upload reports 500

If the file is already present in:

```text
H:\FTP\Uploads
```

but the browser reports an error, a later operation may have failed.

Check the IIS application pool permissions, especially for:

```text
H:\FTP\.metadata.json
```

The application pool identity should have appropriate Modify permission.

---

## Permission denied

Check:

```text
H:\FTP
```

permissions for:

```text
IIS AppPool\LanSharePool
```

---

## Works on server but not other PCs

Check the IIS binding:

```text
HTTP
All Unassigned
Port 8080
Host name empty
```

Then:

```powershell
Test-NetConnection 192.168.10.50 -Port 8080
```

from a client PC.

---

## Port 8080 already in use

Check:

```powershell
netstat -ano | findstr :8080
```

Choose another port if necessary.

Remember to update the firewall rule and URL.

---

# 32. Updating the Application

After changing `Program.cs`:

```powershell
cd C:\LANFileServer\LanShareApp
dotnet publish -c Release -o C:\LANFileServer\publish
```

Then restart IIS:

```powershell
iisreset
```

You do not need to run `dotnet run` for the IIS-hosted production version.

---

# 33. Useful Commands

### Check .NET

```powershell
dotnet --info
```

### Build

```powershell
dotnet build
```

### Run locally

```powershell
dotnet run
```

### Run on LAN for development

```powershell
dotnet run --urls "http://0.0.0.0:5042"
```

### Publish

```powershell
dotnet publish -c Release -o C:\LANFileServer\publish
```

### Restart IIS

```powershell
iisreset
```

### Check port

```powershell
netstat -ano | findstr :8080
```

### Test port remotely

```powershell
Test-NetConnection 192.168.10.50 -Port 8080
```

---

# 34. Recommended Directory Layout

```text
C:\LANFileServer
│
├── LanShareApp
│   ├── Program.cs
│   ├── LanShareApp.csproj
│   └── ...
│
└── publish
    ├── LanShareApp.dll
    ├── web.config
    └── ...
```

Data remains separate:

```text
H:\FTP
├── Documents
├── Photos
├── Projects
├── Software
├── Uploads
└── .metadata.json
```

---

# 35. Backup

Back up:

```text
H:\FTP
```

including:

```text
Documents
Photos
Projects
Software
Uploads
.metadata.json
```

The data directory is more important to back up than the published application because the application can be republished from source.

---

# 36. Security

This setup is intended for a trusted private LAN.

Do **not** expose the application directly to the public Internet without adding appropriate:

- Authentication
- Authorization
- HTTPS
- Security hardening
- Access controls
- Logging/monitoring

Do not create router port-forwarding rules for port 8080 or FTP port 21 unless you intentionally design and secure the application for Internet access.

---

# 37. Final Installation Checklist

## Windows

- [ ] Windows is running
- [ ] Server has stable LAN IP
- [ ] .NET installed
- [ ] IIS installed
- [ ] ASP.NET Core Hosting Bundle installed

## Storage

- [ ] `H:\FTP` exists
- [ ] `Documents` exists
- [ ] `Photos` exists
- [ ] `Projects` exists
- [ ] `Software` exists
- [ ] `Uploads` exists

## Application

- [ ] Root path points to `H:\FTP`
- [ ] Upload path points to `H:\FTP\Uploads`
- [ ] 3 GB ASP.NET Core limit enabled
- [ ] 3 GB server-side validation enabled
- [ ] 3 GB browser-side validation enabled
- [ ] Upload progress enabled
- [ ] Upload limit set to approximately 3 MB/s total
- [ ] Download limit set to approximately 3 MB/s total

## IIS

- [ ] Application published
- [ ] Application pool created
- [ ] Application pool uses `No Managed Code`
- [ ] Website created
- [ ] Physical path points to `C:\LANFileServer\publish`
- [ ] Website uses port 8080
- [ ] `web.config` has 3 GB request limit
- [ ] IIS application pool has storage permissions

## Firewall

- [ ] TCP 666 allowed on LAN
- [ ] TCP 21 allowed only if FTP is used

## Testing

- [ ] `http://localhost:8080` works
- [ ] `http://192.168.10.50:666` works from another PC
- [ ] Files can be browsed
- [ ] Files can be downloaded
- [ ] Files can be uploaded
- [ ] Progress bar works
- [ ] Speed is displayed
- [ ] ETA is displayed
- [ ] >3 GB files are rejected
- [ ] Multiple files work
- [ ] Bandwidth limits work

---

# 38. Normal User Instructions

Users only need to know:

1. Connect to the same LAN/Wi-Fi as the server.
2. Open a browser.
3. Go to:

```text
http://192.168.10.50:666
```

4. Browse folders.
5. Download files as needed.
6. To upload, open `Uploads`.
7. Select or drag files into the upload area.
8. Click Upload.
9. Watch the progress bar.

Files larger than 3 GB cannot be uploaded.

---

# 39. Quick Reference

| Item | Value |
|---|---|
| Server IP | `192.168.10.50` |
| Web GUI | `http://192.168.10.50:666` |
| Development URL | `http://192.168.10.50:5042` |
| FTP port | `21` |
| Storage root | `H:\FTP` |
| Upload folder | `H:\FTP\Uploads` |
| Project | `C:\LANFileServer\LanShareApp` |
| Publish folder | `C:\LANFileServer\publish` |
| Maximum individual upload | `3 GB` |
| Total upload target | `3 MB/s` |
| Total download target | `3 MB/s` |
| IIS application pool | `LanSharePool` |
| IIS mode | `No Managed Code` |

---

# 40. End Result

After setup, users simply open:

```text
http://192.168.10.50:666
```

The Windows server runs IIS, IIS hosts the published ASP.NET Core application, and the application manages the files under:

```text
H:\FTP
```

The normal workflow is:

```text
Windows Server
      ↓
IIS starts website
      ↓
User opens browser
      ↓
http://192.168.10.50:666
      ↓
Browse / Download
      ↓
Upload → H:\FTP\Uploads
```

No `dotnet run` is required for normal IIS operation.
