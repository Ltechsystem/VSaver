# ValheimSync

Keep one Valheim world in sync between a group of friends via a shared Google Drive folder. One person plays at a time (enforced with a cloud lock file), saves upload automatically after Valheim finishes writing them, and everyone else pulls the latest world before their turn.

**Stack:** .NET 8, Avalonia 11 (Fluent, MVVM via CommunityToolkit), Google Drive v3 API.

```
ValheimSync/
├── src/
│   ├── ValheimSync.Core/          # No UI dependencies — all sync logic lives here
│   │   ├── Models/                # WorldSave, RemoteFile, WorldLock, SyncStatus
│   │   ├── Storage/               # ICloudStorageProvider + GoogleDriveStorageProvider
│   │   ├── Sync/                  # WorldScanner, DebouncedWorldWatcher, SyncEngine
│   │   └── Util/                  # MD5 hashing (matches Drive's md5Checksum)
│   └── ValheimSync.App/           # Avalonia desktop app
└── ValheimSync.sln
```

---

## How the sync works (30-second version)

1. A `FileSystemWatcher` watches `worlds_local`. When Valheim writes a save, we wait until the files have been **quiet for 60 s** (debounce) before uploading — never a mid-write upload. Additionally, **while Valheim is open the current save is pushed every 5 minutes** (configurable via `InGameUploadMinutes`) as long as it actually changed and has settled — so a crash never loses more than that interval and friends can watch progress without waiting for you to quit.
2. A **15-minute poll** (configurable) is the fallback and the download path: it compares your local `.db` MD5 against Drive's `md5Checksum`. Identical hash → nothing is transferred. **No duplicates, ever.**
3. `.db` uploads first, `.fwl` last — the tiny `.fwl` is the "commit marker", so a half-finished upload never looks like a valid world.
4. Downloads go to a temp file, are hash-verified, a `.synbak` copy of your old files is kept, and only then are the files moved into place. Never while Valheim is running.
5. **Locking:** before playing, click **Play** — the app writes `<World>.lock` (your name + timestamp) to the Drive folder. Everyone else's app sees the lock and shows the world as in use. Click **Done** when you stop: it pushes your final save and releases the lock. Locks older than 12 h are treated as stale (crashed client). Note: Drive has no atomic compare-and-swap, so two people clicking Play in the same second could theoretically race — fine for a friend group, and the app re-checks after writing to shrink the window.

> ⚠️ Valheim worlds cannot be merged. The lock exists because if two people play "their" copy simultaneously, whoever uploads last silently destroys the other's progress. Respect the lock. If you want *simultaneous* multiplayer, run a dedicated server instead — this tool is for "pass the world around" play.

---

## Part 1 — Get your Google API credentials (one person does this, ~10 minutes)

> ## 🔑 You must download a `credentials.json` from Google — the app cannot connect without it.
> There is **no built-in API key** and none is shipped with the app. You create your own OAuth
> client in the Google Cloud Console (free) and **download the JSON file yourself**. The
> `credentials.json.example` in this repo is only a placeholder showing the expected shape — it
> contains **no real credentials** and will not work until you replace it with your own download.

Drive file access uses **OAuth**, which means a `credentials.json` file instead of a key string. One person in the group creates it; everyone else just receives the file.

1. Go to **https://console.cloud.google.com** and sign in with any Google account.
2. Top bar → project dropdown → **New Project**. Name it `ValheimSync`, click **Create**, then make sure it's selected.
3. Left menu → **APIs & Services → Library**. Search **Google Drive API** → **Enable**.
4. **APIs & Services → OAuth consent screen**:
   - User type: **External** → Create.
   - App name: `ValheimSync`, your email in both email fields. Skip logo/domains. Save through the Scopes page (add nothing).
   - On **Test users**: click **Add users** and enter the **Google email address of every friend** who will use the app (including yourself). This is important — while the app is in "Testing" mode, only listed test users can sign in.
5. **APIs & Services → Credentials → Create Credentials → OAuth client ID**:
   - Application type: **Desktop app**
   - Name: `ValheimSync Desktop`
   - Click **Create** → in the dialog click **⬇ Download JSON**. **This downloaded file is your `credentials.json`** — without it the app has nothing to authenticate with.
6. **Rename** the downloaded file to exactly **`credentials.json`** and place it:
   - **Building from source?** Put it in `src/ValheimSync.App/` (it gets copied next to the exe on build). It replaces the placeholder `credentials.json.example`.
   - **Running the released exe?** Put it in the **same folder as `ValheimSync.exe`**.
   - ✅ Sanity check: open the file — it should contain a real `client_id` ending in `.apps.googleusercontent.com`, **not** the `YOUR_CLIENT_ID` placeholder text.

### Things to know about "Testing" mode
- Everyone will see an *"Google hasn't verified this app"* warning during the one-time sign-in. That's expected — click **Continue**. It's your own app; verification is only needed for public distribution.
- Google may expire OAuth tokens after **7 days** in Testing mode, which means re-consenting weekly. To avoid that: on the OAuth consent screen page click **Publish app** (moves it to "In production"). You'll keep the unverified warning but tokens stop expiring. For a private friend group this is the pragmatic choice.
- The app requests only the `drive.file` scope — it can *only* see files it created itself, not anyone's personal Drive contents. Good for trust and it keeps the consent screen tame.

### Is it OK to share credentials.json with friends?
For a **Desktop app** OAuth client, Google itself documents that the "client secret" is not treated as a secret (it ships inside every installed app). Each friend still signs in with *their own* Google account and grants access only to files the app creates. So yes — bundling `credentials.json` with the app for your group is fine. Just don't commit it to a public GitHub repo (it's already in `.gitignore`).

---

## Part 2 — Create the shared folder (same person, 2 minutes)

1. In **drive.google.com**, create a folder, e.g. `ValheimSync`.
2. Right-click → **Share** → add each friend's Google email as **Editor**.
3. Open the folder and copy the ID from the URL:
   `https://drive.google.com/drive/folders/`**`1aBcD3FgHiJkLmNoP...`** ← that last part is the **folder ID**.
4. Send the folder ID to everyone (or hardcode it as the default in `AppSettings.cs` before building, so friends never have to paste anything).

> Heads up: because the app uses the restricted `drive.file` scope, the cleanest setup is that the *first* upload of each world happens through the app itself (the app then "owns" those files and everyone's app can see them via the shared folder). Don't manually drag save files into the folder through the Drive website.

---

## Part 3 — Build & distribute (with auto-update)

The app updates itself from GitHub Releases: on every launch it asks GitHub for the
latest release, and if that release is newer than the running build it silently downloads
the new exe, swaps it in, and relaunches — so friends always run the current version
without ever reinstalling.

**One-time setup:** create a **public** GitHub repo for this project and set its slug in
`src/ValheimSync.Core/Update/Updater.cs`:

```csharp
public const string Repo = "your-github-name/ValheimSync";
```

> The repo must be **public** so the app can download releases without a token. Keep
> `credentials.json` out of it — it's already in `.gitignore`; distribute it separately (see below).

### To cut a release (every time you publish an update)

1. **Bump the version** in `src/ValheimSync.App/ValheimSync.App.csproj`:
   ```xml
   <Version>1.0.1</Version>
   ```
2. **Build** the self-contained single-file exe (friends need **nothing** installed):
   ```powershell
   dotnet publish src/ValheimSync.App -c Release -r win-x64 --self-contained `
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
     -p:AssemblyName=ValheimSync
   ```
   Output lands in `src/ValheimSync.App/bin/Release/net8.0/win-x64/publish/ValheimSync.exe`.
3. **Create a GitHub Release** whose **tag matches the version** (`v1.0.1`) and attach the exe
   as an asset named exactly **`ValheimSync.exe`** (the updater looks for that name). With the
   [`gh` CLI](https://cli.github.com):
   ```powershell
   gh release create v1.0.1 `
     "src/ValheimSync.App/bin/Release/net8.0/win-x64/publish/ValheimSync.exe" `
     --title "v1.0.1" --notes "What changed"
   ```

That's it — the next time anyone opens the app, it upgrades itself to `v1.0.1`.

> The tag drives updates: the app compares its own `<Version>` against the latest release's
> tag (leading `v` optional). If the tag isn't higher, nothing happens. Never reuse a tag.

### First-time distribution (the only manual install)

New users can't auto-update *into* their first copy — send them a zip once:
- `ValheimSync.exe`
- `credentials.json` (from Part 1)

Each friend unzips anywhere and runs the exe. From then on updates are automatic.

## Part 3.5 — Running in the background

Clicking the window's **X** doesn't quit the app — it hides to the **system tray** so syncing
keeps running. From the tray icon: **left-click** (or right-click → **Open ValheimSync**) to bring
the window back, or right-click → **Quit** to actually exit. Leaving it running in the tray is the
intended mode — that's what keeps your world uploading while you play (see below).

## Part 4 — First run (every user)

1. Run `ValheimSync.App.exe`.
2. Enter your name and paste the shared **folder ID** → **Connect**.
3. A browser opens for Google sign-in (once per machine — the token is cached in `%APPDATA%\ValheimSync\token`). Click through the unverified-app warning.
4. Tick the world(s) you want to sync.
5. Before playing: click **Play** on the world (takes the lock). When you quit Valheim: click **Done** (final upload + lock release).

Settings persist in `settings.json` next to the exe.

---

## Extending it

- **New cloud provider:** implement `ICloudStorageProvider` (7 methods) — the engine and UI don't change. Dropbox is a good second target (simpler auth for small groups).
- **Tests:** `SyncEngine` takes `ICloudStorageProvider` via constructor, so an in-memory fake makes the whole decision logic unit-testable — same pattern as your CBS project's SPI seams.

Already built in: **auto-launch Valheim** on Play (with auto lock-release on exit), **system tray** background mode (Part 3.5), **in-game periodic upload** (point 1 above), and **GitHub auto-update** (Part 3).

## Known limitations (by design, for a starter)

- Windows-only paths for the Valheim save folder (Linux/macOS would need path variants).
- Lock is advisory — the app enforces it in its own UI, but nothing stops someone from launching Valheim directly. Pairing Play with auto-launch (above) mitigates this socially.
- `FindByNameAsync` lists the whole folder per lookup; fine for a handful of worlds, cache it if you sync many.
- No conflict *merge* — Valheim saves can't be merged, so conflicts are prevented (lock), not resolved.
