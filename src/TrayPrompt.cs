using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace AgentSleep
{
    internal enum PromptMode { General, Hotkey, Standalone, TrayLoss }

    internal sealed class TrayPrompt : Form
    {
        internal const string Usage = "--prompt [hotkey|standalone|tray-loss]";
        private const int TestLifetimeMilliseconds = 80000;
        private readonly PromptMode mode;
        private readonly Stopwatch age = new Stopwatch();
        private readonly Timer expiry;
        private Action accept;

        internal static bool TryParse(string[] args, out PromptMode mode)
        {
            mode = PromptMode.General;
            if (args.Length == 0 || args[0] != "--prompt" || args.Length > 2) return false;
            if (args.Length == 1) return true;
            switch (args[1])
            {
                case "hotkey": mode = PromptMode.Hotkey; return true;
                case "standalone": mode = PromptMode.Standalone; return true;
                case "tray-loss": mode = PromptMode.TrayLoss; return true;
                default: return false;
            }
        }

        internal TrayPrompt(PromptMode mode, Rectangle work, string shortcut, string problem, Action accept)
        {
            this.mode = mode;
            this.accept = problem == "" ? accept : null;
            Text = "AgentSleep — Start a display session";
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            TopMost = true;
            ClientSize = new Size(Math.Min(480, work.Width), Math.Min(170, work.Height));
            Location = new Point(work.Left + Math.Max(0, (work.Width - Width) / 2), work.Top + Math.Max(0, (work.Height - Height) / 2));

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 2 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            string instructions;
            switch (mode)
            {
                case PromptMode.Hotkey:
                    Text = "AgentSleep — Restore hotkey check";
                    instructions = "After your physical monitors go dark, wait five seconds and press " + shortcut + " once.";
                    break;
                case PromptMode.Standalone:
                    Text = "AgentSleep — Standalone restore check";
                    instructions = "Leave the mouse and keyboard alone. The test controller will run the restore command.";
                    break;
                case PromptMode.TrayLoss:
                    Text = "AgentSleep — Tray-loss recovery check";
                    instructions = "Leave the mouse and keyboard alone. The test controller will close only AgentSleep so its watchdog restores the displays.";
                    break;
                default: instructions = "Restore your displays with " + shortcut + "."; break;
            }
            string message = problem == "" ? "When you are ready, click Start now to switch to the virtual display. " + instructions : problem;
            layout.Controls.Add(new Label { Text = message, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var start = new Button { Text = "Start now", AutoSize = true, Enabled = this.accept != null };
            cancel.Click += delegate { Choose(false); };
            start.Click += delegate { Choose(true); };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(start);
            layout.Controls.Add(buttons, 0, 1);
            AcceptButton = CancelButton = cancel;
            if (mode != PromptMode.General)
            {
                expiry = new Timer { Interval = TestLifetimeMilliseconds };
                expiry.Tick += delegate { Choose(false); };
            }
            Shown += delegate
            {
                cancel.Focus();
                age.Start();
                if (expiry != null) expiry.Start();
            };
        }

        internal static bool HasExpired(PromptMode mode, long elapsedMilliseconds)
        {
            return mode != PromptMode.General && elapsedMilliseconds >= TestLifetimeMilliseconds;
        }

        internal void Choose(bool start)
        {
            if (IsDisposed) return;
            Action selected = start && !HasExpired(mode, age.ElapsedMilliseconds) ? accept : null;
            accept = null;
            Close();
            if (selected != null) selected();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && expiry != null) expiry.Dispose();
            base.Dispose(disposing);
        }
    }
}
