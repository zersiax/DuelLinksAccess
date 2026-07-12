using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DuelLinksAccess
{
    public enum DialogMixFragmentKind
    {
        Ignore,
        Text,
        Break,
        Insert,
    }

    public readonly record struct DialogMixFragment(
        DialogMixFragmentKind Kind, string Text);

    public static class DialogMixComposer
    {
        public static string Compose(IReadOnlyList<DialogMixFragment> fragments)
        {
            if (fragments == null || fragments.Count == 0) return null;

            var consumed = new bool[fragments.Count];
            var result = new StringBuilder();
            int nextInsert = 0;

            string TakeNextInsert()
            {
                while (nextInsert < fragments.Count)
                {
                    int index = nextInsert++;
                    if (consumed[index]
                        || fragments[index].Kind != DialogMixFragmentKind.Insert)
                    {
                        continue;
                    }

                    consumed[index] = true;
                    return fragments[index].Text ?? "";
                }
                return "";
            }

            for (int i = 0; i < fragments.Count; i++)
            {
                DialogMixFragment fragment = fragments[i];
                switch (fragment.Kind)
                {
                    case DialogMixFragmentKind.Text:
                        string text = Regex.Replace(fragment.Text ?? "", @"@\d", "");
                        result.Append(SpeechTextFormatter.SubstituteDialogPlaceholders(
                            text, TakeNextInsert));
                        break;

                    case DialogMixFragmentKind.Break:
                        result.Append(' ');
                        break;

                    case DialogMixFragmentKind.Insert:
                        if (!consumed[i] && !string.IsNullOrEmpty(fragment.Text))
                        {
                            consumed[i] = true;
                            result.Append(fragment.Text);
                        }
                        break;
                }
            }

            string composed = SpeechTextFormatter.StripRichText(result.ToString());
            composed = Regex.Replace(composed, @"  +", " ");
            return string.IsNullOrEmpty(composed) ? null : composed;
        }
    }
}
