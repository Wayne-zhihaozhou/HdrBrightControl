using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HdrBrightControl
{
    public sealed partial class MainForm
    {
        private static readonly IntPtr IntPtrZero = IntPtr.Zero;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private void RebuildGames(ProgramListKind kind)
        {
            var list = kind == ProgramListKind.Sdr ? _gamesSdr : _gamesHdr;
            var search = kind == ProgramListKind.Sdr ? _searchSdr : _searchHdr;

            string filter = search.Text.Trim();
            GameItem[] items = _engine.GetGameRows(kind)
                .Where(row => filter.Length == 0 || MatchesFilter(row, filter))
                .Select(row => new GameItem(row))
                .ToArray();

            list.SetItems(items, list.SelectedItem?.Key);
        }

        private void RebuildAllGames()
        {
            RebuildGames(ProgramListKind.Hdr);
            RebuildGames(ProgramListKind.Sdr);
        }

        private static bool MatchesFilter(GameRow row, string filter) =>
            row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            row.Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase);

        private bool IsBrightnessControlEnabled() => _engine.Read(config => config.Brightness?.Enabled ?? true);

        private void OnBrightnessControlChanged(bool enabled)
        {
            _engine.Update(config =>
            {
                var brightness = config.Brightness ?? new BrightnessConfig();
                config = config with { Brightness = brightness };
                config.Brightness!.Enabled = enabled;
                return config;
            });
            _engine.ApplyBrightnessControlSettings();
        }

        private void OnGameBrightnessChanged(GameItem item, ProgramListKind kind)
        {
            _engine.SetGameBrightness(item.Processes, item.Brightness, item.Name);

            // Only the program the engine assigns the brightness to right now changes the TV; the others keep their
            // value for later, and the engine applies it as soon as they take the screen back.
            if (item.Tracked && IsBrightnessControlEnabled() && OwnsScreen(item)) ApplyGameBrightness(item.Brightness);

            BeginInvoke(new Action(() => (kind == ProgramListKind.Sdr ? _gamesSdr : _gamesHdr).Invalidate()));
        }

        private bool OwnsScreen(GameItem item)
        {
            string? owner = _engine.Status.BrightnessOwner;
            return owner != null && item.Processes.Contains(owner, StringComparer.OrdinalIgnoreCase);
        }

        private async void ApplyGameBrightness(int brightness)
        {
            try
            {
                await TvCli.SetBrightnessAsync(brightness);
            }
            catch (Exception ex)
            {
                _engine.WriteLine($"[TV] 无法应用游戏亮度: {ex.Message}");
            }
        }

        private async Task ShowTvResultAsync(string busyText, Func<Task<TvResult>> action)
        {
            _tvStatusLabel.Text = busyText;
            _tvStatusLabel.ForeColor = Theme.Muted;

            try
            {
                var result = await action();
                _tvStatusLabel.Text = result.Detail;
                _tvStatusLabel.ForeColor = result.Success ? Color.Green : Color.Red;
            }
            catch (Exception ex)
            {
                _tvStatusLabel.Text = $"失败: {ex.Message}";
                _tvStatusLabel.ForeColor = Color.Red;
            }
        }

        private async Task<TvResult> SetTestBrightnessAsync(int brightness)
        {
            if (!IsBrightnessControlEnabled()) return new TvResult(false, "亮度控制已关闭");

            return await TvCli.SetBrightnessAsync(brightness);
        }

        private async Task ShowHdrResultAsync(string busyText, bool enable)
        {
            _tvStatusLabel.Text = busyText;
            _tvStatusLabel.ForeColor = Theme.Muted;

            try
            {
                var result = HdrController.SetHdrState(enable);
                if (result.Ok)
                {
                    _tvStatusLabel.Text = enable ? "HDR 已开启" : "HDR 已关闭";
                    _tvStatusLabel.ForeColor = Color.Green;
                }
                else
                {
                    _tvStatusLabel.Text = $"失败: {result.Error}";
                    _tvStatusLabel.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                _tvStatusLabel.Text = $"失败: {ex.Message}";
                _tvStatusLabel.ForeColor = Color.Red;
            }
        }

        private void OnDesktopBrightnessChanged()
        {
            int value = _desktopBrightness.Value;
            _engine.Update(config => { _desktopBrightness.Value = value; return config with { DesktopBrightness = value }; });

            if (!IsBrightnessControlEnabled()) return;

            // The TV is showing the desktop brightness right now: change it at once
            if (_engine.Status.BrightnessOwner == null) ApplyGameBrightness(value);
        }

        private void OnResetSettings()
        {
            var answer = MessageBox.Show(this,
                "是否将所有设置恢复为默认值？\n\n您的游戏、它们的 HDR 开关将保留。\"开机自启动\"不会改变。",
                "恢复默认设置", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _engine.ResetSettings();
            LoadSettings();
            RebuildAllGames();
        }

        private void ReloadConfig()
        {
            if (_engine.ReloadConfig())
            {
                LoadSettings();
                RebuildAllGames();
                return;
            }

            MessageBox.Show(this, "config.json 无法读取。请查看日志选项卡。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OnAddExe(ProgramListKind kind)
        {
            using var dialog = new OpenFileDialog
            {
                Title = kind == ProgramListKind.Sdr ? "选择应保持 SDR 的游戏或程序（.exe）" : "选择应使用 HDR 的游戏或程序（.exe）",
                Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
                Multiselect = true,
                InitialDirectory = string.Empty
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            foreach (string file in dialog.FileNames)
                _engine.AddGame(HdrEngine.CleanExeInput(file), exePath: file, kind);

            RebuildGames(kind);
        }

        private void OnAddRunning(ProgramListKind kind)
        {
            var picked = PickRunningProcess();
            if (picked == null) return;

            _engine.AddGame(picked.Value.Name, exePath: picked.Value.Path, kind, picked.Value.Window);
            RebuildGames(kind);
        }

        /// <summary>
        /// Collects the visible top-level windows of all other processes. Child windows are left out on purpose: the
        /// foreground window is always a top-level one, so an entry for a child window could never match.
        /// </summary>
        private (string Name, WindowFilter? Window, string? Path)? PickRunningProcess()
        {
            var windows = new List<(uint ProcessId, string ClassName, string Title)>();

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0) return true;

                var titleBuf = new StringBuilder(512);
                GetWindowText(hWnd, titleBuf, titleBuf.Capacity);
                string title = titleBuf.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                var classBuf = new StringBuilder(256);
                GetClassName(hWnd, classBuf, classBuf.Capacity);
                string className = classBuf.ToString();

                windows.Add((pid, className, title));
                return true;
            }, IntPtrZero);

            var choices = new List<WindowChoice>();
            var seen = new HashSet<(string Process, string Title)>();

            foreach (var window in windows.OrderBy(w => w.ProcessId).ThenBy(w => w.Title, StringComparer.CurrentCulture))
            {
                try
                {
                    using var process = Process.GetProcessById((int)window.ProcessId);
                    string processName = process.ProcessName.ToLowerInvariant();
                    if (processName.Equals(HdrEngine.SelfProcessName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add((processName, window.Title))) continue;

                    choices.Add(new WindowChoice(processName, window.ClassName, window.Title,
                        ProcessPaths.TryGetExePath(process.Id)));
                }
                catch { }
            }

            choices = choices
                .OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(choice => choice.Title, StringComparer.CurrentCulture)
                .ToList();

            using var dialog = new RunningProcessDialog(choices, _windowIcon);
            if (dialog.ShowDialog(this) != DialogResult.OK) return null;
            return dialog.Selected;
        }

        private void OnRemoveGame(ProgramListKind kind)
        {
            var list = kind == ProgramListKind.Sdr ? _gamesSdr : _gamesHdr;
            var selected = list.SelectedItem;
            if (selected == null)
            {
                MessageBox.Show(this, "请先选择一个游戏或程序。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string consequence = kind == ProgramListKind.Sdr
                ? "移除后，该游戏将不再强制保持 SDR。"
                : "移除后，该游戏将不再自动触发 HDR。";
            var answer = MessageBox.Show(this,
                $"是否移除 '{selected.Name}'？\n\n{consequence}",
                "移除游戏", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _engine.RemoveGame(selected.Processes, kind);
            RebuildGames(kind);
        }

        /// <summary>Launches the program of a row that was double-clicked on the HDR or SDR page.</summary>
        private void OnGameItemActivated(GameItem item)
        {
            string? exePath = item.ExePath;

            if (string.IsNullOrWhiteSpace(exePath))
            {
                MessageBox.Show(this,
                    $"'{item.Name}' 没有记录可执行文件路径，无法启动。\n\n请移除该条目，再用「添加程序」重新添加它的 .exe。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!File.Exists(exePath))
            {
                MessageBox.Show(this,
                    $"找不到 '{item.Name}' 的可执行文件：\n{exePath}\n\n请确认文件是否已被移动或删除。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (IsSameExecutable(exePath, Application.ExecutablePath))
            {
                MessageBox.Show(this, "无法从列表中启动 HDR亮控自身。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty
                };
                Process.Start(startInfo)?.Dispose();
                
                // Record the launch time for sorting (most recent first)
                _engine.Update(config =>
                {
                    var lastLaunched = config.LastLaunchedAt ?? new Dictionary<string, long>();
                    foreach (var proc in item.Processes)
                        lastLaunched[proc] = DateTime.UtcNow.Ticks;
                    return config with { LastLaunchedAt = lastLaunched };
                });
                
                _engine.Log("LAUNCH", $"已启动 '{item.Name}'：{exePath}");
                
                // Refresh the list so the newly launched item moves to the top
                RebuildAllGames();
            }
            catch (Exception ex)
            {
                _engine.Log("ERROR", $"无法启动 '{item.Name}'：{ex.Message}");
                MessageBox.Show(this, $"无法启动 '{item.Name}'。\n\n{ex.Message}",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static bool IsSameExecutable(string first, string second)
        {
            string? firstPath = ProgramIdentity.FullPath(first);
            string? secondPath = ProgramIdentity.FullPath(second);
            return firstPath != null && secondPath != null &&
                   string.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase);
        }

        private void OpenInNotepad(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(this, "文件尚不存在：\n" + path, Text);
                    return;
                }

                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
