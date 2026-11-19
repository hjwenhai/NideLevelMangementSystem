namespace Naide.LevelManager.Desktop;

// Only a completed download selected in the native list may reach these helpers.
// No path or shell command is accepted from page messages.
internal static class DownloadFilePolicy
{
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { ".xlsx", ".csv", ".zip", ".json", ".png", ".pdf", ".txt" };

    public static bool TryExistingFile(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return false;
            normalized = full;
            return true;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return false; }
    }

    public static bool CanOpenDocument(string path) => Documents.Contains(Path.GetExtension(path));

    public static bool IsAttachmentNavigation(Uri origin,string value)
    {
        if(!TrustedDownload(origin,value)||!Uri.TryCreate(value,UriKind.Absolute,out var uri))return false;
        var path=uri.AbsolutePath;
        return path.StartsWith("/templates/",StringComparison.Ordinal)&&path.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase)
            ||path.StartsWith("/api/",StringComparison.Ordinal)&&(path.EndsWith("/export",StringComparison.Ordinal)||path.EndsWith("/download",StringComparison.Ordinal)||path=="/api/authorization/request");
    }
    public static bool KeepDocumentAfterCancellation(bool documentLoaded,bool operationCanceled,bool connectionAborted,bool attachmentNavigation)
        =>documentLoaded&&(operationCanceled||connectionAborted&&attachmentNavigation);

    public static bool TrustedDownload(Uri origin, string value)
    {
        if (value.StartsWith("blob:", StringComparison.Ordinal)) value = value[5..];
        if (value.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == origin.Scheme
            && uri.Authority == origin.Authority && string.IsNullOrEmpty(uri.UserInfo);
    }
}
