# Quick launch for Notch

Adds a **Launch** tab to the expanded notch with a button for each app you pin. Click a button to open the app.

- **Add** lists every app in your Start menu (yours and all users'). Click one to pin it, click again to unpin. Type in the line at the bottom and press Enter to search. You can also paste a full path (program, shortcut, folder or document), a link (`https://…`, `steam://…`, `ms-settings:…`) or a program name on your PATH (`wt`, `code`) and press Enter to pin it directly.
- **Edit** lets you move apps up or down, rename them or remove them. Remove asks for a second click.
- Up to 40 apps.

Options under the plugin in Notch's Settings, applied without a restart:

| Option | Default |
|---|---|
| Buttons per row (1 to 6) | 3 |
| Also show pinned apps on the Plugins tab | off |
| Show a notice in the pill when an app opens | on |

Pinned apps are saved in `%AppData%\Notch\plugin-data\justnatefr.quick-launch\apps.json`. Each entry also has an `Arguments` field you can fill in by hand (quit Notch first) if an app needs command-line arguments.

Plugin API version 5, so it works with any Notch that supports page actions and blocks.

## Prebuilt copy

`quick-launch-prebuilt.zip` holds a ready plugin folder, built against Notch 0.7.0 (the first release with plugin API 5), so it runs on Notch 0.7.0 and later. Unzip it into `%AppData%\Notch\plugins\` and switch it on in Settings.

## Build

You need the .NET 10 SDK and Notch installed (the project compiles against `%LocalAppData%\Programs\Notch\Notch.Core.dll`).

```
dotnet build -c Release
```

To build against a different `Notch.Core.dll`, for example one built from a WNotch clone:

```
dotnet build -c Release -p:NotchCorePath=C:\src\WNotch\src\Notch.Core\bin\Release\net10.0\Notch.Core.dll
```

In Visual Studio, open `QuickLaunch.csproj` and build as usual.

## Try it without installing

Quit Notch from its tray icon first, then:

```
"%LocalAppData%\Programs\Notch\Notch.exe" --plugin=bin\Release\net10.0 --pin-open
```

For debugging, set `Notch.exe` as the project's start program with `--plugin=bin\Debug\net10.0` as the argument, or attach to the `Notch` process.

## Install

```
dotnet publish -c Release -o dist\justnatefr.quick-launch
```

Copy `dist\justnatefr.quick-launch` into `%AppData%\Notch\plugins\` (Settings > Plugins > Open folder), open Settings, switch on **Quick launch**, approve it and save.

When you approve it, Notch will mention that the plugin starts programs. That is expected: it is how the buttons open your apps. Nothing else is run.

## Files

| File | What it does |
|---|---|
| `QuickLaunchPlugin.cs` | The plugin: the Launch tab's three views (apps, edit, add), launching, settings, cards. |
| `PinnedApps.cs` | The pinned-app list and saving it to `apps.json`. |
| `AppCatalog.cs` | Reads the Start menu shortcuts shown in the Add view. |
| `plugin.json` | The manifest, including the options shown in Settings. |
