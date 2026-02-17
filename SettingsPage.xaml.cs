using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;

namespace ModernIPTVPlayer
{
    public sealed partial class SettingsPage : Page
    {
        // Simple ViewModel wrapper for x:Bind
        public class SettingsViewModel
        {
            public bool IsAutoCacheEnabled
            {
                get => AppSettings.IsAutoCacheEnabled;
                set => AppSettings.IsAutoCacheEnabled = value;
            }

            public bool IsAutoProbeEnabled
            {
                get => AppSettings.IsAutoProbeEnabled;
                set => AppSettings.IsAutoProbeEnabled = value;
            }
        }

        public SettingsViewModel ViewModel { get; } = new();

        public SettingsPage()
        {
            this.InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            // Set ComboBox Selection
            var currentInterval = AppSettings.CacheIntervalMinutes;
            foreach (ComboBoxItem item in IntervalCombo.Items)
            {
                if (int.Parse(item.Tag.ToString()) == currentInterval)
                {
                    IntervalCombo.SelectedItem = item;
                    break;
                }
            }

            // Set AutoProbe Toggle
            AutoProbeToggle.IsOn = AppSettings.IsAutoProbeEnabled;
            // Set AutoCache Toggle (x:Bind handles two-way, but let's ensure initial state)
            AutoCacheToggle.IsOn = AppSettings.IsAutoCacheEnabled;

            // Player Settings
            PrebufferToggle.IsOn = AppSettings.IsPrebufferEnabled;
            PrebufferSlider.Value = AppSettings.PrebufferSeconds;
            BufferSlider.Value = AppSettings.BufferSeconds;
            
            UpdateSliderHeaders();

            // Set Startup Page Selection
            var currentPage = AppSettings.DefaultStartupPage;
            foreach (ComboBoxItem item in StartupPageCombo.Items)
            {
                if (item.Tag.ToString() == currentPage)
                {
                    StartupPageCombo.SelectedItem = item;
                    break;
                }
            }

            // TMDB Settings
            if (!string.IsNullOrEmpty(AppSettings.TmdbApiKey))
            {
                TmdbApiKeyBox.Password = AppSettings.TmdbApiKey;
                TmdbStatusText.Text = "API Anahtarı kayıtlı. TMDB aktif.";
                TmdbStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGreen);
            }

            LoadPlayerSettings();
        }

        private void UpdateSliderHeaders()
        {
            // Optional: Update text to show current value if needed, or binding
        }

        private void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IntervalCombo.SelectedItem is ComboBoxItem item)
            {
                if (int.TryParse(item.Tag.ToString(), out int minutes))
                {
                    AppSettings.CacheIntervalMinutes = minutes;
                    ShowStatus("Güncelleme sıklığı değiştirildi.");
                }
            }
        }

        private void StartupPageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StartupPageCombo.SelectedItem is ComboBoxItem item)
            {
                AppSettings.DefaultStartupPage = item.Tag.ToString();
                ShowStatus("Açılış sayfası ayarlandı: " + item.Content);
            }
        }

        private void AutoProbeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            AppSettings.IsAutoProbeEnabled = AutoProbeToggle.IsOn;
            ShowStatus(AutoProbeToggle.IsOn ? "Otomatik analiz açıldı." : "Otomatik analiz kapatıldı.");
        }

        private void AutoCacheToggle_Toggled(object sender, RoutedEventArgs e)
        {
            ShowStatus(AutoCacheToggle.IsOn ? "Otomatik güncelleme açıldı." : "Otomatik güncelleme kapatıldı.");
        }

        private void PrebufferToggle_Toggled(object sender, RoutedEventArgs e)
        {
            AppSettings.IsPrebufferEnabled = PrebufferToggle.IsOn;
            ShowStatus(PrebufferToggle.IsOn ? "Hızlı başlatma açıldı." : "Hızlı başlatma kapatıldı.");
        }

        private void PrebufferSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (AppSettings.PrebufferSeconds != (int)e.NewValue)
            {
                AppSettings.PrebufferSeconds = (int)e.NewValue;
            }
        }

        private void BufferSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
             if (AppSettings.BufferSeconds != (int)e.NewValue)
            {
                AppSettings.BufferSeconds = (int)e.NewValue;
            }
        }

        private async void BtnClearCache_Click(object sender, RoutedEventArgs e)
        {
            BtnClearCache.IsEnabled = false;
            await Services.ContentCacheService.Instance.ClearCacheAsync();
            ShowStatus("İçerik önbelleği temizlendi.");
            BtnClearCache.IsEnabled = true;
        }

        private async void BtnRefreshNow_Click(object sender, RoutedEventArgs e)
        {
            // Fire event from ContentCacheService? Or just signal via App?
            // Since we haven't implemented a global 'ForceUpdate' pipeline yet, we'll placeholder this.
            // Ideally, we should expose a method in App or MainWindow.
            
            // For now, let's assume we can trigger it via a static event in App or just show a message
            ShowStatus("Güncelleme isteği gönderildi (Henüz Aktif Değil).");
            
            // TODO: Hook this to XtreamService.ForceUpdate() when refactoring pages.
        }

        private async void BtnClearProbe_Click(object sender, RoutedEventArgs e)
        {
            await Services.ProbeCacheService.Instance.ClearCacheAsync();
            ShowStatus("Analiz (Probe) verileri temizlendi.");
        }

        private void ShowStatus(string msg)
        {
            TxtStatus.Text = msg;
            StatusInfoBar.Message = msg;
            StatusInfoBar.IsOpen = true;
            
            // Auto hide after 3s
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, args) => 
            {
                StatusInfoBar.IsOpen = false;
                timer.Stop();
            };
            timer.Start();
        }

        private void TmdbApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            // Optional: Enable/Disable save button
        }

        private void BtnSaveTmdb_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TmdbApiKeyBox.Password))
            {
                AppSettings.TmdbApiKey = TmdbApiKeyBox.Password.Trim();
                AppSettings.IsTmdbEnabled = true;
                TmdbStatusText.Text = "API Anahtarı kaydedildi ve TMDB aktif edildi.";
                TmdbStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGreen);
                ShowStatus("TMDB API Anahtarı kaydedildi.");
            }
            else
            {
                AppSettings.TmdbApiKey = null;
                AppSettings.IsTmdbEnabled = false;
                TmdbStatusText.Text = "API Anahtarı temizlendi. TMDB devre dışı.";
                TmdbStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
                ShowStatus("TMDB API Anahtarı silindi.");
            }
        }
        private bool _isUpdatingProfile = false;

        private void LoadPlayerSettings()
        {
            _isUpdatingProfile = true;
            try
            {
                var settings = AppSettings.PlayerSettings;

                // Set Profile
                SetComboSelection(PlayerProfileCombo, settings.Profile.ToString());
                UpdateProfileDescription(settings.Profile);

                // Set Individual Settings
                SetComboSelection(HwDecCombo, settings.HardwareDecoding.ToString());
                SetComboSelection(VoCombo, settings.VideoOutput.ToString());
                SetComboSelection(ScalerCombo, settings.Scaler.ToString());
                SetComboSelection(ToneMappingCombo, settings.ToneMapping.ToString());
                SetComboSelection(TargetModeCombo, settings.TargetDisplayMode.ToString());
                SetComboSelection(TargetPeakCombo, settings.TargetPeak.ToString());
                SetComboSelection(AudioChannelsCombo, settings.AudioChannels.ToString());

                DebandToggle.IsOn = settings.Deband == Models.DebandMode.Yes;
                ExclusiveToggle.IsOn = settings.ExclusiveAudio == Models.ExclusiveMode.Yes;
                CustomConfigBox.Text = settings.CustomConfig ?? "";

                // Update UI state based on profile (e.g. enable/disable custom config if needed, though plan says always editable)
            }
            finally
            {
                _isUpdatingProfile = false;
            }
        }

        private void SetComboSelection(ComboBox combo, string tagValue)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (item.Tag?.ToString() == tagValue)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void PlayerProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingProfile) return;

            if (PlayerProfileCombo.SelectedItem is ComboBoxItem item && 
                Enum.TryParse<Models.PlayerProfile>(item.Tag.ToString(), out var profile))
            {
                UpdateProfileDescription(profile);

                if (profile != Models.PlayerProfile.Custom)
                {
                    // Apply Preset Defaults
                    var defaults = Models.PlayerSettings.GetDefault(profile);
                    
                    // Keep Custom Config even when switching profiles? 
                    // Usually presets imply standard settings. Let's keep custom config as is but apply preset values for others.
                    defaults.CustomConfig = CustomConfigBox.Text; 

                    SavePlayerSettings(defaults);
                    
                    // Update UI to reflect new defaults
                    // We must reload UI from these new settings
                     _isUpdatingProfile = true;
                    try
                    {
                        SetComboSelection(HwDecCombo, defaults.HardwareDecoding.ToString());
                        SetComboSelection(VoCombo, defaults.VideoOutput.ToString());
                        SetComboSelection(ScalerCombo, defaults.Scaler.ToString());
                        SetComboSelection(ToneMappingCombo, defaults.ToneMapping.ToString());
                        SetComboSelection(TargetModeCombo, defaults.TargetDisplayMode.ToString());
                        SetComboSelection(TargetPeakCombo, defaults.TargetPeak.ToString());
                        SetComboSelection(AudioChannelsCombo, defaults.AudioChannels.ToString());
                        DebandToggle.IsOn = defaults.Deband == Models.DebandMode.Yes;
                        ExclusiveToggle.IsOn = defaults.ExclusiveAudio == Models.ExclusiveMode.Yes;
                    }
                    finally
                    {
                        _isUpdatingProfile = false;
                    }
                }
                else
                {
                    // Just update the storage that we are now on Custom profile
                    var current = GetCurrentSettingsFromUI();
                    current.Profile = Models.PlayerProfile.Custom;
                    SavePlayerSettings(current);
                }
            }
        }

        private void PlayerSetting_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingProfile) return;
            OnPlayerSettingChanged();
        }

        private void PlayerSetting_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingProfile) return;
            OnPlayerSettingChanged();
        }

        private void CustomConfigBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingProfile) return;
            // Delay saving or save on lost focus? For simplicity, save on change but mark as Custom immediately.
            // TextChanged fires often, maybe don't save to disk every char, but update internal state?
            // For now, let's just mark as custom. Actual save happens on Nav/App close or explicit action?
            // We usually save immediately in this app.
            OnPlayerSettingChanged();
        }

        private void OnPlayerSettingChanged()
        {
            // If we are on a preset, and user changes a setting, switch to Custom.
            if (PlayerProfileCombo.SelectedItem is ComboBoxItem item && 
                item.Tag.ToString() != "Custom")
            {
                 _isUpdatingProfile = true;
                SetComboSelection(PlayerProfileCombo, "Custom");
                UpdateProfileDescription(Models.PlayerProfile.Custom);
                 _isUpdatingProfile = false;
            }

            // Save current UI state
            var settings = GetCurrentSettingsFromUI();
            SavePlayerSettings(settings);
        }

        private Models.PlayerSettings GetCurrentSettingsFromUI()
        {
            var settings = new Models.PlayerSettings();

            if (PlayerProfileCombo.SelectedItem is ComboBoxItem pItem &&
                Enum.TryParse(pItem.Tag.ToString(), out Models.PlayerProfile p))
            {
                settings.Profile = p;
            }

            if (HwDecCombo.SelectedItem is ComboBoxItem hwItem &&
                Enum.TryParse(hwItem.Tag.ToString(), out Models.HardwareDecoding hw))
            {
                settings.HardwareDecoding = hw;
            }

            if (VoCombo.SelectedItem is ComboBoxItem voItem &&
                Enum.TryParse(voItem.Tag.ToString(), out Models.VideoOutput vo))
            {
                settings.VideoOutput = vo;
            }

            if (ScalerCombo.SelectedItem is ComboBoxItem scItem &&
                Enum.TryParse(scItem.Tag.ToString(), out Models.Scaler sc))
            {
                settings.Scaler = sc;
            }

            if (ToneMappingCombo.SelectedItem is ComboBoxItem tmItem &&
                Enum.TryParse(tmItem.Tag.ToString(), out Models.ToneMapping tm))
            {
                settings.ToneMapping = tm;
            }
            
             if (TargetModeCombo.SelectedItem is ComboBoxItem tmcItem &&
                Enum.TryParse(tmcItem.Tag.ToString(), out Models.TargetDisplayMode tmc))
            {
                settings.TargetDisplayMode = tmc;
            }

            if (TargetPeakCombo.SelectedItem is ComboBoxItem tpItem &&
                Enum.TryParse(tpItem.Tag.ToString(), out Models.TargetPeak tp))
            {
                settings.TargetPeak = tp;
            }

            if (AudioChannelsCombo.SelectedItem is ComboBoxItem acItem &&
                Enum.TryParse(acItem.Tag.ToString(), out Models.AudioChannels ac))
            {
                settings.AudioChannels = ac;
            }

            settings.Deband = DebandToggle.IsOn ? Models.DebandMode.Yes : Models.DebandMode.No;
            settings.ExclusiveAudio = ExclusiveToggle.IsOn ? Models.ExclusiveMode.Yes : Models.ExclusiveMode.No;
            settings.CustomConfig = CustomConfigBox.Text;

            return settings;
        }

        private void SavePlayerSettings(Models.PlayerSettings settings)
        {
            AppSettings.PlayerSettings = settings;
        }

        private void UpdateProfileDescription(Models.PlayerProfile profile)
        {
            switch (profile)
            {
                case Models.PlayerProfile.Performance:
                    ProfileDescriptionText.Text = "Hız öncelikli. Pil ömrünü uzatır, işlemciyi yormaz.";
                    break;
                case Models.PlayerProfile.Balanced:
                    ProfileDescriptionText.Text = "Çoğu kullanıcı için önerilen, dengeli ayarlar.";
                    break;
                case Models.PlayerProfile.HighQuality:
                    ProfileDescriptionText.Text = "En yüksek görüntü kalitesi. Güçlü ekran kartı önerilir.";
                    break;
                case Models.PlayerProfile.Custom:
                    ProfileDescriptionText.Text = "Özelleştirilmiş ayarlar.";
                    break;
            }
        }
    }
}
