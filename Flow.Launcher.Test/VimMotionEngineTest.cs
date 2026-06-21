using NUnit.Framework;
using Flow.Launcher.VimMode;

namespace Flow.Launcher.Test
{
    [TestFixture]
    public class VimMotionEngineTest
    {
        private const string Sample = "hello world_foo bar";

        [Test]
        public void MoveLeftTest()
        {
            Assert.That(VimMotionEngine.MoveLeft(5), Is.EqualTo(4));
            Assert.That(VimMotionEngine.MoveLeft(0), Is.EqualTo(0));
            Assert.That(VimMotionEngine.MoveLeft(2), Is.EqualTo(1));
        }

        [Test]
        public void MoveRightTest()
        {
            Assert.That(VimMotionEngine.MoveRight(5, Sample.Length), Is.EqualTo(6));
            Assert.That(VimMotionEngine.MoveRight(Sample.Length, Sample.Length), Is.EqualTo(Sample.Length));
            Assert.That(VimMotionEngine.MoveRight(Sample.Length - 1, Sample.Length), Is.EqualTo(Sample.Length));
        }

        [Test]
        public void MoveStartOfLineTest()
        {
            Assert.That(VimMotionEngine.MoveStartOfLine(), Is.EqualTo(0));
        }

        [Test]
        public void MoveEndOfLineTest()
        {
            Assert.That(VimMotionEngine.MoveEndOfLine(Sample.Length), Is.EqualTo(Sample.Length));
            Assert.That(VimMotionEngine.MoveEndOfLine(0), Is.EqualTo(0));
        }

        [Test]
        public void MoveFirstNonBlankTest()
        {
            Assert.That(VimMotionEngine.MoveFirstNonBlank("   hello"), Is.EqualTo(3));
            Assert.That(VimMotionEngine.MoveFirstNonBlank("hello"), Is.EqualTo(0));
            Assert.That(VimMotionEngine.MoveFirstNonBlank("    "), Is.EqualTo(0));
            Assert.That(VimMotionEngine.MoveFirstNonBlank(""), Is.EqualTo(0));
        }

        [Test]
        public void MoveNextWordTest()
        {
            Assert.That(VimMotionEngine.MoveNextWord(Sample, 0), Is.EqualTo(6));
            Assert.That(VimMotionEngine.MoveNextWord(Sample, 6), Is.EqualTo(16));
            Assert.That(VimMotionEngine.MoveNextWord(Sample, Sample.Length), Is.EqualTo(Sample.Length));
            Assert.That(VimMotionEngine.MoveNextWord("   abc", 0), Is.EqualTo(3));
        }

        [Test]
        public void MovePrevWordTest()
        {
            Assert.That(VimMotionEngine.MovePrevWord(Sample, 6), Is.EqualTo(0));
            Assert.That(VimMotionEngine.MovePrevWord("hello   world", 8), Is.EqualTo(0));
            Assert.That(VimMotionEngine.MovePrevWord(Sample, 0), Is.EqualTo(0));
        }

        [Test]
        public void MoveEndWordTest()
        {
            Assert.That(VimMotionEngine.MoveEndWord(Sample, 0), Is.EqualTo(4));
            Assert.That(VimMotionEngine.MoveEndWord(Sample, 5), Is.EqualTo(14));
            Assert.That(VimMotionEngine.MoveEndWord(Sample, Sample.Length - 1), Is.EqualTo(Sample.Length - 1));
        }

        [Test]
        public void FindCharForwardTest()
        {
            Assert.That(VimMotionEngine.FindCharForward(Sample, 0, 'o', false), Is.EqualTo(4));
            Assert.That(VimMotionEngine.FindCharForward(Sample, 0, 'o', till: true), Is.EqualTo(3));
            Assert.That(VimMotionEngine.FindCharForward(Sample, 0, 'z', false), Is.EqualTo(0));
            Assert.That(VimMotionEngine.FindCharForward(Sample, Sample.Length - 1, 'o', false), Is.EqualTo(Sample.Length - 1));
            Assert.That(VimMotionEngine.FindCharForward(Sample, 4, 'o', false), Is.EqualTo(7));
        }

        [Test]
        public void FindCharBackwardTest()
        {
            Assert.That(VimMotionEngine.FindCharBackward(Sample, 7, 'o', false), Is.EqualTo(4));
            Assert.That(VimMotionEngine.FindCharBackward(Sample, 7, 'o', till: true), Is.EqualTo(5));
            Assert.That(VimMotionEngine.FindCharBackward(Sample, Sample.Length - 1, 'z', false), Is.EqualTo(Sample.Length - 1));
            Assert.That(VimMotionEngine.FindCharBackward(Sample, 0, 'o', false), Is.EqualTo(0));
            Assert.That(VimMotionEngine.FindCharBackward(Sample, 4, 'o', false), Is.EqualTo(4));
        }

        [Test]
        public void MoveEndWordEmptyOrEndDoesNotReturnNegative()
        {
            Assert.That(VimMotionEngine.MoveEndWord("", 0), Is.EqualTo(0));
            Assert.That(VimMotionEngine.MoveEndWordBig("", 0), Is.EqualTo(0));
            Assert.That(VimMotionEngine.MoveEndWord("a", 0), Is.EqualTo(0));
        }

        [Test]
        public void MoveLastNonBlankTest()
        {
            Assert.That(VimMotionEngine.MoveLastNonBlank("hello  "), Is.EqualTo(5));
            Assert.That(VimMotionEngine.MoveLastNonBlank("hello"), Is.EqualTo(5));
            Assert.That(VimMotionEngine.MoveLastNonBlank("   "), Is.EqualTo(0));
            Assert.That(VimMotionEngine.MoveLastNonBlank(""), Is.EqualTo(0));
        }

        [Test]
        public void MoveNextWordBigTest()
        {
            Assert.That(VimMotionEngine.MoveNextWordBig("foo bar baz", 0), Is.EqualTo(4));
            Assert.That(VimMotionEngine.MoveNextWordBig("foo bar baz", 4), Is.EqualTo(8));
            // Punctuation is part of a BIG word (unlike w).
            Assert.That(VimMotionEngine.MoveNextWordBig("foo.bar baz", 0), Is.EqualTo(8));
            Assert.That(VimMotionEngine.MoveNextWordBig("foo", 3), Is.EqualTo(3));
        }

        [Test]
        public void MovePrevWordBigTest()
        {
            Assert.That(VimMotionEngine.MovePrevWordBig("foo bar baz", 8), Is.EqualTo(4));
            Assert.That(VimMotionEngine.MovePrevWordBig("foo bar", 4), Is.EqualTo(0));
            Assert.That(VimMotionEngine.MovePrevWordBig("foo", 0), Is.EqualTo(0));
        }

        [Test]
        public void MoveEndWordBigTest()
        {
            Assert.That(VimMotionEngine.MoveEndWordBig("foo bar", 0), Is.EqualTo(2));
            Assert.That(VimMotionEngine.MoveEndWordBig("foo bar", 2), Is.EqualTo(6));
        }

        [Test]
        public void MoveToMatchingBracketTest()
        {
            Assert.That(VimMotionEngine.MoveToMatchingBracket("a(bc)d", 1), Is.EqualTo(4));
            Assert.That(VimMotionEngine.MoveToMatchingBracket("a(bc)d", 4), Is.EqualTo(1));
            Assert.That(VimMotionEngine.MoveToMatchingBracket("(a(b)c)", 0), Is.EqualTo(6));
            // No bracket under the caret: stay put.
            Assert.That(VimMotionEngine.MoveToMatchingBracket("abc", 1), Is.EqualTo(1));
        }

        [Test]
        public void TextObjectWordTest()
        {
            Assert.That(VimMotionEngine.TextObjectWord("foo bar", 1, false), Is.EqualTo((0, 2)));
            // aw includes the trailing whitespace.
            Assert.That(VimMotionEngine.TextObjectWord("foo bar", 1, true), Is.EqualTo((0, 3)));
            Assert.That(VimMotionEngine.TextObjectWord("foo bar", 5, false), Is.EqualTo((4, 6)));
            // No trailing whitespace at end of string -> aw extends over leading whitespace instead.
            Assert.That(VimMotionEngine.TextObjectWord("foo bar", 5, true), Is.EqualTo((3, 6)));
            Assert.That(VimMotionEngine.TextObjectWord("", 0, false), Is.EqualTo((0, 0)));
        }

        [Test]
        public void TextObjectQuoteTest()
        {
            Assert.That(VimMotionEngine.TextObjectQuote("say \"hi\" now", 5, '"', false), Is.EqualTo((5, 6)));
            Assert.That(VimMotionEngine.TextObjectQuote("say \"hi\" now", 5, '"', true), Is.EqualTo((4, 7)));
            Assert.That(VimMotionEngine.TextObjectQuote("hello", 1, '"', false), Is.EqualTo((-1, -1)));
            // caret past the last char (CaretIndex == text.Length) clamps onto the closing quote
            Assert.That(VimMotionEngine.TextObjectQuote("say \"hi\"", 8, '"', false), Is.EqualTo((5, 6)));
            Assert.That(VimMotionEngine.TextObjectQuote("say \"hi\"", 8, '"', true), Is.EqualTo((4, 7)));
            Assert.That(VimMotionEngine.TextObjectQuote("", 0, '"', false), Is.EqualTo((-1, -1)));
        }

        [Test]
        public void ChangeNumberTest()
        {
            // Ctrl-A / Ctrl-X on the number at or to the right of the caret.
            Assert.That(VimMotionEngine.ChangeNumber("port 8080", 0, 1), Is.EqualTo((true, "port 8081", 8)));
            Assert.That(VimMotionEngine.ChangeNumber("v1", 0, 1), Is.EqualTo((true, "v2", 1)));
            Assert.That(VimMotionEngine.ChangeNumber("9", 0, 1), Is.EqualTo((true, "10", 1)));
            Assert.That(VimMotionEngine.ChangeNumber("5", 0, -1), Is.EqualTo((true, "4", 0)));
            Assert.That(VimMotionEngine.ChangeNumber("count -5 x", 0, 1), Is.EqualTo((true, "count -4 x", 7)));
            Assert.That(VimMotionEngine.ChangeNumber("abc", 0, 1).found, Is.False);
        }

        [Test]
        public void TextObjectDelimitedTest()
        {
            Assert.That(VimMotionEngine.TextObjectDelimited("f(a, b)", 3, '(', ')', false), Is.EqualTo((2, 5)));
            Assert.That(VimMotionEngine.TextObjectDelimited("f(a, b)", 3, '(', ')', true), Is.EqualTo((1, 6)));
            // Nested: caret inside the inner pair selects the inner pair.
            Assert.That(VimMotionEngine.TextObjectDelimited("(a(b)c)", 3, '(', ')', false), Is.EqualTo((3, 3)));
            Assert.That(VimMotionEngine.TextObjectDelimited("(a(b)c)", 3, '(', ')', true), Is.EqualTo((2, 4)));
            Assert.That(VimMotionEngine.TextObjectDelimited("abc", 1, '(', ')', false), Is.EqualTo((-1, -1)));
        }

        [Test]
        public void OperatorRangeExclusiveTest()
        {
            // dw etc.: half-open, destination not included, in either direction.
            Assert.That(VimMotionEngine.OperatorRange(0, 3, MotionInclusivity.Exclusive, 10), Is.EqualTo((0, 3)));
            Assert.That(VimMotionEngine.OperatorRange(5, 2, MotionInclusivity.Exclusive, 10), Is.EqualTo((2, 5)));
        }

        [Test]
        public void OperatorRangeInclusiveForwardTest()
        {
            // de / df: forward motion consumes the destination character.
            Assert.That(VimMotionEngine.OperatorRange(0, 3, MotionInclusivity.InclusiveForward, 10), Is.EqualTo((0, 4)));
            // At the last index it still extends to the end of the text.
            Assert.That(VimMotionEngine.OperatorRange(0, 9, MotionInclusivity.InclusiveForward, 10), Is.EqualTo((0, 10)));
            // Never extends past the text length.
            Assert.That(VimMotionEngine.OperatorRange(0, 10, MotionInclusivity.InclusiveForward, 10), Is.EqualTo((0, 10)));
            // Backward find (F/T): the original caret char is NOT consumed.
            Assert.That(VimMotionEngine.OperatorRange(5, 2, MotionInclusivity.InclusiveForward, 10), Is.EqualTo((2, 5)));
            // Failed find (target == caret): empty range, nothing deleted.
            Assert.That(VimMotionEngine.OperatorRange(5, 5, MotionInclusivity.InclusiveForward, 10), Is.EqualTo((5, 5)));
        }

        [Test]
        public void OperatorRangeInclusivePairTest()
        {
            // d% with caret on '(': forward, both brackets included.
            Assert.That(VimMotionEngine.OperatorRange(1, 4, MotionInclusivity.InclusivePair, 6), Is.EqualTo((1, 5)));
            // d% with caret on ')': backward, the far-end (the ')') is still included.
            Assert.That(VimMotionEngine.OperatorRange(4, 0, MotionInclusivity.InclusivePair, 6), Is.EqualTo((0, 5)));
            // No matching bracket (target == caret): empty range, nothing deleted.
            Assert.That(VimMotionEngine.OperatorRange(3, 3, MotionInclusivity.InclusivePair, 10), Is.EqualTo((3, 3)));
        }

        // ── Multi-line helpers ──
        // "abc\r\ndef\r\nghij": a0 b1 c2 \r3 \n4 d5 e6 f7 \r8 \n9 g10 h11 i12 j13  (len 14)
        private const string Lines = "abc\r\ndef\r\nghij";

        [Test]
        public void GetLineNumberTest()
        {
            Assert.That(VimMotionEngine.GetLineNumber(Lines, 0), Is.EqualTo(0));
            Assert.That(VimMotionEngine.GetLineNumber(Lines, 2), Is.EqualTo(0));
            Assert.That(VimMotionEngine.GetLineNumber(Lines, 5), Is.EqualTo(1));
            Assert.That(VimMotionEngine.GetLineNumber(Lines, 9), Is.EqualTo(1));
            Assert.That(VimMotionEngine.GetLineNumber(Lines, 10), Is.EqualTo(2));
            Assert.That(VimMotionEngine.GetLineNumber(Lines, 13), Is.EqualTo(2));
            Assert.That(VimMotionEngine.GetLineNumber("", 0), Is.EqualTo(0));
        }

        [Test]
        public void GetLineStartTest()
        {
            Assert.That(VimMotionEngine.GetLineStart(Lines, 2), Is.EqualTo(0));
            Assert.That(VimMotionEngine.GetLineStart(Lines, 6), Is.EqualTo(5));
            Assert.That(VimMotionEngine.GetLineStart(Lines, 13), Is.EqualTo(10));
        }

        [Test]
        public void GetLineEndTest()
        {
            // Content end excludes the CRLF terminator.
            Assert.That(VimMotionEngine.GetLineEnd(Lines, 1), Is.EqualTo(3));
            Assert.That(VimMotionEngine.GetLineEnd(Lines, 6), Is.EqualTo(8));
            // Final line has no terminator -> end is text length.
            Assert.That(VimMotionEngine.GetLineEnd(Lines, 12), Is.EqualTo(14));
            // No newlines at all -> whole string is one line.
            Assert.That(VimMotionEngine.GetLineEnd("hello", 2), Is.EqualTo(5));
        }

        [Test]
        public void GetColumnTest()
        {
            Assert.That(VimMotionEngine.GetColumn(Lines, 5), Is.EqualTo(0));
            Assert.That(VimMotionEngine.GetColumn(Lines, 6), Is.EqualTo(1));
            Assert.That(VimMotionEngine.GetColumn(Lines, 13), Is.EqualTo(3));
        }

        [Test]
        public void MoveDownPreservesColumn()
        {
            Assert.That(VimMotionEngine.MoveDown(Lines, 1), Is.EqualTo(6));   // col1 line0 -> col1 line1
            Assert.That(VimMotionEngine.MoveDown(Lines, 6), Is.EqualTo(11));  // col1 line1 -> col1 line2
            Assert.That(VimMotionEngine.MoveDown(Lines, 10), Is.EqualTo(10)); // last line -> unchanged
            // Column clamps to a shorter line below ("abcdef\r\ngh").
            Assert.That(VimMotionEngine.MoveDown("abcdef\r\ngh", 4), Is.EqualTo(10));
            // No newlines -> no line below.
            Assert.That(VimMotionEngine.MoveDown("hello", 2), Is.EqualTo(2));
        }

        [Test]
        public void MoveUpPreservesColumn()
        {
            Assert.That(VimMotionEngine.MoveUp(Lines, 11), Is.EqualTo(6));   // col1 line2 -> col1 line1
            Assert.That(VimMotionEngine.MoveUp(Lines, 1), Is.EqualTo(1));    // first line -> unchanged
            // Column clamps to a shorter line above ("ab\r\ncdef").
            Assert.That(VimMotionEngine.MoveUp("ab\r\ncdef", 7), Is.EqualTo(2));
            Assert.That(VimMotionEngine.MoveUp("hello", 2), Is.EqualTo(2));
        }

        [Test]
        public void GetLineRangeTest()
        {
            // dd / yy include the line's terminator.
            Assert.That(VimMotionEngine.GetLineRange(Lines, 1, true), Is.EqualTo((0, 5)));   // "abc\r\n"
            Assert.That(VimMotionEngine.GetLineRange(Lines, 6, true), Is.EqualTo((5, 10)));  // "def\r\n"
            // Final line: consume the PRECEDING terminator so no blank line is left behind.
            Assert.That(VimMotionEngine.GetLineRange(Lines, 11, true), Is.EqualTo((8, 14))); // "\r\nghij"
            // cc covers content only.
            Assert.That(VimMotionEngine.GetLineRange(Lines, 1, false), Is.EqualTo((0, 3)));
            Assert.That(VimMotionEngine.GetLineRange(Lines, 11, false), Is.EqualTo((10, 14)));
            // Single line: content vs whole.
            Assert.That(VimMotionEngine.GetLineRange("hello", 2, false), Is.EqualTo((0, 5)));
            Assert.That(VimMotionEngine.GetLineRange("hello", 2, true), Is.EqualTo((0, 5)));
        }

        [Test]
        public void LineOperatorRange_SingleLineMode_CoversWholeQuery()
        {
            // Single-line mode: dd/cc/yy operate on the entire query regardless of caret/content.
            Assert.That(VimMotionEngine.LineOperatorRange(Lines, 6, multiLine: false, includeLineBreak: true), Is.EqualTo((0, Lines.Length)));
            Assert.That(VimMotionEngine.LineOperatorRange(Lines, 6, multiLine: false, includeLineBreak: false), Is.EqualTo((0, Lines.Length)));
            Assert.That(VimMotionEngine.LineOperatorRange("hello world", 4, multiLine: false, includeLineBreak: true), Is.EqualTo((0, 11)));
        }

        [Test]
        public void MoveFirstNonBlankOfLineTest()
        {
            // "  ab\r\ncd": spaces then ab on line 0, cd on line 1.
            const string t = "  ab\r\ncd";
            Assert.That(VimMotionEngine.MoveFirstNonBlankOfLine(t, 0), Is.EqualTo(2)); // line 0 -> 'a'
            Assert.That(VimMotionEngine.MoveFirstNonBlankOfLine(t, 6), Is.EqualTo(6)); // line 1 -> 'c'
            // All-blank line returns its start.
            Assert.That(VimMotionEngine.MoveFirstNonBlankOfLine("  \r\nx", 0), Is.EqualTo(0));
        }

        [Test]
        public void LineOperatorRange_MultiLineMode_CoversCurrentLine()
        {
            // Multi-line mode: dd/yy include the line break, cc covers content only.
            Assert.That(VimMotionEngine.LineOperatorRange(Lines, 6, multiLine: true, includeLineBreak: true), Is.EqualTo((5, 10)));
            Assert.That(VimMotionEngine.LineOperatorRange(Lines, 6, multiLine: true, includeLineBreak: false), Is.EqualTo((5, 8)));
        }

        [Test]
        public void GetLinewiseRangeTest()
        {
            // dj from line 0 to line 1 -> both lines incl terminator: "abc\r\ndef\r\n".
            Assert.That(VimMotionEngine.GetLinewiseRange(Lines, 1, 6), Is.EqualTo((0, 10)));
            // Order-independent.
            Assert.That(VimMotionEngine.GetLinewiseRange(Lines, 6, 1), Is.EqualTo((0, 10)));
            // dk from last line to middle -> spans to end of buffer, consumes the leading terminator.
            Assert.That(VimMotionEngine.GetLinewiseRange(Lines, 11, 6), Is.EqualTo((3, 14)));
            // Same line -> just that line.
            Assert.That(VimMotionEngine.GetLinewiseRange(Lines, 1, 1), Is.EqualTo((0, 5)));
        }
    }
}
