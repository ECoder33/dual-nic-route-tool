using System.IO;
using System.Text.Json;
using DualNicRouteTool.Models;

namespace DualNicRouteTool.Services;

/// <summary>用户设置的读写。读写失败都不影响程序使用：读不到用默认值，写不了就放弃。</summary>
public sealed class SettingsStore(string? path = null)
{
    private readonly string _path = path ?? AppEnvironment.SettingsPath;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), BackupStore.JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return new AppSettings();
    }

    public bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, BackupStore.JsonOptions));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
