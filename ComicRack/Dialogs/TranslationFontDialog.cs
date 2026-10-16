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
		private readonly Action<bool, Action<FontIdentification, string>> search;

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

		public TranslationFontDialog(Action<bool, Action<FontIdentification, string>> search, Action<string, bool, Action<string, string>> install)
		{
			this.search = search;
			this.install = install;
			Text = "Identify Font";
			AutoScaleDimensions = new SizeF(6f, 13f);
			AutoScaleMode = AutoScaleMode.Font;
			StartPosition = FormStartPosition.CenterParent;
			ShowInTaskbar = false;
			MinimumSize = new Size(760, 560);
			ClientSize = new Size(800, 700);
			Color gray = ThemeManager.IsDarkModeEnabled ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);

			Controls.Add(new Label { Text = "The lettering in the selected box:", Location = new Point(14, 12), Size = new Size(400, 18) });
			picCrop = new PictureBox { Location = new Point(14, 32), Size = new Size(772, 96), SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
			Controls.Add(picCrop);
			lblStatus = new Label { Location = new Point(14, 134), Size = new Size(772, 34), ForeColor = gray, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Name = "lblStatus" };
			Controls.Add(lblStatus);
			progress = new ProgressBar { Location = new Point(14, 170), Size = new Size(772, 8), Style = ProgressBarStyle.Marquee, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Visible = false };
			Controls.Add(progress);

			rows = new Panel { Location = new Point(14, 184), Size = new Size(772, 292), AutoScroll = true, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Name = "rows" };
			Controls.Add(rows);

			Label other = new Label { Text = "Other places to look (they do not allow automated searching, so these open in your browser):", Location = new Point(14, 484), Size = new Size(772, 18), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
			Controls.Add(other);
			links = new Panel { Location = new Point(14, 504), Size = new Size(772, 64), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Name = "links" };
			Controls.Add(links);
			lblFolder = new Label { Location = new Point(14, 574), Size = new Size(772, 52), ForeColor = gray, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Name = "lblFolder" };
			Controls.Add(lblFolder);

			btCopy = new Button { Text = "Copy lettering picture", Location = new Point(14, 662), Size = new Size(150, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Enabled = false, Name = "btCopy" };
			Button btFolder = new Button { Text = "Open candidates folder", Location = new Point(170, 662), Size = new Size(150, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Name = "btFolder" };
			btSearch = new Button { Text = "Search again", Location = new Point(606, 662), Size = new Size(88, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Name = "btSearch" };
			Button close = new Button { Text = "Close", Location = new Point(700, 662), Size = new Size(86, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel, Name = "btClose" };
			Controls.AddRange(new Control[] { btCopy, btFolder, btSearch, close });
			CancelButton = close;
			new ToolTip().SetToolTip(btCopy, "Copies the lettering as a picture, to paste into WhatTheFont or Font Squirrel's Matcherator");
			new ToolTip().SetToolTip(btFolder, "Fonts you download from dafont or Font Squirrel can be dropped here (zip files too); Search again then includes them");

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
			SetStatus("Searching the fonts on this computer and Google Fonts. The first search builds a small cache of font shapes and can take several minutes; later ones take seconds.");
			search(true, delegate (FontIdentification result, string error)
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
			int y = 4;
			foreach (FontMatch match in result.Matches)
			{
				rows.Controls.Add(MakeRow(match, y));
				y += 86;
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
			lblFolder.Text = "Fonts you download from dafont, Font Squirrel and similar sites can be dropped (zip files too) in:\r\n" + result.CandidatesFolder + "\r\nThen press Search again to have them compared with this lettering. Check each font's licence before using it.";
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
			string where = match.Source == "google" ? "Google Fonts (open licence)" + (string.IsNullOrEmpty(match.Category) ? "" : ", " + match.Category) : (match.Source == "installed" ? "installed on this computer" : (match.Source == "candidates" ? "from your candidates folder" : "in the translator's fonts folder"));
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
}
