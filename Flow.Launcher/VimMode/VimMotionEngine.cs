using System;

namespace Flow.Launcher.VimMode
{
    /// <summary>
    /// Describes how an operator (d/c/y/gu/gU/g~) treats the character at the far end of a motion.
    /// </summary>
    public enum MotionInclusivity
    {
        /// <summary>Half-open range; the destination character is not affected (w, b, h, l, 0, ^, $).</summary>
        Exclusive,
        /// <summary>Forward motions that include the destination character (e, E, f, t).</summary>
        InclusiveForward,
        /// <summary>Paired motions that include the far-end character in either direction (%).</summary>
        InclusivePair
    }

    /// <summary>
    /// Provides pure static methods for calculating caret movements and text object selections.
    /// </summary>
    public class VimMotionEngine
    {
        /// <summary>
        /// Computes the character range [start, end) an operator should act on, given the caret,
        /// the motion target, and the motion's inclusivity. Handles the Vim rule that inclusive
        /// forward motions consume the destination character while backward find motions do not.
        /// </summary>
        public static (int start, int end) OperatorRange(int caret, int target, MotionInclusivity inclusivity, int textLength)
        {
            int start = Math.Max(0, Math.Min(caret, target));
            int end = Math.Max(0, Math.Max(caret, target));

            // Inclusive-forward (e/f/t) consumes the destination only when the motion moved forward;
            // a backward find (F/T) leaves the original caret char untouched. A paired motion (%)
            // consumes the far-end char in either direction, but only when it actually spans a range.
            bool extend = (inclusivity == MotionInclusivity.InclusiveForward && target > caret)
                          || (inclusivity == MotionInclusivity.InclusivePair && end > start);

            if (extend && end < textLength)
                end++;

            return (start, end);
        }

        /// <summary>
        /// Calculates the new caret position after moving left one character.
        /// </summary>
        /// <param name="caret">The current caret index.</param>
        /// <returns>The new caret index.</returns>
        public static int MoveLeft(int caret)
        {
            return Math.Max(0, caret - 1);
        }

        /// <summary>
        /// Calculates the new caret position after moving right one character.
        /// </summary>
        /// <param name="caret">The current caret index.</param>
        /// <param name="length">The total length of the text.</param>
        /// <returns>The new caret index.</returns>
        public static int MoveRight(int caret, int length)
        {
            return Math.Min(length, caret + 1);
        }

        /// <summary>
        /// Calculates the new caret position at the start of the line.
        /// </summary>
        /// <returns>The start index (always 0).</returns>
        public static int MoveStartOfLine()
        {
            return 0;
        }

        /// <summary>
        /// Calculates the new caret position at the end of the line.
        /// </summary>
        /// <param name="length">The total length of the text.</param>
        /// <returns>The end index.</returns>
        public static int MoveEndOfLine(int length)
        {
            return length;
        }

        /// <summary>
        /// Calculates the index of the first non-blank character in the text.
        /// </summary>
        /// <param name="text">The text to search.</param>
        /// <returns>The index of the first non-blank character, or 0 if none.</returns>
        public static int MoveFirstNonBlank(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsWhiteSpace(text[i])) return i;
            }
            return 0;
        }

        /// <summary>
        /// Calculates the index after the last non-blank character in the text.
        /// </summary>
        /// <param name="text">The text to search.</param>
        /// <returns>The target index.</returns>
        public static int MoveLastNonBlank(string text)
        {
            for (int i = text.Length - 1; i >= 0; i--)
            {
                if (!char.IsWhiteSpace(text[i])) return Math.Min(i + 1, text.Length);
            }
            return 0;
        }

        /// <summary>
        /// Calculates the index of the start of the next word (w motion).
        /// </summary>
        /// <param name="text">The text to traverse.</param>
        /// <param name="caret">The current caret index.</param>
        /// <returns>The new caret index.</returns>
        public static int MoveNextWord(string text, int caret)
        {
            if (caret >= text.Length) return text.Length;

            bool startIsWord = IsWordChar(text[caret]);
            int i = caret;

            while (i < text.Length && IsWordChar(text[i]) == startIsWord && !char.IsWhiteSpace(text[i]))
                i++;

            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            return Math.Min(i, text.Length);
        }

        /// <summary>
        /// Calculates the index of the start of the previous word (b motion).
        /// </summary>
        /// <param name="text">The text to traverse.</param>
        /// <param name="caret">The current caret index.</param>
        /// <returns>The new caret index.</returns>
        public static int MovePrevWord(string text, int caret)
        {
            if (caret <= 0) return 0;

            int i = caret - 1;

            while (i > 0 && char.IsWhiteSpace(text[i]))
                i--;

            bool targetIsWord = IsWordChar(text[i]);

            while (i > 0 && IsWordChar(text[i - 1]) == targetIsWord && !char.IsWhiteSpace(text[i - 1]))
                i--;

            return Math.Max(0, i);
        }

        /// <summary>
        /// Calculates the index of the end of the current or next word (e motion).
        /// </summary>
        /// <param name="text">The text to traverse.</param>
        /// <param name="caret">The current caret index.</param>
        /// <returns>The new caret index.</returns>
        public static int MoveEndWord(string text, int caret)
        {
            if (text.Length == 0) return 0;
            if (caret >= text.Length - 1) return text.Length - 1;

            int i = caret + 1;

            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            if (i >= text.Length) return caret;

            bool targetIsWord = IsWordChar(text[i]);

            while (i < text.Length - 1 && IsWordChar(text[i + 1]) == targetIsWord && !char.IsWhiteSpace(text[i + 1]))
                i++;

            return Math.Min(i, text.Length - 1);
        }

        /// <summary>
        /// Calculates the index of the start of the next big word (W motion).
        /// </summary>
        /// <param name="text">The text to traverse.</param>
        /// <param name="caret">The current caret index.</param>
        /// <returns>The new caret index.</returns>
        public static int MoveNextWordBig(string text, int caret)
        {
            if (caret >= text.Length) return text.Length;

            int i = caret;

            while (i < text.Length && !char.IsWhiteSpace(text[i]))
                i++;

            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            return Math.Min(i, text.Length);
        }

        /// <summary>
        /// Calculates the index of the start of the previous big word (B motion).
        /// </summary>
        /// <param name="text">The text to traverse.</param>
        /// <param name="caret">The current caret index.</param>
        /// <returns>The new caret index.</returns>
        public static int MovePrevWordBig(string text, int caret)
        {
            if (caret <= 0) return 0;

            int i = caret - 1;

            while (i > 0 && char.IsWhiteSpace(text[i]))
                i--;

            while (i > 0 && !char.IsWhiteSpace(text[i - 1]))
                i--;

            return Math.Max(0, i);
        }

        /// <summary>
        /// Calculates the index of the end of the current or next big word (E motion).
        /// </summary>
        /// <param name="text">The text to traverse.</param>
        /// <param name="caret">The current caret index.</param>
        /// <returns>The new caret index.</returns>
        public static int MoveEndWordBig(string text, int caret)
        {
            if (text.Length == 0) return 0;
            if (caret >= text.Length - 1) return text.Length - 1;

            int i = caret + 1;

            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            if (i >= text.Length) return caret;

            while (i < text.Length - 1 && !char.IsWhiteSpace(text[i + 1]))
                i++;

            return Math.Min(i, text.Length - 1);
        }

        /// <summary>
        /// Finds the index of the matching bracket (% motion).
        /// </summary>
        /// <param name="text">The text containing brackets.</param>
        /// <param name="caret">The current caret index.</param>
        /// <returns>The index of the matching bracket, or the original caret if none.</returns>
        public static int MoveToMatchingBracket(string text, int caret)
        {
            if (caret >= text.Length) return caret;
            char c = text[caret];

            char target;
            bool forward;
            switch (c)
            {
                case '(': target = ')'; forward = true; break;
                case ')': target = '('; forward = false; break;
                case '[': target = ']'; forward = true; break;
                case ']': target = '['; forward = false; break;
                case '{': target = '}'; forward = true; break;
                case '}': target = '{'; forward = false; break;
                default: return caret;
            }

            int depth = 0;
            char open = forward ? c : target;
            char close = forward ? target : c;

            if (forward)
            {
                for (int i = caret; i < text.Length; i++)
                {
                    if (text[i] == open) depth++;
                    else if (text[i] == close) { depth--; if (depth == 0) return i; }
                }
            }
            else
            {
                for (int i = caret; i >= 0; i--)
                {
                    if (text[i] == close) depth++;
                    else if (text[i] == open) { depth--; if (depth == 0) return i; }
                }
            }

            return caret;
        }

        /// <summary>
        /// Calculates the selection bounds for a word text object (iw / aw).
        /// </summary>
        /// <param name="text">The text.</param>
        /// <param name="caret">The current caret index.</param>
        /// <param name="around">True for aw, false for iw.</param>
        /// <returns>A tuple containing the start and end indices of the selection.</returns>
        public static (int start, int end) TextObjectWord(string text, int caret, bool around)
        {
            if (text.Length == 0) return (0, 0);

            int start, end;
            int i = Math.Min(caret, text.Length - 1);
            bool isWs = char.IsWhiteSpace(text[i]);
            bool startIsWord = IsWordChar(text[i]);

            while (i > 0)
            {
                if (isWs)
                {
                    if (!char.IsWhiteSpace(text[i - 1])) break;
                }
                else
                {
                    if (IsWordChar(text[i - 1]) != startIsWord || char.IsWhiteSpace(text[i - 1])) break;
                }
                i--;
            }
            start = i;

            i = Math.Min(caret, text.Length - 1);
            while (i < text.Length - 1)
            {
                if (isWs)
                {
                    if (!char.IsWhiteSpace(text[i + 1])) break;
                }
                else
                {
                    if (IsWordChar(text[i + 1]) != startIsWord || char.IsWhiteSpace(text[i + 1])) break;
                }
                i++;
            }
            end = i;

            if (around && !isWs)
            {
                if (end + 1 < text.Length && char.IsWhiteSpace(text[end + 1]))
                {
                    while (end + 1 < text.Length && char.IsWhiteSpace(text[end + 1])) end++;
                }
                else if (start > 0 && char.IsWhiteSpace(text[start - 1]))
                {
                    while (start > 0 && char.IsWhiteSpace(text[start - 1])) start--;
                }
            }

            return (start, end);
        }

        /// <summary>
        /// Calculates the selection bounds for a delimited text object, such as parentheses or brackets.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <param name="caret">The current caret index.</param>
        /// <param name="open">The opening delimiter.</param>
        /// <param name="close">The closing delimiter.</param>
        /// <param name="around">True to include the delimiters (a object), false to exclude (i object).</param>
        /// <returns>A tuple containing the start and end indices, or (-1, -1) if invalid.</returns>
        public static (int start, int end) TextObjectDelimited(string text, int caret, char open, char close, bool around)
        {
            int openPos = -1;
            int depth = 0;
            for (int i = Math.Min(caret, text.Length - 1); i >= 0; i--)
            {
                if (text[i] == close) depth++;
                else if (text[i] == open)
                {
                    if (depth == 0) { openPos = i; break; }
                    depth--;
                }
            }

            if (openPos == -1) return (-1, -1);

            int closePos = -1;
            depth = 0;
            for (int i = openPos + 1; i < text.Length; i++)
            {
                if (text[i] == open) depth++;
                else if (text[i] == close)
                {
                    if (depth == 0) { closePos = i; break; }
                    depth--;
                }
            }

            if (closePos == -1) return (-1, -1);

            int start = around ? openPos : openPos + 1;
            int end = around ? closePos : closePos - 1;

            if (start > end) return (start, start - 1);

            return (start, end);
        }

        /// <summary>
        /// Calculates the selection bounds for a quote text object.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <param name="caret">The current caret index.</param>
        /// <param name="quote">The quote character.</param>
        /// <param name="around">True to include the quotes, false to exclude.</param>
        /// <returns>A tuple containing the start and end indices, or (-1, -1) if invalid.</returns>
        public static (int start, int end) TextObjectQuote(string text, int caret, char quote, bool around)
        {
            if (text.Length == 0) return (-1, -1);
            // CaretIndex can be text.Length (caret past the last char); clamp so end-of-query
            // resolves onto the closing quote and ci"/di" still match the last quoted region.
            caret = Math.Max(0, Math.Min(caret, text.Length - 1));
            int first = -1;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == quote)
                {
                    if (first == -1) 
                    {
                        first = i;
                    }
                    else 
                    {
                        if (caret >= first && caret <= i)
                        {
                            int start = around ? first : first + 1;
                            int end = around ? i : i - 1;
                            if (start > end) return (start, start - 1);
                            return (start, end);
                        }
                        first = -1;
                    }
                }
            }

            return (-1, -1);
        }

        

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        /// <summary>
        /// Finds the next occurrence of a character (f / t motion).
        /// </summary>
        /// <param name="text">The text.</param>
        /// <param name="caret">The current caret index.</param>
        /// <param name="target">The character to find.</param>
        /// <param name="till">True for t (stops before), false for f (lands on).</param>
        /// <returns>The index of the target character, or the original caret if not found.</returns>
        public static int FindCharForward(string text, int caret, char target, bool till = false)
        {
            if (caret >= text.Length - 1) return caret;
            int start = caret + 1;
            int index = text.IndexOf(target, start);
            if (index == -1) return caret;
            return till ? index - 1 : index;
        }

        /// <summary>
        /// Finds the previous occurrence of a character (F / T motion).
        /// </summary>
        /// <param name="text">The text.</param>
        /// <param name="caret">The current caret index.</param>
        /// <param name="target">The character to find.</param>
        /// <param name="till">True for T (stops after), false for F (lands on).</param>
        /// <returns>The index of the target character, or the original caret if not found.</returns>
        public static int FindCharBackward(string text, int caret, char target, bool till = false)
        {
            if (caret <= 0) return caret;
            int start = caret - 1;
            int index = text.LastIndexOf(target, start);
            if (index == -1) return caret;
            return till ? index + 1 : index;
        }

        // ─── Multi-line helpers ──────────────────────────────────────────────
        // These treat '\n' as the line separator and tolerate a preceding '\r'
        // (WPF multi-line TextBoxes use "\r\n"). A "column" is the offset of the
        // caret from the start of its line, measured in raw string characters.

        /// <summary>
        /// Returns the zero-based line number containing the given caret index.
        /// </summary>
        public static int GetLineNumber(string text, int caret)
        {
            if (text.Length == 0) return 0;
            caret = Math.Clamp(caret, 0, text.Length);
            int line = 0;
            for (int i = 0; i < caret; i++)
                if (text[i] == '\n') line++;
            return line;
        }

        /// <summary>
        /// Returns the caret index of the first character of the line containing <paramref name="caret"/>.
        /// </summary>
        public static int GetLineStart(string text, int caret)
        {
            caret = Math.Clamp(caret, 0, text.Length);
            int i = caret;
            while (i > 0 && text[i - 1] != '\n') i--;
            return i;
        }

        /// <summary>
        /// Returns the caret index just past the last content character of the line containing
        /// <paramref name="caret"/> — i.e., the position of the line's terminator ('\r' or '\n'),
        /// or the text length for the final line. This is the line-aware analogue of "$".
        /// </summary>
        public static int GetLineEnd(string text, int caret)
        {
            caret = Math.Clamp(caret, 0, text.Length);
            int i = caret;
            while (i < text.Length && text[i] != '\n') i++;
            // i now sits on the line's '\n' or at text length; exclude a preceding '\r' (CRLF).
            if (i > 0 && text[i - 1] == '\r') return i - 1;
            return i;
        }

        /// <summary>
        /// Returns the caret's column: its character offset from the start of its line.
        /// </summary>
        public static int GetColumn(string text, int caret)
        {
            return Math.Clamp(caret, 0, text.Length) - GetLineStart(text, caret);
        }

        /// <summary>
        /// Moves the caret one line down, preserving the column where possible (j). Returns the
        /// original index when already on the last line.
        /// </summary>
        public static int MoveDown(string text, int caret)
        {
            caret = Math.Clamp(caret, 0, text.Length);
            int col = GetColumn(text, caret);

            // Find the start of the next line (char after the next '\n').
            int nl = caret;
            while (nl < text.Length && text[nl] != '\n') nl++;
            if (nl >= text.Length) return caret; // no line below

            int nextStart = nl + 1;
            int nextEnd = GetLineEnd(text, nextStart);
            return Math.Min(nextStart + col, nextEnd);
        }

        /// <summary>
        /// Moves the caret one line up, preserving the column where possible (k). Returns the
        /// original index when already on the first line.
        /// </summary>
        public static int MoveUp(string text, int caret)
        {
            caret = Math.Clamp(caret, 0, text.Length);
            int col = GetColumn(text, caret);
            int lineStart = GetLineStart(text, caret);
            if (lineStart == 0) return caret; // no line above

            int prevStart = GetLineStart(text, lineStart - 1);
            int prevEnd = GetLineEnd(text, lineStart - 1);
            return Math.Min(prevStart + col, prevEnd);
        }

        /// <summary>
        /// Returns the character range for the line containing <paramref name="caret"/>.
        /// When <paramref name="includeLineBreak"/> is true (dd / yy), the range includes the
        /// line's terminator — and for the final line, the preceding terminator instead, so the
        /// delete doesn't leave a dangling blank line. When false (cc), only the content is covered.
        /// </summary>
        public static (int start, int end) GetLineRange(string text, int caret, bool includeLineBreak)
        {
            int start = GetLineStart(text, caret);
            int contentEnd = GetLineEnd(text, caret);
            if (!includeLineBreak)
                return (start, contentEnd);

            int end = contentEnd;
            if (end < text.Length && text[end] == '\r') end++;
            if (end < text.Length && text[end] == '\n') end++;

            if (end == contentEnd)
            {
                // Final line (no trailing terminator): consume the preceding terminator instead.
                int s = start;
                if (s > 0 && text[s - 1] == '\n') s--;
                if (s > 0 && text[s - 1] == '\r') s--;
                return (s, end);
            }
            return (start, end);
        }

        /// <summary>
        /// Returns the index of the first non-blank character on the line containing
        /// <paramref name="caret"/> (line-relative '^'), or the line start if the line is all blank.
        /// </summary>
        public static int MoveFirstNonBlankOfLine(string text, int caret)
        {
            int start = GetLineStart(text, caret);
            int end = GetLineEnd(text, caret);
            for (int i = start; i < end; i++)
                if (!char.IsWhiteSpace(text[i])) return i;
            return start;
        }

        /// <summary>
        /// Returns the range a line-wise operator (dd / cc / yy) should act on.
        /// In single-line mode the operator covers the whole query (the historical behaviour);
        /// in multi-line mode it covers the current line, via <see cref="GetLineRange"/>.
        /// This is the single source of truth for the mode difference, so both modes are testable.
        /// </summary>
        public static (int start, int end) LineOperatorRange(string text, int caret, bool multiLine, bool includeLineBreak)
        {
            if (!multiLine) return (0, text.Length);
            return GetLineRange(text, caret, includeLineBreak);
        }

        /// <summary>
        /// Returns the character range spanning every whole line between the two caret positions
        /// (inclusive), with line terminators — used for line-wise operators like dj / dk and
        /// Visual Line. When the span reaches the end of the buffer, the leading terminator is also
        /// consumed so the delete leaves no dangling blank line.
        /// </summary>
        public static (int start, int end) GetLinewiseRange(string text, int a, int b)
        {
            int lo = Math.Max(0, Math.Min(a, b));
            int hi = Math.Max(0, Math.Max(a, b));
            int start = GetLineStart(text, lo);
            int end = GetLineRange(text, hi, includeLineBreak: true).end;
            if (end >= text.Length)
            {
                if (start > 0 && text[start - 1] == '\n') start--;
                if (start > 0 && text[start - 1] == '\r') start--;
            }
            return (start, Math.Max(start, end));
        }
    }
}
