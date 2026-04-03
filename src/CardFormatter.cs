using System;
using System.Collections.Generic;
using Il2CppYgomGame.Card;

namespace DuelLinksAccess
{
    /// <summary>
    /// Shared card formatting utilities for accessibility announcements.
    /// Used by DeckEditHandler, ShopHandler, TicketExchangeHandler, and others
    /// that need to read card names and stats via the Content database.
    /// </summary>
    public static class CardFormatter
    {
        /// <summary>
        /// Compact card announcement: name, type info, stats.
        /// Example: "Dark Magician, Dark Spellcaster, Level 7, ATK 2500 DEF 2100"
        /// </summary>
        public static string FormatCompact(int mrk)
        {
            try
            {
                var content = Content.Instance;
                if (content == null) return GetName(mrk);

                string name = content.GetName(mrk) ?? Loc.Get("duel_unknown_card");
                var kind = content.GetKind(mrk);

                if (IsMonsterKind(kind))
                {
                    var attr = content.GetAttr(mrk);
                    var type = content.GetType(mrk);
                    int level = content.GetLevel(mrk);
                    int atk = content.GetAtk(mrk);
                    int def = content.GetDef2(mrk);

                    string attrText = content.GetAttributeText(attr);
                    string typeText = content.GetTypeText(type);
                    string kindText = content.GetKindText(kind);
                    string lvLabel = GetLevelLabel(mrk);

                    return $"{name}, {attrText} {typeText} {kindText}, {lvLabel} {level}, ATK {atk} DEF {def}";
                }
                else
                {
                    var icon = content.GetIcon(mrk);
                    string kindText = content.GetKindText(kind);

                    if (icon != Content.Icon.Null)
                    {
                        string iconText = content.GetIconText(icon);
                        return $"{name}, {iconText} {kindText}";
                    }

                    return $"{name}, {kindText}";
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardFormatter",
                    $"FormatCompact error for mrk={mrk}: {ex.Message}");
                return GetName(mrk);
            }
        }

        /// <summary>
        /// Verbose card announcement: compact info + description.
        /// </summary>
        public static string FormatVerbose(int mrk)
        {
            string compact = FormatCompact(mrk);

            try
            {
                var content = Content.Instance;
                string desc = content?.GetDesc(mrk);

                var parts = new List<string> { compact };

                if (!string.IsNullOrEmpty(desc))
                    parts.Add(desc);

                return string.Join(". ", parts);
            }
            catch
            {
                return compact;
            }
        }

        /// <summary>
        /// Gets a card's name from the Content database.
        /// </summary>
        public static string GetName(int mrk)
        {
            try
            {
                return Content.Instance?.GetName(mrk) ?? Loc.Get("duel_unknown_card");
            }
            catch
            {
                return Loc.Get("duel_unknown_card");
            }
        }

        /// <summary>
        /// Checks if a Content.Kind value represents a monster card.
        /// </summary>
        public static bool IsMonsterKind(Content.Kind kind)
        {
            return kind != Content.Kind.Magic
                && kind != Content.Kind.Trap;
        }

        /// <summary>
        /// Gets the level/rank label for a card.
        /// </summary>
        private static string GetLevelLabel(int mrk)
        {
            // Default to "Level" which covers most cards in Duel Links
            return Loc.Get("deck_level");
        }
    }
}
