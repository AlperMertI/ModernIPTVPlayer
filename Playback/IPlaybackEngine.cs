using System;
using System.Threading.Tasks;

namespace ModernIPTVPlayer.Playback
{
    /// <summary>
    /// Defines the core functionality for a playback engine (MPV or Native).
    /// </summary>
    public interface IPlaybackEngine : IDisposable
    {
        /// <summary>
        /// Current playback state.
        /// </summary>
        PlaybackState State { get; }

        /// <summary>
        /// Current playback position.
        /// </summary>
        TimeSpan Position { get; }

        /// <summary>
        /// Total duration of the media.
        /// </summary>
        TimeSpan Duration { get; }

        /// <summary>
        /// Current volume (0.0 to 100.0).
        /// </summary>
        double Volume { get; set; }

        /// <summary>
        /// Current playback speed (e.g., 1.0).
        /// </summary>
        double Speed { get; set; }

        /// <summary>
        /// Whether the audio is muted.
        /// </summary>
        bool IsMuted { get; set; }

        /// <summary>
        /// Whether the media supports seeking.
        /// </summary>
        bool IsSeekable { get; }

        /// <summary>
        /// Hardware decoding status (e.g., "d3d11va", "no").
        /// </summary>
        string HardwareDecoding { get; }

        /// <summary>
        /// Current buffer duration in seconds.
        /// </summary>
        string BufferDuration { get; }

        /// <summary>
        /// Number of dropped frames.
        /// </summary>
        string DroppedFrames { get; }

        /// <summary>
        /// Video resolution (e.g., "1920x1080").
        /// </summary>
        string Resolution { get; }

        /// <summary>
        /// Frames per second.
        /// </summary>
        string Fps { get; }

        /// <summary>
        /// Video codec name.
        /// </summary>
        string VideoCodec { get; }

        /// <summary>
        /// Audio codec name.
        /// </summary>
        string AudioCodec { get; }

        /// <summary>
        /// Whether HDR is active.
        /// </summary>
        bool IsHdr { get; }

        /// <summary>
        /// SDR White level in nits.
        /// </summary>
        string SdrWhite { get; }

        /// <summary>
        /// Display peak luminance in nits.
        /// </summary>
        string DisplayPeak { get; }

        /// <summary>
        /// Current video bitrate.
        /// </summary>
        string Bitrate { get; }
        string Renderer { get; }
        string AvSync { get; }

        /// <summary>
        /// Video colorspace information (matrix/levels).
        /// </summary>
        string Colorspace { get; }

        /// <summary>
        /// HDR Primaries and Transfer characteristics.
        /// </summary>
        string Primaries { get; }

        /// <summary>
        /// Audio channels information.
        /// </summary>
        string AudioChannels { get; }
        string DownloadSpeed { get; }
        string TargetSdrWhite { get; }
        string AppliedPeak { get; }
        bool HdrAvailable { get; }

        /// <summary>
        /// Occurs when the playback state changes.
        /// </summary>
        event EventHandler<PlaybackState> StateChanged;

        /// <summary>
        /// Occurs when the playback position changes.
        /// </summary>
        event EventHandler<TimeSpan> PositionChanged;

        /// <summary>
        /// Occurs when the media duration is determined or changed.
        /// </summary>
        event EventHandler<TimeSpan> DurationChanged;

        /// <summary>
        /// Occurs when the buffering state changes.
        /// </summary>
        event EventHandler<bool>? BufferingChanged;

        /// <summary>
        /// Occurs when the seeking state changes.
        /// </summary>
        event EventHandler<bool>? SeekingChanged;

        /// <summary>
        /// Occurs when an error occurs during playback.
        /// </summary>
        event EventHandler<string> ErrorOccurred;

        /// <summary>
        /// Occurs when media metadata is updated.
        /// </summary>
        event EventHandler MetadataChanged;

        /// <summary>
        /// Loads and starts playback of a URL.
        /// </summary>
        /// <param name="url">The stream URL.</param>
        /// <param name="startPosition">Optional start position in seconds.</param>
        Task LoadAsync(string url, double startPosition = 0);

        /// <summary>
        /// Resumes playback.
        /// </summary>
        void Play();

        /// <summary>
        /// Pauses playback.
        /// </summary>
        void Pause();

        /// <summary>
        /// Stops playback and releases media resources.
        /// </summary>
        void Stop();

        /// <summary>
        /// Seeks to a specific position.
        /// </summary>
        void Seek(TimeSpan position);
    }
}
