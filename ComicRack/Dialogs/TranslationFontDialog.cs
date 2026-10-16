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

		//Sets the font of one kind of lettering (family, capitals?, whole book?, done) for this page or the whole book.
		private readonly Action<string, bool, bool, Action<string, string>> applyFont;

		private readonly Func<bool, StyleFonts> currentFonts;

		//Asks the helper what the search is doing, and stops it.
		private readonly Action<Action<FontProgress>> getProgress;

		private readonly Action cancelSearch;

		//The page drawn with a font (file name, which lettering: 0 capitals 1 mixed 2 both, answer) and the page as it is now.
		private readonly Action<string, int, Action<byte[], string>> previewFont;

		private readonly Func<byte[]> currentPicture;

		private readonly Func<byte[]> originalPicture;

		private FontPreviewPanel previewPanel;

		private Panel selectedRow;

		private System.Windows.Forms.Timer watch;

		//the whole search, as the user waited for it (reading the pages, the helper's search, the answer)
		private readonly System.Diagnostics.Stopwatch searchTime = new System.Diagnostics.Stopwatch();

		private Button btStop;

		private bool searching;

		//The kind of lettering of the selected box (or of most of the page) when the answer does not say.
		private readonly bool defaultCaps;

		private RadioButton optApplyPage;

		private RadioButton optApplyBook;

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

		public TranslationFontDialog(Action<Scope, bool, Action<FontIdentification, string>> search, Action<string, bool, Action<string, string>> install, bool boxSelected, Action<Action<List<FontFile>, string>> listFonts = null, string sample = "",Action<string, bool, bool, Action<string, string>> applyFont = null, Func<bool, StyleFonts> currentFonts = null, bool defaultCaps = false,Action<Action<FontProgress>> getProgress = null, Action cancelSearch = null,Action<string, int, Action<byte[], string>> previewFont = null, Func<byte[]> currentPicture = null, Func<byte[]> originalPicture = null)
		{
			this.previewFont = previewFont;
			this.currentPicture = currentPicture;
			this.originalPicture = originalPicture;
			this.getProgress = getProgress;
			this.cancelSearch = cancelSearch;
			this.applyFont = applyFont;
			this.currentFonts = currentFonts;
			this.defaultCaps = defaultCaps;
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
			ClientSize = previewFont != null ? new Size(1290, 700) : new Size(800, 700);
			WindowMemory.Restore(this, () => Program.Settings.TranslatorMatchFontSize, previewFont != null ? new Size(1100, 560) : new Size(700, 560), Screen.FromPoint(Cursor.Position).WorkingArea);
			WindowMemory.Watch(this, (string size) => Program.Settings.TranslatorMatchFontSize = size);
			Color gray = ThemeManager.IsDarkModeEnabled ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);

			Controls.Add(new Label { Text = "Match the font of:", Location = new Point(14, 12), Size = new Size(110, 18) });
			optBox = new RadioButton { Text = "The selected box", Location = new Point(126, 9), Size = new Size(130, 22), Enabled = boxSelected, Checked = boxSelected, Name = "optBox" };
			optPage = new RadioButton { Text = "This page (all its boxes)", Location = new Point(262, 9), Size = new Size(180, 22), Checked = !boxSelected, Name = "optPage" };
			optBook = new RadioButton { Text = "Whole book (a sample of its translated pages)", Location = new Point(448, 9), Size = new Size(330, 22), Name = "optBook" };
			Controls.AddRange(new Control[] { optBox, optPage, optBook });
			new ToolTip().SetToolTip(optBook, "Looks at the lettering of up to 12 translated pages spread over the book, to find the one font that suits it best");
			picCrop = new PictureBox { Location = new Point(14, 32), Size = new Size(772, 96), SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
			Controls.Add(picCrop);
			lblStatus = new Label { Location = new Point(14, 134), Size = new Size(772, 50), ForeColor = gray, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Name = "lblStatus" };
			Controls.Add(lblStatus);
			lblGroup = new Label { Text = "Lettering style:", Location = new Point(14, 190), Size = new Size(92, 18), Visible = false };
			cboGroup = new ComboBox { Location = new Point(110, 186), Size = new Size(420, 21), DropDownStyle = ComboBoxStyle.DropDownList, Visible = false, Name = "cboGroup" };
			Controls.AddRange(new Control[] { lblGroup, cboGroup });
			new ToolTip().SetToolTip(cboGroup, "The boxes have more than one kind of lettering (capitals and mixed case); each kind is matched on its own");
			cboGroup.SelectedIndexChanged += delegate { ShowGroup(); };
			progress = new ProgressBar { Location = new Point(14, 212), Size = new Size(772, 8), Style = ProgressBarStyle.Marquee, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Visible = false };
			Controls.Add(progress);

			rows = new Panel { Location = new Point(14, 224), Size = new Size(772, 252), AutoScroll = true, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Name = "rows" };
			rows.MouseEnter += delegate { if (!rows.Focused) { rows.Focus(); } };
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
			Controls.Add(new Label { Text = "Apply fonts to:", Location = new Point(14, 636), Size = new Size(92, 18), Anchor = AnchorStyles.Bottom | AnchorStyles.Left });
			optApplyPage = new RadioButton { Text = "This page", Location = new Point(110, 633), Size = new Size(90, 22), Checked = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Name = "optApplyPage" };
			optApplyBook = new RadioButton { Text = "This book (all its pages)", Location = new Point(204, 633), Size = new Size(200, 22), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Name = "optApplyBook" };
			Controls.AddRange(new Control[] { optApplyPage, optApplyBook });
			new ToolTip().SetToolTip(optApplyBook, "The font is used for this kind of lettering on every page of this book (a page's own choice still comes first)");
			btStop = new Button { Text = "Stop", Location = new Point(512, 662), Size = new Size(88, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Visible = false, Name = "btStop" };
			new ToolTip().SetToolTip(btStop, "Stops the search (what was already found is kept in the cache, so the next one is faster)");
			btStop.Click += delegate
			{
				btStop.Enabled = false;
				SetStatus("Stopping the search...");
				cancelSearch?.Invoke();
			};
			Controls.Add(btStop);
			watch = new System.Windows.Forms.Timer { Interval = 1200 };
			watch.Tick += delegate { AskProgress(); };
			FormClosed += delegate { watch.Stop(); watch.Dispose(); };
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
			if (previewFont != null)
			{
				//All the controls so far go into a panel on the left, the preview takes the rest.
				//a window smaller than its content scrolls instead of cutting it off
				Panel left = new Panel { Dock = DockStyle.Left, Width = 800, Name = "pnlLeft", AutoScroll = true, AutoScrollMinSize = new Size(780, 660) };
				foreach (Control c in Controls.Cast<Control>().ToList())
				{
					left.Controls.Add(c);
				}
				previewPanel = new FontPreviewPanel(previewFont, currentPicture, originalPicture) { Dock = DockStyle.Fill, Name = "previewPanel" };
				Controls.Add(previewPanel);
				Controls.Add(left);
				MinimumSize = new Size(1100, 560);
			}
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
			searching = true;
			btStop.Visible = cancelSearch != null;
			btStop.Enabled = true;
			watch.Start();
			searchTime.Restart();
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

		/// <summary>"48 s", "2 min 05 s".</summary>
		internal static string Duration(double seconds)
		{
			int s = (int)Math.Round(Math.Max(0, seconds));
			return s < 60 ? s + " s" : (s / 60) + " min " + (s % 60).ToString("00") + " s";
		}

		/// <summary>While a search runs: shows which step it is on, how far it has got and how long it has taken.</summary>
		private void AskProgress()
		{
			if (!searching || getProgress == null)
			{
				return;
			}
			getProgress(delegate (FontProgress info)
			{
				if (info == null || closed || IsDisposed)
				{
					return;
				}
				try
				{
					BeginInvoke(new Action(delegate
					{
						if (!searching || !info.Busy || string.IsNullOrEmpty(info.Stage))
						{
							return;
						}
						string count = info.Total > 0 ? " (" + info.Done + " of " + info.Total + ")" : "";
						SetStatus(info.Stage + count + " - " + Duration(info.Seconds) + " so far. The first search builds a font cache and takes a few minutes; later ones take seconds.");
					}));
				}
				catch (Exception)
				{
				}
			});
		}

		private void ShowResult(FontIdentification result, string error)
		{
			searching = false;
			watch.Stop();
			searchTime.Stop();
			btStop.Visible = false;
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
				string counted = result.Compared + " fonts compared" + (result.LookedCloser > 0 ? ", the best " + result.LookedCloser + " looked at closely" : "");
				SetStatus("The closest fonts, best first (" + counted + "; the search took " + Duration(searchTime.Elapsed.TotalSeconds) + "). A score is how near the lettering looks, not a certainty: handwriting and hand-drawn comic lettering often has no exact font. " + string.Join(" ", result.Notes));
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

		/// <summary>Marks a row as the chosen one and draws the page with its font in the preview.</summary>
		private void SelectRow(Panel row, FontMatch match)
		{
			if (selectedRow != null && !selectedRow.IsDisposed)
			{
				selectedRow.BackColor = SystemColors.Control;
			}
			selectedRow = row;
			row.BackColor = ThemeManager.IsDarkModeEnabled ? Color.FromArgb(50, 70, 100) : Color.FromArgb(210, 225, 245);
			previewPanel.Kind = CurrentCaps() ? 0 : 1;
			previewPanel.ShowFont(string.IsNullOrEmpty(match.File) ? match.Family + ".ttf" : match.File);
		}

		private void ShowMatches(List<FontMatch> matches)
		{
			selectedRow = null;
			rows.Controls.Clear();
			int y = 4;
			foreach (FontMatch match in matches)
			{
				rows.Controls.Add(MakeRow(match, y));
				y += 86;
			}
			rows.AutoScrollMinSize = new Size(0, y + 6);
			rows.AutoScrollPosition = new Point(0, 0);
		}

		/// <summary>Lets the user pick any font the translator can use and use it for the lettering.</summary>
		private void PickOwnFont()
		{
			if (listFonts == null)
			{
				return;
			}
			using (FontPickerDialog picker = new FontPickerDialog(listFonts, install, sample, applyFont, currentFonts, optApplyBook.Checked, previewFont, currentPicture, originalPicture))
			{
				picker.ShowDialog(this);
				if (!string.IsNullOrEmpty(picker.UsedFile))
				{
					SetStatus(picker.UsedFile);
				}
			}
		}

		private Control MakeRow(FontMatch match, int y)
		{
			Panel row = new Panel { Location = new Point(2, y), Size = new Size(742, 80), BorderStyle = BorderStyle.FixedSingle, Tag = match };
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
			bool caps = CurrentCaps();
			Button use = new Button { Text = caps ? "Use for capitals" : "Use for mixed case", Location = new Point(6, 52), Size = new Size(130, 24), Enabled = applyFont != null };
			new ToolTip().SetToolTip(use, "Use this font for the " + (caps ? "capital-letter" : "mixed-case") + " lettering of this page, or of the whole book (see Apply fonts to)");
			Button get = new Button { Text = match.Source == "google" ? "Install to translator" : "Copy to translator", Location = new Point(142, 52), Size = new Size(140, 24) };
			Button page = new Button { Text = "Font page", Location = new Point(288, 52), Size = new Size(90, 24), Visible = !string.IsNullOrEmpty(match.Url) };
			Label done = new Label { Location = new Point(384, 55), Size = new Size(350, 18), AutoEllipsis = true };
			use.Click += delegate { Apply(match.Family, caps, use, get, done); };
			get.Click += delegate { Install(match, false, use, get, done); };
			page.Click += delegate { OpenPage(match.Url); };
			row.Controls.AddRange(new Control[] { preview, name, sub, use, get, page, done });
			//the wheel scrolls the list wherever the pointer is on it (a panel only gets the wheel when it has the focus)
			foreach (Control c in new Control[] { row, preview, name, sub, use, get, page, done })
			{
				c.MouseEnter += delegate { if (!rows.Focused) { rows.Focus(); } };
			}
			if (previewPanel != null)
			{
				//a click on the row (not on its buttons) chooses it for the preview; so does pressing a button
				EventHandler choose = delegate { SelectRow(row, match); };
				foreach (Control c in new Control[] { row, preview, name, sub, done })
				{
					c.Click += choose;
				}
				use.Click += choose;
				get.Click += choose;
			}
			return row;
		}

		/// <summary>The kind of lettering the shown fonts were matched for.</summary>
		private bool CurrentCaps()
		{
			if (last != null && cboGroup.Visible && cboGroup.SelectedIndex >= 0 && cboGroup.SelectedIndex < last.Groups.Count)
			{
				return last.Groups[cboGroup.SelectedIndex].Label.StartsWith("Capital", StringComparison.OrdinalIgnoreCase);
			}
			if (last != null && !string.IsNullOrEmpty(last.Label))
			{
				return last.Label.StartsWith("Capital", StringComparison.OrdinalIgnoreCase);
			}
			return defaultCaps;
		}

		private void Apply(string family, bool caps, Button use, Button get, Label done)
		{
			if (applyFont == null)
			{
				return;
			}
			bool book = optApplyBook.Checked;
			use.Enabled = false;
			get.Enabled = false;
			done.Text = "Working...";
			applyFont(family, caps, book, delegate (string file, string error)
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
						done.Text = error != null ? "Failed: " + error : file + " is now the " + (caps ? "capitals" : "mixed-case") + " font of " + (book ? "this book" : "this page") + ".";
					}));
				}
				catch (Exception)
				{
				}
			});
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
	/// Every font file on this computer the translator can use, with a filter and a preview of the text. The chosen font can be set for the
	/// capital-letter lettering and for the mixed-case lettering, of this page or of the whole book.
	/// </summary>
	/// <summary>Remembers the size a window was given, and gives it back the next time (kept inside the screen).</summary>
	internal static class WindowMemory
	{
		public static void Restore(Form form, Func<string> saved, Size minimum, Rectangle area)
		{
			try
			{
				string[] parts = (saved() ?? "").Split(',');
				if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
				{
					w = Math.Max(minimum.Width, Math.Min(w, area.Width - 20));
					h = Math.Max(minimum.Height, Math.Min(h, area.Height - 40));
					form.ClientSize = new Size(w, h);
				}
			}
			catch (Exception)
			{
				//no settings (a test, or a damaged value): the window keeps its usual size
			}
		}

		/// <summary>Saves the size when the user has finished resizing and when the window closes (not while it is maximized or minimized).</summary>
		public static void Watch(Form form, Action<string> save)
		{
			Action write = delegate
			{
				try
				{
					if (form.WindowState == FormWindowState.Normal && form.ClientSize.Width > 100 && form.ClientSize.Height > 100)
					{
						save(form.ClientSize.Width + "," + form.ClientSize.Height);
					}
				}
				catch (Exception)
				{
				}
			};
			form.ResizeEnd += delegate { write(); };
			form.FormClosing += delegate { write(); };
		}
	}

	public class FontPickerDialog : FormEx
	{
		private readonly Action<string, bool, Action<string, string>> install;

		private readonly Action<string, bool, bool, Action<string, string>> applyFont;

		private readonly Func<bool, StyleFonts> currentFonts;

		private readonly string sample;

		private List<FontFile> all = new List<FontFile>();

		private TextBox txtFilter;

		private ListBox lstFonts;

		private PictureBox picPreview;

		private Label lblInfo;

		private Label lblCaps;

		private Label lblMixed;

		private Button btCaps;

		private Button btMixed;

		private Button btClearCaps;

		private Button btClearMixed;

		private RadioButton optPage;

		private RadioButton optBook;

		private Button btCopy;

		private FontPreviewPanel previewPanel;

		private readonly Action<string, int, Action<byte[], string>> previewFont;

		private readonly Func<byte[]> currentPicture;

		private readonly Func<byte[]> originalPicture;

		private System.Drawing.Text.PrivateFontCollection loaded;

		/// <summary>What was last set, to report in the window that opened this one.</summary>
		public string UsedFile { get; private set; }

		public override UIComponent UIComponent => UIComponent.Content;

		public FontPickerDialog(Action<Action<List<FontFile>, string>> listFonts, Action<string, bool, Action<string, string>> install, string sample, Action<string, bool, bool, Action<string, string>> applyFont = null,Func<bool, StyleFonts> currentFonts = null, bool wholeBook = false,Action<string, int, Action<byte[], string>> previewFont = null, Func<byte[]> currentPicture = null, Func<byte[]> originalPicture = null)
		{
			this.previewFont = previewFont;
			this.currentPicture = currentPicture;
			this.originalPicture = originalPicture;
			this.install = install;
			this.applyFont = applyFont;
			this.currentFonts = currentFonts;
			this.sample = string.IsNullOrWhiteSpace(sample) ? "The quick brown fox jumps over the lazy dog" : string.Join(" ", sample.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
			Text = "Use a Font of My Own";
			AutoScaleDimensions = new SizeF(6f, 13f);
			AutoScaleMode = AutoScaleMode.Font;
			StartPosition = FormStartPosition.CenterParent;
			ShowInTaskbar = false;
			MinimizeBox = false;
			ClientSize = previewFont != null ? new Size(1130, 590) : new Size(620, 590);
			MinimumSize = new Size(560, 520);
			WindowMemory.Restore(this, () => Program.Settings.TranslatorFontPickerSize, previewFont != null ? new Size(900, 520) : new Size(540, 520), Screen.FromPoint(Cursor.Position).WorkingArea);
			WindowMemory.Watch(this, (string size) => Program.Settings.TranslatorFontPickerSize = size);
			Color gray = ThemeManager.IsDarkModeEnabled ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);
			Controls.Add(new Label { Text = "Find:", Location = new Point(14, 15), Size = new Size(40, 18) });
			txtFilter = new TextBox { Location = new Point(56, 12), Size = new Size(550, 21), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Name = "txtFilter" };
			lstFonts = new ListBox { Location = new Point(14, 40), Size = new Size(592, 230), IntegralHeight = false, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Name = "lstFonts" };
			picPreview = new PictureBox { Location = new Point(14, 278), Size = new Size(592, 90), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Name = "picPreview" };
			lblInfo = new Label { Location = new Point(14, 372), Size = new Size(592, 34), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Text = "Reading the fonts on this computer...", Name = "lblInfo" };
			Controls.AddRange(new Control[] { txtFilter, lstFonts, picPreview, lblInfo });

			//the two kinds of lettering, and where the choice applies
			Controls.Add(new Label { Text = "Capital letters:", Location = new Point(14, 418), Size = new Size(96, 18), Anchor = AnchorStyles.Bottom | AnchorStyles.Left });
			lblCaps = new Label { Location = new Point(112, 418), Size = new Size(250, 18), ForeColor = gray, AutoEllipsis = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Name = "lblCaps" };
			btCaps = new Button { Text = "Use selected font", Location = new Point(368, 413), Size = new Size(130, 26), Enabled = false, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Name = "btCaps" };
			btClearCaps = new Button { Text = "Default", Location = new Point(504, 413), Size = new Size(102, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Name = "btClearCaps" };
			Controls.Add(new Label { Text = "Mixed case:", Location = new Point(14, 452), Size = new Size(96, 18), Anchor = AnchorStyles.Bottom | AnchorStyles.Left });
			lblMixed = new Label { Location = new Point(112, 452), Size = new Size(250, 18), ForeColor = gray, AutoEllipsis = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Name = "lblMixed" };
			btMixed = new Button { Text = "Use selected font", Location = new Point(368, 447), Size = new Size(130, 26), Enabled = false, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Name = "btMixed" };
			btClearMixed = new Button { Text = "Default", Location = new Point(504, 447), Size = new Size(102, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Name = "btClearMixed" };
			Controls.Add(new Label { Text = "Apply to:", Location = new Point(14, 488), Size = new Size(96, 18), Anchor = AnchorStyles.Bottom | AnchorStyles.Left });
			optPage = new RadioButton { Text = "This page", Location = new Point(112, 485), Size = new Size(90, 22), Checked = !wholeBook, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Name = "optPage" };
			optBook = new RadioButton { Text = "This book (all its pages)", Location = new Point(206, 485), Size = new Size(200, 22), Checked = wholeBook, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Name = "optBook" };
			Controls.AddRange(new Control[] { lblCaps, btCaps, btClearCaps, lblMixed, btMixed, btClearMixed, optPage, optBook });
			new ToolTip().SetToolTip(btClearCaps, "Go back to the translator's own font for capital-letter lettering");
			new ToolTip().SetToolTip(btClearMixed, "Go back to the translator's own font for mixed-case lettering");
			new ToolTip().SetToolTip(optBook, "Every page of this book (a page's own choice still comes first)");

			btCopy = new Button { Text = "Copy to translator", Location = new Point(14, 552), Size = new Size(130, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Enabled = false, Name = "btCopy" };
			Button close = new Button { Text = "Close", Location = new Point(520, 552), Size = new Size(86, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel };
			Controls.AddRange(new Control[] { btCopy, close });
			CancelButton = close;
			new ToolTip().SetToolTip(btCopy, "Copies the font into the translator's fonts folder, so it shows in the translator's settings, without using it");

			txtFilter.TextChanged += delegate { Fill(); };
			lstFonts.SelectedIndexChanged += delegate { Preview(); };
			btCaps.Click += delegate { SetSlot(true, Chosen?.Name); };
			btMixed.Click += delegate { SetSlot(false, Chosen?.Name); };
			btClearCaps.Click += delegate { SetSlot(true, null); };
			btClearMixed.Click += delegate { SetSlot(false, null); };
			optPage.CheckedChanged += delegate { ShowSlots(); };
			btCopy.Click += delegate { Copy(); };
			FormClosed += delegate { loaded?.Dispose(); };
			ShowSlots();
			if (previewFont != null)
			{
				Panel left = new Panel { Dock = DockStyle.Left, Width = 620, Name = "pnlLeft", AutoScroll = true, AutoScrollMinSize = new Size(600, 560) };
				foreach (Control c in Controls.Cast<Control>().ToList())
				{
					left.Controls.Add(c);
				}
				previewPanel = new FontPreviewPanel(previewFont, currentPicture, originalPicture) { Dock = DockStyle.Fill, Name = "previewPanel" };
				Controls.Add(previewPanel);
				Controls.Add(left);
				MinimumSize = new Size(900, 520);
			}
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

		/// <summary>Shows the fonts set now for the page or the book.</summary>
		private void ShowSlots()
		{
			StyleFonts now = currentFonts?.Invoke(optBook.Checked) ?? new StyleFonts();
			lblCaps.Text = string.IsNullOrEmpty(now.Caps) ? "(the translator's own choice)" : now.Caps;
			lblMixed.Text = string.IsNullOrEmpty(now.Mixed) ? "(the translator's own choice)" : now.Mixed;
			bool can = applyFont != null;
			btClearCaps.Enabled = can && !string.IsNullOrEmpty(now.Caps);
			btClearMixed.Enabled = can && !string.IsNullOrEmpty(now.Mixed);
			btCaps.Enabled = can && Chosen != null;
			btMixed.Enabled = can && Chosen != null;
		}

		private void SetSlot(bool caps, string family)
		{
			if (applyFont == null)
			{
				return;
			}
			bool book = optBook.Checked;
			foreach (Button b in new[] { btCaps, btMixed, btClearCaps, btClearMixed })
			{
				b.Enabled = false;
			}
			lblInfo.Text = "Working...";
			applyFont(family, caps, book, delegate (string file, string error)
			{
				try
				{
					BeginInvoke(new Action(delegate
					{
						string what = (caps ? "capital-letter" : "mixed-case") + " lettering of " + (book ? "this book" : "this page");
						if (error != null)
						{
							lblInfo.Text = "Failed: " + error;
						}
						else
						{
							UsedFile = string.IsNullOrEmpty(file) ? "The " + what + " is back to the translator's own font." : file + " is now used for the " + what + ".";
							lblInfo.Text = UsedFile + " The pages are drawn again with it.";
						}
						ShowSlots();
					}));
				}
				catch (Exception)
				{
				}
			});
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
			btCopy.Enabled = f != null;
			ShowSlots();
			if (f != null && previewPanel != null)
			{
				previewPanel.ShowFont(Path.GetFileName(f.Path));
			}
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

		private void Copy()
		{
			FontFile f = Chosen;
			if (f == null || install == null)
			{
				return;
			}
			btCopy.Enabled = false;
			lblInfo.Text = "Working...";
			install(f.Name, false, delegate (string file, string error)
			{
				try
				{
					BeginInvoke(new Action(delegate
					{
						btCopy.Enabled = true;
						lblInfo.Text = error != null ? "Failed: " + error : file + " was copied into the translator's fonts folder.";
					}));
				}
				catch (Exception)
				{
				}
			});
		}
	}

	/// <summary>A picture that can be zoomed with the mouse wheel and moved by dragging (double click fits it to the panel).</summary>
	public class ZoomCanvas : Control
	{
		private readonly Color back = Color.FromArgb(58, 58, 62);

		private Bitmap image;

		private float zoom = 1f;

		private PointF pan;

		private bool dragging;

		private Point lastMouse;

		private bool fitted = true;

		public string Message { get; set; }

		/// <summary>A small label in the corner ("The page now", "With Bangers.ttf").</summary>
		public string Caption { get; set; }

		public bool HasImage => image != null;

		/// <summary>The zoom or position changed (by the user, or by fitting): the other pane of a side by side view follows.</summary>
		public event EventHandler ViewChanged;

		public PointF Pan => pan;

		public bool IsFitted => fitted;

		/// <summary>Takes a view (zoom, position, fitted or not) from the other pane, without telling it back.</summary>
		public void SetView(float newZoom, PointF newPan, bool isFitted)
		{
			zoom = newZoom;
			pan = newPan;
			fitted = isFitted;
			Invalidate();
		}

		private void ViewMoved()
		{
			ViewChanged?.Invoke(this, EventArgs.Empty);
		}

		public ZoomCanvas()
		{
			SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
			Cursor = Cursors.Hand;
		}

		/// <summary>Shows a picture; the view (zoom and position) is kept when it is a picture of the same size.</summary>
		public void SetImage(Bitmap bitmap, bool keepView)
		{
			bool same = image != null && bitmap != null && image.Size == bitmap.Size;
			Bitmap old = image;
			image = bitmap;
			old?.Dispose();
			if (image == null)
			{
				Invalidate();
				return;
			}
			if (!(keepView && same) && !(keepView && !fitted))
			{
				Fit();
			}
			Invalidate();
		}

		public void Fit()
		{
			if (image == null)
			{
				return;
			}
			zoom = Math.Min((float)ClientSize.Width / image.Width, (float)ClientSize.Height / image.Height);
			zoom = Math.Max(0.02f, zoom);
			pan = new PointF((ClientSize.Width - image.Width * zoom) / 2f, (ClientSize.Height - image.Height * zoom) / 2f);
			fitted = true;
			Invalidate();
			ViewMoved();
		}

		public void ZoomTo(float value, PointF? around = null)
		{
			if (image == null)
			{
				return;
			}
			value = Math.Max(0.02f, Math.Min(8f, value));
			PointF c = around ?? new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
			pan = new PointF(c.X - (c.X - pan.X) * value / zoom, c.Y - (c.Y - pan.Y) * value / zoom);
			zoom = value;
			fitted = false;
			Invalidate();
			ViewMoved();
		}

		public float Zoom => zoom;

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			g.Clear(back);
			if (image != null)
			{
				g.InterpolationMode = zoom < 1f ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear : System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
				g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
				g.DrawImage(image, new RectangleF(pan.X, pan.Y, image.Width * zoom, image.Height * zoom));
			}
			if (!string.IsNullOrEmpty(Caption))
			{
				using (Font f = new Font(Font.FontFamily, 9f, FontStyle.Bold))
				{
					SizeF size = g.MeasureString(Caption, f);
					using (SolidBrush fill = new SolidBrush(Color.FromArgb(190, 20, 20, 20)))
					{
						g.FillRectangle(fill, 6, Height - size.Height - 12, size.Width + 12, size.Height + 6);
					}
					g.DrawString(Caption, f, Brushes.White, 12, Height - size.Height - 9);
				}
			}
			if (!string.IsNullOrEmpty(Message))
			{
				using (Font f = new Font(Font.FontFamily, 9f))
				{
					SizeF size = g.MeasureString(Message, f, Math.Max(40, Width - 40));
					RectangleF box = new RectangleF((Width - size.Width) / 2f - 8, 10, size.Width + 16, size.Height + 8);
					using (SolidBrush fill = new SolidBrush(Color.FromArgb(200, 20, 20, 20)))
					{
						g.FillRectangle(fill, box);
					}
					g.DrawString(Message, f, Brushes.White, new RectangleF(box.X + 8, box.Y + 4, size.Width, size.Height));
				}
			}
		}

		protected override void OnResize(EventArgs e)
		{
			base.OnResize(e);
			if (fitted)
			{
				Fit();
			}
		}

		protected override void OnMouseWheel(MouseEventArgs e)
		{
			base.OnMouseWheel(e);
			ZoomTo(e.Delta > 0 ? zoom * 1.25f : zoom / 1.25f, e.Location);
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			Focus();
			if (e.Button == MouseButtons.Left && image != null)
			{
				dragging = true;
				lastMouse = e.Location;
				Cursor = Cursors.SizeAll;
			}
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			if (dragging)
			{
				pan = new PointF(pan.X + e.X - lastMouse.X, pan.Y + e.Y - lastMouse.Y);
				lastMouse = e.Location;
				fitted = false;
				Invalidate();
				ViewMoved();
			}
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			base.OnMouseUp(e);
			dragging = false;
			Cursor = Cursors.Hand;
		}

		protected override void OnDoubleClick(EventArgs e)
		{
			base.OnDoubleClick(e);
			Fit();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				image?.Dispose();
			}
			base.Dispose(disposing);
		}
	}

	/// <summary>
	/// The page drawn with a font before it is used. The picture can be zoomed and moved, shown on its own or beside the page as it is now
	/// and the page as it was before any translating (two or three panes that zoom and move together), and the font can be tried for the
	/// capital-letter lettering, the mixed-case lettering or both.
	/// </summary>
	public class FontPreviewPanel : Panel
	{
		private readonly Action<string, int, Action<byte[], string>> render;

		private readonly Func<byte[]> currentPicture;

		private readonly Func<byte[]> originalPicture;

		private readonly ZoomCanvas cvFont;

		private readonly ZoomCanvas cvNow;

		private readonly ZoomCanvas cvOriginal;

		private readonly TableLayoutPanel host;

		private readonly ComboBox cboView;

		private readonly ComboBox cboKind;

		private readonly Label lblInfo;

		private readonly System.Windows.Forms.Timer wait;

		private int sequence;

		private string wanted;

		private Bitmap withFont;

		private bool syncing;

		private bool loadedNow;

		private bool loadedOriginal;

		/// <summary>Which lettering the font is tried for: 0 capital letters, 1 mixed case, 2 both.</summary>
		public int Kind
		{
			get
			{
				return Math.Max(0, cboKind.SelectedIndex);
			}
			set
			{
				if (cboKind.SelectedIndex != value)
				{
					cboKind.SelectedIndex = value;
				}
			}
		}

		/// <summary>0 the page with the font; 1 the page now beside it; 2 the original page beside it; 3 original, page now and with the font.</summary>
		public int View => Math.Max(0, cboView.SelectedIndex);

		public FontPreviewPanel(Action<string, int, Action<byte[], string>> render, Func<byte[]> currentPicture, Func<byte[]> originalPicture = null)
		{
			this.render = render;
			this.currentPicture = currentPicture;
			this.originalPicture = originalPicture;
			BorderStyle = BorderStyle.FixedSingle;
			Panel bar = new Panel { Dock = DockStyle.Top, Height = 122 };
			Button fit = new Button { Text = "Fit", Location = new Point(6, 5), Size = new Size(44, 24), Name = "btPrevFit" };
			Button full = new Button { Text = "100%", Location = new Point(54, 5), Size = new Size(48, 24), Name = "btPrev100" };
			Button minus = new Button { Text = "-", Location = new Point(106, 5), Size = new Size(30, 24), Name = "btPrevOut" };
			Button plus = new Button { Text = "+", Location = new Point(140, 5), Size = new Size(30, 24), Name = "btPrevIn" };
			Label viewLabel = new Label { Text = "Show:", Location = new Point(6, 38), Size = new Size(40, 18) };
			cboView = new ComboBox { Location = new Point(50, 35), Size = new Size(300, 21), DropDownStyle = ComboBoxStyle.DropDownList, Name = "cboPrevView" };
			cboView.Items.AddRange(new object[] { "The page with the font", "Page now | with the font", "Original page | with the font", "Original | page now | with the font" });
			cboView.SelectedIndex = 0;
			Label tryLabel = new Label { Text = "Try the font for:", Location = new Point(6, 68), Size = new Size(96, 18) };
			cboKind = new ComboBox { Location = new Point(106, 65), Size = new Size(140, 21), DropDownStyle = ComboBoxStyle.DropDownList, Name = "cboPrevKind" };
			cboKind.Items.AddRange(new object[] { "Capital letters", "Mixed case", "Both" });
			cboKind.SelectedIndex = 2;
			lblInfo = new Label { Location = new Point(6, 92), Size = new Size(430, 28), AutoEllipsis = true, Name = "lblPrevInfo", Text = "Select a font to see the page with it." };
			bar.Controls.AddRange(new Control[] { fit, full, minus, plus, viewLabel, cboView, tryLabel, cboKind, lblInfo });
			cvOriginal = new ZoomCanvas { Dock = DockStyle.Fill, Name = "prevCanvasOriginal", Caption = "Original page (not translated)", Visible = false, Margin = Padding.Empty };
			cvNow = new ZoomCanvas { Dock = DockStyle.Fill, Name = "prevCanvasNow", Caption = "The page now", Visible = false, Margin = Padding.Empty };
			cvFont = new ZoomCanvas { Dock = DockStyle.Fill, Name = "prevCanvas", Caption = "With the font", Margin = Padding.Empty };
			host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty, Name = "prevHost" };
			host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 0f));
			host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 0f));
			host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
			host.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
			host.Controls.Add(cvOriginal, 0, 0);
			host.Controls.Add(cvNow, 1, 0);
			host.Controls.Add(cvFont, 2, 0);
			Controls.Add(host);
			Controls.Add(bar);
			new ToolTip().SetToolTip(cvFont, "Mouse wheel zooms, drag moves the page, double click fits it");
			new ToolTip().SetToolTip(cboKind, "Which lettering the font is tried for: capital letters, mixed case or both");
			new ToolTip().SetToolTip(cboView, "Compare the font with the page as it is now and with the original, untranslated page; the panes zoom and move together");
			//the panes zoom and move together
			EventHandler follow = delegate (object sender, EventArgs e)
			{
				if (syncing)
				{
					return;
				}
				syncing = true;
				ZoomCanvas from = (ZoomCanvas)sender;
				foreach (ZoomCanvas other in new[] { cvOriginal, cvNow, cvFont })
				{
					if (other != from && other.Visible)
					{
						other.SetView(from.Zoom, from.Pan, from.IsFitted);
					}
				}
				syncing = false;
			};
			cvOriginal.ViewChanged += follow;
			cvNow.ViewChanged += follow;
			cvFont.ViewChanged += follow;
			fit.Click += delegate { cvFont.Fit(); };
			full.Click += delegate { cvFont.ZoomTo(1f); };
			minus.Click += delegate { cvFont.ZoomTo(cvFont.Zoom / 1.25f); };
			plus.Click += delegate { cvFont.ZoomTo(cvFont.Zoom * 1.25f); };
			cboView.SelectedIndexChanged += delegate { ApplyView(); };
			cboKind.SelectedIndexChanged += delegate { Redraw(wanted); };
			wait = new System.Windows.Forms.Timer { Interval = 350 };
			wait.Tick += delegate
			{
				wait.Stop();
				Draw();
			};
			Disposed += delegate { wait.Stop(); wait.Dispose(); withFont?.Dispose(); };
			cvFont.Message = "Select a font to see the page with it.";
		}

		private static Bitmap Decode(byte[] data)
		{
			try
			{
				using (MemoryStream ms = new MemoryStream(data))
				using (Image decoded = Image.FromStream(ms))
				{
					return new Bitmap(decoded);
				}
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// <summary>Shows the panes the chosen view needs, loading the page as it is now and the original page the first time they are wanted.</summary>
		private void ApplyView()
		{
			int mode = View;
			bool showOriginal = mode >= 2, showNow = mode == 1 || mode == 3;
			if (showNow && !loadedNow)
			{
				loadedNow = true;
				byte[] data = currentPicture?.Invoke();
				Bitmap bmp = data == null ? null : Decode(data);
				if (bmp != null)
				{
					cvNow.SetImage(bmp, keepView: false);
				}
				else
				{
					cvNow.Message = "The page as it is now is not available.";
				}
			}
			if (showOriginal && !loadedOriginal)
			{
				loadedOriginal = true;
				byte[] data = originalPicture?.Invoke();
				Bitmap bmp = data == null ? null : Decode(data);
				if (bmp != null)
				{
					cvOriginal.SetImage(bmp, keepView: false);
				}
				else
				{
					cvOriginal.Message = "The original page is not available.";
				}
			}
			int shown = 1 + (showOriginal ? 1 : 0) + (showNow ? 1 : 0);
			//a pane needs room to be useful: widen the window when it is too narrow for the panes asked for (as far as the screen allows)
			Form owner = FindForm();
			if (owner != null && owner.Visible && owner.WindowState == FormWindowState.Normal && shown > 1 && host.Width < 340 * shown)
			{
				Rectangle area = Screen.FromControl(owner).WorkingArea;
				int grow = Math.Min(340 * shown - host.Width, area.Width - owner.Width);
				if (grow > 0)
				{
					int left = Math.Max(area.Left, Math.Min(owner.Left, area.Right - owner.Width - grow));
					owner.SetBounds(left, owner.Top, owner.Width + grow, owner.Height);
				}
			}
			host.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, showOriginal ? 100f / shown : 0f);
			host.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, showNow ? 100f / shown : 0f);
			host.ColumnStyles[2] = new ColumnStyle(SizeType.Percent, 100f / shown);
			cvOriginal.Visible = showOriginal;
			cvNow.Visible = showNow;
			host.PerformLayout();
			cvFont.Fit();
		}

		/// <summary>Shows the page with a font (by file name), a moment after the last call so that moving through a list stays quick.</summary>
		public void ShowFont(string fontFile)
		{
			wanted = fontFile;
			if (string.IsNullOrEmpty(fontFile))
			{
				return;
			}
			lblInfo.Text = "Drawing the page with " + fontFile + "...";
			wait.Stop();
			wait.Start();
		}

		private void Redraw(string fontFile)
		{
			if (!string.IsNullOrEmpty(fontFile))
			{
				ShowFont(fontFile);
			}
		}

		private void Draw()
		{
			string file = wanted;
			int kind = Kind;
			int mine = ++sequence;
			cvFont.Message = "Drawing the page with " + file + "...";
			cvFont.Invalidate();
			render(file, kind, delegate (byte[] jpeg, string error)
			{
				try
				{
					BeginInvoke(new Action(delegate
					{
						if (mine != sequence || IsDisposed)
						{
							return;      //a later choice has been asked for since
						}
						if (jpeg == null)
						{
							cvFont.Message = "No preview: " + error;
							lblInfo.Text = "The page could not be drawn with " + file + ": " + error;
							cvFont.Invalidate();
							return;
						}
						Bitmap bmp = Decode(jpeg);
						if (bmp == null)
						{
							cvFont.Message = "No preview: the picture could not be read.";
							cvFont.Invalidate();
							return;
						}
						withFont?.Dispose();
						withFont = bmp;
						cvFont.Message = null;
						cvFont.Caption = "With " + file;
						lblInfo.Text = "The page with " + file + " for " + cboKind.SelectedItem.ToString().ToLowerInvariant() + ". Not saved: use the buttons to apply it.";
						cvFont.SetImage(new Bitmap(withFont), keepView: true);
					}));
				}
				catch (Exception)
				{
				}
			});
		}
	}
}
