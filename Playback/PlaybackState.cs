using System;

namespace ModernIPTVPlayer.Playback
{
    /// <summary>
    /// Represents the various states of a playback engine.
    /// </summary>
    public enum PlaybackState
    {
        /// <summary>
        /// Player is idle and no media is loaded.
        /// </summary>
        Idle,

        /// <summary>
        /// Player is currently opening/loading media.
        /// </summary>
        Opening,

        /// <summary>
        /// Player is buffering data from the stream.
        /// </summary>
        Buffering,

        /// <summary>
        /// Media is actively playing.
        /// </summary>
        Playing,

        /// <summary>
        /// Playback is paused.
        /// </summary>
        Paused,

        /// <summary>
        /// Media has reached the end.
        /// </summary>
        Ended,

        /// <summary>
        /// Player is seeking to a new position.
        /// </summary>
        Seeking,

        /// <summary>
        /// An error occurred during playback or loading.
        /// </summary>
        Error
    }
}
