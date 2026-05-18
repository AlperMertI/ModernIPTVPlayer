using System;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ModernIPTVPlayer.Playback
{
    /// <summary>
    /// Implementation of <see cref="IPlaybackEngine"/> using the native Windows MediaPlayer.
    /// </summary>
    public class NativePlaybackEngine : IPlaybackEngine
    {
        private readonly MediaPlayer _player;
        private PlaybackState _state = PlaybackState.Idle;
        private bool _isDisposed;

        public PlaybackState State
        {
            get => _state;
            private set
            {
                if (_state != value)
                {
                    _state = value;
                    StateChanged?.Invoke(this, _state);
                }
            }
        }

        public TimeSpan Position => _player.PlaybackSession.Position;

        public TimeSpan Duration => _player.PlaybackSession.NaturalDuration;

        public double Volume
        {
            get => _player.Volume * 100.0;
            set => _player.Volume = value / 100.0;
        }

        public double Speed
        {
            get => _player.PlaybackSession.PlaybackRate;
            set => _player.PlaybackSession.PlaybackRate = value;
        }

        public bool IsMuted
        {
            get => _player.IsMuted;
            set => _player.IsMuted = value;
        }

        public bool IsSeekable => _player.PlaybackSession.CanSeek;
        public string HardwareDecoding => "Native";
        public string BufferDuration => "-";
        public string DroppedFrames => "0";
        public string Resolution => "-";
        public string Fps => "-";
        public string VideoCodec => "-";
        public string AudioCodec => "-";
        public bool IsHdr => false;
        public string SdrWhite => "-";
        public string DisplayPeak => "-";
        public string Bitrate => "-";
        public string Colorspace => "-";
        public string Primaries => "-";
        public string AudioChannels => "-";
        public string Renderer => "DirectComposition (Native)";
        public string AvSync => "0.0 ms";
        public string DownloadSpeed => "-";
        public string TargetSdrWhite => "-";
        public string AppliedPeak => "-";
        public bool HdrAvailable => false;

        public event EventHandler<PlaybackState>? StateChanged;
        public event EventHandler<TimeSpan>? PositionChanged;
        public event EventHandler<TimeSpan>? DurationChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler? MetadataChanged;
        public event EventHandler<bool>? BufferingChanged;
        public event EventHandler<bool>? SeekingChanged;

        public NativePlaybackEngine(MediaPlayer player)
        {
            _player = player ?? throw new ArgumentNullException(nameof(player));
            
            _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
            _player.PlaybackSession.PositionChanged += OnPositionChanged;
            _player.PlaybackSession.NaturalDurationChanged += OnDurationChanged;
            _player.MediaFailed += OnMediaFailed;
            _player.MediaOpened += OnMediaOpened;
        }

        private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            switch (sender.PlaybackState)
            {
                case MediaPlaybackState.None:
                    State = PlaybackState.Idle;
                    break;
                case MediaPlaybackState.Opening:
                    State = PlaybackState.Opening;
                    break;
                case MediaPlaybackState.Buffering:
                    State = PlaybackState.Buffering;
                    BufferingChanged?.Invoke(this, true);
                    break;
                case MediaPlaybackState.Playing:
                    State = PlaybackState.Playing;
                    BufferingChanged?.Invoke(this, false);
                    break;
                case MediaPlaybackState.Paused:
                    State = PlaybackState.Paused;
                    break;
            }
        }

        private void OnPositionChanged(MediaPlaybackSession sender, object args)
        {
            PositionChanged?.Invoke(this, sender.Position);
        }

        private void OnDurationChanged(MediaPlaybackSession sender, object args)
        {
            DurationChanged?.Invoke(this, sender.NaturalDuration);
        }

        private void OnMediaOpened(MediaPlayer sender, object args)
        {
            MetadataChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            State = PlaybackState.Error;
            ErrorOccurred?.Invoke(this, args.ErrorMessage ?? "Native playback failed");
        }

        public Task LoadAsync(string url, double startPosition = 0)
        {
            State = PlaybackState.Opening;
            _player.Source = MediaSource.CreateFromUri(new Uri(url));
            if (startPosition > 0)
            {
                _player.PlaybackSession.Position = TimeSpan.FromSeconds(startPosition);
            }
            _player.Play();
            return Task.CompletedTask;
        }

        public void Play() => _player.Play();

        public void Pause() => _player.Pause();

        public void Stop()
        {
            _player.Pause();
            _player.Source = null;
            State = PlaybackState.Idle;
        }

        public void Seek(TimeSpan position)
        {
            _player.PlaybackSession.Position = position;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try { _player.MediaFailed -= OnMediaFailed; } catch { }
            try { _player.MediaOpened -= OnMediaOpened; } catch { }
        }
    }
}
