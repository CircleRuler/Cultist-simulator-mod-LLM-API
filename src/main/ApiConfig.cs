using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// Helper types live in CsGameApi, not LLM_API: the entry class must be free to occupy the
// global namespace as `LLM_API` (see the note at the top of GameApiMod.cs).
namespace CsGameApi
{
    /// <summary>
    /// Runtime configuration, read once at startup from config.txt next to the mod.
    /// Any missing key falls back to a working default, so the mod runs with no config at all.
    /// </summary>
    internal static class ApiConfig
    {
        public const int DefaultPort = 8710;

        public static int Port = DefaultPort;
        public static bool Enabled = true;

        /// <summary>Where the request log and the self-test report are written.</summary>
        public static string Directory = DefaultDirectory();

        /// <summary>Run the built-in read/place/start/move self-test a few seconds after boot.</summary>
        public static bool SelfTest = true;

        /// <summary>Reference slot used by the self-test, in FucinePath form.</summary>
        public static string SelfTestSlotPath = "";

        /// <summary>Token id the self-test moves; empty means "first draggable card on the tabletop".</summary>
        public static string SelfTestTokenId = "";

        /// <summary>Milliseconds a request waits for the game thread before giving up.</summary>
        public static int RequestTimeoutMs = 15000;

        private static string DefaultDirectory()
        {
            try
            {
                string localLow = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                // LocalApplicationData points at ...\AppData\Local; LocalLow is its sibling.
                string appData = System.IO.Directory.GetParent(localLow)?.FullName;
                if (!string.IsNullOrEmpty(appData))
                {
                    string candidate = Path.Combine(appData, "LocalLow", "Weather Factory", "Cultist Simulator", "LLM_API");
                    return candidate;
                }
            }
            catch { }

            try
            {
                return Path.Combine(Path.GetTempPath(), "LLM_API");
            }
            catch
            {
                return "LLM_API";
            }
        }

        public static void Load(string modFolder)
        {
            try
            {
                string path = Path.Combine(modFolder, "config.txt");
                if (!File.Exists(path))
                {
                    WriteDefaultConfig(path);
                    return;
                }

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "port":
                            int p;
                            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out p) && p > 0 && p < 65536) Port = p;
                            break;
                        case "enabled":
                            Enabled = IsTrue(value);
                            break;
                        case "directory":
                        case "logdir":
                            if (value.Length > 0) Directory = value;
                            break;
                        case "selftest":
                            SelfTest = IsTrue(value);
                            break;
                        case "selftest_slot_path":
                            SelfTestSlotPath = value;
                            break;
                        case "selftest_token_id":
                            SelfTestTokenId = value;
                            break;
                        case "request_timeout_ms":
                            int t;
                            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out t) && t > 0) RequestTimeoutMs = t;
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Reflect.Log("config load failed, using defaults: " + e.Message);
            }
        }

        private static bool IsTrue(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            value = value.Trim().ToLowerInvariant();
            return value == "1" || value == "true" || value == "yes" || value == "on";
        }

        private static void WriteDefaultConfig(string path)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# LLM_API - external API for Cultist Simulator");
                sb.AppendLine("# Edit and restart the game to apply.");
                sb.AppendLine();
                sb.AppendLine("# TCP port the HTTP API listens on (localhost only).");
                sb.AppendLine("port=" + DefaultPort);
                sb.AppendLine();
                sb.AppendLine("# Set to 0 to load the mod without starting the HTTP server.");
                sb.AppendLine("enabled=1");
                sb.AppendLine();
                sb.AppendLine("# Where LLM_API.log and selftest.json are written.");
                sb.AppendLine("directory=" + DefaultDirectory());
                sb.AppendLine();
                sb.AppendLine("# Run the built-in read -> place -> start -> move self-test on the next boot.");
                sb.AppendLine("selftest=1");
                sb.AppendLine();
                sb.AppendLine("# Slot used by the self-test. Comment out to auto-detect the first open slot.");
                sb.AppendLine("# selftest_slot_path=~/tabletop!work_0/work");
                sb.AppendLine();
                sb.AppendLine("# Token moved by the self-test. Comment out to move the first draggable card.");
                sb.AppendLine("# selftest_token_id=!introjob_2");
                sb.AppendLine();
                sb.AppendLine("# How long an HTTP request waits for the game thread.");
                sb.AppendLine("request_timeout_ms=" + RequestTimeoutMs);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
