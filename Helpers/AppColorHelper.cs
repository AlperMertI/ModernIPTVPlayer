using System;
using Microsoft.UI;
using Windows.UI;

namespace ModernIPTVPlayer.Helpers
{
    public static class AppColorHelper
    {
        public static Windows.UI.Color ToWindowsColor(string hex)
        {
             var c = ToColor(hex);
             return Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        public static Color ToColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Colors.Transparent;
            try
            {
                hex = hex.Replace("#", "");
                if (hex.Length == 6) hex = "FF" + hex;
                if (hex.Length != 8) return Colors.Transparent;

                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);

                return Color.FromArgb(a, r, g, b);
            }
            catch
            {
                return Colors.Transparent;
            }
        }

        public static Color FromHex(string hex) => ToColor(hex);
    }
}
