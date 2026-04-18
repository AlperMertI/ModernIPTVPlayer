using System;
using System.Runtime.InteropServices;

namespace ModernIPTVPlayer.Services
{
    public static partial class SleepPreventionService
    {
        [Flags]
        private enum EXECUTION_STATE : uint
        {
            ES_AWAYMODE_REQUIRED = 0x00000040,
            ES_CONTINUOUS = 0x80000000,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_SYSTEM_REQUIRED = 0x00000001
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial uint SetThreadExecutionState(EXECUTION_STATE esFlags);

        public static void PreventSleep()
        {
            try
            {
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED | EXECUTION_STATE.ES_DISPLAY_REQUIRED);
                AppLogger.Info("[SleepPreventionService] Sleep prevention enabled.");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[SleepPreventionService] Error enabling sleep prevention: {ex.Message}");
            }
        }

        public static void AllowSleep()
        {
            try
            {
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
                AppLogger.Info("[SleepPreventionService] Sleep prevention disabled.");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[SleepPreventionService] Error disabling sleep prevention: {ex.Message}");
            }
        }
    }
}
