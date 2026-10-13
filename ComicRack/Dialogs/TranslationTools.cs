using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using cYo.Common.IO;
using cYo.Common.Windows.Forms;
using cYo.Common.Windows.Forms.Theme;
using cYo.Projects.ComicRack.Engine;
using cYo.Projects.ComicRack.Engine.Display.Forms;
using cYo.Projects.ComicRack.Engine.IO;
using cYo.Projects.ComicRack.Engine.IO.Provider;

namespace cYo.Projects.ComicRack.Viewer.Dialogs
{
	public enum TranslationCacheScope
	{
		Box,
		Page,
		Book
	}

	/// <summary>A big icon button with a caption: one of a group of choices, or a switch. Draws itself, so it
	/// reads well in the dark and light themes, and reports its description when the mouse is over it.</summary>
	public class ChoiceTile : Control
	{
		public enum IconKind
		{
			Box,
			Page,
			Book,
			Edits
		}

		private readonly IconKind kind;

		private bool hot;

		private bool isChecked;

		public string Caption { get; }

		public string Description { get; set; } = "";

		public bool IsSwitch { get; set; }

		public bool Checked
		{
			get
			{
				return isChecked;
			}
			set
			{
				if (isChecked != value)
				{
					isChecked = value;
					Invalidate();
					CheckedChanged?.Invoke(this, EventArgs.Empty);
				}
			}
		}

		public event EventHandler CheckedChanged;

		public event EventHandler Hovered;

		public ChoiceTile(IconKind kind, string caption)
		{
			this.kind = kind;
			Caption = caption;
			Size = new Size(104, 92);
			SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
			TabStop = true;
			Cursor = Cursors.Hand;
		}

		protected override void OnMouseEnter(EventArgs e)
		{
			base.OnMouseEnter(e);
			hot = true;
			Invalidate();
			Hovered?.Invoke(this, EventArgs.Empty);
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			base.OnMouseLeave(e);
			hot = false;
			Invalidate();
			Hovered?.Invoke(this, EventArgs.Empty);
		}

		public bool IsHot => hot || Focused;

		protected override void OnEnabledChanged(EventArgs e)
		{
			base.OnEnabledChanged(e);
			Cursor = Enabled ? Cursors.Hand : Cursors.Default;
			Invalidate();
		}

		protected override void OnGotFocus(EventArgs e)
		{
			base.OnGotFocus(e);
			Invalidate();
		}

		protected override void OnLostFocus(EventArgs e)
		{
			base.OnLostFocus(e);
			Invalidate();
		}

		protected override void OnClick(EventArgs e)
		{
			base.OnClick(e);
			if (!Enabled)
			{
				return;
			}
			Focus();
			Checked = IsSwitch ? !Checked : true;
		}

		protected override bool IsInputKey(Keys keyData)
		{
			return keyData == Keys.Space || base.IsInputKey(keyData);
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.KeyCode == Keys.Space && Enabled)
			{
				Checked = IsSwitch ? !Checked : true;
				e.Handled = true;
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			bool dark = ThemeManager.IsDarkModeEnabled;
			Color back = dark ? Color.FromArgb(40, 40, 42) : Color.FromArgb(246, 246, 246);
			Color fg = dark ? Color.FromArgb(225, 225, 225) : Color.FromArgb(40, 40, 40);
			Color accent = Color.FromArgb(255, 150, 20);
			g.Clear(dark ? Color.FromArgb(32, 32, 32) : SystemColors.Control);
			g.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
			if (!Enabled)
			{
				fg = Color.FromArgb(120, 120, 120);
			}
			using (GraphicsPath path = Rounded(r, 8))
			{
				Color fill = isChecked ? (dark ? Color.FromArgb(70, 52, 28) : Color.FromArgb(255, 235, 205)) : (hot ? (dark ? Color.FromArgb(56, 56, 60) : Color.FromArgb(232, 232, 236)) : back);
				using (SolidBrush brush = new SolidBrush(fill))
				{
					g.FillPath(brush, path);
				}
				using (Pen pen = new Pen(isChecked ? accent : (dark ? Color.FromArgb(90, 90, 94) : Color.FromArgb(190, 190, 195)), isChecked ? 2.2f : 1.2f))
				{
					g.DrawPath(pen, path);
				}
				if (Focused && Enabled)
				{
					using (Pen focus = new Pen(Color.FromArgb(130, accent), 1f) { DashStyle = DashStyle.Dot })
					{
						g.DrawPath(focus, Rounded(new Rectangle(4, 4, Width - 9, Height - 9), 6));
					}
				}
			}
			Color ink = isChecked && Enabled ? accent : fg;
			//The icon is drawn in a 40 x 40 square and scaled to the tile's height.
			float icon = Math.Min(40f, Height - 34f);
			float scale = icon / 40f;
			GraphicsState state = g.Save();
			g.TranslateTransform(Width / 2f - icon / 2f, 6f);
			g.ScaleTransform(scale, scale);
			DrawIcon(g, new RectangleF(0, 0, 40, 40), ink, fg, dark ? back : Color.White);
			g.Restore(state);
			using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
			using (SolidBrush text = new SolidBrush(fg))
			{
				float top = 6f + icon + 2f;
				g.DrawString(Caption, Font, text, new RectangleF(2, top, Width - 4, Height - top - 3), sf);
			}
		}

		private static GraphicsPath Rounded(Rectangle r, int radius)
		{
			GraphicsPath path = new GraphicsPath();
			int d = radius * 2;
			path.AddArc(r.Left, r.Top, d, d, 180, 90);
			path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
			path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
			path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
			path.CloseFigure();
			return path;
		}

		private void DrawIcon(Graphics g, RectangleF b, Color ink, Color fg, Color paper)
		{
			using (Pen pen = new Pen(ink, 2.2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
			using (SolidBrush fillPaper = new SolidBrush(paper))
			using (SolidBrush solid = new SolidBrush(ink))
			{
				switch (kind)
				{
				case IconKind.Box:
					{
						//A page corner with one text box outlined on it.
						RectangleF pg = new RectangleF(b.Left + 6, b.Top + 2, 28, 36);
						g.FillRectangle(fillPaper, pg);
						using (Pen thin = new Pen(Color.FromArgb(140, fg), 1.4f))
						{
							g.DrawRectangle(thin, pg.X, pg.Y, pg.Width, pg.Height);
						}
						RectangleF box = new RectangleF(pg.X + 5, pg.Y + 9, 18, 13);
						using (Pen dash = new Pen(ink, 2.2f) { DashStyle = DashStyle.Dash })
						{
							g.DrawRectangle(dash, box.X, box.Y, box.Width, box.Height);
						}
						g.DrawLine(pen, box.X + 3, box.Y + 5, box.Right - 3, box.Y + 5);
						g.DrawLine(pen, box.X + 3, box.Y + 9, box.Right - 7, box.Y + 9);
						break;
					}
				case IconKind.Page:
					{
						PointF[] sheet = { new PointF(b.Left + 7, b.Top + 2), new PointF(b.Right - 12, b.Top + 2), new PointF(b.Right - 5, b.Top + 9), new PointF(b.Right - 5, b.Bottom - 2), new PointF(b.Left + 7, b.Bottom - 2) };
						g.FillPolygon(fillPaper, sheet);
						g.DrawPolygon(pen, sheet);
						g.DrawLine(pen, b.Right - 12, b.Top + 2, b.Right - 12, b.Top + 9);
						g.DrawLine(pen, b.Right - 12, b.Top + 9, b.Right - 5, b.Top + 9);
						g.DrawLine(pen, b.Left + 12, b.Top + 18, b.Right - 10, b.Top + 18);
						g.DrawLine(pen, b.Left + 12, b.Top + 24, b.Right - 10, b.Top + 24);
						g.DrawLine(pen, b.Left + 12, b.Top + 30, b.Right - 16, b.Top + 30);
						break;
					}
				case IconKind.Book:
					{
						//An open book.
						PointF[] left = { new PointF(b.Left + 2, b.Top + 8), new PointF(b.Left + 20, b.Top + 12), new PointF(b.Left + 20, b.Bottom - 4), new PointF(b.Left + 2, b.Bottom - 8) };
						PointF[] right = { new PointF(b.Right - 2, b.Top + 8), new PointF(b.Right - 20, b.Top + 12), new PointF(b.Right - 20, b.Bottom - 4), new PointF(b.Right - 2, b.Bottom - 8) };
						g.FillPolygon(fillPaper, left);
						g.FillPolygon(fillPaper, right);
						g.DrawPolygon(pen, left);
						g.DrawPolygon(pen, right);
						g.DrawLine(pen, b.Left + 7, b.Top + 17, b.Left + 15, b.Top + 19);
						g.DrawLine(pen, b.Right - 7, b.Top + 17, b.Right - 15, b.Top + 19);
						g.DrawLine(pen, b.Left + 7, b.Top + 23, b.Left + 15, b.Top + 25);
						g.DrawLine(pen, b.Right - 7, b.Top + 23, b.Right - 15, b.Top + 25);
						break;
					}
				case IconKind.Edits:
					{
						//A pencil.
						PointF[] body = { new PointF(b.Left + 8, b.Bottom - 4), new PointF(b.Left + 10, b.Bottom - 12), new PointF(b.Right - 10, b.Top + 8), new PointF(b.Right - 4, b.Top + 14), new PointF(b.Left + 16, b.Bottom - 6) };
						g.FillPolygon(fillPaper, body);
						g.DrawPolygon(pen, body);
						g.DrawLine(pen, b.Left + 10, b.Bottom - 12, b.Left + 16, b.Bottom - 6);
						g.DrawLine(pen, b.Right - 14, b.Top + 12, b.Right - 8, b.Top + 18);
						g.FillEllipse(solid, b.Left + 5, b.Bottom - 7, 4, 4);
						break;
					}
				}
			}
		}
	}

	/// <summary>Asks which stored translations to delete: one box, the page on screen or the whole book.
	/// The choices are icon tiles; what each one does is shown in the pane at the bottom while the mouse is over it.</summary>
	public class TranslationCacheDialog : FormEx
	{
		private ChoiceTile tileBox;

		private ChoiceTile tilePage;

		private ChoiceTile tileBook;

		private ChoiceTile tileEdits;

		private Label lblInfo;

		private Label lblSummary;

		private readonly string pageText;

		public override UIComponent UIComponent => UIComponent.Content;

		public TranslationCacheScope Scope => tileBox.Checked ? TranslationCacheScope.Box : (tileBook.Checked ? TranslationCacheScope.Book : TranslationCacheScope.Page);

		/// <summary>Also delete the user's own changes (moved, resized and retyped boxes, boxes added by hand).</summary>
		public bool DeleteEdits => tileEdits.Checked;

		public TranslationCacheDialog(bool boxAvailable, int storedPages, int editedPages, string bookTitle)
		{
			Text = "Delete Translation Cache";
			AutoScaleDimensions = new SizeF(6f, 13f);
			AutoScaleMode = AutoScaleMode.Font;
			FormBorderStyle = FormBorderStyle.FixedDialog;
			StartPosition = FormStartPosition.CenterParent;
			MaximizeBox = false;
			MinimizeBox = false;
			ShowInTaskbar = false;
			ClientSize = new Size(520, 292);
			Color gray = ThemeManager.IsDarkModeEnabled ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);
			pageText = bookTitle;

			Controls.Add(new Label { Text = "Forget the translation so it is read and translated again:", Location = new Point(14, 12), Size = new Size(492, 18) });
			tileBox = new ChoiceTile(ChoiceTile.IconKind.Box, "This text box") { Location = new Point(14, 38), Enabled = boxAvailable, Name = "tileBox" };
			tilePage = new ChoiceTile(ChoiceTile.IconKind.Page, "This page") { Location = new Point(124, 38), Checked = true, Name = "tilePage" };
			tileBook = new ChoiceTile(ChoiceTile.IconKind.Book, "Whole book") { Location = new Point(234, 38), Name = "tileBook" };
			tileEdits = new ChoiceTile(ChoiceTile.IconKind.Edits, "My changes too") { Location = new Point(404, 38), IsSwitch = true, Name = "tileEdits" };
			Controls.AddRange(new Control[] { tileBox, tilePage, tileBook, tileEdits });
			Controls.Add(new Label { Text = "+", Location = new Point(366, 70), Size = new Size(30, 28), Font = new Font(Font.FontFamily, 14f, FontStyle.Bold), ForeColor = gray, TextAlign = ContentAlignment.MiddleCenter });

			tileBox.Description = boxAvailable
				? "This text box only: the selected box is read and translated again. Its position, size and style stay as you set them unless \"My changes too\" is on."
				: "This text box only: select a box in the editor first, then use Delete cache there.";
			tilePage.Description = "This page: the page on screen is read again by the vision model and translated again the next time it is shown.";
			tileBook.Description = "Whole book: every page (" + storedPages + " translated so far) is read again and translated again as you read it. This can take a while.";
			tileEdits.Description = "My changes too: also delete what you changed by hand (moved, resized, retyped and added boxes" + (editedPages > 0 ? ", on " + editedPages + " page" + (editedPages == 1 ? "" : "s") : "") + "). Off keeps them and applies them to the new translation.";

			Panel pane = new Panel { Location = new Point(14, 142), Size = new Size(492, 84), BorderStyle = BorderStyle.FixedSingle };
			lblInfo = new Label { Location = new Point(8, 6), Size = new Size(474, 72), Name = "lblInfo" };
			pane.Controls.Add(lblInfo);
			Controls.Add(pane);
			lblSummary = new Label { Location = new Point(14, 236), Size = new Size(492, 18), ForeColor = gray, AutoEllipsis = true, Name = "lblSummary" };
			Controls.Add(lblSummary);
			Button ok = new Button { Text = "Delete", Location = new Point(342, 258), Size = new Size(80, 26), DialogResult = DialogResult.OK, Name = "btOk" };
			Button cancel = new Button { Text = "Cancel", Location = new Point(428, 258), Size = new Size(80, 26), DialogResult = DialogResult.Cancel, Name = "btCancel" };
			Controls.AddRange(new Control[] { ok, cancel });
			AcceptButton = ok;
			CancelButton = cancel;

			foreach (ChoiceTile tile in new[] { tileBox, tilePage, tileBook, tileEdits })
			{
				ChoiceTile t = tile;
				t.Hovered += delegate { ShowDescription(); };
				t.CheckedChanged += delegate
				{
					if (!t.IsSwitch && t.Checked)
					{
						foreach (ChoiceTile other in new[] { tileBox, tilePage, tileBook })
						{
							if (other != t)
							{
								other.Checked = false;
							}
						}
					}
					ShowDescription();
				};
			}
			ShowDescription();
		}

		private ChoiceTile HotTile()
		{
			foreach (ChoiceTile tile in new[] { tileBox, tilePage, tileBook, tileEdits })
			{
				if (tile.ClientRectangle.Contains(tile.PointToClient(Cursor.Position)) && tile.Parent != null && tile.IsHot)
				{
					return tile;
				}
			}
			return null;
		}

		//The pane shows what the tile under the mouse does, or else what the current choice does.
		private void ShowDescription()
		{
			ChoiceTile hot = HotTile();
			ChoiceTile shown = hot ?? (tileBox.Checked ? tileBox : (tileBook.Checked ? tileBook : tilePage));
			lblInfo.Text = shown.Description;
			string scope = tileBox.Checked ? "this text box" : (tileBook.Checked ? "the whole book" : "the page on screen");
			lblSummary.Text = "Will delete: " + scope + (tileEdits.Checked ? ", and my own changes" : ", keeping my own changes") + "   (" + pageText + ")";
		}
	}

	/// <summary>Writes the translated book to a new file (original name + " - AI Translated").</summary>
	public class TranslationExportDialog : FormEx
	{
		private class FormatChoice
		{
			public string Label;

			public int FormatId;

			public bool Rar;

			public override string ToString()
			{
				return Label;
			}
		}

		private readonly ComicDisplayControl reader;

		private readonly ComicBook book;

		private readonly string rarPath;

		private ComboBox cboFormat;

		private TextBox txtFolder;

		private TextBox txtName;

		private CheckBox chkOverwrite;

		private CheckBox chkAdd;

		private Label lblTarget;

		private Label lblNote;

		private ProgressBar progress;

		private Label lblProgress;

		private Button btStart;

		private Button btClose;

		private TranslationExport running;

		private Thread worker;

		public override UIComponent UIComponent => UIComponent.Content;

		public TranslationExportDialog(ComicDisplayControl reader)
		{
			this.reader = reader;
			book = reader.Book?.Comic;
			rarPath = TranslationExport.FindRar();
			Text = "Export Translated Book";
			AutoScaleDimensions = new SizeF(6f, 13f);
			AutoScaleMode = AutoScaleMode.Font;
			FormBorderStyle = FormBorderStyle.FixedDialog;
			StartPosition = FormStartPosition.CenterParent;
			MaximizeBox = false;
			MinimizeBox = false;
			ShowInTaskbar = false;
			ClientSize = new Size(560, 372);
			Color gray = ThemeManager.IsDarkModeEnabled ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);

			Controls.Add(new Label { Text = "Book:", Location = new Point(14, 16), Size = new Size(70, 18) });
			Controls.Add(new Label { Text = book?.Caption ?? "", Location = new Point(90, 16), Size = new Size(456, 18), AutoEllipsis = true });
			Controls.Add(new Label { Text = "Save as:", Location = new Point(14, 48), Size = new Size(70, 18) });
			cboFormat = new ComboBoxEx { Location = new Point(90, 44), Size = new Size(456, 21), DropDownStyle = ComboBoxStyle.DropDownList, Name = "cboFormat" };
			Controls.Add(cboFormat);
			Controls.Add(new Label { Text = "Folder:", Location = new Point(14, 80), Size = new Size(70, 18) });
			txtFolder = new TextBox { Location = new Point(90, 77), Size = new Size(368, 20), Name = "txtFolder" };
			Button browse = new Button { Text = "Browse...", Location = new Point(464, 75), Size = new Size(82, 24), Name = "btBrowse" };
			Controls.AddRange(new Control[] { txtFolder, browse });
			Controls.Add(new Label { Text = "File name:", Location = new Point(14, 112), Size = new Size(70, 18) });
			txtName = new TextBox { Location = new Point(90, 109), Size = new Size(456, 20), Name = "txtName" };
			Controls.Add(txtName);
			lblTarget = new Label { Location = new Point(90, 134), Size = new Size(456, 32), ForeColor = gray, AutoEllipsis = true, Name = "lblTarget" };
			Controls.Add(lblTarget);
			chkOverwrite = new CheckBox { Text = "Replace the file if it already exists", Location = new Point(90, 170), Size = new Size(456, 22), Name = "chkOverwrite" };
			chkAdd = new CheckBox { Text = "Add the new book to my library", Location = new Point(90, 194), Size = new Size(456, 22), Name = "chkAdd" };
			Controls.AddRange(new Control[] { chkOverwrite, chkAdd });
			lblNote = new Label { Location = new Point(14, 226), Size = new Size(532, 58), ForeColor = gray, Name = "lblNote" };
			Controls.Add(lblNote);
			progress = new ProgressBar { Location = new Point(14, 294), Size = new Size(532, 18), Name = "progress" };
			lblProgress = new Label { Location = new Point(14, 316), Size = new Size(532, 18), Name = "lblProgress", AutoEllipsis = true };
			Controls.AddRange(new Control[] { progress, lblProgress });
			btStart = new Button { Text = "Export", Location = new Point(378, 336), Size = new Size(84, 28), Name = "btStart" };
			btClose = new Button { Text = "Close", Location = new Point(468, 336), Size = new Size(78, 28), Name = "btClose" };
			Controls.AddRange(new Control[] { btStart, btClose });
			AcceptButton = btStart;

			string current = book?.FileFormat ?? "";
			cboFormat.Items.Add(new FormatChoice { Label = "Same as the original" + (string.IsNullOrEmpty(current) ? "" : " (" + current + ")"), FormatId = 0 });
			cboFormat.Items.Add(new FormatChoice { Label = "CBZ (ZIP)", FormatId = KnownFileFormats.CBZ });
			cboFormat.Items.Add(new FormatChoice { Label = "CB7 (7-Zip)", FormatId = KnownFileFormats.CB7 });
			cboFormat.Items.Add(new FormatChoice { Label = "CBT (TAR)", FormatId = KnownFileFormats.CBT });
			if (rarPath != null)
			{
				cboFormat.Items.Add(new FormatChoice { Label = "CBR (RAR, packed with WinRAR)", FormatId = KnownFileFormats.CBZ, Rar = true });
			}
			//A book that is a CBR now is written as a CBR when WinRAR is there, since ComicRack cannot write RAR itself.
			bool wasRar = current.IndexOf("CBR", StringComparison.OrdinalIgnoreCase) >= 0 || current.IndexOf("RAR", StringComparison.OrdinalIgnoreCase) >= 0;
			cboFormat.SelectedIndex = (wasRar && rarPath != null) ? cboFormat.Items.Count - 1 : 0;
			txtFolder.Text = string.IsNullOrEmpty(book?.FilePath) ? "" : Path.GetDirectoryName(book.FilePath);
			txtName.Text = (book == null ? "Book" : book.FileName) + " - AI Translated";

			cboFormat.SelectedIndexChanged += delegate { UpdateTarget(); };
			txtFolder.TextChanged += delegate { UpdateTarget(); };
			txtName.TextChanged += delegate { UpdateTarget(); };
			browse.Click += delegate
			{
				using (FolderBrowserDialog dialog = new FolderBrowserDialog { SelectedPath = txtFolder.Text })
				{
					if (dialog.ShowDialog(this) == DialogResult.OK)
					{
						txtFolder.Text = dialog.SelectedPath;
					}
				}
			};
			btStart.Click += delegate { Start(); };
			btClose.Click += delegate
			{
				if (running != null)
				{
					running.Cancel();
					btClose.Enabled = false;
					lblProgress.Text = "Cancelling...";
				}
				else
				{
					Close();
				}
			};
			FormClosing += delegate (object s, FormClosingEventArgs e)
			{
				if (running != null)
				{
					e.Cancel = true;
					running.Cancel();
					lblProgress.Text = "Cancelling...";
				}
			};
			UpdateTarget();
		}

		private FormatChoice Choice => (FormatChoice)cboFormat.SelectedItem;

		private ExportSetting BuildSetting()
		{
			return new ExportSetting
			{
				Target = ExportTarget.NewFolder,
				TargetFolder = txtFolder.Text.Trim(),
				Naming = ExportNaming.Custom,
				CustomName = txtName.Text.Trim(),
				CustomNamingStart = 0,
				FormatId = Choice.FormatId,
				Overwrite = chkOverwrite.Checked,
				EmbedComicInfo = true
			};
		}

		private string PreviewPath()
		{
			if (book == null || string.IsNullOrWhiteSpace(txtName.Text) || string.IsNullOrWhiteSpace(txtFolder.Text))
			{
				return null;
			}
			try
			{
				string path = BuildSetting().GetTargetPath(book, 0);
				return Choice.Rar ? Path.ChangeExtension(path, ".cbr") : path;
			}
			catch (Exception)
			{
				return null;
			}
		}

		private void UpdateTarget()
		{
			string path = PreviewPath();
			lblTarget.Text = path ?? "Choose a folder and a file name.";
			btStart.Enabled = path != null && running == null;
			int stored = reader.TranslationService?.Store.TranslatedPageCount(reader.TranslationBookKey) ?? 0;
			int total = reader.Book?.ProviderPageCount ?? 0;
			string note = stored + " of " + total + " pages are translated already; the others are translated now, which can take a while. Your saved changes (moved, resized and added boxes) are included. The original book is not touched.";
			if (Choice != null && Choice.FormatId == 0 && !Choice.Rar && (book?.FileFormat ?? "").IndexOf("CBR", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				note += " ComicRack cannot write RAR files itself, so install WinRAR to export as CBR; otherwise the book is saved as CBZ.";
			}
			lblNote.Text = note;
		}

		private void Start()
		{
			if (book == null)
			{
				return;
			}
			TranslationExport export = new TranslationExport(book, BuildSetting(), reader.PagePool, reader.TranslationServerUrl, reader.TranslationService?.Store ?? new TranslationStore(TranslationStore.DefaultFolder))
			{
				RarPath = Choice.Rar ? rarPath : null
			};
			string target = export.TargetPath;
			if (File.Exists(target) && !chkOverwrite.Checked)
			{
				MessageBox.Show(this, "The file already exists:\n" + target + "\n\nTick \"Replace the file if it already exists\" or change the name.", "Export Translated Book", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}
			bool addToLibrary = chkAdd.Checked;
			running = export;
			btStart.Enabled = false;
			cboFormat.Enabled = txtFolder.Enabled = txtName.Enabled = chkOverwrite.Enabled = chkAdd.Enabled = false;
			btClose.Text = "Cancel";
			progress.Value = 0;
			lblProgress.Text = "Starting...";
			export.Progress += delegate (int done, int total)
			{
				BeginInvokeSafe(delegate
				{
					progress.Maximum = Math.Max(1, total);
					progress.Value = Math.Min(progress.Maximum, done);
					lblProgress.Text = "Translated page " + done + " of " + total;
				});
			};
			worker = new Thread(delegate ()
			{
				string result = null;
				string problem = null;
				bool cancelled = false;
				try
				{
					result = export.Run();
					if (addToLibrary)
					{
						Program.BookFactory.Create(result, CreateBookOption.AddToStorage);
					}
				}
				catch (OperationCanceledException)
				{
					cancelled = true;
				}
				catch (Exception ex)
				{
					problem = ex.Message;
				}
				BeginInvokeSafe(delegate
				{
					Finished(result, problem, cancelled);
				});
			})
			{
				IsBackground = true,
				Name = "Export translated book"
			};
			worker.Start();
		}

		private void Finished(string result, string problem, bool cancelled)
		{
			running = null;
			btClose.Enabled = true;
			btClose.Text = "Close";
			cboFormat.Enabled = txtFolder.Enabled = txtName.Enabled = chkOverwrite.Enabled = chkAdd.Enabled = true;
			if (result != null)
			{
				progress.Value = progress.Maximum;
				lblProgress.Text = "Done: " + result;
				MessageBox.Show(this, "The translated book was saved as:\n" + result, "Export Translated Book", MessageBoxButtons.OK, MessageBoxIcon.Information);
			}
			else if (cancelled)
			{
				lblProgress.Text = "Cancelled. Pages already translated are kept, so exporting again continues quickly.";
			}
			else
			{
				lblProgress.Text = "Failed: " + problem;
				MessageBox.Show(this, "The book could not be exported:\n" + problem, "Export Translated Book", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
			UpdateTarget();
		}

		private void BeginInvokeSafe(Action action)
		{
			if (IsDisposed || !IsHandleCreated)
			{
				return;
			}
			try
			{
				BeginInvoke(action);
			}
			catch (InvalidOperationException)
			{
			}
		}
	}

	/// <summary>The entry points the menus and the editor window use.</summary>
	public static class TranslationTools
	{
		/// <summary>Forgets translations: one box (through readAgain), the page on screen or the whole book.</summary>
		public static void ApplyCache(IWin32Window owner, ComicDisplayControl reader, TranslationCacheScope scope, bool deleteEdits, TranslationBox selected, Action<TranslationBox, bool> readAgain)
		{
			PageTranslationService service = reader.TranslationService;
			if (service == null || reader.Book == null)
			{
				MessageBox.Show(owner, "Turn on Translate Pages and open a book first.", "Delete Translation Cache", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}
			string bookKey = reader.TranslationBookKey;
			switch (scope)
			{
			case TranslationCacheScope.Box:
				readAgain?.Invoke(selected, deleteEdits);
				break;
			case TranslationCacheScope.Page:
				service.ForgetPage(reader.GetTranslationPageKey(reader.CurrentPage), bookKey, !deleteEdits);
				break;
			case TranslationCacheScope.Book:
				if (MessageBox.Show(owner, "Forget the translation of every page of this book" + (deleteEdits ? " and all your own changes" : "") + "?", "Delete Translation Cache", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
				{
					service.ForgetBook(bookKey, !deleteEdits);
				}
				break;
			}
		}

		public static void ShowCacheDialog(IWin32Window owner, ComicDisplayControl reader, TranslationBox selected, Action<TranslationBox, bool> readAgain)
		{
			PageTranslationService service = reader.TranslationService;
			if (service == null || reader.Book == null)
			{
				MessageBox.Show(owner, "Turn on Translate Pages and open a book first.", "Delete Translation Cache", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}
			string bookKey = reader.TranslationBookKey;
			int stored = service.Store.TranslatedPageCount(bookKey);
			int edited = service.Store.EditedPageCount(bookKey);
			using (TranslationCacheDialog dialog = new TranslationCacheDialog(selected != null && readAgain != null, stored, edited, reader.Book.Comic.Caption))
			{
				if (dialog.ShowDialog(owner) != DialogResult.OK)
				{
					return;
				}
				switch (dialog.Scope)
				{
				case TranslationCacheScope.Box:
					readAgain?.Invoke(selected, dialog.DeleteEdits);
					break;
				case TranslationCacheScope.Page:
					service.ForgetPage(reader.GetTranslationPageKey(reader.CurrentPage), bookKey, !dialog.DeleteEdits);
					break;
				case TranslationCacheScope.Book:
					if (MessageBox.Show(owner, "Forget the translation of every page of this book" + (dialog.DeleteEdits ? " and all your own changes" : "") + "?", "Delete Translation Cache", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
					{
						service.ForgetBook(bookKey, !dialog.DeleteEdits);
					}
					break;
				}
			}
		}

		public static void ShowExportDialog(IWin32Window owner, ComicDisplayControl reader)
		{
			if (reader.Book?.Comic == null)
			{
				MessageBox.Show(owner, "Open a book first.", "Export Translated Book", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}
			using (TranslationExportDialog dialog = new TranslationExportDialog(reader))
			{
				dialog.ShowDialog(owner);
			}
		}
	}
}
