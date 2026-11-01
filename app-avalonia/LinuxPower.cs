using System;
using System.IO;

namespace NoiseReduction.Avalonia
{
    internal static class LinuxPower
    {
        internal static bool? Read(string directory = "/sys/class/power_supply")
        {
            if (!Directory.Exists(directory)) return null;
            bool battery = false, discharging = false, external = false, charging = false;
            try
            {
                foreach (string supply in Directory.EnumerateDirectories(directory))
                {
                    string type = Text(supply, "type");
                    if (type == "Battery" && Text(supply, "present") != "0")
                    {
                        battery = true;
                        string status = Text(supply, "status");
                        discharging |= status == "Discharging";
                        charging |= status == "Charging" || status == "Full";
                    }
                    else if (Text(supply, "online") == "1") external = true;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            if (external) return false;
            if (battery && discharging) return true;
            return charging ? false : null;
        }

        private static string Text(string directory, string name)
        {
            string path = Path.Combine(directory, name);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
    }
}
