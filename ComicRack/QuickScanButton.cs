using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using cYo.Projects.ComicRack.Viewer.Properties;

namespace cYo.Projects.ComicRack.Viewer
{
	/// <summary>
	/// Quick Scan in a book list's toolbar: the button scans the folders ticked in Quick Scan Folders at once (no window), with the
	/// subfolders when that was chosen there; its arrow lists those folders and opens the Quick Scan Folders window to change them.
	/// </summary>
	public static class QuickScanButton
	{
		//Folders listed by name in the menu; the rest are counted.
		private const int ListedFolders = 12;

		public static void Install(ToolStrip strip, int index)
		{
			int size = Math.Max(16, strip.ImageScalingSize.Height);
			ToolStripSplitButton button = new ToolStripSplitButton
			{
				Name = "tbQuickScan",
				DisplayStyle = ToolStripItemDisplayStyle.Image,
				//yellow when idle, amber while books are being scanned (any scan: this one, a full scan, a watched folder)
				Image = GetIcon(false, size),
				ImageScaling = ToolStripItemImageScaling.None,
				AutoToolTip = false,
				Text = "Quick Scan"
			};
			bool shownBusy = false;
			Timer watch = new Timer { Interval = 700 };
			watch.Tick += delegate
			{
				bool busy = IsScanning;
				if (busy != shownBusy)
				{
					shownBusy = busy;
					button.Image = GetIcon(busy, size);
				}
			};
			watch.Start();
			strip.Disposed += delegate
			{
				watch.Stop();
				watch.Dispose();
			};
			ToolStripMenuItem scanNow = new ToolStripMenuItem("Scan the ticked folders now", GetIcon(false, 16));
			scanNow.Click += delegate { ScanNow(); };
			ToolStripMenuItem choose = new ToolStripMenuItem("Quick Scan Folders...")
			{
				ShortcutKeyDisplayString = "Ctrl+Alt+S"
			};
			choose.Click += delegate { Program.MainForm?.QuickScanFolders(); };
			ToolStripSeparator listSeparator = new ToolStripSeparator();
			button.DropDownItems.AddRange(new ToolStripItem[] { scanNow, choose, listSeparator });
			List<ToolStripItem> folderLines = new List<ToolStripItem>();
			button.ButtonClick += delegate { ScanNow(); };
			button.DropDownOpening += delegate
			{
				//the ticked folders, as the next scan will take them
				foreach (ToolStripItem line in folderLines)
				{
					button.DropDownItems.Remove(line);
					line.Dispose();
				}
				folderLines.Clear();
				List<string> folders = Ticked();
				scanNow.Enabled = folders.Count > 0;
				scanNow.Text = folders.Count == 0 ? "Scan the ticked folders now (none ticked yet)" : "Scan the " + (folders.Count == 1 ? "ticked folder" : folders.Count + " ticked folders") + " now" + (Program.Settings.QuickScanSubfolders ? ", with subfolders" : "");
				listSeparator.Visible = folders.Count > 0;
				foreach (string folder in folders.Take(ListedFolders))
				{
					folderLines.Add(new ToolStripMenuItem(folder) { Enabled = false });
				}
				if (folders.Count > ListedFolders)
				{
					folderLines.Add(new ToolStripMenuItem("... and " + (folders.Count - ListedFolders) + " more") { Enabled = false });
				}
				button.DropDownItems.AddRange(folderLines.ToArray());
			};
			button.MouseEnter += delegate
			{
				List<string> folders = Ticked();
				string location = IsScanning ? Program.Scanner?.CurrentLocation : null;
				button.ToolTipText = IsScanning
					? "Scanning for new books" + (string.IsNullOrEmpty(location) ? "..." : ": " + location) + "\nThe arrow: choose the folders (Quick Scan Folders)."
					: folders.Count == 0
					? "Quick Scan: no folders are ticked yet. Click to choose the folders to scan."
					: "Quick Scan: look for new books in the " + (folders.Count == 1 ? "ticked folder" : folders.Count + " ticked folders") + (Program.Settings.QuickScanSubfolders ? " and their subfolders" : "") + " now.\n" + string.Join("\n", folders.Take(ListedFolders)) + (folders.Count > ListedFolders ? "\n... and " + (folders.Count - ListedFolders) + " more" : "") + "\nThe arrow: choose the folders (Quick Scan Folders).";
			};
			index = Math.Max(0, Math.Min(index, strip.Items.Count));
			strip.Items.Insert(index, new ToolStripSeparator());
			strip.Items.Insert(index + 1, button);
		}

		private static bool IsScanning
		{
			get
			{
				try
				{
					return Program.Scanner != null && Program.Scanner.IsScanning;
				}
				catch (Exception)
				{
					return false;
				}
			}
		}

		private static readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>();

		/// <summary>A folder with a magnifier over it: yellow, or amber while scanning (the amber of the translator's busy light).</summary>
		public static Bitmap GetIcon(bool busy, int size)
		{
			string key = (busy ? "busy" : "idle") + size;
			lock (icons)
			{
				if (icons.TryGetValue(key, out Bitmap cached))
				{
					return cached;
				}
				Bitmap bmp = new Bitmap(size, size);
				using (Graphics g = Graphics.FromImage(bmp))
				{
					g.SmoothingMode = SmoothingMode.AntiAlias;
					float u = size / 16f;
					Color fill = busy ? Color.FromArgb(245, 150, 20) : Color.FromArgb(250, 215, 60);
					Color back = busy ? Color.FromArgb(200, 110, 10) : Color.FromArgb(215, 175, 30);
					Color rim = Color.FromArgb(70, 50, 15);
					using (Pen pen = new Pen(rim, Math.Max(1f, 0.9f * u)) { LineJoin = LineJoin.Round })
					using (SolidBrush backBrush = new SolidBrush(back))
					using (SolidBrush fillBrush = new SolidBrush(fill))
					{
						//the folder: its tab and back, then its front
						PointF[] rear = { new PointF(1 * u, 3 * u), new PointF(5.5f * u, 3 * u), new PointF(7 * u, 4.5f * u), new PointF(14 * u, 4.5f * u), new PointF(14 * u, 13 * u), new PointF(1 * u, 13 * u) };
						g.FillPolygon(backBrush, rear);
						g.DrawPolygon(pen, rear);
						PointF[] front = { new PointF(1 * u, 6.5f * u), new PointF(14 * u, 6.5f * u), new PointF(14 * u, 13 * u), new PointF(1 * u, 13 * u) };
						g.FillPolygon(fillBrush, front);
						g.DrawPolygon(pen, front);
					}
					//the magnifier at the bottom right: a white glass in a dark ring, and its handle
					using (Pen ring = new Pen(Color.FromArgb(40, 40, 46), Math.Max(1.2f, 1.3f * u)))
					using (Pen handle = new Pen(Color.FromArgb(40, 40, 46), Math.Max(1.6f, 2.0f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
					using (SolidBrush glass = new SolidBrush(Color.FromArgb(235, 245, 255)))
					{
						RectangleF lens = new RectangleF(7.2f * u, 6.8f * u, 5.2f * u, 5.2f * u);
						g.FillEllipse(glass, lens);
						g.DrawEllipse(ring, lens);
						g.DrawLine(handle, 11.8f * u, 11.4f * u, 14.6f * u, 14.4f * u);
					}
				}
				icons[key] = bmp;
				return bmp;
			}
		}

		/// <summary>The folders ticked in Quick Scan Folders, without empties and repeats.</summary>
		private static List<string> Ticked()
		{
			return (Program.Settings?.QuickScanTicked ?? new List<string>()).Where((string f) => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		}

		/// <summary>Scans the ticked folders now, as Quick Scan Folders would; with none ticked yet, opens that window to choose them.</summary>
		private static void ScanNow()
		{
			List<string> folders = Ticked();
			if (folders.Count == 0)
			{
				Program.MainForm?.QuickScanFolders();
				return;
			}
			Program.Scanner.ScanFilesOrFolders(folders, all: Program.Settings.QuickScanSubfolders, removeMissing: false);
		}
	}
}
