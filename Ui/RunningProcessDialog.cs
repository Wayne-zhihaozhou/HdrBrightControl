using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace HdrBrightControl
{
    /// <summary>One top-level window offered by the "add running program" dialog.</summary>
    internal sealed class WindowChoice
    {
        public WindowChoice(string name, string className, string title, string? path)
        {
            Name = name;
            ClassName = className;
            Title = title;
            Path = path;
        }

        public string Name { get; }

        public string ClassName { get; }

        public string Title { get; }

        public string? Path { get; }
    }

    internal sealed class RunningProcessDialog : Form
    {
        private const int MinDialogWidth = 640;
        private const int MinDialogHeight = 460;
        private const int MaxDisplayLength = 70;
        private const int ScopeLabelWidth = 80;
        private const int HintHeight = 36;

        private readonly ListBox _list;
        private readonly Label _hint;
        private readonly ComboBox _scope;
        private readonly List<WindowChoice> _rows;

        public (string Name, WindowFilter? Window, string? Path)? Selected { get; private set; }

        public RunningProcessDialog(IEnumerable<WindowChoice> windows, Icon? icon)
        {
            _rows = new List<WindowChoice>(windows);

            var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int dialogWidth = Math.Max(MinDialogWidth, (int)(screen.Width * 0.75));
            int dialogHeight = Math.Max(MinDialogHeight, (int)(screen.Height * 0.65));

            Text = "添加正在运行的程序";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            MinimumSize = new Size(MinDialogWidth, MinDialogHeight);
            Padding = new Padding(12);
            SuspendLayout();

            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            foreach (var row in _rows)
                _list.Items.Add($"{row.Name}   —   {Shorten(row.Title)}");
            _list.SelectedIndexChanged += (_, _) => ShowScopeForSelection();

            var scopeLabel = new Label
            {
                Text = "匹配范围",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _scope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            _scope.SelectedIndexChanged += (_, _) => ShowHint();

            var scopeRow = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 30,
                ColumnCount = 2,
                Margin = new Padding(0)
            };
            scopeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScopeLabelWidth));
            scopeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scopeRow.Controls.Add(scopeLabel, 0, 0);
            scopeRow.Controls.Add(_scope, 1, 0);

            _hint = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Bottom,
                Height = HintHeight,
                ForeColor = SystemColors.GrayText,
                TextAlign = ContentAlignment.MiddleLeft
            };

            var ok = new Button { Text = "添加", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 8, 0, 0)
            };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(ok);

            _list.DoubleClick += (_, _) => { if (_list.SelectedIndex >= 0) DialogResult = DialogResult.OK; };

            Controls.Add(_list);
            Controls.Add(_hint);
            Controls.Add(scopeRow);
            Controls.Add(bottom);
            Font = SystemFonts.MenuFont;
            ClientSize = new Size(dialogWidth, dialogHeight);
            AcceptButton = ok;
            CancelButton = cancel;
            Icon = icon;
            ResumeLayout(false);
        }

        private void ShowScopeForSelection()
        {
            int previous = _scope.SelectedIndex;
            _scope.Items.Clear();

            if (_list.SelectedIndex < 0)
            {
                _scope.Enabled = false;
                _hint.Text = string.Empty;
                return;
            }

            _scope.Enabled = true;
            _scope.Items.Add("该程序的所有窗口（推荐）");
            _scope.Items.Add($"仅此窗口：{Shorten(_rows[_list.SelectedIndex].Title)}");
            _scope.Items.Add($"除此窗口以外的所有窗口：{Shorten(_rows[_list.SelectedIndex].Title)}");
            _scope.SelectedIndex = previous >= 0 ? previous : 0;
        }

        private void ShowHint()
        {
            _hint.Text = _scope.SelectedIndex switch
            {
                0 => "进程一出现就算命中，主窗口和播放窗口都算。",
                1 => "只有这个标题的窗口在前台时才算；标题一换就不再命中，不适合视频页。",
                2 => "除这个标题的窗口以外，其它窗口都算。适合主窗口标题固定、播放窗口标题总变的程序。",
                _ => string.Empty
            };
        }

        private static string Shorten(string value)
        {
            const int MaxLength = MaxDisplayLength;
            return value.Length <= MaxLength ? value : value[..(MaxLength - 3)] + "...";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK && _list.SelectedIndex >= 0)
            {
                var row = _rows[_list.SelectedIndex];
                Selected = (row.Name, BuildFilter(row), row.Path);
            }
            base.OnFormClosing(e);
        }

        private WindowFilter? BuildFilter(WindowChoice row) => _scope.SelectedIndex switch
        {
            1 when row.Title.Length > 0 => new WindowFilter(WindowFilterKind.TitleEquals, row.Title),
            2 when row.Title.Length > 0 => new WindowFilter(WindowFilterKind.TitleNotEquals, row.Title),
            _ => null
        };
    }
}
