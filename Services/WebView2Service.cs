using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace ModernIPTVPlayer.Services
{
    public static class WebView2Service
    {
        private static CoreWebView2Environment _sharedEnvironment;
        private static readonly SemaphoreSlim _envLock = new SemaphoreSlim(1, 1);

        public static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
        {
            if (_sharedEnvironment == null)
            {
                await _envLock.WaitAsync();
                try { if (_sharedEnvironment == null) {
                    string userDataFolder = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "WebView2_Shared_Cache");
                    var options = new CoreWebView2EnvironmentOptions 
                    { 
                        AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required --disable-features=PreloadMediaEngagementData,AutoplayIgnoreWebAudio --enable-features=RunVideoWithDisplayOff --disable-backgrounding-occluded-windows" 
                    };
                    _sharedEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options);
                }} finally { _envLock.Release(); }
            }
            return _sharedEnvironment;
        }

        public static async Task ApplyYouTubeCleanUISettingsAsync(CoreWebView2 webView)
        {
            const string cleanScript = @"
                (function() {
                    const host = window.location.host || 'top';
                    function log(msg) {
                        const m = '[SmartCrop][' + host + '] ' + msg;
                        console.log(m);
                        try { window.chrome.webview.postMessage('LOG:' + m); } catch(e) {}
                        try { window.parent.postMessage({ type: 'LOG_FORWARD', msg: 'LOG:' + m }, '*'); } catch(e) {}
                    }

                    function applyStyles() {
                        if (!document.head && !document.documentElement) {
                            setTimeout(applyStyles, 100);
                            return;
                        }
                        const style = document.createElement('style');
                        style.textContent = `
                            .ytp-chrome-top, .ytp-chrome-bottom, .ytp-youtube-button, 
                            .ytp-impression-link, .ytp-pause-overlay, .ytp-ce-element,
                            .ytp-gradient-top, .ytp-gradient-bottom, .ytp-watermark,
                            .ytp-cards-button, .ytp-cards-teaser, .ytp-paid-content-overlay,
                            .ytp-bezel, .ytp-error-screen, .ytp-spinner {
                                display: none !important; opacity: 0 !important; visibility: hidden !important;
                            }
                            video { transition: transform 0.6s cubic-bezier(0.4, 0, 0.2, 1) !important; }
                        `;
                        (document.head || document.documentElement).appendChild(style);
                    }
                    applyStyles();

                    // [PRO] Internal state with sliding history window
                    window._smartCropState = {
                        lastScale: 1, history: [],
                        startTime: Date.now(), lastResetTime: 0, isLocked: false
                    };

                    window.resetSmartCrop = function() {
                        log('Reseting state (Sliding Window Reset)');
                        const state = window._smartCropState;
                        state.lastScale = 1; state.history = []; state.isLocked = false;
                        state.startTime = Date.now(); state.lastResetTime = Date.now();
                        document.querySelectorAll('video').forEach(v => v.style.transform = 'scale(1)');
                    };

                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.addEventListener('message', function(e) {
                            if (e.data === 'RESET_SMART_CROP') window.resetSmartCrop();
                        });
                    }
                    window.addEventListener('message', function(e) {
                        if (e.data === 'RESET_SMART_CROP') window.resetSmartCrop();
                    });

                    window.addEventListener('resize', function() {
                        log('Window resized caught');
                        window.resetSmartCrop();
                    });

                    function getMedian(arr) {
                        if (arr.length === 0) return 1;
                        const s = [...arr].sort((a,b) => a-b);
                        const mid = Math.floor(s.length / 2);
                        return s.length % 2 !== 0 ? s[mid] : (s[mid-1] + s[mid]) / 2;
                    }

                    function runSmartCrop() {
                        const state = window._smartCropState;
                        if (state.isLocked) return;

                        const videos = Array.from(document.querySelectorAll('video'));
                        const video = videos.find(v => !v.paused && v.readyState >= 3) || videos[0];

                        const uptime = Date.now() - state.startTime;
                        if (!video || video.paused || video.readyState < 3 || video.seeking) return;

                        const timeSinceReset = Date.now() - state.lastResetTime;
                        if (state.lastResetTime > 0 && timeSinceReset < 1500) return;

                        const canvas = document.createElement('canvas');
                        canvas.width = 100; canvas.height = 100;
                        const ctx = canvas.getContext('2d');
                        
                        try {
                            ctx.drawImage(video, 0, 0, 100, 100);
                            const data = ctx.getImageData(0, 0, 100, 100).data;
                            const getPixel = (x, y) => {
                                const i = (y * 100 + x) * 4;
                                return Math.max(data[i], data[i+1], data[i+2]);
                            };

                            const threshold = 28;
                            let inTop = 0;
                            for (let y = 0; y < 45; y++) {
                                let found = false;
                                for (let x = 10; x < 90; x += 10) if (getPixel(x,y) > threshold) { found = true; break; }
                                if (found) break;
                                inTop++;
                            }
                            let inBot = 0;
                            for (let y = 99; y > 55; y--) {
                                let found = false;
                                for (let x = 10; x < 90; x += 10) if (getPixel(x,y) > threshold) { found = true; break; }
                                if (found) break;
                                inBot++;
                            }
                            let inLeft = 0;
                            for (let x = 0; x < 45; x++) {
                                let found = false;
                                for (let y = 10; y < 90; y += 10) if (getPixel(x,y) > threshold) { found = true; break; }
                                if (found) break;
                                inLeft++;
                            }
                            let inRight = 0;
                            for (let x = 99; x > 55; x--) {
                                let found = false;
                                for (let y = 10; y < 90; y += 10) if (getPixel(x,y) > threshold) { found = true; break; }
                                if (found) break;
                                inRight++;
                            }

                            // Factor out current scale for 'True' box
                            const vRect = video.getBoundingClientRect();
                            const trueVW = vRect.width / state.lastScale;
                            const trueVH = vRect.height / state.lastScale;
                            const wWidth = window.innerWidth;
                            const wHeight = window.innerHeight;

                            // Content limit calculation
                            const cRatioX = 1 - (inLeft + inRight) / 100;
                            const cRatioY = 1 - (inTop + inBot) / 100;
                            const maxW = wWidth / (trueVW * cRatioX);
                            const maxH = wHeight / (trueVH * cRatioY);

                            let currentDetection = Math.min(maxW, maxH);
                            currentDetection = Math.min(1.35, Math.max(1, currentDetection));

                            // Update sliding window
                            const isInitial = uptime < 40000;
                            const windowSize = isInitial ? 3 : 20; // 3sec initial, 20sec production
                            state.history.push(currentDetection);
                            while (state.history.length > windowSize) state.history.shift();
                            const median = getMedian(state.history);

                            // Only log on change (> 0.04) or during initial settling
                            if (isInitial || Math.abs(median - state.lastScale) > 0.04) {
                                log('Analyzing: cur=' + currentDetection.toFixed(2) + ' median=' + median.toFixed(2) + ' (initial=' + isInitial + ')');
                            }

                            // Commit if median deviates significantly from last scale
                            if (Math.abs(median - state.lastScale) > 0.04) {
                                log('Applying Stabilized Median: ' + state.lastScale.toFixed(2) + ' -> ' + median.toFixed(2));
                                video.style.transform = 'scale(' + median + ')';
                                state.lastScale = median;
                            } else if (!isInitial && state.history.length >= windowSize && state.lastScale > 1.02) {
                                // Lock if stable for 20 frames outside initial window
                                log('System Lock at scale ' + state.lastScale.toFixed(2));
                                state.isLocked = true;
                            }
                        } catch(e) {}
                    }

                    setInterval(runSmartCrop, 1000);
                })();
            ";
            await webView.AddScriptToExecuteOnDocumentCreatedAsync(cleanScript);
        }
    }
}
