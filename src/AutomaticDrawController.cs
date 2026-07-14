using System;

namespace DuelLinksAccess
{
    /// <summary>
    /// Drives the automatic draw at the start of the local player's turn.
    /// Gesture-first: the draw prompt is answered with the same swipe a
    /// sighted player performs, which runs the game's draw presentation
    /// exactly once. The direct engine draw command is only a timed
    /// fallback for when the prompt never becomes touchable or the swipe
    /// does not advance the engine — dispatching it alongside the gesture
    /// replays the draw cutin and voice line (2026-07-14 regression).
    /// </summary>
    public sealed class AutomaticDrawController
    {
        /// <summary>
        /// Seconds to wait for the draw prompt to become touchable before
        /// falling back to the engine command. The prompt normally appears
        /// well under a second after Draw Phase starts.
        /// </summary>
        public const float GestureWaitSeconds = 3f;

        /// <summary>
        /// Seconds after a completed swipe to wait for the engine to leave
        /// Draw Phase before falling back to the engine command.
        /// </summary>
        public const float FallbackAfterGestureSeconds = 2.5f;

        private bool _armed;
        private bool _gesturePending;
        private bool _gestureDone;
        private bool _detailPending;
        private bool _fallbackUsed;
        private float _phaseStart;
        private float _gestureTime;

        /// <summary>
        /// Per-frame driver. Arms the gesture when an eligible local Draw
        /// Phase starts, and dispatches the engine-command fallback when the
        /// gesture path has stalled. Returns true when the fallback fired.
        /// </summary>
        public bool Update(
            bool duelActive,
            bool localTurn,
            bool drawPhase,
            float now,
            Action dispatchFallback)
        {
            if (!duelActive || !localTurn)
            {
                Reset();
                return false;
            }

            if (!drawPhase)
            {
                // Draw already resolved — a late swipe on the lingering
                // prompt would replay the presentation. The detail prompt
                // can outlive the input state, so it stays pending.
                _armed = false;
                _gesturePending = false;
                return false;
            }

            if (!_armed)
            {
                _armed = true;
                _phaseStart = now;
                _gesturePending = true;
                _gestureDone = false;
                _detailPending = true;
                _fallbackUsed = false;
                return false;
            }

            if (_fallbackUsed) return false;

            bool promptStalled = _gesturePending
                && now - _phaseStart >= GestureWaitSeconds;
            bool engineStalled = _gestureDone
                && now - _gestureTime >= FallbackAfterGestureSeconds;
            if (!promptStalled && !engineStalled) return false;

            return DispatchCommand(dispatchFallback);
        }

        /// <summary>
        /// Explicit retry (Space) — dispatches the engine command right away.
        /// </summary>
        public bool Retry(
            bool duelActive,
            bool localTurn,
            bool drawPhase,
            Action dispatch)
        {
            if (!duelActive || !localTurn || !drawPhase) return false;

            return DispatchCommand(dispatch);
        }

        /// <summary>
        /// Performs the swipe once the draw prompt is ready. `now` stamps
        /// the gesture so the engine fallback can detect a stalled swipe.
        /// </summary>
        public bool CompleteGesture(bool operationReady, float now, Action complete)
        {
            if (!_gesturePending || !operationReady) return false;
            if (complete == null) throw new ArgumentNullException(nameof(complete));

            _gesturePending = false;
            try
            {
                complete();
                _gestureDone = true;
                _gestureTime = now;
                return true;
            }
            catch
            {
                _gesturePending = true;
                throw;
            }
        }

        /// <summary>
        /// Advances the lingering card-detail prompt after the draw.
        /// </summary>
        public bool CompleteDetail(bool operationReady, Action complete)
        {
            if (!_detailPending || !operationReady) return false;
            if (complete == null) throw new ArgumentNullException(nameof(complete));

            _detailPending = false;
            try
            {
                complete();
                return true;
            }
            catch
            {
                _detailPending = true;
                throw;
            }
        }

        public void Reset()
        {
            _armed = false;
            _gesturePending = false;
            _gestureDone = false;
            _detailPending = false;
            _fallbackUsed = false;
        }

        private bool DispatchCommand(Action dispatch)
        {
            if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));

            // The engine draw replaces the gesture; swiping afterwards
            // would replay the presentation.
            bool hadGesturePending = _gesturePending;
            _gesturePending = false;
            _detailPending = true;
            _fallbackUsed = true;
            try
            {
                dispatch();
                return true;
            }
            catch
            {
                _gesturePending = hadGesturePending;
                _fallbackUsed = false;
                throw;
            }
        }
    }
}
