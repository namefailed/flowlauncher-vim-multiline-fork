using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Flow.Launcher.ViewModel;

namespace Flow.Launcher.VimMode
{
    /// <summary>
    /// Manages the integration of Vim keybindings and UI overlays into the application window.
    /// </summary>
    public class VimManager : IDisposable
    {
        private bool _disposed;
        private readonly MainWindow _mainWindow;
        private readonly MainViewModel _viewModel;
        private readonly TextBox _queryTextBox;

        // Tracks the last yank/delete so p/P can paste line-wise (yy/dd/Visual Line) vs char-wise.
        private string _lastYankText;
        private bool _lastYankLinewise;
        // Last line-wise operator (dj/dk/cj/ck/yj/yk) extent + direction, so '.' can repeat it.
        private int _lastLineCount = 1;
        private bool _lastLineDown = true;

        private void SetClipboardText(string text)
        {
            // Default to char-wise; line-wise sites flip _lastYankLinewise = true afterwards.
            _lastYankText = text;
            _lastYankLinewise = false;
            if (!string.IsNullOrEmpty(text))
            {
                try { Clipboard.SetText(text); } catch (Exception ex) { Flow.Launcher.Infrastructure.Logger.Log.Exception("VimManager", "Clipboard operation failed", ex); }
            }
        }
        
        private static System.Windows.Media.SolidColorBrush CreateBrush(byte r, byte g, byte b)
        {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
        private readonly VimEngine _vimEngine;
        private readonly System.Windows.Shapes.Rectangle _vimBlockCaret;
        private readonly Border _vimModeIndicator;
        private readonly Border _vimStatusBarHost;
        private readonly Border _vimModeSegment;
        private readonly System.Windows.Controls.TextBlock _vimModeText;
        private readonly System.Windows.Controls.TextBlock _vimStatusInfo;
        private readonly System.Windows.Controls.Canvas _vimLineGutter;
        // The Grid that holds the query box + all editor overlays; hard-capped in the editor so the
        // window can't stretch on a fast paste (see SetMultiLineMode).
        private readonly FrameworkElement _queryBoxArea;
        // The results list; in the editor its MaxHeight is capped to a single row so only the top result
        // shows. The original (bound) MaxHeight is captured so it can be restored on exit.
        private readonly FrameworkElement _resultListBox;
        private System.Windows.Data.BindingBase _resultMaxHeightBinding;
        // Flow's single-line search chrome, hidden while the editor is active.
        private readonly UIElement _clockPanel;
        private readonly UIElement _placeholderBox;
        private readonly UIElement _suggestionBox;
        // The Border wrapping the search icon + plugin-activation icon (collapsed in the editor).
        private readonly UIElement _queryIconArea;
        private System.Windows.Controls.ScrollViewer _editorScrollViewer;
        private string _pendingCommand = "";
        private string _awaitingCharCommand = "";
        private string _lastFindCmd = "";
        private char _lastFindChar = '\0';
        private DateTime _lastEscapeTime = DateTime.MinValue;
        private int _visualAnchor;
        private int _visualCaret;
        private int _count;
        private bool _gPending;
        private string _awaitingTextObject = "";
        private (int anchor, int caret)? _lastVisualRange;
        private string _lastChange = "";
        private int _lastChangeLen = 0;   // chars affected by last change (for . repeat)
        private char _lastReplaceChar = '\0'; // char used in last r{char} (for . repeat)
        private bool _multiLineMode;      // true when the query box is the multi-line editor
        private string _multiLineBuffer = ""; // the multi-line editor's text; preserved across hide/show and mode switches
        private string _singleLineBuffer = ""; // the normal single-line Flow query; preserved while editing in multi-line
        // Crash/restart-safe draft of the editor buffer: debounce-written to disk while editing, restored on
        // the first editor open of a fresh process so a crash or reboot can't lose a half-written entry.
        private bool _draftRestored;
        // Editor conveniences (wired to settings later; default on). Auto-indent carries a line's leading
        // whitespace onto the next; auto-pair inserts the closing bracket/quote and skips over it.
        private bool _autoIndent = true;
        private bool _autoPair = true;
        private readonly object _draftLock = new object();
        private System.Windows.Threading.DispatcherTimer _draftTimer;
        private static readonly string DraftPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlowLauncher", "vim-scratch-draft.txt");
        private readonly Flow.Launcher.Infrastructure.UserSettings.Settings _settings;

        /// <summary>
        /// Enables or disables multi-line editor mode. In multi-line mode the query box becomes a
        /// multi-line editor: j/k move the caret between lines (result navigation shifts to
        /// Ctrl+J/Ctrl+K) and dd/cc/yy act on the current line. Leaving multi-line mode clears the
        /// scratchpad buffer (single-line mode always starts empty).
        /// </summary>
        public void SetMultiLineMode(bool enabled)
        {
            if (_multiLineMode == enabled) return;

            // Each mode keeps its own text buffer, so Ctrl+Enter never loses what you were writing in the
            // other one. Stash the text of the mode we're leaving before switching to the other's buffer.
            if (enabled) _singleLineBuffer = _queryTextBox.Text;
            else _multiLineBuffer = _queryTextBox.Text;

            _multiLineMode = enabled;

            if (enabled)
            {
                _queryTextBox.AcceptsReturn = true;
                _queryTextBox.TextWrapping = TextWrapping.Wrap;
                _queryTextBox.VerticalContentAlignment = VerticalAlignment.Top;
                // Size the text viewport to an exact whole number of lines so the bottom line is never
                // half-clipped, and so the editor window is no taller than the default launcher window.
                _editorTextHeight = EditorVisibleLines * MeasureEditorLineHeight();
                double boxHeight = _editorTextHeight + EditorModeStrip; // text viewport + the mode-line strip

                // Fixed editor size (MinHeight overrides the bound single-line Height; we never write the
                // Height DP itself, which is TwoWay-bound to the persisted single-line query-box height). The
                // real text bound is the inner viewer's explicit Height (EnableEditorScrollbar); this just
                // gives the TextBox a strip below the viewer for the mode line.
                _queryTextBox.MinHeight = boxHeight;
                _queryTextBox.MaxHeight = boxHeight;
                // Drop the query box's 16px left margin (it reserved space for the search icon) so the text
                // sits next to the small gutter; the 14px right padding leaves room for the scrollbar.
                _queryTextBox.Margin = new Thickness(0, EditorTopMargin, 0, 0);
                _queryTextBox.Padding = new Thickness(GutterWidth, 2, 14, 0);
                // Hard-cap the query-box Grid. The root content is a vertical StackPanel, which measures
                // children with infinite height; under SizeToContent a fast paste / huge wrapped line can
                // inflate the TextBox before layout settles. Capping this plain Grid (whose MaxHeight clamp
                // is reliable) + clipping is the deterministic window ceiling. Reverted on exit.
                if (_queryBoxArea != null)
                {
                    _queryBoxArea.MaxHeight = EditorTopMargin + boxHeight;
                    _queryBoxArea.ClipToBounds = true;
                }
                LimitResultsToTop(true);        // show only the top-most result in the editor
                ApplyEditorChrome(true);        // also resolves _editorScrollViewer via HookEditorScroll
                EnableEditorScrollbar(true);    // real scrollbar + viewport clamp (fixes paste-stretch)
                RestoreDraftIfAny();            // recover a crash/restart-orphaned entry on first open
                // Restore the editor scratchpad and select it so typing starts over (Flow's default feel).
                SetText(_multiLineBuffer);
                _vimEngine.SwitchToInsert();    // land in Insert so the user can type immediately
                _queryTextBox.SelectAll();
            }
            else
            {
                _queryTextBox.AcceptsReturn = false;
                _queryTextBox.TextWrapping = TextWrapping.NoWrap;
                _queryTextBox.VerticalContentAlignment = VerticalAlignment.Center;
                _queryTextBox.ClearValue(FrameworkElement.MinHeightProperty);
                _queryTextBox.ClearValue(FrameworkElement.MaxHeightProperty);
                _queryTextBox.ClearValue(FrameworkElement.MarginProperty);
                _queryTextBox.ClearValue(System.Windows.Controls.Control.PaddingProperty);
                if (_queryBoxArea != null)
                {
                    _queryBoxArea.ClearValue(FrameworkElement.MaxHeightProperty);
                    _queryBoxArea.ClearValue(UIElement.ClipToBoundsProperty);
                }
                LimitResultsToTop(false);       // restore the normal multi-result list
                EnableEditorScrollbar(false);   // restore the template's hidden scrollbar + clear the clamp
                ApplyEditorChrome(false);
                // Restore the single-line query and select it so typing starts over (Flow's default feel).
                SetText(_singleLineBuffer);
                _vimEngine.SwitchToInsert();
                _queryTextBox.SelectAll();
            }

            UpdateStatusBar();
            RedrawLineNumbers();
        }

        /// <summary>
        /// Debounced autosave of the editor buffer to disk (called on every text change while editing), so a
        /// crash/reboot can't lose a half-written entry. Writes ~1s after typing stops.
        /// </summary>
        private void ScheduleDraftSave()
        {
            if (_draftTimer == null)
            {
                _draftTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                _draftTimer.Tick += (s, e) => { _draftTimer.Stop(); SaveDraft(_queryTextBox.Text); };
            }
            _draftTimer.Stop();
            _draftTimer.Start();
        }

        /// <summary>Writes the (LF-normalized) draft to disk. Safe to call from any thread.</summary>
        private void SaveDraft(string text)
        {
            try
            {
                lock (_draftLock)
                {
                    var dir = System.IO.Path.GetDirectoryName(DraftPath);
                    if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(DraftPath, NormalizeLf(text) ?? "");
                }
            }
            catch (Exception ex) { Flow.Launcher.Infrastructure.Logger.Log.Exception("VimManager", "Draft save failed", ex); }
        }

        /// <summary>
        /// On the first editor open of a fresh process, if there's no in-memory buffer yet but a saved draft
        /// exists, load it — recovering a half-written entry left behind by a crash, reboot, or restart.
        /// </summary>
        private void RestoreDraftIfAny()
        {
            if (_draftRestored) return;
            _draftRestored = true;
            if (!string.IsNullOrEmpty(_multiLineBuffer)) return;
            try
            {
                if (System.IO.File.Exists(DraftPath))
                {
                    var draft = NormalizeLf(System.IO.File.ReadAllText(DraftPath));
                    if (!string.IsNullOrEmpty(draft)) _multiLineBuffer = draft;
                }
            }
            catch (Exception ex) { Flow.Launcher.Infrastructure.Logger.Log.Exception("VimManager", "Draft restore failed", ex); }
        }

        /// <summary>
        /// In the editor, cap the results list to a single row so only the top-most result is shown; restore
        /// the normal (bound) MaxHeight on exit. The results list's MaxHeight is normally bound to
        /// MaxResultsToShow * ItemHeightSize, and each row is exactly ItemHeightSize — so a one-row cap shows
        /// just the top result. Scoped to the editor; single-line mode keeps its full multi-result list.
        /// </summary>
        private void LimitResultsToTop(bool editor)
        {
            if (_resultListBox == null) return;
            if (editor)
            {
                // Capture the original bound MaxHeight once, before we override it with a local value.
                _resultMaxHeightBinding ??= System.Windows.Data.BindingOperations.GetBinding(_resultListBox, FrameworkElement.MaxHeightProperty);
                _resultListBox.MaxHeight = _settings.ItemHeightSize;
            }
            else if (_resultMaxHeightBinding != null)
            {
                System.Windows.Data.BindingOperations.SetBinding(_resultListBox, FrameworkElement.MaxHeightProperty, _resultMaxHeightBinding);
            }
            else
            {
                _resultListBox.ClearValue(FrameworkElement.MaxHeightProperty);
            }
        }

        /// <summary>
        /// Turns the editor's inner ScrollViewer (the TextBox template's PART_ContentHost) into a real,
        /// content-constraining scrollbar while the editor is active, and restores the template default on
        /// exit. The base template hardcodes VerticalScrollBarVisibility="Hidden" as a literal (not a
        /// TemplateBinding), so setting the property on the TextBox is a no-op — we must set it on the
        /// resolved inner viewer instead.
        ///
        /// This is also what stops the window stretching on a big paste. With "Hidden" the inner viewer
        /// measures its content at infinite height, so under the window's SizeToContent="Height" a bulk paste
        /// briefly inflates the whole window (it only self-corrects on the next layout pass). "Auto" makes the
        /// viewer measure against its viewport and scroll, so its desired height is bounded by MaxHeight and
        /// can never grow the window. Both properties are set only on this editor instance and fully reverted
        /// on exit (Hidden + ClearValue), so single-line mode is byte-for-byte unchanged.
        /// </summary>
        private void EnableEditorScrollbar(bool editor)
        {
            void Apply()
            {
                if (_editorScrollViewer == null) return;
                if (editor)
                {
                    _editorScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    // EXPLICIT fixed height (not MaxHeight) so the text viewport is reliably bounded and
                    // clipped regardless of how the TextBox tries to grow. Top-aligned so it sits at the top
                    // of the (taller) TextBox, leaving the strip below for the mode line; ClipToBounds so a
                    // huge wrapped line can never paint past it into the mode line / results.
                    _editorScrollViewer.Height = _editorTextHeight;
                    _editorScrollViewer.VerticalAlignment = VerticalAlignment.Top;
                    _editorScrollViewer.ClipToBounds = true;
                    _editorScrollViewer.Margin = new Thickness(0);
                    // Slim, thumb-only scrollbar (scoped to this viewer only).
                    if (_mainWindow.TryFindResource("VimEditorScrollBarStyle") is Style slim)
                        _editorScrollViewer.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = slim;
                }
                else
                {
                    _editorScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden; // template default
                    _editorScrollViewer.ClearValue(FrameworkElement.HeightProperty);
                    _editorScrollViewer.ClearValue(FrameworkElement.MaxHeightProperty);
                    _editorScrollViewer.ClearValue(FrameworkElement.VerticalAlignmentProperty);
                    _editorScrollViewer.ClearValue(UIElement.ClipToBoundsProperty);
                    _editorScrollViewer.ClearValue(FrameworkElement.MarginProperty);
                    _editorScrollViewer.Resources.Remove(typeof(System.Windows.Controls.Primitives.ScrollBar));
                }
            }

            HookEditorScroll(); // idempotent; ensures _editorScrollViewer is resolved
            if (_editorScrollViewer != null)
            {
                Apply();
            }
            else if (editor)
            {
                // Very first entry, before the visual tree is realized: defer one layout pass.
                _mainWindow.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    HookEditorScroll();
                    Apply();
                }));
            }
        }

        private const double GutterWidth = 24;
        // Editor sizing. The inner scroll-viewer gets an EXPLICIT fixed Height — not a MaxHeight — so the text
        // viewport is reliably bounded and clipped no matter how the TextBox tries to grow (a single huge
        // wrapped line would otherwise balloon it). That height is set to an exact whole number of text lines
        // (EditorVisibleLines x measured line height) so the bottom line is never shown half-clipped, and the
        // gutter is keyed off the same height so its numbers exactly match the visible text. The line count is
        // kept small enough that the editor window is no taller than the default (4-result) launcher window.
        private const int EditorVisibleLines = 9;   // text rows shown in the editor
        private const double EditorModeStrip = 34;  // space reserved below the text viewport for the mode line
        private const double EditorTopMargin = 7;
        private double _editorTextHeight = 200;      // computed on entry = EditorVisibleLines * line height

        /// <summary>Measures the editor's text line height from its font (no layout pass needed).</summary>
        private double MeasureEditorLineHeight()
        {
            try
            {
                var typeface = new System.Windows.Media.Typeface(_queryTextBox.FontFamily, _queryTextBox.FontStyle, _queryTextBox.FontWeight, _queryTextBox.FontStretch);
                double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_queryTextBox).PixelsPerDip;
                var ft = new System.Windows.Media.FormattedText("Xg", System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, _queryTextBox.FontSize, System.Windows.Media.Brushes.Black, dpi);
                if (ft.Height > 1) return ft.Height;
            }
            catch { }
            return Math.Max(_queryTextBox.FontSize * 1.4, 20);
        }

        /// <summary>
        /// Hides Flow's single-line search chrome (clock, search icon, placeholder, suggestion) while
        /// the editor is active and shows the gutter + mode line; restores them on exit.
        /// </summary>
        private void ApplyEditorChrome(bool editor)
        {
            void Toggle(UIElement el)
            {
                if (el == null) return;
                // Belt and suspenders: Collapse handles the clock/icon (whose Opacity is animated, so
                // Opacity=0 alone gets overridden), and Opacity=0 handles the placeholder (whose
                // Visibility Flow re-asserts on query changes). Together they hide reliably.
                if (editor) { el.Visibility = Visibility.Collapsed; el.Opacity = 0; }
                else { el.ClearValue(UIElement.VisibilityProperty); el.ClearValue(UIElement.OpacityProperty); }
            }
            Toggle(_clockPanel);
            Toggle(_placeholderBox);
            Toggle(_suggestionBox);

            // The search + plugin-activation icons share one Border. Collapse the whole Border so
            // neither occupies layout or draws over the editor text. (Collapsing the individual icons
            // isn't enough: the plugin icon's style re-asserts its visibility/opacity as the query
            // changes, leaving an invisible overlay on top of the text. Flow never touches this Border.)
            if (_queryIconArea != null)
            {
                if (editor) _queryIconArea.Visibility = Visibility.Collapsed;
                else _queryIconArea.ClearValue(UIElement.VisibilityProperty);
            }

            if (_vimLineGutter != null)
                _vimLineGutter.Visibility = editor ? Visibility.Visible : Visibility.Collapsed;

            if (editor) HookEditorScroll();
        }

        private void HookEditorScroll()
        {
            if (_editorScrollViewer != null) return;
            _editorScrollViewer = FindDescendantScrollViewer(_queryTextBox);
            if (_editorScrollViewer != null)
                _editorScrollViewer.ScrollChanged += OnEditorScrolled;
        }

        private bool _snappingScroll;

        private void OnEditorScrolled(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
        {
            SnapScrollToLine();
            // Reposition the block caret (and redraw the gutter) after a scroll — e.g. the scroll a paste
            // triggers — so the caret tracks its new on-screen position instead of going stale.
            UpdateCaretPosition();
        }

        /// <summary>
        /// Snaps the editor's vertical scroll to a whole-line boundary so the top line is never shown
        /// half-off. This keeps the rendered text aligned with the line-number gutter (one number per
        /// visible line) — without it, a partial top line gets a number but no visible text.
        /// </summary>
        private void SnapScrollToLine()
        {
            if (_snappingScroll || _editorScrollViewer == null || !_multiLineMode) return;
            double lineH = EditorLineHeight();
            if (lineH <= 1) return;
            double off = _editorScrollViewer.VerticalOffset;
            double snapped = Math.Round(off / lineH) * lineH;
            if (Math.Abs(snapped - off) > 0.5)
            {
                _snappingScroll = true;
                try { _editorScrollViewer.ScrollToVerticalOffset(snapped); }
                finally { _snappingScroll = false; }
            }
        }

        /// <summary>Best-effort editor line height, measured from an on-screen line (caret, else line 0).</summary>
        private double EditorLineHeight()
        {
            var r = _queryTextBox.GetRectFromCharacterIndex(_queryTextBox.CaretIndex);
            if (r.IsEmpty || r.Height <= 1) r = _queryTextBox.GetRectFromCharacterIndex(0);
            return (!r.IsEmpty && r.Height > 1) ? r.Height : 0;
        }

        private static System.Windows.Controls.ScrollViewer FindDescendantScrollViewer(DependencyObject root)
        {
            if (root == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.Controls.ScrollViewer sv) return sv;
                var found = FindDescendantScrollViewer(child);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Draws line numbers in the gutter, aligning each to its line's first visual row via
        /// GetRectFromCharacterIndex so wrapping and scrolling stay correct.
        /// </summary>
        private void RedrawLineNumbers()
        {
            if (_vimLineGutter == null) return;
            _vimLineGutter.Children.Clear();
            if (!_multiLineMode || !_settings.EnableVimMode) return;

            try
            {
                string text = _queryTextBox.Text;
                // Match the gutter's visible range to the fixed text viewport (not the TextBox's ActualHeight,
                // which can be taller), so numbers are drawn for exactly the lines the viewer renders.
                double viewHeight = _editorTextHeight;
                double marginTop = _queryTextBox.Margin.Top;
                var fg = (Application.Current.TryFindResource("Color08B") as System.Windows.Media.Brush) ?? CreateBrush(135, 135, 135);

                int idx = 0;
                for (int ln = 1; ; ln++)
                {
                    var rect = _queryTextBox.GetRectFromCharacterIndex(idx);
                    // Only number a line whose TOP is within the viewport, so the gutter matches the text:
                    // a line scrolled half-off the top (top above 0) is not rendered by the TextBox, so it
                    // must not get a number either. Scroll snapping keeps the top line whole (top ~= 0).
                    if (!rect.IsEmpty && rect.Top >= -1 && rect.Top < viewHeight)
                    {
                        var tb = new System.Windows.Controls.TextBlock
                        {
                            Text = ln.ToString(),
                            FontSize = 11,
                            Foreground = fg,
                            TextAlignment = TextAlignment.Center,
                            Width = GutterWidth
                        };
                        // Vertically center the (smaller) number within the text row's box so it lines up
                        // with the glyphs instead of sitting at the top of the taller line box.
                        tb.Measure(new System.Windows.Size(GutterWidth, double.PositiveInfinity));
                        System.Windows.Controls.Canvas.SetTop(tb, rect.Top + marginTop + (rect.Height - tb.DesiredSize.Height) / 2.0);
                        System.Windows.Controls.Canvas.SetLeft(tb, 0);
                        _vimLineGutter.Children.Add(tb);
                    }

                    int nl = text.IndexOf('\n', idx);
                    if (nl < 0) break;
                    idx = nl + 1;
                }
            }
            catch (Exception ex) { Flow.Launcher.Infrastructure.Logger.Log.Exception("VimManager", "Line gutter redraw failed", ex); }
        }

        /// <summary>Toggles multi-line editor mode (bound to Ctrl+Enter).</summary>
        public void ToggleMultiLineMode() => SetMultiLineMode(!_multiLineMode);

        /// <summary>Gets whether multi-line editor mode is currently active.</summary>
        public bool IsMultiLineMode => _multiLineMode;

        /// <summary>
        /// Refreshes the editor status bar (mode, line/column, character count). Visible only in
        /// multi-line mode with Vim enabled.
        /// </summary>
        private void UpdateStatusBar()
        {
            if (_vimStatusBarHost == null) return;

            bool show = _multiLineMode && _settings.EnableVimMode;
            _vimStatusBarHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show || _vimModeText == null) return;

            string text = _queryTextBox.Text;
            int caret = _queryTextBox.CaretIndex;
            int line = VimMotionEngine.GetLineNumber(text, caret) + 1;
            int col = VimMotionEngine.GetColumn(text, caret) + 1;
            // The pill uses the app's accent (set in XAML) to match the theme; the label conveys the mode.
            _vimModeText.Text = _vimEngine.CurrentMode switch
            {
                VimModeType.Normal => "NORMAL",
                VimModeType.Visual => "VISUAL",
                VimModeType.VisualLine => "V-LINE",
                _ => "INSERT"
            };
            if (_vimStatusInfo != null) _vimStatusInfo.Text = $"Ln {line}, Col {col}     {text.Length} chars";
        }

        // Vim-style operation-level undo/redo stacks
        private readonly System.Collections.Generic.Stack<(string text, int caretIndex)> _undoStack = new();
        private readonly System.Collections.Generic.Stack<(string text, int caretIndex)> _redoStack = new();

        /// <summary>Snapshots the current text+caret state onto the undo stack and clears the redo stack.</summary>
        private void PushUndo()
        {
            _undoStack.Push((_queryTextBox.Text, _queryTextBox.CaretIndex));
            _redoStack.Clear();
        }

        private void VimUndo()
        {
            if (_undoStack.Count == 0) return;
            _redoStack.Push((_queryTextBox.Text, _queryTextBox.CaretIndex));
            var (text, caret) = _undoStack.Pop();
            _queryTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, text);
            _queryTextBox.CaretIndex = Math.Min(caret, text.Length);
        }

        private void VimRedo()
        {
            if (_redoStack.Count == 0) return;
            _undoStack.Push((_queryTextBox.Text, _queryTextBox.CaretIndex));
            var (text, caret) = _redoStack.Pop();
            _queryTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, text);
            _queryTextBox.CaretIndex = Math.Min(caret, text.Length);
        }

        /// <summary>
        /// Applies a text mutation: snapshots the current state onto the undo stack,
        /// then sets the TextBox text. All text-mutating Vim commands must use this
        /// instead of calling SetCurrentValue directly.
        /// </summary>
        private void SetText(string newText)
        {
            PushUndo();
            _queryTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, newText);
        }


        /// <summary>
        /// Initializes a new instance of the VimManager class.
        /// </summary>
        public VimManager(MainWindow mainWindow, MainViewModel viewModel, TextBox queryTextBox, System.Windows.Shapes.Rectangle vimBlockCaret, Border vimModeIndicator, Border vimStatusBarHost, Flow.Launcher.Infrastructure.UserSettings.Settings settings)
        {
            _mainWindow = mainWindow;
            _viewModel = viewModel;
            _queryTextBox = queryTextBox;
            _vimBlockCaret = vimBlockCaret;
            _vimModeIndicator = vimModeIndicator;
            _vimStatusBarHost = vimStatusBarHost;
            _vimModeSegment = mainWindow.FindName("VimModeSegment") as Border;
            _vimModeText = mainWindow.FindName("VimModeText") as System.Windows.Controls.TextBlock;
            _vimStatusInfo = mainWindow.FindName("VimStatusInfo") as System.Windows.Controls.TextBlock;
            _vimLineGutter = mainWindow.FindName("VimLineGutter") as System.Windows.Controls.Canvas;
            _queryBoxArea = mainWindow.FindName("QueryBoxArea") as FrameworkElement;
            _resultListBox = mainWindow.FindName("ResultListBox") as FrameworkElement;
            _clockPanel = mainWindow.FindName("ClockPanel") as UIElement;
            _placeholderBox = mainWindow.FindName("QueryTextPlaceholderBox") as UIElement;
            _suggestionBox = mainWindow.FindName("QueryTextSuggestionBox") as UIElement;
            _queryIconArea = mainWindow.FindName("QueryIconArea") as UIElement;
            _settings = settings;

            _vimEngine = new VimEngine();
            _vimEngine.ModeChanged += VimEngine_ModeChanged;
            _queryTextBox.PreviewTextInput += QueryTextBox_PreviewTextInput;
            _queryTextBox.SelectionChanged += QueryTextBox_SelectionChanged;
            _queryTextBox.TextChanged += QueryTextBox_TextChanged;
            _mainWindow.Loaded += MainWindow_Loaded;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _settings.PropertyChanged += OnSettingsPropertyChanged;
        }

        private void OnSettingsPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // When Vim mode is turned off, return to Insert so native typing is not blocked
            // by QueryTextBox_PreviewTextInput and clear any lingering overlays/state.
            if (e.PropertyName == nameof(Flow.Launcher.Infrastructure.UserSettings.Settings.EnableVimMode) && !_settings.EnableVimMode)
            {
                _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    SetMultiLineMode(false); // also leave the editor (no-op if not in it)
                    _queryTextBox.SelectionLength = 0;
                    ResetPendingState();
                    _vimEngine.SwitchToInsert();
                }));
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Initial state
            UpdateIndicatorAsync(_vimEngine.CurrentMode);
        }

        private void QueryTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            UpdateCaretPosition();
        }

        private void QueryTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateCaretPosition();
            if (_multiLineMode) ScheduleDraftSave(); // crash/restart-safe autosave of the editor buffer
            // A text mutation (e.g. `p`) updates layout asynchronously, so the position above can be from the
            // stale layout and the block caret would vanish until the next action. Re-run once layout settles.
            if (!_caretRedrawPending && _vimEngine.CurrentMode != VimModeType.Insert)
            {
                _caretRedrawPending = true;
                _mainWindow.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    _caretRedrawPending = false;
                    UpdateCaretPosition();
                }));
            }
        }

        private void UpdateCaretPosition()
        {
            if (_vimEngine.CurrentMode != VimModeType.Insert && _vimBlockCaret != null)
            {
                try
                {
                    int index = (_vimEngine.CurrentMode == VimModeType.Visual || _vimEngine.CurrentMode == VimModeType.VisualLine)
                        ? _visualCaret
                        : _queryTextBox.CaretIndex;
                    var rect = _queryTextBox.GetRectFromCharacterIndex(index);
                    if (!rect.IsEmpty)
                    {
                        var m = _queryTextBox.Margin;
                        _vimBlockCaret.Margin = new Thickness(rect.Left + m.Left, rect.Top + m.Top, 0, 0);
                        _vimBlockCaret.Width = Math.Max(rect.Width, 8);
                        _vimBlockCaret.Height = rect.Height;
                    }
                    else if (!_caretRedrawPending)
                    {
                        // Right after a paste/edit the new caret position has no rect yet (layout still
                        // pending), so the block caret would vanish until the next keypress. Retry once
                        // layout settles.
                        _caretRedrawPending = true;
                        _mainWindow.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                        {
                            _caretRedrawPending = false;
                            UpdateCaretPosition();
                        }));
                    }
                }
                catch (Exception ex) { Flow.Launcher.Infrastructure.Logger.Log.Exception("VimManager", "Layout exception in UpdateCaretPosition", ex); }
            }

            if (_multiLineMode)
            {
                UpdateStatusBar();
                RedrawLineNumbers();
            }
        }

        private System.Windows.Controls.Canvas _vimYankFlash;
        private int _yankFlashToken;
        private bool _caretRedrawPending; // guards the deferred block-caret reposition after an edit

        /// <summary>
        /// Briefly highlights a just-yanked text range (like Neovim's on-yank flash) so the user gets
        /// visual confirmation the yank happened. Draws one accent rectangle per line of the range on the
        /// VimYankFlash canvas overlay and fades it out. Purely visual — does not touch text, caret, or
        /// selection. Only call this for pure yanks (y/Y/yy/yj/visual y), never for cuts (x/d/c/s).
        /// </summary>
        private void FlashYank(int start, int length)
        {
            if (length <= 0) return;
            if (_vimYankFlash == null)
                _vimYankFlash = _mainWindow.FindName("VimYankFlash") as System.Windows.Controls.Canvas;
            if (_vimYankFlash == null) return;

            try
            {
                string text = _queryTextBox.Text;
                if (start < 0 || start >= text.Length) return;
                int end = Math.Min(start + length, text.Length); // exclusive

                _vimYankFlash.Children.Clear();
                var m = _queryTextBox.Margin;
                var fill = (System.Windows.Media.Brush)Application.Current.TryFindResource("BasicSystemAccentColor")
                           ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215));

                // Split the range into per-line segments (at '\n') and draw a rectangle for each, so a
                // multi-line yank highlights every line rather than a single oversized box.
                int segStart = start;
                for (int i = start; i <= end; i++)
                {
                    bool atBreak = i == end || (i < text.Length && text[i] == '\n');
                    if (!atBreak) continue;
                    if (i > segStart) AddFlashRect(segStart, i, m, fill);
                    segStart = i + 1; // skip the newline
                }
                if (_vimYankFlash.Children.Count == 0) return;

                _yankFlashToken++;
                int token = _yankFlashToken;
                _vimYankFlash.Visibility = Visibility.Visible;
                var anim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(300)));
                anim.Completed += (_, __) =>
                {
                    if (_yankFlashToken != token) return; // a newer flash superseded this one
                    _vimYankFlash.Children.Clear();
                    _vimYankFlash.Visibility = Visibility.Collapsed;
                };
                _vimYankFlash.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch (Exception ex) { Flow.Launcher.Infrastructure.Logger.Log.Exception("VimManager", "FlashYank layout exception", ex); }
        }

        private void AddFlashRect(int segStart, int segEnd, Thickness m, System.Windows.Media.Brush fill)
        {
            var r1 = _queryTextBox.GetRectFromCharacterIndex(segStart);
            var r2 = _queryTextBox.GetRectFromCharacterIndex(segEnd); // leading edge of the char past the segment
            if (r1.IsEmpty || r2.IsEmpty) return;

            double left = r1.Left + m.Left;
            double top = Math.Min(r1.Top, r2.Top) + m.Top;
            double width = Math.Max(r2.Left - r1.Left, 2);
            double height = Math.Max(r1.Bottom, r2.Bottom) - Math.Min(r1.Top, r2.Top);

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = width,
                Height = height,
                Fill = fill,
                Opacity = 0.35,
                RadiusX = 2,
                RadiusY = 2,
                IsHitTestVisible = false,
            };
            System.Windows.Controls.Canvas.SetLeft(rect, left);
            System.Windows.Controls.Canvas.SetTop(rect, top);
            _vimYankFlash.Children.Add(rect);
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.MainWindowVisibilityStatus))
            {
                if (_viewModel.MainWindowVisibilityStatus)
                {
                    _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // Restore the scratchpad if multi-line mode was left on when dismissed.
                        if (_multiLineMode && !string.IsNullOrEmpty(_multiLineBuffer) && string.IsNullOrEmpty(_queryTextBox.Text))
                        {
                            SetText(_multiLineBuffer);
                            _queryTextBox.CaretIndex = Math.Min(_multiLineBuffer.Length, _queryTextBox.Text.Length);
                        }
                        if (_vimEngine.CurrentMode != VimModeType.Insert)
                        {
                            _queryTextBox.SelectionLength = 0;
                            _vimEngine.SwitchToInsert();
                        }
                        UpdateStatusBar();
                    }));
                }
                else
                {
                    // Hiding: capture the latest editor text. Both buffers persist across hide/show so
                    // neither mode loses its content. Read the view-model's QueryText (a plain string kept in
                    // sync with the box), NOT _queryTextBox.Text — MainWindowVisibilityStatus can change on a
                    // background thread (Hide() runs off the UI thread), and touching the TextBox there throws
                    // a cross-thread exception that silently crashes the app.
                    if (_multiLineMode) _multiLineBuffer = _viewModel.QueryText ?? "";
                }
            }
        }

        private void VimEngine_ModeChanged(VimModeType mode)
        {
            UpdateIndicatorAsync(mode);
        }

        private void UpdateIndicatorAsync(VimModeType mode)
        {
            if (_mainWindow.Dispatcher.CheckAccess())
            {
                ApplyModeUI(mode);
            }
            else
            {
                _mainWindow.Dispatcher.BeginInvoke(new Action(() => ApplyModeUI(mode)));
            }
        }

        private void ApplyModeUI(VimModeType mode)
        {
            InputMethod.SetIsInputMethodSuspended(_queryTextBox, mode != VimModeType.Insert);

            if (_vimModeIndicator != null)
            {
                // The mode is shown as a small color-coded dot rather than a text label
                // (Normal = accent, Visual = purple, Visual Line = orange). This keeps the
                // indicator from overlapping the query text or otherwise altering Flow
                // Launcher's search-bar layout; a text label can be added later if desired.
                // In the multi-line editor the mode line shows the mode, so the dot is hidden there.
                _vimModeIndicator.Visibility = _settings.EnableVimMode && mode != VimModeType.Insert && !_multiLineMode ? Visibility.Visible : Visibility.Collapsed;

                _vimModeIndicator.Background = mode switch
                {
                    VimModeType.Normal => (System.Windows.Media.Brush)Application.Current.FindResource("BasicSystemAccentColor") ?? CreateBrush(0, 120, 215),
                    VimModeType.Visual => CreateBrush(153, 50, 204),
                    VimModeType.VisualLine => CreateBrush(255, 140, 0),
                    _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent)
                };
            }

            UpdateStatusBar();

            if (_vimBlockCaret == null) return;

            if (mode == VimModeType.Insert)
            {
                _vimBlockCaret.Visibility = Visibility.Collapsed;
                _queryTextBox.ClearValue(System.Windows.Controls.TextBox.CaretBrushProperty);
            }
            else
            {
                _vimBlockCaret.Visibility = Visibility.Visible;
                _queryTextBox.CaretBrush = System.Windows.Media.Brushes.Transparent;
                UpdateCaretPosition();
            }
        }

        /// <summary>
        /// Intercepts and processes key presses before they reach the main window, applying Vim bindings if enabled.
        /// </summary>
        /// <param name="e">The key event arguments.</param>
        /// <returns>True if the key was handled by Vim mode, otherwise false.</returns>
        public bool HandlePreviewKeyDown(KeyEventArgs e)
        {
            if (!_settings.EnableVimMode)
            {
                if (_vimBlockCaret != null && _vimBlockCaret.Visibility == Visibility.Visible)
                {
                    _vimBlockCaret.Visibility = Visibility.Collapsed;
                }
                if (_vimModeIndicator != null && _vimModeIndicator.Visibility == Visibility.Visible)
                {
                    _vimModeIndicator.Visibility = Visibility.Collapsed;
                }
                _queryTextBox.ClearValue(System.Windows.Controls.TextBox.CaretBrushProperty);
                InputMethod.SetIsInputMethodSuspended(_queryTextBox, false);
                return false;
            }

            if (_vimBlockCaret != null && _vimBlockCaret.Visibility == Visibility.Collapsed && _vimEngine.CurrentMode != VimModeType.Insert)
            {
                _vimBlockCaret.Visibility = Visibility.Visible;
            }

            var modifiers = e.KeyboardDevice.Modifiers;

            // Ctrl+Enter toggles the multi-line editor (open Flow normally, then drop into the editor).
            if (modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.Enter)
            {
                ToggleMultiLineMode();
                e.Handled = true;
                return true;
            }

            // Ctrl+Shift+E in the editor: hand the buffer off to the OS text editor (for content
            // that has outgrown the box).
            if (_multiLineMode && modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.E)
            {
                OpenInExternalEditor();
                e.Handled = true;
                return true;
            }

            // In the editor, Enter while NOT in Insert mode (i.e. Esc'd to Normal/Visual) sends the
            // whole multi-line buffer to the selected result/plugin.
            if (_multiLineMode && e.Key == Key.Enter && modifiers == ModifierKeys.None
                && _vimEngine.CurrentMode != VimModeType.Insert)
            {
                // Keep the entry after sending (accidental-send insurance): the send is a blind execute of
                // whatever result is selected, so retain the buffer + draft so a misfire is recoverable.
                _multiLineBuffer = _queryTextBox.Text;
                SaveDraft(_multiLineBuffer);
                _viewModel.OpenResultCommand.Execute(null);
                e.Handled = true;
                return true;
            }

            // In Insert mode, Enter inserts a bare line feed (\n) rather than WPF's default \r\n, so the
            // text handed to plugins uses Unix line endings (no ^M in editors like Emacs).
            if (_multiLineMode && e.Key == Key.Enter && _vimEngine.CurrentMode == VimModeType.Insert
                && !modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Alt))
            {
                int c = _queryTextBox.CaretIndex;
                string text = _queryTextBox.Text;
                // Auto-indent: carry the current line's leading whitespace onto the new line. (When the line
                // has no indent — typical prose — this is just a plain "\n", so it never gets in the way.)
                string indent = "";
                if (_autoIndent)
                {
                    int lineStart = c;
                    while (lineStart > 0 && text[lineStart - 1] != '\n') lineStart--;
                    int ws = lineStart;
                    while (ws < c && (text[ws] == ' ' || text[ws] == '\t')) ws++;
                    indent = text.Substring(lineStart, ws - lineStart);
                }
                string ins = "\n" + indent;
                SetText(text.Insert(c, ins));
                _queryTextBox.CaretIndex = Math.Min(c + ins.Length, _queryTextBox.Text.Length);
                e.Handled = true;
                return true;
            }

            if (modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.R && _vimEngine.CurrentMode == VimModeType.Normal)
            {
                VimRedo();
                e.Handled = true;
                return true;
            }

            // Ctrl-A / Ctrl-X increment / decrement the number at or after the cursor (count-aware).
            // Normal mode only, so Insert-mode Ctrl-A (select all) is unaffected.
            if (modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Alt)
                && _vimEngine.CurrentMode == VimModeType.Normal && (e.Key == Key.A || e.Key == Key.X))
            {
                int delta = (e.Key == Key.A ? 1 : -1) * Math.Max(1, GetCount());
                var (found, newText, newCaret) = VimMotionEngine.ChangeNumber(_queryTextBox.Text, _queryTextBox.CaretIndex, delta);
                if (found)
                {
                    SetText(newText);
                    _queryTextBox.CaretIndex = Math.Min(newCaret, newText.Length);
                }
                e.Handled = true;
                return true;
            }

            // In multi-line mode j/k move between lines, so result navigation lives on Ctrl+J/Ctrl+K.
            if (_multiLineMode && modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Alt)
                && _vimEngine.CurrentMode != VimModeType.Insert)
            {
                if (e.Key == Key.J) { _viewModel.SelectNextItemCommand.Execute(null); e.Handled = true; return true; }
                if (e.Key == Key.K) { _viewModel.SelectPrevItemCommand.Execute(null); e.Handled = true; return true; }
            }

            // Ctrl+V in the editor: paste with line endings normalized to \n (so CRLF text from other
            // apps doesn't reintroduce ^M), instead of WPF's default paste.
            if (_multiLineMode && modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.V)
            {
                PasteAtCaretNormalized();
                e.Handled = true;
                return true;
            }

            if (modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Alt))
            {
                return false;
            }

            if (e.Key == Key.Escape && e.KeyboardDevice.Modifiers == ModifierKeys.Shift)
            {
                _viewModel.Hide();
                e.Handled = true;
                return true;
            }

            if (_vimEngine.CurrentMode == VimModeType.Insert)
            {
                if (e.Key == Key.Escape)
                {
                    _vimEngine.SwitchToNormal();
                    _lastEscapeTime = DateTime.Now;
                    e.Handled = true;
                    return true;
                }
                // Auto-pair: Backspace with the caret between an empty pair deletes both sides.
                if (_autoPair && _multiLineMode && e.Key == Key.Back && modifiers == ModifierKeys.None)
                {
                    int c = _queryTextBox.CaretIndex;
                    string text = _queryTextBox.Text;
                    if (c > 0 && c < text.Length)
                    {
                        char l = text[c - 1], r = text[c];
                        bool emptyPair = (l == '(' && r == ')') || (l == '[' && r == ']') || (l == '{' && r == '}')
                                         || (l == '"' && r == '"') || (l == '\'' && r == '\'');
                        if (emptyPair)
                        {
                            _queryTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, text.Remove(c - 1, 2));
                            _queryTextBox.CaretIndex = c - 1;
                            e.Handled = true;
                            return true;
                        }
                    }
                }
            }
            else
            {
                if (_vimEngine.CurrentMode == VimModeType.Visual || _vimEngine.CurrentMode == VimModeType.VisualLine)
                {
                    if (e.Key == Key.Escape)
                    {
                        SaveVisualRange();
                        _queryTextBox.SelectionLength = 0;
                        ResetPendingState();
                        _vimEngine.SwitchToNormal();
                        e.Handled = true;
                        return true;
                    }
                }

                if (e.Key == Key.Escape)
                {
                    if ((DateTime.Now - _lastEscapeTime).TotalMilliseconds < 400)
                    {
                        _lastEscapeTime = DateTime.MinValue;
                        return false;
                    }
                    else
                    {
                        _lastEscapeTime = DateTime.Now;
                        ResetPendingState();
                        e.Handled = true;
                        return true;
                    }
                }

                if (HandleVimKey(e))
                {
                    e.Handled = true;
                    return true;
                }
            }

            return false;
        }

        private bool HandleVimKey(KeyEventArgs e)
        {
            var modifiers = e.KeyboardDevice.Modifiers;

            if (_vimEngine.CurrentMode == VimModeType.Normal || _vimEngine.CurrentMode == VimModeType.Visual)
            {
                if (_gPending)
                {
                    _gPending = false;
                    return HandleGKey(e, modifiers);
                }

                if (string.IsNullOrEmpty(_awaitingCharCommand) && string.IsNullOrEmpty(_awaitingTextObject))
                {
                    if (e.Key >= Key.D1 && e.Key <= Key.D9 && !modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        _count = _count * 10 + (e.Key - Key.D0);
                        return true;
                    }
                    if (e.Key == Key.D0 && !modifiers.HasFlag(ModifierKeys.Shift) && _count > 0)
                    {
                        _count = _count * 10;
                        return true;
                    }
                }

                if (string.IsNullOrEmpty(_awaitingCharCommand) && _awaitingTextObject.Length > 0)
                {
                    return HandleTextObjectKey(e, modifiers);
                }
            }

            if (_vimEngine.CurrentMode == VimModeType.Normal || _vimEngine.CurrentMode == VimModeType.Visual || _vimEngine.CurrentMode == VimModeType.VisualLine)
            {
                if (!string.IsNullOrEmpty(_awaitingCharCommand))
                {
                    char c = GetCharFromKey(e.Key, modifiers);
                    if (c != '\0')
                    {
                        ExecuteCharCommand(_awaitingCharCommand, c);
                    }
                    else if (e.Key == Key.Escape)
                    {
                        _awaitingCharCommand = ""; // Cancel
                    }
                    e.Handled = true;
                    return true;
                }
            }

            switch (_vimEngine.CurrentMode)
            {
                case VimModeType.Normal:
                    switch (e.Key)
                    {
                        case Key.J when modifiers.HasFlag(ModifierKeys.Shift) && _multiLineMode && !IsLineOperatorPending():
                            JoinLines(GetCount(), withSpace: true); // J — join the current line with the next
                            return true;
                        case Key.J:
                            if (_multiLineMode && IsLineOperatorPending())
                                ApplyLinewiseOperator(down: true);
                            else if (_multiLineMode)
                                ExecuteMotion(ApplyCountMove(i => VimMotionEngine.MoveDown(_queryTextBox.Text, i)));
                            else
                                _viewModel.SelectNextItemCommand.Execute(null);
                            return true;
                        case Key.K:
                            if (_multiLineMode && IsLineOperatorPending())
                                ApplyLinewiseOperator(down: false);
                            else if (_multiLineMode)
                                ExecuteMotion(ApplyCountMove(i => VimMotionEngine.MoveUp(_queryTextBox.Text, i)));
                            else
                                _viewModel.SelectPrevItemCommand.Execute(null);
                            return true;
                        case Key.O when _multiLineMode:
                            OpenLine(below: !modifiers.HasFlag(ModifierKeys.Shift));
                            return true;
                        case Key.H:
                            ExecuteMotion(ApplyCountMove(i => VimMotionEngine.MoveLeft(i)));
                            return true;
                        case Key.L:
                            ExecuteMotion(ApplyCountMove(i => VimMotionEngine.MoveRight(i, _queryTextBox.Text.Length)));
                            return true;
                        case Key.W when modifiers == ModifierKeys.None:
                            ExecuteMotion(ApplyCountMove(i => VimMotionEngine.MoveNextWord(_queryTextBox.Text, i)));
                            return true;
                        case Key.B when modifiers == ModifierKeys.None:
                            ExecuteMotion(ApplyCountMove(i => VimMotionEngine.MovePrevWord(_queryTextBox.Text, i)));
                            return true;
                        case Key.E when modifiers == ModifierKeys.None:
                            ExecuteMotion(ApplyCountMove(i => VimMotionEngine.MoveEndWord(_queryTextBox.Text, i)), MotionInclusivity.InclusiveForward);
                            return true;
                        case Key.W when modifiers.HasFlag(ModifierKeys.Shift):
                            ExecuteMotion(ApplyCountMove(i => VimMotionEngine.MoveNextWordBig(_queryTextBox.Text, i)));
                            return true;
                        case Key.B when modifiers.HasFlag(ModifierKeys.Shift):
                            ExecuteMotion(ApplyCountMove(i => VimMotionEngine.MovePrevWordBig(_queryTextBox.Text, i)));
                            return true;
                        case Key.E when modifiers.HasFlag(ModifierKeys.Shift):
                            ExecuteMotion(ApplyCountMove(i => VimMotionEngine.MoveEndWordBig(_queryTextBox.Text, i)), MotionInclusivity.InclusiveForward);
                            return true;
                        case Key.D5 when modifiers.HasFlag(ModifierKeys.Shift):
                            ExecuteMotion(VimMotionEngine.MoveToMatchingBracket(_queryTextBox.Text, _queryTextBox.CaretIndex), MotionInclusivity.InclusivePair);
                            return true;
                        case Key.G:
                            // Shift+G -> last line (G), or line {count} with a count (5G). Plain 'g' is the
                            // prefix for multi-key commands (gg, gu, gU, g~, gv, g_).
                            if (modifiers.HasFlag(ModifierKeys.Shift))
                            {
                                if (_multiLineMode)
                                {
                                    int line = _count > 0 ? GetCount() : 0;
                                    ExecuteMotion(line >= 1
                                        ? StartOfLineNumber(_queryTextBox.Text, line)
                                        : VimMotionEngine.GetLineStart(_queryTextBox.Text, _queryTextBox.Text.Length));
                                }
                            }
                            else if (modifiers == ModifierKeys.None)
                            {
                                _gPending = true;
                            }
                            return true;
                        case Key.D0:
                            if (modifiers.HasFlag(ModifierKeys.Shift)) return false; // Handle ')' normally or ignore
                            ExecuteMotion(_multiLineMode
                                ? VimMotionEngine.GetLineStart(_queryTextBox.Text, _queryTextBox.CaretIndex)
                                : VimMotionEngine.MoveStartOfLine());
                            return true;
                        case Key.D6:
                            if (modifiers.HasFlag(ModifierKeys.Shift)) // ^
                            {
                                ExecuteMotion(_multiLineMode
                                    ? VimMotionEngine.MoveFirstNonBlankOfLine(_queryTextBox.Text, _queryTextBox.CaretIndex)
                                    : VimMotionEngine.MoveFirstNonBlank(_queryTextBox.Text));
                                return true;
                            }
                            return false;
                        case Key.D4:
                            if (modifiers.HasFlag(ModifierKeys.Shift)) // $
                            {
                                ExecuteMotion(_multiLineMode
                                    ? VimMotionEngine.GetLineEnd(_queryTextBox.Text, _queryTextBox.CaretIndex)
                                    : VimMotionEngine.MoveEndOfLine(_queryTextBox.Text.Length));
                                return true;
                            }
                            return false;
                        case Key.F:
                        case Key.T:
                            _awaitingCharCommand = modifiers.HasFlag(ModifierKeys.Shift) ? e.Key.ToString() : e.Key.ToString().ToLower();
                            return true;
                        case Key.R:
                            _awaitingCharCommand = "r";
                            return true;
                        case Key.OemSemicolon:
                            if (!modifiers.HasFlag(ModifierKeys.Shift) && _lastFindChar != '\0')
                            {
                                ExecuteFindCommand(_lastFindCmd, _lastFindChar, GetCount());
                                return true;
                            }
                            return false;
                        case Key.OemComma:
                            if (!modifiers.HasFlag(ModifierKeys.Shift) && _lastFindChar != '\0')
                            {
                                string reverseCmd = _lastFindCmd == "f" ? "F" : _lastFindCmd == "F" ? "f" : _lastFindCmd == "t" ? "T" : "t";
                                ExecuteFindCommand(reverseCmd, _lastFindChar, GetCount());
                                return true;
                            }
                            return false;
                        case Key.X:
                            if (modifiers.HasFlag(ModifierKeys.Shift)) // X
                            {
                                if (_queryTextBox.CaretIndex > 0)
                                {
                                    int c = _queryTextBox.CaretIndex - 1;
                                    SetClipboardText(_queryTextBox.Text.Substring(c, 1));
                                    SetText(_queryTextBox.Text.Remove(c, 1));
                                    _queryTextBox.CaretIndex = c;
                                }
                                _lastChange = "X";
                                _lastChangeLen = 1;
                            }
                            else // x
                            {
                                int n = GetCount();
                                if (_queryTextBox.CaretIndex < _queryTextBox.Text.Length)
                                {
                                    int c = _queryTextBox.CaretIndex;
                                    int len = Math.Min(n, _queryTextBox.Text.Length - c);
                                    SetClipboardText(_queryTextBox.Text.Substring(c, len));
                                    SetText(_queryTextBox.Text.Remove(c, len));
                                    _queryTextBox.CaretIndex = c;
                                }
                                _lastChange = "x";
                                _lastChangeLen = n;
                            }
                            return true;
                        case Key.S:
                            if (modifiers.HasFlag(ModifierKeys.Shift)) // S -> cc (substitute line)
                            {
                                // Multi-line: substitute the current line; single-line: the whole query.
                                _queryTextBox.CaretIndex = _multiLineMode
                                    ? VimMotionEngine.GetLineStart(_queryTextBox.Text, _queryTextBox.CaretIndex)
                                    : 0;
                                _pendingCommand = "c";
                                ExecuteMotion(_multiLineMode
                                    ? VimMotionEngine.GetLineEnd(_queryTextBox.Text, _queryTextBox.CaretIndex)
                                    : VimMotionEngine.MoveEndOfLine(_queryTextBox.Text.Length));
                            }
                            else // s -> cl
                            {
                                if (_queryTextBox.CaretIndex < _queryTextBox.Text.Length)
                                {
                                    int c = _queryTextBox.CaretIndex;
                                    SetClipboardText(_queryTextBox.Text.Substring(c, 1));
                                    SetText(_queryTextBox.Text.Remove(c, 1));
                                    _queryTextBox.CaretIndex = c;
                                }
                                _lastChange = "s";
                                _lastChangeLen = 1;
                                _vimEngine.SwitchToInsert();
                            }
                            return true;
                        case Key.OemTilde:
                            if (modifiers.HasFlag(ModifierKeys.Shift))
                            {
                                int c = _queryTextBox.CaretIndex;
                                if (c < _queryTextBox.Text.Length)
                                {
                                    char ch = _queryTextBox.Text[c];
                                    ch = char.IsUpper(ch) ? char.ToLower(ch) : char.ToUpper(ch);
                                    SetText(_queryTextBox.Text.Remove(c, 1).Insert(c, ch.ToString()));
                                    _queryTextBox.CaretIndex = Math.Min(_queryTextBox.Text.Length, c + 1);
                                }
                                _lastChange = "~";
                                return true;
                            }
                            return false;

                        case Key.P:
                            if (modifiers.HasFlag(ModifierKeys.Shift))
                                PasteBeforeCursor(GetCount()); // P: before cursor / line above
                            else
                                PasteAfterCursor(GetCount());  // p: after cursor / line below
                            return true;
                        case Key.U:
                            VimUndo();
                            return true;
                        case Key.D:
                        case Key.C:
                        case Key.Y:
                            string cmd = e.Key.ToString().ToLower();
                            if (modifiers.HasFlag(ModifierKeys.Shift)) // D, C, Y
                            {
                                _pendingCommand = cmd;
                                int eol = _multiLineMode
                                    ? VimMotionEngine.GetLineEnd(_queryTextBox.Text, _queryTextBox.CaretIndex)
                                    : VimMotionEngine.MoveEndOfLine(_queryTextBox.Text.Length);
                                ExecuteMotion(eol);
                                _lastChange = cmd.ToUpper() + "_eol";
                                return true;
                            }

                            if (_pendingCommand == cmd) // dd, cc, yy
                            {
                                // Single source of truth for the mode difference: whole query in
                                // single-line mode, current line in multi-line mode (cc keeps the line).
                                // A count (3dd / 2yy) extends over that many lines in the editor.
                                int lineCount = _multiLineMode ? Math.Max(1, GetCount()) : 1;
                                int ls, le;
                                if (_multiLineMode && lineCount > 1)
                                {
                                    int target = _queryTextBox.CaretIndex;
                                    for (int i = 1; i < lineCount; i++)
                                        target = VimMotionEngine.MoveDown(_queryTextBox.Text, target);
                                    (ls, le) = VimMotionEngine.GetLinewiseRange(_queryTextBox.Text, _queryTextBox.CaretIndex, target);
                                }
                                else
                                {
                                    (ls, le) = VimMotionEngine.LineOperatorRange(
                                        _queryTextBox.Text, _queryTextBox.CaretIndex, _multiLineMode, includeLineBreak: cmd != "c");
                                }
                                if (le > ls)
                                    SetClipboardText(_queryTextBox.Text.Substring(ls, le - ls));
                                _lastYankLinewise = _multiLineMode; // dd/cc/yy is line-wise in the editor
                                if (cmd == "y") FlashYank(ls, le - ls);
                                if (cmd == "d" || cmd == "c")
                                {
                                    SetText(_queryTextBox.Text.Remove(ls, le - ls));
                                    _queryTextBox.CaretIndex = Math.Min(ls, _queryTextBox.Text.Length);
                                }
                                if (cmd == "c")
                                    _vimEngine.SwitchToInsert();
                                _lastChange = cmd + cmd;
                                _pendingCommand = "";
                                _count = 0;
                            }
                            else if (_pendingCommand != "" && _pendingCommand != cmd)
                            {
                                _pendingCommand = "";
                                return true;
                            }
                            else
                            {
                                _pendingCommand = cmd;
                            }
                            return true;
                        case Key.A when _pendingCommand == "d" || _pendingCommand == "c" || _pendingCommand == "y":
                            _awaitingTextObject = "a";
                            return true;
                        case Key.I when _pendingCommand == "d" || _pendingCommand == "c" || _pendingCommand == "y":
                            _awaitingTextObject = "i";
                            return true;
                        case Key.I when modifiers == ModifierKeys.None && _pendingCommand == "":
                            PushUndo(); // snapshot pre-insert state so `u` reverts the whole insert session
                            _vimEngine.SwitchToInsert();
                            return true;
                        case Key.I when modifiers.HasFlag(ModifierKeys.Shift):
                            PushUndo();
                            _vimEngine.SwitchToInsert();
                            // Start of the current line in the editor; start of the whole query otherwise.
                            _queryTextBox.CaretIndex = _multiLineMode
                                ? VimMotionEngine.GetLineStart(_queryTextBox.Text, _queryTextBox.CaretIndex)
                                : 0;
                            return true;
                        case Key.A when modifiers == ModifierKeys.None && _pendingCommand == "":
                            PushUndo();
                            _vimEngine.SwitchToInsert();
                            if (_queryTextBox.CaretIndex < _queryTextBox.Text.Length)
                            {
                                _queryTextBox.CaretIndex++;
                            }
                            return true;
                        case Key.A when modifiers.HasFlag(ModifierKeys.Shift):
                            PushUndo();
                            _vimEngine.SwitchToInsert();
                            // End of the current line in the editor; end of the whole query otherwise.
                            _queryTextBox.CaretIndex = _multiLineMode
                                ? VimMotionEngine.GetLineEnd(_queryTextBox.Text, _queryTextBox.CaretIndex)
                                : _queryTextBox.Text.Length;
                            return true;
                        case Key.V:
                            if (modifiers.HasFlag(ModifierKeys.Shift))
                                EnterVisualLineMode();
                            else
                                EnterVisualMode();
                            return true;
                        case Key.OemPeriod:
                            if (!string.IsNullOrEmpty(_lastChange))
                                RepeatLastChange();
                            return true;
                        case Key.Escape:
                            return true; 
                        default:
                            if (IsVimBlockedKey(e.Key))
                                return true;
                            return false;
                    }

                case VimModeType.Visual:
                    switch (e.Key)
                    {
                        case Key.V:
                            if (modifiers.HasFlag(ModifierKeys.Shift))
                                EnterVisualLineMode();
                            else
                            {
                                _queryTextBox.SelectionLength = 0;
                                _vimEngine.SwitchToNormal();
                            }
                            return true;
                        case Key.O when modifiers == ModifierKeys.None:
                            SwapVisualEnds();
                            return true;
                        case Key.G when modifiers == ModifierKeys.None:
                            // 'g' prefix in Visual mode (e.g. gu / gU on the selection).
                            _gPending = true;
                            return true;
                        case Key.H:
                            ExecuteVisualMotion(ApplyCountMove(i => VimMotionEngine.MoveLeft(i)));
                            return true;
                        case Key.L:
                            ExecuteVisualMotion(ApplyCountMove(i => VimMotionEngine.MoveRight(i, _queryTextBox.Text.Length)));
                            return true;
                        case Key.W when modifiers == ModifierKeys.None:
                            ExecuteVisualMotion(ApplyCountMove(i => VimMotionEngine.MoveNextWord(_queryTextBox.Text, i)));
                            return true;
                        case Key.B when modifiers == ModifierKeys.None:
                            ExecuteVisualMotion(ApplyCountMove(i => VimMotionEngine.MovePrevWord(_queryTextBox.Text, i)));
                            return true;
                        case Key.E when modifiers == ModifierKeys.None:
                            ExecuteVisualMotion(ApplyCountMove(i => VimMotionEngine.MoveEndWord(_queryTextBox.Text, i)));
                            return true;
                        case Key.W when modifiers.HasFlag(ModifierKeys.Shift):
                            ExecuteVisualMotion(ApplyCountMove(i => VimMotionEngine.MoveNextWordBig(_queryTextBox.Text, i)));
                            return true;
                        case Key.B when modifiers.HasFlag(ModifierKeys.Shift):
                            ExecuteVisualMotion(ApplyCountMove(i => VimMotionEngine.MovePrevWordBig(_queryTextBox.Text, i)));
                            return true;
                        case Key.E when modifiers.HasFlag(ModifierKeys.Shift):
                            ExecuteVisualMotion(ApplyCountMove(i => VimMotionEngine.MoveEndWordBig(_queryTextBox.Text, i)));
                            return true;
                        case Key.D0:
                            if (modifiers.HasFlag(ModifierKeys.Shift)) return true;
                            ExecuteVisualMotion(_multiLineMode
                                ? VimMotionEngine.GetLineStart(_queryTextBox.Text, _visualCaret)
                                : VimMotionEngine.MoveStartOfLine());
                            return true;
                        case Key.D5 when modifiers.HasFlag(ModifierKeys.Shift):
                            ExecuteVisualMotion(VimMotionEngine.MoveToMatchingBracket(_queryTextBox.Text, _visualCaret));
                            return true;
                        case Key.D6:
                            if (modifiers.HasFlag(ModifierKeys.Shift))
                            {
                                ExecuteVisualMotion(_multiLineMode
                                    ? VimMotionEngine.MoveFirstNonBlankOfLine(_queryTextBox.Text, _visualCaret)
                                    : VimMotionEngine.MoveFirstNonBlank(_queryTextBox.Text));
                                return true;
                            }
                            return true;
                        case Key.D4:
                            if (modifiers.HasFlag(ModifierKeys.Shift))
                            {
                                ExecuteVisualMotion(_multiLineMode
                                    ? VimMotionEngine.GetLineEnd(_queryTextBox.Text, _visualCaret)
                                    : VimMotionEngine.MoveEndOfLine(_queryTextBox.Text.Length));
                                return true;
                            }
                            return true;
                        case Key.F:
                        case Key.T:
                            _awaitingCharCommand = modifiers.HasFlag(ModifierKeys.Shift) ? e.Key.ToString() : e.Key.ToString().ToLower();
                            return true;
                        case Key.R:
                            _awaitingCharCommand = "r";
                            return true;
                        case Key.OemSemicolon:
                            if (!modifiers.HasFlag(ModifierKeys.Shift) && _lastFindChar != '\0')
                            {
                                ExecuteFindCommand(_lastFindCmd, _lastFindChar, GetCount());
                                return true;
                            }
                            return true;
                        case Key.OemComma:
                            if (!modifiers.HasFlag(ModifierKeys.Shift) && _lastFindChar != '\0')
                            {
                                string reverseCmd = _lastFindCmd == "f" ? "F" : _lastFindCmd == "F" ? "f" : _lastFindCmd == "t" ? "T" : "t";
                                ExecuteFindCommand(reverseCmd, _lastFindChar, GetCount());
                                return true;
                            }
                            return true;
                        case Key.A when modifiers == ModifierKeys.None:
                            _awaitingTextObject = "a";
                            return true;
                        case Key.I when modifiers == ModifierKeys.None:
                            _awaitingTextObject = "i";
                            return true;
                        case Key.X:
                        case Key.D:
                            {
                                int selStart = _queryTextBox.SelectionStart;
                                int selLength = _queryTextBox.SelectionLength;
                                if (selLength > 0)
                                {
                                    SetClipboardText(_queryTextBox.Text.Substring(selStart, selLength));
                                    SetText(_queryTextBox.Text.Remove(selStart, selLength));
                                    _queryTextBox.CaretIndex = selStart;
                                }
                                _vimEngine.SwitchToNormal();
                            }
                            return true;
                        case Key.Y:
                            {
                                int selStart = _queryTextBox.SelectionStart;
                                int selLength = _queryTextBox.SelectionLength;
                                if (selLength > 0)
                                {
                                    SetClipboardText(_queryTextBox.Text.Substring(selStart, selLength));
                                    FlashYank(selStart, selLength);
                                }
                                _queryTextBox.CaretIndex = selStart;
                                _queryTextBox.SelectionLength = 0;
                                _vimEngine.SwitchToNormal();
                            }
                            return true;
                        case Key.C:
                        case Key.S:
                            {
                                int selStart = _queryTextBox.SelectionStart;
                                int selLength = _queryTextBox.SelectionLength;
                                if (selLength > 0)
                                {
                                    SetClipboardText(_queryTextBox.Text.Substring(selStart, selLength));
                                    SetText(_queryTextBox.Text.Remove(selStart, selLength));
                                    _queryTextBox.CaretIndex = selStart;
                                }
                                _vimEngine.SwitchToInsert();
                            }
                            return true;
                        case Key.OemTilde:
                            if (modifiers.HasFlag(ModifierKeys.Shift))
                            {
                                int selStart = _queryTextBox.SelectionStart;
                                int selLength = _queryTextBox.SelectionLength;
                                if (selLength > 0)
                                {
                                    char[] chars = _queryTextBox.Text.ToCharArray();
                                    for (int i = selStart; i < selStart + selLength && i < chars.Length; i++)
                                        chars[i] = char.IsUpper(chars[i]) ? char.ToLower(chars[i]) : char.ToUpper(chars[i]);
                                    SetText(new string(chars));
                                    _queryTextBox.CaretIndex = selStart;
                                    _queryTextBox.SelectionLength = 0;
                                }
                                _vimEngine.SwitchToNormal();
                            }
                            return true;
                        case Key.J:
                            if (_multiLineMode)
                                ExecuteVisualMotion(VimMotionEngine.MoveDown(_queryTextBox.Text, _visualCaret));
                            else
                                _viewModel.SelectNextItemCommand.Execute(null);
                            return true;
                        case Key.K:
                            if (_multiLineMode)
                                ExecuteVisualMotion(VimMotionEngine.MoveUp(_queryTextBox.Text, _visualCaret));
                            else
                                _viewModel.SelectPrevItemCommand.Execute(null);
                            return true;
                        default:
                            return true;
                    }

                case VimModeType.VisualLine:
                    switch (e.Key)
                    {
                        case Key.V:
                            if (modifiers.HasFlag(ModifierKeys.Shift))
                            {
                                _queryTextBox.SelectionLength = 0;
                                _vimEngine.SwitchToNormal();
                            }
                            else
                            {
                                _vimEngine.SwitchToVisual();
                                UpdateVisualSelection();
                            }
                            return true;
                        case Key.X:
                        case Key.D:
                        case Key.C:
                        case Key.S:
                            {
                                var (s, en) = VisualLineRange();
                                if (en > s) SetClipboardText(_queryTextBox.Text.Substring(s, en - s));
                                _lastYankLinewise = _multiLineMode;
                                SetText(_queryTextBox.Text.Remove(s, en - s));
                                _queryTextBox.CaretIndex = Math.Min(s, _queryTextBox.Text.Length);
                                if (e.Key == Key.C || e.Key == Key.S)
                                    _vimEngine.SwitchToInsert();
                                else
                                    _vimEngine.SwitchToNormal();
                            }
                            return true;
                        case Key.Y:
                            {
                                var (s, en) = VisualLineRange();
                                if (en > s) SetClipboardText(_queryTextBox.Text.Substring(s, en - s));
                                _lastYankLinewise = _multiLineMode;
                                if (en > s) FlashYank(s, en - s);
                                _queryTextBox.CaretIndex = Math.Min(s, _queryTextBox.Text.Length);
                                _queryTextBox.SelectionLength = 0;
                                _vimEngine.SwitchToNormal();
                            }
                            return true;
                        case Key.R:
                            _awaitingCharCommand = "r";
                            return true;
                        case Key.OemTilde:
                            if (modifiers.HasFlag(ModifierKeys.Shift))
                            {
                                int ts = _queryTextBox.SelectionStart;
                                int tlen = _queryTextBox.SelectionLength;
                                if (tlen > 0)
                                {
                                    char[] chars = _queryTextBox.Text.ToCharArray();
                                    for (int i = ts; i < ts + tlen && i < chars.Length; i++)
                                        chars[i] = char.IsUpper(chars[i]) ? char.ToLower(chars[i]) : char.ToUpper(chars[i]);
                                    SetText(new string(chars));
                                }
                                _queryTextBox.CaretIndex = ts;
                                _queryTextBox.SelectionLength = 0;
                                _vimEngine.SwitchToNormal();
                            }
                            return true;
                        case Key.J when modifiers.HasFlag(ModifierKeys.Shift) && _multiLineMode:
                            {
                                // Visual-Line J joins all the selected lines.
                                var (js, je) = VisualLineRange();
                                _queryTextBox.CaretIndex = Math.Min(js, _queryTextBox.Text.Length);
                                int lines = 1;
                                for (int i = js; i < je && i < _queryTextBox.Text.Length; i++)
                                    if (_queryTextBox.Text[i] == '\n') lines++;
                                JoinLines(lines, withSpace: true);
                                _queryTextBox.SelectionLength = 0;
                                _vimEngine.SwitchToNormal();
                            }
                            return true;
                        case Key.J:
                            if (_multiLineMode)
                            {
                                _visualCaret = VimMotionEngine.MoveDown(_queryTextBox.Text, _visualCaret);
                                UpdateVisualLineSelection();
                            }
                            else _viewModel.SelectNextItemCommand.Execute(null);
                            return true;
                        case Key.K:
                            if (_multiLineMode)
                            {
                                _visualCaret = VimMotionEngine.MoveUp(_queryTextBox.Text, _visualCaret);
                                UpdateVisualLineSelection();
                            }
                            else _viewModel.SelectPrevItemCommand.Execute(null);
                            return true;
                        default:
                            return true;
                    }

                default:
                    return false;
            }
        }

        private bool HandleGKey(KeyEventArgs e, ModifierKeys modifiers)
        {
            switch (_vimEngine.CurrentMode)
            {
                case VimModeType.Normal:
                    switch (e.Key)
                    {
                        case Key.G when modifiers == ModifierKeys.None && _multiLineMode:
                            // gg -> document start, or line {count} with a count (5gg). Multi-line only.
                            {
                                int line = _count > 0 ? GetCount() : 0;
                                ExecuteMotion(line >= 1 ? StartOfLineNumber(_queryTextBox.Text, line) : 0);
                            }
                            return true;
                        case Key.OemMinus when modifiers.HasFlag(ModifierKeys.Shift):
                            ExecuteMotion(VimMotionEngine.MoveLastNonBlank(_queryTextBox.Text));
                            return true;
                        case Key.T when modifiers.HasFlag(ModifierKeys.Shift):
                            _pendingCommand = "~";
                            return true;
                        case Key.J when _multiLineMode:
                            JoinLines(GetCount(), withSpace: false); // gJ — join without inserting a space
                            return true;
                        case Key.U when modifiers == ModifierKeys.None:
                            _pendingCommand = "gu";
                            return true;
                        case Key.U when modifiers.HasFlag(ModifierKeys.Shift):
                            _pendingCommand = "gU";
                            return true;
                        case Key.V:
                            if (_lastVisualRange != null)
                            {
                                _visualAnchor = _lastVisualRange.Value.anchor;
                                _visualCaret = _lastVisualRange.Value.caret;
                                _vimEngine.SwitchToVisual();
                                UpdateVisualSelection();
                            }
                            return true;
                        case Key.OemTilde:
                            _pendingCommand = "~";
                            return true;
                        default:
                            return true;
                    }
                case VimModeType.Visual:
                    switch (e.Key)
                    {
                        case Key.U when modifiers == ModifierKeys.None:
                            ChangeSelectionCase(toUpper: false);
                            return true;
                        case Key.U when modifiers.HasFlag(ModifierKeys.Shift):
                            ChangeSelectionCase(toUpper: true);
                            return true;
                        case Key.V:
                            if (_lastVisualRange != null)
                            {
                                _visualAnchor = _lastVisualRange.Value.anchor;
                                _visualCaret = _lastVisualRange.Value.caret;
                                UpdateVisualSelection();
                            }
                            return true;
                        default:
                            return true;
                    }
                default:
                    return true;
            }
        }

        private bool HandleTextObjectKey(KeyEventArgs e, ModifierKeys modifiers)
        {
            string prefix = _awaitingTextObject;
            _awaitingTextObject = "";

            char delim = GetCharFromKey(e.Key, modifiers);
            if (delim == '\0' && e.Key != Key.W && e.Key != Key.B) return true;
            if (e.Key == Key.W) delim = 'w';
            // Vim block-object aliases: ib/ab == i(/a(  and  iB/aB == i{/a{
            if (e.Key == Key.B) delim = modifiers.HasFlag(ModifierKeys.Shift) ? 'B' : 'b';

            string text = _queryTextBox.Text;
            int caret = (_vimEngine.CurrentMode == VimModeType.Visual) ? _visualCaret : _queryTextBox.CaretIndex;
            bool around = prefix == "a";
            (int start, int end) range = (0, 0);

            switch (delim)
            {
                case 'w':
                    range = VimMotionEngine.TextObjectWord(text, caret, around);
                    break;
                case '"':
                    range = VimMotionEngine.TextObjectQuote(text, caret, '"', around);
                    break;
                case '\'':
                    range = VimMotionEngine.TextObjectQuote(text, caret, '\'', around);
                    break;
                case '(':
                case ')':
                case 'b':
                    range = VimMotionEngine.TextObjectDelimited(text, caret, '(', ')', around);
                    break;
                case '[':
                case ']':
                    range = VimMotionEngine.TextObjectDelimited(text, caret, '[', ']', around);
                    break;
                case '{':
                case '}':
                case 'B':
                    range = VimMotionEngine.TextObjectDelimited(text, caret, '{', '}', around);
                    break;
                default:
                    return true;
            }

            if (range.start < 0) return true;

            // Counted word text objects (2aw / 3iw) extend the range through additional words.
            int toCount = Math.Max(1, GetCount());
            if (toCount > 1 && delim == 'w')
            {
                int wEnd = range.end;
                for (int k = 1; k < toCount; k++)
                {
                    var next = VimMotionEngine.TextObjectWord(text, wEnd + 1, around);
                    if (next.start < 0 || next.end <= wEnd) break;
                    wEnd = next.end;
                }
                range.end = wEnd;
            }

            if (_vimEngine.CurrentMode == VimModeType.Visual)
            {
                _visualAnchor = range.start;
                _visualCaret = range.end;
                UpdateVisualSelection();
                return true;
            }

            if (_pendingCommand == "d" || _pendingCommand == "c" || _pendingCommand == "y")
            {
                int len = range.end - range.start + 1;
                if (len > 0)
                {
                    SetClipboardText(text.Substring(range.start, len));
                    if (_pendingCommand != "y")
                    {
                        SetText(text.Remove(range.start, len));
                        _queryTextBox.CaretIndex = range.start;
                    }
                    else FlashYank(range.start, len);
                    if (_pendingCommand == "c")
                        _vimEngine.SwitchToInsert();
                }
                _pendingCommand = "";
                return true;
            }

            return true;
        }

        private int ApplyCountMove(Func<int, int> move)
        {
            int target = (_vimEngine.CurrentMode == VimModeType.Visual) ? _visualCaret : _queryTextBox.CaretIndex;
            int n = _count > 0 ? _count : 1;
            for (int i = 0; i < n; i++)
                target = move(target);
            _count = 0;
            return target;
        }

        private int GetCount()
        {
            int c = _count > 0 ? _count : 1;
            _count = 0;
            return c;
        }

        /// <summary>
        /// Clears all transient command state (pending operator, awaited char/text-object,
        /// the g-prefix flag, and the numeric count). Called on Escape and on mode entry
        /// so partially-typed commands never leak into the next one.
        /// </summary>
        private void ResetPendingState()
        {
            _pendingCommand = "";
            _awaitingCharCommand = "";
            _awaitingTextObject = "";
            _gPending = false;
            _count = 0;
        }

        private void RepeatLastChange()
        {
            switch (_lastChange)
            {
                case "J":
                    JoinLines(_count > 0 ? GetCount() : 1, withSpace: true);
                    break;
                case "gJ":
                    JoinLines(_count > 0 ? GetCount() : 1, withSpace: false);
                    break;
                case "dd":
                    SetClipboardText(_queryTextBox.Text);
                    SetText("");
                    _queryTextBox.CaretIndex = 0;
                    break;
                case "cc":
                    SetClipboardText(_queryTextBox.Text);
                    SetText("");
                    _queryTextBox.CaretIndex = 0;
                    _vimEngine.SwitchToInsert();
                    break;
                case "D_eol":
                case "C_eol":
                    {
                        int c = _queryTextBox.CaretIndex;
                        if (c < _queryTextBox.Text.Length)
                        {
                            SetClipboardText(_queryTextBox.Text.Substring(c));
                            SetText(_queryTextBox.Text.Remove(c));
                            _queryTextBox.CaretIndex = c;
                        }
                        if (_lastChange == "C_eol") _vimEngine.SwitchToInsert();
                    }
                    break;
                case "Y_eol":
                    SetClipboardText(_queryTextBox.Text);
                    break;
                case "p":
                    PasteAfterCursor(GetCount());
                    break;
                case "x":
                    {
                        int n = _count > 0 ? GetCount() : _lastChangeLen; // bare `.` repeats the original count
                        if (n < 1) n = 1;
                        int c = _queryTextBox.CaretIndex;
                        int len = Math.Min(n, _queryTextBox.Text.Length - c);
                        if (len > 0)
                        {
                            SetClipboardText(_queryTextBox.Text.Substring(c, len));
                            SetText(_queryTextBox.Text.Remove(c, len));
                            _queryTextBox.CaretIndex = c;
                        }
                    }
                    break;
                case "~":
                    {
                        int c = _queryTextBox.CaretIndex;
                        if (c < _queryTextBox.Text.Length)
                        {
                            char ch = _queryTextBox.Text[c];
                            ch = char.IsUpper(ch) ? char.ToLower(ch) : char.ToUpper(ch);
                            SetText(_queryTextBox.Text.Remove(c, 1).Insert(c, ch.ToString()));
                            _queryTextBox.CaretIndex = Math.Min(_queryTextBox.Text.Length, c + 1);
                        }
                    }
                    break;
                case "d_motion":
                    {
                        int c = _queryTextBox.CaretIndex;
                        int len = Math.Min(_lastChangeLen, _queryTextBox.Text.Length - c);
                        if (len > 0)
                        {
                            SetClipboardText(_queryTextBox.Text.Substring(c, len));
                            SetText(_queryTextBox.Text.Remove(c, len));
                            _queryTextBox.CaretIndex = c;
                        }
                    }
                    break;
                case "c_motion":
                    {
                        int c = _queryTextBox.CaretIndex;
                        int len = Math.Min(_lastChangeLen, _queryTextBox.Text.Length - c);
                        if (len > 0)
                        {
                            SetClipboardText(_queryTextBox.Text.Substring(c, len));
                            SetText(_queryTextBox.Text.Remove(c, len));
                            _queryTextBox.CaretIndex = c;
                        }
                        _vimEngine.SwitchToInsert();
                    }
                    break;
                case "d_lines":
                case "c_lines":
                case "y_lines":
                    {
                        // Replay dj/dk/cj/ck/yj/yk line-wise from the current caret (count-aware).
                        int cnt = _count > 0 ? GetCount() : Math.Max(1, _lastLineCount);
                        string t = _queryTextBox.Text;
                        int caret = _queryTextBox.CaretIndex;
                        int target = caret;
                        for (int i = 0; i < cnt; i++)
                            target = _lastLineDown ? VimMotionEngine.MoveDown(t, target) : VimMotionEngine.MoveUp(t, target);
                        var (ls, le) = VimMotionEngine.GetLinewiseRange(t, caret, target);
                        if (le > ls)
                        {
                            SetClipboardText(t.Substring(ls, le - ls));
                            _lastYankLinewise = true;
                            if (_lastChange != "y_lines")
                            {
                                SetText(t.Remove(ls, le - ls));
                                _queryTextBox.CaretIndex = Math.Min(ls, _queryTextBox.Text.Length);
                            }
                            else FlashYank(ls, le - ls);
                        }
                        if (_lastChange == "c_lines") _vimEngine.SwitchToInsert();
                    }
                    break;
                case "s":
                case "X":
                    {
                        int c = _queryTextBox.CaretIndex;
                        if (_lastChange == "X") c = Math.Max(0, c - 1);
                        int len = Math.Min(_lastChangeLen, _queryTextBox.Text.Length - c);
                        if (len > 0)
                        {
                            SetClipboardText(_queryTextBox.Text.Substring(c, len));
                            SetText(_queryTextBox.Text.Remove(c, len));
                            _queryTextBox.CaretIndex = c;
                        }
                        if (_lastChange == "s") _vimEngine.SwitchToInsert();
                    }
                    break;
                case "r":
                    {
                        int n = _count > 0 ? GetCount() : _lastChangeLen; // bare `.` repeats the original count
                        if (n < 1) n = 1;
                        int c = _queryTextBox.CaretIndex;
                        if (_lastReplaceChar != '\0' && c < _queryTextBox.Text.Length)
                        {
                            int len = Math.Min(n, _queryTextBox.Text.Length - c);
                            char[] chars = _queryTextBox.Text.ToCharArray();
                            for (int i = c; i < c + len; i++) chars[i] = _lastReplaceChar;
                            SetText(new string(chars));
                            _queryTextBox.CaretIndex = c + len - 1;
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// Opens a new line below (o) or above (O) the current line, places the caret on it,
        /// and enters Insert mode. Multi-line mode only.
        /// </summary>
        private void OpenLine(bool below)
        {
            string text = _queryTextBox.Text;
            int caret = _queryTextBox.CaretIndex;

            if (below)
            {
                int insertPos = VimMotionEngine.GetLineEnd(text, caret);
                SetText(text.Insert(insertPos, "\n"));
                _queryTextBox.CaretIndex = Math.Min(insertPos + 1, _queryTextBox.Text.Length);
            }
            else
            {
                int insertPos = VimMotionEngine.GetLineStart(text, caret);
                SetText(text.Insert(insertPos, "\n"));
                _queryTextBox.CaretIndex = insertPos;
            }
            _vimEngine.SwitchToInsert();
        }

        private bool IsLineOperatorPending() =>
            _pendingCommand == "d" || _pendingCommand == "c" || _pendingCommand == "y";

        /// <summary>
        /// Writes the scratchpad to a temp file, opens it in the OS default text editor, then clears
        /// the editor and hides Flow — for when the note has outgrown the inline box (Ctrl+Shift+E).
        /// </summary>
        private void OpenInExternalEditor()
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flowlauncher-vim-scratch.txt");
                // LF-normalize so the handed-off file doesn't reintroduce ^M (matching the rest of the editor).
                System.IO.File.WriteAllText(path, NormalizeLf(_queryTextBox.Text) ?? "");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                // Only now that the write + launch have succeeded is it safe to leave the editor and drop the
                // scratchpad; if either threw above we keep the buffer (and editor) so the entry isn't lost.
                SetMultiLineMode(false); // content now lives in the file; leave the inline editor
                _multiLineBuffer = "";   // the scratchpad was exported to the file; start fresh next time
                SaveDraft("");           // content lives in the handed-off file now; clear the recovery draft
                _viewModel.Hide();
            }
            catch (Exception ex) { Flow.Launcher.Infrastructure.Logger.Log.Exception("VimManager", "Open in external editor failed", ex); }
        }

        /// <summary>
        /// Applies a pending d/c/y operator line-wise over the current line plus <c>count</c> lines
        /// down (j) or up (k) — i.e. dj/dk/yj/yk/cj/ck operate on whole lines, like Vim.
        /// </summary>
        private void ApplyLinewiseOperator(bool down)
        {
            string text = _queryTextBox.Text;
            int caret = _queryTextBox.CaretIndex;
            int n = GetCount();
            int target = caret;
            for (int i = 0; i < n; i++)
                target = down ? VimMotionEngine.MoveDown(text, target) : VimMotionEngine.MoveUp(text, target);

            var (s, e) = VimMotionEngine.GetLinewiseRange(text, caret, target);
            if (e > s)
            {
                SetClipboardText(text.Substring(s, e - s));
                _lastYankLinewise = true; // dj/dk/yj/yk/cj/ck are line-wise
                if (_pendingCommand != "y")
                {
                    SetText(text.Remove(s, e - s));
                    _queryTextBox.CaretIndex = Math.Min(s, _queryTextBox.Text.Length);
                }
                else FlashYank(s, e - s);
            }
            if (_pendingCommand == "c")
                _vimEngine.SwitchToInsert();
            _lastChange = _pendingCommand + "_lines";
            _lastLineCount = n;
            _lastLineDown = down;
            _pendingCommand = "";
        }

        /// <summary>
        /// Vim J / gJ: joins the current line with the following line(s). With a count, joins that many
        /// lines (NJ does N-1 joins; bare J joins 2 lines). <paramref name="withSpace"/> true = J (collapse
        /// the break + the next line's leading whitespace to a single space); false = gJ (just delete the
        /// break, keeping all other characters). The caret lands at the join point.
        /// </summary>
        private void JoinLines(int count, bool withSpace)
        {
            string text = _queryTextBox.Text;
            int caret = _queryTextBox.CaretIndex;
            int joins = count <= 1 ? 1 : count - 1;
            int newCaret = caret;
            for (int j = 0; j < joins; j++)
            {
                int nl = text.IndexOf('\n', Math.Min(Math.Max(caret, 0), text.Length));
                if (nl < 0) break; // nothing below to join
                int removeStart = nl;
                if (removeStart > 0 && text[removeStart - 1] == '\r') removeStart--; // swallow CRLF's \r
                int removeEnd = nl + 1;
                if (withSpace)
                {
                    while (removeEnd < text.Length && (text[removeEnd] == ' ' || text[removeEnd] == '\t')) removeEnd++;
                    bool prevIsSpace = removeStart > 0 && (text[removeStart - 1] == ' ' || text[removeStart - 1] == '\t');
                    string sep = (prevIsSpace || removeStart == 0) ? "" : " ";
                    text = text.Substring(0, removeStart) + sep + text.Substring(removeEnd);
                }
                else
                {
                    text = text.Substring(0, removeStart) + text.Substring(removeEnd);
                }
                newCaret = removeStart;
                caret = removeStart;
            }
            SetText(text);
            _queryTextBox.CaretIndex = Math.Min(newCaret, _queryTextBox.Text.Length);
            _lastChange = withSpace ? "J" : "gJ";
        }

        /// <summary>Returns the start index of the 1-based <paramref name="line"/>, clamped to the last line.</summary>
        private static int StartOfLineNumber(string text, int line)
        {
            if (line <= 1) return 0;
            int idx = 0, current = 1;
            while (current < line)
            {
                int nl = text.IndexOf('\n', idx);
                if (nl < 0) return idx; // fewer lines than asked -> start of the last line
                idx = nl + 1;
                current++;
            }
            return Math.Min(idx, text.Length);
        }

        /// <summary>
        /// Pastes the clipboard <paramref name="count"/> times. If the last yank/delete was line-wise
        /// (yy/dd/dj/Visual Line), pastes whole line(s) below (p) or above (P) the current line, like
        /// Vim; otherwise pastes char-wise after (p) or before (P) the caret. Records '.' repeat.
        /// </summary>
        private void PasteAfterCursor(int count) => Paste(count, before: false);

        private void PasteBeforeCursor(int count) => Paste(count, before: true);

        // The editor keeps the buffer LF-only (\n) so text handed to plugins has Unix line endings.
        private static string NormalizeLf(string s) => s?.Replace("\r\n", "\n").Replace("\r", "\n");

        /// <summary>Ctrl+V in the editor: insert the clipboard at the caret with LF-normalized newlines.</summary>
        private void PasteAtCaretNormalized()
        {
            try
            {
                string clip = NormalizeLf(Clipboard.GetText());
                if (string.IsNullOrEmpty(clip)) return;
                int c = _queryTextBox.CaretIndex;
                SetText(_queryTextBox.Text.Insert(c, clip));
                _queryTextBox.CaretIndex = Math.Min(c + clip.Length, _queryTextBox.Text.Length);
            }
            catch (Exception ex) { Flow.Launcher.Infrastructure.Logger.Log.Exception("VimManager", "Ctrl+V paste failed", ex); }
        }

        private void Paste(int count, bool before)
        {
            try
            {
                // Normalize to Unix line endings so pasted content keeps the buffer \n-only.
                string clip = NormalizeLf(Clipboard.GetText());
                if (string.IsNullOrEmpty(clip)) return;
                if (count < 1) count = 1;

                // Line-wise only when our own line-wise yank still matches the clipboard.
                bool linewise = _lastYankLinewise && clip == NormalizeLf(_lastYankText);
                string text = _queryTextBox.Text;

                if (linewise)
                {
                    string content = clip.TrimEnd('\n');
                    string block = content;
                    for (int i = 1; i < count; i++) block += "\n" + content;

                    int insertPos = before
                        ? VimMotionEngine.GetLineStart(text, _queryTextBox.CaretIndex)
                        : VimMotionEngine.GetLineEnd(text, _queryTextBox.CaretIndex);

                    string toInsert = before ? block + "\n" : "\n" + block;
                    SetText(text.Insert(insertPos, toInsert));
                    // Caret on the first pasted line.
                    _queryTextBox.CaretIndex = before ? insertPos : Math.Min(insertPos + 1, _queryTextBox.Text.Length);
                }
                else
                {
                    string pasted = clip;
                    for (int i = 1; i < count; i++) pasted += clip;

                    int c = _queryTextBox.CaretIndex;
                    if (!before && c < text.Length) c++; // p pastes after the cursor
                    SetText(text.Insert(c, pasted));
                    _queryTextBox.CaretIndex = Math.Min(c + pasted.Length - 1, Math.Max(0, _queryTextBox.Text.Length - 1));
                }
                _lastChange = "p";
            }
            catch (Exception ex) { Flow.Launcher.Infrastructure.Logger.Log.Exception("VimManager", "Clipboard paste operation failed", ex); }
        }

        private void ExecuteCharCommand(string cmd, char c)
        {
            _awaitingCharCommand = "";
            // Consume any count typed before the operator (e.g. 3rx, 2f,) so it never leaks
            // into the next command.
            int count = GetCount();
            string text = _queryTextBox.Text;
            int caret = _queryTextBox.CaretIndex;

            if (cmd == "r")
            {
                if (_vimEngine.CurrentMode == VimModeType.Visual || _vimEngine.CurrentMode == VimModeType.VisualLine)
                {
                    int selStart = _queryTextBox.SelectionStart;
                    int selLength = _queryTextBox.SelectionLength;
                    if (selLength > 0)
                    {
                        char[] chars = text.ToCharArray();
                        for (int i = selStart; i < selStart + selLength && i < chars.Length; i++)
                            chars[i] = c;
                        SetText(new string(chars));
                        _queryTextBox.CaretIndex = selStart;
                        _queryTextBox.SelectionLength = 0;
                        _vimEngine.SwitchToNormal();
                    }
                }
                else if (caret < text.Length)
                {
                    // r{char} with a count replaces that many characters (vim no-ops if fewer remain).
                    int len = Math.Min(count, text.Length - caret);
                    if (count <= text.Length - caret && len > 0)
                    {
                        char[] chars = text.ToCharArray();
                        for (int i = caret; i < caret + len; i++) chars[i] = c;
                        SetText(new string(chars));
                        _queryTextBox.CaretIndex = caret + len - 1;
                        _lastChange = "r";
                        _lastChangeLen = len; // so a bare `.` repeats the counted replace
                        _lastReplaceChar = c;
                    }
                }
            }
            else if (cmd == "f" || cmd == "F" || cmd == "t" || cmd == "T")
            {
                _lastFindCmd = cmd;
                _lastFindChar = c;
                ExecuteFindCommand(cmd, c, count);
            }
        }

        private void ExecuteFindCommand(string cmd, char c, int count = 1)
        {
            string text = _queryTextBox.Text;
            int caret = (_vimEngine.CurrentMode == VimModeType.Visual || _vimEngine.CurrentMode == VimModeType.VisualLine)
                ? _visualCaret
                : _queryTextBox.CaretIndex;

            int target = caret;
            for (int k = 0; k < (count < 1 ? 1 : count); k++)
            {
                int next = cmd switch
                {
                    "f" => VimMotionEngine.FindCharForward(text, target, c, false),
                    "F" => VimMotionEngine.FindCharBackward(text, target, c, false),
                    "t" => VimMotionEngine.FindCharForward(text, target, c, true),
                    "T" => VimMotionEngine.FindCharBackward(text, target, c, true),
                    _ => target
                };
                if (next == target) break; // not found / no further progress
                target = next;
            }

            if (_vimEngine.CurrentMode == VimModeType.Visual || _vimEngine.CurrentMode == VimModeType.VisualLine)
                ExecuteVisualMotion(target);
            else
                ExecuteMotion(target, MotionInclusivity.InclusiveForward);
        }

        private void ExecuteMotion(int targetCaret, MotionInclusivity inclusivity = MotionInclusivity.Exclusive)
        {
            if (_pendingCommand == "d" || _pendingCommand == "c" || _pendingCommand == "y")
            {
                var (start, end) = VimMotionEngine.OperatorRange(_queryTextBox.CaretIndex, targetCaret, inclusivity, _queryTextBox.Text.Length);

                if (end > start)
                {
                    SetClipboardText(_queryTextBox.Text.Substring(start, end - start));
                    if (_pendingCommand != "y")
                    {
                        SetText(_queryTextBox.Text.Remove(start, end - start));
                        _queryTextBox.CaretIndex = start;
                    }
                    else FlashYank(start, end - start);
                }

                if (_pendingCommand == "c")
                    _vimEngine.SwitchToInsert();
                _lastChange = _pendingCommand + "_motion";
                _lastChangeLen = end - start;
                _pendingCommand = "";
                _count = 0;
            }
            else if (_pendingCommand == "~" || _pendingCommand == "gu" || _pendingCommand == "gU")
            {
                var (start, end) = VimMotionEngine.OperatorRange(_queryTextBox.CaretIndex, targetCaret, inclusivity, _queryTextBox.Text.Length);

                if (end > start)
                {
                    char[] chars = _queryTextBox.Text.ToCharArray();
                    for (int i = start; i < end && i < chars.Length; i++)
                    {
                        chars[i] = _pendingCommand == "gu" ? char.ToLower(chars[i])
                                 : _pendingCommand == "gU" ? char.ToUpper(chars[i])
                                 : char.IsUpper(chars[i]) ? char.ToLower(chars[i]) : char.ToUpper(chars[i]);
                    }
                    SetText(new string(chars));
                    _queryTextBox.CaretIndex = Math.Min(start, _queryTextBox.Text.Length);
                }
                if (_pendingCommand == "~") _lastChange = "~";
                _pendingCommand = "";
                _count = 0;
            }
            else
            {
                _queryTextBox.CaretIndex = targetCaret;
            }
        }

        private void UpdateVisualSelection()
        {
            int start = Math.Min(_visualAnchor, _visualCaret);
            int end = Math.Max(_visualAnchor, _visualCaret);
            if (start < 0) start = 0;
            int length = Math.Min(end - start + 1, _queryTextBox.Text.Length - start);
            if (length < 0) length = 0;
            _queryTextBox.Select(start, length);
            UpdateCaretPosition();
        }

        private void ExecuteVisualMotion(int targetCaret)
        {
            _visualCaret = targetCaret;
            UpdateVisualSelection();
        }

        private void EnterVisualMode()
        {
            if (_queryTextBox.Text.Length == 0) return;
            if (_queryTextBox.CaretIndex >= _queryTextBox.Text.Length)
                _queryTextBox.CaretIndex = _queryTextBox.Text.Length - 1;
            _visualAnchor = _queryTextBox.CaretIndex;
            _visualCaret = _queryTextBox.CaretIndex;
            ResetPendingState();
            _vimEngine.SwitchToVisual();
            UpdateVisualSelection();
        }

        private void EnterVisualLineMode()
        {
            if (_queryTextBox.Text.Length == 0) return;
            ResetPendingState();
            if (_multiLineMode)
            {
                // Select the current line; j/k extend by line.
                _visualAnchor = _queryTextBox.CaretIndex;
                _visualCaret = _queryTextBox.CaretIndex;
                _vimEngine.SwitchToVisualLine();
                UpdateVisualLineSelection();
            }
            else
            {
                // Single line: the whole query is the line.
                _visualAnchor = 0;
                _visualCaret = _queryTextBox.Text.Length - 1;
                _vimEngine.SwitchToVisualLine();
                _queryTextBox.Select(0, _queryTextBox.Text.Length);
                UpdateCaretPosition();
            }
        }

        /// <summary>The range a Visual Line operator deletes/yanks: whole lines in multi-line mode
        /// (with terminators), the whole query in single-line mode.</summary>
        private (int start, int end) VisualLineRange()
        {
            if (_multiLineMode)
                return VimMotionEngine.GetLinewiseRange(_queryTextBox.Text, _visualAnchor, _visualCaret);
            return (0, _queryTextBox.Text.Length);
        }

        /// <summary>Highlights the lines spanned by the Visual Line selection (content only).</summary>
        private void UpdateVisualLineSelection()
        {
            string text = _queryTextBox.Text;
            int lo = Math.Min(_visualAnchor, _visualCaret);
            int hi = Math.Max(_visualAnchor, _visualCaret);
            int start = VimMotionEngine.GetLineStart(text, lo);
            int end = VimMotionEngine.GetLineEnd(text, hi);
            if (end < start) end = start;
            _queryTextBox.Select(start, Math.Max(0, Math.Min(end - start, text.Length - start)));
            UpdateCaretPosition();
        }

        private void SwapVisualEnds()
        {
            int temp = _visualAnchor;
            _visualAnchor = _visualCaret;
            _visualCaret = temp;
            UpdateVisualSelection();
        }

        /// <summary>
        /// Applies a case transform to the current Visual-mode selection (gu / gU) and
        /// returns to Normal mode, mirroring the '~' selection handler.
        /// </summary>
        private void ChangeSelectionCase(bool toUpper)
        {
            int selStart = _queryTextBox.SelectionStart;
            int selLength = _queryTextBox.SelectionLength;
            if (selLength > 0)
            {
                char[] chars = _queryTextBox.Text.ToCharArray();
                for (int i = selStart; i < selStart + selLength && i < chars.Length; i++)
                    chars[i] = toUpper ? char.ToUpper(chars[i]) : char.ToLower(chars[i]);
                SetText(new string(chars));
                _queryTextBox.CaretIndex = selStart;
                _queryTextBox.SelectionLength = 0;
            }
            _vimEngine.SwitchToNormal();
        }

        private void SaveVisualRange()
        {
            _lastVisualRange = (_visualAnchor, _visualCaret);
        }

        private static bool IsVimBlockedKey(Key key)
        {
            if (key >= Key.A && key <= Key.Z) return true;
            if (key >= Key.D0 && key <= Key.D9) return true;
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return true;
            if (key == Key.Space) return true;
            if (key == Key.OemTilde) return true;
            if (key == Key.OemMinus) return true;
            if (key == Key.OemPlus) return true;
            if (key >= Key.OemOpenBrackets && key <= Key.OemQuotes) return true;
            if (key == Key.OemPeriod || key == Key.OemComma) return true;
            if (key == Key.Back) return true;
            return false;
        }

        private static char GetCharFromKey(Key key, ModifierKeys modifiers)
        {
            bool shift = modifiers.HasFlag(ModifierKeys.Shift);
            if (key >= Key.A && key <= Key.Z)
                return shift ? key.ToString()[0] : key.ToString().ToLower()[0];
            if (key >= Key.D0 && key <= Key.D9)
            {
                if (!shift) return (char)('0' + (key - Key.D0));
                switch (key)
                {
                    case Key.D1: return '!'; case Key.D2: return '@'; case Key.D3: return '#';
                    case Key.D4: return '$'; case Key.D5: return '%'; case Key.D6: return '^';
                    case Key.D7: return '&'; case Key.D8: return '*'; case Key.D9: return '(';
                    case Key.D0: return ')';
                }
            }
            switch (key)
            {
                case Key.Space: return ' ';
                case Key.OemSemicolon: return shift ? ':' : ';';
                case Key.OemComma: return shift ? '<' : ',';
                case Key.OemPeriod: return shift ? '>' : '.';
                case Key.OemQuestion: return shift ? '?' : '/';
                case Key.OemQuotes: return shift ? '"' : '\'';
                case Key.OemOpenBrackets: return shift ? '{' : '[';
                case Key.OemCloseBrackets: return shift ? '}' : ']';
                case Key.OemPipe: return shift ? '|' : '\\';
                case Key.OemMinus: return shift ? '_' : '-';
                case Key.OemPlus: return shift ? '+' : '=';
                case Key.OemTilde: return shift ? '~' : '`';
            }
            return '\0';
        }

        private void QueryTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (_settings.EnableVimMode && _vimEngine.CurrentMode != VimModeType.Insert)
            {
                e.Handled = true;
                return;
            }
            // Auto-pair brackets/quotes while typing in the editor.
            if (_autoPair && _multiLineMode && _vimEngine.CurrentMode == VimModeType.Insert
                && e.Text != null && e.Text.Length == 1 && TryAutoPair(e.Text[0]))
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// Auto-pairing for the editor. On an opening ( [ { it inserts the matching close and parks the caret
        /// between; on a quote " ' it pairs only when not extending a word (so apostrophes in prose are left
        /// alone); typing a close char when it's already to the right just steps over it. Returns true if it
        /// handled the keystroke (caller should mark the event handled). Does not push a Vim undo snapshot, so
        /// the whole insert session still undoes as one with `u`.
        /// </summary>
        private bool TryAutoPair(char ch)
        {
            const string opens = "([{";
            const string closes = ")]}";
            string text = _queryTextBox.Text;
            int c = _queryTextBox.CaretIndex;
            char rightOf = c < text.Length ? text[c] : '\0';

            void InsertPair(string pair)
            {
                _queryTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, text.Insert(c, pair));
                _queryTextBox.CaretIndex = c + 1; // between the pair
            }

            // Step over an existing close char ) ] } or a closing quote.
            if ((closes.IndexOf(ch) >= 0 || ch == '"' || ch == '\'') && rightOf == ch)
            {
                _queryTextBox.CaretIndex = c + 1;
                return true;
            }
            // Opening bracket -> insert the matching pair.
            int oi = opens.IndexOf(ch);
            if (oi >= 0)
            {
                InsertPair(ch.ToString() + closes[oi]);
                return true;
            }
            // Quote -> pair only when starting fresh (not right after a word char, so "don't" stays intact).
            if (ch == '"' || ch == '\'')
            {
                char leftOf = c > 0 ? text[c - 1] : '\0';
                bool afterWord = char.IsLetterOrDigit(leftOf);
                if (!afterWord && !char.IsLetterOrDigit(rightOf))
                {
                    InsertPair(new string(ch, 2));
                    return true;
                }
            }
            return false; // let WPF insert the character normally
        }

        /// <summary>
        /// Gets a value indicating whether native text input is currently blocked by Vim mode.
        /// </summary>
        public bool IsInputBlocked => _vimEngine.CurrentMode != VimModeType.Insert;

        /// <summary>
        /// Disposes the Vim manager and detaches from window events.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _vimEngine.ModeChanged -= VimEngine_ModeChanged;
                    _mainWindow.Loaded -= MainWindow_Loaded;
                    _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                    _settings.PropertyChanged -= OnSettingsPropertyChanged;
                    _queryTextBox.PreviewTextInput -= QueryTextBox_PreviewTextInput;
                    _queryTextBox.SelectionChanged -= QueryTextBox_SelectionChanged;
                    _queryTextBox.TextChanged -= QueryTextBox_TextChanged;
                    if (_editorScrollViewer != null)
                        _editorScrollViewer.ScrollChanged -= OnEditorScrolled;
                }

                _disposed = true;
            }
        }
    }
}

