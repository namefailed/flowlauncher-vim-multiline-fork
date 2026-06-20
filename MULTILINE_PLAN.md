# Flow Launcher — Vim Multiline Editor Fork

**Project:** `flowlauncher-vim-multiline-fork`  
**Based on:** `namefailed/flowlauncher-vim-fork` (existing Vim mode fork)  
**Goal:** Transform Flow Launcher's popup into a floating, terminal-grade, **multi-line Vim editor** — a scratchpad you summon globally, write in with full Vim motions, then dismiss or export.

> **Status:** this is the original forward-looking plan, kept for history. For what actually shipped and how it
> works (the as-built reference), see [`docs/MULTILINE_EDITOR.md`](docs/MULTILINE_EDITOR.md).

---

## What Is This?

Think of it as a **global scratchpad with a real Vim engine** that lives in your system tray and pops up on a hotkey. Instead of the single-line search bar, you get a resizable multi-line text editor — fully modal, fully keyboard-driven — that you can use to:

- Draft quick notes, journal entries, or commands
- Edit and transform text (copy/paste to/from clipboard)
- Run the content through Flow Launcher plugins (e.g., pass to shell, calculator, etc.)
- Dismiss it instantly with `Escape` or `double-Escape`

---

## Architecture Overview

```
┌─────────────────────────────────┐
│  Flow Launcher Popup Window      │
│  ┌───────────────────────────┐  │
│  │ [NORMAL] ●                │  │  ← Mode indicator + status bar
│  ├───────────────────────────┤  │
│  │ Line 1: Hello world       │  │
│  │ Line 2: This is a note    │  │  ← Multi-line TextBox (VimTextBox)
│  │ Line 3: _                 │  │
│  │                           │  │
│  ├───────────────────────────┤  │
│  │ [Plugin Results / Output] │  │  ← Optional results panel (collapsible)
│  └───────────────────────────┘  │
└─────────────────────────────────┘
```

---

## Phase 1 — Core UI Transformation

### 1.1 Replace the Search TextBox

The single-line `QueryTextBox` gets replaced (or extended) with a **multi-line `TextBox`** (`AcceptsReturn="True"`, `TextWrapping="Wrap"`).

**Key XAML changes in `MainWindow.xaml`:**
- Set `AcceptsReturn="True"` on the query TextBox
- Set `TextWrapping="Wrap"`
- Make the window vertically resizable (remove fixed height)
- Add a minimum/default height (e.g., 200px editor area)
- Add a resize grip at the bottom edge

### 1.2 Status Bar

Replace the simple mode indicator dot with a proper **status line** at the bottom of the editor area (like real Vim):

```
[NORMAL]  Ln 3, Col 7  |  42 chars
```

- Mode pill (colored: blue=Normal, purple=Visual, orange=VisualLine, green=Insert)
- Line/column display
- Total character count

### 1.3 Line Numbers (Optional, toggle-able)

A narrow left gutter showing line numbers, styled to match the Flow theme.

---

## Phase 2 — Vim Engine Multi-Line Support

This is the core engineering work. The existing `VimMotionEngine.cs` operates on a flat string. We extend it to understand **lines**.

### 2.1 New Motion Engine Concepts

Add line-aware motion helpers to `VimMotionEngine.cs`:

| Method | Description |
|---|---|
| `GetLineAt(text, caretIndex)` | Returns the line number of a given caret index |
| `GetLineStart(text, lineNumber)` | Returns the caret index of the start of a line |
| `GetLineEnd(text, lineNumber)` | Returns the caret index of the end of a line |
| `MoveDown(text, caretIndex)` | Move caret down one visual line (preserving column) |
| `MoveUp(text, caretIndex)` | Move caret up one visual line |
| `MoveFirstLine(text)` | `gg` — move to start of document |
| `MoveLastLine(text)` | `G` — move to end of document |

### 2.2 Key Binding Changes

In **Normal Mode**, `j` and `k` change behavior:

| Before (single-line) | After (multi-line) |
|---|---|
| `j` → Navigate results down | `j` → Move caret to next line |
| `k` → Navigate results up | `k` → Move caret to previous line |

> Result navigation moves to `Ctrl+J` / `Ctrl+K` or a dedicated key (TBD).

### 2.3 New Commands

| Command | Behavior |
|---|---|
| `o` | Open new line below current line, enter Insert mode |
| `O` | Open new line above current line, enter Insert mode |
| `gg` | Jump to start of document |
| `G` | Jump to end of document |
| `dd` | Delete current line (not entire query) |
| `cc` | Change current line |
| `yy` | Yank current line |
| `{n}j` / `{n}k` | Move `n` lines down/up |

### 2.4 Visual Line Mode

In multi-line, Visual Line (`V`) selects the **entire current line** (not the full query). Extend selection with `j`/`k`.

---

## Phase 3 — Plugin / Output Integration

### 3.1 Results Panel

A collapsible panel below the editor shows plugin results.

- Trigger: `:` prefix on any line routes that line to the Flow Launcher query engine
- Or: a dedicated "run" keybind (e.g., `Ctrl+Enter`) submits the first line as a query

### 3.2 Clipboard Export

- `Ctrl+C` / `y` in Normal mode: copy selection or line to clipboard
- `YY` (Shift+Y twice): yank entire document to clipboard
- `:copy` or `:c` command: copy all content to clipboard and dismiss

### 3.3 Potential `:` Command Mode

A basic ex-command line (`:`) at the bottom:

| Command | Action |
|---|---|
| `:q` / `Escape` | Close the popup |
| `:w` | Copy all content to clipboard |
| `:wq` | Copy to clipboard and close |
| `:clear` | Clear all content |
| `:set nu` | Toggle line numbers |

---

## Phase 4 — Window & UX Polish

### 4.1 Resizable Window
- Drag bottom/right edge to resize
- Remember last size per session

### 4.2 Font & Theme
- Monospace font (Cascadia Mono, Consolas, or JetBrains Mono) for the editor
- Match system dark/light theme like the rest of Flow Launcher

### 4.3 Persistence (Optional)
- Optionally save buffer content between sessions (a "scratchpad" mode)
- Hot-reload: if the user dismisses and re-opens, content is still there

---

## Open Questions

Before Phase 1 begins, these need answers:

1. **Plugin results**: Should the results panel still exist, or is this a pure editor with no plugin integration?
2. **j/k for results**: If plugins are kept, how do you navigate results when j/k now move lines?
3. **Window trigger**: Same `Alt+Space` hotkey, or a separate one for the multiline editor?
4. **Persistence**: Should the buffer persist between invocations, or always start empty?
5. **File I/O**: Should `:w filename` be able to write to disk?
6. **Separate settings**: Should this fork have its own settings page, or inherit from the base?

---

## Repository Setup

- **Local path**: `C:\Users\Namef\Projects\dev\flowlauncher-vim-multiline-fork`
- **Based on**: `namefailed/flowlauncher-vim-fork` @ `vim-mode` branch
- **Working branch**: `multiline-editor`
- **Upstream remote**: `upstream-vim` → `https://github.com/namefailed/flowlauncher-vim-fork.git`
- **Origin / releases**: `https://github.com/namefailed/flowlauncher-vim-multiline-fork` (auto-built installers on the Releases page)

---

## Implementation Status

**Done**
- Toggle: open Flow, press **`Ctrl+Enter`** to enter/leave the editor.
- Editor UI: fixed-size box, line-number gutter, full-width themed mode line (mode pill + Ln/Col + char count). Flow's clock/search-icon/placeholder are hidden while editing; content is clamped (scrolls inside the box, never spills into results).
- Motions: `h l w W b B e E % f/F/t/T ; ,`, and line-aware `0 ^ $ j k gg G` (in both Normal and Visual).
- Operators: `x X s S r ~ p u Ctrl+R`, char/word `d c y` + text objects, line-wise `dd cc yy` and **`dj dk yj yk cj ck`**.
- Visual + **Visual Line** (line-wise: `V` selects the line, `j`/`k` extend by line, `d/y/c/x/~` act on whole lines).
- Result navigation moves to **`Ctrl+J` / `Ctrl+K`** in the editor.
- **`Ctrl+Shift+E`** hands the buffer off to the OS text editor.
- Buffer persists across hide/show while the editor is on; leaving the editor clears it.

**Decisions (from Open Questions)**
1. Plugins kept. 2. Result nav → `Ctrl+J`/`Ctrl+K`. 3. In-window toggle (`Ctrl+Enter`), not a separate hotkey. 4. Persist while editor is on; clear on exit.

**Remaining / optional**
- `dj`/`dk` etc. are linewise; full Vim linewise paste/`p` of line registers is not modelled.
- External-editor handoff is one-way (no read-back) and manual (`Ctrl+Shift+E`), not auto-on-overflow.
- `:` ex-command mode (`:w`, `:q`, `:set nu`), a resize grip, and a monospace editor font (Plan 1.3 / 3.3 / 4) are not implemented.
- File I/O (Q5) and a separate settings page (Q6) — not implemented.
