using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.RegularExpressions;
using PstWeb.Models;
using XstReader;
using XstReader.ElementProperties;

namespace PstWeb.Services;

/// <summary>
/// Singleton that holds one open PST/OST file at a time.
/// Thread-safety: a SemaphoreSlim gates all XstReader access since the library
/// is not documented as thread-safe.
/// </summary>
public sealed class PstService : IDisposable
{
    private readonly ILogger<PstService> _log;
    private readonly string _pstDir;

    private readonly List<XstFile> _xstFiles = new();
    private List<string> _openFilenames = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Sequential-ID → lightweight index entry (holds the XstMessage reference)
    private readonly ConcurrentDictionary<int, MessageIndexEntry> _index = new();
    private int _nextId;

    public PstService(ILogger<PstService> log, IConfiguration cfg)
    {
        _log    = log;
        _pstDir = cfg["PST_DIR"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "PST Explorer", "pst_files");
        Directory.CreateDirectory(_pstDir);
    }

    // ── File listing ──────────────────────────────────────────────────────────

    public IEnumerable<PstFileInfo> ListFiles() =>
        Directory.Exists(_pstDir)
            ? Directory.EnumerateFiles(_pstDir, "*.pst", SearchOption.TopDirectoryOnly)
                       .Concat(Directory.EnumerateFiles(_pstDir, "*.ost", SearchOption.TopDirectoryOnly))
                       .Select(p => new FileInfo(p))
                       .OrderBy(fi => fi.Name)
                       .Select(fi => new PstFileInfo(fi.Name, fi.Length))
            : Enumerable.Empty<PstFileInfo>();

    // ── Open / close ──────────────────────────────────────────────────────────

    public async Task<(bool success, string message)> OpenAsync(List<string> filenames)
    {
        var validPaths = new List<string>();
        foreach(var filename in filenames)
        {
            var fullPath = Path.Combine(_pstDir, Path.GetFileName(filename)); // prevent path traversal
            if (!File.Exists(fullPath))
                return (false, $"File not found: {filename}");
            validPaths.Add(fullPath);
        }

        await _lock.WaitAsync();
        try
        {
            CloseInternal();

            for (int i = 0; i < filenames.Count; i++)
            {
                var fullPath = validPaths[i];
                var filename = filenames[i];
                _log.LogInformation("Opening PST: {Path}", fullPath);
                _xstFiles.Add(new XstFile(fullPath));
                _openFilenames.Add(filename);
            }

            await Task.Run(BuildIndex);   // index on background thread

            _log.LogInformation("Indexed {Count} messages from {FileCount} files", _index.Count, _xstFiles.Count);
            return (true, $"Opened {_xstFiles.Count} file(s), indexed {_index.Count:N0} messages");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open PST(s)");
            CloseInternal();
            return (false, $"Error: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    public PstStatusResponse Status() => new(
        IsOpen:        _xstFiles.Count > 0,
        Filename:      _openFilenames.FirstOrDefault(),
        Filenames:     _openFilenames,
        TotalMessages: _index.Count
    );

    // ── Folder tree ───────────────────────────────────────────────────────────

    public async Task<List<FolderDto>?> GetFolderTreeAsync()
    {
        if (_xstFiles.Count == 0) return null;

        await _lock.WaitAsync();
        try
        {
            if (_xstFiles.Count == 1)
            {
                return BuildFolderTree(_xstFiles[0].RootFolder, "").Children;
            }

            var roots = new List<FolderDto>();
            for (int i = 0; i < _xstFiles.Count; i++)
            {
                var filename = _openFilenames[i];
                var file = _xstFiles[i];
                var tree = BuildFolderTree(file.RootFolder, $"/{filename}");
                // Override the root name to be the filename itself
                roots.Add(new FolderDto(
                    Id: $"/{filename}",
                    Name: filename,
                    Path: $"/{filename}",
                    MessageCount: tree.MessageCount,
                    Children: tree.Children
                ));
            }
            return roots;
        }
        finally { _lock.Release(); }
    }

    private static FolderDto BuildFolderTree(XstFolder folder, string prefix)
    {
        var path = folder.Path ?? folder.DisplayName;
        // When prefix is set, folder.Path may start with the PST filename — strip it to avoid duplication
        if (!string.IsNullOrEmpty(prefix) && path.StartsWith(prefix.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
            path = path[prefix.TrimStart('/').Length..];
        var fullPath = string.IsNullOrEmpty(prefix) ? path : $"{prefix}{path}";

        var children = folder.Folders
            .Select(f => BuildFolderTree(f, prefix))
            .ToList();

        return new FolderDto(
            Id:           fullPath,
            Name:         folder.DisplayName,
            Path:         fullPath,
            MessageCount: folder.ContentCount,
            Children:     children
        );
    }

    // ── Message listing / search ──────────────────────────────────────────────

    public async Task<(List<MessageDto> Items, int Total)> GetMessagesAsync(
        string?  folderPath,
        string?  search,
        int      page,
        int      pageSize,
        bool     grouped = false)
    {
        if (_xstFiles.Count == 0) return ([], 0);

        var query = _index.Values.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(folderPath))
            query = query.Where(m => m.FolderPath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Support regex if wrapped in / /
            if (search.StartsWith('/') && search.EndsWith('/') && search.Length > 2)
            {
                var pattern = search[1..^1];
                try
                {
                    var rx = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                    query  = query.Where(m => { try { return rx.IsMatch(m.Subject) || rx.IsMatch(m.From) || rx.IsMatch(m.To); } catch (RegexMatchTimeoutException) { return false; } });
                }
                catch { /* fall back to substring on bad regex */
                    query = query.Where(m => m.Subject.Contains(search, StringComparison.OrdinalIgnoreCase) || m.From.Contains(search, StringComparison.OrdinalIgnoreCase) || m.To.Contains(search, StringComparison.OrdinalIgnoreCase));
                }
            }
            else
            {
                query = query.Where(m => m.Subject.Contains(search, StringComparison.OrdinalIgnoreCase) || m.From.Contains(search, StringComparison.OrdinalIgnoreCase) || m.To.Contains(search, StringComparison.OrdinalIgnoreCase));
            }
        }

        var filtered = query.OrderByDescending(m => m.Date).ToList();

        // Build per-conversation chain info from the filtered set
        var convGroups = filtered.GroupBy(m => m.ConversationId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Date).ToList());

        List<MessageIndexEntry> displayList;
        if (grouped)
        {
            // Show only the newest message per conversation
            displayList = convGroups.Values
                .Select(g => g.First())
                .OrderByDescending(m => m.Date)
                .ToList();
        }
        else
        {
            displayList = filtered;
        }

        var total = displayList.Count;
        var page_items = displayList
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e =>
            {
                var chain = convGroups.TryGetValue(e.ConversationId, out var g) ? g : [e];
                return new MessageDto(
                    e.Id, e.Subject, e.From, e.To, e.Date,
                    e.FolderPath, e.HasAttachments, e.AttachmentCount,
                    e.ConversationId,
                    chain.Count,
                    chain.Count > 1 ? chain.Select(c => c.Id).ToList() : null);
            })
            .ToList();

        return (page_items, total);
    }

    // ── Preview ───────────────────────────────────────────────────────────────

    public async Task<PreviewResponse?> GetPreviewAsync(int id)
    {
        if (!_index.TryGetValue(id, out var entry)) return null;

        await _lock.WaitAsync();
        try
        {
            var msg = entry.Message;
            var html = GetHtmlBody(msg);

            var attachments = msg.Attachments
                .Where(a => a.IsFile)
                .Select(a => a.FileName ?? "(unnamed)")
                .ToList();

            var recipients = msg.Recipients.Items
                .Where(r => r.RecipientType == RecipientType.To)
                .Select(r => r.DisplayName ?? r.Address ?? "")
                .Where(r => !string.IsNullOrEmpty(r));

            return new PreviewResponse(
                Subject:     entry.Subject,
                From:        entry.From,
                To:          string.Join(", ", recipients),
                Cc:          "",  // XstReader.Api exposes Recipients; CC filtering requires checking RecipientType
                Date:        entry.Date,
                FolderPath:  entry.FolderPath,
                HtmlBody:    html,
                Attachments: attachments
            );
        }
        finally { _lock.Release(); }
    }

    // ── Export helpers (called by PdfService) ─────────────────────────────────

    public async Task<List<ExportMessageData>> GetMessagesForExportAsync(List<int> ids)
    {
        var result = new List<ExportMessageData>();
        if (_xstFiles.Count == 0) return result;

        await _lock.WaitAsync();
        try
        {
            foreach (var id in ids)
            {
                if (!_index.TryGetValue(id, out var entry)) continue;
                var attachments = ExtractAttachments(entry.Message);
                result.Add(new ExportMessageData(entry, GetHtmlBody(entry.Message), attachments));
            }
        }
        finally { _lock.Release(); }

        return result;
    }

    private static List<ExportAttachment> ExtractAttachments(XstMessage msg)
    {
        var list = new List<ExportAttachment>();
        try
        {
            foreach (var att in msg.Attachments.Where(a => a.IsFile && !a.IsInlineAttachment))
            {
                try
                {
                    using var ms = new MemoryStream();
                    att.SaveToStream(ms);
                    list.Add(new ExportAttachment(
                        att.FileName ?? att.LongFileName ?? $"attachment_{list.Count}",
                        ms.ToArray()
                    ));
                }
                catch { /* skip unreadable attachments */ }
            }
        }
        catch { }
        return list;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void BuildIndex()
    {
        _index.Clear();
        _nextId = 0;

        for (int i = 0; i < _xstFiles.Count; i++)
        {
            var file = _xstFiles[i];
            var filename = _openFilenames[i];
            var prefix = _xstFiles.Count > 1 ? $"/{filename}" : "";

            foreach (var folder in AllFolders(file.RootFolder))
            {
                var basePath = folder.Path ?? folder.DisplayName;
                // When prefix is set, folder.Path may start with the PST filename — strip it to avoid duplication
                if (!string.IsNullOrEmpty(prefix) && basePath.StartsWith(prefix.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
                    basePath = basePath[prefix.TrimStart('/').Length..];
                var folderPath = string.IsNullOrEmpty(prefix) ? basePath : $"{prefix}{basePath}";

                foreach (var msg in folder.Messages)
                {
                    var id    = Interlocked.Increment(ref _nextId);
                    var from  = ExtractFrom(msg);
                    var attCount = msg.Attachments.Count();

                    _index[id] = new MessageIndexEntry
                    {
                        Id              = id,
                        Subject         = msg.Subject ?? "(no subject)",
                        From            = from,
                        To              = "",
                        Date            = msg.Date?.ToString("yyyy-MM-dd HH:mm") ?? "",
                        FolderPath      = folderPath,
                        HasAttachments  = attCount > 0,
                        AttachmentCount = attCount,
                        ConversationId  = ExtractConversationId(msg),
                        Message         = msg
                    };
                }
            }
        }
    }

    private static IEnumerable<XstFolder> AllFolders(XstFolder root)
    {
        yield return root;
        foreach (var child in root.Folders)
        foreach (var f in AllFolders(child))
            yield return f;
    }

    private static string ExtractFrom(XstMessage msg)
    {
        try
        {
            var from = msg.From;
            if (!string.IsNullOrEmpty(from)) return from;
        }
        catch { }

        try
        {
            var sender = msg.Recipients.Sender;
            if (sender is not null)
                return sender.DisplayName ?? sender.Address ?? "";
        }
        catch { }

        return "(unknown sender)";
    }

    private static string ExtractConversationId(XstMessage msg)
    {
        try
        {
            var prop = msg.Properties?.Get(PropertyCanonicalName.PidTagConversationId);
            if (prop?.Value is byte[] bytes && bytes.Length > 0)
                return Convert.ToBase64String(bytes);
        }
        catch { }

        // Fallback: normalize subject by stripping Re:/Fwd: prefixes
        var subj = msg.Subject ?? "";
        subj = Regex.Replace(subj, @"^(\s*(Re|Fwd?|Fw)\s*:\s*)+", "", RegexOptions.IgnoreCase).Trim();
        return $"subj:{subj}";
    }

    /// <summary>
    /// Given a message ID, returns the IDs of all messages in the same conversation chain.
    /// </summary>
    public List<int> GetChainIds(int id)
    {
        if (!_index.TryGetValue(id, out var entry)) return [id];
        var convId = entry.ConversationId;
        if (string.IsNullOrEmpty(convId)) return [id];

        return _index.Values
            .Where(e => e.ConversationId == convId)
            .OrderByDescending(e => e.Date)
            .Select(e => e.Id)
            .ToList();
    }

    private static string GetHtmlBody(XstMessage msg)
    {
        try
        {
            var body = msg.Body;
            if (body is null) return "<p><em>(no body)</em></p>";

            return body.Format switch
            {
                XstMessageBodyFormat.Html      => body.Text ?? "",
                XstMessageBodyFormat.PlainText => $"<pre style='white-space:pre-wrap;font-family:inherit'>{HtmlEncode(body.Text)}</pre>",
                XstMessageBodyFormat.Rtf       => $"<p><em>[RTF body — {body.Bytes?.Length ?? 0} bytes, preview unavailable]</em></p>",
                _                              => "<p><em>(unknown format)</em></p>"
            };
        }
        catch (Exception ex)
        {
            return $"<p><em>Error reading body: {ex.Message}</em></p>";
        }
    }

    private static string HtmlEncode(string? s) =>
        System.Web.HttpUtility.HtmlEncode(s ?? "");

    private void CloseInternal()
    {
        _index.Clear();
        _nextId       = 0;
        _openFilenames.Clear();

        foreach(var f in _xstFiles)
        {
            try { f.Dispose(); } catch { }
        }
        _xstFiles.Clear();
    }

    public void Dispose()
    {
        _lock.Dispose();
        CloseInternal();
    }
}
