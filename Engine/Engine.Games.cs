using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HdrBrightControl
{
    public sealed partial class HdrEngine
    {
        private const string EntrySeparator = "|";
        private const string TitleEqualsPrefix = "T=";
        private const string TitleNotEqualsPrefix = "T!=";
        private const string ClassEqualsPrefix = "C=";

        internal static string CleanExeInput(string rawInput)
        {
            string trimmed = rawInput.Trim().Trim('"', '\'');
            if (trimmed.Contains('\\') || trimmed.Contains('/') || trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                trimmed = Path.GetFileNameWithoutExtension(trimmed);
            return trimmed.ToLowerInvariant();
        }

        /// <summary>
        /// Parses a config entry: the process name, and optionally the window filter after "|".
        /// "T=" is an exact title, "T!=" every title but that one, "C=" a window class. A bare value behind the
        /// separator is read as a class name, which is what an earlier version wrote there.
        /// </summary>
        internal static (string ProcessName, WindowFilter? Window) ParseEntry(string entry)
        {
            int separator = entry.IndexOf(EntrySeparator);
            if (separator <= 0) return (entry.ToLowerInvariant(), null);

            string processName = entry[..separator].ToLowerInvariant();
            string spec = entry[(separator + 1)..];
            if (string.IsNullOrWhiteSpace(spec)) return (processName, null);

            if (spec.StartsWith(TitleNotEqualsPrefix, StringComparison.Ordinal))
                return (processName, new WindowFilter(WindowFilterKind.TitleNotEquals, spec[TitleNotEqualsPrefix.Length..]));
            if (spec.StartsWith(TitleEqualsPrefix, StringComparison.Ordinal))
                return (processName, new WindowFilter(WindowFilterKind.TitleEquals, spec[TitleEqualsPrefix.Length..]));
            if (spec.StartsWith(ClassEqualsPrefix, StringComparison.Ordinal))
                return (processName, new WindowFilter(WindowFilterKind.ClassEquals, spec[ClassEqualsPrefix.Length..]));

            return (processName, new WindowFilter(WindowFilterKind.ClassEquals, spec));
        }

        /// <summary>Formats a process name and its optional window filter into a config entry.</summary>
        internal static string FormatEntry(string processName, WindowFilter? window)
        {
            if (window == null || string.IsNullOrWhiteSpace(window.Value)) return processName;

            string prefix = window.Kind switch
            {
                WindowFilterKind.TitleNotEquals => TitleNotEqualsPrefix,
                WindowFilterKind.TitleEquals => TitleEqualsPrefix,
                _ => ClassEqualsPrefix
            };
            return string.Concat(processName, EntrySeparator, prefix, window.Value);
        }

        /// <summary>How a window filter reads in the list.</summary>
        private static string DescribeWindow(WindowFilter? window) => window switch
        {
            null => string.Empty,
            { Kind: WindowFilterKind.TitleNotEquals } filter => $"除「{Shorten(filter.Value)}」以外的窗口",
            { Kind: WindowFilterKind.TitleEquals } filter => $"仅「{Shorten(filter.Value)}」",
            { Kind: WindowFilterKind.ClassEquals } filter => $"窗口类 {filter.Value}",
            _ => string.Empty
        };

        private static string Shorten(string value)
        {
            const int MaxLength = 32;
            return value.Length <= MaxLength ? value : value[..(MaxLength - 3)] + "...";
        }

        /// <summary>Rows for one of the two lists: every library game (real name, source, icon) plus its own programs.</summary>
        public List<GameRow> GetGameRows(ProgramListKind kind = ProgramListKind.Hdr)
        {
            lock (Sync)
            {
                var list = kind == ProgramListKind.Sdr ? Config.SdrGamesAndApps : Config.HdrGamesAndApps;
                var paths = Config.ProgramPaths ?? new Dictionary<string, string>();
                var brightnessMap = Config.PerGameBrightness ?? new Dictionary<string, int>();
                var hdrModes = Config.GameHdrModes;
                var lastLaunched = Config.LastLaunchedAt ?? new Dictionary<string, long>();

                var rows = new List<GameRow>();
                var processes = list ?? new List<string>();

                foreach (string entry in processes)
                {
                    var (processName, window) = ParseEntry(entry);
                    string windowText = DescribeWindow(window);
                    string displayName = windowText.Length == 0 ? processName : $"{processName}（{windowText}）";
                    string subtitle = windowText.Length == 0
                        ? $"程序  ·  {processName}.exe"
                        : $"窗口限定  ·  {windowText}";

                    rows.Add(new GameRow
                    {
                        Key = "proc:" + processName,
                        Name = displayName,
                        Subtitle = subtitle,
                        Processes = new List<string> { processName },
                        ExePath = paths.TryGetValue(processName, out var path) ? path : null,
                        Tracked = kind == ProgramListKind.Sdr || IsHdrEnabledFor(hdrModes, processName),
                        Brightness = brightnessMap.TryGetValue(processName, out int brightness) ? brightness : (int)BrightnessRange.Default
                    });
                }

                return rows.OrderByDescending(r => LastLaunchTime(r.Processes, lastLaunched), Comparer<long>.Default)
                           .ThenBy(r => r.Name, Cmp)
                           .ToList();
            }
        }

        private static long LastLaunchTime(IEnumerable<string> processes, Dictionary<string, long> lastLaunched)
        {
            long max = 0;
            foreach (var p in processes)
            {
                if (lastLaunched.TryGetValue(p, out var t) && t > max)
                    max = t;
            }
            return max;
        }

        public void SetGameBrightness(IEnumerable<string> processes, int brightness, string displayName)
        {
            int value = Math.Clamp(brightness, (int)BrightnessRange.Min, (int)BrightnessRange.Max);

            lock (Sync)
            {
                var brightnessMap = Config.PerGameBrightness ?? new Dictionary<string, int>(Cmp);
                foreach (string process in processes) brightnessMap[process] = value;
                Config = Config with { PerGameBrightness = brightnessMap };

                InvalidateSnapshot();
                Save();
            }

            Log("BRIGHT", $"'{displayName}' 亮度设为 {value}");
        }

        /// <summary>
        /// Adds a program to one of the two lists; it is removed from the other one. Adding a program that is already
        /// listed replaces its entry, which is how the window scope of an existing entry is changed.
        /// </summary>
        public bool AddGame(string procName, string? exePath = null, ProgramListKind kind = ProgramListKind.Hdr, WindowFilter? window = null)
        {
            if (string.IsNullOrWhiteSpace(procName) ||
                procName.Equals(ProcessNames.Explorer, StringComparison.OrdinalIgnoreCase) ||
                IsSelf(procName))
            {
                Log("ERROR", "无效的程序名。");
                return false;
            }

            procName = procName.ToLowerInvariant();
            string listName = kind == ProgramListKind.Sdr ? "SDR" : "HDR";
            WindowFilter? filter = window == null || string.IsNullOrWhiteSpace(window.Value) ? null : window;
            string configEntry = FormatEntry(procName, filter);
            string windowText = DescribeWindow(filter);

            lock (Sync)
            {
                var target = kind == ProgramListKind.Sdr ? Config.SdrGamesAndApps : Config.HdrGamesAndApps;
                var other = kind == ProgramListKind.Sdr ? Config.HdrGamesAndApps : Config.SdrGamesAndApps;

                bool wasListed = target.Exists(entry => Cmp.Equals(ParseEntry(entry).ProcessName, procName));
                target.RemoveAll(entry => Cmp.Equals(ParseEntry(entry).ProcessName, procName));
                other.RemoveAll(entry => Cmp.Equals(ParseEntry(entry).ProcessName, procName));

                target.Add(configEntry);
                target.Sort(Cmp);

                string scope = windowText.Length == 0 ? string.Empty : $"（{windowText}）";
                Log("LIST", wasListed
                    ? $"'{procName}' 的 {listName} 条目已更新{scope}"
                    : $"+ '{procName}' 已添加到 {listName} 列表{scope}");

                if (kind == ProgramListKind.Sdr) Config.GameHdrModes.Remove(procName);
                else Config.GameHdrModes[procName] = true;

                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    Config.ProgramPaths[procName] = exePath;

                InvalidateSnapshot();
                Save();
            }

            return true;
        }

        public void RemoveGame(IEnumerable<string> processes, ProgramListKind kind = ProgramListKind.Hdr)
        {
            var procList = processes.ToList();
            lock (Sync)
            {
                var target = kind == ProgramListKind.Sdr ? Config.SdrGamesAndApps : Config.HdrGamesAndApps;

                foreach (string process in procList)
                {
                    target.RemoveAll(name => Cmp.Equals(ParseEntry(name).ProcessName, process));
                    Config.ProgramPaths.Remove(process);
                    if (kind == ProgramListKind.Hdr) Config.GameHdrModes.Remove(process);
                }

                target.Sort(Cmp);
                InvalidateSnapshot();
                Save();
            }

            if (procList.Count > 0) Log("LIST", $"- '{procList[0]}' 已移除");
        }
    }
}
