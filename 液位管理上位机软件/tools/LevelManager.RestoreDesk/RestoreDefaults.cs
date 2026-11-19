using System.Text.Json;

namespace Naide.LevelManager.RestoreDesk;

internal static class RestoreDefaults
{
    public static string? FindDataRoot(string[] args)
    {
        var index = Array.IndexOf(args, "--data-root");
        if (index >= 0) return index + 1 < args.Length && Path.IsPathFullyQualified(args[index + 1]) ? Path.GetFullPath(args[index + 1]) : null;
        foreach (var file in new[] { Path.Combine(AppContext.BaseDirectory, "installation.json"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Naide", "LevelManager", "installation.json") })
        {
            if (!File.Exists(file)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file).TrimStart('\uFEFF'));
                foreach (var p in document.RootElement.EnumerateObject())
                    if (p.Name.Equals("DataRoot", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } root && Path.IsPathFullyQualified(root) && File.Exists(Path.Combine(root, "level-manager.db"))) return Path.GetFullPath(root);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Naide", "LevelManager");
        return File.Exists(Path.Combine(standard, "level-manager.db")) ? standard : null;
    }
}
