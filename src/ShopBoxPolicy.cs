using System;

namespace DuelLinksAccess
{
    /// <summary>
    /// Pure policy for identifying and categorizing shop card boxes and packs.
    /// Decoupled from Unity/IL2CPP for full unit-testability.
    /// </summary>
    public static class ShopBoxPolicy
    {
        /// <summary>
        /// Checks if a string represents a generic box or pack category
        /// rather than a specific box/product title.
        /// </summary>
        public static bool IsGenericBoxCategory(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string trimmed = s.Trim();

            string normalized = trimmed.Replace(" ", "").Replace("/", "").Replace("-", "").ToLowerInvariant();

            if (normalized == "主卡盒" || normalized == "迷你卡盒" || normalized == "超迷你卡盒"
                || normalized == "mainbox" || normalized == "minibox" || normalized == "superminibox"
                || normalized == "selectionbox" || normalized == "selectionboxmini"
                || normalized == "构组卡组" || normalized == "预组卡组" || normalized == "structuredeck"
                || normalized == "structuredeckex"
                || normalized == "主卡盒快速决斗" || normalized == "迷你卡盒快速决斗" || normalized == "超迷你卡盒快速决斗"
                || normalized == "主卡盒超速决斗" || normalized == "迷你卡盒超速决斗" || normalized == "超迷你卡盒超速决斗"
                || normalized == "最近看过的卡盒快速决斗" || normalized == "最近看过的卡盒超速决斗"
                || normalized == "最近看过的卡盒")
            {
                return true;
            }

            if (trimmed.StartsWith("发布日期：") || trimmed.StartsWith("发布日期:"))
            {
                return true;
            }

            return false;
        }
    }
}
