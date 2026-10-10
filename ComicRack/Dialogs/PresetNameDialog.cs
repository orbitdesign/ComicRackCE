using System;
using System.Drawing;
using System.Windows.Forms;
using cYo.Common.Windows.Forms;
using cYo.Common.Windows.Forms.Theme;

namespace cYo.Projects.ComicRack.Viewer.Dialogs
{
	/// <summary>
	/// A small "type a name" box for saving a display preset. Built in code rather than as
	/// designer markup because it is just a label, a text box and two buttons.
	/// </summary>
	public class PresetNameDialog : FormEx
	{
		private readonly TextBox txtName;

		private readonly Button btOK;

		public override UIComponent UIComponent => UIComponent.Content;

		public string PresetName => txtName.Text.Trim();

		public PresetNameDialog(string title, string name)
		{
			Text = title;
			FormBorderStyle = FormBorderStyle.FixedDialog;
			StartPosition = FormStartPosition.CenterParent;
			MaximizeBox = false;
			MinimizeBox = false;
			ShowInTaskbar = false;
			ClientSize = new Size(340, 100);

			Label label = new Label
			{
				Text = "Preset name:",
				AutoSize = true,
				Location = new Point(12, 14)
			};
			txtName = new TextBox
			{
				Location = new Point(12, 34),
				Size = new Size(316, 20),
				MaxLength = 60,
				Text = name ?? string.Empty
			};
			btOK = new Button
			{
				Text = "OK",
				DialogResult = DialogResult.OK,
				Location = new Point(172, 66),
				Size = new Size(75, 23)
			};
			Button btCancel = new Button
			{
				Text = "Cancel",
				DialogResult = DialogResult.Cancel,
				Location = new Point(253, 66),
				Size = new Size(75, 23)
			};
			Controls.AddRange(new Control[] { label, txtName, btOK, btCancel });
			AcceptButton = btOK;
			CancelButton = btCancel;

			btOK.Enabled = PresetName.Length > 0;
			txtName.TextChanged += delegate
			{
				btOK.Enabled = PresetName.Length > 0;
			};
			Shown += delegate
			{
				txtName.Focus();
				txtName.SelectAll();
			};
		}
	}
}
