using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using cYo.Common.Windows.Forms;
using cYo.Common.Windows.Forms.Theme;
using cYo.Projects.ComicRack.Engine;
using cYo.Projects.ComicRack.Engine.Display.Forms;
using cYo.Projects.ComicRack.Engine.IO;
using cYo.Projects.ComicRack.Engine.IO.Provider;

namespace cYo.Projects.ComicRack.Viewer.Dialogs
{
	/// <summary>
	/// Writes translated copies of the books selected in the library, one after the other. Books translated already (every page,
	/// into one language) are written in that language with the pages the translator stored, so nothing is translated; a book
	/// translated into two languages is written once for each. Books not translated yet, or only partly, are listed unticked:
	/// ticked, their missing pages are translated into the language chosen for them while they are written.
	/// </summary>
	public class TranslationBatchExportDialog : FormEx
	{
		private class Job
		{
			public ComicBook Book;

			public string BookKey;

			public int Pages;

			//pages translated into each language ("" for the translator's own, pages noted before languages were)
			public Dictionary<string, int> Counts = new Dictionary<string, int>();

			//"en", "zh-CN", or null: the translator's own language
			public string Target;

			//every page is translated into Target: written from what the translator stored, nothing is translated
			public bool Complete;

			//cannot be written at all (no file)
			public string Problem;

			public ListViewItem Row;

			public bool Tickable => Problem == null;

			public int Translated => Counts.TryGetValue(Target ?? "", out int n) ? n : 0;

			public string Language => TranslationButtons.LanguageName(Target) ?? "as set now";
		}

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

		private class LanguageChoice
		{
			public string Label;

			public string Code;

			public override string ToString()
			{
				return Label;
			}
		}

		private const string NameEnding = " - AI Translated";

		private readonly List<ComicBook> books;

		private readonly List<Job> jobs = new List<Job>();

		private readonly string rarPath;

		private ListView list;

		private ComboBox cboLanguage;

		private Label lblLanguage;

		private ComboBox cboFormat;

		private CheckBox chkBeside;

		private TextBox txtFolder;

		private Button btBrowse;

		private CheckBox chkOverwrite;

		private CheckBox chkAdd;

		private Label lblNote;

		private ProgressBar progress;

		private Label lblProgress;

		private Button btStart;

		private Button btClose;

		private volatile TranslationExport running;

		private volatile bool cancelled;

		private bool working;

		private bool showingLanguage;

		public override UIComponent UIComponent => UIComponent.Content;

		public TranslationBatchExportDialog(IEnumerable<ComicBook> books)
		{
			this.books = books.Where((ComicBook b) => b != null).ToList();
			rarPath = TranslationExport.FindRar();
			Text = this.books.Count == 1 ? "Export Translated Book" : "Export Translated Books";
			AutoScaleDimensions = new SizeF(6f, 13f);
			AutoScaleMode = AutoScaleMode.Font;
			FormBorderStyle = FormBorderStyle.Sizable;
			StartPosition = FormStartPosition.CenterParent;
			MaximizeBox = false;
			MinimizeBox = false;
			ShowInTaskbar = false;
			ClientSize = new Size(680, 500);
			MinimumSize = new Size(560, 450);
			Color gray = ThemeManager.IsDarkModeEnabled ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);
			AnchorStyles topWide = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			AnchorStyles bottomWide = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
			AnchorStyles bottomLeft = AnchorStyles.Bottom | AnchorStyles.Left;
			AnchorStyles bottomRight = AnchorStyles.Bottom | AnchorStyles.Right;

			Controls.Add(new Label
			{
				Text = "Books translated already are written in their language, from what the translator stored: nothing is translated. Tick a book that is not fully translated to have its missing pages translated while it is written (that takes a while), into the language chosen below.",
				Location = new Point(12, 8),
				Size = new Size(656, 30),
				Anchor = topWide
			});
			list = new ListView
			{
				Location = new Point(12, 42),
				Size = new Size(656, 200),
				View = View.Details,
				CheckBoxes = true,
				FullRowSelect = true,
				HideSelection = false,
				HeaderStyle = ColumnHeaderStyle.Nonclickable,
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				Name = "lvBooks"
			};
			list.Columns.Add("Book", 290);
			list.Columns.Add("Language", 90);
			list.Columns.Add("Translated", 80);
			list.Columns.Add("", 190);
			//a book without a file cannot be written
			list.ItemCheck += delegate (object s, ItemCheckEventArgs e)
			{
				if (working || !(list.Items[e.Index].Tag is Job job) || !job.Tickable)
				{
					e.NewValue = working ? e.CurrentValue : CheckState.Unchecked;
				}
			};
			list.ItemChecked += delegate { UpdateButtons(); };
			list.SelectedIndexChanged += delegate { ShowLanguage(); };
			Controls.Add(list);

			lblLanguage = new Label { Text = "Translate the rest into:", Location = new Point(12, 253), Size = new Size(130, 18), Anchor = bottomLeft };
			cboLanguage = new ComboBoxEx { Location = new Point(148, 249), Size = new Size(160, 21), DropDownStyle = ComboBoxStyle.DropDownList, Anchor = bottomLeft, Name = "cboLanguage", Enabled = false };
			cboLanguage.Items.Add(new LanguageChoice { Label = "English", Code = "en" });
			cboLanguage.Items.Add(new LanguageChoice { Label = "Chinese", Code = TranslationButtons.ChineseTarget });
			cboLanguage.SelectedIndexChanged += delegate { SetLanguage(); };
			Controls.Add(lblLanguage);
			Controls.Add(cboLanguage);
			Controls.Add(new Label { Text = "(for the selected books that are not fully translated)", Location = new Point(316, 253), Size = new Size(352, 18), ForeColor = gray, Anchor = bottomWide });

			Controls.Add(new Label { Text = "Save as:", Location = new Point(12, 285), Size = new Size(70, 18), Anchor = bottomLeft });
			cboFormat = new ComboBoxEx { Location = new Point(88, 281), Size = new Size(580, 21), DropDownStyle = ComboBoxStyle.DropDownList, Anchor = bottomWide, Name = "cboFormat" };
			cboFormat.Items.Add(new FormatChoice { Label = "Same as each original", FormatId = 0 });
			cboFormat.Items.Add(new FormatChoice { Label = "CBZ (ZIP)", FormatId = KnownFileFormats.CBZ });
			cboFormat.Items.Add(new FormatChoice { Label = "CB7 (7-Zip)", FormatId = KnownFileFormats.CB7 });
			cboFormat.Items.Add(new FormatChoice { Label = "CBT (TAR)", FormatId = KnownFileFormats.CBT });
			if (rarPath != null)
			{
				cboFormat.Items.Add(new FormatChoice { Label = "CBR (RAR, packed with WinRAR)", FormatId = KnownFileFormats.CBZ, Rar = true });
			}
			cboFormat.SelectedIndex = 0;
			Controls.Add(cboFormat);

			chkBeside = new CheckBox { Text = "Save each next to its original book", Location = new Point(88, 310), Size = new Size(580, 22), Checked = true, Anchor = bottomWide, Name = "chkBeside" };
			Controls.Add(new Label { Text = "Folder:", Location = new Point(12, 339), Size = new Size(70, 18), Anchor = bottomLeft });
			txtFolder = new TextBox { Location = new Point(88, 336), Size = new Size(492, 20), Anchor = bottomWide, Name = "txtFolder", Enabled = false };
			btBrowse = new Button { Text = "Browse...", Location = new Point(586, 334), Size = new Size(82, 24), Anchor = bottomRight, Name = "btBrowse", Enabled = false };
			chkOverwrite = new CheckBox { Text = "Replace a file if it already exists", Location = new Point(88, 362), Size = new Size(580, 22), Anchor = bottomWide, Name = "chkOverwrite" };
			chkAdd = new CheckBox { Text = "Add the new books to my library", Location = new Point(88, 384), Size = new Size(580, 22), Anchor = bottomWide, Name = "chkAdd" };
			lblNote = new Label { Location = new Point(12, 410), Size = new Size(656, 18), ForeColor = gray, AutoEllipsis = true, Anchor = bottomWide, Name = "lblNote" };
			progress = new ProgressBar { Location = new Point(12, 432), Size = new Size(656, 16), Anchor = bottomWide, Name = "progress" };
			lblProgress = new Label { Location = new Point(12, 452), Size = new Size(480, 18), AutoEllipsis = true, Anchor = bottomWide, Name = "lblProgress" };
			btStart = new Button { Text = "Export", Location = new Point(500, 466), Size = new Size(84, 28), Anchor = bottomRight, Name = "btStart", Enabled = false };
			btClose = new Button { Text = "Close", Location = new Point(590, 466), Size = new Size(78, 28), Anchor = bottomRight, Name = "btClose" };
			Controls.AddRange(new Control[] { chkBeside, txtFolder, btBrowse, chkOverwrite, chkAdd, lblNote, progress, lblProgress, btStart, btClose });
			AcceptButton = btStart;

			chkBeside.CheckedChanged += delegate
			{
				txtFolder.Enabled = btBrowse.Enabled = !chkBeside.Checked;
				UpdateButtons();
			};
			txtFolder.TextChanged += delegate { UpdateButtons(); };
			btBrowse.Click += delegate
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
				if (working)
				{
					Cancel();
				}
				else
				{
					Close();
				}
			};
			FormClosing += delegate (object s, FormClosingEventArgs e)
			{
				if (working)
				{
					e.Cancel = true;
					Cancel();
				}
			};
			lblNote.Text = "Looking at the books...";
			//once the window is there, so the answer has somewhere to go
			Load += delegate { LoadBooks(); };
		}

		/// <summary>Finds out (off the interface thread) which books are translated, into what, and how many pages they have.</summary>
		private void LoadBooks()
		{
			List<ComicBook> todo = books.ToList();
			ThreadPool.QueueUserWorkItem(delegate
			{
				List<Job> found = new List<Job>();
				TranslationStore store = TranslationStore.Shared;
				string now = TranslationButtons.CurrentTarget;
				foreach (ComicBook book in todo)
				{
					string key = TranslationStore.BookKeyOf(book);
					int pages = PageCount(book);
					Dictionary<string, int> counts = key != null ? store.LanguageCounts(key) : new Dictionary<string, int>();
					//pages noted before languages were: taken as in the translator's language of now
					if (counts.TryGetValue("", out int unknown) && !string.IsNullOrEmpty(now))
					{
						counts.Remove("");
						counts.TryGetValue(now, out int n);
						counts[now] = Math.Max(n, unknown);
					}
					if (string.IsNullOrEmpty(book.FilePath) || key == null)
					{
						found.Add(new Job { Book = book, BookKey = key, Pages = pages, Counts = counts, Problem = "no file: cannot be written" });
						continue;
					}
					List<string> complete = counts.Where((KeyValuePair<string, int> c) => pages > 0 && c.Value >= pages).Select((KeyValuePair<string, int> c) => c.Key).OrderBy((string c) => c).ToList();
					foreach (string language in complete)
					{
						found.Add(new Job { Book = book, BookKey = key, Pages = pages, Counts = counts, Target = language.Length > 0 ? language : null, Complete = true });
					}
					if (complete.Count == 0)
					{
						//not fully translated: offered in the language most of its pages are in, else the translator's own
						string language = counts.Where((KeyValuePair<string, int> c) => c.Key.Length > 0).OrderByDescending((KeyValuePair<string, int> c) => c.Value).Select((KeyValuePair<string, int> c) => c.Key).FirstOrDefault();
						if (language == null)
						{
							language = string.IsNullOrEmpty(now) ? "en" : now;
						}
						found.Add(new Job { Book = book, BookKey = key, Pages = pages, Counts = counts, Target = language });
					}
				}
				BeginInvokeSafe(delegate { ShowBooks(found); });
			});
		}

		private static int PageCount(ComicBook book)
		{
			if (book.PageCount > 0)
			{
				return book.PageCount;
			}
			try
			{
				using (ImageProvider provider = book.OpenProvider())
				{
					return provider?.Count ?? 0;
				}
			}
			catch (Exception)
			{
				return 0;
			}
		}

		private void ShowBooks(List<Job> found)
		{
			jobs.Clear();
			jobs.AddRange(found);
			list.BeginUpdate();
			list.Items.Clear();
			foreach (Job job in jobs)
			{
				job.Row = new ListViewItem(new[] { job.Book.Caption, "", "", "" }) { Tag = job };
				FillRow(job);
				list.Items.Add(job.Row);
			}
			foreach (Job job in jobs)
			{
				job.Row.Checked = job.Complete;
			}
			list.EndUpdate();
			int ready = jobs.Count((Job j) => j.Complete);
			int unfinished = jobs.Count((Job j) => !j.Complete && j.Tickable);
			lblNote.Text = ready + (ready == 1 ? " book is" : " books are") + " translated and ticked" + (unfinished > 0 ? "; " + unfinished + " not fully translated (tick to translate the rest)." : ".");
			ShowLanguage();
			UpdateButtons();
		}

		private void FillRow(Job job)
		{
			job.Row.SubItems[1].Text = job.Tickable ? job.Language : "";
			job.Row.SubItems[2].Text = job.Translated + " of " + (job.Pages > 0 ? job.Pages.ToString() : "?");
			job.Row.SubItems[3].Text = job.Problem ?? (job.Complete ? "" : job.Translated == 0 ? "not translated: tick to translate" : "partly: tick to translate the rest");
			job.Row.ForeColor = job.Complete ? list.ForeColor : SystemColors.GrayText;
		}

		private List<Job> SelectedUnfinished => list.SelectedItems.Cast<ListViewItem>().Select((ListViewItem r) => (Job)r.Tag).Where((Job j) => j.Tickable && !j.Complete).ToList();

		/// <summary>The language box shows (and changes) the language of the selected books that are not fully translated.</summary>
		private void ShowLanguage()
		{
			List<Job> selected = SelectedUnfinished;
			cboLanguage.Enabled = lblLanguage.Enabled = !working && selected.Count > 0;
			if (selected.Count == 0)
			{
				return;
			}
			string code = selected[0].Target ?? "";
			showingLanguage = true;
			cboLanguage.SelectedIndex = code.StartsWith("zh") ? 1 : code == "en" ? 0 : -1;
			showingLanguage = false;
		}

		private void SetLanguage()
		{
			if (showingLanguage || !(cboLanguage.SelectedItem is LanguageChoice choice))
			{
				return;
			}
			foreach (Job job in SelectedUnfinished)
			{
				job.Target = choice.Code;
				FillRow(job);
			}
		}

		private FormatChoice Choice => (FormatChoice)cboFormat.SelectedItem;

		private List<Job> Ticked => list.Items.Cast<ListViewItem>().Where((ListViewItem r) => r.Checked).Select((ListViewItem r) => (Job)r.Tag).ToList();

		private void UpdateButtons()
		{
			btStart.Enabled = !working && Ticked.Count > 0 && (chkBeside.Checked || !string.IsNullOrWhiteSpace(txtFolder.Text));
		}

		private ExportSetting SettingFor(Job job)
		{
			string language = TranslationButtons.LanguageName(job.Target);
			return new ExportSetting
			{
				Target = ExportTarget.NewFolder,
				TargetFolder = chkBeside.Checked ? Path.GetDirectoryName(job.Book.FilePath) : txtFolder.Text.Trim(),
				Naming = ExportNaming.Custom,
				CustomName = job.Book.FileName + NameEnding + (language != null ? " (" + language + ")" : ""),
				CustomNamingStart = 0,
				FormatId = Choice.FormatId,
				Overwrite = chkOverwrite.Checked,
				EmbedComicInfo = true
			};
		}

		private TranslationExport ExportFor(Job job)
		{
			return new TranslationExport(job.Book, SettingFor(job), Program.CacheManager?.ImagePool, Program.Settings.TranslatorServerUrl, TranslationStore.Shared)
			{
				Target = job.Target,
				//a translated book is written from what the translator stored; an unfinished one has the rest translated
				StoredOnly = job.Complete,
				RarPath = Choice.Rar ? rarPath : null
			};
		}

		private void Start()
		{
			List<Job> todo = Ticked;
			if (todo.Count == 0)
			{
				return;
			}
			if (!TranslationServices.HelperUp)
			{
				MessageBox.Show(this, "The translator is not running. Start it with the Translation services button: the translated pages are kept by the translator.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}
			//the same book twice into one language (an unfinished one set to a language it is complete in too) is written once
			todo = todo.GroupBy((Job j) => ExportFor(j).TargetPath, StringComparer.OrdinalIgnoreCase).Select((IGrouping<string, Job> g) => g.OrderByDescending((Job j) => j.Complete).First()).ToList();
			if (!chkOverwrite.Checked)
			{
				List<string> existing = todo.Select((Job j) => ExportFor(j).TargetPath).Where(File.Exists).ToList();
				if (existing.Count > 0)
				{
					MessageBox.Show(this, (existing.Count == 1 ? "This file already exists:\n" : "These files already exist:\n") + string.Join("\n", existing.Take(8)) + (existing.Count > 8 ? "\n..." : "") + "\n\nTick \"Replace a file if it already exists\", or untick those books.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
					return;
				}
			}
			bool addToLibrary = chkAdd.Checked;
			List<KeyValuePair<Job, TranslationExport>> work = todo.Select((Job j) => new KeyValuePair<Job, TranslationExport>(j, ExportFor(j))).ToList();
			working = true;
			cancelled = false;
			cboFormat.Enabled = chkBeside.Enabled = txtFolder.Enabled = btBrowse.Enabled = chkOverwrite.Enabled = chkAdd.Enabled = false;
			cboLanguage.Enabled = lblLanguage.Enabled = false;
			btStart.Enabled = false;
			btClose.Text = "Cancel";
			progress.Maximum = Math.Max(1, work.Sum((KeyValuePair<Job, TranslationExport> w) => Math.Max(1, w.Key.Pages)));
			progress.Value = 0;
			foreach (KeyValuePair<Job, TranslationExport> w in work)
			{
				w.Key.Row.SubItems[3].Text = "waiting";
			}
			Thread worker = new Thread(delegate ()
			{
				List<string> written = new List<string>();
				List<string> failed = new List<string>();
				int before = 0;
				for (int i = 0; i < work.Count && !cancelled; i++)
				{
					Job job = work[i].Key;
					TranslationExport export = work[i].Value;
					int number = i + 1;
					int offset = before;
					string doing = job.Complete ? "page " : "translating page ";
					running = export;
					BeginInvokeSafe(delegate
					{
						job.Row.SubItems[3].Text = job.Complete ? "writing..." : "translating and writing...";
						lblProgress.Text = "Book " + number + " of " + work.Count + ": " + job.Book.Caption;
					});
					export.Progress += delegate (int done, int total)
					{
						BeginInvokeSafe(delegate
						{
							progress.Value = Math.Min(progress.Maximum, offset + done);
							lblProgress.Text = "Book " + number + " of " + work.Count + ", " + doing + done + " of " + total + ": " + job.Book.Caption;
						});
					};
					string status;
					try
					{
						string path = export.Run();
						written.Add(path);
						if (addToLibrary)
						{
							Program.BookFactory.Create(path, CreateBookOption.AddToStorage);
						}
						status = "done";
					}
					catch (OperationCanceledException)
					{
						status = "cancelled";
					}
					catch (Exception ex)
					{
						failed.Add("\"" + job.Book.Caption + "\": " + ex.Message);
						status = "failed: " + ex.Message;
					}
					before += Math.Max(1, job.Pages);
					BeginInvokeSafe(delegate
					{
						job.Row.SubItems[3].Text = status;
						progress.Value = Math.Min(progress.Maximum, before);
					});
				}
				running = null;
				BeginInvokeSafe(delegate { Finished(written, failed, work.Count); });
			})
			{
				IsBackground = true,
				Name = "Export translated books"
			};
			worker.Start();
		}

		private void Cancel()
		{
			cancelled = true;
			running?.Cancel();
			btClose.Enabled = false;
			lblProgress.Text = "Cancelling...";
		}

		private void Finished(List<string> written, List<string> failed, int asked)
		{
			working = false;
			btClose.Enabled = true;
			btClose.Text = "Close";
			cboFormat.Enabled = chkBeside.Enabled = chkOverwrite.Enabled = chkAdd.Enabled = true;
			txtFolder.Enabled = btBrowse.Enabled = !chkBeside.Checked;
			//what was translated now counts: refresh the numbers
			TranslationStore store = TranslationStore.Shared;
			foreach (Job job in jobs.Where((Job j) => j.BookKey != null))
			{
				job.Counts = store.LanguageCounts(job.BookKey);
			}
			ShowLanguage();
			UpdateButtons();
			string summary = written.Count + " of " + asked + (asked == 1 ? " book" : " books") + " written";
			lblProgress.Text = (cancelled ? "Cancelled. " : "") + summary + (failed.Count > 0 ? ", " + failed.Count + " failed." : ".");
			if (cancelled)
			{
				return;
			}
			string text = summary + ":" + (written.Count > 0 ? "\n" + string.Join("\n", written.Take(10)) + (written.Count > 10 ? "\n..." : "") : "");
			if (failed.Count > 0)
			{
				text += "\n\nNot written:\n" + string.Join("\n", failed.Take(10));
			}
			MessageBox.Show(this, text, Text, MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
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
}
