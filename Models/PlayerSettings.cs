using System;

namespace ModernIPTVPlayer.Models
{
    public enum PlayerProfile
    {
        Performance,
        Balanced,
        HighQuality,
        Custom
    }

    public enum HardwareDecoding
    {
        AutoSafe,
        AutoCopy,
        No
    }

    public enum VideoOutput
    {
        GpuNext,
        Gpu
    }

    public enum Scaler
    {
        Bilinear,
        Spline36,
        EwaLanczos
    }

    public enum DebandMode
    {
        No,
        Yes
    }

    public enum ToneMapping
    {
        Auto,
        Spline,
        Bt2446a,
        St2094_40,
        Clip
    }

    public enum TargetPeak
    {
        Auto,
        Sdr100,
        Sdr203,
        Hdr400,
        Hdr1000
    }

    public enum TargetDisplayMode
    {
        Auto,
        SdrForce,
        HdrPassthrough
    }

    public enum AudioChannels
    {
        AutoSafe,
        Stereo,
        Surround51,
        Surround71
    }

    public enum ExclusiveMode
    {
        No,
        Yes
    }

    public class PlayerSettings
    {
        public PlayerProfile Profile { get; set; } = PlayerProfile.Balanced;
        public HardwareDecoding HardwareDecoding { get; set; } = HardwareDecoding.AutoSafe;
        public VideoOutput VideoOutput { get; set; } = VideoOutput.GpuNext;
        public Scaler Scaler { get; set; } = Scaler.Spline36;
        public DebandMode Deband { get; set; } = DebandMode.No;
        public ToneMapping ToneMapping { get; set; } = ToneMapping.Spline;
        public TargetPeak TargetPeak { get; set; } = TargetPeak.Auto;
        public TargetDisplayMode TargetDisplayMode { get; set; } = TargetDisplayMode.Auto;
        public AudioChannels AudioChannels { get; set; } = AudioChannels.AutoSafe;
        public ExclusiveMode ExclusiveAudio { get; set; } = ExclusiveMode.No;
        public string CustomConfig { get; set; } = "";

        public static PlayerSettings GetDefault(PlayerProfile profile)
        {
            var settings = new PlayerSettings { Profile = profile };
            switch (profile)
            {
                case PlayerProfile.Performance:
                    settings.HardwareDecoding = HardwareDecoding.AutoSafe;
                    settings.VideoOutput = VideoOutput.GpuNext;
                    settings.Scaler = Scaler.Bilinear;
                    settings.Deband = DebandMode.No;
                    settings.ToneMapping = ToneMapping.Auto;
                    settings.TargetPeak = TargetPeak.Auto;
                    settings.TargetDisplayMode = TargetDisplayMode.Auto;
                    break;

                case PlayerProfile.Balanced:
                    settings.HardwareDecoding = HardwareDecoding.AutoSafe;
                    settings.VideoOutput = VideoOutput.GpuNext;
                    settings.Scaler = Scaler.Spline36;
                    settings.Deband = DebandMode.No;
                    settings.ToneMapping = ToneMapping.Spline;
                    settings.TargetPeak = TargetPeak.Auto;
                    settings.TargetDisplayMode = TargetDisplayMode.Auto;
                    break;

                case PlayerProfile.HighQuality:
                    settings.HardwareDecoding = HardwareDecoding.AutoCopy;
                    settings.VideoOutput = VideoOutput.GpuNext;
                    settings.Scaler = Scaler.EwaLanczos;
                    settings.Deband = DebandMode.Yes;
                    settings.ToneMapping = ToneMapping.St2094_40;
                    settings.TargetPeak = TargetPeak.Auto;
                    settings.TargetDisplayMode = TargetDisplayMode.Auto;
                    break;
                    
                // Custom keeps whatever was last set, technically doesn't change anything here
            }
            return settings;
        }
    }
}
