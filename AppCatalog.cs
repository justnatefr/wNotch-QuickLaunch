namespace QuickLaunch;

/// <summary>An app found in the Start menu.</summary>
/// <param name="Group">The Start menu folder it sits in, e.g. "Accessories"; empty at the top level.</param>
public sealed record CatalogApp(string Name, string Target, string Group);

/// <summary>
/// The apps the user can pick from: every shortcut in the current user's and all users' Start
/// menu, the same list Windows search draws on for desktop apps.
/// </summary>
public static class AppCatalog
{
    private static readonly string[] Extensions = [".lnk", ".url", ".appref-ms"];

    // Shortcuts that are never what someone wants on a launcher.
    private static readonly string[] Noise = ["uninstall", "readme", "release notes", "license"];

    public static IReadOnlyList<CatalogApp> Scan(CancellationToken cancel)
    {
        var found = new Dictionary<string, CatalogApp>(StringComparer.OrdinalIgnoreCase);

        // The user's own Start menu first, so their copy wins over an all-users one with the same name.
        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
        {
            string root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (string file in Directory.EnumerateFiles(root, "*", options))
            {
                cancel.ThrowIfCancellationRequested();

                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                string name = Path.GetFileNameWithoutExtension(file);
                if (Noise.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                found.TryAdd(name, new CatalogApp(name, file, GroupOf(root, file)));
            }
        }

        return [.. found.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    // "Start Menu\Programs\Accessories\Paint.lnk" -> "Accessories". Almost everything lives under
    // Programs, so that part says nothing.
    private static string GroupOf(string root, string file)
    {
        string group = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
        if (group == ".")
        {
            return "";
        }

        const string programs = "Programs";
        if (group.Equals(programs, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return group.StartsWith(programs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? group[(programs.Length + 1)..]
            : group;
    }
}
