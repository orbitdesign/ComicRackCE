using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace cYo.Projects.ComicRack.Viewer
{
	/// <summary>
	/// Opens books in ComicRack Lite (1.186): "Open in ComicRack Lite" in the library's right-click menu (the selected books,
	/// each in a window of its own) and in the reader's page and tab menus (the book shown, at the page shown).
	/// </summary>
	public static class LiteLauncher
	{
		private const int AskAbove = 5;

		private static Image icon;

		public static string Exe => Path.Combine(Application.StartupPath, "ComicRackLite.exe");

		public static bool Available => File.Exists(Exe);

		/// <summary>Lite's own logo, small, for the menus.</summary>
		public static Image Icon
		{
			get
			{
				if (icon == null && Available)
				{
					try
					{
						using (Icon large = System.Drawing.Icon.ExtractAssociatedIcon(Exe))
						using (Bitmap full = large.ToBitmap())
						{
							Bitmap small = new Bitmap(16, 16);
							using (Graphics g = Graphics.FromImage(small))
							{
								g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
								g.DrawImage(full, new Rectangle(0, 0, 16, 16));
							}
							icon = small;
						}
					}
					catch (Exception)
					{
					}
				}
				return icon;
			}
		}

		/// <summary>The menu text for this many books.</summary>
		public static string Text(int count)
		{
			return count > 1 ? "Open " + count + " Books in ComicRack Lite" : "Open in ComicRack Lite";
		}

		/// <summary>
		/// Opens the books in ComicRack Lite, each in its own window; with a page (the image's number, from 1) the book opens
		/// there, else where Lite last left it. Asks first for more than a few books.
		/// </summary>
		public static void Open(IWin32Window owner, IEnumerable<string> files, int page = 0)
		{
			List<string> books = files.Where((string f) => !string.IsNullOrEmpty(f) && (File.Exists(f) || Directory.Exists(f))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (!Available)
			{
				MessageBox.Show(owner, "ComicRackLite.exe is not in " + Application.StartupPath + ".", "ComicRack Lite", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}
			if (books.Count == 0)
			{
				MessageBox.Show(owner, "The selected books are not files on this computer, so ComicRack Lite cannot open them.", "ComicRack Lite", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}
			if (books.Count > AskAbove && MessageBox.Show(owner, "Open " + books.Count + " books in ComicRack Lite? Each opens in a window of its own.", "ComicRack Lite", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
			{
				return;
			}
			foreach (string book in books)
			{
				try
				{
					string args = "\"" + book.TrimEnd('\\') + "\"" + (page > 0 ? " -p " + page : "");
					Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = false, WorkingDirectory = Application.StartupPath });
				}
				catch (Exception ex)
				{
					MessageBox.Show(owner, "ComicRack Lite could not be started: " + ex.Message, "ComicRack Lite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
					return;
				}
			}
		}
	}
}
