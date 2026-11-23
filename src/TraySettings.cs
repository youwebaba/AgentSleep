using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AgentSleep
{
    internal sealed class TraySettings
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "AgentSleep";
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSleep", "settings.txt");
        private static readonly string[] ShortcutNames = { "Ctrl+Alt", "Ctrl+Shift", "Alt+Shift", "Ctrl+Alt+Shift" };
        private static readonly uint[] ShortcutModifiers = { 3, 6, 5, 7 };

        internal string TargetId = "";
        internal string QualifiedId = "";
        internal bool Timer;
        internal int Minutes = 10;
        internal int ModifierChoice;
        internal Keys Key = Keys.F11;

        internal uint Modifiers { get { return ShortcutModifiers[ModifierChoice]; } }
        internal string Shortcut { get { return ShortcutNames[ModifierChoice] + "+" + Key; } }
        internal bool Qualified
        {
            get { return TargetId.Length != 0 && String.Equals(TargetId, QualifiedId, StringComparison.OrdinalIgnoreCase); }
        }

        internal static TraySettings Load()
        {
            if (!File.Exists(SettingsPath)) return new TraySettings();
            string[] lines = File.ReadAllLines(SettingsPath);
            int minutes, modifiers, key;
            if (lines.Length != 7 || lines[0] != "1" || (lines[3] != "0" && lines[3] != "1") ||
                !Int32.TryParse(lines[4], out minutes) || minutes < 1 || minutes > 240 ||
                !Int32.TryParse(lines[5], out modifiers) || modifiers < 0 || modifiers >= ShortcutModifiers.Length ||
                !Int32.TryParse(lines[6], out key) || key < (int)Keys.F2 || key > (int)Keys.F11)
                throw new InvalidDataException("AgentSleep settings could not be read. Timer and switching are paused.");

            return new TraySettings {
                TargetId = lines[1], QualifiedId = lines[2], Timer = lines[3] == "1",
                Minutes = minutes, ModifierChoice = modifiers, Key = (Keys)key
            };
        }

        internal void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            string temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temp, new[] { "1", TargetId, QualifiedId, Timer ? "1" : "0",
                    Minutes.ToString(CultureInfo.InvariantCulture), ModifierChoice.ToString(CultureInfo.InvariantCulture),
                    ((int)Key).ToString(CultureInfo.InvariantCulture) });
                if (File.Exists(SettingsPath)) File.Replace(temp, SettingsPath, null);
                else File.Move(temp, SettingsPath);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static string StartupCommand { get { return "\"" + Application.ExecutablePath + "\""; } }

        internal static bool StartsWithWindows()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && String.Equals(key.GetValue(RunName) as string, StartupCommand, StringComparison.OrdinalIgnoreCase);
        }

        internal static void SetStartup(bool enable)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) key.SetValue(RunName, StartupCommand, RegistryValueKind.String);
                else key.DeleteValue(RunName, false);
            }
        }

        internal static TraySettings Edit(TraySettings current, DisplayTarget[] choices, bool startup, out bool startWithWindows)
        {
            startWithWindows = startup;
            using (var dialog = new Form())
            {
                dialog.Text = "AgentSleep settings";
                dialog.AutoScaleMode = AutoScaleMode.Font;
                dialog.StartPosition = FormStartPosition.CenterScreen;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = dialog.MaximizeBox = false;
                dialog.AutoSize = true;
                dialog.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2 };
                dialog.Controls.Add(layout);

                var targets = new ComboBox { Width = 290, DropDownStyle = ComboBoxStyle.DropDownList };
                targets.Items.Add("Select a verified virtual display");
                int selected = 0;
                for (int i = 0; i < choices.Length; i++)
                {
                    DisplayTarget display = choices[i];
                    targets.Items.Add(display.Label + " [" + display.Id.Substring(0, Math.Min(8, display.Id.Length)) + "]");
                    if (String.Equals(current.TargetId, display.Id, StringComparison.OrdinalIgnoreCase)) selected = i + 1;
                }
                if (selected == 0 && current.TargetId.Length != 0)
                    selected = targets.Items.Add("Previously selected target is unavailable");
                targets.SelectedIndex = selected;
                AddRow(layout, "Virtual display", targets);

                var timer = new CheckBox { Text = "Start a session after Windows input is idle", AutoSize = true, Checked = current.Timer };
                AddRow(layout, "Idle timer", timer);
                var minutes = new NumericUpDown { Minimum = 1, Maximum = 240, Width = 75, Value = current.Minutes };
                AddRow(layout, "Idle minutes", minutes);

                var chords = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
                var modifiers = new ComboBox { Width = 123, DropDownStyle = ComboBoxStyle.DropDownList };
                modifiers.Items.AddRange(ShortcutNames);
                modifiers.SelectedIndex = current.ModifierChoice;
                chords.Controls.Add(modifiers);
                var keys = new ComboBox { Width = 65, DropDownStyle = ComboBoxStyle.DropDownList };
                for (Keys key = Keys.F2; key <= Keys.F11; key++) keys.Items.Add(key);
                keys.SelectedItem = current.Key;
                chords.Controls.Add(keys);
                AddRow(layout, "Restore hotkey", chords);

                var run = new CheckBox { Text = "Start AgentSleep when I sign in", AutoSize = true, Checked = startup };
                AddRow(layout, "Windows startup", run);
                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
                var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(save);
                layout.Controls.Add(buttons, 0, layout.RowCount++);
                layout.SetColumnSpan(buttons, 2);
                dialog.AcceptButton = save;
                dialog.CancelButton = cancel;
                if (dialog.ShowDialog() != DialogResult.OK) return null;

                string target = targets.SelectedIndex == 0 ? "" : targets.SelectedIndex <= choices.Length
                    ? choices[targets.SelectedIndex - 1].Id : current.TargetId;
                startWithWindows = run.Checked;
                return new TraySettings {
                    TargetId = target,
                    QualifiedId = String.Equals(target, current.TargetId, StringComparison.OrdinalIgnoreCase) ? current.QualifiedId : "",
                    Timer = timer.Checked, Minutes = (int)minutes.Value, ModifierChoice = modifiers.SelectedIndex,
                    Key = (Keys)keys.SelectedItem
                };
            }
        }

        private static void AddRow(TableLayoutPanel layout, string title, Control control)
        {
            int row = layout.RowCount++;
            var label = new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 12, 3) };
            control.AccessibleName = title;
            layout.Controls.Add(label, 0, row);
            layout.Controls.Add(control, 1, row);
        }
    }
}
