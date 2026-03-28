using System.Collections.Generic;
using UnityEngine;

namespace DuelLinksAccess
{
    /// <summary>
    /// Keyboard input layer with key consumption and key repeat.
    /// Key consumption prevents the same keypress from being processed
    /// by multiple handlers in a single frame.
    /// Key repeat allows held navigation keys (arrows) to auto-repeat.
    ///
    /// Call InputManager.Update() FIRST in Main.OnUpdate() each frame.
    /// </summary>
    public static class InputManager
    {
        #region Fields

        // Consumed keys this frame — prevents double-processing
        private static readonly HashSet<KeyCode> _consumedKeys = new();

        // Key repeat tracking
        private static readonly Dictionary<KeyCode, float> _heldSince = new();
        private static readonly Dictionary<KeyCode, float> _lastRepeat = new();

        // Keys that support auto-repeat when held
        private static readonly HashSet<KeyCode> _repeatableKeys = new()
        {
            KeyCode.UpArrow,
            KeyCode.DownArrow,
            KeyCode.LeftArrow,
            KeyCode.RightArrow,
            KeyCode.Tab,
        };

        /// <summary>
        /// Delay before key repeat starts (seconds).
        /// </summary>
        private const float RepeatDelay = 0.4f;

        /// <summary>
        /// Interval between repeats once started (seconds).
        /// </summary>
        private const float RepeatRate = 0.08f;

        #endregion

        #region Public Methods

        /// <summary>
        /// Must be called at the start of each frame before any input processing.
        /// Clears consumed keys from previous frame and updates repeat state.
        /// </summary>
        public static void Update()
        {
            _consumedKeys.Clear();
            UpdateKeyRepeat();
        }

        /// <summary>
        /// Returns true if the key was pressed this frame and hasn't been consumed.
        /// Does NOT consume the key — call ConsumeKey() explicitly after handling.
        /// </summary>
        public static bool IsKeyDown(KeyCode key)
        {
            if (_consumedKeys.Contains(key)) return false;
            return Input.GetKeyDown(key);
        }

        /// <summary>
        /// Returns true if the key was pressed this frame OR is auto-repeating,
        /// and hasn't been consumed. For navigation keys (arrows, Tab).
        /// Does NOT consume the key — call ConsumeKey() explicitly after handling.
        /// </summary>
        public static bool IsKeyDownOrRepeat(KeyCode key)
        {
            if (_consumedKeys.Contains(key)) return false;
            if (Input.GetKeyDown(key)) return true;
            return IsRepeating(key);
        }

        /// <summary>
        /// Returns true if the key is currently held down and hasn't been consumed.
        /// </summary>
        public static bool IsKeyHeld(KeyCode key)
        {
            if (_consumedKeys.Contains(key)) return false;
            return Input.GetKey(key);
        }

        /// <summary>
        /// Marks a key as consumed for this frame. No other handler will see it.
        /// </summary>
        public static void ConsumeKey(KeyCode key)
        {
            _consumedKeys.Add(key);
        }

        /// <summary>
        /// Checks key down and consumes it in one call. Returns true if key was available.
        /// </summary>
        public static bool TryConsumeKeyDown(KeyCode key)
        {
            if (!IsKeyDown(key)) return false;
            ConsumeKey(key);
            return true;
        }

        /// <summary>
        /// Checks key down/repeat and consumes it in one call. For navigation keys.
        /// </summary>
        public static bool TryConsumeKeyDownOrRepeat(KeyCode key)
        {
            if (!IsKeyDownOrRepeat(key)) return false;
            ConsumeKey(key);
            return true;
        }

        #endregion

        #region Key Repeat

        private static void UpdateKeyRepeat()
        {
            float time = Time.unscaledTime;

            foreach (var key in _repeatableKeys)
            {
                if (Input.GetKey(key))
                {
                    if (!_heldSince.ContainsKey(key))
                    {
                        _heldSince[key] = time;
                        _lastRepeat[key] = time;
                    }
                }
                else
                {
                    _heldSince.Remove(key);
                    _lastRepeat.Remove(key);
                }
            }
        }

        private static bool IsRepeating(KeyCode key)
        {
            if (!_heldSince.TryGetValue(key, out float heldSince)) return false;

            float time = Time.unscaledTime;
            float holdDuration = time - heldSince;

            if (holdDuration < RepeatDelay) return false;

            if (!_lastRepeat.TryGetValue(key, out float lastRepeat)) return false;

            if (time - lastRepeat >= RepeatRate)
            {
                _lastRepeat[key] = time;
                return true;
            }

            return false;
        }

        #endregion
    }
}
