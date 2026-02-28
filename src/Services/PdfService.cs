using System.IO.Compression;
using System.Text;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using PstWeb.Models;

namespace PstWeb.Services;

public sealed class PdfService : IAsyncDisposable
{
    private readonly PstService _pst;
    private readonly ILogger<PdfService> _log;
    private readonly string _exportDir;

    private IBrowser? _browser;
    private readonly SemaphoreSlim _browserLock = new(1, 1);

    public PdfService(PstService pst, ILogger<PdfService> log, IConfiguration cfg)
    {
        _pst       = pst;
        _log       = log;
        _exportDir = cfg["EXPORT_DIR"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "PST Explorer", "exports");
        Directory.CreateDirectory(_exportDir);
    }

    // ── Well-known Chromium-based browser paths on macOS ──────────────────────
    private static readonly string[] ChromePaths =
    [
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
        "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        "/Applications/Chromium.app/Contents/MacOS/Chromium",
        "/Applications/Vivaldi.app/Contents/MacOS/Vivaldi",
        "/Applications/Arc.app/Contents/MacOS/Arc",
        // Home-directory installs
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Applications/Google Chrome.app/Contents/MacOS/Google Chrome"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Applications/Brave Browser.app/Contents/MacOS/Brave Browser"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"),
    ];

    /// <summary>
    /// Find an installed Chromium-based browser on the host, or return null.
    /// </summary>
    private static string? FindInstalledChrome()
    {
        foreach (var path in ChromePaths)
            if (File.Exists(path))
                return path;
        return null;
    }

    /// <summary>
    /// Lazily connect to a headless browser for PDF generation.
    /// Prefers an already-installed Chrome/Brave/Edge on the host so we never
    /// download a separate ~170 MB Chromium bundle.
    /// Falls back to PuppeteerSharp's built-in download only as a last resort.
    /// </summary>
    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is not null && !_browser.IsClosed) return _browser;

        await _browserLock.WaitAsync();
        try
        {
            if (_browser is not null && !_browser.IsClosed) return _browser;

            var chromePath = FindInstalledChrome();

            if (chromePath is not null)
            {
                _log.LogInformation("Using installed browser for PDF: {Path}", chromePath);
                _browser = await Puppeteer.LaunchAsync(new LaunchOptions
                {
                    Headless = true,
                    ExecutablePath = chromePath,
                    Args = new[]
                    {
                        "--no-sandbox", "--disable-setuid-sandbox", "--disable-gpu",
                        "--disable-extensions", "--disable-sync",
                        "--no-first-run", "--no-default-browser-check"
                    }
                });
            }
            else
            {
                _log.LogInformation("No installed Chromium browser found — downloading Chromium…");
                var fetcher = new BrowserFetcher();
                await fetcher.DownloadAsync();

                _browser = await Puppeteer.LaunchAsync(new LaunchOptions
                {
                    Headless = true,
                    Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-gpu" }
                });
            }

            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    /// <summary>
    /// Export a single message (or its full chain) to PDF + attachments.
    /// Returns a ZIP if there are attachments, otherwise a bare PDF.
    /// </summary>
    public async Task<(byte[] Bytes, string Filename, string ContentType)?> ExportSingleAsync(int id, bool includeChain = false)
    {
        var ids = includeChain ? _pst.GetChainIds(id) : [id];
        var messages = await _pst.GetMessagesForExportAsync(ids);
        if (messages.Count == 0) return null;

        // Sort chronologically (oldest first) for chain readability
        messages = messages.OrderBy(m => m.Entry.Date).ToList();

        var pdfBytes = await BuildChainPdfAsync(messages);
        var topic    = messages.Last().Entry.Subject;
        var baseName = SanitizeName(topic);

        var allAttachments = messages.SelectMany(m => m.Attachments).ToList();
        if (allAttachments.Count == 0)
            return (pdfBytes, baseName + ".pdf", "application/pdf");

        // Bundle PDF + all attachments into a ZIP
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var ze = archive.CreateEntry(baseName + ".pdf", CompressionLevel.Optimal);
            using (var s = ze.Open()) await s.WriteAsync(pdfBytes);

            var usedAttNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var att in allAttachments)
            {
                var attName  = UniqueFileName(att.FileName, usedAttNames);
                var attEntry = archive.CreateEntry("attachments/" + attName, CompressionLevel.Optimal);
                using var s = attEntry.Open();
                await s.WriteAsync(att.Data);
            }
        }
        return (zipStream.ToArray(), baseName + ".zip", "application/zip");
    }

    /// <summary>
    /// Export message IDs to ZIP. When groupByChain is true, messages sharing
    /// a conversation are merged into one combined PDF with a single attachments folder.
    /// </summary>
    public async Task<(byte[] ZipBytes, string SavedPath)> ExportAsync(
        List<int> ids, string? zipName = null, bool groupByChain = false)
    {
        if (ids.Count == 0) throw new ArgumentException("No message IDs provided.");

        _log.LogInformation("Exporting {Count} message(s), groupByChain={Group}", ids.Count, groupByChain);

        var messages = await _pst.GetMessagesForExportAsync(ids);
        if (messages.Count == 0) throw new InvalidOperationException("None of the requested IDs could be found.");

        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (groupByChain)
            {
                // Group messages by conversation, each group → one PDF + one attachments/ folder
                var groups = messages
                    .GroupBy(m => m.Entry.ConversationId)
                    .ToList();

                var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in groups)
                {
                    var chain = group.OrderBy(m => m.Entry.Date).ToList();
                    var topic = chain.Last().Entry.Subject;
                    var folderName = SafeFolderName(topic, chain.Last().Entry.Date, usedFolders);

                    // Combined chain PDF
                    var pdfBytes = await BuildChainPdfAsync(chain);
                    var pdfEntry = archive.CreateEntry($"{folderName}/conversation.pdf", CompressionLevel.Optimal);
                    using (var s = pdfEntry.Open()) await s.WriteAsync(pdfBytes);

                    // All attachments from all messages in one flat folder
                    var usedAttNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var att in chain.SelectMany(m => m.Attachments))
                    {
                        var attName  = UniqueFileName(att.FileName, usedAttNames);
                        var attEntry = archive.CreateEntry($"{folderName}/attachments/{attName}", CompressionLevel.Optimal);
                        using var s = attEntry.Open();
                        await s.WriteAsync(att.Data);
                    }
                }
            }
            else
            {
                // Original behavior: one PDF per message
                var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var msg in messages)
                {
                    var folderName = SafeFolderName(msg.Entry.Subject, msg.Entry.Date, usedFolders);

                    var pdfBytes = await ConvertToPdfAsync(msg.Entry, msg.Html);
                    var pdfEntry = archive.CreateEntry($"{folderName}/email.pdf", CompressionLevel.Optimal);
                    using (var s = pdfEntry.Open()) await s.WriteAsync(pdfBytes);

                    var usedAttNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var att in msg.Attachments)
                    {
                        var attName  = UniqueFileName(att.FileName, usedAttNames);
                        var attEntry = archive.CreateEntry($"{folderName}/attachments/{attName}", CompressionLevel.Optimal);
                        using var s = attEntry.Open();
                        await s.WriteAsync(att.Data);
                    }
                }
            }
        }

        var zipBytes = zipStream.ToArray();

        var fileName = SanitizeName(zipName ?? $"export_{DateTime.UtcNow:yyyyMMdd_HHmmss}") + ".zip";
        var savePath = Path.Combine(_exportDir, fileName);
        await File.WriteAllBytesAsync(savePath, zipBytes);

        _log.LogInformation("Saved export to {Path} ({Count} messages)", savePath, messages.Count);
        return (zipBytes, savePath);
    }

    // ── PDF rendering ─────────────────────────────────────────────────────────

    /// <summary>
    /// Render multiple messages into a single combined PDF (chain/conversation view).
    /// Each message gets its own header block, separated by a horizontal rule.
    /// </summary>
    private async Task<byte[]> BuildChainPdfAsync(List<ExportMessageData> chain)
    {
        if (chain.Count == 1)
            return await ConvertToPdfAsync(chain[0].Entry, chain[0].Html);

        var sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8"/>
              <style>
                body        { font-family: -apple-system, BlinkMacSystemFont, Arial, sans-serif; font-size: 13px; color: #222; }
                .msg        { margin-bottom: 24px; }
                .msg-header { border-bottom: 2px solid #333; padding-bottom: 10px; margin-bottom: 14px; }
                .header-row { display: flex; gap: 8px; margin: 3px 0; }
                .label      { font-weight: bold; min-width: 80px; color: #555; }
                .subject    { font-size: 16px; font-weight: bold; margin-bottom: 8px; color: #111; }
                .badge      { display: inline-block; background: #e8f0fe; color: #1a73e8;
                              border-radius: 4px; padding: 2px 8px; font-size: 11px; margin-left: 6px; }
                .chain-title { font-size: 20px; font-weight: bold; color: #111; margin-bottom: 6px; }
                .chain-meta  { font-size: 12px; color: #555; margin-bottom: 20px; border-bottom: 3px double #333; padding-bottom: 14px; }
                .msg-sep    { border: none; border-top: 1px dashed #ccc; margin: 20px 0; }
                .body       { margin-top: 8px; }
                img         { max-width: 100%; }
              </style>
            </head>
            <body>
            """);

        var topic = chain.Last().Entry.Subject;
        sb.Append($"<div class=\"chain-title\">{Encode(topic)}</div>");
        sb.Append($"<div class=\"chain-meta\">{chain.Count} messages in conversation &middot; {Encode(chain.First().Entry.Date)} to {Encode(chain.Last().Entry.Date)}</div>");

        for (var i = 0; i < chain.Count; i++)
        {
            var entry = chain[i].Entry;
            var html  = chain[i].Html;

            if (i > 0) sb.Append("<hr class=\"msg-sep\"/>");

            sb.Append("<div class=\"msg\"><div class=\"msg-header\">");
            sb.Append($"<div class=\"subject\">{Encode(entry.Subject)}</div>");
            sb.Append($"<div class=\"header-row\"><span class=\"label\">From:</span><span>{Encode(entry.From)}</span></div>");
            if (!string.IsNullOrEmpty(entry.To))
                sb.Append($"<div class=\"header-row\"><span class=\"label\">To:</span><span>{Encode(entry.To)}</span></div>");
            sb.Append($"<div class=\"header-row\"><span class=\"label\">Date:</span><span>{Encode(entry.Date)}</span></div>");
            if (entry.HasAttachments)
                sb.Append($"<div class=\"header-row\"><span class=\"label\">Attachments:</span><span>{entry.AttachmentCount} file(s) <span class=\"badge\">included in ZIP</span></span></div>");
            sb.Append("</div>");
            sb.Append($"<div class=\"body\">{html}</div>");
            sb.Append("</div>");
        }

        sb.Append("</body></html>");
        return await ConvertHtmlToPdfAsync(sb.ToString());
    }

    private async Task<byte[]> ConvertToPdfAsync(MessageIndexEntry entry, string htmlBody) =>
        await ConvertHtmlToPdfAsync(BuildEmailHtml(entry, htmlBody));

    private async Task<byte[]> ConvertHtmlToPdfAsync(string fullHtml)
    {
        var browser = await GetBrowserAsync();
        await using var page = await browser.NewPageAsync();

        await page.SetContentAsync(fullHtml, new NavigationOptions
        {
            WaitUntil = new[] { WaitUntilNavigation.Networkidle0 }
        });

        var pdfBytes = await page.PdfDataAsync(new PdfOptions
        {
            Format        = PaperFormat.A4,
            PrintBackground = true,
            MarginOptions = new MarginOptions
            {
                Top    = "15mm",
                Bottom = "15mm",
                Left   = "15mm",
                Right  = "15mm"
            },
            DisplayHeaderFooter = true,
            HeaderTemplate = "<div style='font-size:8px;width:100%;text-align:right;padding-right:15mm;'><span class='pageNumber'></span> of <span class='totalPages'></span></div>",
            FooterTemplate = $"<div style='font-size:8px;width:100%;text-align:center;'>Exported {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC</div>"
        });

        return pdfBytes;
    }

    private static string BuildEmailHtml(MessageIndexEntry entry, string body)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8"/>
              <style>
                body        { font-family: -apple-system, BlinkMacSystemFont, Arial, sans-serif; font-size: 13px; color: #222; }
                .header     { border-bottom: 2px solid #333; padding-bottom: 12px; margin-bottom: 18px; }
                .header-row { display: flex; gap: 8px; margin: 3px 0; }
                .label      { font-weight: bold; min-width: 80px; color: #555; }
                .subject    { font-size: 18px; font-weight: bold; margin-bottom: 10px; color: #111; }
                .badge      { display: inline-block; background: #e8f0fe; color: #1a73e8;
                              border-radius: 4px; padding: 2px 8px; font-size: 11px; margin-left: 6px; }
                .body       { margin-top: 8px; }
                img         { max-width: 100%; }
              </style>
            </head>
            <body>
              <div class="header">
            """);

        sb.Append($"<div class=\"subject\">{Encode(entry.Subject)}</div>");
        sb.Append($"<div class=\"header-row\"><span class=\"label\">From:</span><span>{Encode(entry.From)}</span></div>");
        if (!string.IsNullOrEmpty(entry.To))
            sb.Append($"<div class=\"header-row\"><span class=\"label\">To:</span><span>{Encode(entry.To)}</span></div>");
        sb.Append($"<div class=\"header-row\"><span class=\"label\">Date:</span><span>{Encode(entry.Date)}</span></div>");
        sb.Append($"<div class=\"header-row\"><span class=\"label\">Folder:</span><span>{Encode(entry.FolderPath)}</span></div>");
        if (entry.HasAttachments)
            sb.Append($"<div class=\"header-row\"><span class=\"label\">Attachments:</span><span>{entry.AttachmentCount} attachment(s) <span class=\"badge\">included in ZIP</span></span></div>");

        sb.Append("</div><div class=\"body\">");
        sb.Append(body);
        sb.Append("</div></body></html>");

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SafeFolderName(string subject, string date, HashSet<string> used)
    {
        var datePart = date.Replace(":", "").Replace(" ", "_").Replace("-", "");
        var base_    = $"{datePart}_{SanitizeName(subject)}";
        var name     = base_;
        var i        = 1;
        while (!used.Add(name))
            name = $"{base_}_{i++}";
        return name;
    }

    private static string UniqueFileName(string fileName, HashSet<string> used)
    {
        var name = SanitizeName(Path.GetFileNameWithoutExtension(fileName))
                 + Path.GetExtension(fileName);
        var baseName = name;
        var i = 1;
        while (!used.Add(name))
        {
            var ext = Path.GetExtension(baseName);
            name = $"{Path.GetFileNameWithoutExtension(baseName)}_{i++}{ext}";
        }
        return name;
    }

    private static string SanitizeName(string s)
    {
        var clean = System.Text.RegularExpressions.Regex.Replace(s, @"[^\w\-. ]", "_")
                    .Trim().Replace(' ', '_');
        return clean[..Math.Min(clean.Length, 80)];
    }

    private static string Encode(string? s) =>
        System.Web.HttpUtility.HtmlEncode(s ?? "");

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); } catch { }
            try { _browser.Dispose(); } catch { }
        }
        _browserLock.Dispose();
    }
}
