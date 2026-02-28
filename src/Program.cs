using System.Diagnostics;
using System.Runtime.InteropServices;
using PstWeb.Models;

using PstWeb.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// ── macOS-friendly default paths ──────────────────────────────────────────────
var home     = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var appData  = Path.Combine(home, "Documents", "PST Explorer");
var pstDir   = builder.Configuration["PST_DIR"] ?? Path.Combine(appData, "pst_files");
var exportDir = builder.Configuration["EXPORT_DIR"] ?? Path.Combine(appData, "exports");
Directory.CreateDirectory(pstDir);
Directory.CreateDirectory(exportDir);

// Inject paths into configuration so services can read them
builder.Configuration["PST_DIR"]    = pstDir;
builder.Configuration["EXPORT_DIR"] = exportDir;

// Port — use PORT env var or default to 9090
var port = builder.Configuration["PORT"] ?? "9090";
builder.WebHost.UseUrls($"http://localhost:{port}");

// Increase upload size limit (default is 28 MB — PSTs are large)
builder.WebHost.ConfigureKestrel(opts =>
    opts.Limits.MaxRequestBodySize = 10L * 1024 * 1024 * 1024); // 10 GB

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(opts =>
    opts.MultipartBodyLengthLimit = 10L * 1024 * 1024 * 1024); // 10 GB per file

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<PstService>();
builder.Services.AddSingleton<PdfService>();
// CORS: only allow requests from the local server itself (same-origin)
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins($"http://localhost:{port}", $"http://127.0.0.1:{port}")
     .AllowAnyHeader()
     .AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

var pst = app.Services.GetRequiredService<PstService>();
var pdf = app.Services.GetRequiredService<PdfService>();

// ── API ───────────────────────────────────────────────────────────────────────

app.MapGet("/api/pst/files", () =>
    Results.Ok(pst.ListFiles()));

app.MapGet("/api/pst/status", () =>
    Results.Ok(pst.Status()));

app.MapPost("/api/pst/open", async (OpenRequest req) =>
{
    var files = req.Filenames ?? new List<string>();
    if (!string.IsNullOrEmpty(req.Filename) && !files.Contains(req.Filename))
        files.Add(req.Filename);

    var (success, msg) = await pst.OpenAsync(files);
    return success ? Results.Ok(new { message = msg }) : Results.BadRequest(new { error = msg });
});

app.MapGet("/api/pst/folders", async () =>
{
    var tree = await pst.GetFolderTreeAsync();
    return tree is null ? Results.BadRequest(new { error = "No PST open" }) : Results.Ok(tree);
});

app.MapGet("/api/pst/messages", async (
    string? folder,
    string? search,
    int     page     = 1,
    int     pageSize = 100,
    bool    grouped  = false) =>
{
    page     = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 10, 500);
    var (items, total) = await pst.GetMessagesAsync(folder, search, page, pageSize, grouped);
    return Results.Ok(new { items, total, page, pageSize });
});

app.MapGet("/api/pst/preview/{id:int}", async (int id) =>
{
    var preview = await pst.GetPreviewAsync(id);
    return preview is null ? Results.NotFound() : Results.Ok(preview);
});

app.MapPost("/api/pst/export", async (ExportRequest req) =>
{
    if (req.Ids.Count == 0)
        return Results.BadRequest(new { error = "No messages selected" });
    if (req.Ids.Count > 5000)
        return Results.BadRequest(new { error = "Too many messages selected (max 5000)" });

    try
    {
        var (zipBytes, _) = await pdf.ExportAsync(req.Ids, req.ZipName, req.GroupByChain);
        var zipName = (req.ZipName ?? $"export_{DateTime.UtcNow:yyyyMMdd_HHmmss}") + ".zip";
        return Results.File(zipBytes, "application/zip", zipName);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Export failed");
        return Results.Problem("Export failed. Check the application log for details.");
    }
});

// Single-message PDF download — GET /api/pst/export/{id}/pdf
app.MapGet("/api/pst/export/{id:int}/pdf", async (int id) =>
{
    try
    {
        var result = await pdf.ExportSingleAsync(id);
        if (result is null) return Results.NotFound(new { error = "Message not found" });
        var (bytes, filename, contentType) = result.Value;
        return Results.File(bytes, contentType, filename);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Single PDF export failed for message {Id}", id);
        return Results.Problem("Export failed. Check the application log for details.");
    }
});

// ── Upload endpoint ───────────────────────────────────────────────────────────
// Streams directly to disk — no full file buffering in memory.
// Accepts only .pst and .ost. Sanitizes filename. Renames on collision.
app.MapPost("/api/pst/upload", async (HttpRequest request, ILogger<Program> log, IConfiguration cfg) =>
{
    var dir = cfg["PST_DIR"]!;
    Directory.CreateDirectory(dir);

    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Expected multipart/form-data" });

    var form = await request.ReadFormAsync();

    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "No file provided" });

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext != ".pst" && ext != ".ost")
        return Results.BadRequest(new { error = "Only .pst and .ost files are accepted" });

    var safeName = Path.GetFileName(file.FileName); // strips directory traversal
    if (string.IsNullOrWhiteSpace(safeName))
        return Results.BadRequest(new { error = "Invalid filename" });

    var destPath = Path.Combine(dir, safeName);
    if (File.Exists(destPath))
    {
        var ts   = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var stem = Path.GetFileNameWithoutExtension(safeName);
        safeName = $"{stem}_{ts}{ext}";
        destPath = Path.Combine(dir, safeName);
    }

    log.LogInformation("Upload starting: {Name} ({Size:N0} bytes)", safeName, file.Length);
    await using var dest = File.Create(destPath);
    await file.CopyToAsync(dest);
    log.LogInformation("Upload complete: {Path}", destPath);

    return Results.Ok(new { filename = safeName, size = file.Length });
}).DisableRequestTimeout();

// ── Delete endpoint ───────────────────────────────────────────────────────────
// Refuses to delete the currently open file.
app.MapDelete("/api/pst/files/{filename}", (string filename, ILogger<Program> log, IConfiguration cfg) =>
{
    var dir      = cfg["PST_DIR"]!;
    var safeName = Path.GetFileName(filename);
    var fullPath = Path.Combine(dir, safeName);

    if (!File.Exists(fullPath))
        return Results.NotFound(new { error = "File not found" });

    var s = pst.Status();
    if (s.IsOpen && (s.Filenames?.Contains(safeName, StringComparer.OrdinalIgnoreCase) == true))
        return Results.Conflict(new { error = "Cannot delete a currently open file. Close it first." });

    File.Delete(fullPath);
    log.LogInformation("Deleted: {Path}", fullPath);
    return Results.Ok(new { deleted = safeName });
});










app.MapFallbackToFile("index.html");

// ── Log startup info (browser is opened by the .app launcher script) ──────────
app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = $"http://localhost:{port}";
    Log.Information("PST Explorer is running at {Url}", url);
    Log.Information("PST files:  {PstDir}", pstDir);
    Log.Information("Exports:    {ExportDir}", exportDir);
});

app.Run();
