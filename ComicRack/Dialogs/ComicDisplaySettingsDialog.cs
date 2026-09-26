using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using cYo.Common.Collections;
using cYo.Common.ComponentModel;
using cYo.Common.Drawing;
using cYo.Common.Localize;
using cYo.Common.Text;
using cYo.Common.Windows;
using cYo.Common.Windows.Forms;
using cYo.Common.Windows.Forms.Theme;
using cYo.Common.Windows.Forms.Theme.Resources;
using cYo.Projects.ComicRack.Engine.Display;
using cYo.Projects.ComicRack.Viewer.Config;

namespace cYo.Projects.ComicRack.Viewer.Dialogs
{
	public partial class ComicDisplaySettingsDialog : FormEx
	{
		private class TextureFileItem : ComboBoxSkinner.ComboBoxItem<string>
		{
			private Regex rxFormatCode = new Regex("\\s*\\[(?<code>[CSTZ])\\]\\z", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

			private bool failed;

			public string Name
			{
				get;
				set;
			}

			public bool IsCustom
			{
				get;
				set;
			}

			public string Default
			{
				get;
				set;
			}

			public ImageLayout Layout
			{
				get;
				set;
			}

			public Bitmap Sample
			{
				get;
				set;
			}

			public TextureFileItem(string file, bool custom = true)
				: base(file)
			{
				Default = TR.Default["None", "None"];
				base.IsOwnerDrawn = true;
				IsCustom = custom;
				if (!IsCustom)
				{
					ParseFileName(file);
				}
			}

			public TextureFileItem()
				: this(null, custom: false)
			{
			}

			public override string ToString()
			{
				if (string.IsNullOrEmpty(base.Item))
				{
					return Default;
				}
				if (!IsCustom)
				{
					return Name;
				}
				return Path.GetFileName(base.Item);
			}

			public override bool Equals(object obj)
			{
				if (obj is TextureFileItem)
				{
					return ((TextureFileItem)obj).Item == base.Item;
				}
				return false;
			}

			public override int GetHashCode()
			{
				return base.GetHashCode();
			}

			private void ParseFileName(string path)
			{
				if (string.IsNullOrEmpty(path))
				{
					return;
				}
				string text = Path.GetFileNameWithoutExtension(path);
				Layout = ImageLayout.Tile;
				Match match = rxFormatCode.Match(text);
				if (match.Success)
				{
					switch (match.Groups["code"].Value.ToUpper())
					{
						case "C":
							Layout = ImageLayout.Center;
							break;
						case "S":
							Layout = ImageLayout.Stretch;
							break;
						case "Z":
							Layout = ImageLayout.Zoom;
							break;
					}
					text = rxFormatCode.Replace(text, string.Empty);
				}
				Name = TR.Load("Textures")[text, text.PascalToSpaced()];
			}

			public override Size Measure(Graphics gr, Font font)
			{
				Size result = base.Measure(gr, font);
				result.Height *= 2;
				return result;
			}

			public override void Draw(Graphics gr, Rectangle bounds, Color foreColor, Font font)
			{
				int height = Measure(gr, font).Height;
				if (Sample == null && !failed)
				{
					try
					{
						using (Bitmap image = Image.FromFile(base.Item) as Bitmap)
						{
							Sample = image.CreateCopy(new Size(height * 2, height).ToRectangle(), alwaysTrueCopy: true);
						}
					}
					catch
					{
						failed = true;
					}
				}
				try
				{
					if (Sample != null)
					{
						gr.DrawImage(Sample, bounds.X, bounds.Y);
					}
				}
				catch (Exception)
				{
				}
				using (SolidBrush brush = new SolidBrush(foreColor))
				{
					using (StringFormat stringFormat = new StringFormat(StringFormatFlags.NoWrap)
					{
						Alignment = StringAlignment.Near,
						LineAlignment = StringAlignment.Center
					})
					{
						if (IsCustom)
						{
							stringFormat.Trimming = StringTrimming.EllipsisPath;
						}
						gr.DrawString(ToString(), font, brush, bounds.Pad(height * 2 + 4, 0), stringFormat);
					}
				}
			}
		}


		private DisplayWorkspace Workspace
		{
			get;
			set;
		}

		private Action<DisplayWorkspace> ApplyAction
		{
			get;
			set;
		}

		public override UIComponent UIComponent => UIComponent.Content;

		public ComicDisplaySettingsDialog()
		{
			LocalizeUtility.UpdateRightToLeft(this);
			InitializeComponent();
			this.RestorePosition();
			LocalizeUtility.Localize(this, null);
			new ComboBoxSkinner(cbPaperTexture);
			new ComboBoxSkinner(cbBackgroundTexture);
			labelBackgroundTexture.Location = labelBackgroundColor.Location;
			cbBackgroundTexture.Location = cpBackgroundColor.Location;
			btBrowseTexture.Top = cbBackgroundTexture.Top;
			cpBackgroundColor.FillKnownColors(includingSystem: false);
			cbBackgroundTexture.Items.Add(new TextureFileItem());
			string[] array = Program.LoadDefaultBackgroundTextures();
			foreach (string file in array)
			{
				cbBackgroundTexture.Items.Add(new TextureFileItem(file, custom: false));
			}
			cbPaperTexture.Items.Add(new TextureFileItem
			{
				Default = TR.Default["Default", "Default"]
			});
			string[] array2 = Program.LoadDefaultPaperTextures();
			foreach (string file2 in array2)
			{
				cbPaperTexture.Items.Add(new TextureFileItem(file2, custom: false));
			}
			LocalizeUtility.Localize(TR.Load(base.Name), cbPageTransition);
			LocalizeUtility.Localize(TR.Load(base.Name), cbBackgroundType);
			LocalizeUtility.Localize(TR.Load(base.Name), cbPaperLayout);
			LocalizeUtility.Localize(TR.Load(base.Name), cbTextureLayout);
			AddLibraryPanels();
		}

		//The two groups below are built directly in code rather than as Designer.cs markup:
		//simpler and safer for a one-off addition than hand-writing generated-style markup
		//with no way to see it rendered before it ships. They are their own two panels,
		//entirely independent of one another and of the Reader Background group above -
		//unlike that one, they apply to the library, not to the page while reading, and are
		//read and written straight from Program.Settings rather than through the workspace
		//(ws) that the rest of this dialog uses, since the library is not workspace-specific.

		private CheckBox chkLibBackgroundEnabled;

		private FlowLayoutPanel flowLayoutPanel2;

		private TextBox txtLibBackgroundPath;

		private ComboBox cbLibBackgroundLayout;

		private CheckBox chkLibShelfEnabled;

		private TextBox txtLibShelfPath;

		private TrackBarLite tbLibShelfPosition;

		private TrackBarLite tbLibShelfHeight;

		private TrackBarLite tbLibShelfDistance;

		private TrackBarLite tbLibShelfAngle;

		private TrackBarLite tbLibShelfBlur;

		private TrackBarLite tbLibShelfTransparency;

		private SimpleColorPicker cpLibShelfColor;

		private void AddLibraryPanels()
		{
			flowLayoutPanel2 = new FlowLayoutPanel
			{
				FlowDirection = FlowDirection.TopDown,
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				WrapContents = false,
				Location = new Point(flowLayoutPanel1.Right + 12, flowLayoutPanel1.Top)
			};

			GroupBox grpLibBackground = new GroupBox
			{
				Text = "Library Background",
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				Size = new Size(395, 115),
				Margin = new Padding(3, 3, 3, 3)
			};
			chkLibBackgroundEnabled = new CheckBox
			{
				Text = "Enable",
				AutoSize = true,
				Location = new Point(15, 22)
			};
			Label labelLibBackgroundPath = new Label
			{
				Text = "Image:",
				AutoSize = true,
				Location = new Point(15, 53),
				TextAlign = ContentAlignment.MiddleLeft
			};
			txtLibBackgroundPath = new TextBox
			{
				ReadOnly = true,
				Location = new Point(103, 50),
				Size = new Size(195, 20)
			};
			Button btLibBackgroundBrowse = new Button
			{
				Text = "Browse...",
				Location = new Point(304, 49),
				Size = new Size(76, 23)
			};
			btLibBackgroundBrowse.Click += delegate
			{
				string texture = GetTexture();
				if (!string.IsNullOrEmpty(texture))
				{
					txtLibBackgroundPath.Text = texture;
				}
			};
			Label labelLibBackgroundLayout = new Label
			{
				Text = "Layout:",
				AutoSize = true,
				Location = new Point(15, 82),
				TextAlign = ContentAlignment.MiddleLeft
			};
			cbLibBackgroundLayout = new ComboBox
			{
				DropDownStyle = ComboBoxStyle.DropDownList,
				Location = new Point(103, 79),
				Size = new Size(120, 21)
			};
			cbLibBackgroundLayout.Items.AddRange(new object[] { "Tile", "Stretch", "Center", "Zoom" });
			grpLibBackground.Controls.AddRange(new Control[] { chkLibBackgroundEnabled, labelLibBackgroundPath, txtLibBackgroundPath, btLibBackgroundBrowse, labelLibBackgroundLayout, cbLibBackgroundLayout });
			grpLibBackground.PerformLayout();

			//Smaller now that the shadow settings have their own panel below - just the shelf
			//itself: whether it is on, its picture, and the two controls for lining it up
			//against whatever cover size is set (see the class-level comment on ShelfOffset).
			GroupBox grpLibShelf = new GroupBox
			{
				Text = "Library Bookshelf",
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				Size = new Size(395, 145),
				Margin = new Padding(3, 3, 3, 3)
			};
			chkLibShelfEnabled = new CheckBox
			{
				Text = "Enable",
				AutoSize = true,
				Location = new Point(15, 22)
			};
			Label labelLibShelfPath = new Label
			{
				Text = "Image:",
				AutoSize = true,
				Location = new Point(15, 53),
				TextAlign = ContentAlignment.MiddleLeft
			};
			txtLibShelfPath = new TextBox
			{
				ReadOnly = true,
				Location = new Point(103, 50),
				Size = new Size(195, 20)
			};
			Button btLibShelfBrowse = new Button
			{
				Text = "Browse...",
				Location = new Point(304, 49),
				Size = new Size(76, 23)
			};
			btLibShelfBrowse.Click += delegate
			{
				string texture = GetTexture();
				if (!string.IsNullOrEmpty(texture))
				{
					txtLibShelfPath.Text = texture;
				}
			};
			Label labelLibShelfPosition = new Label
			{
				Text = "Shelf Position:",
				AutoSize = true,
				Location = new Point(15, 85),
				TextAlign = ContentAlignment.MiddleLeft
			};
			tbLibShelfPosition = new TrackBarLite
			{
				Minimum = -30,
				Maximum = 30,
				Location = new Point(150, 85),
				Size = new Size(230, 18)
			};
			tbLibShelfPosition.ValueChanged += delegate
			{
				toolTip.SetToolTip(tbLibShelfPosition, $"{tbLibShelfPosition.Value}px");
			};
			Label labelLibShelfHeight = new Label
			{
				Text = "Shelf Height:",
				AutoSize = true,
				Location = new Point(15, 112),
				TextAlign = ContentAlignment.MiddleLeft
			};
			tbLibShelfHeight = new TrackBarLite
			{
				Minimum = 2,
				Maximum = 150,
				Location = new Point(150, 112),
				Size = new Size(230, 18)
			};
			tbLibShelfHeight.ValueChanged += delegate
			{
				toolTip.SetToolTip(tbLibShelfHeight, $"{tbLibShelfHeight.Value}px");
			};
			grpLibShelf.Controls.AddRange(new Control[] { chkLibShelfEnabled, labelLibShelfPath, txtLibShelfPath, btLibShelfBrowse, labelLibShelfPosition, tbLibShelfPosition, labelLibShelfHeight, tbLibShelfHeight });
			grpLibShelf.PerformLayout();

			//Split out from Library Bookshelf on its own: the shadow a book casts onto
			//whichever shelf it is sitting on.
			GroupBox grpBookCoverShadow = new GroupBox
			{
				Text = "Book Cover Shadow",
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				Size = new Size(395, 165),
				Margin = new Padding(3, 3, 3, 3)
			};
			Label labelLibShelfDistance = new Label
			{
				Text = "Shadow Distance:",
				AutoSize = true,
				Location = new Point(15, 22),
				TextAlign = ContentAlignment.MiddleLeft
			};
			tbLibShelfDistance = new TrackBarLite
			{
				Minimum = 0,
				Maximum = 60,
				Location = new Point(150, 22),
				Size = new Size(230, 18)
			};
			tbLibShelfDistance.ValueChanged += delegate
			{
				toolTip.SetToolTip(tbLibShelfDistance, $"{tbLibShelfDistance.Value}px");
			};
			Label labelLibShelfAngle = new Label
			{
				Text = "Shadow Angle:",
				AutoSize = true,
				Location = new Point(15, 49),
				TextAlign = ContentAlignment.MiddleLeft
			};
			tbLibShelfAngle = new TrackBarLite
			{
				Minimum = -60,
				Maximum = 60,
				Location = new Point(150, 49),
				Size = new Size(230, 18)
			};
			tbLibShelfAngle.ValueChanged += delegate
			{
				toolTip.SetToolTip(tbLibShelfAngle, $"{tbLibShelfAngle.Value}\u00b0");
			};
			Label labelLibShelfBlur = new Label
			{
				Text = "Shadow Blur:",
				AutoSize = true,
				Location = new Point(15, 76),
				TextAlign = ContentAlignment.MiddleLeft
			};
			tbLibShelfBlur = new TrackBarLite
			{
				Minimum = 1,
				Maximum = 100,
				Location = new Point(150, 76),
				Size = new Size(230, 18)
			};
			tbLibShelfBlur.ValueChanged += delegate
			{
				toolTip.SetToolTip(tbLibShelfBlur, $"{tbLibShelfBlur.Value}px");
			};
			Label labelLibShelfTransparency = new Label
			{
				Text = "Shadow Transparency:",
				AutoSize = true,
				Location = new Point(15, 103),
				TextAlign = ContentAlignment.MiddleLeft
			};
			tbLibShelfTransparency = new TrackBarLite
			{
				Minimum = 0,
				Maximum = 100,
				Location = new Point(150, 103),
				Size = new Size(230, 18)
			};
			tbLibShelfTransparency.ValueChanged += PercentTrackbarValueChanged;
			Label labelLibShelfColor = new Label
			{
				Text = "Shadow Color:",
				AutoSize = true,
				Location = new Point(15, 134),
				TextAlign = ContentAlignment.MiddleLeft
			};
			cpLibShelfColor = new SimpleColorPicker
			{
				Location = new Point(150, 131),
				Size = new Size(150, 21)
			};
			cpLibShelfColor.FillKnownColors(includingSystem: false);
			grpBookCoverShadow.Controls.AddRange(new Control[] { labelLibShelfDistance, tbLibShelfDistance, labelLibShelfAngle, tbLibShelfAngle, labelLibShelfBlur, tbLibShelfBlur, labelLibShelfTransparency, tbLibShelfTransparency, labelLibShelfColor, cpLibShelfColor });
			grpBookCoverShadow.PerformLayout();

			flowLayoutPanel2.Controls.Add(grpLibBackground);
			flowLayoutPanel2.Controls.Add(grpLibShelf);
			flowLayoutPanel2.Controls.Add(grpBookCoverShadow);
			Controls.Add(flowLayoutPanel2);
		}

		/// <summary>
		/// Positions the second column and resizes the form and the OK/Apply/Cancel row to
		/// fit both columns. Deliberately not done from the constructor, alongside
		/// AddLibraryPanels which builds the controls themselves: at construction time the
		/// form has never actually been shown, and an AutoSize container's own preferred size
		/// is not reliably settled until it has actually appeared on screen - reading
		/// flowLayoutPanel2.Right/Bottom before that point was giving stale figures. Called
		/// from OnShown rather than OnLoad for the same reason: OnShown only fires once the
		/// form is genuinely visible, which is a stronger guarantee that layout has actually
		/// settled than the slightly earlier point OnLoad fires at.
		///
		/// panel1 (the OK/Apply/Cancel row) turned out to be a flowed child of
		/// flowLayoutPanel1 itself, not a sibling of it sitting independently on the form as
		/// it appeared to be - so its Anchor and Location were never in panel1's own hands to
		/// begin with; flowLayoutPanel1's own TopDown flow was placing it right under that
		/// column regardless of anything set on panel1, and flowLayoutPanel1's own height
		/// already counted panel1 once, before this method's own height calculation added it
		/// a second time on top. Moving panel1 out to be a direct child of the form - a true
		/// sibling of both columns - is what actually lets it be positioned independently.
		/// </summary>
		private void FinalizeLibraryPanelsLayout()
		{
			flowLayoutPanel1.Controls.Remove(panel1);
			Controls.Add(panel1);
			flowLayoutPanel1.PerformLayout();
			flowLayoutPanel2.PerformLayout();
			int contentRight = flowLayoutPanel2.Right;
			int contentBottom = Math.Max(flowLayoutPanel1.Bottom, flowLayoutPanel2.Bottom);
			AutoSize = false;
			panel1.Anchor = AnchorStyles.Top | AnchorStyles.Left;
			ClientSize = new Size(contentRight + 12, contentBottom + panel1.Height + 24);
			panel1.Location = new Point(contentRight - panel1.Width, contentBottom + 12);
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			FinalizeLibraryPanelsLayout();
		}

		private void UpdateLibraryPanels()
		{
			chkLibBackgroundEnabled.Checked = Program.Settings.LibraryBackgroundEnabled;
			txtLibBackgroundPath.Text = Program.Settings.LibraryBackgroundTexturePath;
			cbLibBackgroundLayout.SelectedIndex = (int)Program.Settings.LibraryBackgroundLayout;
			chkLibShelfEnabled.Checked = Program.Settings.LibraryShelfEnabled;
			txtLibShelfPath.Text = Program.Settings.LibraryShelfTexturePath;
			tbLibShelfPosition.Value = Program.Settings.LibraryShelfOffset;
			tbLibShelfHeight.Value = Program.Settings.LibraryShelfHeight;
			tbLibShelfDistance.Value = Program.Settings.LibraryShelfShadowDistance;
			tbLibShelfAngle.Value = Program.Settings.LibraryShelfShadowAngle;
			tbLibShelfBlur.Value = Program.Settings.LibraryShelfShadowBlur;
			tbLibShelfTransparency.Value = Program.Settings.LibraryShelfShadowTransparency;
			cpLibShelfColor.SelectedColor = Program.Settings.LibraryShelfShadowColor;
		}

		private void ApplyLibraryPanels()
		{
			Program.Settings.LibraryBackgroundEnabled = chkLibBackgroundEnabled.Checked;
			Program.Settings.LibraryBackgroundTexturePath = txtLibBackgroundPath.Text;
			Program.Settings.LibraryBackgroundLayout = (ImageLayout)cbLibBackgroundLayout.SelectedIndex;
			Program.Settings.LibraryShelfEnabled = chkLibShelfEnabled.Checked;
			Program.Settings.LibraryShelfTexturePath = txtLibShelfPath.Text;
			Program.Settings.LibraryShelfOffset = tbLibShelfPosition.Value;
			Program.Settings.LibraryShelfHeight = tbLibShelfHeight.Value;
			Program.Settings.LibraryShelfShadowDistance = tbLibShelfDistance.Value;
			Program.Settings.LibraryShelfShadowAngle = tbLibShelfAngle.Value;
			Program.Settings.LibraryShelfShadowBlur = tbLibShelfBlur.Value;
			Program.Settings.LibraryShelfShadowTransparency = tbLibShelfTransparency.Value;
			Program.Settings.LibraryShelfShadowColor = cpLibShelfColor.SelectedColor;
			cYo.Projects.ComicRack.Viewer.Views.ComicBrowserControl.RefreshAllListBackgrounds();
		}


		protected override void OnClosed(EventArgs e)
		{
			foreach (TextureFileItem item in cbPaperTexture.Items)
			{
				item.Sample.SafeDispose();
			}
			foreach (TextureFileItem item2 in cbBackgroundTexture.Items)
			{
				item2.Sample.SafeDispose();
			}
			base.OnClosed(e);
		}

		private void btBroweTexture_Click(object sender, EventArgs e)
		{
			string texture = GetTexture();
			if (!string.IsNullOrEmpty(texture))
			{
				SelectTextureFile(cbBackgroundTexture, texture);
			}
		}

		private void btBrowsePaper_Click(object sender, EventArgs e)
		{
			string texture = GetTexture();
			if (!string.IsNullOrEmpty(texture))
			{
				SelectTextureFile(cbPaperTexture, texture);
			}
		}

		private void cbPaperTexture_SelectedIndexChanged(object sender, EventArgs e)
		{
			Label label = labelPaperStrength;
			ComboBox comboBox = cbPaperLayout;
			bool flag2 = (tbPaperStrength.Visible = cbPaperTexture.SelectedIndex != 0);
			bool visible = (comboBox.Visible = flag2);
			label.Visible = visible;
			TextureFileItem textureFileItem = (TextureFileItem)cbPaperTexture.SelectedItem;
			if (!textureFileItem.IsCustom)
			{
				cbPaperLayout.SelectedIndex = (int)textureFileItem.Layout;
			}
			cbPaperLayout.Visible = textureFileItem.IsCustom;
		}

		private void cbBackgroundTexture_SelectedIndexChanged(object sender, EventArgs e)
		{
			TextureFileItem textureFileItem = (TextureFileItem)cbBackgroundTexture.SelectedItem;
			if (!textureFileItem.IsCustom)
			{
				cbTextureLayout.SelectedIndex = (int)textureFileItem.Layout;
			}
			cbTextureLayout.Visible = textureFileItem.IsCustom;
		}

		private void cbBackgroundType_SelectedIndexChanged(object sender, EventArgs e)
		{
			int selectedIndex = cbBackgroundType.SelectedIndex;
			Label label = labelBackgroundColor;
			bool visible = (cpBackgroundColor.Visible = selectedIndex == 1);
			label.Visible = visible;
			Label label2 = labelBackgroundTexture;
			ComboBox comboBox = cbBackgroundTexture;
			bool flag3 = (btBrowseTexture.Visible = selectedIndex == 2);
			visible = (comboBox.Visible = flag3);
			label2.Visible = visible;
			TextureFileItem textureFileItem = cbBackgroundTexture.SelectedItem as TextureFileItem;
			cbTextureLayout.Visible = selectedIndex == 2 && (textureFileItem?.IsCustom ?? true);
		}

		private void btApply_Click(object sender, EventArgs e)
		{
			if (ApplyAction != null)
			{
				Apply(Workspace);
				ApplyAction(Workspace);
			}
		}

		private void PercentTrackbarValueChanged(object sender, EventArgs e)
		{
			TrackBarLite trackBarLite = sender as TrackBarLite;
			toolTip.SetToolTip(trackBarLite, $"{trackBarLite.Value}%");
		}

		private void Apply(DisplayWorkspace ws)
		{
			ws.PageTransitionEffect = (PageTransitionEffect)cbPageTransition.SelectedIndex;
			ws.DrawRealisticPages = chkRealisticPages.Checked;
			ws.PageMargin = chkPageMargin.Checked;
			ws.PageMarginPercentWidth = (float)tbMargin.Value / 100f;
			ws.PageImageBackgroundMode = (ImageBackgroundMode)cbBackgroundType.SelectedIndex;
			ws.BackgroundColor = cpBackgroundColor.SelectedColorName;
			ws.BackgroundTexture = ((TextureFileItem)cbBackgroundTexture.SelectedItem).Item;
			ws.PaperTexture = ((TextureFileItem)cbPaperTexture.SelectedItem).Item;
			ws.PaperTextureStrength = (float)tbPaperStrength.Value / 100f;
			ws.PaperTextureLayout = (ImageLayout)cbPaperLayout.SelectedIndex;
			ws.BackgroundImageLayout = (ImageLayout)cbTextureLayout.SelectedIndex;
			ws.PageCurlAmount = (float)tbCurlAmount.Value / 100f;
			ws.PageCurlShadowStrength = (float)tbShadowStrength.Value / 100f;
			ws.PageCurlGrabArea = (float)tbGrabArea.Value / 100f;
			ws.PageCurlDuration = (int)nudTurnDuration.Value;
			ApplyLibraryPanels();
		}

		private void Update(DisplayWorkspace ws)
		{
			Workspace = ws;
			cbPageTransition.SelectedIndex = (int)ws.PageTransitionEffect;
			chkRealisticPages.Checked = ws.DrawRealisticPages;
			chkPageMargin.Checked = ws.PageMargin;
			tbMargin.Value = (int)(ws.PageMarginPercentWidth * 100f);
			cpBackgroundColor.SelectedColorName = ws.BackgroundColor;
			cbBackgroundType.SelectedIndex = (int)ws.PageImageBackgroundMode;
			SelectTextureFile(cbBackgroundTexture, ws.BackgroundTexture);
			SelectTextureFile(cbPaperTexture, ws.PaperTexture);
			tbPaperStrength.Value = (int)(ws.PaperTextureStrength * 100f);
			cbPaperLayout.SelectedIndex = (int)ws.PaperTextureLayout;
			cbTextureLayout.SelectedIndex = (int)ws.BackgroundImageLayout;
			tbCurlAmount.Value = (int)(ws.PageCurlAmount * 100f);
			tbShadowStrength.Value = (int)(ws.PageCurlShadowStrength * 100f);
			tbGrabArea.Value = (int)(ws.PageCurlGrabArea * 100f);
			nudTurnDuration.Value = ws.PageCurlDuration;
			UpdateLibraryPanels();
		}

		private void SelectTextureFile(ComboBox cb, string texture)
		{
			int num = cb.Items.OfType<TextureFileItem>().FindIndex((TextureFileItem i) => string.Equals(i.Item, texture, StringComparison.OrdinalIgnoreCase));
			if (num != -1)
			{
				cb.SelectedIndex = num;
				return;
			}
			TextureFileItem textureFileItem = cb.Items[cb.Items.Count - 1] as TextureFileItem;
			if (textureFileItem.IsCustom)
			{
				textureFileItem.Sample.SafeDispose();
				textureFileItem.Sample = null;
				cb.Items.Remove(textureFileItem);
			}
			textureFileItem = new TextureFileItem
			{
				Item = texture,
				IsCustom = true
			};
			cb.Items.Add(textureFileItem);
			cb.SelectedItem = textureFileItem;
		}

		private string GetTexture()
		{
			using (OpenFileDialog openFileDialog = new OpenFileDialog())
			{
				openFileDialog.Filter = TR.Load("FileFilter")["PageImageSave", "JPEG Image|*.jpg|Windows Bitmap Image|*.bmp|PNG Image|*.png|GIF Image|*.gif|TIFF Image|*.tif"];
				openFileDialog.CheckFileExists = true;
				if (openFileDialog.ShowDialog(this) == DialogResult.OK)
				{
					return openFileDialog.FileName;
				}
			}
			return null;
		}

		public static bool Show(IWin32Window parent, bool enableHardware, DisplayWorkspace ws, Action<DisplayWorkspace> apply)
		{
			using (ComicDisplaySettingsDialog comicDisplaySettingsDialog = new ComicDisplaySettingsDialog())
			{
				comicDisplaySettingsDialog.Update(ws);
				comicDisplaySettingsDialog.ApplyAction = apply;
				comicDisplaySettingsDialog.grpEffects.Visible = enableHardware;
				comicDisplaySettingsDialog.grpPageCurl.Visible = enableHardware;
				if (comicDisplaySettingsDialog.ShowDialog(parent) != DialogResult.OK)
				{
					return false;
				}
				comicDisplaySettingsDialog.Apply(ws);
				apply?.Invoke(ws);
				return true;
			}
		}

	}
}
