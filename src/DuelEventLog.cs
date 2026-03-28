using System.Collections.Generic;

namespace DuelLinksAccess
{
    /// <summary>
    /// Stores duel event announcements in a browseable list.
    /// Players can scroll through past events to review what happened.
    /// </summary>
    public class DuelEventLog
    {
        #region Fields

        private const int MaxEntries = 200;
        private readonly List<string> _entries = new();
        private int _browseIndex = -1;
        private bool _browsing;

        #endregion

        #region Properties

        /// <summary>Whether the player is currently browsing the log.</summary>
        public bool IsBrowsing => _browsing;

        /// <summary>Number of entries in the log.</summary>
        public int Count => _entries.Count;

        #endregion

        #region Public Methods

        /// <summary>
        /// Adds an event to the log. Oldest entries are removed when full.
        /// </summary>
        public void Add(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            _entries.Add(message);
            if (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        }

        /// <summary>Clears all log entries.</summary>
        public void Clear()
        {
            _entries.Clear();
            _browseIndex = -1;
            _browsing = false;
        }

        /// <summary>
        /// Enters browse mode at the most recent entry.
        /// </summary>
        public void StartBrowsing()
        {
            if (_entries.Count == 0)
            {
                ScreenReader.Say(Loc.Get("duel_log_empty"));
                return;
            }
            _browsing = true;
            _browseIndex = _entries.Count - 1;
            ScreenReader.Say(Loc.Get("duel_log_opened", _entries.Count));
            ReadCurrent();
        }

        /// <summary>Exits browse mode.</summary>
        public void StopBrowsing()
        {
            _browsing = false;
            ScreenReader.Say(Loc.Get("duel_log_closed"));
        }

        /// <summary>Moves to an older entry (toward the beginning).</summary>
        public void BrowseOlder()
        {
            if (!_browsing || _entries.Count == 0) return;
            if (_browseIndex > 0)
                _browseIndex--;
            ReadCurrent();
        }

        /// <summary>Moves to a newer entry (toward the end).</summary>
        public void BrowseNewer()
        {
            if (!_browsing || _entries.Count == 0) return;
            if (_browseIndex < _entries.Count - 1)
                _browseIndex++;
            ReadCurrent();
        }

        /// <summary>Announces the current log entry with position.</summary>
        public void ReadCurrent()
        {
            if (!_browsing || _entries.Count == 0 || _browseIndex < 0) return;

            if (_browseIndex >= _entries.Count)
                _browseIndex = _entries.Count - 1;

            string entry = _entries[_browseIndex];
            int displayIndex = _browseIndex + 1;
            ScreenReader.Say(Loc.Get("duel_log_entry", displayIndex, _entries.Count, entry));
        }

        #endregion
    }
}
