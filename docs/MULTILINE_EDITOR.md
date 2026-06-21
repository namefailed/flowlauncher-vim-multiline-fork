# Multi-line editor — how it works

This is the as-built technical reference for the multi-line editor that this fork adds on top of the
single-line Vim mode. It is written so that, a year from now, you can re-open it and understand exactly what
was changed and why — including the WPF quirks that drove some non-obvious decisions.

For the user-facing summary and keybindings, see the [root README](../README.md) and
[`Flow.Launcher/VimMode/README.md`](../Flow.Launcher/VimMode/README.md). This document is the authoritative
reference for the multi-line editor: it describes what actually shipped and how it works.

---

## 1. Mental model

There is **no separate editor control**. The multi-line editor is the *same* `QueryTextBox` that Flow Launcher
already uses for search, reconfigured at runtime. A single boolean field, `VimManager._multiLineMode`, decides
whether we're in "launcher" mode or "editor" mode, and `SetMultiLineMode(bool)` flips every property that
differs between the two and flips them all back on exit.

This matters: because both modes share one `TextBox`, **every editor-only tweak must be fully reverted on
exit**, or it leaks into single-line search. The guiding invariant is:

> With the editor closed (and especially with Vim mode disabled in settings), the build must behave
> byte-for-byte like upstream Flow Launcher.

The Vim engine itself (modes, motions, operators) is unchanged from the single-line fork. The multi-line work
is almost entirely in `VimManager` (UI wiring) plus a set of **line-aware** helpers added to
`VimMotionEngine` (pure, unit-tested math).

---

## 2. Code map

| File | What lives here |
| --- | --- |
| `Flow.Launcher/VimMode/VimManager.cs` | All editor wiring: `SetMultiLineMode`, `EnableEditorScrollbar`, `ApplyEditorChrome`, the gutter/status redraws, the two text buffers, the editor-only key handling (`Ctrl+Enter`, `Enter`, `Ctrl+J/K`, `Ctrl+V`, `Ctrl+Shift+E`), paste normalisation, and external-editor handoff. |
| `Flow.Launcher/VimMode/VimMotionEngine.cs` | Pure caret/range math. The multi-line fork adds the **line-aware** helpers (see §8). All unit-tested. |
| `Flow.Launcher/VimMode/VimEngine.cs` | The mode state machine. Unchanged from the single-line fork. |
| `Flow.Launcher/MainWindow.xaml` | Adds the editor overlays: `VimLineGutter` (Canvas), `VimStatusBarHost` (the mode line) with `VimModeSegment`/`VimModeText`/`VimStatusInfo`, `VimBlockCaret`, `VimModeIndicator`, and `VimYankFlash` (the yank-highlight Canvas). Also where the shared `QueryIconArea` Border lives. |
| `Flow.Launcher/Converters/MultilineTitleConverter.cs` | Collapses a multi-line result *title* to one line for display (see §9). Registered in `ResultListBox.xaml`. |
| `Flow.Launcher.Test/VimMotionEngineTest.cs` | Unit tests for the motion math, including the line-aware helpers. |

Key fields in `VimManager`:

- `_multiLineMode` — are we in the editor?
- `_multiLineBuffer` / `_singleLineBuffer` — the two persisted text buffers (see §4).
- `_editorScrollViewer` — the `PART_ContentHost` `ScrollViewerEx` inside the `TextBox` template, resolved
  lazily via `FindDescendantScrollViewer` in `HookEditorScroll()`. Used for the scrollbar/height fix (§5) and
  for keeping the gutter aligned on scroll.
- Chrome refs: `_clockPanel`, `_placeholderBox`, `_suggestionBox`, `_queryIconArea` (see §6).
- Overlay refs: `_vimLineGutter`, `_vimStatusBarHost` / `_vimModeSegment` / `_vimModeText` / `_vimStatusInfo`,
  `_vimBlockCaret`, `_vimModeIndicator`.
- `GutterWidth` (const `24`) — width reserved on the left for line numbers.

---

## 3. Entering & leaving the editor — `SetMultiLineMode(bool)`

Bound to **`Ctrl+Enter`** via `ToggleMultiLineMode()`. The handler is at the top of
`HandlePreviewKeyDown` so it works from any mode.

On **enter** (`SetMultiLineMode(true)`):

1. Stash the current single-line query into `_singleLineBuffer` (§4).
2. `AcceptsReturn = true`, `TextWrapping = Wrap`, `VerticalContentAlignment = Top`.
3. `MinHeight = MaxHeight = 220` — pins the box to a fixed height. **We never set the `Height` DP** (see §5
   for why that would be a bug).
4. `Margin = (0,7,0,7)` (drops Flow's 16px left margin that reserved space for the search icon) and
   `Padding = (GutterWidth, 6, 14, 32)` — left padding clears the gutter, right padding (14) leaves room for
   the scrollbar, bottom padding clears the mode line.
5. `ApplyEditorChrome(true)` — hide the launcher chrome, show the gutter, and resolve `_editorScrollViewer`
   (§6).
6. `EnableEditorScrollbar(true)` — turn the inner scroll viewer into a real, content-constraining scrollbar
   (§5).
7. `SetText(_multiLineBuffer)` then `SelectAll()` — restore the editor's own buffer and select it so typing
   starts over (§4).
8. `SwitchToInsert()` — land in Insert so you can type immediately.

On **exit** (`SetMultiLineMode(false)`) every one of those is reverted: `ClearValue` on
MinHeight/MaxHeight/Margin/Padding, `EnableEditorScrollbar(false)` restores the hidden scrollbar and clears the
inner-viewer clamp, `ApplyEditorChrome(false)` restores the chrome, and the single-line buffer is restored and
selected.

`UpdateStatusBar()` and `RedrawLineNumbers()` run at the end of both paths.

---

## 4. Separate text buffers

**Problem solved:** toggling `Ctrl+Enter` used to lose whatever you'd typed on the other side, because both
modes share one `TextBox`.

**Design:** two string fields hold each mode's text independently:

- `_singleLineBuffer` — the normal Flow search query.
- `_multiLineBuffer` — the editor scratchpad.

On every `SetMultiLineMode` switch we **stash the outgoing mode's text** into its buffer, then **restore the
incoming mode's buffer** via `SetText(...)`, then `SelectAll()`. The select-all mimics Flow's own
"remember the last query but pre-select it" behaviour: the text is there if you want it, but the first
keystroke replaces it — an easy "start over".

Persistence rules:

- **Across `Ctrl+Enter`:** both buffers survive; neither is cleared. This is the whole point.
- **Across hide/show:** both buffers persist. On hide, the latest editor text is captured into
  `_multiLineBuffer` (only when in editor mode); on show, if the editor was left open and the box came back
  empty, the buffer is restored. (See `ViewModel_PropertyChanged` on `MainWindowVisibilityStatus`.)
- **After `Ctrl+Shift+E` (external editor):** `_multiLineBuffer` is explicitly cleared — the content now lives
  in the file you handed off, so the editor starts fresh next time (§10).

> Note: `SetText` runs through the normal query path, so restoring `_singleLineBuffer` re-runs the search and
> shows results, exactly as if you'd typed it.

---

## 5. Fixed height + scrollbar — `EnableEditorScrollbar(bool)` (the subtle one)

This single method fixes **two** symptoms at once: the editor stretching the whole window on a big paste, and
the lack of a scrollbar. Both have the same root cause.

### The root cause

The `QueryTextBox` control template (`BaseQueryBoxStyle` in `Themes/Base.xaml`) hosts its content in an
`ui:ScrollViewerEx` named `PART_ContentHost` with **`VerticalScrollBarVisibility="Hidden"` hardcoded as a
literal** — *not* a `TemplateBinding`. Two consequences:

1. **The scrollbar can never be shown by the obvious route.** Setting
   `_queryTextBox.VerticalScrollBarVisibility` sets the *TextBox's* `ScrollViewer.*` attached property, which
   the template never reads. It's a dead no-op.
2. **`Hidden` measures content at infinite height.** In WPF a `ScrollViewer` in `Hidden` *or* `Disabled` mode
   passes `PositiveInfinity` as the available height to its child during measure — it does **not** clamp the
   child to the viewport the way `Auto`/`Visible` do. So with `Hidden`, the inner content reports its *full*
   natural height (e.g. ~1000px for 40 pasted lines).

Now combine that with two facts about the window:

- The `Window` has `SizeToContent="Height"` (`MainWindow.xaml`), so the window sizes itself to its content's
  desired height.
- `QueryTextBox.Height` is **`TwoWay`-bound** to `MainWindowHeight` → `Settings.WindowHeightSize` (~48).

During a rapid paste, a big `TextChanged` fires; the inner `ScrollViewerEx` (mode `Hidden`) re-measures its
content at infinite height and briefly reports a very tall desired size up the tree. `SizeToContent` consumes
that inflated height **before** the `TextBox`'s own `MaxHeight=220` clamp settles the subtree on the next
layout pass — so the window stretches tall to fit every line. It "self-corrects" the moment you move the
caret, because that triggers a fresh full measure/arrange in which the `MaxHeight` clamp wins and the WM_SIZE
hook re-asserts `SizeToContent`. `MaxHeight` is honoured in steady state; it's only *transiently* defeated by
the measure race. (This is the classic "`SizeToContent` + a `ScrollViewer` that measures at infinity" trap — a
`ScrollViewer` only caps window growth when it's actually allowed to scroll, i.e. `Auto`/`Visible`.)

### The fix

`EnableEditorScrollbar(true)` reaches the resolved inner viewer (`_editorScrollViewer`, the `PART_ContentHost`)
and sets, **on that instance only**:

```csharp
_editorScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
_editorScrollViewer.MaxHeight = 220; // pin the viewport; content scrolls inside
```

`Auto` makes the viewer measure its child against the viewport and scroll, so its desired height is bounded by
`MaxHeight` and can never inflate the window — **stretch fixed** — and it shows a scrollbar when content
overflows — **scrollbar fixed**. The bar is the thin, themed, auto-hiding WinUI/Fluent overlay scrollbar that
ships with `ui:XamlControlsResources` (loaded in `App.xaml`), so no extra styling is needed and it tracks the
active theme.

`EnableEditorScrollbar(false)` reverts **both** properties:

```csharp
_editorScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden; // template default
_editorScrollViewer.ClearValue(FrameworkElement.MaxHeightProperty);           // REQUIRED revert
```

The `ClearValue` is load-bearing: without it, single-line search inherits a 220px-capped inner viewport. This
was the one correction an adversarial review caught before shipping.

### Two rules to remember

1. **Never set the `Height` DP** to pin the editor. It's `TwoWay`-bound to `Settings.WindowHeightSize`, so any
   write *persists* and corrupts the single-line query-box height for good. Use `MinHeight`/`MaxHeight` on the
   `TextBox` and `MaxHeight` on the inner viewer instead — none of those write back to settings.
2. **Mutate the resolved `_editorScrollViewer` instance, never the shared template.** Editing the template (or
   styling `ScrollViewerEx` by name) would surface a scrollbar in single-line mode and the two clone boxes
   (`QueryTextPlaceholderBox`, `QueryTextSuggestionBox`) and would have to be duplicated across every theme
   file. `FindDescendantScrollViewer` finds whatever viewer the active theme produced, so the runtime approach
   is theme-agnostic.

`_editorScrollViewer` is null until the visual tree is realised, so `EnableEditorScrollbar` guards for null and
defers to a `DispatcherPriority.Loaded` callback on the very first entry.

---

## 6. Hiding the launcher chrome — `ApplyEditorChrome(bool)`

In editor mode we hide everything that belongs to single-line search and show the gutter:

- **Clock/date** (`ClockPanel`), **placeholder** (`QueryTextPlaceholderBox`), **suggestion ghost**
  (`QueryTextSuggestionBox`) — hidden with *both* `Visibility = Collapsed` **and** `Opacity = 0`. Belt and
  suspenders: the clock's opacity is animated (so `Opacity=0` alone gets overridden), and Flow re-asserts the
  placeholder's `Visibility` on query changes (so `Collapsed` alone gets overridden). Together they hide
  reliably. Restored with `ClearValue` on both.
- **Search icon + plugin-activation icon** — these two share one wrapping `<Border x:Name="QueryIconArea">`.
  We collapse the **whole Border**, not the individual icons. Collapsing just the plugin icon doesn't work:
  its style re-asserts the icon's visibility/opacity as the query changes, leaving an *invisible but
  still-laid-out* overlay that paints on top of the editor text (you could type "under" it and not see your
  text). Flow never touches the parent Border, so collapsing it removes the icon region from layout entirely.
- **Gutter** (`VimLineGutter`) — shown in editor mode, collapsed otherwise.

`ApplyEditorChrome(true)` also calls `HookEditorScroll()`, which resolves `_editorScrollViewer` and subscribes
to its `ScrollChanged` so the gutter stays aligned while scrolling.

---

## 7. Line-number gutter & status line

**Gutter** (`RedrawLineNumbers`): a `Canvas` (`VimLineGutter`, width = `GutterWidth` = 24) on which we draw one
right-aligned number per visible line. We get each line's vertical position from
`QueryTextBox.GetRectFromCharacterIndex(...)` at the start index of each line, offset by the current scroll
position. It's re-run on text change, caret move, and (via `OnEditorScrolled`) on scroll, so numbers track the
text as it wraps and scrolls. Numbers use the theme's subtle `Color08B` foreground.

**Mode line** (`UpdateStatusBar`): the full-width transparent `VimStatusBarHost` at the bottom shows an accent
"pill" (`VimModeSegment` + `VimModeText`) with the current mode name, and `VimStatusInfo` with `Ln`, `Col`, and
the character count. The pill uses the theme accent so it matches Flow's look rather than being a hard block.

---

## 8. Line-aware motions (`VimMotionEngine`)

The single-line fork treats the query as one line. The multi-line fork adds pure helpers so motions and
operators understand real line breaks. All are CRLF-tolerant and unit-tested in `VimMotionEngineTest`:

- `GetLineNumber(text, caret)`, `GetLineStart(text, caret)`, `GetLineEnd(text, caret)`, `GetColumn(text, caret)`
- `MoveUp` / `MoveDown` — column-preserving vertical motion
- `GetLineRange(text, caret, includeLineBreak)` — the current line's span
- `LineOperatorRange(text, caret, multiLine, includeLineBreak)` and `GetLinewiseRange(text, a, b)` — line-wise
  operator ranges (for `dd`, `cc`, `dj`/`dk`, etc.)
- `MoveFirstNonBlankOfLine(text, caret)` — for `^`

**Keys gated to multi-line mode** (they keep their single-line behaviour when the editor is off): `0` `^` `$`
operate on the current line rather than the whole query; `gg`/`G` (top/bottom, count-aware: `5G`/`5gg` jump to
line 5) are bound; `I`/`A` go to the start/end of the **current line** (not the whole buffer); `o`/`O` open a
line below/above and enter Insert; `dd`/`cc`/`yy` act on whole lines and take a count (`3dd`); line-wise
`dj`/`dk` (and `cj`/`yk`, etc.) act on whole lines and are `.`-repeatable; `J`/`gJ` join the current line with
the next (with / without a space; count-aware; also from Visual-Line); Visual-Line selects whole lines instead
of the whole buffer.

`Ctrl-A`/`Ctrl-X` (increment/decrement the number at the cursor), the `ib`/`aB` block-object aliases, and
counted word text objects (`2daw`) work in both modes and are shared with the single-line fork.

---

## 9. Sending to a plugin — result display + `Enter`

The editor is meant to feed a plugin (the `j` journaling command). Two pieces make that work:

- **`MultilineTitleConverter`** — when the query is multi-line, Flow's result *titles* would render across
  several lines and blow up the result row. This `IValueConverter` replaces `\r`/`\n` with spaces **for
  display only**, character-for-character, so the title collapses to one line while preserving the highlight
  offsets. Registered in `ResultListBox.xaml` and applied to the title `Binding`.
- **`Enter` to send** — in editor mode, `Enter` while **not** in Insert mode (i.e. you've `Esc`'d to
  Normal/Visual) runs `_viewModel.OpenResultCommand.Execute(null)`, sending the whole buffer to the selected
  result. In **Insert** mode, `Enter` instead inserts a bare `\n` (§10).
- **`Ctrl+J` / `Ctrl+K`** move the *result* selection, because plain `j`/`k` move between editor lines here.

So the journaling flow is: `Ctrl+Enter` → type the entry → `Esc` → (optionally `Ctrl+J/K` to pick the result)
→ `Enter` to send it to the plugin.

---

## 10. Unix line endings & paste

Emacs (and other editors) showed `^M` on lines written by the editor because WPF's `TextBox` inserts `\r\n`.
The editor is therefore **LF-only**:

- **`Enter` in Insert mode** inserts a bare `\n` (not WPF's default `\r\n`).
- **`Ctrl+V`** is intercepted (`PasteAtCaretNormalized`) and pastes clipboard text with line endings normalised
  to `\n` (via `NormalizeLf`), so CRLF text copied from other apps doesn't reintroduce `^M`.
- The Vim paste commands (`p`/`P`) also normalise.

## 11. External-editor handoff — `Ctrl+Shift+E`

When a note outgrows the fixed box, `Ctrl+Shift+E` (`OpenInExternalEditor`) writes the buffer to a temp file,
opens it with the OS default handler, leaves editor mode, clears `_multiLineBuffer`, and hides the launcher.
The philosophy: the small box is for quick entries; anything bigger belongs in a real editor.

---

## 12. Independence guarantee (single-line vs multi-line)

Everything editor-specific is applied in `SetMultiLineMode(true)` and reverted in `SetMultiLineMode(false)`
(or guarded behind `if (_multiLineMode)` / `if (_multiLineMode && ...)` in the key handlers). Concretely:

- Layout props (`AcceptsReturn`, `TextWrapping`, `VerticalContentAlignment`, `MinHeight`, `MaxHeight`,
  `Margin`, `Padding`) are `ClearValue`'d on exit.
- The inner viewer's `VerticalScrollBarVisibility` is restored to `Hidden` and its `MaxHeight` is `ClearValue`'d.
- Chrome is restored via `ClearValue`.
- The `Height` DP is never touched, so `Settings.WindowHeightSize` is never corrupted.

If you add a new editor tweak, **add its revert in the exit branch in the same commit.**

---

## 13. Editor features (drafts, conveniences, search, command-line, visual-block)

These were layered on top of the core editor; all live in `VimManager.cs` unless noted.

- **Crash-safe drafts** — `ScheduleDraftSave` debounce-writes the LF-normalized buffer to
  `%APPDATA%/FlowLauncher/vim-scratch-draft.txt` on every edit; `RestoreDraftIfAny` reloads it on the first
  editor open of a fresh process (only when there's no in-memory buffer), so a crash/reboot can't lose an
  entry. The draft is cleared after a `Ctrl+Shift+E` handoff; a normal send keeps it.
- **Auto-indent / auto-pair** — `TryAutoPair` (in `PreviewTextInput`) pairs `([{` and quotes (quotes skip
  apostrophes after a word char), steps over a close char, and Backspace deletes an empty pair; the Insert
  `Enter` handler carries the line's leading whitespace. Both gated by `Settings.VimEditorAutoPair/AutoIndent`.
- **Settings** — `Settings.EnableVimMultiLineEditor` (gates `Ctrl+Enter`), `VimEditorVisibleLines`
  (drives `_editorTextHeight`), `VimEditorAutoPair`, `VimEditorAutoIndent`, surfaced in `SettingsPaneGeneral.xaml`.
- **Search** — `/` `?` enter command-line mode; `ExecuteCommandLine` runs `FindNext`, which compiles the
  pattern via `BuildRegex` (regex, `SmartCaseIgnore` case folding, literal fallback on a parse error) and
  wraps around (`LastMatchIndex` walks backwards); `n`/`N` via `RepeatSearch`.
- **Command-line** — `EnterCommandLine`/`UpdateCommandLineDisplay` render `prefix + text` in the mode line
  (`VimCommandLine`); `RunExCommand` handles `:w`/`:wq`/`:x` (send), `:q` (close). `Substitute` does
  `:s`/`:%s` — regex per line (current line, or every line for `%`), flags `g`/`i`/`I`, .NET replacement syntax.
- **Marks** — `_marks` dictionary; `m`/`` ` ``/`'` set the `_pendingMark` prefix, the next key is the register,
  `HandleMark` stores/jumps (jumps go through `ExecuteMotion`, so they compose with operators).
- **Visual-block** — `VimModeType.VisualBlock`; entered by Normal/Visual `Ctrl-V`. The block is drawn by the
  `VimBlockSelection` overlay (`UpdateBlockSelection`, one rect per row); `BlockColEnd` gives each row's right
  edge, honoring the `_blockToEol` ($) flag. `h/l/j/k/0/$/w/b/e` move the corner; `BlockYank`/`BlockDelete`
  operate on the column span; `BlockInsert` + `CommitBlockInsert` replicate `Shift+I`/`Shift+A` typing across
  rows on Esc (I skips short rows, A pads them, `$`-A appends at each row's own end).

---

## 14. Manual test checklist

- `Ctrl+Enter` toggles in/out; toggling back and forth preserves each side's text; first keystroke after a
  toggle replaces the (selected) restored text.
- **Paste a large block fast** → the window stays at the fixed height and a scrollbar appears; it does **not**
  stretch then snap back.
- Scroll with the bar / `j`/`k` → gutter numbers stay aligned with their lines.
- `Esc` then `Enter` → the whole buffer is sent to the selected result; `Ctrl+J`/`Ctrl+K` change the result.
- Write multiple lines, send to a plugin, confirm **no `^M`** in the destination (LF only). Same after
  `Ctrl+V` of CRLF text.
- `Ctrl+Shift+E` opens the buffer in the external editor and the in-app buffer is cleared next time.
- Disable Vim mode in settings → the search bar behaves exactly like upstream (no gutter, no fixed height, no
  scrollbar leak, IME/caret restored).

---

## 15. Build & release

`.github/workflows/fork-release.yml` builds the installer + portable zip (Velopack via
`Scripts/post_build.ps1`) and publishes a GitHub Release on every push to `multiline-editor` (and on tags /
manual dispatch). The download link is always
[`/releases/latest`](https://github.com/namefailed/flowlauncher-vim-multiline-fork/releases/latest).

Engine-level fixes shared with the single-line fork are pulled from the `upstream-vim` remote and applied to
both repos so they stay in parity.
