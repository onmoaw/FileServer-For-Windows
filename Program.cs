using Microsoft.AspNetCore.Http.Features;
using System.Text.Json;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 3L * 1024 * 1024 * 1024; // 3 GB
});

var app = builder.Build();

const string RootPath = @"H:\FTP";
const string UploadPath = @"H:\FTP\Uploads";
const string MetadataPath = @"H:\FTP\.metadata.json";

// ============================================================
// BANDWIDTH LIMITS
// ============================================================

// 3 MB/s TOTAL for all uploads combined
const long UploadLimitBytesPerSecond = 3L * 1024 * 1024;

// 3 MB/s TOTAL for all downloads combined
const long DownloadLimitBytesPerSecond = 3L * 1024 * 1024;

// Maximum individual file size
const long MaxUploadFileSize = 3L * 1024 * 1024 * 1024;

var uploadLimiter =
    new SharedBandwidthLimiter(
        UploadLimitBytesPerSecond);

var downloadLimiter =
    new SharedBandwidthLimiter(
        DownloadLimitBytesPerSecond);

app.UseDefaultFiles();
app.UseStaticFiles();


// ============================================================
// SECURITY
// ============================================================

string GetSafePath(string? relativePath)
{
    relativePath ??= "";

    relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);

    // Remove leading separators
    relativePath = relativePath.TrimStart(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar);

    var root = Path.GetFullPath(RootPath);

    var combined = Path.GetFullPath(
        Path.Combine(root, relativePath));

    // Make sure the resulting path is actually inside H:\FTP
    if (!combined.StartsWith(
        root + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase)
        && !string.Equals(
            combined,
            root,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new UnauthorizedAccessException(
            "Invalid file path.");
    }

    return combined;
}




async Task<Dictionary<string, UploadMetadata>> LoadMetadata()
{
    try
    {
        if (!File.Exists(MetadataPath))
            return new Dictionary<string, UploadMetadata>(StringComparer.OrdinalIgnoreCase);

        var json = await File.ReadAllTextAsync(MetadataPath);
        return JsonSerializer.Deserialize<Dictionary<string, UploadMetadata>>(json)
               ?? new Dictionary<string, UploadMetadata>(StringComparer.OrdinalIgnoreCase);
    }
    catch
    {
        return new Dictionary<string, UploadMetadata>(StringComparer.OrdinalIgnoreCase);
    }
}

async Task SaveMetadata(Dictionary<string, UploadMetadata> metadata)
{
    var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(MetadataPath, json);
}


// FILE LISTING API
// ============================================================

app.MapGet("/api/files", async (string? path) =>
{
    try
    {
        var directory = GetSafePath(path);

        if (!Directory.Exists(directory))
        {
            return Results.NotFound(new
            {
                message = "Directory not found."
            });
        }

        var items = new List<object>();
        var metadata = await LoadMetadata();

        // Folders
        foreach (var folder in Directory.GetDirectories(directory))
        {
            var info = new DirectoryInfo(folder);

            // Hide hidden/system folders
            if (info.Attributes.HasFlag(FileAttributes.Hidden) ||
                info.Attributes.HasFlag(FileAttributes.System))
                continue;

            items.Add(new
            {
                name = info.Name,
                type = "folder",
                size = (long?)null,
                modified = info.LastWriteTime,
                path = Path.GetRelativePath(
                            RootPath,
                            folder).Replace('\\', '/')
            });
        }

        // Files
        foreach (var file in Directory.GetFiles(directory))
        {
            var info = new FileInfo(file);

            // Hide Windows/IIS configuration files
            if (info.Name.Equals("Desktop.ini", StringComparison.OrdinalIgnoreCase))
                continue;

            if (info.Name.Equals("web.config", StringComparison.OrdinalIgnoreCase))
                continue;

            if (info.Name.Equals(".metadata.json", StringComparison.OrdinalIgnoreCase))
                continue;

            // Hide hidden/system files
            if (info.Attributes.HasFlag(FileAttributes.Hidden) ||
                info.Attributes.HasFlag(FileAttributes.System))
                continue;

            items.Add(new
            {
                name = info.Name,
                type = "file",
                size = info.Length,
                modified = info.LastWriteTime,
                path = Path.GetRelativePath(
                    RootPath,
                    file).Replace('\\', '/'),
                uploader = metadata.TryGetValue(
                    Path.GetRelativePath(RootPath, file).Replace('\\', '/'),
                    out var fileMetadata) ? fileMetadata.Uploader : ""
            });
        }

        return Results.Ok(items);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.StatusCode(403);
    }
});


// ============================================================
// FILE DOWNLOAD
// ============================================================

app.MapGet("/api/download", (string path) =>
{
    try
    {
        var filePath = GetSafePath(path);

        var fileName =
            Path.GetFileName(filePath);

        if (fileName.Equals(
                "web.config",
                StringComparison.OrdinalIgnoreCase)
            ||
            fileName.Equals(
                "Desktop.ini",
                StringComparison.OrdinalIgnoreCase)
            ||
            fileName.Equals(
                ".metadata.json",
                StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound();
        }

        if (!File.Exists(filePath))
        {
            return Results.NotFound();
        }

        var contentType = "application/octet-stream";

        var extension =
            Path.GetExtension(filePath).ToLowerInvariant();

        contentType = extension switch
        {
            ".pdf" => "application/pdf",

            ".jpg" or ".jpeg" =>
                "image/jpeg",

            ".png" =>
                "image/png",

            ".gif" =>
                "image/gif",

            ".webp" =>
                "image/webp",

            ".txt" =>
                "text/plain",

            ".html" or ".htm" =>
                "text/html",

            ".mp4" =>
                "video/mp4",

            ".mp3" =>
                "audio/mpeg",

            ".zip" =>
                "application/zip",

            _ => "application/octet-stream"
        };

        var fileStream =
    new FileStream(
        filePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        64 * 1024,
        FileOptions.Asynchronous |
        FileOptions.SequentialScan);

        var throttledStream =
            new ThrottledReadStream(
                fileStream,
                downloadLimiter);

        return Results.Stream(
            throttledStream,
            contentType,
            Path.GetFileName(filePath),
            enableRangeProcessing: false);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.StatusCode(403);
    }
});


// ============================================================
// UPLOAD
// ============================================================

app.MapPost("/api/upload", async (HttpRequest request) =>
{
    try
    {
        if (!Directory.Exists(UploadPath))
        {
            Directory.CreateDirectory(UploadPath);
        }

        var form = await request.ReadFormAsync();
        var uploader = form["uploader"].FirstOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(uploader))
            return Results.BadRequest(new { message = "Please enter the uploader name." });

        if (uploader.Length > 100)
            return Results.BadRequest(new { message = "Uploader name is too long." });


        if (form.Files.Count == 0)
        {
            return Results.BadRequest(new
            {
                message = "No files selected."
            });
        }

        var uploaded = new List<string>();
        var metadata = await LoadMetadata();

        foreach (var file in form.Files)
        {
            if (file.Length <= 0)
                continue;

            // Maximum individual file size = 3 GB
            if (file.Length > MaxUploadFileSize)
            {
                return Results.BadRequest(new
                {
                    message =
                        $"File '{file.FileName}' exceeds the maximum allowed size of 3 GB."
                });
            }

            // Only keep the actual filename.
            var fileName =
                Path.GetFileName(file.FileName);

            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            // Prevent overwriting.
            var destination =
                Path.Combine(UploadPath, fileName);

            if (File.Exists(destination))
            {
                var name =
                    Path.GetFileNameWithoutExtension(fileName);

                var extension =
                    Path.GetExtension(fileName);

                var counter = 1;

                do
                {
                    fileName =
                        $"{name} ({counter}){extension}";

                    destination =
                        Path.Combine(
                            UploadPath,
                            fileName);

                    counter++;

                } while (File.Exists(destination));
            }

            await using var stream =
                new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);

            await using var input = file.OpenReadStream();

            await CopyWithThrottleAsync(
                input,
                stream,
                uploadLimiter);

            var relativePath = Path.GetRelativePath(RootPath, destination).Replace('\\', '/');
            metadata[relativePath] = new UploadMetadata
            {
                Uploader = uploader,
                UploadedAt = DateTime.Now
            };

            uploaded.Add(fileName);
        }

        await SaveMetadata(metadata);
        return Results.Ok(new
        {
            message =
                $"{uploaded.Count} file(s) uploaded successfully.",

            files = uploaded
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex);

        return Results.Problem(
            detail: ex.ToString(),
            title: "Upload Error",
            statusCode: 500);
    }
});

// ============================================================
// FILE PREVIEW
// ============================================================

app.MapGet("/api/preview", async (string path) =>
{
    try
    {
        var filePath = GetSafePath(path);

        if (!File.Exists(filePath))
            return Results.NotFound();

        var extension =
            Path.GetExtension(filePath).ToLowerInvariant();

        var allowedExtensions = new[]
        {
            ".txt",
            ".csv",
            ".json",
            ".xml",
            ".log",
            ".md"
        };

        if (!allowedExtensions.Contains(extension))
            return Results.BadRequest();

        // Only read a limited amount for preview
        const int maxPreviewLength = 5000;

        await using var stream =
            new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

        using var reader =
            new StreamReader(stream);

        var buffer = new char[maxPreviewLength];

        var charsRead =
            await reader.ReadAsync(buffer, 0, buffer.Length);

        var content =
            new string(buffer, 0, charsRead);

        return Results.Text(content, "text/plain");
    }
    catch (UnauthorizedAccessException)
    {
        return Results.StatusCode(403);
    }
});


// ============================================================
// WEB APPLICATION
// ============================================================

app.MapGet("/", () =>
{
    return Results.Content("""
<!DOCTYPE html>

<html lang="en">

<head>

<meta charset="UTF-8">

<meta name="viewport"
      content="width=device-width, initial-scale=1.0">

<title>TASOG File Server</title>

<style>

* {
    box-sizing: border-box;
}

body {
    margin: 0;
    min-height: 100vh;
    display: flex;
    flex-direction: column;

    font-family:
        Inter,
        Segoe UI,
        Arial,
        sans-serif;

    background: #f5f7fb;
    color: #1f2937;
}


/* =========================================================
   HEADER
   ========================================================= */

.header {

    height: 64px;

    background: #ffffff;

    border-bottom:
        1px solid #e5e7eb;

    display: flex;

    align-items: center;

    justify-content: space-between;

    padding:
        0 24px;
}

.logo {

    display: flex;

    align-items: center;

    gap: 10px;

    font-size: 18px;

    font-weight: 700;
}

.logo-icon {

    width: 36px;

    height: 36px;

    border-radius: 9px;

    background: #2563eb;

    color: white;

    display: flex;

    align-items: center;

    justify-content: center;

    font-size: 18px;
}

.refresh {

    border: none;

    background: #f3f4f6;

    border-radius: 8px;

    padding:
        9px 13px;

    cursor: pointer;

    font-size: 14px;
}

.refresh:hover {

    background: #e5e7eb;
}


/* =========================================================
   LAYOUT
   ========================================================= */

.container {
    width: 100%;
    max-width: 1400px;
    margin: 24px auto;
    padding: 0 24px;
    flex: 1;
}

.layout {

    display: grid;

    grid-template-columns:
        minmax(0, 1fr)
        360px;

    gap: 24px;

    align-items: start;
}


/* =========================================================
   MAIN PANEL
   ========================================================= */

.panel {

    background: white;

    border:
        1px solid #e5e7eb;

    border-radius: 12px;

    overflow: hidden;
}

.panel-header {

    padding:
        18px 20px;

    border-bottom:
        1px solid #e5e7eb;
}

.breadcrumb {

    display: flex;

    align-items: center;

    gap: 7px;

    flex-wrap: wrap;

    font-size: 14px;
}

.breadcrumb button {

    border: none;

    background: none;

    color: #2563eb;

    cursor: pointer;

    padding: 0;

    font-size: 14px;
}

.breadcrumb button:hover {

    text-decoration: underline;
}


/* =========================================================
   SEARCH
   ========================================================= */

.toolbar {

    display: flex;

    justify-content:
        space-between;

    align-items: center;

    gap: 12px;

    padding:
        14px 20px;

    border-bottom:
        1px solid #e5e7eb;
}

.search {

    width: 100%;

    max-width: 350px;

    padding:
        10px 12px;

    border:
        1px solid #d1d5db;

    border-radius: 8px;

    outline: none;

    font-size: 14px;
}

.search:focus {

    border-color: #2563eb;
}


/* =========================================================
   FILE LIST
   ========================================================= */

.file-list {

    min-height: 400px;
}

.file-row {

    display: grid;

    grid-template-columns:
        minmax(0, 1fr)
        120px
        170px
        130px
        90px;

    align-items: center;

    gap: 12px;

    padding:
        13px 20px;

    border-bottom:
        1px solid #f0f1f3;

    font-size: 14px;
}

.file-row:hover {

    background: #f9fafb;
}

.file-name {

    display: flex;

    align-items: center;

    gap: 10px;

    min-width: 0;
}

.file-name span:last-child {

    overflow: hidden;

    text-overflow: ellipsis;

    white-space: nowrap;
}

.icon {

    font-size: 20px;

    flex-shrink: 0;
}

.file-action {

    color: #2563eb;

    border: none;

    background: none;

    cursor: pointer;

    font-size: 14px;

    text-align: left;
}

.file-action:hover {

    text-decoration: underline;
}

.muted {

    color: #6b7280;
}

.empty {

    padding:
        70px 20px;

    text-align: center;

    color: #6b7280;
}

.item-name-button {

    display: flex;

    align-items: center;

    gap: 10px;

    width: 100%;

    border: none;

    background: transparent;

    padding: 0;

    margin: 0;

    color: #1f2937;

    font-size: 14px;

    text-align: left;

    cursor: pointer;
}

.item-name-button:hover {

    color: #2563eb;

}

/* =========================================================
   UPLOAD PANEL
   ========================================================= */

.upload-panel {

    padding: 20px;
}

.upload-title {

    margin:
        0 0 6px;

    font-size: 18px;

    font-weight: 700;
}

.upload-description {

    margin:
        0 0 20px;

    color: #6b7280;

    font-size: 14px;
}

.uploader-label {
    display: block;
    margin: 0 0 7px;
    font-size: 13px;
    font-weight: 600;
    color: #374151;
}

.uploader-input {
    width: 100%;
    margin-bottom: 16px;
    padding: 10px 12px;
    border: 1px solid #d1d5db;
    border-radius: 8px;
    outline: none;
    font-size: 14px;
}

.uploader-input:focus {
    border-color: #2563eb;
}


.upload-box {

    border:
        1px solid #dbe1ea;

    border-radius: 12px;

    padding: 32px 20px;

    text-align: center;

    background: #fafbfc;

    transition: .2s;
}

.upload-box.dragover {

    border-color: #2563eb;

    background: #eff6ff;
}

.upload-icon {

    width: 46px;

    height: 46px;

    margin:
        0 auto 14px;

    border-radius: 50%;

    background: #eff6ff;

    color: #2563eb;

    display: flex;

    align-items: center;

    justify-content: center;

    font-size: 26px;

    font-weight: 700;
}

.upload-heading {

    font-size: 15px;

    font-weight: 600;

    margin-bottom: 5px;
}

.upload-description-small {

    font-size: 13px;

    color: #6b7280;

    margin-bottom: 18px;
}

.choose-button {

    border: 1px solid #2563eb;

    background: white;

    color: #2563eb;

    border-radius: 7px;

    padding:
        9px 18px;

    font-size: 14px;

    font-weight: 600;

    cursor: pointer;
}

.choose-button:hover {

    background: #eff6ff;
}

.file-count {

    margin-top: 14px;

    font-size: 13px;

    color: #6b7280;

    min-height: 18px;
}

.selected-files {
    margin-top: 14px;
    display: flex;
    flex-direction: column;
    gap: 6px;
    text-align: left;
}

.selected-file {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 10px;
    border: 1px solid #e5e7eb;
    border-radius: 7px;
    background: #ffffff;
    font-size: 13px;
}

.upload-item {
    padding: 10px 12px;
    border: 1px solid rgba(128, 128, 128, 0.25);
    border-radius: 8px;
    margin-bottom: 8px;
}

.upload-item-header {
    display: flex;
    align-items: center;
    gap: 8px;
}

.upload-item-name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.upload-progress {
    width: 100%;
    height: 8px;
    margin-top: 8px;
    background: rgba(128, 128, 128, 0.18);
    border-radius: 999px;
    overflow: hidden;
}

.upload-progress-bar {
    width: 0%;
    height: 100%;
    border-radius: inherit;
    transition: width 0.15s ease;
}

.upload-progress-info {
    display: flex;
    justify-content: space-between;
    gap: 12px;
    margin-top: 5px;
    font-size: 12px;
    opacity: 0.8;
}

.upload-item.success .upload-progress-bar {
    width: 100%;
}

.upload-item.error .upload-progress-bar {
    opacity: 0.6;
}

.selected-file-name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    color: #374151;
}

.remove-file {
    width: 24px;
    height: 24px;
    flex-shrink: 0;
    border: none;
    border-radius: 50%;
    background: transparent;
    color: #6b7280;
    font-size: 16px;
    line-height: 1;
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
}

.remove-file:hover {
    background: #fee2e2;
    color: #dc2626;
}

.no-files {
    color: #6b7280;
    font-size: 13px;
    text-align: center;
    padding: 4px;
}

.upload-button {

    width: 100%;

    margin-top: 16px;

    border: none;

    border-radius: 8px;

    background: #2563eb;

    color: white;

    padding:
        12px;

    cursor: pointer;

    font-size: 14px;

    font-weight: 600;
}

.upload-button:hover {

    background: #1d4ed8;
}

.upload-button:disabled {

    background: #9ca3af;

    cursor: not-allowed;
}

.destination {

    margin-top: 16px;

    padding:
        12px;

    border-radius: 8px;

    background: #f3f4f6;

    font-size: 13px;

    color: #4b5563;
}

.status {

    margin-top: 14px;

    padding:
        11px 12px;

    border-radius: 8px;

    font-size: 13px;

    display: none;
}

.status.success {

    display: block;

    background: #ecfdf5;

    color: #065f46;
}

.status.error {

    display: block;

    background: #fef2f2;

    color: #991b1b;
}


/* =========================================================
   RESPONSIVE
   ========================================================= */

@media (max-width: 900px) {

    .layout {

        grid-template-columns: 1fr;
    }

    .file-row {

        grid-template-columns:
            minmax(0, 1fr)
            80px
            100px;
    }

    .file-row .modified,
    .file-row .uploader-column {

        display: none;
    }
}

@media (max-width: 600px) {

    .container {

        padding: 0 12px;

        margin-top: 12px;
    }

    .header {

        padding: 0 14px;
    }

    .file-row {

        grid-template-columns:
            minmax(0, 1fr)
            70px;
    }

    .file-row .size {

        display: none;
    }
}

/* =========================================================
   FILE PREVIEW
   ========================================================= */

.preview-popup {
    position: fixed;
    z-index: 9999;
    width: 360px;
    max-width: calc(100vw - 30px);

    background: #ffffff;
    border: 1px solid #dbe1ea;
    border-radius: 10px;

    box-shadow:
        0 12px 35px rgba(0, 0, 0, 0.15);

    padding: 10px;

    display: none;

    pointer-events: none;
}

.preview-popup.visible {
    display: block;
}

.preview-image {
    display: block;

    width: 100%;
    max-height: 260px;

    object-fit: contain;

    border-radius: 7px;

    background: #f3f4f6;
}

.preview-text {
    margin: 0;

    max-height: 260px;

    overflow: hidden;

    white-space: pre-wrap;
    word-break: break-word;

    font-family:
        Consolas,
        "Courier New",
        monospace;

    font-size: 12px;
    line-height: 1.5;

    color: #374151;

    background: #f8fafc;

    border-radius: 7px;

    padding: 10px;
}

.preview-title {
    font-size: 12px;
    font-weight: 600;

    color: #374151;

    margin-bottom: 7px;

    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

/* =========================================================
   FOOTER
   ========================================================= */

.footer {
    flex-shrink: 0;

    display: flex;
    align-items: center;
    justify-content: center;

    padding: 18px 24px;

    margin-top: auto;

    border-top: 1px solid #e5e7eb;

    background: #ffffff;

    color: #6b7280;

    font-size: 13px;
}

.onmoeon-link {
    margin: 0 8px;

    text-decoration: none;

    display: inline-block;

    transition:
        transform 0.3s ease;
}

.onmoeon-text {
    font-size: 14px;
    font-weight: 600;

    cursor: pointer;

    display: inline-block;

    background:
        linear-gradient(
            90deg,
            rgb(52, 166, 244) 0%,
            rgb(83, 234, 253) 50%,
            rgb(52, 166, 244) 100%
        );

    background-size: 200% auto;

    -webkit-background-clip: text;
    background-clip: text;

    -webkit-text-fill-color: transparent;

    transition:
        transform 0.3s ease;
}

.onmoeon-link:hover .onmoeon-text {
    transform:
        translateY(-2px)
        scale(1.05);
}

</style>

</head>


<body>

    <div id="previewPopup" class="preview-popup"></div>

    <header class="header">

    <div class="logo">

        <div class="logo-icon">
            📁
        </div>

        <span>
            TASOG File Server
        </span>

    </div>


    <button
        class="refresh"
        onclick="loadFiles()">

        ↻ Refresh

    </button>

</header>



<main class="container">


<div class="layout">


<!-- =====================================================
     FILE BROWSER
     ===================================================== -->

<section class="panel">


<div class="panel-header">

    <div
        class="breadcrumb"
        id="breadcrumb">
    </div>

</div>



<div class="toolbar">

    <input
        class="search"
        id="search"
        type="search"
        placeholder="Search files and folders..."
        oninput="renderFiles()">

</div>



<div id="fileList"
     class="file-list">

    <div class="empty">

        Loading...

    </div>

</div>


</section>



<!-- =====================================================
     UPLOAD
     ===================================================== -->

<section class="panel">


<div class="upload-panel">

    <h2 class="upload-title">
        Upload Files
    </h2>

    <p class="upload-description">

        Upload files directly to the
        <strong>Uploads</strong> folder.

    </p>


<label class="uploader-label" for="uploaderName">Uploader Name</label>

<input
    type="text"
    id="uploaderName"
    class="uploader-input"
    placeholder="Enter your name"
    maxlength="100"
    autocomplete="name">

<div class="upload-box" id="dropZone">

    <div class="upload-icon">
        ↑
    </div>

    <div class="upload-heading">
        Upload files
    </div>

    <div class="upload-description-small">
        Select files from your computer
    </div>

    <input
        type="file"
        id="fileInput"
        multiple
        hidden>

    <button
        type="button"
        class="choose-button"
        id="chooseButton">

        Choose Files

    </button>

    <div class="selected-files" id="selectedFiles">
        <div class="no-files">
            No files selected
        </div>
    </div>

</div>


    <button
        id="uploadButton"
        class="upload-button"
        disabled>

        Upload Files

    </button>


    <div
        id="uploadStatus"
        class="status">
    </div>


    <div class="destination">

        Destination:

        <strong>
            /Uploads
        </strong>

    </div>


</div>

</section>


</div>


</main>

<footer class="footer">
    <span>Powered By</span>

    <a
        href="https://www.onmoeon.online/"
        target="_blank"
        rel="noopener noreferrer"
        class="onmoeon-link">

        <span class="onmoeon-text">
            ONMOEON
        </span>

    </a>
</footer>

<script>


// =========================================================
// STATE
// =========================================================

let currentPath = "";

let currentItems = [];

let selectedFiles = [];


// =========================================================
// INITIAL LOAD
// =========================================================

document.addEventListener(
    "DOMContentLoaded",
    () => {

        loadFiles();

        setupUpload();

    });


// =========================================================
// LOAD FILES
// =========================================================

async function loadFiles(path = currentPath) {

    currentPath = path;

    const list =
        document.getElementById("fileList");

    list.innerHTML = `
        <div class="empty">
            Loading...
        </div>
    `;

    try {

        const response =
            await fetch(
                "/api/files?path=" +
                encodeURIComponent(currentPath)
            );

        if (!response.ok) {

            throw new Error(
                "Unable to load files."
            );
        }

        currentItems =
            await response.json();

        renderBreadcrumb();

        renderFiles();

    }
    catch (error) {

        list.innerHTML = `
            <div class="empty">
                Unable to load files.
            </div>
        `;
    }
}


// =========================================================
// BREADCRUMB
// =========================================================

function renderBreadcrumb() {

    const breadcrumb =
        document.getElementById("breadcrumb");

    breadcrumb.innerHTML = "";


    const home =
        document.createElement("button");

    home.textContent = "Home";

    home.onclick = () =>
        loadFiles("");

    breadcrumb.appendChild(home);


    if (!currentPath)
        return;


    const parts =
        currentPath
            .split("/")
            .filter(Boolean);


    let accumulated = "";


    parts.forEach(
        (part, index) => {

            const separator =
                document.createElement("span");

            separator.textContent = "›";

            separator.className = "muted";

            breadcrumb.appendChild(
                separator
            );


            accumulated +=
                (accumulated ? "/" : "") +
                part;


            const button =
                document.createElement("button");

            button.textContent = part;


            const target =
                accumulated;


            button.onclick = () =>
                loadFiles(target);


            breadcrumb.appendChild(
                button
            );

        });
}

// =========================================================
// FILE PREVIEW
// =========================================================

let previewTimeout = null;

function isImageFile(name) {

    const extension =
        name.split(".").pop().toLowerCase();

    return [
        "jpg",
        "jpeg",
        "png",
        "gif",
        "webp",
        "bmp",
        "svg"
    ].includes(extension);
}


function isTextFile(name) {

    const extension =
        name.split(".").pop().toLowerCase();

    return [
        "txt",
        "csv",
        "json",
        "xml",
        "log",
        "md"
    ].includes(extension);
}


function showFilePreview(item, element) {

    if (item.type !== "file")
        return;

    if (!isImageFile(item.name) &&
        !isTextFile(item.name))
        return;

    clearTimeout(previewTimeout);

    previewTimeout = setTimeout(async () => {

        const popup =
            document.getElementById("previewPopup");

        popup.innerHTML = "";

        const title =
            document.createElement("div");

        title.className = "preview-title";
        title.textContent = item.name;

        popup.appendChild(title);

        if (isImageFile(item.name)) {

            const image =
                document.createElement("img");

            image.className = "preview-image";

            image.src =
                "/api/download?path=" +
                encodeURIComponent(item.path);

            image.alt = item.name;

            popup.appendChild(image);

            positionPreview(popup, element);

            popup.classList.add("visible");

            return;
        }

        if (isTextFile(item.name)) {

            const text =
                document.createElement("pre");

            text.className = "preview-text";

            text.textContent =
                "Loading preview...";

            popup.appendChild(text);

            positionPreview(popup, element);

            popup.classList.add("visible");

            try {

                const response =
                    await fetch(
                        "/api/preview?path=" +
                        encodeURIComponent(item.path)
                    );

                if (!response.ok)
                    throw new Error();

                const content =
                    await response.text();

                text.textContent =
                    content ||
                    "File is empty.";

            }
            catch {

                text.textContent =
                    "Preview unavailable.";
            }
        }

    }, 250);
}


function positionPreview(popup, element) {

    const rect =
        element.getBoundingClientRect();

    const popupWidth = 360;

    let left =
        rect.right + 12;

    let top =
        rect.top;

    // Keep preview inside the right side
    if (left + popupWidth >
        window.innerWidth - 15) {

        left =
            rect.left - popupWidth - 12;
    }

    // Keep preview inside the viewport
    if (left < 15)
        left = 15;

    const popupHeight =
        popup.offsetHeight || 300;

    if (top + popupHeight >
        window.innerHeight - 15) {

        top =
            window.innerHeight -
            popupHeight -
            15;
    }

    if (top < 15)
        top = 15;

    popup.style.left =
        `${left}px`;

    popup.style.top =
        `${top}px`;
}


function hideFilePreview() {

    clearTimeout(previewTimeout);

    const popup =
        document.getElementById("previewPopup");

    popup.classList.remove("visible");
}


// =========================================================
// RENDER FILES
// =========================================================

function renderFiles() {

    const list =
        document.getElementById("fileList");


    const search =
        document
            .getElementById("search")
            .value
            .toLowerCase();


    let items =
        currentItems.filter(
            item =>
                item.name
                    .toLowerCase()
                    .includes(search)
        );


    // Folders first

    items.sort(
        (a, b) => {

            if (
                a.type === "folder" &&
                b.type !== "folder"
            )
                return -1;

            if (
                a.type !== "folder" &&
                b.type === "folder"
            )
                return 1;

            return a.name
                .localeCompare(
                    b.name
                );
        });


    if (items.length === 0) {

        list.innerHTML = `
            <div class="empty">
                No files or folders found.
            </div>
        `;

        return;
    }


    list.innerHTML = `

        <div class="file-row"
             style="font-weight:600;background:#f9fafb">

            <div>Name</div>

            <div class="size">
                Size
            </div>

            <div class="modified">
                Modified
            </div>

            <div class="uploader-column">
                Uploaded By
            </div>

            <div>
                Action
            </div>

        </div>

    `;


    items.forEach(
        item => {

            const row =
                document.createElement(
                    "div");

            row.className =
                "file-row";


            const icon =
                item.type === "folder"
                    ? "📁"
                    : getFileIcon(
                        item.name);


            const size =
                item.type === "folder"
                    ? "—"
                    : formatSize(
                        item.size);


            const modified =
                new Date(
                    item.modified
                ).toLocaleString();


            const action =
                item.type === "folder"
                    ? "Open"
                    : "Download";


            row.innerHTML = `

                <div class="file-name">

    <button
        class="item-name-button"
        title="${escapeHtml(item.name)}">

        <span class="icon">
            ${icon}
        </span>

        <span>
            ${escapeHtml(item.name)}
        </span>

    </button>

</div>


                <div class="size muted">
                    ${size}
                </div>


                <div class="modified muted">
                    ${modified}
                </div>

                <div class="uploader-column muted">
                    ${item.uploader ? escapeHtml(item.uploader) : "—"}
                </div>


                <div>

                    <button
                        class="file-action">

                        ${action}

                    </button>

                </div>

            `;


            const actionButton =
    row.querySelector(
        ".file-action");

const nameButton =
    row.querySelector(
        ".item-name-button");
if (item.type === "file") {

    nameButton.addEventListener(
        "mouseenter",
        () => showFilePreview(
            item,
            nameButton
        )
    );

    nameButton.addEventListener(
        "mouseleave",
        hideFilePreview
    );
}

            if (item.type === "folder") {

    nameButton.onclick =
        () => loadFiles(item.path);

    actionButton.onclick =
        () => loadFiles(item.path);

}
else {

    nameButton.onclick =
        () => downloadFile(item.path);

    actionButton.onclick =
        () => downloadFile(item.path);

}


            list.appendChild(row);

        });
}



// =========================================================
// DOWNLOAD
// =========================================================

function downloadFile(path) {

    window.location.href =
        "/api/download?path=" +
        encodeURIComponent(path);
}


// =========================================================
// FILE ICON
// =========================================================

function getFileIcon(name) {

    const extension =
        name
            .split(".")
            .pop()
            .toLowerCase();


    const icons = {

        pdf: "📕",

        doc:
            "📘",

        docx:
            "📘",

        xls:
            "📗",

        xlsx:
            "📗",

        ppt:
            "📙",

        pptx:
            "📙",

        jpg:
            "🖼️",

        jpeg:
            "🖼️",

        png:
            "🖼️",

        gif:
            "🖼️",

        mp4:
            "🎬",

        mp3:
            "🎵",

        zip:
            "🗜️",

        rar:
            "🗜️",

        txt:
            "📄",

        csv:
            "📊"

    };


    return icons[extension] || "📄";
}


// =========================================================
// SIZE
// =========================================================

function formatSize(bytes) {

    if (!bytes)
        return "0 B";


    const units =
        [
            "B",
            "KB",
            "MB",
            "GB",
            "TB"
        ];


    const index =
        Math.floor(
            Math.log(bytes) /
            Math.log(1024)
        );


    return (
        bytes /
        Math.pow(
            1024,
            index
        )
    ).toFixed(
        index === 0 ? 0 : 1
    ) +
    " " +
    units[index];
}


// =========================================================
// ESCAPE HTML
// =========================================================

function escapeHtml(value) {

    return value
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#039;");
}


// =========================================================
// UPLOAD
// =========================================================

function setupUpload() {

    const input =
        document.getElementById(
            "fileInput");

    const dropZone =
        document.getElementById(
            "dropZone");

    const button =
        document.getElementById(
            "uploadButton");

    const chooseButton =
        document.getElementById(
            "chooseButton");

    const uploaderInput =
        document.getElementById(
            "uploaderName");


    chooseButton.addEventListener(
        "click",
        (event) => {

            event.stopPropagation();

            input.click();

        });


    uploaderInput.addEventListener(
        "input",
        updateUploadUI);


    input.addEventListener(
        "change",
        () => {

            selectedFiles =
                Array.from(
                    input.files);

            updateUploadUI();

        });


    dropZone.addEventListener(
        "dragover",
        event => {

            event.preventDefault();

            dropZone.classList.add(
                "dragover"
            );

        });


    dropZone.addEventListener(
        "dragleave",
        () => {

            dropZone.classList.remove(
                "dragover"
            );

        });


    dropZone.addEventListener(
        "drop",
        event => {

            event.preventDefault();

            dropZone.classList.remove(
                "dragover"
            );

            selectedFiles =
                Array.from(
                    event.dataTransfer.files);

            updateUploadUI();

        });


    button.addEventListener(
        "click",
        uploadFiles);

}


function updateUploadUI() {

    const selectedFilesContainer =
        document.getElementById("selectedFiles");

    const button =
        document.getElementById("uploadButton");

    if (selectedFiles.length === 0) {

        selectedFilesContainer.innerHTML = `
            <div class="no-files">
                No files selected
            </div>
        `;

        button.disabled = true;

        return;
    }

    selectedFilesContainer.innerHTML = "";

    selectedFiles.forEach((file, index) => {

        const item =
            document.createElement("div");

        item.className = "selected-file";

        item.innerHTML = `
            <span>📄</span>

            <span class="selected-file-name"
                  title="${escapeHtml(file.name)}">
                ${escapeHtml(file.name)}
            </span>

            <button
                type="button"
                class="remove-file"
                title="Remove file">
                ×
            </button>
        `;

        const removeButton =
            item.querySelector(".remove-file");

        removeButton.addEventListener(
            "click",
            () => removeSelectedFile(index)
        );

        selectedFilesContainer.appendChild(item);
    });

    button.disabled =
        !document
            .getElementById("uploaderName")
            .value
            .trim();
}

function removeSelectedFile(index) {

    selectedFiles.splice(index, 1);

    // Rebuild the browser file input so it matches
    // the selectedFiles array.
    const input =
        document.getElementById("fileInput");

    const dataTransfer =
        new DataTransfer();

    selectedFiles.forEach(file => {
        dataTransfer.items.add(file);
    });

    input.files = dataTransfer.files;

    updateUploadUI();
}


// =========================================================
// UPLOAD FILES
// =========================================================

async function uploadFiles() {

    if (selectedFiles.length === 0)
        return;

    const button =
        document.getElementById("uploadButton");

    const status =
        document.getElementById("uploadStatus");

    const uploader =
        document.getElementById("uploaderName").value.trim();

    // Maximum individual file size = 3 GB
    const maxFileSize =
        3 * 1024 * 1024 * 1024;

    const oversizedFile =
        selectedFiles.find(
            file => file.size > maxFileSize
        );

    if (oversizedFile) {
        status.className = "status error";
        status.textContent =
            `File "${oversizedFile.name}" exceeds the maximum allowed size of 3 GB.`;
        return;
    }

    if (!uploader) {
        status.className = "status error";
        status.textContent = "Please enter your name.";
        return;
    }

    button.disabled = true;

    const filesToUpload = [...selectedFiles];
    const selectedFilesContainer =
        document.getElementById("selectedFiles");

    // Build progress rows once. Uploads are sent one at a time so the
    // existing 3 MB/s shared server limiter remains the total upload limit.
    selectedFilesContainer.innerHTML = "";

    const rows = filesToUpload.map((file, index) => {
        const item = document.createElement("div");
        item.className = "upload-item";
        item.innerHTML = `
            <div class="upload-item-header">
                <span>📄</span>
                <span class="upload-item-name" title="${escapeHtml(file.name)}">
                    ${escapeHtml(file.name)}
                </span>
                <span class="upload-percent">0%</span>
            </div>
            <div class="upload-progress">
                <div class="upload-progress-bar"></div>
            </div>
            <div class="upload-progress-info">
                <span class="upload-speed">Waiting...</span>
                <span class="upload-eta">Waiting...</span>
            </div>
        `;
        selectedFilesContainer.appendChild(item);

        return {
            item,
            bar: item.querySelector(".upload-progress-bar"),
            percent: item.querySelector(".upload-percent"),
            speed: item.querySelector(".upload-speed"),
            eta: item.querySelector(".upload-eta")
        };
    });

    const formatSpeed = bytesPerSecond =>
        `${formatSize(bytesPerSecond)}/s`;

    const formatEta = seconds => {
        if (!Number.isFinite(seconds) || seconds < 0)
            return "Calculating...";

        seconds = Math.ceil(seconds);

        const hours = Math.floor(seconds / 3600);
        const minutes = Math.floor((seconds % 3600) / 60);
        const secs = seconds % 60;

        if (hours > 0)
            return `${hours}h ${minutes}m ${secs}s remaining`;

        if (minutes > 0)
            return `${minutes}m ${secs}s remaining`;

        return `${secs}s remaining`;
    };

    const uploadOneFile = (file, row) => {
        return new Promise((resolve, reject) => {
            const xhr = new XMLHttpRequest();
            const formData = new FormData();

            formData.append("uploader", uploader);
            formData.append("files", file);

            const startTime = performance.now();
            let lastLoaded = 0;
            let lastTime = startTime;

            xhr.upload.addEventListener("progress", event => {
                if (!event.lengthComputable)
                    return;

                const now = performance.now();
                const loaded = event.loaded;
                const total = event.total || file.size;
                const percent = Math.min(
                    100,
                    (loaded / total) * 100
                );

                row.bar.style.width = `${percent.toFixed(1)}%`;
                row.percent.textContent = `${percent.toFixed(0)}%`;

                const elapsedSeconds =
                    (now - startTime) / 1000;

                // Use a rolling speed measurement for a more stable display.
                const intervalSeconds =
                    (now - lastTime) / 1000;

                let speed;

                if (intervalSeconds >= 0.25) {
                    speed =
                        (loaded - lastLoaded) /
                        intervalSeconds;

                    lastLoaded = loaded;
                    lastTime = now;
                } else if (elapsedSeconds > 0) {
                    speed = loaded / elapsedSeconds;
                } else {
                    speed = 0;
                }

                row.speed.textContent =
                    speed > 0
                        ? formatSpeed(speed)
                        : "Starting...";

                const remaining = total - loaded;

                row.eta.textContent =
                    speed > 0
                        ? formatEta(remaining / speed)
                        : "Calculating...";
            });

            xhr.addEventListener("load", async () => {
                let result = null;

                try {
                    result = xhr.responseText
                        ? JSON.parse(xhr.responseText)
                        : null;
                } catch {
                    // IIS may return an HTML error page instead of JSON.
                }

                if (xhr.status >= 200 && xhr.status < 300) {
                    row.item.classList.add("success");
                    row.bar.style.width = "100%";
                    row.percent.textContent = "100%";
                    row.speed.textContent = "Complete";
                    row.eta.textContent = "Done";
                    resolve(result);
                    return;
                }

                reject(
                    new Error(
                        result?.message ||
                        `Upload failed for "${file.name}" (HTTP ${xhr.status}).`
                    )
                );
            });

            xhr.addEventListener("error", () => {
                reject(
                    new Error(
                        `Network error while uploading "${file.name}".`
                    )
                );
            });

            xhr.addEventListener("abort", () => {
                reject(
                    new Error(
                        `Upload cancelled for "${file.name}".`
                    )
                );
            });

            xhr.open("POST", "/api/upload");
            xhr.send(formData);
        });
    };

    let completed = 0;
    const failed = [];

    status.className = "status success";
    status.textContent =
        `Uploading 0 of ${filesToUpload.length}...`;

    for (let i = 0; i < filesToUpload.length; i++) {
        const file = filesToUpload[i];
        const row = rows[i];

        row.speed.textContent = "Uploading...";
        row.eta.textContent = "Calculating...";

        try {
            await uploadOneFile(file, row);
            completed++;

            status.textContent =
                `Uploading ${completed} of ${filesToUpload.length}...`;
        } catch (error) {
            row.item.classList.add("error");
            row.speed.textContent = "Failed";
            row.eta.textContent = error.message;
            failed.push(file.name);

            // Continue with the remaining files.
            status.className = "status error";
            status.textContent =
                `Completed ${completed} of ${filesToUpload.length}; continuing...`;
        }
    }

    if (failed.length === 0) {
        status.className = "status success";
        status.textContent =
            filesToUpload.length === 1
                ? "Upload completed successfully."
                : `${completed} files uploaded successfully.`;

        selectedFiles = [];
        document.getElementById("fileInput").value = "";

        updateUploadUI();

        if (currentPath.toLowerCase() === "uploads") {
            loadFiles(currentPath);
        }
    } else {
        status.className = "status error";
        status.textContent =
            `${completed} of ${filesToUpload.length} files uploaded. Failed: ${failed.join(", ")}`;

        // Keep failed files in the selection so the user can retry them.
        selectedFiles = filesToUpload.filter(
            file => failed.includes(file.name)
        );

        const input = document.getElementById("fileInput");
        const dataTransfer = new DataTransfer();
        selectedFiles.forEach(file => dataTransfer.items.add(file));
        input.files = dataTransfer.files;

        // Re-render failed files as selectable files for retry.
        updateUploadUI();
    }

    button.disabled = selectedFiles.length === 0;
}


</script>

</body>

</html>
""", "text/html");
});


app.Run();

// ============================================================
// THROTTLED UPLOAD
// ============================================================

static async Task CopyWithThrottleAsync(
    Stream input,
    Stream output,
    SharedBandwidthLimiter limiter)
{
    const int bufferSize = 64 * 1024;

    var buffer =
        new byte[bufferSize];

    int bytesRead;

    while ((bytesRead =
        await input.ReadAsync(
            buffer,
            0,
            buffer.Length)) > 0)
    {
        await limiter.WaitAsync(bytesRead);

        await output.WriteAsync(
            buffer,
            0,
            bytesRead);
    }
}


class UploadMetadata
{
    public string Uploader { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}

// ============================================================
// SHARED BANDWIDTH LIMITER
// ============================================================

class SharedBandwidthLimiter
{
    private readonly long _bytesPerSecond;

    private readonly object _lock = new();

    private DateTime _nextAvailableTime =
        DateTime.UtcNow;

    public SharedBandwidthLimiter(
        long bytesPerSecond)
    {
        _bytesPerSecond = bytesPerSecond;
    }

    public async Task WaitAsync(int bytes)
    {
        if (bytes <= 0)
            return;

        TimeSpan delay;

        lock (_lock)
        {
            var now = DateTime.UtcNow;

            if (_nextAvailableTime < now)
                _nextAvailableTime = now;

            var transferTime =
                TimeSpan.FromSeconds(
                    (double)bytes /
                    _bytesPerSecond);

            var startTime =
                _nextAvailableTime;

            _nextAvailableTime =
                startTime + transferTime;

            delay =
                startTime > now
                    ? startTime - now
                    : TimeSpan.Zero;
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);
    }
}


// ============================================================
// THROTTLED DOWNLOAD STREAM
// ============================================================

class ThrottledReadStream : Stream
{
    private readonly Stream _inner;
    private readonly SharedBandwidthLimiter _limiter;

    public ThrottledReadStream(
        Stream inner,
        SharedBandwidthLimiter limiter)
    {
        _inner = inner;
        _limiter = limiter;
    }

    public override bool CanRead =>
        _inner.CanRead;

    public override bool CanSeek =>
        _inner.CanSeek;

    public override bool CanWrite =>
        false;

    public override long Length =>
        _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() =>
        _inner.Flush();

    public override Task FlushAsync(
        CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    public override int Read(
        byte[] buffer,
        int offset,
        int count)
    {
        var bytesRead =
            _inner.Read(
                buffer,
                offset,
                count);

        if (bytesRead > 0)
        {
            _limiter
                .WaitAsync(bytesRead)
                .GetAwaiter()
                .GetResult();
        }

        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var bytesRead =
            await _inner.ReadAsync(
                buffer,
                cancellationToken);

        if (bytesRead > 0)
        {
            await _limiter.WaitAsync(bytesRead);
        }

        return bytesRead;
    }

    public override long Seek(
        long offset,
        SeekOrigin origin) =>
        _inner.Seek(offset, origin);

    public override void SetLength(
        long value) =>
        _inner.SetLength(value);

    public override void Write(
        byte[] buffer,
        int offset,
        int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();

        base.Dispose(disposing);
    }
}