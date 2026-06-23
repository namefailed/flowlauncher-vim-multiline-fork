# Multi-line editor — how it works

This is the as-built technical reference for the multi-line editor that this fork adds on top of the
single-line Vim mode. It is written so that, a year from now, you can re-open it and understand exactly what
was changed and why — including the WPF quirks that drove some non-obvious decisions.

For the user-facing summary and keybindings, see the [root README](../README.md) and
[`Flow.Launcher/VimMode/README.md`](../Flow.Launcher/VimMode/README.md). **Keybindings and end-user behaviour
live in `VimMode/README.md`; this document does not re-list keys — it explains the mechanism and the
rationale** behind what shipped.

## Contents

1. [Mental model](#1-mental-model) ·
2. [Code map](#2-code-map) ·
3. [Entering & leaving](#3-entering--leaving-the-editor--setmultilinemodebool) ·
4. [Text buffers](#4-separate-text-buffers) ·
5. [Fixed height + scrollbar](#5-fixed-height--scrollbar--enableeditorscrollbarbool-the-subtle-one) ·
6. [Hiding chrome](#6-hiding-the-launcher-chrome--applyeditorchromebool) ·
7. [Gutter, status line & block caret](#7-line-number-gutter-status-line--block-caret) ·
8. [Line-aware motions](#8-line-aware-motions-vimmotionengine) ·
9. [Send to plugin](#9-sending-to-a-plugin--result-display--enter) ·
10. [Unix line endings & paste](#10-unix-line-endings--paste) ·
11. [External editor](#11-external-editor-handoff--ctrlshifte) ·
12. [Independence guarantee](#12-independence-guarantee-single-line-vs-multi-line) ·
13. [Editor features](#13-editor-features) ·
14. [Test checklist](#14-manual-test-checklist) ·
15. [Build & release](#15-build--release)

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
| `Flow.Launcher/VimMode/VimManager.cs` | All editor wiring: `SetMultiLineMode`, `EnableEditorScrollbar`, `ApplyEditorChrome`, `LimitResultsToTop`, the gutter/status redraws, the two text buffers, the editor-only / Vim key handling (`Ctrl+Enter`, `Enter`, `Ctrl+J/K`, `Ctrl+V`, `Ctrl+Shift+E`, plus `Ctrl+R` redo and `Ctrl+A`/`Ctrl+X` increment/decrement), paste normalisation, search/command-line/marks/visual-block, and external-editor handoff. |
| `Flow.Launcher/VimMode/VimMotionEngine.cs` | Pure caret/range math. The multi-line fork adds the **line-aware** helpers (see §8). All unit-tested. |
| `Flow.Launcher/VimMode/VimEngine.cs` | The mode state machine (Insert / Normal / Visual / Visual Line / Visual Block). Unchanged from the single-line fork except for the Visual Block mode. |
| `Flow.Launcher/MainWindow.xaml` | Adds the editor overlays: `VimLineGutter` (Canvas), `VimStatusBarHost` (the mode line) with `VimModeSegment`/`VimModeText`/`VimCommandLine` (the `:`/`/`/`?` command-line text)/`VimStatusInfo`, `VimBlockCaret`, `VimModeIndicator`, `VimYankFlash` (the yank-highlight Canvas), and `VimBlockSelection` (the Canvas that draws the visual-block rectangles, §13). The `VimEditorScrollBarStyle` resource (a slim, thumb-only `ScrollBar`) also lives here and is applied at runtime to the editor's inner scroll viewer (see §5). Also where the shared `QueryIconArea` Border lives. |
| `Flow.Launcher/Converters/MultilineTitleConverter.cs` | Collapses a multi-line result *title* to one line for display (see §9). Registered in `ResultListBox.xaml`. |
| `Flow.Launcher.Test/VimMotionEngineTest.cs` | Unit tests for the motion math, including the line-aware helpers. |

Key fields in `VimManager`:

- `_multiLineMode` — are we in the editor?
- `_multiLineBuffer` / `_singleLineBuffer` — the two persisted text buffers (see §4).
- `_editorScrollViewer` — the `PART_ContentHost` `ScrollViewerEx` inside the `TextBox` template, resolved
  lazily via `FindDescendantScrollViewer` in `HookEditorScroll()`. Used for the scrollbar/height fix (§5) and
  for keeping the gutter aligned on scroll.
- Chrome refs: `_clockPanel`, `_placeholderBox`, `_suggestionBox`, `_queryIconArea` (see §6).
- Overlay refs: `_vimLineGutter`, `_vimStatusBarHost` / `_vimModeSegment` / `_vimModeText` / `_vimStatusInfo` /
  `_vimCommandLine`, `_vimBlockCaret`, `_vimModeIndicator`, `_vimBlockSelection`.
- `GutterWidth` (const `24`) — width reserved on the left for line numbers.
- Consts `EditorModeStrip` (`34`) — the strip reserved below the text viewport for the mode line — and
  `EditorTopMargin` (`7`), the editor's top margin.
- `_editorTextHeight` — the inner text viewport height (the explicit `Height` set on `_editorScrollViewer`),
  computed on entry as `Math.Max(3, Settings.VimEditorVisibleLines) * MeasureEditorLineHeight()` (a 3-line
  floor); the `TextBox`'s pinned height is `_editorTextHeight + EditorModeStrip`. See §5.
- `MeasureEditorLineHeight()` — measures one text line's height from the query box's font via `FormattedText`
  ("Xg"), with no layout pass.
- `_queryBoxArea` — the query-box `Grid` (`QueryBoxArea`) holding the query box + overlays; in the editor its
  `MaxHeight` is capped and `ClipToBounds` set as the deterministic window ceiling (§5), reverted via
  `ClearValue` on exit.
- `_resultListBox` / `_resultMaxHeightBinding` — used by `LimitResultsToTop`: on editor entry it captures the
  result list's bound `MaxHeight` into `_resultMaxHeightBinding` (once) and caps `_resultListBox.MaxHeight` to
  one row (`ItemHeightSize`); on exit it restores the captured binding (§3, §9).

---

## 3. Entering & leaving the editor — `SetMultiLineMode(bool)`

Bound to **`Ctrl+Enter`** via `ToggleMultiLineMode()`. The handler sits near the top of `HandlePreviewKeyDown`
(after the command-line capture block), so it works from Insert / Normal / Visual. Entering is gated by
`Settings.EnableVimMultiLineEditor`; leaving the editor is always allowed.

On **enter** (`SetMultiLineMode(true)`):

1. Stash the current single-line query into `_singleLineBuffer` (§4).
2. `AcceptsReturn = true`, `TextWrapping = Wrap`, `VerticalContentAlignment = Top`.
3. Compute the text-viewport height from settings:
   `_editorTextHeight = Math.Max(3, Settings.VimEditorVisibleLines) * MeasureEditorLineHeight()` (default 9
   lines, clamped 3–20). Then `boxHeight = _editorTextHeight + EditorModeStrip` (the 34px strip below the text
   for the mode line) and pin the box: `MinHeight = MaxHeight = boxHeight`. Sizing to an exact whole number of
   text lines means the bottom line is never half-clipped. **We never set the `Height` DP** (see §5 for why
   that would be a bug); the real text bound is the inner viewer's explicit `Height` (§5), not the box height.
4. `Margin = (0, EditorTopMargin=7, 0, 0)` — drops Flow's 16px left margin that reserved space for the search
   icon — and `Padding = (GutterWidth, 2, 14, 0)`: left padding clears the gutter, right padding (14) leaves
   room for the scrollbar. The space for the mode line is reserved by the box being `EditorModeStrip` taller
   than the inner text viewport, not by padding.
5. **Hard-cap the query-box `Grid`** (`_queryBoxArea`): `MaxHeight = EditorTopMargin + boxHeight` and
   `ClipToBounds = true`. This is the deterministic window ceiling on a fast paste — see §5. Reverted on exit.
6. `LimitResultsToTop(true)` — cap the results list to a single row (`_resultListBox.MaxHeight =
   Settings.ItemHeightSize`) so only the top-most result shows beneath the editor. The original bound
   `MaxHeight` is captured once into `_resultMaxHeightBinding` and restored on exit.
7. `ApplyEditorChrome(true)` — hide the launcher chrome, show the gutter, and resolve `_editorScrollViewer`
   (§6).
8. `EnableEditorScrollbar(true)` — turn the inner scroll viewer into a real, content-constraining scrollbar
   with a fixed viewport height (§5).
9. `RestoreDraftIfAny()` — on the first editor open of a fresh process, recover a crash/restart-orphaned entry
   into `_multiLineBuffer` before it is shown (§13.1).
10. `SetText(_multiLineBuffer)` — restore the editor's own buffer (§4).
11. `SwitchToInsert()` — land in Insert so you can type immediately.
12. `SelectAll()` — select the restored buffer so the first keystroke replaces it (§4). Called *after*
    `SwitchToInsert()`, matching `_vimEngine.SwitchToInsert(); _queryTextBox.SelectAll();`.

On **exit** (`SetMultiLineMode(false)`) every one of those is reverted: `AcceptsReturn` / `TextWrapping` /
`VerticalContentAlignment` are set back to their single-line values (`false` / `NoWrap` / `Center`);
`ClearValue` on `MinHeight` / `MaxHeight` / `Margin` / `Padding`; the `_queryBoxArea` cap is undone
(`ClearValue` on `MaxHeight` and `ClipToBounds`); `LimitResultsToTop(false)` restores the multi-result list;
`EnableEditorScrollbar(false)` restores the hidden scrollbar and clears the inner-viewer overrides;
`ApplyEditorChrome(false)` restores the chrome; and the single-line buffer is restored and selected.

`UpdateStatusBar()` and `RedrawLineNumbers()` run at the end of both paths.

---

## 4. Separate text buffers

**Problem solved:** toggling `Ctrl+Enter` used to lose whatever you'd typed on the other side, because both
modes share one `TextBox`.

**Design:** separate string state holds each mode's text independently:

- `_singleLineBuffer` — the normal Flow search query.
- `_buffers` (3 slots: left / main / right) — the editor scratchpads, with `_bufIndex` for the active one.
  `_multiLineBuffer` is a **property** that proxies `_buffers[_bufIndex]`, so all the buffer/hide-show/draft
  code below acts on the active editor buffer transparently. `Ctrl+L` / `Ctrl+H` (`SwitchBuffer`) cycle the
  slots; the mode line shows `buf n/3`. `Ctrl+X` (`ClearBuffer`) empties the active slot (via `SetText`, so
  `u` un-clears). Switching sets the text directly (not `SetText`) and **clears the undo/redo stacks**, so `u`
  can't drag one buffer's text into another — undo doesn't cross a buffer switch.

On every `SetMultiLineMode` switch we **stash the outgoing mode's text** into its buffer, then **restore the
incoming mode's buffer** via `SetText(...)`, then `SelectAll()`. The select-all mimics Flow's own
"remember the last query but pre-select it" behaviour: the text is there if you want it, but the first
keystroke replaces it — an easy "start over". (`SwitchBuffer`, by contrast, does *not* select-all — you keep
editing the buffer you moved to.)

Persistence rules:

- **Across `Ctrl+Enter`:** both buffers survive; neither is cleared. This is the whole point.
- **Across hide/show:** both buffers persist. On hide, the latest editor text is captured into
  `_multiLineBuffer` (only when in editor mode); on show, if the editor was left open and the box came back
  empty, the buffer is restored. (See `ViewModel_PropertyChanged` on `MainWindowVisibilityStatus`.)
  On the hide path the text is read from the view-model's `QueryText` string — a plain mirror of the box —
  **not** `_queryTextBox.Text`. The reason is threading: `MainViewModel.Hide()` is `async void` and, after its
  `await`, can resume on a thread-pool thread, where it flips `MainWindowVisibilityStatus`. That setter fires
  `PropertyChanged` synchronously, so `ViewModel_PropertyChanged` runs on that background thread; touching the
  WPF `TextBox` there throws a cross-thread `InvalidOperationException` that silently crashes the app. Reading
  the plain `QueryText` string is thread-safe and avoids it. *(This was a real crash diagnosed via the Flow
  logs before it was fixed — do not "simplify" it back to `_queryTextBox.Text`.)*
- **After a normal send (`Esc`+`Enter`, or `:w`/`:wq`/`:x`):** the buffer is **kept** and the draft is re-saved
  (`SaveDraft`). The send is a blind execute of whatever result happens to be selected, so retaining the entry
  makes an accidental send recoverable — re-open the editor and it's still there (§9, §13.1).
- **After `Ctrl+Shift+E` (external editor):** `_multiLineBuffer` and the recovery draft are explicitly cleared
  — the content now lives in the file you handed off, so the editor starts fresh next time (§11).

> Note: `SetText` runs through the normal query path, so restoring `_singleLineBuffer` re-runs the search and
> shows results, exactly as if you'd typed it.

---

## 5. Fixed height + scrollbar — `EnableEditorScrollbar(bool)` (the subtle one)

This method, together with the `_queryBoxArea` Grid cap from §3, fixes **two** symptoms at once: the editor
stretching the whole window on a big paste, and the lack of a scrollbar. Both have the same root cause.

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
- `QueryTextBox.Height` is **`TwoWay`-bound** to `MainWindowHeight` → `Settings.WindowHeightSize` (42 by
  default).

During a rapid paste, a big `TextChanged` fires; the inner `ScrollViewerEx` (mode `Hidden`) re-measures its
content at infinite height and briefly reports a very tall desired size up the tree. Under `SizeToContent`, a
per-control `MaxHeight` on the `TextBox` alone isn't enough — a fast paste / huge wrapped line can inflate the
subtree before that clamp settles on the next layout pass. So the **deterministic window ceiling** is the
shared query-box `Grid` (`_queryBoxArea`), not the `TextBox`: on entry we set
`_queryBoxArea.MaxHeight = EditorTopMargin + boxHeight` and `_queryBoxArea.ClipToBounds = true` (§3). That
Grid's `MaxHeight` clamp is reliable, and the clip guarantees no child can paint past it, so the window holds a
fixed height and **never transiently stretches then snaps back** (see the §14 test bullet). The root content
is a vertical `StackPanel`, which measures its children at infinite height, which is exactly why a
self-measuring `ScrollViewer` could otherwise balloon it. (This is the classic "`SizeToContent` + a
`ScrollViewer` that measures at infinity" trap, fixed by capping a reliable container rather than relying on
the TextBox's own `MaxHeight`.)

### The fix

`EnableEditorScrollbar(true)` reaches the resolved inner viewer (`_editorScrollViewer`, the `PART_ContentHost`)
and sets, **on that instance only**:

```csharp
_editorScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
_editorScrollViewer.Height = _editorTextHeight;          // EXPLICIT fixed height, not MaxHeight (see below)
_editorScrollViewer.VerticalAlignment = VerticalAlignment.Top; // sit at the top; the mode-line strip stays below
_editorScrollViewer.ClipToBounds = true;                 // a huge wrapped line can't paint into the mode line
_editorScrollViewer.Margin = new Thickness(0);
// Slim, thumb-only scrollbar, scoped to this viewer instance only:
_editorScrollViewer.Resources[typeof(ScrollBar)] = (Style)_mainWindow.FindResource("VimEditorScrollBarStyle");
```

`Auto` makes the viewer measure its child against the viewport and scroll, so its desired height is bounded by
its **explicit `Height`** (`_editorTextHeight` = visible lines × measured line height) and can never inflate
the window — **stretch fixed** — and it shows a scrollbar when content overflows — **scrollbar fixed**. An
explicit `Height` is used rather than `MaxHeight` deliberately: with `MaxHeight` a single very long wrapped
line can still balloon the viewport during the measure race, whereas a fixed `Height` (plus `ClipToBounds`)
bounds and clips it no matter how the `TextBox` tries to grow. The bar is the slim, thumb-only
`VimEditorScrollBarStyle` (defined in `MainWindow.xaml`) applied to this viewer instance's `Resources` only, so
single-line mode is untouched.

`EnableEditorScrollbar(false)` reverts everything it set:

```csharp
_editorScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden; // template default
_editorScrollViewer.ClearValue(FrameworkElement.HeightProperty);              // REQUIRED revert (load-bearing)
_editorScrollViewer.ClearValue(FrameworkElement.MaxHeightProperty);           // defensive; editor no longer sets it
_editorScrollViewer.ClearValue(FrameworkElement.VerticalAlignmentProperty);
_editorScrollViewer.ClearValue(UIElement.ClipToBoundsProperty);
_editorScrollViewer.ClearValue(FrameworkElement.MarginProperty);
_editorScrollViewer.Resources.Remove(typeof(ScrollBar));                      // drop the slim scrollbar style
```

The `Height` `ClearValue` is the load-bearing one: without it, single-line search inherits a fixed-height,
top-aligned, clipped inner viewport. Every other editor-only property set above (`VerticalAlignment`,
`ClipToBounds`, `Margin`, the scrollbar style) is reverted in the same branch so single-line mode is
byte-for-byte unchanged.

### Two rules to remember

1. **Never set the `Height` DP** to pin the editor. It's `TwoWay`-bound to `Settings.WindowHeightSize`, so any
   write *persists* and corrupts the single-line query-box height for good. Use `MinHeight`/`MaxHeight` on the
   `TextBox`, an explicit `Height` on the inner `_editorScrollViewer`, and `MaxHeight` + `ClipToBounds` on the
   `_queryBoxArea` Grid instead — none of those write back to settings.
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
  We collapse the **whole Border** with `Visibility = Collapsed` only (no `Opacity = 0`), since collapsing the
  parent removes the entire icon region from layout. Collapsing just the plugin icon doesn't work: its style
  re-asserts the icon's visibility/opacity as the query changes, leaving an *invisible but still-laid-out*
  overlay that paints on top of the editor text (you could type "under" it and not see your text). Flow never
  touches the parent Border, so collapsing it removes the icon region from layout entirely.
- **Gutter** (`VimLineGutter`) — shown in editor mode, collapsed otherwise.

`ApplyEditorChrome(true)` also calls `HookEditorScroll()`, which resolves `_editorScrollViewer` and subscribes
to its `ScrollChanged` so the gutter stays aligned while scrolling.

---

## 7. Line-number gutter, status line & block caret

**Gutter** (`RedrawLineNumbers`): a `Canvas` (`VimLineGutter`, fixed `Width="24"` in XAML, matching the
`GutterWidth` const used for the text padding and per-number width) on which we draw one right-aligned number
per visible line. We get each line's **viewport-relative** vertical position from
`QueryTextBox.GetRectFromCharacterIndex(...)` at the start index of each line, draw a number only when the
line's top falls within the fixed text viewport (`rect.Top >= -1 && rect.Top < viewHeight`, where `viewHeight`
is `_editorTextHeight`), and vertically centre the number within the row. There is no explicit "add the scroll
offset" step — `GetRectFromCharacterIndex` already returns viewport-relative coordinates. It re-runs on text
change, caret move, and (via `OnEditorScrolled`) on scroll, so numbers track the text as it wraps and scrolls.
Numbers use the theme's subtle `Color08B` foreground.

**Whole-line scroll snapping** (`SnapScrollToLine`, called from `OnEditorScrolled` on every `ScrollChanged`):
the editor rounds its vertical offset to the nearest whole-line boundary (`Math.Round(off / lineH) * lineH`),
so the top line is never shown half-off and every gutter number has visible text beside it. This is what keeps
the gutter honest — because `RedrawLineNumbers` only numbers a line whose top is within the viewport, a
partially-scrolled top line would otherwise get a number but no rendered text. The reentrancy guard
`_snappingScroll` stops the programmatic `ScrollToVerticalOffset` from re-triggering itself. The snap's line
height comes from `EditorLineHeight()` — the height of an on-screen line measured via
`GetRectFromCharacterIndex` — which is distinct from `MeasureEditorLineHeight()`, the font-metric height used
on entry to size the viewport (§5).

**Mode line** (`UpdateStatusBar`): the full-width transparent `VimStatusBarHost` at the bottom shows an accent
"pill" (`VimModeSegment` + `VimModeText`) with the current mode name (`NORMAL` / `VISUAL` / `V-LINE` /
`V-BLOCK` / `INSERT`), and `VimStatusInfo` with `Ln`, `Col`, and the character count. While you're typing a `:`
/ `/` / `?` command, `VimCommandLine` shows it in the same strip (§13.4–13.5). The pill uses the theme accent
so it matches Flow's look rather than being a hard block.

**Block caret** — in any non-Insert mode the native caret is hidden (`CaretBrush=Transparent`) and a
`VimBlockCaret` Rectangle is positioned over the current char via `GetRectFromCharacterIndex`; the IME is
suspended (`InputMethod.SetIsInputMethodSuspended`) so dead keys / composition don't fire while keys are
commands. After a text mutation the new caret position has no layout rect yet, so `UpdateCaretPosition` re-runs
once at `DispatcherPriority.Loaded` (guarded by `_caretRedrawPending`) to stop the block caret blinking out
after a paste/edit.

---

## 8. Line-aware motions (`VimMotionEngine`)

The single-line fork treats the query as one line. The multi-line fork adds pure helpers so motions and
operators understand real line breaks. All are CRLF-tolerant and unit-tested in `VimMotionEngineTest`:

- `GetLineNumber(text, caret)`, `GetLineStart(text, caret)`, `GetLineEnd(text, caret)`, `GetColumn(text, caret)`
- `MoveUp` / `MoveDown` — column-preserving vertical motion
- `GetLineRange(text, caret, includeLineBreak)` — the current line's span
- `LineOperatorRange(text, caret, multiLine, includeLineBreak)` and `GetLinewiseRange(text, a, b)` — line-wise
  operator ranges (for `dd`, `cc`, `dj`/`dk`, etc.)
- `MoveFirstNonBlankOfLine(text, caret)` — for `^`; `MoveLastNonBlank(text)` — for `g_`

**Keys gated to multi-line mode** (they keep their single-line behaviour when the editor is off): `0` `^` `$`
operate on the current line rather than the whole query; `gg`/`G` (top/bottom, count-aware: `5G`/`5gg` jump to
line 5) work **only in the editor** (no-op in single-line mode); `I`/`A` go to the start/end of the **current
line** (not the whole buffer); `o`/`O` open a line below/above and enter Insert; `dd`/`cc`/`yy` act on whole
lines and take a count (`3dd`); line-wise `dj`/`dk` (and `cj`/`yk`, etc.) act on whole lines and are
`.`-repeatable; `J`/`gJ` join the current line with the next (with / without a space; count-aware; also from
Visual-Line); Visual-Line selects whole lines instead of the whole buffer.

`Ctrl-A`/`Ctrl-X` (increment/decrement the number at the cursor), the `ib`/`aB` block-object aliases, and
counted word text objects (`2daw`) work in both modes and are shared with the single-line fork.

---

## 9. Sending to a plugin — result display + `Enter`

The editor is meant to feed a plugin (the `j` journaling command). Three pieces make that work:

- **`MultilineTitleConverter`** — when the query is multi-line, Flow's result *titles* would render across
  several lines and blow up the result row. This `IValueConverter` replaces `\r`/`\n` with spaces **for
  display only**, character-for-character, so the title collapses to one line while preserving the highlight
  offsets. Registered in `ResultListBox.xaml` and applied to the title `Binding`.
- **`Enter` to send** — in editor mode, `Enter` while **not** in Insert mode (i.e. you've `Esc`'d to
  Normal/Visual) runs `_viewModel.OpenResultCommand.Execute(null)`, sending the whole buffer to the selected
  result. Before executing, the handler first retains the entry as accidental-send insurance —
  `_multiLineBuffer = _queryTextBox.Text; SaveDraft(_multiLineBuffer);` (`SaveDraft` writes the LF-normalised
  buffer to the on-disk draft) — so a blind execute of the wrong result is recoverable (§4). The same
  retain-then-send pattern backs the `:w`/`:wq`/`:x` ex-commands (§13.5). In **Insert** mode, `Enter` instead
  inserts a bare `\n` (§10).
- **Result list capped to one row** — `LimitResultsToTop(true)` (§3) limits the list to a single visible row,
  so the editor shows only the top-most result. `Ctrl+J` / `Ctrl+K` still move the selection through the full
  result set, one visible row at a time, so you can pick a non-top result even though only one row is shown
  (plain `j`/`k` move between editor lines here). The cap is reverted on editor exit.

So the journaling flow is: `Ctrl+Enter` → type the entry → `Esc` → (optionally `Ctrl+J/K` to pick the result)
→ `Enter` to send it to the plugin.

---

## 10. Unix line endings & paste

Emacs (and other editors) showed `^M` on lines written by the editor because WPF's `TextBox` inserts `\r\n`.
The editor is therefore **LF-only**:

- **`Enter` in Insert mode** inserts a bare `\n` (not WPF's default `\r\n`). *(Editor-only, gated on
  `_multiLineMode`.)*
- **`Ctrl+V` in Insert mode** is intercepted (`PasteAtCaretNormalized`) and pastes clipboard text with line
  endings normalised to `\n` (via `NormalizeLf`), so CRLF text copied from other apps doesn't reintroduce
  `^M`. *(In Normal/Visual mode, `Ctrl+V` instead toggles Visual-Block — see §13.7.)*
- **The Vim paste commands (`p`/`P`)** normalise to `\n` in **both** modes (a no-op in single-line search,
  which has no newlines).

**Line-wise vs char-wise paste.** `Paste(...)` decides whether to paste whole lines or inline with
`bool linewise = _lastYankLinewise && clip == NormalizeLf(_lastYankText);`. `_lastYankLinewise` is set true
only by line-wise yanks/deletes in the editor — `dd`/`cc`/`yy` and Visual-Line set it to `_multiLineMode` (so
they are char-wise in single-line mode), while `dj`/`dk`/`yj`/`yk` (`ApplyLinewiseOperator`) set it
unconditionally but are themselves editor-only. The clipboard-equality guard means a line-wise paste only
happens while our own line-wise yank is still on the clipboard; if you copy from another app in between, the
equality fails and `p`/`P` fall back to char-wise — matching Vim's "the register is char-wise unless it was set
line-wise" behaviour without a real register system.

---

## 11. External-editor handoff — `Ctrl+Shift+E`

When a note outgrows the fixed box, `Ctrl+Shift+E` (`OpenInExternalEditor`) writes the (LF-normalised) buffer
to a temp file and opens it with the OS default handler, and **only after the write + launch succeed** (in a
`try`/`catch`): leaves editor mode, clears `_multiLineBuffer`, clears the recovery draft (`SaveDraft("")`), and
hides the launcher. If the write/launch throws, the buffer and editor are kept so the entry isn't lost. The
philosophy: the small box is for quick entries; anything bigger belongs in a real editor.

---

## 12. Independence guarantee (single-line vs multi-line)

Everything editor-specific is applied in `SetMultiLineMode(true)` and reverted in `SetMultiLineMode(false)`
(or guarded behind `if (_multiLineMode)` / `if (_multiLineMode && ...)` in the key handlers). Concretely, on
exit:

- TextBox layout props: `AcceptsReturn`, `TextWrapping`, and `VerticalContentAlignment` are set back to their
  single-line values (`false` / `NoWrap` / `Center`), and `MinHeight`, `MaxHeight`, `Margin`, and `Padding`
  are `ClearValue`'d.
- The `_queryBoxArea` Grid's `MaxHeight` and `ClipToBounds` are `ClearValue`'d.
- The result-list `MaxHeight` binding is restored (`LimitResultsToTop(false)` re-applies the captured binding,
  or `ClearValue`s if none was captured).
- The inner viewer's `VerticalScrollBarVisibility` is set back to `Hidden`, and its `Height`, `MaxHeight`,
  `VerticalAlignment`, `ClipToBounds`, and `Margin` are `ClearValue`'d (and the slim scrollbar style is removed
  from its `Resources`).
- Chrome is restored via `ClearValue` (§6).
- The `Height` DP is never touched, so `Settings.WindowHeightSize` is never corrupted.

If you add a new editor tweak, **add its revert in the exit branch in the same commit.**

---

## 13. Editor features

Layered on top of the core editor; all live in `VimManager.cs` unless noted. This section explains
*mechanism and rationale* — for the keys themselves see [`VimMode/README.md`](../Flow.Launcher/VimMode/README.md).

### 13.1 Crash-safe drafts

`ScheduleDraftSave` debounce-writes (≈1s after typing stops, via a `DispatcherTimer`) to
`%APPDATA%/FlowLauncher/vim-scratch-draft.txt`; `SaveDraft` does the locked, LF-normalised write and is safe to
call from any thread. It persists **all three buffers** (§4) plus the active index, NUL-delimited
(`index\0buf0\0buf1\0buf2`) — NUL can't appear in typed text, so it's a safe separator. `RestoreDraftIfAny`
reloads them on the first editor open of a fresh process — only when no buffer has content yet (`_draftRestored`
makes it run once) — so a crash/reboot can't lose a half-written entry; a legacy single-string draft (no NULs)
is migrated into the main slot. The active buffer's draft is cleared after a `Ctrl+Shift+E` handoff (§11); a
normal send keeps it (§9, §4).

### 13.2 Auto-indent & auto-pair

`TryAutoPair` (in `QueryTextBox_PreviewTextInput`) pairs `([{` and quotes (quotes skip apostrophes after a word
char, so prose isn't mangled), steps over a typed close char, and Backspace deletes an empty pair; the
Insert-mode `Enter` handler carries the current line's leading whitespace to the new line. Both are gated by
`Settings.VimEditorAutoPair` / `VimEditorAutoIndent` and only run in the editor in Insert mode. Auto-pair edits
through `SetCurrentValue` and does **not** push an undo snapshot (see §13 undo note), so it stays inside the
current insert session.

### 13.3 Editor settings

`Settings.EnableVimMultiLineEditor` (default **true**; gates whether `Ctrl+Enter` can enter the editor),
`VimEditorVisibleLines` (default **9**, clamped 3–20; drives
`_editorTextHeight = VimEditorVisibleLines * MeasureEditorLineHeight()`), `VimEditorAutoPair` (default true),
`VimEditorAutoIndent` (default true) — all surfaced in `SettingsPaneGeneral.xaml` under the *Enable Advanced
Vim Mode* expander. Because the editor toggle defaults on, `Ctrl+Enter` works out of the box once Vim mode is
enabled.

### 13.4 In-buffer search (`/` `?` `n` `N`)

`/` `?` enter command-line mode; `ExecuteCommandLine` routes `/`/`?` to `FindNext` (and `:` to `RunExCommand`).
`FindNext` compiles the pattern via `BuildRegex` — a .NET regex with `SmartCaseIgnore` case folding
(case-insensitive unless the pattern has an uppercase letter) and a literal-escape fallback on a parse error,
which mirrors Vim magic-mode handling of characters like a lone `(`. Forward search uses `re.Match(text, start)`
and wraps to index 0; backward search uses `LastMatchIndex` (the last match at-or-before the start position,
found by iterating matches forward and keeping the last qualifying one) and wraps to the buffer end. `n`/`N`
repeat via `RepeatSearch`.

### 13.5 Command-line (`:` and `:s`)

`EnterCommandLine`/`UpdateCommandLineDisplay` render `prefix + text` in the mode line (`VimCommandLine`).
`RunExCommand` strips a leading `%` (sets whole-buffer), then handles `:w`/`:wq`/`:x` (send — same
retain-then-send as `Esc`+`Enter`, §9), `:q`/`:q!` (close). `Substitute` does `:s`/`:%s`: a regex replace per
line — current line by default, every line for `%` — so a non-global `:%s` still replaces the first match on
every line, matching Vim. Flags `g` (every match on a line), `i`/`I` (force ignore-/match-case, otherwise
smart-case); the replacement uses .NET syntax (`$1` for groups, `$&` for the whole match).

### 13.6 Marks

`_marks` is a `Dictionary<char,int>`. `m`/`` ` ``/`'` set the `_pendingMark` prefix; the next key (any letter
or digit) is the register; `HandleMark` stores the caret (`m`) or jumps — `` ` `` to the exact position, `'` to
the line's first non-blank. Jumps route through `ExecuteMotion`/`ExecuteVisualMotion`, so they compose with a
pending operator (e.g. `` d`a ``) and extend a Visual selection.

### 13.7 Visual-block

`VimModeType.VisualBlock`, entered by Normal/Visual `Ctrl-V` (editor only). The block is drawn by the
`VimBlockSelection` overlay (`UpdateBlockSelection`, one translucent rect per row); `BlockBounds` gives the
row/column extent and `BlockColEnd` gives each row's right edge, honouring the `_blockToEol` (`$`) flag.
`h/l/j/k/0/$/w/b/e` move the active corner (the block reshapes around it); `BlockYank`/`BlockDelete` operate on
the column span; `BlockInsert` + `CommitBlockInsert` implement `Shift+I`/`Shift+A` — the text typed on the top
row is replicated to the other rows **on `Esc`** (and skipped if it contains a newline). For Vim fidelity, `I`
skips rows shorter than the column, `A` pads short rows with spaces, and after `$` an `A` appends at each row's
own end.

### Undo model

Vim mode keeps its *own* operation-level undo/redo stacks (`_undoStack`/`_redoStack`); WPF's native TextBox
undo is not used. Every text-mutating Vim command routes through `SetText`, which snapshots `(text, caret)`
onto the undo stack and clears redo before mutating. Entering Insert is deliberately treated as one unit:
`i`/`I`/`a`/`A` call `PushUndo` once before `SwitchToInsert`, `o`/`O` snapshot the pre-newline state through
`SetText`, and plain typed characters and auto-pair (no snapshot) stay inside that one unit — so the operation
plus everything typed afterward reverts with a single `u`. The one exception: pressing **`Enter`** in Insert
mode (the auto-indent path) goes through `SetText`, so it *does* push a snapshot — an insert session that spans
line breaks undoes one line-segment at a time. `Ctrl+R` redoes; both `u` and `Ctrl+R` are gated to Normal mode.

---

## 14. Manual test checklist

- `Ctrl+Enter` toggles in/out; toggling back and forth preserves each side's text; first keystroke after a
  toggle replaces the (selected) restored text.
- **Paste a large block fast** → the window stays at the fixed height and a scrollbar appears; it does **not**
  stretch then snap back.
- Scroll with the bar / `j`/`k` → gutter numbers stay aligned with their lines; the top line is never
  half-clipped (scroll snapping).
- `Esc` then `Enter` → the whole buffer is sent to the selected result; only one result row shows but
  `Ctrl+J`/`Ctrl+K` still change the selection. Re-open the editor → the just-sent entry is **still there**.
- Kill Flow (or reboot) with a half-written entry open, relaunch, `Ctrl+Enter` → the draft is restored.
- Write multiple lines, send to a plugin, confirm **no `^M`** in the destination (LF only). Same after
  `Ctrl+V` of CRLF text.
- `Ctrl+Shift+E` opens the buffer in the external editor and the in-app buffer + draft are cleared next time.
- Disable Vim mode in settings → the search bar behaves exactly like upstream (no gutter, no fixed height, no
  scrollbar leak, IME/caret restored, full multi-result list).

---

## 15. Build & release

`.github/workflows/fork-release.yml` builds the installer + portable zip (Velopack via
`Scripts/post_build.ps1`) and publishes a GitHub Release on every push to `multiline-editor` (and on tags /
manual dispatch). The download link is always
[`/releases/latest`](https://github.com/namefailed/flowlauncher-vim-multiline-fork/releases/latest).

Engine-level fixes shared with the single-line fork are pulled from the `upstream-vim` remote and applied to
both repos so they stay in parity.
