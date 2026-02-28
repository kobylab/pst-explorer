namespace PstWeb.Models;

// ── API request/response shapes ───────────────────────────────────────────────

public record OpenRequest(List<string>? Filenames, string? Filename);

public record FolderDto(
    string Id,
    string Name,
    string Path,
    int MessageCount,
    List<FolderDto> Children
);

public record MessageDto(
    int    Id,
    string Subject,
    string From,
    string To,
    string Date,
    string FolderPath,
    bool   HasAttachments,
    int    AttachmentCount,
    string ConversationId,
    int    ChainCount,
    List<int>? ChainIds
);

public record PreviewResponse(
    string  Subject,
    string  From,
    string  To,
    string  Cc,
    string  Date,
    string  FolderPath,
    string  HtmlBody,
    List<string> Attachments
);

public record ExportRequest(List<int> Ids, string? ZipName, bool GroupByChain = false);

public record PstStatusResponse(bool IsOpen, string? Filename, List<string>? Filenames, int TotalMessages);


public record PstFileInfo(string Filename, long Size);

// ── Export data types ────────────────────────────────────────────────────────

public record ExportAttachment(string FileName, byte[] Data);
public record ExportMessageData(MessageIndexEntry Entry, string Html, List<ExportAttachment> Attachments);

// ── Internal index entry — holds a lightweight reference per indexed message ──

public class MessageIndexEntry
{
    public int    Id          { get; init; }
    public string Subject     { get; init; } = string.Empty;
    public string From        { get; init; } = string.Empty;
    public string To          { get; init; } = string.Empty;
    public string Date        { get; init; } = string.Empty;
    public string FolderPath  { get; init; } = string.Empty;
    public bool   HasAttachments { get; init; }
    public int    AttachmentCount { get; init; }
    public string ConversationId { get; init; } = string.Empty;

    // Hold the actual XstMessage reference for body/attachment access.
    // XstFile keeps the file open so lazy properties remain readable.
    public XstReader.XstMessage Message { get; init; } = null!;
}
