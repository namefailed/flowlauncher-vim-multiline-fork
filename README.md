<p align="center">
  <img src="https://user-images.githubusercontent.com/6903107/207168016-85d0dd16-1f3b-4d42-9d37-0e0d5a596ead.png" width="400">
</p>

<h1 align="center">Flow Launcher — Vim Multiline Edition <sub>(fork)</sub></h1>

<p align="center">
A personal fork of <a href="https://github.com/Flow-Launcher/Flow.Launcher">Flow Launcher</a> that turns the
search bar into a <b>modal, multi-line Vim editor</b> — a global scratchpad you summon on a hotkey, write in
with full Vim motions, and send straight to a plugin (e.g. a journaling command).
</p>

<p align="center">
  <a href="https://github.com/namefailed/flowlauncher-vim-multiline-fork/releases/latest"><img src="https://img.shields.io/github/v/release/namefailed/flowlauncher-vim-multiline-fork?label=download&color=7389D8"></a>
</p>

> [!NOTE]
> This is **not** the official Flow Launcher, and it is **not** the upstream Vim PR. It is a personal,
> opinionated fork built on top of [`flowlauncher-vim-fork`](https://github.com/namefailed/flowlauncher-vim-fork)
> (the single-line Vim mode that is proposed upstream in
> [Flow-Launcher/Flow.Launcher#4541](https://github.com/Flow-Launcher/Flow.Launcher/pull/4541)).
> It adds a **multi-line editor mode** on top of that Vim layer.

---

## What this is

Flow Launcher is a single-line app/file launcher. This fork keeps all of that, and adds a second mode: press
**`Ctrl+Enter`** and the one-line search box becomes a small, fixed-size **multi-line text editor** with Vim
keybindings, line numbers, and a status line. Press `Ctrl+Enter` again to flip back to the normal launcher.

It was built for one concrete workflow: **typing multi-line journal entries and handing them to a personal
`j` journaling plugin** without leaving the keyboard. Write the entry in the editor with real Vim motions,
`Esc` to Normal mode, and press `Enter` to send the whole buffer to the selected plugin result.

Both modes keep **separate text buffers**, so toggling between them never loses what you were writing on the
other side.

With Vim mode disabled in settings, this build behaves exactly like upstream Flow Launcher.

## Download & install

1. Go to the **[latest release](https://github.com/namefailed/flowlauncher-vim-multiline-fork/releases/latest)**.
2. Download **`Flow-Launcher-Setup.exe`** (installer) or the portable zip.
3. Run it. Windows SmartScreen may warn that the build is unsigned — these releases are built by GitHub Actions
   from this repo's source; choose **More info → Run anyway** if you trust it.
4. Open Flow Launcher settings and enable **General → "Enable Advanced Vim Mode"**. The multi-line editor is
   **on by default** once Vim mode is enabled; you can disable it or change its height (visible lines, default
   9) in the same settings expander.
5. Open the launcher and press **`Ctrl+Enter`** to drop into the multi-line editor.

> Builds are produced by the [Fork Release workflow](.github/workflows/fork-release.yml) on every push to
> `multiline-editor`. If a release isn't available yet, see [Building from source](#building-from-source).

## At a glance

**Toggle:** `Ctrl+Enter` switches between the single-line launcher and the multi-line editor.

**In the editor:**

| Key | Action |
| --- | --- |
| `Ctrl+Enter` | Leave the editor, back to the single-line launcher |
| `Enter` (Insert mode) | Insert a newline (`\n`) |
| `Enter` (Normal/Visual) | **Send the whole buffer** to the selected plugin result |
| `Ctrl+J` / `Ctrl+K` | Move the result selection (since `j`/`k` move between lines here) |
| `Ctrl+V` | Paste with line endings normalised to `\n` |
| `Ctrl+Shift+E` | Hand the buffer off to your external `$EDITOR` and dismiss |
| `Esc` | Insert → Normal (then `Enter` sends) |

Plus the full Vim layer from the base fork — Normal/Visual/Visual-Line modes, motions, operators, text
objects, counts, `.` repeat, `u`/`Ctrl+R` — extended to be **line-aware** here (`0` `^` `$` `gg` `G`, `o`/`O`,
`dd`/`cc`, line-wise `dj`/`dk`, Visual-Line selection all act on real lines).

📖 **Full Vim keybinding reference:** [`Flow.Launcher/VimMode/README.md`](Flow.Launcher/VimMode/README.md)
🛠 **How the editor is built (deep technical doc):** [`docs/MULTILINE_EDITOR.md`](docs/MULTILINE_EDITOR.md)

## Building from source

```powershell
git clone https://github.com/namefailed/flowlauncher-vim-multiline-fork.git
cd flowlauncher-vim-multiline-fork
nuget restore
dotnet build -c Release
# Optional: produce the installer + portable zip in Output\Packages
dotnet tool install -g vpk
.\Scripts\post_build.ps1
```

Run the unit tests with `dotnet test` (the Vim motion/engine logic is covered in
[`Flow.Launcher.Test`](Flow.Launcher.Test)).

## Branches & relationship to the other repos

This fork sits at the end of a small chain:

```
Flow-Launcher/Flow.Launcher        (upstream, official)
        └─ namefailed/flowlauncher-vim-fork      (adds single-line Vim mode; upstream PR #4541)
                └─ namefailed/flowlauncher-vim-multiline-fork   (this repo; adds the multi-line editor)
```

| Remote | Role |
| --- | --- |
| **`origin`** → `flowlauncher-vim-multiline-fork` | This public repo. The `multiline-editor` branch is the default and what Releases are built from. |
| **`upstream-vim`** → `flowlauncher-vim-fork` | The single-line Vim fork. Engine-level fixes are pulled from here so the two stay in parity. |

When a fix applies to both (e.g. a Vim motion bug), it is made in the single-line fork *and* mirrored here.
Features that only make sense with multiple lines live only in this repo.

All credit for Flow Launcher itself goes to the
[Flow Launcher team and contributors](https://github.com/Flow-Launcher/Flow.Launcher/graphs/contributors).
This fork only adds the Vim + multi-line editor layer. For everything else — plugins, themes, the launcher
itself — see the official project:

[Website](https://flowlauncher.com) · [Documentation](https://flowlauncher.com/docs/) · [Official repo](https://github.com/Flow-Launcher/Flow.Launcher)
