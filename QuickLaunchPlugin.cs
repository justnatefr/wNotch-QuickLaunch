using System.ComponentModel;
using System.Diagnostics;
using Notch.Core.Activities;
using Notch.Core.Plugins;

namespace QuickLaunch;

/// <summary>
/// A "Launch" tab in the expanded notch: a grid of buttons, one per pinned app. Apps are picked
/// from the Start menu (or pasted as a path or URL) on the tab's Add view, and reordered,
/// renamed or removed on its Edit view.
/// </summary>
public sealed class QuickLaunchPlugin : INotchPlugin
{
    private const string PageId = "launch";
    private const string LaunchGlyph = "\uE8A7";
    private const string ErrorGlyph = "\uE783";
    private const int MaxChoices = 150;

    private enum View
    {
        Apps,
        Edit,
        Add,
    }

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();

    private IPluginHost _host = null!;
    private PinnedAppStore _store = null!;

    // Everything below is guarded by _gate.
    private View _view = View.Apps;
    private string _filter = "";
    private string? _renamingId;
    private IReadOnlyList<CatalogApp> _catalog = [];
    private bool _scanning;
    private int _columns = 3;
    private bool _showCards;
    private bool _notifyOnLaunch = true;
    private bool _cardsShown;
    private HashSet<string> _shownCardIds = [];

    public void Start(IPluginHost host)
    {
        _host = host;
        _store = PinnedAppStore.Load(host.DataDirectory, host.Log);

        ReadSettings(writeDefaults: true);

        // Listening means Notch applies changed options without restarting the plugin.
        host.Settings.Changed += OnSettingsChanged;

        Render();
        ScanStartMenu();
    }

    public void Stop()
    {
        _stopping.Cancel();
        _host.Settings.Changed -= OnSettingsChanged;
    }

    // ---- Settings ---------------------------------------------------------------------------

    private void ReadSettings(bool writeDefaults)
    {
        IPluginSettings settings = _host.Settings;
        double columns = settings.Get("columns", 3.0);
        bool showCards = settings.Get("showCards", false);
        bool notify = settings.Get("notifyOnLaunch", true);

        if (writeDefaults)
        {
            // Written back so settings.json lists every option, as the plugin docs suggest.
            settings.Set("columns", columns);
            settings.Set("showCards", showCards);
            settings.Set("notifyOnLaunch", notify);
        }

        lock (_gate)
        {
            _columns = double.IsFinite(columns) ? Math.Clamp((int)Math.Round(columns), 1, 6) : 3;
            _showCards = showCards;
            _notifyOnLaunch = notify;
        }
    }

    private void OnSettingsChanged(object? sender, string key)
    {
        try
        {
            ReadSettings(writeDefaults: false);
            Render();
        }
        catch (Exception e)
        {
            _host.Log.Error("Could not apply changed settings.", e);
        }
    }

    // ---- Start menu -------------------------------------------------------------------------

    private void ScanStartMenu()
    {
        lock (_gate)
        {
            if (_scanning)
            {
                return;
            }

            _scanning = true;
        }

        Render();

        CancellationToken cancel = _stopping.Token;
        _ = Task.Run(() =>
        {
            IReadOnlyList<CatalogApp> found = [];
            try
            {
                found = AppCatalog.Scan(cancel);
                _host.Log.Info($"Found {found.Count} apps in the Start menu.");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _host.Log.Error("Could not read the Start menu.", e);
            }

            try
            {
                lock (_gate)
                {
                    _catalog = found;
                    _scanning = false;
                }

                Render();
            }
            catch (Exception e)
            {
                _host.Log.Error("Could not show the Start menu apps.", e);
            }
        }, cancel);
    }

    // ---- Actions ----------------------------------------------------------------------------

    private void Show(View view)
    {
        lock (_gate)
        {
            _view = view;
            _renamingId = null;
            if (view != View.Add)
            {
                _filter = "";
            }
        }

        Render();
    }

    private void Launch(PinnedApp app)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.ExpandEnvironmentVariables(app.Target),
                Arguments = app.Arguments ?? "",
                UseShellExecute = true,
            };

            // Programs that look for files next to themselves expect to start in their own folder.
            if (File.Exists(start.FileName) && Path.GetDirectoryName(start.FileName) is { Length: > 0 } dir)
            {
                start.WorkingDirectory = dir;
            }

            Process.Start(start)?.Dispose();

            bool notify;
            lock (_gate)
            {
                notify = _notifyOnLaunch;
            }

            if (notify)
            {
                _host.Shell.Notify(app.Name, "Opening", LaunchGlyph, GlowColor.Cyan, TimeSpan.FromSeconds(1.5));
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            _host.Log.Warn($"Could not open \"{app.Name}\" ({app.Target}): {e.Message}");
            _host.Shell.Notify($"Couldn't open {app.Name}", e.Message, ErrorGlyph, GlowColor.Red, TimeSpan.FromSeconds(4));
        }
    }

    private void TogglePin(CatalogApp app)
    {
        if (!_store.RemoveTarget(app.Target) && !_store.Add(app.Name, app.Target))
        {
            _host.Shell.Notify("Quick launch is full", $"Remove an app to pin more (up to {PinnedAppStore.MaxApps}).", ErrorGlyph, GlowColor.Amber);
        }

        Render();
    }

    /// <summary>The Add view's input line: a path or URL is pinned straight away, anything else filters.</summary>
    private void OnAddInput(string line)
    {
        string text = line.Trim().Trim('"');

        if (TryReadTarget(text) is { } target)
        {
            if (_store.ContainsTarget(target.Target))
            {
                _host.Shell.Notify($"{target.Name} is already pinned", glyph: LaunchGlyph);
            }
            else if (_store.Add(target.Name, target.Target))
            {
                _host.Shell.Notify($"Pinned {target.Name}", glyph: LaunchGlyph, color: GlowColor.Green);
            }
            else
            {
                _host.Shell.Notify("Quick launch is full", $"Remove an app to pin more (up to {PinnedAppStore.MaxApps}).", ErrorGlyph, GlowColor.Amber);
            }

            text = "";
        }

        lock (_gate)
        {
            _filter = text;
        }

        Render();
    }

    private static (string Name, string Target)? TryReadTarget(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(text);
        if (File.Exists(expanded))
        {
            return (Path.GetFileNameWithoutExtension(expanded), text);
        }

        if (Directory.Exists(expanded))
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(expanded));
            return (name.Length > 0 ? name : expanded, text);
        }

        // A bare program name on the PATH, e.g. "wt" or "code".
        if (FindOnPath(expanded) is { } onPath)
        {
            return (Path.GetFileNameWithoutExtension(onPath), onPath);
        }

        // Links and protocol handlers: https://..., steam://rungameid/..., ms-settings:, mailto:...
        // A single letter before the colon is a drive, not a scheme, so "C:\nothing" is not one.
        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Scheme.Length > 1 && !uri.IsFile)
        {
            string name = uri.IsAbsoluteUri && !string.IsNullOrEmpty(uri.Host) ? uri.Host : text;
            return (name.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? name[4..] : name, text);
        }

        return null;
    }

    private static string? FindOnPath(string name)
    {
        if (name.IndexOfAny([' ', '\\', '/', ':']) >= 0)
        {
            return null;
        }

        string[] names = Path.HasExtension(name) ? [name] : [name + ".exe", name + ".cmd", name + ".bat"];
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string candidate in names)
            {
                try
                {
                    string path = Path.Combine(dir.Trim('"'), candidate);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry; skip it.
                }
            }
        }

        return null;
    }

    private void StartRename(string? id)
    {
        lock (_gate)
        {
            _renamingId = id;
        }

        Render();
    }

    private void FinishRename(string id, string name)
    {
        _store.Rename(id, name);
        lock (_gate)
        {
            _renamingId = null;
        }

        Render();
    }

    private void Remove(string id)
    {
        _store.Remove(id);
        lock (_gate)
        {
            if (_renamingId == id)
            {
                _renamingId = null;
            }

            if (_store.All.Count == 0)
            {
                _view = View.Apps;
            }
        }

        Render();
    }

    private void Move(string id, int delta)
    {
        _store.Move(id, delta);
        Render();
    }

    // ---- Drawing ----------------------------------------------------------------------------

    /// <summary>Sends the page (and the cards) for the current state.</summary>
    private void Render()
    {
        // Held while sending, so two callbacks finishing together cannot send their pages out of order.
        lock (_gate)
        {
            IReadOnlyList<PinnedApp> apps = _store.All;
            PluginPage page = _view switch
            {
                View.Edit => EditPage(apps),
                View.Add => AddPage(apps),
                _ => AppsPage(apps),
            };

            _host.Pages.Set(page);
            RenderCards(apps);
        }
    }

    private PluginPage AppsPage(IReadOnlyList<PinnedApp> apps)
    {
        List<PluginBlock> blocks = [];
        if (apps.Count == 0)
        {
            blocks.Add(new PluginText { Text = "Nothing pinned yet", Style = PluginTextStyle.Heading });
            blocks.Add(new PluginText
            {
                Text = "Pick apps from the Start menu, or paste the path of a program, folder or file, or a link.",
                Style = PluginTextStyle.Muted,
            });
            blocks.Add(new PluginButtons { Actions = [new PluginAction { Label = "Add apps", Clicked = () => Show(View.Add) }] });
        }
        else
        {
            foreach (PinnedApp[] row in apps.Chunk(_columns))
            {
                blocks.Add(new PluginButtons
                {
                    Actions = [.. row.Select(app => new PluginAction
                    {
                        Label = app.Name,
                        Hint = app.Target,
                        Clicked = () => Launch(app),
                    })],
                });
            }
        }

        return new PluginPage
        {
            Id = PageId,
            Title = "Launch",
            Actions =
            [
                new PluginAction { Label = "Add", Hint = "Pin apps to this tab", Clicked = () => Show(View.Add) },
                new PluginAction { Label = "Edit", Hint = "Reorder, rename or remove apps", Enabled = apps.Count > 0, Clicked = () => Show(View.Edit) },
            ],
            Blocks = blocks,
        };
    }

    private PluginPage EditPage(IReadOnlyList<PinnedApp> apps)
    {
        // Two blocks per app; PinnedAppStore.MaxApps keeps that under the page's 100-block limit.
        List<PluginBlock> blocks = [];
        for (int i = 0; i < apps.Count; i++)
        {
            PinnedApp app = apps[i];
            string id = app.Id;

            if (_renamingId == id)
            {
                blocks.Add(new PluginTextField
                {
                    Label = "Name",
                    Value = app.Name,
                    Hint = app.Name,
                    SubmitLabel = "Rename",
                    Submitted = name => FinishRename(id, name),
                });
            }
            else
            {
                blocks.Add(new PluginValueRow { Label = app.Name, Value = Shorten(app.Target) });
            }

            blocks.Add(new PluginButtons
            {
                Actions =
                [
                    new PluginAction { Label = "Up", Enabled = i > 0, Clicked = () => Move(id, -1) },
                    new PluginAction { Label = "Down", Enabled = i < apps.Count - 1, Clicked = () => Move(id, +1) },
                    _renamingId == id
                        ? new PluginAction { Label = "Cancel", Clicked = () => StartRename(null) }
                        : new PluginAction { Label = "Rename", Clicked = () => StartRename(id) },
                    new PluginAction { Label = "Remove", Color = GlowColor.Red, Confirm = true, Clicked = () => Remove(id) },
                ],
            });
        }

        return new PluginPage
        {
            Id = PageId,
            Title = "Launch",
            Status = $"Editing {apps.Count} pinned {(apps.Count == 1 ? "app" : "apps")}",
            Back = () => Show(View.Apps),
            BackLabel = "Done",
            Blocks = blocks,
        };
    }

    private PluginPage AddPage(IReadOnlyList<PinnedApp> apps)
    {
        var pinned = new HashSet<string>(apps.Select(a => a.Target), StringComparer.OrdinalIgnoreCase);
        List<CatalogApp> matches = [.. _catalog.Where(a =>
            _filter.Length == 0
            || a.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)
            || a.Group.Contains(_filter, StringComparison.CurrentCultureIgnoreCase))];

        List<PluginChoice> choices = [.. matches.Take(MaxChoices).Select(app => new PluginChoice
        {
            Label = app.Name,
            Value = pinned.Contains(app.Target) ? "Pinned" : null,
            Color = pinned.Contains(app.Target) ? GlowColor.Green : null,
            Detail = app.Group.Length > 0 ? app.Group : null,
            Clicked = () => TogglePin(app),
        })];

        string status = _scanning
            ? "Looking through the Start menu…"
            : _filter.Length > 0
                ? $"{matches.Count} {(matches.Count == 1 ? "match" : "matches")} for \"{_filter}\"{(matches.Count > MaxChoices ? $", showing {MaxChoices}" : "")}"
                : $"{_catalog.Count} apps in the Start menu. Click one to pin or unpin it.";

        // Shown only when there are no choices to list.
        List<PluginBlock> blocks = _scanning
            ? [new PluginText { Text = "Looking through the Start menu…", Style = PluginTextStyle.Muted }]
            :
            [
                new PluginText { Text = _filter.Length > 0 ? $"No apps match \"{_filter}\"." : "No apps found in the Start menu.", Style = PluginTextStyle.Muted },
                new PluginText { Text = "Paste the full path of a program, folder or file, or a link, and press Enter to pin it.", Style = PluginTextStyle.Muted },
            ];

        return new PluginPage
        {
            Id = PageId,
            Title = "Launch",
            Status = status,
            Back = () => Show(View.Apps),
            BackLabel = "Done",
            Actions =
            [
                new PluginAction
                {
                    Label = "Show all",
                    Hint = "Clear the search",
                    Enabled = _filter.Length > 0,
                    Clicked = () => OnAddInput(""),
                },
                new PluginAction { Label = "Refresh", Hint = "Read the Start menu again", Enabled = !_scanning, Clicked = ScanStartMenu },
            ],
            Choices = choices,
            Blocks = blocks,
            Input = OnAddInput,
            InputHint = "Search apps, or paste a path or link and press Enter",
        };
    }

    private void RenderCards(IReadOnlyList<PinnedApp> apps)
    {
        if (!_showCards)
        {
            if (_cardsShown)
            {
                _host.Cards.Clear();
                _shownCardIds = [];
                _cardsShown = false;
            }

            return;
        }

        var keep = apps.Select(a => a.Id).ToHashSet();
        foreach (PinnedApp app in apps)
        {
            _host.Cards.Set(new PluginCard
            {
                Id = app.Id,
                Label = "Quick launch",
                Value = app.Name,
                Detail = Shorten(app.Target),
                Clicked = () => Launch(app),
            });
        }

        // Cards of apps that were removed since the last render.
        foreach (string id in _shownCardIds.Where(id => !keep.Contains(id)).ToList())
        {
            _host.Cards.Remove(id);
        }

        _shownCardIds = keep;
        _cardsShown = true;
    }

    /// <summary>"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Paint.lnk" -> "…\Programs\Paint.lnk".</summary>
    private static string Shorten(string target)
    {
        if (target.Contains("://", StringComparison.Ordinal) || target.Length <= 48)
        {
            return target;
        }

        string[] parts = target.Split(Path.DirectorySeparatorChar, '\\');
        return parts.Length <= 3 ? target : "…\\" + string.Join('\\', parts[^2..]);
    }
}
