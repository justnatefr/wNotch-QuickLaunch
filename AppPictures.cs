using System.Collections.Concurrent;
using Notch.Core.Plugins;

namespace QuickLaunch;

/// <summary>
/// The picture shown for each pinned app: the one the user chose, or else the app's own icon,
/// read from Windows once and kept in the plugin's data folder (icons\).
/// </summary>
public sealed class AppPictures
{
    /// <summary>Largest picture file the user can choose.</summary>
    public const long MaxPictureBytes = 4 * 1024 * 1024;

    private static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico"];

    private readonly string _folder;
    private readonly IPluginLog _log;
    private readonly ConcurrentDictionary<string, byte[]> _loaded = new();
    private readonly SemaphoreSlim _extracting = new(1, 1);

    public AppPictures(string dataDirectory, IPluginLog log)
    {
        _folder = Path.Combine(dataDirectory, "icons");
        _log = log;
    }

    /// <summary>The picture to show for <paramref name="app"/>, or null while there is none.</summary>
    public byte[]? For(PinnedApp app)
    {
        if (app.Picture is { } custom && Load(custom) is { } chosen)
        {
            return chosen;
        }

        return Load(AutoFile(app.Id));
    }

    /// <summary>
    /// Reads the icons of the apps that have none yet, one at a time on a background thread, and
    /// calls <paramref name="changed"/> after each one found.
    /// </summary>
    public void FetchMissing(IReadOnlyList<PinnedApp> apps, Action changed, CancellationToken cancel)
    {
        List<PinnedApp> missing = [.. apps.Where(a => Load(AutoFile(a.Id)) is null)];
        if (missing.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _extracting.WaitAsync(cancel);
                try
                {
                    foreach (PinnedApp app in missing)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (Load(AutoFile(app.Id)) is not null)
                        {
                            continue;
                        }

                        byte[]? png = WindowsShell.IconPng(app.Target);
                        if (png is null)
                        {
                            continue;
                        }

                        Save(AutoFile(app.Id), png);
                        changed();
                    }
                }
                finally
                {
                    _extracting.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _log.Error("Could not read app icons.", e);
            }
        }, cancel);
    }

    /// <summary>
    /// Copies a picture the user chose into the data folder and returns its file name there, for
    /// <see cref="PinnedApp.Picture"/>. Throws <see cref="InvalidDataException"/> with a sentence
    /// for the user when the file cannot be used.
    /// </summary>
    public string Import(string appId, string source)
    {
        string path = Environment.ExpandEnvironmentVariables(source.Trim().Trim('"'));
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (!PictureExtensions.Contains(extension))
        {
            throw new InvalidDataException("Choose a PNG, JPEG, BMP, GIF or ICO file.");
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new InvalidDataException("That file does not exist.");
        }

        if (info.Length > MaxPictureBytes)
        {
            throw new InvalidDataException("That picture is larger than 4 MB.");
        }

        // A new name each time, so the notch sees new bytes rather than a cached picture.
        string name = $"{appId}-custom-{DateTime.UtcNow:yyyyMMddHHmmss}{extension}";
        Save(name, File.ReadAllBytes(path));
        return name;
    }

    /// <summary>Deletes the picture files of an app (all of them, or only the chosen one).</summary>
    public void Forget(string appId, bool keepIcon = false)
    {
        try
        {
            if (!Directory.Exists(_folder))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(_folder, appId + "*"))
            {
                string name = Path.GetFileName(file);
                if (keepIcon && name == AutoFile(appId))
                {
                    continue;
                }

                _loaded.TryRemove(name, out _);
                File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not delete the pictures of {appId}: {e.Message}");
        }
    }

    private static string AutoFile(string appId) => appId + "-icon.png";

    private byte[]? Load(string name)
    {
        if (_loaded.TryGetValue(name, out byte[]? bytes))
        {
            return bytes;
        }

        string path = Path.Combine(_folder, name);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            bytes = File.ReadAllBytes(path);
            _loaded[name] = bytes;
            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not read {path}: {e.Message}");
            return null;
        }
    }

    private void Save(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, name), bytes);
        _loaded[name] = bytes;
    }
}
