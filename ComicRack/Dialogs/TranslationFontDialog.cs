using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using cYo.Common.Windows.Forms;
using cYo.Common.Windows.Forms.Theme;
using cYo.Projects.ComicRack.Engine.Display.Forms;

namespace cYo.Projects.ComicRack.Viewer.Dialogs
{
	/// <summary>
	/// Shows which fonts are like the lettering of a text box: the closest ones on this computer and in Google Fonts, each with the
	/// box's own text set in it, a button to install it into the translator (and use it for the lettering) and a link to its page.
	/// Places the helper does not search by itself (dafont, Font Squirrel, WhatTheFont) open in the user's own browser.
	/// </summary>
	public class TranslationFontDialog : FormEx
	{
		/// <summary>What the search looks at.</summary>
		public enum Scope
		{
			Box,
			Page,
			Book
		}

		private readonly Action<Scope, bool, Action<FontIdentification, string>> search;

		private RadioButton optBox;

		private RadioButton optPage;

		private RadioButton optBook;

		private Label lblGroup;

		private ComboBox cboGroup;

		private readonly Action<Action<List<FontFile>, string>> listFonts;

		private readonly string sample;

		private readonly Action<string, bool, Action<string, string>> install;

		private Label lblStatus;

		private PictureBox picCrop;

		private Panel rows;

		private Panel links;

		private Label lblFolder;

		private Button btSearch;

		private Button btCopy;

		private ProgressBar progress;

		private FontIdentification last;

		private bool closed;

		public override UIComponent UIComponent => UIComponent.Content;

		public TranslationFontDialog(Action<Scope, bool, Action<FontIdentification, string>> search, Action<string, bool, Action<string, string>> install, bool boxSelected, Action<Action<List<FontFile>, string>> listFonts = null, string sample = "")
		{
			this.listFonts = listFonts;
			this.sample = sample;
			this.search = search;
			this.install = install;
			Text = "Match Font";
			AutoScaleDimensions = new SizeF(6f, 13f);
			AutoScaleMode = AutoScaleMode.Font;
			StartPosition = FormStartPosition.CenterParent;
			ShowInTaskbar = false;
			MinimumSize = new Size(760, 560);
			ClientSize = new Size(800, 700);
			Color gray = ThemeManager.IsDarkModeEnabled ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);

			Controls.Add(new Label { Text = "Match the font of:", Location = new Point(14, 12), Size = new Size(110, 18) });
			optBox = new RadioButton { Text = "The selected box", Location = new Point(126, 9), Size = new Size(130, 22), Enabled = boxSelected, Checked = boxSelected, Name = "optBox" };
			optPage = new RadioButton { Text = "This page (all its boxes)", Location = new Point(262, 9), Size = new Size(180, 22), Checked = !boxSelected, Name = "optPage" };
			optBook = new RadioButton { Text = "Whole book (a sample of its translated pages)", Location = new Point(448, 9), Size = new Size(330, 22), Name = "optBook" };
			Controls.AddRange(new Control[] { optBox, optPage, optBook });
			new ToolTip().SetToolTip(optBook, "Looks at the lettering of up to 12 translated pages spread over the book, to find the one font that suits it best");
			picCrop = new PictureBox { Location = new Point(14, 32), Size = new Size(772, 96), SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
			Controls.Add(picCrop);
			lblStatus = new Label { Location = new Point(14, 134), Size = new Size(772, 30), ForeColor = gray, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Name = "lblStatus" };
			Controls.Add(lblStatus);
			lblGroup = new Label { Text = "Lettering style:", Location = new Point(14, 170), Size = new Size(92, 18), Visible = false };
			cboGroup = new ComboBox { Location = new Point(110, 166), Size = new Size(420, 21), DropDownStyle = ComboBoxStyle.DropDownList, Visible = false, Name = "cboGroup" };
			Controls.AddRange(new Control[] { lblGroup, cboGroup });
			new ToolTip().SetToolTip(cboGroup, "The boxes have more than one kind of lettering (capitals and mixed case); each kind is matched on its own");
			cboGroup.SelectedIndexChanged += delegate { ShowGroup(); };
			progress = new ProgressBar { Location = new Point(14, 192), Size = new Size(772, 8), Style = ProgressBarStyle.Marquee, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Visible = false };
			Controls.Add(progress);

			rows = new Panel { Location = new Point(14, 204), Size = new Size(772, 272), AutoScroll = true, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Name = "rows" };
			Controls.Add(rows);

			Label other = new Label { Text = "Other places to look (they do not allow automated searching, so these open in your browser):", Location = new Point(14, 484), Size = new Size(772, 18), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
			Controls.Add(other);
			links = new Panel { Location = new Point(14, 504), Size = new Size(772, 64), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Name = "links" };
			Controls.Add(links);
			lblFolder = new Label { Location = new Point(14, 574), Size = new Size(772, 52), ForeColor = gray, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Name = "lblFolder" };
			Controls.Add(lblFolder);

			btCopy = new Button { Text = "Copy lettering picture", Location = new Point(14, 662), Size = new Size(150, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Enabled = false, Name = "btCopy" };
			Button btFolder = new Button { Text = "Open candidates folder", Location = new Point(170, 662), Size = new Size(150, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Name = "btFolder" };
			btSearch = new Button { Text = "Match again", Location = new Point(606, 662), Size = new Size(88, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Name = "btSearch" };
			Button close = new Button { Text = "Close", Location = new Point(700, 662), Size = new Size(86, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel, Name = "btClose" };
			Button btOwn = new Button { Text = "Use a font of my own...", Location = new Point(326, 662), Size = new Size(170, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Enabled = listFonts != null, Name = "btOwn" };
			new ToolTip().SetToolTip(btOwn, "Pick any font installed on this computer (or in the translator's fonts folder) and use it for the lettering");
			btOwn.Click += delegate { PickOwnFont(); };
			Controls.AddRange(new Control[] { btCopy, btFolder, btOwn, btSearch, close });
			CancelButton = close;
			new ToolTip().SetToolTip(btCopy, "Copies the lettering as a picture, to paste into WhatTheFont or Font Squirrel's Matcherator");
			new ToolTip().SetToolTip(btFolder, "Fonts you download from dafont or Font Squirrel can be dropped here (zip files too); Match again then includes them");

			btCopy.Click += delegate
			{
				try
				{
					if (last?.Crop != null)
					{
						using (MemoryStream ms = new MemoryStream(last.Crop))
						using (Bitmap bmp = new Bitmap(ms))
						{
							Clipboard.SetImage(new Bitmap(bmp));
						}
						SetStatus("The lettering picture is on the clipboard.");
					}
				}
				catch (Exception ex)
				{
					SetStatus("The picture could not be copied: " + ex.Message);
				}
			};
			btFolder.Click += delegate
			{
				string folder = last?.CandidatesFolder;
				if (string.IsNullOrEmpty(folder))
				{
					SetStatus("The folder is known after the first search.");
					return;
				}
				try
				{
					Directory.CreateDirectory(folder);
					Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
				}
				catch (Exception ex)
				{
					SetStatus("The folder could not be opened: " + ex.Message);
				}
			};
			btSearch.Click += delegate { Search(); };
			Shown += delegate { Search(); };
			FormClosed += delegate { closed = true; };
		}

		private void SetStatus(string text)
		{
			lblStatus.Text = text;
		}

		private void Search()
		{
			btSearch.Enabled = false;
			progress.Visible = true;
			rows.Controls.Clear();
			SetStatus((optBook.Checked ? "Reading a sample of the book's pages, then searching" : (optPage.Checked ? "Searching for this page's lettering in" : "Searching")) + " the fonts on this computer and Google Fonts. The first search builds a small cache of font shapes and can take several minutes; later ones take seconds.");
			Scope scope = optBook.Checked ? Scope.Book : (optPage.Checked ? Scope.Page : Scope.Box);
			picCrop.Image = null;
			search(scope, true, delegate (FontIdentification result, string error)
			{
				if (closed || IsDisposed)
				{
					return;
				}
				try
				{
					BeginInvoke(new Action(delegate { ShowResult(result, error); }));
				}
				catch (Exception)
				{
				}
			});
		}

		private void ShowResult(FontIdentification result, string error)
		{
			progress.Visible = false;
			btSearch.Enabled = true;
			rows.Controls.Clear();
			if (result == null)
			{
				SetStatus("The search failed: " + error);
				return;
			}
			last = result;
			btCopy.Enabled = result.Crop != null;
			if (result.Crop != null)
			{
				try
				{
					using (MemoryStream ms = new MemoryStream(result.Crop))
					{
						picCrop.Image = new Bitmap(ms);
					}
				}
				catch (Exception)
				{
				}
			}
			if (!string.IsNullOrEmpty(result.Error) || result.Matches.Count == 0)
			{
				SetStatus(string.IsNullOrEmpty(result.Error) ? "No font could be compared." : result.Error);
			}
			else
			{
				SetStatus("The closest fonts, best first (" + result.Compared + " compared). A score is how near the lettering looks, not a certainty: handwriting and hand-drawn comic lettering often has no exact font. " + string.Join(" ", result.Notes));
			}
			cboGroup.Items.Clear();
			bool several = result.Groups.Count > 1;
			lblGroup.Visible = several;
			cboGroup.Visible = several;
			if (several)
			{
				foreach (FontGroup group in result.Groups)
				{
					cboGroup.Items.Add(group.Label);
				}
				cboGroup.SelectedIndex = 0;     //shows the first group
			}
			else
			{
				ShowMatches(result.Matches);
			}
			links.Controls.Clear();
			int x = 0, row = 0;
			foreach (FontLink link in result.Links)
			{
				FontLink l = link;
				Button b = new Button { Text = l.Name, Size = new Size(Math.Max(110, l.Name.Length * 7 + 20), 26), Location = new Point(x, row * 30) };
				if (x + b.Width > 760)
				{
					x = 0;
					row++;
					b.Location = new Point(x, row * 30);
				}
				x += b.Width + 6;
				new ToolTip().SetToolTip(b, l.About);
				b.Click += delegate { OpenPage(l.Url); };
				links.Controls.Add(b);
			}
			lblFolder.Text = "Fonts you download from dafont, Font Squirrel and similar sites can be dropped (zip files too) in:\r\n" + result.CandidatesFolder + "\r\nThen press Match again to have them compared with this lettering. Check each font's licence before using it.";
		}

		private void ShowGroup()
		{
			if (last == null || cboGroup.SelectedIndex < 0 || cboGroup.SelectedIndex >= last.Groups.Count)
			{
				return;
			}
			FontGroup group = last.Groups[cboGroup.SelectedIndex];
			if (group.Crop != null)
			{
				try
				{
					using (MemoryStream ms = new MemoryStream(group.Crop))
					{
						picCrop.Image = new Bitmap(ms);
					}
				}
				catch (Exception)
				{
				}
			}
			ShowMatches(group.Matches);
		}

		private void ShowMatches(List<FontMatch> matches)
		{
			rows.Controls.Clear();
			int y = 4;
			foreach (FontMatch match in matches)
			{
				rows.Controls.Add(MakeRow(match, y));
				y += 86;
			}
		}

		/// <summary>Lets the user pick any font the translator can use and use it for the lettering.</summary>
		private void PickOwnFont()
		{
			if (listFonts == null)
			{
				return;
			}
			using (FontPickerDialog picker = new FontPickerDialog(listFonts, install, sample))
			{
				picker.ShowDialog(this);
				if (!string.IsNullOrEmpty(picker.UsedFile))
				{
					SetStatus(picker.UsedFile + " is now the lettering font. Pages are drawn with it from now on.");
				}
			}
		}

		private Control MakeRow(FontMatch match, int y)
		{
			Panel row = new Panel { Location = new Point(2, y), Size = new Size(742, 80), BorderStyle = BorderStyle.FixedSingle };
			PictureBox preview = new PictureBox { Location = new Point(6, 6), Size = new Size(410, 46), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
			if (match.Preview != null)
			{
				try
				{
					using (MemoryStream ms = new MemoryStream(match.Preview))
					{
						preview.Image = new Bitmap(ms);
					}
				}
				catch (Exception)
				{
				}
			}
			string where = match.Source == "google" ? "Google Fonts (open licence)" + (string.IsNullOrEmpty(match.Category) ? "" : ", " + match.Category) + (match.InstalledCopy ? "; also on this computer" : "") : (match.Source == "installed" ? "installed on this computer" : (match.Source == "candidates" ? "from your candidates folder" : "in the translator's fonts folder"));
			Label name = new Label { Text = match.Family + "   " + match.Score + "%", Location = new Point(424, 6), Size = new Size(310, 20), Font = new Font(Font, FontStyle.Bold), AutoEllipsis = true };
			Label sub = new Label { Text = where, Location = new Point(424, 26), Size = new Size(310, 16), ForeColor = ThemeManager.IsDarkModeEnabled ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110), AutoEllipsis = true };
			Button use = new Button { Text = "Use for lettering", Location = new Point(6, 52), Size = new Size(130, 24) };
			Button get = new Button { Text = match.Source == "google" ? "Install to translator" : "Copy to translator", Location = new Point(142, 52), Size = new Size(140, 24) };
			Button page = new Button { Text = "Font page", Location = new Point(288, 52), Size = new Size(90, 24), Visible = !string.IsNullOrEmpty(match.Url) };
			Label done = new Label { Location = new Point(384, 55), Size = new Size(350, 18), AutoEllipsis = true };
			use.Click += delegate { Install(match, true, use, get, done); };
			get.Click += delegate { Install(match, false, use, get, done); };
			page.Click += delegate { OpenPage(match.Url); };
			row.Controls.AddRange(new Control[] { preview, name, sub, use, get, page, done });
			return row;
		}

		private void Install(FontMatch match, bool useIt, Button use, Button get, Label done)
		{
			use.Enabled = false;
			get.Enabled = false;
			done.Text = "Working...";
			install(match.Family, useIt, delegate (string file, string error)
			{
				if (closed || IsDisposed)
				{
					return;
				}
				try
				{
					BeginInvoke(new Action(delegate
					{
						use.Enabled = true;
						get.Enabled = true;
						done.Text = error != null ? "Failed: " + error : (useIt ? file + " is now the lettering font. New pages use it." : file + " was added to the translator's fonts.");
					}));
				}
				catch (Exception)
				{
				}
			});
		}

		private void OpenPage(string url)
		{
			//Only web pages, and only ones the helper named.
			if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			try
			{
				Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
			}
			catch (Exception ex)
			{
				SetStatus("The page could not be opened: " + ex.Message);
			}
		}
	}

	/// <summary>
	/// Every font file on this computer the translator can use, with a filter and a preview of the box's own text; the chosen one is
	/// copied into the translator's fonts folder and, when asked, used for the lettering.
	/// </summary>
	public class FontPickerDialog : FormEx
	{
		private readonly Action<string, bool, Action<string, string>> install;

		private readonly string sample;

		private List<FontFile> all = new List<FontFile>();

		private TextBox txtFilter;

		private ListBox lstFonts;

		private PictureBox picPreview;

		private Label lblInfo;

		private Button btUse;

		private Button btCopy;

		private System.Drawing.Text.PrivateFontCollection loaded;

		/// <summary>The file name of the font that was made the lettering font, if one was.</summary>
		public string UsedFile { get; private set; }

		public override UIComponent UIComponent => UIComponent.Content;

		public FontPickerDialog(Action<Action<List<FontFile>, string>> listFonts, Action<string, bool, Action<string, string>> install, string sample)
		{
			this.install = install;
			this.sample = string.IsNullOrWhiteSpace(sample) ? "The quick brown fox jumps over the lazy dog" : string.Join(" ", sample.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
			Text = "Use a Font of My Own";
			AutoScaleDimensions = new SizeF(6f, 13f);
			AutoScaleMode = AutoScaleMode.Font;
			StartPosition = FormStartPosition.CenterParent;
			ShowInTaskbar = false;
			MinimizeBox = false;
			ClientSize = new Size(620, 520);
			MinimumSize = new Size(560, 460);
			Controls.Add(new Label { Text = "Find:", Location = new Point(14, 15), Size = new Size(40, 18) });
			txtFilter = new TextBox { Location = new Point(56, 12), Size = new Size(550, 21), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Name = "txtFilter" };
			lstFonts = new ListBox { Location = new Point(14, 40), Size = new Size(592, 260), IntegralHeight = false, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Name = "lstFonts" };
			picPreview = new PictureBox { Location = new Point(14, 308), Size = new Size(592, 100), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Name = "picPreview" };
			lblInfo = new Label { Location = new Point(14, 414), Size = new Size(592, 52), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Text = "Reading the fonts on this computer...", Name = "lblInfo" };
			btUse = new Button { Text = "Use for lettering", Location = new Point(14, 482), Size = new Size(130, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Enabled = false, Name = "btUse" };
			btCopy = new Button { Text = "Copy to translator", Location = new Point(150, 482), Size = new Size(130, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Enabled = false, Name = "btCopy" };
			Button close = new Button { Text = "Close", Location = new Point(520, 482), Size = new Size(86, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel };
			Controls.AddRange(new Control[] { txtFilter, lstFonts, picPreview, lblInfo, btUse, btCopy, close });
			CancelButton = close;
			new ToolTip().SetToolTip(btCopy, "Copies the font into the translator's fonts folder, so it shows in the translator's settings, without using it yet");
			txtFilter.TextChanged += delegate { Fill(); };
			lstFonts.SelectedIndexChanged += delegate { Preview(); };
			lstFonts.DoubleClick += delegate { Install(true); };
			btUse.Click += delegate { Install(true); };
			btCopy.Click += delegate { Install(false); };
			FormClosed += delegate { loaded?.Dispose(); };
			Shown += delegate
			{
				listFonts(delegate (List<FontFile> fonts, string error)
				{
					try
					{
						BeginInvoke(new Action(delegate
						{
							if (fonts == null)
							{
								lblInfo.Text = "The fonts could not be read: " + error;
								return;
							}
							all = fonts;
							Fill();
							lblInfo.Text = fonts.Count + " fonts the translator can use. Type part of a name to find one.";
						}));
					}
					catch (Exception)
					{
					}
				});
			};
		}

		private void Fill()
		{
			//"shadows into" finds "ShadowsIntoLight-Regular": spaces, hyphens and case do not count
			string want = Squeeze(txtFilter.Text);
			lstFonts.BeginUpdate();
			lstFonts.Items.Clear();
			foreach (FontFile f in all.Where((FontFile f) => want.Length == 0 || Squeeze(f.Name).Contains(want)))
			{
				lstFonts.Items.Add(f.Name);
			}
			lstFonts.EndUpdate();
			if (lstFonts.Items.Count > 0)
			{
				lstFonts.SelectedIndex = 0;
			}
			else
			{
				Preview();
			}
		}

		private static string Squeeze(string s)
		{
			return new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
		}

		private FontFile Chosen => lstFonts.SelectedItem == null ? null : all.FirstOrDefault((FontFile f) => f.Name == (string)lstFonts.SelectedItem);

		private void Preview()
		{
			FontFile f = Chosen;
			btUse.Enabled = f != null;
			btCopy.Enabled = f != null;
			Image old = picPreview.Image;
			picPreview.Image = null;
			old?.Dispose();
			loaded?.Dispose();
			loaded = null;
			if (f == null)
			{
				return;
			}
			Bitmap bmp = new Bitmap(Math.Max(10, picPreview.ClientSize.Width), Math.Max(10, picPreview.ClientSize.Height));
			using (Graphics g = Graphics.FromImage(bmp))
			{
				g.Clear(Color.White);
				g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
				try
				{
					loaded = new System.Drawing.Text.PrivateFontCollection();
					loaded.AddFontFile(f.Path);
					using (Font font = new Font(loaded.Families[0], 26f, FontStyle.Regular, GraphicsUnit.Pixel))
					{
						g.DrawString(sample, font, Brushes.Black, new RectangleF(6, 6, bmp.Width - 12, bmp.Height - 12));
					}
				}
				catch (Exception)
				{
					using (Font font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12f))
					{
						g.DrawString("No preview for this font here (the translator may still be able to use it).", font, Brushes.Gray, 6, 6);
					}
				}
			}
			picPreview.Image = bmp;
			lblInfo.Text = f.Name + "   (" + (f.Source == "installed" ? "installed on this computer" : (f.Source == "candidates" ? "from the candidates folder" : "in the translator's fonts folder")) + ")\r\n" + f.Path;
		}

		private void Install(bool use)
		{
			FontFile f = Chosen;
			if (f == null)
			{
				return;
			}
			btUse.Enabled = false;
			btCopy.Enabled = false;
			lblInfo.Text = "Working...";
			install(f.Name, use, delegate (string file, string error)
			{
				try
				{
					BeginInvoke(new Action(delegate
					{
						btUse.Enabled = true;
						btCopy.Enabled = true;
						if (error != null)
						{
							lblInfo.Text = "Failed: " + error;
							return;
						}
						if (use)
						{
							UsedFile = file;
							lblInfo.Text = file + " is now the lettering font. Pages are drawn with it from now on.";
						}
						else
						{
							lblInfo.Text = file + " was copied into the translator's fonts folder.";
						}
					}));
				}
				catch (Exception)
				{
				}
			});
		}
	}
}
