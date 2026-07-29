using MediaBrowser.Model.Plugins;

namespace JellyfinGraveyardAnalytics.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // --- Start of Tracearr Integration ---
        public bool EnableTracearr { get; set; }
        public string TracearrUrl { get; set; }
        public string TracearrApiKey { get; set; }
        // --- End of Tracearr Integration ---

        // Backing fields are clamped on write: the config XML is hand-editable and the
        // admin UI posts arbitrary numbers, so an out-of-range value must not reach a query.
        private int _minPlayDurationSeconds = DefaultMinPlayDurationSeconds;
        private int _morgueGraceDays = DefaultMorgueGraceDays;
        private int _guestbookRowLimit = DefaultGuestbookRowLimit;

        public const int DefaultMinPlayDurationSeconds = 120;
        public const int DefaultMorgueGraceDays = 180;
        public const int DefaultGuestbookRowLimit = 5000;

        /// <summary>
        /// Seconds of playback before a session counts as a play. Nobody accidentally
        /// streams two minutes, and every aggregate applies this same floor.
        /// Effective range is 1–3600: a config written before this key existed
        /// deserializes as 0, and honouring that as "no floor" would silently restore the
        /// unfiltered aggregates this setting exists to fix. Use 1 for effectively no floor.
        /// </summary>
        public int MinPlayDurationSeconds
        {
            get => _minPlayDurationSeconds;
            set => _minPlayDurationSeconds = Clamp(value, 0, 3600, DefaultMinPlayDurationSeconds);
        }

        /// <summary>
        /// How long an item must have been in the library before zero plays means neglect
        /// rather than "added last week". Clamped at read time to the history actually
        /// available, so a young Playback Reporting database cannot flood the Morgue.
        /// </summary>
        public int MorgueGraceDays
        {
            get => _morgueGraceDays;
            set => _morgueGraceDays = Clamp(value, 30, 365, DefaultMorgueGraceDays);
        }

        /// <summary>
        /// Row ceiling for the Guestbook query. Twelve weeks on a busy server is tens of
        /// thousands of sessions in one JSON blob.
        /// </summary>
        public int GuestbookRowLimit
        {
            get => _guestbookRowLimit;
            set => _guestbookRowLimit = Clamp(value, 100, 50000, DefaultGuestbookRowLimit);
        }

        public PluginConfiguration()
        {
            EnableTracearr = false;
            TracearrUrl = string.Empty;
            TracearrApiKey = string.Empty;
        }

        /// <summary>
        /// Zero and negative values are treated as "unset" and fall back to the default,
        /// because a deserialized config missing an element arrives as 0.
        /// </summary>
        private static int Clamp(int value, int min, int max, int fallback)
        {
            if (value <= 0)
            {
                return fallback;
            }

            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }
    }
}
