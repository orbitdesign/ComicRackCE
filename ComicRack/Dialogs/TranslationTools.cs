using System;
using System.Collections.Generic;
using System.Drawing;
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

	/// <summary>Asks which stored translations to delete: one box, the page on screen or the whole book.</summary>
	public class TranslationCacheDialog : FormEx
	{
		private RadioButton rbBox;

		private RadioButton rbPage;

		private RadioButton rbBook;

		private CheckBox chkEdits;

		private Label lblInfo;

		public override UIComponent UIComponent => UIComponent.Content;

		public TranslationCacheScope Scope => rbBox.Checked ? TranslationCacheScope.Box : (rbBook.Checked ? TranslationCacheScope.Book : TranslationCacheScope.Page);

		/// <summary>Also delete the user's own changes (moved, resized and retyped boxes, boxes added by hand).</summary>
		public bool DeleteEdits => chkEdits.Checked;

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
			ClientSize = new Size(440, 292);
			Color gray = ThemeManager.IsDarkModeEnabled ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);

			Controls.Add(new Label { Text = "Forget the translation, so it is read and translated again:", Location = new Point(14, 14), Size = new Size(412, 18) });
			rbBox = new RadioButton { Text = "This text box only (the selected one)", Location = new Point(24, 40), Size = new Size(396, 22), Enabled = boxAvailable, Name = "rbBox" };
			rbPage = new RadioButton { Text = "The page on screen", Location = new Point(24, 66), Size = new Size(396, 22), Checked = true, Name = "rbPage" };
			rbBook = new RadioButton { Text = "The whole book (" + storedPages + " translated page" + (storedPages == 1 ? "" : "s") + ")", Location = new Point(24, 92), Size = new Size(396, 22), Name = "rbBook" };
			Controls.AddRange(new Control[] { rbBox, rbPage, rbBook });
			lblInfo = new Label { Location = new Point(44, 118), Size = new Size(380, 56), ForeColor = gray, Name = "lblInfo" };
			Controls.Add(lblInfo);
			chkEdits = new CheckBox
			{
				Text = "Also delete my own changes (moved, resized, retyped and added boxes)" + (editedPages > 0 ? " on " + editedPages + " page" + (editedPages == 1 ? "" : "s") : ""),
				Location = new Point(24, 184),
				Size = new Size(396, 38),
				Name = "chkEdits"
			};
			Controls.Add(chkEdits);
			Label title = new Label { Text = bookTitle, Location = new Point(14, 232), Size = new Size(412, 18), ForeColor = gray, AutoEllipsis = true };
			Controls.Add(title);
			Button ok = new Button { Text = "Delete", Location = new Point(262, 254), Size = new Size(80, 28), DialogResult = DialogResult.OK, Name = "btOk" };
			Button cancel = new Button { Text = "Cancel", Location = new Point(348, 254), Size = new Size(80, 28), DialogResult = DialogResult.Cancel, Name = "btCancel" };
			Controls.AddRange(new Control[] { ok, cancel });
			AcceptButton = ok;
			CancelButton = cancel;
			EventHandler update = delegate
			{
				lblInfo.Text = rbBox.Checked ? "The text of this box is read and translated again. Its position and size stay as you set them unless you also delete your changes."
					: rbPage.Checked ? "The page is read again by the vision model and translated again the next time it is shown."
					: "Every page of this book is read again by the vision model and translated again as you read it. This can take a while.";
			};
			rbBox.CheckedChanged += update;
			rbPage.CheckedChanged += update;
			rbBook.CheckedChanged += update;
			update(this, EventArgs.Empty);
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
