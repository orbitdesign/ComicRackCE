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
using cYo.Projects.ComicRack.Viewer.Config;

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

		private CheckBox chkBatch;

		private Label lblNote;

		private Label lblBackground;

		private Button btStart;

		private Button btClose;

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
			ClientSize = new Size(680, 522);
			MinimumSize = new Size(560, 472);
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
				if (!(list.Items[e.Index].Tag is Job job) || !job.Tickable)
				{
					e.NewValue = CheckState.Unchecked;
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
			chkBatch = new CheckBox { Text = "Also write each book the batch translation finishes, with these choices", Location = new Point(88, 406), Size = new Size(580, 22), Anchor = bottomWide, Name = "chkBatch" };
			lblNote = new Label { Location = new Point(12, 432), Size = new Size(656, 18), ForeColor = gray, AutoEllipsis = true, Anchor = bottomWide, Name = "lblNote" };
			lblBackground = new Label { Text = "The books are written in the background: the status bar shows how far it got, and a click there stops it. You can close this window and go on reading.", Location = new Point(12, 452), Size = new Size(656, 30), ForeColor = gray, Anchor = bottomWide, Name = "lblBackground" };
			btStart = new Button { Text = "Export", Location = new Point(500, 488), Size = new Size(84, 28), Anchor = bottomRight, Name = "btStart", Enabled = false };
			btClose = new Button { Text = "Close", Location = new Point(590, 488), Size = new Size(78, 28), Anchor = bottomRight, Name = "btClose" };
			Controls.AddRange(new Control[] { chkBeside, txtFolder, btBrowse, chkOverwrite, chkAdd, chkBatch, lblNote, lblBackground, btStart, btClose });
			LoadChoices();
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
			btClose.Click += delegate { Close(); };
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
			cboLanguage.Enabled = lblLanguage.Enabled = selected.Count > 0;
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
			btStart.Enabled = Ticked.Count > 0 && (chkBeside.Checked || !string.IsNullOrWhiteSpace(txtFolder.Text));
		}

		/// <summary>The choices last used (they are kept in the settings, and the batch translation writes its books with them).</summary>
		private void LoadChoices()
		{
			Settings settings = Program.Settings;
			int format = cboFormat.Items.Cast<FormatChoice>().ToList().FindIndex((FormatChoice c) => c.FormatId == settings.TranslatorExportFormatId && c.Rar == settings.TranslatorExportRar);
			cboFormat.SelectedIndex = Math.Max(0, format);
			chkBeside.Checked = settings.TranslatorExportBeside || string.IsNullOrWhiteSpace(settings.TranslatorExportFolder);
			txtFolder.Text = settings.TranslatorExportFolder ?? "";
			chkAdd.Checked = settings.TranslatorExportAddToLibrary;
			chkBatch.Checked = settings.TranslatorBatchExport;
		}

		private void SaveChoices()
		{
			Settings settings = Program.Settings;
			settings.TranslatorExportFormatId = Choice.FormatId;
			settings.TranslatorExportRar = Choice.Rar;
			settings.TranslatorExportBeside = chkBeside.Checked;
			settings.TranslatorExportFolder = string.IsNullOrWhiteSpace(txtFolder.Text) ? null : txtFolder.Text.Trim();
			settings.TranslatorExportAddToLibrary = chkAdd.Checked;
			settings.TranslatorBatchExport = chkBatch.Checked;
		}

		private void Start()
		{
			List<Job> ticked = Ticked;
			if (ticked.Count == 0)
			{
				return;
			}
			if (!TranslationServices.HelperUp)
			{
				MessageBox.Show(this, "The translator is not running. Start it with the Translation services button: the translated pages are kept by the translator.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}
			SaveChoices();
			bool overwrite = chkOverwrite.Checked;
			List<KeyValuePair<Job, TranslationExportQueue.Item>> todo = ticked.Select((Job j) => new KeyValuePair<Job, TranslationExportQueue.Item>(j, TranslationExportQueue.Item.For(j.Book, j.Target, j.Complete, overwrite))).ToList();
			//the same book twice into one language (an unfinished one set to a language it is complete in too) is written once
			todo = todo.GroupBy((KeyValuePair<Job, TranslationExportQueue.Item> w) => w.Value.TargetPath, StringComparer.OrdinalIgnoreCase).Select((IGrouping<string, KeyValuePair<Job, TranslationExportQueue.Item>> g) => g.OrderByDescending((KeyValuePair<Job, TranslationExportQueue.Item> w) => w.Key.Complete).First()).ToList();
			//books the background export is writing already, or will, are not queued twice
			List<KeyValuePair<Job, TranslationExportQueue.Item>> queued = todo.Where((KeyValuePair<Job, TranslationExportQueue.Item> w) => TranslationExportQueue.IsQueued(w.Value.TargetPath)).ToList();
			foreach (KeyValuePair<Job, TranslationExportQueue.Item> w in queued)
			{
				w.Key.Row.SubItems[3].Text = "being exported already";
			}
			todo = todo.Except(queued).ToList();
			if (todo.Count == 0)
			{
				lblNote.Text = "Nothing added: every ticked book is being exported already.";
				return;
			}
			if (!overwrite)
			{
				List<KeyValuePair<Job, TranslationExportQueue.Item>> existing = todo.Where((KeyValuePair<Job, TranslationExportQueue.Item> w) => File.Exists(w.Value.TargetPath)).ToList();
				if (existing.Count > 0)
				{
					//a copy is out of date when the book's translation changed after it was written (pages translated, edited or forgotten)
					TranslationStore store = TranslationStore.Shared;
					List<KeyValuePair<Job, TranslationExportQueue.Item>> changed = existing.Where((KeyValuePair<Job, TranslationExportQueue.Item> w) => store.LastChanged(w.Key.BookKey) > File.GetLastWriteTimeUtc(w.Value.TargetPath).AddSeconds(2)).ToList();
					DialogResult answer = AskExisting(existing.Select((KeyValuePair<Job, TranslationExportQueue.Item> w) => w.Value.TargetPath + (changed.Contains(w) ? "   (changed since)" : "")).ToList(), changed.Count, existing.Count == todo.Count);
					if (answer == DialogResult.Cancel)
					{
						return;
					}
					//Retry: every copy is written again; Yes: only the ones out of date; Ignore: none of them
					List<KeyValuePair<Job, TranslationExportQueue.Item>> again = answer == DialogResult.Retry ? existing : answer == DialogResult.Yes ? changed : new List<KeyValuePair<Job, TranslationExportQueue.Item>>();
					foreach (KeyValuePair<Job, TranslationExportQueue.Item> w in existing)
					{
						if (again.Contains(w))
						{
							w.Value.Overwrite = true;
						}
						else
						{
							w.Key.Row.SubItems[3].Text = changed.Contains(w) ? "skipped: exported already" : "skipped: exported, unchanged since";
						}
					}
					todo = todo.Where((KeyValuePair<Job, TranslationExportQueue.Item> w) => !existing.Contains(w) || again.Contains(w)).ToList();
					if (todo.Count == 0)
					{
						lblNote.Text = "Nothing written: every ticked book is exported already" + (answer == DialogResult.Yes ? " and unchanged since." : ".");
						return;
					}
				}
			}
			TranslationExportQueue.Start(todo.Select((KeyValuePair<Job, TranslationExportQueue.Item> w) => w.Value).ToList());
			Close();
		}

		/// <summary>
		/// Asks what to do with books whose translated copy is there already: Ignore skips them (the others are written), Yes writes
		/// again only those whose translation changed since, Retry replaces them all, Cancel writes nothing.
		/// </summary>
		private DialogResult AskExisting(List<string> existing, int changed, bool all)
		{
			using (ExistingPrompt prompt = new ExistingPrompt())
			{
				prompt.Text = Text;
				Label message = new Label
				{
					Text = (existing.Count == 1 ? "This file already exists:\n" : existing.Count + " files already exist:\n") + string.Join("\n", existing.Take(8)) + (existing.Count > 8 ? "\n..." : "")
						+ "\n\n" + (all ? "Skip writes nothing: every ticked book is exported already." : "Skip leaves " + (existing.Count == 1 ? "it" : "them") + " as " + (existing.Count == 1 ? "it is" : "they are") + " and writes the other books.")
						+ (changed > 0 ? " Changed only writes again the " + (changed == 1 ? "one" : changed + " books") + " whose translation changed since " + (changed == 1 ? "it was" : "they were") + " written (pages translated or edited)." : " None of them changed since " + (existing.Count == 1 ? "it was" : "they were") + " written.")
						+ " Replace writes " + (existing.Count == 1 ? "it" : "all of them") + " again.",
					Location = new Point(12, 12),
					MaximumSize = new Size(560, 0),
					AutoSize = true
				};
				prompt.Controls.Add(message);
				Size used = message.GetPreferredSize(new Size(560, 0));
				int top = 12 + used.Height + 16;
				Button skip = new Button { Text = "Skip", DialogResult = DialogResult.Ignore, Size = new Size(84, 28), Name = "btSkip" };
				Button update = new Button { Text = changed > 0 ? "Changed only (" + changed + ")" : "Changed only", DialogResult = DialogResult.Yes, Size = new Size(124, 28), Name = "btChanged", Enabled = changed > 0 };
				Button replace = new Button { Text = "Replace", DialogResult = DialogResult.Retry, Size = new Size(84, 28), Name = "btReplace" };
				Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(84, 28), Name = "btCancel" };
				int width = Math.Max(used.Width + 24, 3 * 84 + 124 + 3 * 6 + 24);
				cancel.Location = new Point(width - 12 - 84, top);
				replace.Location = new Point(cancel.Left - 6 - 84, top);
				update.Location = new Point(replace.Left - 6 - 124, top);
				skip.Location = new Point(update.Left - 6 - 84, top);
				prompt.Controls.AddRange(new Control[] { skip, update, replace, cancel });
				prompt.AcceptButton = changed > 0 ? update : skip;
				prompt.CancelButton = cancel;
				prompt.ClientSize = new Size(width, top + 28 + 12);
				return prompt.ShowDialog(this);
			}
		}

		private class ExistingPrompt : FormEx
		{
			public override UIComponent UIComponent => UIComponent.Content;

			public ExistingPrompt()
			{
				AutoScaleMode = AutoScaleMode.None;
				FormBorderStyle = FormBorderStyle.FixedDialog;
				StartPosition = FormStartPosition.CenterParent;
				MaximizeBox = false;
				MinimizeBox = false;
				ShowInTaskbar = false;
			}
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
