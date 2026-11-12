using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace DiskSpaceAnalyzer.Services;

public sealed record McpServerSettings
{
    public bool Enabled { get; init; } = true;
    public int Port { get; init; } = 47831;
    public string Token { get; init; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "simplePC", "DiskSpaceAnalyzer", "mcp-settings.json");

    public static McpServerSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<McpServerSettings>(File.ReadAllText(SettingsPath));
            if (settings is { Port: >= 1024 and <= 65535, Token.Length: 64 } &&
                settings.Token.All(Uri.IsHexDigit)) return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
