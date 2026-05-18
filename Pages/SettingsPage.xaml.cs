using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;

namespace ModernIPTVPlayer
{
    [Microsoft.UI.Xaml.Data.Bindable]
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
            PrebufferValueText.Text = AppSettings.PrebufferSeconds.ToString();
            BufferSlider.Value = AppSettings.BufferSeconds;
            BufferSecondsValueText.Text = AppSettings.BufferSeconds.ToString();

            MaxBufferSlider.Value = AppSettings.MaxBufferMegabytes;
            MaxBufferValueText.Text = AppSettings.MaxBufferMegabytes.ToString();

            SeekBackSlider.Value = AppSettings.SeekBackwardSeconds;
            SeekBackValueText.Text = AppSettings.SeekBackwardSeconds.ToString();

            SeekForwardSlider.Value = AppSettings.SeekForwardSeconds;
            SeekForwardValueText.Text = AppSettings.SeekForwardSeconds.ToString();
            
            UpdateSliderHeaders();

            // Set Worker Count
            WorkerCountSlider.Value = AppSettings.ProbingWorkerCount;
            WorkerCountValueText.Text = AppSettings.ProbingWorkerCount.ToString();

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

            if (!string.IsNullOrEmpty(AppSettings.TmdbApiKey))
            {
                TmdbApiKeyBox.Password = AppSettings.TmdbApiKey;
                TmdbStatusText.Text = "API Anahtarı kayıtlı. TMDB aktif.";
                TmdbStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGreen);
            }

            // Set TMDB Language
            var currentTmdbLang = AppSettings.TmdbLanguage;
            foreach (ComboBoxItem item in TmdbLanguageCombo.Items)
            {
                if (item.Tag?.ToString() == currentTmdbLang)
                {
                    TmdbLanguageCombo.SelectedItem = item;
                    break;
                }
            }

            // Set Trailer Quality
            var currentTrailerQ = AppSettings.TrailerQuality;
            foreach (ComboBoxItem item in TrailerQualityCombo.Items)
            {
                if (int.Parse(item.Tag.ToString()) == currentTrailerQ)
                {
                    TrailerQualityCombo.SelectedItem = item;
                    break;
                }
            }

            LoadPlayerSettings();
            LoadAioMetadataSettings();
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

        private void TrailerQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TrailerQualityCombo.SelectedItem is ComboBoxItem item)
            {
                if (int.TryParse(item.Tag.ToString(), out int q))
                {
                    if (q != AppSettings.TrailerQuality)
                    {
                        AppSettings.TrailerQuality = q;
                        ShowStatus("Fragman kalitesi ayarlandı: " + item.Content);
                    }
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
            if (PrebufferValueText != null) PrebufferValueText.Text = ((int)e.NewValue).ToString();
            if (AppSettings.PrebufferSeconds != (int)e.NewValue)
            {
                AppSettings.PrebufferSeconds = (int)e.NewValue;
            }
        }

        private void BufferSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (BufferSecondsValueText != null) BufferSecondsValueText.Text = ((int)e.NewValue).ToString();
            if (AppSettings.BufferSeconds != (int)e.NewValue)
            {
                AppSettings.BufferSeconds = (int)e.NewValue;
            }
        }

        private void MaxBufferSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (MaxBufferValueText != null) MaxBufferValueText.Text = ((int)e.NewValue).ToString();
            if (AppSettings.MaxBufferMegabytes != (int)e.NewValue)
            {
                AppSettings.MaxBufferMegabytes = (int)e.NewValue;
            }
        }

        private void SeekBackSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (SeekBackValueText != null) SeekBackValueText.Text = ((int)e.NewValue).ToString();
            if (AppSettings.SeekBackwardSeconds != (int)e.NewValue)
            {
                AppSettings.SeekBackwardSeconds = (int)e.NewValue;
            }
        }

        private void SeekForwardSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (SeekForwardValueText != null) SeekForwardValueText.Text = ((int)e.NewValue).ToString();
            if (AppSettings.SeekForwardSeconds != (int)e.NewValue)
            {
                AppSettings.SeekForwardSeconds = (int)e.NewValue;
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
            var login = App.CurrentLogin;
            if (login == null) return;

            BtnRefreshNow.IsEnabled = false;
            ShowStatus("Yenileme Başlatıldı...");
            
            await Services.ContentCacheService.Instance.SyncNowAsync(login);

            ShowStatus("İçerik başarıyla güncellendi.");
            BtnRefreshNow.IsEnabled = true;
        }

        private async void BtnClearProbe_Click(object sender, RoutedEventArgs e)
        {
            await Services.ProbeCacheService.Instance.ClearCacheAsync();
            ShowStatus("Analiz (Probe) verileri temizlendi.");
        }

        private async void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            await HistoryManager.Instance.ClearAsync();
            ShowStatus("İzleme geçmişi temizlendi.");
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
        private async void TmdbLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TmdbLanguageCombo.SelectedItem is ComboBoxItem item)
            {
                var lang = item.Tag?.ToString();
                if (!string.IsNullOrEmpty(lang) && AppSettings.TmdbLanguage != lang)
                {
                    AppSettings.TmdbLanguage = lang;
                    // [FIX] Clear all metadata related caches when language changes
                    await Services.ContentCacheService.Instance.ClearCacheAsync();
                    await Services.TmdbCacheService.Instance.ClearCacheAsync();
                    Services.Metadata.MetadataProvider.Instance.ClearCache();
                    
                    ShowStatus($"TMDB içerik dili ({item.Content}) olarak değiştirildi. Önbellek temizlendi.");
                }
            }
        }

        private bool _isUpdatingProfile = false;

        private void LoadPlayerSettings()
        {
            _isUpdatingProfile = true;
            try
            {
                var settings = AppSettings.PlayerSettings;

                // Set Engine
                SetComboSelection(PlayerEngineCombo, settings.Engine.ToString());

                // Set Profile
                SetComboSelection(PlayerProfileCombo, settings.Profile.ToString());
                UpdateProfileDescription(settings.Profile);

                // Set Individual Settings
                SetComboSelection(HwDecCombo, settings.HardwareDecoding.ToString());
                SetComboSelection(VoCombo, settings.VideoOutput.ToString());
                SetComboSelection(ScalerCombo, settings.Scaler.ToString());
                SetComboSelection(ToneMappingCombo, settings.ToneMapping.ToString());
                SetComboSelection(HdrTargetModeCombo, settings.TargetDisplayMode.ToString());
                TargetPeakBox.Text = settings.TargetPeak == 0 ? "" : settings.TargetPeak.ToString();
                SetComboSelection(AudioChannelsCombo, settings.AudioChannels.ToString());

                DebandToggle.IsOn = settings.Deband == Models.DebandMode.Yes;
                HdrComputePeakToggle.IsOn = settings.HdrComputePeak;
                ExclusiveToggle.IsOn = settings.ExclusiveAudio == Models.ExclusiveMode.Yes;
                PreferredAudioLangBox.Text = settings.PreferredAudioLanguage ?? "";
                PreferredSubtitleLangBox.Text = settings.PreferredSubtitleLanguage ?? "";
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
                        SetComboSelection(HdrTargetModeCombo, defaults.TargetDisplayMode.ToString());
                        TargetPeakBox.Text = defaults.TargetPeak == 0 ? "" : defaults.TargetPeak.ToString();
                        SetComboSelection(AudioChannelsCombo, defaults.AudioChannels.ToString());
                        DebandToggle.IsOn = defaults.Deband == Models.DebandMode.Yes;
                        HdrComputePeakToggle.IsOn = defaults.HdrComputePeak;
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
            OnPlayerSettingChanged();
        }

        private void TargetPeakBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingProfile) return;
            OnPlayerSettingChanged();
        }
        
        private void PreferredLang_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingProfile) return;
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

        private void HdrTargetModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingProfile) return;
            OnPlayerSettingChanged();
        }

        private Models.PlayerSettings GetCurrentSettingsFromUI()
        {
            var settings = new Models.PlayerSettings();

            if (PlayerEngineCombo.SelectedItem is ComboBoxItem eItem &&
                Enum.TryParse(eItem.Tag.ToString(), out Models.PlayerEngine engine))
            {
                settings.Engine = engine;
            }

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
            
             if (HdrTargetModeCombo.SelectedItem is ComboBoxItem tmcItem &&
                Enum.TryParse(tmcItem.Tag.ToString(), out Models.TargetDisplayMode tmc))
            {
                settings.TargetDisplayMode = tmc;
            }

            if (int.TryParse(TargetPeakBox.Text, out int nits))
            {
                settings.TargetPeak = nits;
            }
            else
            {
                settings.TargetPeak = 0; // Auto
            }

            if (AudioChannelsCombo.SelectedItem is ComboBoxItem acItem &&
                Enum.TryParse(acItem.Tag.ToString(), out Models.AudioChannels ac))
            {
                settings.AudioChannels = ac;
            }

            settings.Deband = DebandToggle.IsOn ? Models.DebandMode.Yes : Models.DebandMode.No;
            settings.HdrComputePeak = HdrComputePeakToggle.IsOn;
            settings.ExclusiveAudio = ExclusiveToggle.IsOn ? Models.ExclusiveMode.Yes : Models.ExclusiveMode.No;
            settings.PreferredAudioLanguage = PreferredAudioLangBox.Text?.Trim();
            settings.PreferredSubtitleLanguage = PreferredSubtitleLangBox.Text?.Trim();
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
        private void WorkerCountSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (WorkerCountValueText != null)
            {
                WorkerCountValueText.Text = ((int)e.NewValue).ToString();
            }
            if (AppSettings.ProbingWorkerCount != (int)e.NewValue)
            {
                AppSettings.ProbingWorkerCount = (int)e.NewValue;
            }
        }

        // ==========================================
        // AIOMetadata Settings
        // ==========================================
        private bool _isCustomAioMode = false;

        private void LoadAioMetadataSettings()
        {
            _isCustomAioMode = !string.IsNullOrEmpty(AppSettings.CustomAioMetadataUrl);

            if (_isCustomAioMode)
            {
                AioStatusText.Text = "Kişisel sunucu aktif";
                AioStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGreen);
                AioUrlBox.Text = AppSettings.CustomAioMetadataUrl;
                AioUrlBox.Visibility = Visibility.Visible;
                AioToggleBtn.Content = "Varsayılana Dön";
                AioConfigBtn.Visibility = Visibility.Visible;
                AioResetBtn.Visibility = Visibility.Visible;
            }
            else
            {
                AioStatusText.Text = "Varsayılan sunucu kullanılıyor";
                AioStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
                AioUrlBox.Visibility = Visibility.Collapsed;
                AioToggleBtn.Content = "Kendi Sunucumu Kullan";
                AioConfigBtn.Visibility = Visibility.Collapsed;
                AioResetBtn.Visibility = Visibility.Collapsed;
            }
        }

        private async void AioToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isCustomAioMode)
            {
                // Switch back to default
                AppSettings.CustomAioMetadataUrl = null;
                _isCustomAioMode = false;
                LoadAioMetadataSettings();
                ShowStatus("Varsayılan AIOMetadata sunucusuna dönüldü.");
            }
            else
            {
                // Switch to custom mode
                _isCustomAioMode = true;
                AioUrlBox.Visibility = Visibility.Visible;
                AioToggleBtn.Content = "Varsayılana Dön";
                AioConfigBtn.Visibility = Visibility.Visible;
                AioResetBtn.Visibility = Visibility.Visible;
                AioUrlBox.Focus(FocusState.Programmatic);
            }
        }

        private async void AioConfigBtn_Click(object sender, RoutedEventArgs e)
        {
            // Open the configurator in the default browser
            string configUrl = "https://aiometadatafortheweebs.midnightignite.me/configure/";
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(configUrl));
                ShowStatus("Yapılandırma sayfası tarayıcıda açıldı. Ayarları kaydedin ve URL'yi buraya yapıştırın.");
            }
            catch (Exception ex)
            {
                ShowStatus($"Tarayıcı açılamadı: {ex.Message}");
            }
        }

        private async void AioResetBtn_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.CustomAioMetadataUrl = null;
            _isCustomAioMode = false;
            LoadAioMetadataSettings();
            ShowStatus("AIOMetadata ayarları varsayılana sıfırlandı.");
        }

        private void AioUrlBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var url = AioUrlBox.Text.Trim();
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http"))
            {
                AioStatusText.Text = "URL geçerli — Kaydetmek için yapılandırma sayfasını kullanın";
                AioStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGreen);
                AppSettings.CustomAioMetadataUrl = url;
            }
            else if (!string.IsNullOrEmpty(url))
            {
                AioStatusText.Text = "Geçerli bir URL girin (https://...)";
                AioStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
            }
        }
    }
}
