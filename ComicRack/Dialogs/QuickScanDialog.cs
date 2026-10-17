using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using cYo.Common.Windows.Forms;
using cYo.Common.Windows.Forms.Theme;

namespace cYo.Projects.ComicRack.Viewer.Dialogs
{
	/// <summary>
	/// A short scan of a few chosen folders, when it is known where new books are: the ticked folders are scanned once for books that
	/// are not in the library yet (books already there are not read again, nothing is removed, and no folder becomes a watched folder).
	/// The list holds the folders scanned this way before, the watched folders and the favorite folders; local folders, mapped drives
	/// and network paths (\\server\share\...) can be added by typing them or by browsing. Whether each folder can be reached is looked
	/// at in the background, so a slow or switched-off computer on the network does not hold the window up.
	/// Built in code (like PresetNameDialog) rather than as designer markup.
	/// </summary>
	public class QuickScanDialog : FormEx
	{
		public class FolderEntry
		{
			public string Path;

			public string Source;

			public bool Ticked;
		}

		private enum Reach
		{
			Checking,
			Yes,
			No,
			NoAnswer
		}

		/// <summary>How long a folder is waited for before it is shown as not answering (a network share can take a while to wake).</summary>
		public static int ReachTimeout = 6000;

		private readonly ListView list;

		private readonly TextBox txtPath;

		private readonly Button btAdd;

		private readonly Button btBrowse;

		private readonly Button btRemove;

		private readonly CheckBox chkSubfolders;

		private readonly Button btScan;

		private readonly Button btCancel;

		private readonly Label lblInfo;

		private readonly Label lblStatus;

		private readonly Dictionary<string, Reach> reach = new Dictionary<string, Reach>(StringComparer.OrdinalIgnoreCase);

		private bool closed;

		/// <summary>The ticked folders, in list order, after the window was closed with Scan.</summary>
		public List<string> SelectedFolders { get; private set; } = new List<string>();

		/// <summary>The folders typed or browsed for in this window (they join the recent list).</summary>
		public List<string> AddedFolders { get; } = new List<string>();

		/// <summary>The recent folders the user removed from the list.</summary>
		public List<string> RemovedFolders { get; } = new List<string>();

		public bool IncludeSubfolders => chkSubfolders.Checked;

		public QuickScanDialog(IEnumerable<FolderEntry> folders, bool subfolders)
		{
			Text = "Quick Scan Folders";
			AutoScaleDimensions = new SizeF(6f, 13f);
			AutoScaleMode = AutoScaleMode.Font;
			FormBorderStyle = FormBorderStyle.Sizable;
			StartPosition = FormStartPosition.CenterParent;
			MaximizeBox = false;
			MinimizeBox = false;
			ShowInTaskbar = false;
			ClientSize = new Size(640, 420);
			MinimumSize = new Size(480, 320);

			lblInfo = new Label
			{
				Text = "Tick the folders with new books. They are scanned once: books not in the library yet are added, books already there are left as they are, nothing is removed, and the folders are not added to the watched folders.",
				Location = new Point(10, 8),
				Size = new Size(620, 30),
				Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
			};
			list = new ListView
			{
				Location = new Point(10, 42),
				Size = new Size(620, 252),
				View = View.Details,
				CheckBoxes = true,
				FullRowSelect = true,
				HideSelection = false,
				MultiSelect = true,
				HeaderStyle = ColumnHeaderStyle.Nonclickable,
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				Name = "lvFolders"
			};
			list.Columns.Add("Folder", 400);
			list.Columns.Add("From", 90);
			list.Columns.Add("Status", 110);
			txtPath = new TextBox
			{
				Location = new Point(10, 302),
				Size = new Size(392, 21),
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				Name = "txtPath"
			};
			btAdd = new Button { Text = "Add", Location = new Point(408, 300), Size = new Size(70, 24), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Name = "btAdd" };
			btBrowse = new Button { Text = "Browse...", Location = new Point(484, 300), Size = new Size(70, 24), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Name = "btBrowse" };
			btRemove = new Button { Text = "Remove", Location = new Point(560, 300), Size = new Size(70, 24), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Name = "btRemove" };
			chkSubfolders = new CheckBox
			{
				Text = "Include subfolders",
				Checked = subfolders,
				Location = new Point(10, 334),
				AutoSize = true,
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Name = "chkSubfolders"
			};
			lblStatus = new Label
			{
				Location = new Point(10, 358),
				Size = new Size(620, 18),
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				Name = "lblStatus"
			};
			btScan = new Button { Text = "Scan", Location = new Point(474, 386), Size = new Size(75, 24), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Name = "btScan" };
			btCancel = new Button { Text = "Cancel", Location = new Point(555, 386), Size = new Size(75, 24), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel, Name = "btCancel" };
			Controls.AddRange(new Control[] { lblInfo, list, txtPath, btAdd, btBrowse, btRemove, chkSubfolders, lblStatus, btScan, btCancel });
			AcceptButton = btScan;
			CancelButton = btCancel;

			foreach (FolderEntry folder in folders ?? Enumerable.Empty<FolderEntry>())
			{
				AddRow(folder.Path, folder.Source, folder.Ticked);
			}
			list.ItemChecked += delegate { UpdateButtons(); };
			list.SelectedIndexChanged += delegate { UpdateButtons(); };
			txtPath.TextChanged += delegate { UpdateButtons(); };
			txtPath.KeyDown += delegate(object sender, KeyEventArgs e)
			{
				if (e.KeyCode == Keys.Enter && txtPath.Text.Trim().Length > 0)
				{
					e.SuppressKeyPress = true;
					AddTyped();
				}
			};
			btAdd.Click += delegate { AddTyped(); };
			btBrowse.Click += delegate { Browse(); };
			btRemove.Click += delegate { RemoveSelected(); };
			btScan.Click += delegate { Scan(); };
			FormClosed += delegate { closed = true; };
			Shown += delegate
			{
				foreach (string path in list.Items.Cast<ListViewItem>().Select((ListViewItem i) => (string)i.Tag).ToList())
				{
					CheckReach(path);
				}
				UpdateButtons();
			};
			UpdateButtons();
		}

		/// <summary>A folder as it is compared and kept: full, without a closing separator ("\\server\share" stays as it is).</summary>
		public static string Normalize(string path)
		{
			path = (path ?? "").Trim().Trim('"');
			if (path.Length == 0)
			{
				return path;
			}
			try
			{
				path = System.IO.Path.GetFullPath(path);
			}
			catch (Exception)
			{
			}
			if (path.Length > 3 && (path.EndsWith("\\") || path.EndsWith("/")))
			{
				path = path.TrimEnd('\\', '/');
			}
			return path;
		}

		private ListViewItem Find(string path)
		{
			return list.Items.Cast<ListViewItem>().FirstOrDefault((ListViewItem i) => string.Equals((string)i.Tag, path, StringComparison.OrdinalIgnoreCase));
		}

		private ListViewItem AddRow(string path, string source, bool ticked)
		{
			path = Normalize(path);
			if (path.Length == 0)
			{
				return null;
			}
			ListViewItem item = Find(path);
			if (item != null)
			{
				item.Checked |= ticked;
				return item;
			}
			item = new ListViewItem(new[] { path, source, "" }) { Tag = path, Checked = ticked, ToolTipText = path };
			list.Items.Add(item);
			if (IsHandleCreated)
			{
				CheckReach(path);
			}
			else
			{
				//the answer can only be shown once the window is there: looked at when it is shown
				reach[path] = Reach.Checking;
				ShowReach(path);
			}
			return item;
		}

		/// <summary>Looks in the background whether a folder is there; a share that does not answer in time is shown as such.</summary>
		private void CheckReach(string path)
		{
			reach[path] = Reach.Checking;
			ShowReach(path);
			Task<bool> look = Task.Run(() =>
			{
				try
				{
					return Directory.Exists(path);
				}
				catch (Exception)
				{
					return false;
				}
			});
			Task.WhenAny(look, Task.Delay(ReachTimeout)).ContinueWith(delegate(Task<Task> done)
			{
				Reach result = done.Result == look ? (look.Result ? Reach.Yes : Reach.No) : Reach.NoAnswer;
				OnUi(delegate
				{
					reach[path] = result;
					ShowReach(path);
					UpdateButtons();
				});
				if (result == Reach.NoAnswer)
				{
					//it may still answer: then it is shown after all
					look.ContinueWith(delegate
					{
						Reach late = look.Result ? Reach.Yes : Reach.No;
						OnUi(delegate
						{
							reach[path] = late;
							ShowReach(path);
							UpdateButtons();
						});
					});
				}
			});
		}

		private void OnUi(Action action)
		{
			if (closed || IsDisposed || !IsHandleCreated)
			{
				return;
			}
			try
			{
				BeginInvoke(action);
			}
			catch (InvalidOperationException)
			{
				//the window went away meanwhile
			}
		}

		private void ShowReach(string path)
		{
			ListViewItem item = Find(path);
			if (item == null)
			{
				return;
			}
			reach.TryGetValue(path, out Reach r);
			item.SubItems[2].Text = r == Reach.Checking ? "checking..." : (r == Reach.Yes ? "found" : (r == Reach.No ? "not found" : "no answer"));
			item.ForeColor = r == Reach.No || r == Reach.NoAnswer ? Color.FromArgb(220, 110, 90) : list.ForeColor;
		}

		private void AddTyped()
		{
			string path = Normalize(txtPath.Text);
			if (path.Length == 0)
			{
				return;
			}
			bool known = Find(path) != null;
			ListViewItem item = AddRow(path, "Added", ticked: true);
			if (item != null)
			{
				item.Checked = true;
				item.EnsureVisible();
				if (!known)
				{
					AddedFolders.Add(path);
				}
			}
			txtPath.Text = "";
			UpdateButtons();
		}

		private void Browse()
		{
			using (FolderBrowserDialog dialog = new FolderBrowserDialog())
			{
				dialog.Description = "A folder with new books (on this computer, a mapped drive or the network)";
				dialog.ShowNewFolderButton = false;
				string start = Normalize(txtPath.Text);
				if (start.Length > 0)
				{
					dialog.SelectedPath = start;
				}
				if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedPath))
				{
					txtPath.Text = dialog.SelectedPath;
					AddTyped();
				}
			}
		}

		private void RemoveSelected()
		{
			foreach (ListViewItem item in list.SelectedItems.Cast<ListViewItem>().Where((ListViewItem i) => IsRemovable(i)).ToList())
			{
				string path = (string)item.Tag;
				list.Items.Remove(item);
				AddedFolders.RemoveAll((string p) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
				RemovedFolders.Add(path);
			}
			UpdateButtons();
		}

		//Watched and favorite folders belong to the library's settings; only the folders of this window can be taken off its list.
		private static bool IsRemovable(ListViewItem item)
		{
			string from = item.SubItems[1].Text;
			return from == "Recent" || from == "Added";
		}

		private List<string> Ticked()
		{
			return list.Items.Cast<ListViewItem>().Where((ListViewItem i) => i.Checked).Select((ListViewItem i) => (string)i.Tag).ToList();
		}

		private void UpdateButtons()
		{
			List<string> ticked = Ticked();
			int missing = ticked.Count((string p) => reach.TryGetValue(p, out Reach r) && r == Reach.No);
			int quiet = ticked.Count((string p) => reach.TryGetValue(p, out Reach r) && r == Reach.NoAnswer);
			btScan.Enabled = ticked.Count > missing;
			btAdd.Enabled = txtPath.Text.Trim().Length > 0;
			btRemove.Enabled = list.SelectedItems.Cast<ListViewItem>().Any(IsRemovable);
			string text = ticked.Count == 0 ? "Tick at least one folder." : ticked.Count + (ticked.Count == 1 ? " folder" : " folders") + " to scan";
			if (missing > 0)
			{
				text += "; " + missing + " not found (left out)";
			}
			if (quiet > 0)
			{
				text += "; " + quiet + " not answering yet (scanned if it answers)";
			}
			lblStatus.Text = ticked.Count == 0 ? text : text + ".";
		}

		private void Scan()
		{
			SelectedFolders = Ticked().Where((string p) => !(reach.TryGetValue(p, out Reach r) && r == Reach.No)).ToList();
			if (SelectedFolders.Count == 0)
			{
				return;
			}
			DialogResult = DialogResult.OK;
			Close();
		}

		/// <summary>The list for the window: the folders scanned this way before (most recent first), the watched folders and the
		/// favorite folders, each once; the ones ticked last time are ticked again.</summary>
		public static List<FolderEntry> BuildList(IEnumerable<string> recent, IEnumerable<string> ticked, IEnumerable<string> watched, IEnumerable<string> favorites)
		{
			HashSet<string> tick = new HashSet<string>((ticked ?? Enumerable.Empty<string>()).Select(Normalize), StringComparer.OrdinalIgnoreCase);
			List<FolderEntry> result = new List<FolderEntry>();
			HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			void Add(IEnumerable<string> paths, string source)
			{
				foreach (string p in paths ?? Enumerable.Empty<string>())
				{
					string path = Normalize(p);
					if (path.Length > 0 && seen.Add(path))
					{
						result.Add(new FolderEntry { Path = path, Source = source, Ticked = tick.Contains(path) });
					}
				}
			}
			Add(recent, "Recent");
			Add(watched, "Watched");
			Add(favorites, "Favorite");
			return result;
		}

		/// <summary>The recent list after a scan: the scanned and added folders first, then the others, without the removed ones.</summary>
		public static List<string> UpdateRecent(IEnumerable<string> recent, IEnumerable<string> scanned, IEnumerable<string> added, IEnumerable<string> removed, int max = 20)
		{
			HashSet<string> gone = new HashSet<string>((removed ?? Enumerable.Empty<string>()).Select(Normalize), StringComparer.OrdinalIgnoreCase);
			List<string> result = new List<string>();
			HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string p in (scanned ?? Enumerable.Empty<string>()).Concat(added ?? Enumerable.Empty<string>()).Concat(recent ?? Enumerable.Empty<string>()))
			{
				string path = Normalize(p);
				if (path.Length > 0 && !gone.Contains(path) && seen.Add(path))
				{
					result.Add(path);
				}
			}
			return result.Take(max).ToList();
		}
	}
}
