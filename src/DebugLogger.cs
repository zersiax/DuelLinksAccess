using MelonLoader;

namespace DuelLinksAccess
{
    /// <summary>
    /// Centralized debug logging with categories.
    /// Only logs when Main.DebugMode is true (toggled with F12).
    /// </summary>
    public static class DebugLogger
    {
        /// <summary>
        /// Log a debug message with category.
        /// </summary>
        public static void Log(LogCategory category, string message)
        {
            if (!Main.DebugMode) return;

            string prefix = GetPrefix(category);
            MelonLogger.Msg($"{prefix} {message}");
        }

        /// <summary>
        /// Log a debug message with category and source.
        /// </summary>
        public static void Log(LogCategory category, string source, string message)
        {
            if (!Main.DebugMode) return;

            string prefix = GetPrefix(category);
            MelonLogger.Msg($"{prefix} [{source}] {message}");
        }

        /// <summary>
        /// Log screenreader output. Called automatically by ScreenReader.Say().
        /// </summary>
        public static void LogScreenReader(string text)
        {
            if (!Main.DebugMode) return;

            MelonLogger.Msg($"[SR] {text}");
        }

        /// <summary>
        /// Log a key press event.
        /// </summary>
        public static void LogInput(string keyName, string action = null)
        {
            if (!Main.DebugMode) return;

            string msg = action != null
                ? $"{keyName} -> {action}"
                : keyName;
            MelonLogger.Msg($"[INPUT] {msg}");
        }

        /// <summary>
        /// Log a state change (screen opened/closed, mode changed).
        /// </summary>
        public static void LogState(string description)
        {
            if (!Main.DebugMode) return;

            MelonLogger.Msg($"[STATE] {description}");
        }

        /// <summary>
        /// Log a game value that was read (for debugging data extraction).
        /// </summary>
        public static void LogGameValue(string name, object value)
        {
            if (!Main.DebugMode) return;

            MelonLogger.Msg($"[GAME] {name} = {value}");
        }

        private static string GetPrefix(LogCategory category)
        {
            return category switch
            {
                LogCategory.ScreenReader => "[SR]",
                LogCategory.Input => "[INPUT]",
                LogCategory.State => "[STATE]",
                LogCategory.Handler => "[HANDLER]",
                LogCategory.Game => "[GAME]",
                _ => "[DEBUG]"
            };
        }
    }

    /// <summary>
    /// Categories for debug logging.
    /// </summary>
    public enum LogCategory
    {
        ScreenReader,
        Input,
        State,
        Handler,
        Game
    }
}
