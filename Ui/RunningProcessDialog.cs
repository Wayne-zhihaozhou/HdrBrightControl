using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HdrBrightControl
{
    internal sealed class RunningProcessDialog : Form
    {
        private const int MinDialogWidth = 640;
        private const int MinDialogHeight = 420;
        private const int DefaultDialogWidth = 800;
        private const int DefaultDialogHeight = 550;
        private const int MaxDisplayLength = 80;

        private readonly ListBox _list;
        private readonly List<(string Name, string ClassName, string? Path)> _rows;

        public (string Name, string? WindowClass, string? Path)? Selected { get; private set; }

        public RunningProcessDialog(IEnumerable<(string Name, string ClassName, string? Path)> processes, Icon? icon)
        {
            _rows = new List<(string Name, string ClassName, string? Path)>(processes);

            Text = "添加正在运行的程序";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            MinimumSize = new Size(MinDialogWidth, MinDialogHeight);
            Padding = new Padding(12);
            SuspendLayout();

            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            foreach (var row in _rows)
            {
                string displayClass = row.ClassName.Length > MaxDisplayLength
                    ? row.ClassName[..(MaxDisplayLength - 3)] + "..."
                    : row.ClassName;
                _list.Items.Add($"{row.Name}   —   类：{displayClass}");
            }

            var ok = new Button { Text = "添加", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 10, 0, 0)
            };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(ok);

            _list.DoubleClick += (_, _) => { if (_list.SelectedIndex >= 0) DialogResult = DialogResult.OK; };

            Controls.Add(_list);
            Controls.Add(bottom);
            Font = SystemFonts.MenuFont;
            ClientSize = new Size(DefaultDialogWidth, DefaultDialogHeight);
            AcceptButton = ok;
            CancelButton = cancel;
            Icon = icon;
            ResumeLayout(false);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK && _list.SelectedIndex >= 0)
            {
                var row = _rows[_list.SelectedIndex];
                Selected = (row.Name, row.ClassName, row.Path);
            }
            base.OnFormClosing(e);
        }
    }
}
