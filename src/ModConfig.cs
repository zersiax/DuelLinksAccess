using MelonLoader;
using UnityEngine;

namespace DuelLinksAccess
{
    /// <summary>
    /// Mod configuration using MelonPreferences.
    /// Settings are automatically saved to UserData/DuelLinksAccess.cfg.
    /// Toggle settings menu with Ctrl+F11. Navigate with arrows, change with Left/Right.
    /// </summary>
    public static class ModConfig
    {
        #region Preferences

        private static MelonPreferences_Category _category;

        private static MelonPreferences_Entry<int> _verbosity;
        private static MelonPreferences_Entry<bool> _announceEmptyStates;

        #endregion

        #region Public Accessors

        /// <summary>Announcement verbosity: 0=minimal, 1=normal, 2=verbose.</summary>
        public static int Verbosity => _verbosity.Value;

        /// <summary>Whether to announce empty states ("No cards", "Empty deck slot").</summary>
        public static bool AnnounceEmptyStates => _announceEmptyStates.Value;

        #endregion

        #region Settings Menu State

        private static bool _menuOpen = false;
        private static int _currentSettingIndex = 0;

        private static readonly string[] _settingNames = new[]
        {
            "Verbosity",
            "Announce empty states",
        };

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes mod preferences. Call once in OnInitializeMelon().
        /// </summary>
        public static void Initialize()
        {
            _category = MelonPreferences.CreateCategory("DuelLinksAccess",
                "DuelLinksAccess Accessibility Settings");

            _verbosity = _category.CreateEntry("Verbosity", 1,
                description: "Announcement detail level: 0=minimal, 1=normal, 2=verbose");

            _announceEmptyStates = _category.CreateEntry("AnnounceEmptyStates", true,
                description: "Announce when lists or inventories are empty");
        }

        #endregion

        #region Settings Menu

        /// <summary>
        /// Toggles the in-game settings menu.
        /// </summary>
        public static void ToggleMenu()
        {
            _menuOpen = !_menuOpen;

            if (_menuOpen)
            {
                _currentSettingIndex = 0;
                ScreenReader.Say(Loc.Get("settings_opened"));
                AnnounceCurrentSetting();
            }
            else
            {
                MelonPreferences.Save();
                ScreenReader.Say(Loc.Get("settings_closed"));
            }
        }

        /// <summary>
        /// Whether the settings menu is currently open.
        /// </summary>
        public static bool IsMenuOpen => _menuOpen;

        /// <summary>
        /// Processes input for the settings menu.
        /// </summary>
        public static void Update()
        {
            if (!_menuOpen) return;

            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                _currentSettingIndex--;
                if (_currentSettingIndex < 0)
                    _currentSettingIndex = _settingNames.Length - 1;
                AnnounceCurrentSetting();
            }
            else if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                _currentSettingIndex++;
                if (_currentSettingIndex >= _settingNames.Length)
                    _currentSettingIndex = 0;
                AnnounceCurrentSetting();
            }
            else if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                ChangeCurrentSetting(-1);
            }
            else if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                ChangeCurrentSetting(1);
            }
            else if (Input.GetKeyDown(KeyCode.Escape))
            {
                ToggleMenu();
            }
        }

        private static void AnnounceCurrentSetting()
        {
            string name = _settingNames[_currentSettingIndex];
            string value = GetCurrentSettingValue();
            int pos = _currentSettingIndex + 1;
            int total = _settingNames.Length;

            ScreenReader.Say(Loc.Get("settings_item", pos, total, name, value));
        }

        private static string GetCurrentSettingValue()
        {
            switch (_currentSettingIndex)
            {
                case 0:
                    return _verbosity.Value switch
                    {
                        0 => "Minimal",
                        1 => "Normal",
                        2 => "Verbose",
                        _ => _verbosity.Value.ToString()
                    };

                case 1:
                    return _announceEmptyStates.Value ? "On" : "Off";

                default:
                    return "Unknown";
            }
        }

        private static void ChangeCurrentSetting(int direction)
        {
            switch (_currentSettingIndex)
            {
                case 0:
                    int newVal = _verbosity.Value + direction;
                    if (newVal < 0) newVal = 2;
                    if (newVal > 2) newVal = 0;
                    _verbosity.Value = newVal;
                    break;

                case 1:
                    _announceEmptyStates.Value = !_announceEmptyStates.Value;
                    break;
            }

            string name = _settingNames[_currentSettingIndex];
            string value = GetCurrentSettingValue();
            ScreenReader.Say($"{name}: {value}");
        }

        #endregion
    }
}
