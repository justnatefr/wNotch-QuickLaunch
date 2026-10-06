# Publishing Quick launch on GitHub

Notch installs a plugin from GitHub when the repository's **latest release has exactly one `.zip` attached** with `plugin.json` at its top. This folder is already set up for that: `.github/workflows/release.yml` builds the plugin and publishes that release whenever you push a tag like `v1.0.0`.

Replace `YOURNAME` below with your GitHub username.

## 1. Create the repository

On GitHub, create a new **public** repository, for example `wNotch-QuickLaunch`. Leave it empty (no README, license or .gitignore), since this folder has its own.

## 2. Point the manifest at it

In `plugin.json`, add this line under `"author"`:

```json
"repository": "justnatefr/wNotch-QuickLaunch",
```

This is optional. It lets copies that people installed by hand (like the prebuilt zip) find your updates. Copies installed from GitHub find them without it.

## 3. Push the source

From this folder:

```
git init -b main
git add .
git commit -m "Quick launch plugin"
git remote add origin https://github.com/justnatefr/wNotch-QuickLaunch.git
git push -u origin main
```

`.gitignore` leaves out `bin`, `obj`, `dist` and the prebuilt zip.

## 4. Release it

```
git tag v1.0.0
git push origin v1.0.0
```

The **Actions** tab shows the `release` workflow running. When it finishes, the repository has a `v1.0.0` release with `quick-launch.zip` attached.

## 5. Check it installs

In Notch, open **Settings > Plugins**, type `justnatefr/wNotch-QuickLaunch` into the box and press **Install**. If something is wrong, Notch says what in a sentence.

## Later releases

Raise `"version"` in `plugin.json` (for example to `1.0.1`), commit and push, then tag and push the same version (`v1.0.1`). Or, instead of pushing a tag, open **Actions > release > Run workflow** on GitHub and type `v1.0.1`; the workflow creates the tag and the release. Keep the `id` the same forever, since it names the plugin's data folder. People see **Update and restart** in Settings.

## Optional: the plugin list on the Notch website

To get an **Install** button on the website and show up under **Browse plugins** in Notch, open a pull request on `Brick-Bread/WNotch` that adds this entry to the `plugins` array in `site/plugins/registry.json`:

```json
{
  "id": "justnatefr.quick-launch",
  "name": "Quick launch",
  "repository": "justnatefr/wNotch-QuickLaunch",
  "author": "Nate",
  "description": "A Launch tab with the apps you pin. Pick them from the Start menu or paste a path or link, then open them with one click.",
  "tags": ["launcher", "productivity"],
  "permissions": ["shell", "filesystem"],
  "apiVersion": 5
}
```

Don't set `verified`; the maintainers do that after reading the source. The website card uses `description.md` (already in this folder) and a `logo.png` or `logo.webp` at the repository root if you add one.
