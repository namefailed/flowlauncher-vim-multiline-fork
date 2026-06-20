using System;
using System.Globalization;
using System.Windows.Data;

namespace Flow.Launcher.Converters
{
    /// <summary>
    /// Collapses line breaks in a result title to spaces so a multi-line editor query renders as a
    /// single-line result (you can still tell which plugin is matched). The replacement is
    /// char-for-char ('\r'/'\n' -> ' '), preserving string length so the title's highlight offsets
    /// stay valid. Display only — the underlying query/command keeps its newlines.
    /// </summary>
    public class MultilineTitleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s && (s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0))
                return s.Replace('\r', ' ').Replace('\n', ' ');
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value;
    }
}
