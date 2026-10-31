using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsUltimate.Core.Infrastructure;

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {path}", ex);
            return null;
        }
    }

    /// <summary>Writes atomically (temp file + replace) so a crash never leaves a half-written journal.</summary>
    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
