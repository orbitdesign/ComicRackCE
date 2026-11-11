using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ComicRackLite
{
	/// <summary>Small dialogs in the reader's theme: a text to read (keys, about, notes) and "Go to page".</summary>
	internal static class Dialogs
	{
		[DllImport("user32.dll")]
		private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref int lParam);

		private const int EM_SETTABSTOPS = 0xCB;

		public static void ShowText(IWin32Window owner, string title, string text, bool dark, int width = 520, int height = 440)
		{
			using (Form f = NewForm(title, width, height, dark))
			{
				TextBox box = new TextBox
				{
					Multiline = true,
					ReadOnly = true,
					ScrollBars = ScrollBars.Vertical,
					Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
					Location = new Point(12, 12),
					Size = new Size(width - 24, height - 64),
					Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
					Font = new Font("Segoe UI", 9.5f),
					TabStop = false
				};
				Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(84, 28), Location = new Point(width - 96, height - 42), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
				f.Controls.Add(box);
				f.Controls.Add(ok);
				f.AcceptButton = ok;
				f.CancelButton = ok;
				Theme.Apply(f, dark);
				if (text.Contains("\t"))
				{
					//a table: the first column 110 dialog units (about 27 characters) wide
					box.HandleCreated += delegate
					{
						int stop = 110;
						SendMessage(box.Handle, EM_SETTABSTOPS, (IntPtr)1, ref stop);
					};
				}
				f.Shown += delegate
				{
					box.SelectionLength = 0;
					ok.Focus();
				};
				f.ShowDialog(owner);
			}
		}

		/// <summary>The page number chosen (0 based), or -1.</summary>
		public static int AskPage(IWin32Window owner, int current, int count, bool dark)
		{
			using (Form f = NewForm("Go to Page", 300, 130, dark))
			{
				Label label = new Label { Text = "Page (1 to " + count + "):", Location = new Point(12, 18), AutoSize = true };
				NumericUpDown number = new NumericUpDown { Minimum = 1, Maximum = Math.Max(1, count), Value = Math.Min(Math.Max(1, current + 1), Math.Max(1, count)), Location = new Point(150, 15), Width = 130 };
				Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(84, 28), Location = new Point(106, 88) };
				Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(84, 28), Location = new Point(196, 88) };
				f.Controls.AddRange(new Control[] { label, number, ok, cancel });
				f.AcceptButton = ok;
				f.CancelButton = cancel;
				Theme.Apply(f, dark);
				f.Shown += delegate
				{
					number.Select(0, number.Text.Length);
					number.Focus();
				};
				return f.ShowDialog(owner) == DialogResult.OK ? (int)number.Value - 1 : -1;
			}
		}

		private static Form NewForm(string title, int width, int height, bool dark)
		{
			Form f = new Form
			{
				Text = title,
				ClientSize = new Size(width, height),
				FormBorderStyle = FormBorderStyle.FixedDialog,
				StartPosition = FormStartPosition.CenterParent,
				MinimizeBox = false,
				MaximizeBox = false,
				ShowInTaskbar = false,
				Font = new Font("Segoe UI", 9f),
				AutoScaleMode = AutoScaleMode.None
			};
			f.HandleCreated += delegate
			{
				Theme.SetTitleBar(f.Handle, dark);
			};
			return f;
		}
	}
}
