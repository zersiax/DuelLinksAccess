using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Cleans and formats speech text before sending to Tolk/screen reader.
        /// Merges vertical CJK characters separated by newlines, eliminates broken pauses,
        /// and ensures smooth, natural reading flow.
        /// </summary>
        public static string CleanSpeechText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 1. Strip rich text formatting
            text = StripRichText(text);
            if (string.IsNullOrEmpty(text)) return text;

            // 2. Normalize line breaks and non-breaking spaces
            text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace('\u00a0', ' ');

            // 3. Handle vertical CJK layout or character-by-character newlines
            var paragraphs = Regex.Split(text, @"\n{2,}");
            var cleanedParas = new List<string>(paragraphs.Length);

            foreach (var para in paragraphs)
            {
                if (string.IsNullOrWhiteSpace(para)) continue;

                var rawLines = para.Split('\n');
                var lines = new List<string>(rawLines.Length);
                foreach (var l in rawLines)
                {
                    string t = l.Trim();
                    if (!string.IsNullOrEmpty(t)) lines.Add(t);
                }
                if (lines.Count == 0) continue;

                // Check if this paragraph appears to be vertically formatted
                // (e.g. many short lines <= 3 characters)
                int shortLineCount = 0;
                foreach (var l in lines)
                {
                    if (l.Length <= 3) shortLineCount++;
                }

                if (lines.Count >= 3 && (float)shortLineCount / lines.Count > 0.5f)
                {
                    // Vertical format detected: merge lines smoothly without spaces between CJK
                    var sb = new StringBuilder();
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string line = lines[i];
                        if (sb.Length == 0)
                        {
                            sb.Append(line);
                        }
                        else
                        {
                            char prev = sb[sb.Length - 1];
                            char next = line[0];
                            // Insert space only between ASCII alphanumeric words
                            if (char.IsLetterOrDigit(prev) && prev < 128 && char.IsLetterOrDigit(next) && next < 128)
                            {
                                sb.Append(' ');
                            }
                            sb.Append(line);
                        }
                    }
                    cleanedParas.Add(sb.ToString());
                }
                else
                {
                    // Standard paragraph: collapse single newlines
                    // If newline is between CJK characters, join without space; otherwise join with space
                    var sb = new StringBuilder();
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string line = lines[i];
                        if (sb.Length == 0)
                        {
                            sb.Append(line);
                        }
                        else
                        {
                            char prev = sb[sb.Length - 1];
                            char next = line[0];
                            bool prevCjk = (prev >= '\u2e80' && prev <= '\u9fff') || (prev >= '\u3000' && prev <= '\u303f') || (prev >= '\uff00' && prev <= '\uffef');
                            bool nextCjk = (next >= '\u2e80' && next <= '\u9fff') || (next >= '\u3000' && next <= '\u303f') || (next >= '\uff00' && next <= '\uffef');

                            if (prevCjk && nextCjk)
                            {
                                sb.Append(line);
                            }
                            else
                            {
                                sb.Append(' ');
                                sb.Append(line);
                            }
                        }
                    }
                    cleanedParas.Add(sb.ToString());
                }
            }

            // Join paragraphs with natural sentence pause
            var finalSb = new StringBuilder();
            for (int i = 0; i < cleanedParas.Count; i++)
            {
                string p = cleanedParas[i];
                if (finalSb.Length > 0)
                {
                    char lastChar = finalSb[finalSb.Length - 1];
                    if (lastChar == '。' || lastChar == '！' || lastChar == '？' || lastChar == '.' || lastChar == '!' || lastChar == '?')
                    {
                        finalSb.Append(' ');
                    }
                    else
                    {
                        finalSb.Append("，");
                    }
                }
                finalSb.Append(p);
            }

            string result = finalSb.ToString();
            result = Regex.Replace(result, @"[，\s]+([。！])", "$1");
            result = Regex.Replace(result, @"，+", "，");
            result = Regex.Replace(result, @"\s{2,}", " ");
            return result.Trim();
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
