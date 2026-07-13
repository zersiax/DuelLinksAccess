using System;
using System.Text;
using System.Text.RegularExpressions;

namespace DuelLinksAccess
{
    public static class SpeechTextFormatter
    {
        private static readonly Regex RichTextRegex = new(
            @"<[^>]+>", RegexOptions.Compiled);
        public static string StripRichText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return RichTextRegex.Replace(text, "").Trim();
        }

        public static string SubstituteDialogPlaceholders(
            string text, Func<string> nextInsert)
        {
            if (text == null) return null;
            if (nextInsert == null)
                throw new ArgumentNullException(nameof(nextInsert));

            var result = new StringBuilder(text.Length);
            int current = 0;
            while (current < text.Length)
            {
                int stringPos = text.IndexOf("%s", current,
                    StringComparison.Ordinal);
                int numberPos = text.IndexOf("%d", current,
                    StringComparison.Ordinal);
                int next = stringPos < 0
                    ? numberPos
                    : numberPos < 0 ? stringPos : Math.Min(stringPos, numberPos);

                if (next < 0) break;

                result.Append(text, current, next - current);
                result.Append(nextInsert() ?? "");
                current = next + 2;
            }

            result.Append(text, current, text.Length - current);
            return result.ToString();
        }
    }
}
