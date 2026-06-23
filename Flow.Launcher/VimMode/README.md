# Vim Mode

An **opt-in**, terminal-style Vim editing layer for the Flow Launcher search bar. It turns the query box
into a small modal editor so you can navigate and fix long queries without leaving the home row.

The feature is **disabled by default**. Enable it under **Settings → General → "Enable Advanced Vim Mode"**.
When off, the search bar behaves exactly as it always has.

> **This is the multi-line fork.** Press `Ctrl+Enter` to turn the search box into a fixed-size, scrollable,
> multi-line Vim editor (line numbers, mode line, and send-to-plugin on `Enter`). Every keybinding below still
> applies; in the editor the line-wise commands act on real lines. This doc covers the **keys and behaviour**;
> the *how and why* of the editor live in [`docs/MULTILINE_EDITOR.md`](../../docs/MULTILINE_EDITOR.md).

## Design

The implementation is split into three pieces so the logic stays testable:

| Type | Responsibility |
| --- | --- |
| [`VimEngine`](VimEngine.cs) | The mode state machine (Insert / Normal / Visual / Visual Line / Visual Block) and the `ModeChanged` event. |
| [`VimMotionEngine`](VimMotionEngine.cs) | Pure, side-effect-free caret/range math (motions, text objects, operator ranges). Fully unit-tested. |
| [`VimManager`](VimManager.cs) | Wires the engines into the WPF `MainWindow`: key interception, the block-caret overlay, the mode indicator, and clipboard operations. Implements `IDisposable` and is torn down in `MainWindow.Dispose`. |

Unit tests live in [`Flow.Launcher.Test`](../../Flow.Launcher.Test) (`VimEngineTest`, `VimMotionEngineTest`).

## Modes

- **Mode indicator** — a small, color-coded dot at the left of the search bar shows the current mode:
  accent for **Normal**, purple for **Visual**, orange for **Visual Line**, and teal for **Visual Block**. In
  Insert mode the dot is hidden. (In the multi-line editor the dot is hidden too; the mode line shows a text
  label instead — `NORMAL` / `VISUAL` / `V-LINE` / `V-BLOCK` / `INSERT`.) A dot (rather than a text label) is
  used in the search bar deliberately: it conveys the mode at a glance without overlapping the query text or
  changing Flow Launcher's existing layout, so users who never enable Vim mode see no difference.
- **Insert** — the default. Works exactly like the standard search bar (blinking caret).
- **Normal** — a solid block caret; alphanumeric keys are interpreted as commands instead of text.
- **Visual** — character-wise selection; motions extend the selection from a fixed anchor.
- **Visual Line** — line-wise selection. Outside the editor this is the whole (single-line) query; in the
  editor it starts on the current line and `j` / `k` extend it line by line.
- **`Esc`** — from Insert, switches to Normal. **Double-`Esc`** (within 400 ms) hides the launcher.
- **`Ctrl`/`Alt` chords pass through untouched**, so existing Flow Launcher hotkeys are unaffected.

## Normal mode

### Navigation
- `h` / `l` — move left / right
- `j` / `k` — move down / up through the search results
- `0` — start of the query · `^` — first non-blank · `$` — end of the query
- `g_` — last non-blank character of the line

### Word motions
- `w` / `W` — start of next word / WORD
- `e` / `E` — end of word / WORD
- `b` / `B` — start of previous word / WORD

### Character search
- `f{char}` / `F{char}` — to next / previous `{char}`
- `t{char}` / `T{char}` — till just before / after `{char}`
- `;` / `,` — repeat the last character search, same / opposite direction
- `%` — jump to the matching bracket

### Search & marks
- `/{pat}` / `?{pat}` — search forward / backward; `n` / `N` repeat same / opposite. The pattern is a regex
  (an invalid one falls back to a literal match), smart-case (case-insensitive unless it contains an uppercase
  letter), and wraps around.
- `m{a-z}` — set a mark · `` `{a-z} `` — jump to its exact spot · `'{a-z}` — jump to its line. Composes with
  operators (e.g. `` d`a ``).

### Editing (integrated with the system clipboard)
- `x` / `X` — delete the character under / before the cursor
- `s` / `S` — substitute the character / whole query, then enter Insert
- `r{char}` — replace the character(s) under the cursor with `{char}`
- `~` — toggle the case of the single character under the cursor (use `g~{motion}` for a range)
- `Ctrl+A` / `Ctrl+X` — increment / decrement the number at or after the cursor (count-aware)
- `gu` / `gU` / `g~{motion}` — lowercase / uppercase / toggle-case (operator + motion, e.g. `guw`, `g~w`)
- `gv` — reselect the last Visual selection
- `dd` / `cc` — delete / change the whole query (current line in the editor; `3dd` for several)
- `D` / `C` — delete / change from the cursor to the end
- (editor) `dj` / `dk`, `cj` / `ck`, `yj` / `yk` — operate line-wise on the current line plus the line
  below / above (count-aware, e.g. `d2j`)
- `J` / `gJ` — (editor) join the current line with the next, with / without a space
- `Y` — yank from the cursor to the end of the line (like `y$`); `yy` yanks the whole line/query
- `p` — paste after the cursor · `P` — paste before the cursor
- `u` — undo the last operation · `Ctrl+R` — redo (both Normal mode). Vim mode uses its own operation-level
  undo stack — an insert session (`i`/`a`/`o`…) reverts as a single `u`; see
  [`docs/MULTILINE_EDITOR.md`](../../docs/MULTILINE_EDITOR.md).
- Yanking (`y{motion}`, `yy`, `yj`/`yk`, `Y`, Visual `y`) briefly flashes a highlight over the copied text as confirmation.

### Operators + text objects
Use a text object after an operator (`d`, `c`, `y`, `gu`, …):
- modifiers: `i` (inner), `a` (around)
- targets: `w` (word), `"` `'` (quotes), `(` `[` `{` (brackets); `b` = `(` and `B` = `{` aliases
- counts: `2daw` / `3iw` extend word objects through additional words
- examples: `diw` (delete inner word), `ci"` (change inside quotes), `ya(` / `yab` (yank around parens)

### Repeat & counts
- `.` — repeat the last change (e.g. `x`, `dw`, `r{char}`, `p`).
- `{count}` — most motions and operators take a numeric prefix: `3w`, `5x`, `2p`, `d3w` / `3dw`, `3f,`, `2;`.
  (`0` on its own is the start-of-line motion, but once a count is already being entered it acts as a digit, so
  `10w` moves ten words.)

### Mode switches
- `i` / `I` — insert at the cursor / start of the query
- `a` / `A` — insert after the cursor / at the end of the query
- `o` / `O` — (editor) open a new line below / above and enter Insert
- `v` / `V` — Visual / Visual Line mode · `Ctrl-V` — Visual Block (multi-line editor only; in Insert mode it
  pastes — see below)

## Visual mode

- Extend the selection with any motion (`h` `l` `w` `b` `e` `0` `^` `$` `f`/`t`/`F`/`T`, `;` `,`).
- Operators on the selection: `d` / `x` (delete), `y` (yank), `c` / `s` (change), `r{char}` (replace),
  `~` (toggle case), `gu` / `gU` (lower / upper).
- `i` / `a` start a text object (e.g. `vi(`), `o` swaps the selection ends.
- (editor, Visual Line) `J` — join all the selected lines (with a space, like Normal-mode `J`).
- `v` toggles between Visual and Visual Line; `Esc` returns to Normal; `j` / `k` navigate results
  (single-line) or extend the selection by line (editor).

## Visual Block mode (`Ctrl-V`, editor)

- A rectangular column selection. `h` / `l` change the column, `j` / `k` the rows, `w` / `b` / `e` (and
  `W` / `B` / `E`) move the corner by words, `0` jumps to column 0, and `$` makes the block run to each row's
  own end (a ragged right edge).
- `y` yanks the block (rows joined by newlines), `d` / `x` deletes the columns, `c` changes them.
- `Shift+I` / `Shift+A` insert before / append after the block on **every** selected row — type once on the
  top row and it's copied to the rest on `Esc`. Like Vim, `Shift+I` skips rows shorter than the column,
  `Shift+A` pads short rows with spaces, and after `$` a `Shift+A` appends at each row's own end. (The typed
  text is mirrored to the other rows only on `Esc`, and only if it contains no newline.)
- `Ctrl-V` again or `Esc` returns to Normal. (`Ctrl-V` in Insert mode still pastes.)

## Command-line (`:`, editor)

- `:w` / `:wq` / `:x` — send the buffer to the selected result (plugin); `:q` / `:q!` — leave the editor.
- `:s/pat/rep/[flags]` — substitute on the current line; `:%s/pat/rep/[flags]` over the whole buffer. `pat`
  is a regex (smart-case; an invalid pattern falls back to a literal match) and `rep` uses .NET syntax (`$1`
  for groups, `$&` for the whole match). Flags: `g` (every match on a line, not just the first), `i` / `I`
  (force ignore- / match-case).

## Editor conveniences

- **Three buffers** — the editor has three scratchpads (left / main / right). `Ctrl+L` cycles to the next,
  `Ctrl+H` to the previous; the mode line shows `buf n/3`. Each keeps its own text; switching keeps your mode
  and leaves the caret at the end (no select-all), so you just keep editing. `Ctrl+X` clears the active buffer
  (undoable with `u`). *(In the editor `Ctrl+H` swaps buffers rather than acting as Insert-mode backspace — use
  the `Backspace` key — and `Ctrl+X` clears rather than decrementing a number.)*
- **Auto-pair / auto-indent** — typing `(` `[` `{` or a quote inserts the matching close (quotes skip
  apostrophes in words); `Enter` carries the line's indent. Both toggle in settings.
- **Crash-safe drafts** — all three editor buffers are autosaved and restored after a crash, reboot, or restart.
- **Open in external editor** (`Ctrl+Shift+E`) — hand the buffer off to your OS default text editor (for
  content that has outgrown the box); this clears the scratchpad and hides Flow.
- **Settings** — General → *Enable Advanced Vim Mode* expander: the *Vim multi-line editor* toggle is **on by
  default once Vim mode is enabled**, so `Ctrl+Enter` opens the editor immediately — turn it off to keep
  single-line Vim only. The same expander sets the editor height in lines (3–20, default 9) and toggles
  auto-pair / auto-indent.

## Known limitations

- **Keyboard layout** — character-pending commands (`f`/`F`/`t`/`T`, `r`, and the quote/bracket text
  objects) reconstruct the target character from the physical key, assuming a US-QWERTY layout. Letters and
  digits work on any layout; some symbols/punctuation may resolve incorrectly on others.
- **Dot-repeat of inserts** — `.` replays the operator/motion of the last change but not text typed in
  Insert mode, so `cwfoo<Esc>.` re-deletes a word without re-typing `foo`.
- **Single line vs editor** — outside the multi-line editor the box is a single line, so line-wise commands
  (`dd`, `cc`, `V`, `0`/`$`) act on the whole query (char-wise) and `gg`/`G` are not bound. Inside the editor
  (`Ctrl+Enter`) they act on real lines, and `gg`/`G` (jump to the first / last line), `o`/`O`, and line-wise
  `dj`/`dk` are bound. With a count, `{count}gg` / `{count}G` jump to a specific line (e.g. `5G`, `5gg`). See
  [`docs/MULTILINE_EDITOR.md`](../../docs/MULTILINE_EDITOR.md).
- **Line-aware paste (editor only)** — in the editor, after a *line-wise* yank/delete (`dd`/`yy`/`cc`,
  `dj`/`dk`/`yj`/`yk`, or a Visual-Line `y`/`d`) `p`/`P` insert whole line(s) below/above the current line;
  after a char-wise one they paste inline after/before the caret. If the clipboard has changed since (e.g. you
  copied from another app), paste falls back to char-wise. In single-line mode `dd`/`yy`/Visual-Line are
  char-wise, so `p` always pastes inline there.
- **One result row in the editor** — while the editor is open the results list is collapsed to a single row to
  keep the window compact, so only the top match is visible. After `Esc` (Normal/Visual mode), `Ctrl+J` /
  `Ctrl+K` still move the (hidden) selection and `Enter` sends the buffer to whichever result is selected.
