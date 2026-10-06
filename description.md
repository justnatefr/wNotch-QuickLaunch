A **Launch** tab in the expanded notch with a button for each app you pin, under the app's own icon or a picture you choose.

- **Add** lists every app in your Start menu: click one to pin it, or type to search. You can also paste a path, a link (`steam://`, `ms-settings:`) or a program name on your PATH.
- **Edit** moves, renames and removes pinned apps, and lets you pick a different picture for any of them.
- Options: buttons per row, show the apps as cards on the Plugins tab, and a notice when an app opens.

Uses `shell` to open the apps you pin and `filesystem` to read your Start menu shortcuts and the pictures you choose; it asks Windows for app icons. Nothing else is run and nothing is sent anywhere.
