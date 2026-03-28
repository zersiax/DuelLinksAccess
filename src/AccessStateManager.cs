using System;

namespace DuelLinksAccess
{
    /// <summary>
    /// Central state manager for accessibility handlers.
    /// Only one handler is active at a time. When a handler activates,
    /// the previous one is automatically deactivated.
    ///
    /// Usage:
    /// - Call TryEnter() when a handler becomes active
    /// - Call Exit() when it deactivates
    /// - Call SetContext() when switching major game sections
    /// - Use ForceReset() for scene changes
    /// </summary>
    public static class AccessStateManager
    {
        /// <summary>
        /// Available accessibility states. One per handler/feature that needs exclusive input.
        /// Expand as new handlers are added.
        /// </summary>
        public enum State
        {
            None,
            Home,
            Duel,
            Deck,
            Dialog,
            Shop,
        }

        /// <summary>
        /// Major game context. Resets handler state when switching between these.
        /// </summary>
        public enum Context
        {
            Unknown,
            Title,
            Game,
        }

        /// <summary>
        /// Currently active state.
        /// </summary>
        public static State Current { get; private set; } = State.None;

        /// <summary>
        /// Current context.
        /// </summary>
        public static Context CurrentContext { get; private set; } = Context.Unknown;

        /// <summary>
        /// Fired when state changes. Parameters: (oldState, newState).
        /// </summary>
        public static event Action<State, State> OnStateChanged;

        /// <summary>
        /// Fired when context changes. Parameters: (oldContext, newContext).
        /// </summary>
        public static event Action<Context, Context> OnContextChanged;

        /// <summary>
        /// Sets the current context. Resets state when context changes.
        /// </summary>
        public static void SetContext(Context context)
        {
            if (CurrentContext == context) return;

            var oldContext = CurrentContext;

            if (Current != State.None)
            {
                DebugLogger.Log(LogCategory.State, "AccessState",
                    $"Context change {oldContext} -> {context}, resetting {Current}");
                ForceReset();
            }

            CurrentContext = context;
            DebugLogger.Log(LogCategory.State, "AccessState", $"Context: {context}");
            OnContextChanged?.Invoke(oldContext, context);
        }

        /// <summary>
        /// Try to enter a new state. Automatically exits the previous state.
        /// </summary>
        public static bool TryEnter(State state)
        {
            if (state == State.None)
            {
                DebugLogger.Log(LogCategory.State, "AccessState",
                    "Warning: Use Exit() instead of TryEnter(None)");
                return false;
            }

            if (Current == state) return true;

            if (Current != State.None)
            {
                DebugLogger.Log(LogCategory.State, "AccessState",
                    $"Auto-exiting {Current} for {state}");
                var previousState = Current;
                Current = State.None;
                OnStateChanged?.Invoke(previousState, State.None);
            }

            var oldState = Current;
            Current = state;
            DebugLogger.Log(LogCategory.State, "AccessState", $"Entered {state}");
            OnStateChanged?.Invoke(oldState, state);
            return true;
        }

        /// <summary>
        /// Exit from a state. Only exits if currently in that state.
        /// </summary>
        public static void Exit(State state)
        {
            if (Current != state) return;

            var oldState = Current;
            Current = State.None;
            DebugLogger.Log(LogCategory.State, "AccessState", $"Exited {state}");
            OnStateChanged?.Invoke(oldState, State.None);
        }

        /// <summary>
        /// Force exit from any state. Use for scene/context changes.
        /// </summary>
        public static void ForceReset()
        {
            if (Current != State.None)
            {
                var oldState = Current;
                Current = State.None;
                DebugLogger.Log(LogCategory.State, "AccessState",
                    $"Force reset from {oldState}");
                OnStateChanged?.Invoke(oldState, State.None);
            }

            CurrentContext = Context.Unknown;
        }

        /// <summary>
        /// Check if currently in a specific state.
        /// </summary>
        public static bool IsIn(State state)
        {
            return Current == state;
        }

        /// <summary>
        /// Maps a GameScreen to the appropriate Context.
        /// Call this from GameStateTracker.OnScreenChanged handler.
        /// </summary>
        public static Context ContextFromScreen(GameStateTracker.GameScreen screen)
        {
            return screen switch
            {
                GameStateTracker.GameScreen.Title => Context.Title,
                GameStateTracker.GameScreen.Unknown => Context.Unknown,
                _ => Context.Game
            };
        }
    }
}
