using System;
using System.Drawing;
using System.Windows.Forms;
using cYo.Projects.ComicRack.Viewer.Config;

namespace cYo.Projects.ComicRack.Viewer.Dialogs
{
	/// <summary>
	/// Lets the user choose what, if anything, fills the background of the library's book
	/// display panel: nothing, a plain texture (tiled and panning with the list, or fixed to
	/// the window the same as a normal background picture), or a shelf strip that repositions
	/// itself under each row of books - whatever their current cover size - with a soft shadow
	/// cast by each book onto it.
	///
	/// Built directly in code rather than as a separate Designer.cs file: the layout is simple
	/// enough that this is safer than hand-writing generated-style designer markup with no way
	/// to see it rendered before it ships.
	/// </summary>
	public class LibraryBackgroundDialog : Form
	{
		private ComboBox cbType;

		private TextBox txtPath;

		private Button btBrowse;

		private Label labelLayout;

		private ComboBox cbLayout;

		private Label labelHint;

		private Button btOk;

		private Button btCancel;

		public LibraryBackgroundType SelectedType { get; private set; }

		public string SelectedPath { get; private set; }

		public ImageLayout SelectedLayout { get; private set; }

		public LibraryBackgroundDialog(LibraryBackgroundType type, string path, ImageLayout layout)
		{
			Text = "Library Background";
			FormBorderStyle = FormBorderStyle.FixedDialog;
			MaximizeBox = false;
			MinimizeBox = false;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.CenterParent;
			ClientSize = new Size(430, 190);
			Font = SystemInformation.MenuFont ?? Font;

			Label labelType = new Label
			{
				Text = "Type:",
				Location = new Point(12, 15),
				Size = new Size(70, 20),
				TextAlign = ContentAlignment.MiddleLeft
			};
			cbType = new ComboBox
			{
				Location = new Point(90, 12),
				Size = new Size(150, 21),
				DropDownStyle = ComboBoxStyle.DropDownList
			};
			cbType.Items.AddRange(new object[] { "None", "Texture", "Bookshelf" });
			cbType.SelectedIndexChanged += delegate
			{
				UpdateEnabled();
			};

			Label labelPath = new Label
			{
				Text = "Image:",
				Location = new Point(12, 48),
				Size = new Size(70, 20),
				TextAlign = ContentAlignment.MiddleLeft
			};
			txtPath = new TextBox
			{
				Location = new Point(90, 45),
				Size = new Size(240, 21),
				ReadOnly = true
			};
			btBrowse = new Button
			{
				Text = "Browse...",
				Location = new Point(336, 44),
				Size = new Size(82, 23)
			};
			btBrowse.Click += BtBrowse_Click;

			labelLayout = new Label
			{
				Text = "Layout:",
				Location = new Point(12, 81),
				Size = new Size(70, 20),
				TextAlign = ContentAlignment.MiddleLeft
			};
			cbLayout = new ComboBox
			{
				Location = new Point(90, 78),
				Size = new Size(150, 21),
				DropDownStyle = ComboBoxStyle.DropDownList
			};
			cbLayout.Items.AddRange(new object[] { "Tile", "Stretch", "Center", "Zoom" });

			labelHint = new Label
			{
				Location = new Point(12, 112),
				Size = new Size(406, 40),
				AutoSize = false
			};

			btOk = new Button
			{
				Text = "OK",
				DialogResult = DialogResult.OK,
				Location = new Point(263, 155),
				Size = new Size(75, 25)
			};
			btOk.Click += BtOk_Click;

			btCancel = new Button
			{
				Text = "Cancel",
				DialogResult = DialogResult.Cancel,
				Location = new Point(343, 155),
				Size = new Size(75, 25)
			};

			Controls.AddRange(new Control[] { labelType, cbType, labelPath, txtPath, btBrowse, labelLayout, cbLayout, labelHint, btOk, btCancel });
			AcceptButton = btOk;
			CancelButton = btCancel;

			cbType.SelectedIndex = (int)type;
			txtPath.Text = path ?? string.Empty;
			cbLayout.SelectedIndex = Math.Max(0, cbLayout.Items.IndexOf(layout.ToString()));
			UpdateEnabled();
		}

		private void UpdateEnabled()
		{
			LibraryBackgroundType type = (LibraryBackgroundType)cbType.SelectedIndex;
			bool hasImage = type != LibraryBackgroundType.None;
			txtPath.Enabled = hasImage;
			btBrowse.Enabled = hasImage;
			bool isTexture = type == LibraryBackgroundType.Texture;
			labelLayout.Enabled = isTexture;
			cbLayout.Enabled = isTexture;
			labelHint.Text = (type == LibraryBackgroundType.Bookshelf) ? "The image is used as a shelf strip under each row of books, sized to fit however wide the panel is. It repositions itself to match whatever cover size is set, so a plain wood or shelf edge texture works best." : "Tile pans together with the list as you scroll. Stretch, Center and Zoom stay fixed to the window instead, the same as a normal background picture.";
		}

		private void BtBrowse_Click(object sender, EventArgs e)
		{
			using (OpenFileDialog openFileDialog = new OpenFileDialog
			{
				Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif",
				Title = "Choose Image"
			})
			{
				if (openFileDialog.ShowDialog(this) == DialogResult.OK)
				{
					txtPath.Text = openFileDialog.FileName;
				}
			}
		}

		private void BtOk_Click(object sender, EventArgs e)
		{
			LibraryBackgroundType type = (LibraryBackgroundType)cbType.SelectedIndex;
			if (type != LibraryBackgroundType.None && string.IsNullOrEmpty(txtPath.Text))
			{
				MessageBox.Show(this, "Choose an image, or set Type to None.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
				DialogResult = DialogResult.None;
				return;
			}
			SelectedType = type;
			SelectedPath = (type == LibraryBackgroundType.None) ? string.Empty : txtPath.Text;
			SelectedLayout = (ImageLayout)Enum.Parse(typeof(ImageLayout), (string)cbLayout.SelectedItem ?? "Tile");
		}
	}
}
