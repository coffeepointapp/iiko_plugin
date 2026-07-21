using System;
using System.IO;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>Каталог DLL плагина (Host грузит из bin\…\net472).</summary>
    internal static class PluginPaths
    {
        public static string PluginDirectory
        {
            get
            {
                try
                {
                    var loc = typeof(PluginPaths).Assembly.Location;
                    if (!string.IsNullOrEmpty(loc))
                    {
                        var dir = Path.GetDirectoryName(loc);
                        if (!string.IsNullOrEmpty(dir))
                            return dir;
                    }
                }
                catch { /* fall through */ }

                return AppDomain.CurrentDomain.BaseDirectory?.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    ?? ".";
            }
        }

        public static string OrderGuestsJsonPath =>
            Path.Combine(PluginDirectory, "Bonoos_order_guests.json");

        public static string ApiAuditJsonPath =>
            Path.Combine(PluginDirectory, "Bonoos_loyalty_audit.json");
    }
}
