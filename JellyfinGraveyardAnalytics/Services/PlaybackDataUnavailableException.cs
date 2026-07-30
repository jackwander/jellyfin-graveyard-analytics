using System;

namespace JellyfinGraveyardAnalytics.Services
{
    /// <summary>
    /// Raised when the playback data source is missing or unusable, so the caller can be
    /// told something actionable instead of the generic failure. Its message is logged, not
    /// echoed — the client always receives a literal from the controller.
    /// </summary>
    /// <remarks>
    /// Declared here rather than beside the controller that catches it: the services that
    /// throw it must not have to reference the API layer to do so.
    /// </remarks>
    public sealed class PlaybackDataUnavailableException : Exception
    {
        public PlaybackDataUnavailableException(string message)
            : base(message)
        {
        }
    }
}
