using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DualNicRouteTool.Models;

namespace DualNicRouteTool.Services;

/// <summary>备份的存取。</summary>
public interface IBackupStore
{
    string Directory { get; }

    void Save(RouteSnapshot snapshot);

    /// <summary>全部备份，最新的在前。</summary>
    IReadOnlyList<RouteSnapshot> LoadAll();

    void Delete(RouteSnapshot snapshot);
}

public static class BackupStoreExtensions
{
    /// <summary>当前仍然有效、尚未恢复的最新备份；没有则返回 null。开机前创建的备份视为过期，不参与。</summary>
    public static RouteSnapshot? FindActive(this IBackupStore store, DateTime bootTimeUtc) =>
        store.LoadAll().FirstOrDefault(s => s.IsRestorable && !s.IsStale(bootTimeUtc));
}

/// <summary>把备份保存为数据目录下的 JSON 文件。</summary>
public sealed class BackupStore : IBackupStore
{
    public const string FilePrefix = "route_backup_";
    public const int MaxFiles = 20;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 保留中文原文，方便人工查看
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public BackupStore(string? directory = null)
    {
        Directory = directory ?? AppEnvironment.BackupDirectory;
    }

    public string Directory { get; }

    public void Save(RouteSnapshot snapshot)
    {
        System.IO.Directory.CreateDirectory(Directory);
        snapshot.FilePath ??= Path.Combine(Directory, $"{FilePrefix}{snapshot.Id}.json");
        snapshot.UpdatedUtc = DateTime.UtcNow;

        // 先写临时文件再替换，避免写到一半断电留下损坏的备份
        var temp = snapshot.FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temp, snapshot.FilePath, overwrite: true);

        Prune(snapshot);
    }

    public IReadOnlyList<RouteSnapshot> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];

        var result = new List<RouteSnapshot>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, $"{FilePrefix}*.json"))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<RouteSnapshot>(File.ReadAllText(file), JsonOptions);
                if (snapshot is null || string.IsNullOrEmpty(snapshot.Id)) continue;
                snapshot.FilePath = file;
                result.Add(snapshot);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // 无法识别的文件（损坏或格式不兼容）不参与恢复，也不删除
            }
        }

        return result.OrderByDescending(s => s.CreatedUtc).ThenByDescending(s => s.Id, StringComparer.Ordinal).ToList();
    }

    public void Delete(RouteSnapshot snapshot)
    {
        if (snapshot.FilePath is not null && File.Exists(snapshot.FilePath))
            File.Delete(snapshot.FilePath);
    }

    /// <summary>只保留最近 <see cref="MaxFiles"/> 份；仍可恢复的备份不会被清理。</summary>
    private void Prune(RouteSnapshot justSaved)
    {
        try
        {
            foreach (var old in LoadAll().Skip(MaxFiles))
            {
                if (old.IsRestorable || old.Id == justSaved.Id) continue;
                Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理旧文件失败不影响本次保存
        }
    }
}
