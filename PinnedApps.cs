using System.Text.Json;
using Notch.Core.Plugins;

namespace QuickLaunch;

/// <summary>One app on the Launch tab.</summary>
public sealed record PinnedApp
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>What gets opened: an .exe, a shortcut (.lnk, .url), a folder, a document or a URL.</summary>
    public required string Target { get; init; }

    /// <summary>Optional command-line arguments. Not set from the UI; edit apps.json to add them.</summary>
    public string? Arguments { get; init; }

    /// <summary>A picture the user chose, as a file name in the data folder's icons\; null for the app's own icon.</summary>
    public string? Picture { get; init; }
}

/// <summary>
/// The pinned apps, in display order, kept in apps.json in the plugin's data folder.
/// Thread-safe: page callbacks arrive on background threads.
/// </summary>
public sealed class PinnedAppStore
{
    public const int MaxApps = 40;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly IPluginLog _log;
    private List<PinnedApp> _apps;

    private PinnedAppStore(string path, IPluginLog log, List<PinnedApp> apps)
    {
        _path = path;
        _log = log;
        _apps = apps;
    }

    public static PinnedAppStore Load(string dataDirectory, IPluginLog log)
    {
        string path = Path.Combine(dataDirectory, "apps.json");
        List<PinnedApp> apps = [];
        try
        {
            if (File.Exists(path))
            {
                apps = JsonSerializer.Deserialize<List<PinnedApp>>(File.ReadAllText(path), Json) ?? [];
                apps = [.. apps
                    .Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Target))
                    .DistinctBy(a => a.Id)
                    .Take(MaxApps)];
            }
        }
        catch (Exception e)
        {
            // Keep the broken file for the user to look at rather than overwriting it on the next save.
            log.Error($"Could not read {path}; starting with no pinned apps.", e);
            TryBackUp(path, log);
        }

        return new PinnedAppStore(path, log, apps);
    }

    public IReadOnlyList<PinnedApp> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _apps];
            }
        }
    }

    public bool IsFull
    {
        get
        {
            lock (_gate)
            {
                return _apps.Count >= MaxApps;
            }
        }
    }

    public bool ContainsTarget(string target)
    {
        lock (_gate)
        {
            return _apps.Any(a => string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Adds an app at the end. False when it is already pinned or the list is full.</summary>
    public bool Add(string name, string target)
    {
        lock (_gate)
        {
            if (_apps.Count >= MaxApps || _apps.Any(a => string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _apps.Add(new PinnedApp { Id = Guid.NewGuid().ToString("N")[..12], Name = name, Target = target });
            Save();
            return true;
        }
    }

    public bool RemoveTarget(string target)
    {
        lock (_gate)
        {
            if (_apps.RemoveAll(a => string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return false;
            }

            Save();
            return true;
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_apps.RemoveAll(a => a.Id == id) > 0)
            {
                Save();
            }
        }
    }

    public void Rename(string id, string name)
    {
        lock (_gate)
        {
            int i = _apps.FindIndex(a => a.Id == id);
            if (i >= 0 && !string.IsNullOrWhiteSpace(name))
            {
                _apps[i] = _apps[i] with { Name = name.Trim() };
                Save();
            }
        }
    }

    /// <summary>Sets or clears (null) the picture the user chose. Returns the previous one.</summary>
    public string? SetPicture(string id, string? picture)
    {
        lock (_gate)
        {
            int i = _apps.FindIndex(a => a.Id == id);
            if (i < 0)
            {
                return null;
            }

            string? previous = _apps[i].Picture;
            _apps[i] = _apps[i] with { Picture = picture };
            Save();
            return previous;
        }
    }

    /// <summary>Moves an app by <paramref name="delta"/> places, e.g. -1 for up.</summary>
    public void Move(string id, int delta)
    {
        lock (_gate)
        {
            int from = _apps.FindIndex(a => a.Id == id);
            int to = from + delta;
            if (from < 0 || to < 0 || to >= _apps.Count)
            {
                return;
            }

            PinnedApp app = _apps[from];
            _apps.RemoveAt(from);
            _apps.Insert(to, app);
            Save();
        }
    }

    // Called with _gate held. Written to a temporary file first so a crash cannot leave half a file.
    private void Save()
    {
        try
        {
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_apps, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e)
        {
            _log.Error($"Could not save {_path}.", e);
        }
    }

    private static void TryBackUp(string path, IPluginLog log)
    {
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
        }
        catch (Exception e)
        {
            log.Warn($"Could not back up {path}: {e.Message}");
        }
    }
}
