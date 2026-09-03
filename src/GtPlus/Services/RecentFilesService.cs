using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GtPlus.Services;

public class RecentFilesService
{
    private const int MaxItems = 10;

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GtPlus", "recent.json");

    private List<string> _paths = new();

    public IReadOnlyList<string> Paths => _paths;

    public RecentFilesService() => Load();

    public void Add(string path)
    {
        _paths.Remove(path);
        _paths.Insert(0, path);
        if (_paths.Count > MaxItems)
            _paths.RemoveRange(MaxItems, _paths.Count - MaxItems);
        Save();
        Logger.Debug($"Recent files: added '{path}' ({_paths.Count} total)");
    }

    public void Clear()
    {
        _paths.Clear();
        Save();
        Logger.Debug("Recent files cleared");
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var json = File.ReadAllText(StorePath);
            _paths = JsonSerializer.Deserialize<List<string>>(json) ?? new();
            _paths.RemoveAll(p => !File.Exists(p));
            Logger.Debug($"Recent files loaded: {_paths.Count} entries from {StorePath}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not load recent files: {ex.Message}");
            _paths = new();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_paths));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not save recent files: {ex.Message}");
        }
    }
}
