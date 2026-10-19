using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Management;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using cYo.Common.Windows.Forms.Theme;
using cYo.Projects.ComicRack.Viewer.Dialogs;
using cYo.Projects.ComicRack.Engine;
using cYo.Projects.ComicRack.Engine.Display.Forms;

namespace cYo.Projects.ComicRack.Viewer
{
	/// <summary>
	/// Knows whether the ComicTranslator helper and Ollama are running, and starts and stops them for the
	/// toolbar buttons. The helper is found by the port it listens on, so nothing else is touched.
	/// </summary>
	public static class TranslationServices
	{
		private static readonly object sync = new object();

		private static System.Threading.Timer timer;

		private static string ollamaUrl = "http://127.0.0.1:11434";

		private static bool lastHelper;

		private static int missed;

		private static bool lastOllama;

		private static bool lastBusy;

		public static bool HelperUp { get; private set; }

		public static bool OllamaUp { get; private set; }

		/// <summary>True while a start or stop is under way.</summary>
		public static bool Busy { get; private set; }

		/// <summary>What went wrong the last time something was started or stopped, or null.</summary>
		public static string Problem { get; private set; }

		public static event EventHandler Changed;

		private static string HelperUrl => string.IsNullOrWhiteSpace(Program.Settings.TranslatorServerUrl) ? "http://127.0.0.1:8790" : Program.Settings.TranslatorServerUrl.Trim().TrimEnd('/');

		/// <summary>Starts watching the two services (once).</summary>
		public static void StartWatching()
		{
			lock (sync)
			{
				if (timer == null)
				{
					timer = new System.Threading.Timer(delegate { Poll(); }, null, 300, 4000);
				}
			}
		}

		public static void PollSoon()
		{
			ThreadPool.QueueUserWorkItem(delegate { Poll(); });
		}

		private static DateTime configRead = DateTime.MinValue;

		private static void Poll()
		{
			//Liveness is asked with the helper's light health call. A helper that is busy lettering a page can be slow to answer
			//anything: slow is not stopped. Only a refused connection (nothing listening) counts, and then twice in a row.
			Reach reach = Get(HelperUrl + "/health", out _);
			bool helper;
			if (reach == Reach.Refused)
			{
				missed++;
				helper = missed < 2 && HelperUp;
			}
			else
			{
				missed = 0;
				helper = true;
			}
			if (helper && reach == Reach.Answered && DateTime.UtcNow - configRead > TimeSpan.FromSeconds(60))
			{
				//The address of Ollama is in the helper's settings; read now and then, not every few seconds.
				if (Get(HelperUrl + "/api/config", out string body) == Reach.Answered && body != null)
				{
					configRead = DateTime.UtcNow;
					Match m = Regex.Match(body, "\"ollama_url\"\\s*:\\s*\"([^\"]+)\"");
					if (m.Success)
					{
						ollamaUrl = m.Groups[1].Value.TrimEnd('/');
					}
				}
			}
			//Ollama too: busy (slow to answer) is still running.
			Reach ollamaReach = Get(ollamaUrl + "/api/tags", out _);
			bool ollama = ollamaReach == Reach.Answered || (ollamaReach == Reach.Slow && OllamaUp);
			HelperUp = helper;
			OllamaUp = ollama;
			if (helper != lastHelper || ollama != lastOllama || Busy != lastBusy)
			{
				lastHelper = helper;
				lastOllama = ollama;
				lastBusy = Busy;
				Changed?.Invoke(null, EventArgs.Empty);
			}
		}

		private enum Reach
		{
			Answered,
			Refused,
			Slow
		}

		private static Reach Get(string url, out string body)
		{
			body = null;
			try
			{
				HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
				request.Proxy = null;
				request.Timeout = 5000;
				request.ReadWriteTimeout = 5000;
				using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
				using (StreamReader reader = new StreamReader(response.GetResponseStream()))
				{
					body = reader.ReadToEnd();
					return Reach.Answered;
				}
			}
			catch (WebException ex)
			{
				if (ex.Response != null)
				{
					ex.Response.Dispose();
					return Reach.Answered;          //an error answer is still an answer
				}
				return ex.Status == WebExceptionStatus.Timeout ? Reach.Slow : Reach.Refused;
			}
			catch (Exception)
			{
				return Reach.Refused;
			}
		}

		private static void SetBusy(bool busy, string problem = null)
		{
			Busy = busy;
			Problem = problem;
			lastBusy = busy;
			Changed?.Invoke(null, EventArgs.Empty);
		}

		private static void Work(Action action)
		{
			if (Busy)
			{
				return;
			}
			SetBusy(true);
			ThreadPool.QueueUserWorkItem(delegate
			{
				string problem = null;
				try
				{
					action();
				}
				catch (Exception ex)
				{
					problem = ex.Message;
				}
				Poll();
				SetBusy(false, problem);
			});
		}

		/// <summary>
		/// Called as ComicRack closes: ends the helper and Ollama (and its model runner) if they are running, and
		/// waits for that, so nothing is left holding video memory. Quick when there is nothing to stop.
		/// </summary>
		public static void StopOnExit()
		{
			try
			{
				//Quick checks only (a closed port answers at once), so closing is not held up when nothing is running.
				if (PortOpen(HelperUrl))
				{
					KillHelper();
				}
				if (Process.GetProcessesByName("ollama").Length > 0 || Process.GetProcessesByName("ollama app").Length > 0 || Process.GetProcessesByName("llama-server").Length > 0)
				{
					KillOllama();
				}
			}
			catch (Exception)
			{
				//Closing must never fail because of this.
			}
		}

		private static bool PortOpen(string url)
		{
			try
			{
				Uri uri = new Uri(url);
				using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient())
				{
					IAsyncResult result = client.BeginConnect(uri.Host, uri.Port, null, null);
					return result.AsyncWaitHandle.WaitOne(400) && client.Connected;
				}
			}
			catch (Exception)
			{
				return false;
			}
		}

		// ---- both together ----

		/// <summary>Stops both when either is running, otherwise starts both.</summary>
		public static void ToggleAll(IWin32Window owner)
		{
			if (HelperUp || OllamaUp)
			{
				StopAll();
			}
			else
			{
				StartAll(owner);
			}
		}

		public static void StartAll(IWin32Window owner)
		{
			string folder = HelperUp ? null : FindHelperFolder(owner);
			if (!HelperUp && folder == null)
			{
				return;
			}
			bool needOllama = !OllamaUp;
			bool needHelper = !HelperUp;
			Work(delegate
			{
				if (needOllama)
				{
					LaunchOllama();
					WaitFor(delegate { return OllamaPolled(); }, 60);
				}
				if (needHelper)
				{
					LaunchHelper(folder);
					WaitFor(delegate { return HelperPolled(); }, 180);
				}
			});
		}

		public static void StopAll()
		{
			Work(delegate
			{
				KillHelper();
				KillOllama();
				WaitFor(delegate { return !HelperPolled() && !OllamaPolled(); }, 15);
			});
		}

		public static void ToggleHelper(IWin32Window owner)
		{
			if (HelperUp)
			{
				Work(delegate
				{
					KillHelper();
					WaitFor(delegate { return !HelperPolled(); }, 15);
				});
				return;
			}
			string folder = FindHelperFolder(owner);
			if (folder == null)
			{
				return;
			}
			Work(delegate
			{
				LaunchHelper(folder);
				WaitFor(delegate { return HelperPolled(); }, 180);
			});
		}

		public static void ToggleOllama()
		{
			if (OllamaUp)
			{
				Work(delegate
				{
					KillOllama();
					WaitFor(delegate { return !OllamaPolled(); }, 15);
				});
			}
			else
			{
				Work(delegate
				{
					LaunchOllama();
					WaitFor(delegate { return OllamaPolled(); }, 60);
				});
			}
		}

		private static bool HelperPolled()
		{
			return Get(HelperUrl + "/health", out _) != Reach.Refused;
		}

		private static bool OllamaPolled()
		{
			return Get(ollamaUrl + "/api/tags", out _) != Reach.Refused;
		}

		private static void WaitFor(Func<bool> condition, int seconds)
		{
			DateTime until = DateTime.UtcNow.AddSeconds(seconds);
			while (DateTime.UtcNow < until)
			{
				if (condition())
				{
					return;
				}
				Thread.Sleep(700);
			}
		}

		// ---- the helper ----

		/// <summary>Where the setup program puts the translator helper (its files are there when the component was installed).</summary>
		public static string InstalledHelperFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicTranslator");

		private static string FindHelperFolder(IWin32Window owner)
		{
			string folder = Program.Settings.TranslatorHelperFolder;
			if (!string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, "run.bat")))
			{
				return folder;
			}
			if (File.Exists(Path.Combine(InstalledHelperFolder, "run.bat")))
			{
				Program.Settings.TranslatorHelperFolder = InstalledHelperFolder;
				return InstalledHelperFolder;
			}
			using (FolderBrowserDialog dialog = new FolderBrowserDialog())
			{
				dialog.Description = "Choose the ComicTranslator folder (the one that contains run.bat).";
				if (dialog.ShowDialog(owner) != DialogResult.OK)
				{
					return null;
				}
				if (!File.Exists(Path.Combine(dialog.SelectedPath, "run.bat")))
				{
					MessageBox.Show(owner, "There is no run.bat in that folder.", "ComicTranslator", MessageBoxButtons.OK, MessageBoxIcon.Information);
					return null;
				}
				Program.Settings.TranslatorHelperFolder = dialog.SelectedPath;
				return dialog.SelectedPath;
			}
		}

		private static void LaunchHelper(string folder)
		{
			Process.Start(new ProcessStartInfo(Path.Combine(folder, "run.bat"))
			{
				WorkingDirectory = folder,
				UseShellExecute = true,
				WindowStyle = ProcessWindowStyle.Minimized
			});
		}

		private static void KillHelper()
		{
			int port = 8790;
			try
			{
				port = new Uri(HelperUrl).Port;
			}
			catch (Exception)
			{
			}
			int pid = ListeningProcess(port);
			if (pid <= 0)
			{
				return;
			}
			//Go up to the run.bat window that started the helper, so no empty console is left behind.
			int top = pid;
			for (int i = 0; i < 4; i++)
			{
				int parent = ParentOf(top, out string name);
				if (parent <= 0 || !(name.Equals("python.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)))
				{
					break;
				}
				top = parent;
			}
			using (Process kill = Process.Start(new ProcessStartInfo("taskkill", "/PID " + top + " /T /F") { CreateNoWindow = true, UseShellExecute = false }))
			{
				kill.WaitForExit(8000);
			}
		}

		private static int ListeningProcess(int port)
		{
			ProcessStartInfo info = new ProcessStartInfo("netstat", "-ano -p TCP") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
			using (Process p = Process.Start(info))
			{
				string text = p.StandardOutput.ReadToEnd();
				p.WaitForExit(5000);
				foreach (string line in text.Split('\n'))
				{
					string[] parts = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length >= 5 && parts[3] == "LISTENING" && parts[1].EndsWith(":" + port) && int.TryParse(parts[4], out int pid))
					{
						return pid;
					}
				}
			}
			return 0;
		}

		private static int ParentOf(int pid, out string parentName)
		{
			parentName = "";
			try
			{
				using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid))
				{
					foreach (ManagementObject mo in searcher.Get())
					{
						int parent = Convert.ToInt32(mo["ParentProcessId"]);
						try
						{
							parentName = Process.GetProcessById(parent).ProcessName + ".exe";
						}
						catch (Exception)
						{
							return 0;
						}
						return parent;
					}
				}
			}
			catch (Exception)
			{
			}
			return 0;
		}

		// ---- Ollama ----

		private static void LaunchOllama()
		{
			string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama");
			string app = Path.Combine(dir, "ollama app.exe");
			string cli = Path.Combine(dir, "ollama.exe");
			if (File.Exists(app))
			{
				Process.Start(new ProcessStartInfo(app) { WorkingDirectory = dir, UseShellExecute = true });
			}
			else if (File.Exists(cli))
			{
				Process.Start(new ProcessStartInfo(cli, "serve") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true });
			}
			else
			{
				Process.Start(new ProcessStartInfo("ollama", "serve") { UseShellExecute = false, CreateNoWindow = true });
			}
		}

		private static void KillOllama()
		{
			//The tray app and the server first, with everything they started (taskkill /T), or the tray app starts
			//the server again. Then any model runner left behind: the runner (llama-server.exe in current versions)
			//holds the model in video memory and carries on after its parent has gone.
			foreach (string name in new[] { "ollama app", "ollama" })
			{
				foreach (Process p in Process.GetProcessesByName(name))
				{
					try
					{
						using (Process kill = Process.Start(new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F") { CreateNoWindow = true, UseShellExecute = false }))
						{
							kill.WaitForExit(8000);
						}
					}
					catch (Exception)
					{
					}
					p.Dispose();
				}
			}
			foreach (string name in new[] { "llama-server", "ollama_llama_server", "ollama-runner" })
			{
				foreach (Process p in Process.GetProcessesByName(name))
				{
					try
					{
						//Only runners that belong to Ollama.
						string path = null;
						try
						{
							path = p.MainModule?.FileName;
						}
						catch (Exception)
						{
						}
						if (path == null || path.IndexOf("ollama", StringComparison.OrdinalIgnoreCase) >= 0)
						{
							p.Kill();
						}
					}
					catch (Exception)
					{
					}
					p.Dispose();
				}
			}
		}
	}

	public enum TranslationLed
	{
		Off,
		On,
		Busy,
		Problem,

		/// <summary>Blue: pages are only being loaded from what was translated before.</summary>
		Loading
	}

	/// <summary>
	/// The two toolbar buttons for page translation: one that turns translating on and off and doubles as its
	/// status light, one that starts and stops the helper and Ollama. Their pictures change colour with the state.
	/// </summary>
	public static class TranslationButtons
	{
		private class Pair
		{
			public ToolStrip Strip;

			public ToolStripSplitButton Translator;

			public ToolStripSplitButton Services;

			public ToolStripMenuItem StatusPanel;

			public ToolStripMenuItem English;

			public ToolStripMenuItem Chinese;

			public ToolStripMenuItem HelperItem;

			public ToolStripMenuItem OllamaItem;

			public ToolStripMenuItem StateItem;

			public ToolStripMenuItem ExitItem;

			//A split button: its arrow opens the menu; the button opens it too, except while a batch waits to be continued (paused or
			//stopped, with the small play mark on its icon), when the button continues the batch.
			public ToolStripSplitButton Ahead;

			public ToolStripMenuItem AheadState;

			public ToolStripMenuItem AheadStop;

			public ToolStripMenuItem BatchStart;

			public ToolStripMenuItem BatchState;

			public ToolStripMenuItem BatchContinue;

			public ToolStripMenuItem BatchPause;

			public ToolStripMenuItem BatchCancel;

			//The books of the batch, listed under its state line
			public readonly List<ToolStripMenuItem> BatchBooks = new List<ToolStripMenuItem>();

			public Font BatchCurrentFont;

			public int AheadLed = -1;

			public TranslationLed TranslatorLed = (TranslationLed)(-1);

			public TranslationLed ServicesLed = (TranslationLed)(-1);

			public int Size;
		}

		private static readonly List<Pair> pairs = new List<Pair>();

		private static readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>();

		private static bool hooked;

		private static MainForm Main => Program.MainForm;

		/// <summary>Adds the two buttons to a toolbar at the given position.</summary>
		public static void Install(ToolStrip strip, int index, bool separatorBefore, bool separatorAfter)
		{
			Pair pair = new Pair { Strip = strip };
			pair.Translator = new ToolStripSplitButton
			{
				Name = "tbTranslator",
				DisplayStyle = ToolStripItemDisplayStyle.Image,
				AutoToolTip = false,
				Text = "Translate Pages"
			};
			pair.Translator.ButtonClick += delegate { Main?.ToggleTranslatePages(); };
			int iconSize = Math.Max(16, strip.ImageScalingSize.Height);
			pair.StatusPanel = new ToolStripMenuItem("Show Translation Status Panel", ActionIcon(TranslationAction.StatusPanel, iconSize));
			pair.StatusPanel.Click += delegate
			{
				ComicDisplayControl reader = Main?.TranslationReader;
				if (reader != null)
				{
					reader.TranslationStatusVisible = !reader.TranslationStatusVisible;
					Program.Settings.TranslatorShowStatus = reader.TranslationStatusVisible;
				}
			};
			//The language the pages are translated into, one click away: the helper's own setting, changed here without the settings window.
			pair.English = new ToolStripMenuItem("Translate to English", GetMenuIcon(MenuIcon.Language, "EN", iconSize), delegate { Main?.SetTranslationLanguage("en"); });
			pair.Chinese = new ToolStripMenuItem("Translate to Chinese", GetMenuIcon(MenuIcon.Language, "中", iconSize), delegate { Main?.SetTranslationLanguage(targetCode.StartsWith("zh") ? targetCode : "zh-CN"); });
			pair.Translator.DropDownItems.Add(pair.English);
			pair.Translator.DropDownItems.Add(pair.Chinese);
			pair.Translator.DropDownItems.Add(new ToolStripSeparator());
			pair.Translator.DropDownItems.Add(pair.StatusPanel);
			pair.Translator.DropDownItems.Add(new ToolStripSeparator());
			pair.Translator.DropDownItems.Add(new ToolStripMenuItem("Edit Translated Text...", ActionIcon(TranslationAction.Edit, iconSize), delegate { Main?.OpenTranslationEditor(); }));
			pair.Translator.DropDownItems.Add(new ToolStripMenuItem("Delete Translation Cache...", ActionIcon(TranslationAction.DeleteCache, iconSize), delegate { Main?.OpenTranslationCache(); }));
			pair.Translator.DropDownItems.Add(new ToolStripMenuItem("Export Translated Book...", ActionIcon(TranslationAction.Export, iconSize), delegate { Main?.OpenTranslationExport(); }));
			pair.Translator.DropDownItems.Add(new ToolStripSeparator());
			pair.Translator.DropDownItems.Add(new ToolStripMenuItem("Translation Settings...", ActionIcon(TranslationAction.Settings, iconSize), delegate { Main?.OpenTranslationSettings(); }));
			pair.Translator.DropDownOpening += delegate
			{
				pair.StatusPanel.Checked = Main?.TranslationReader?.TranslationStatusVisible ?? true;
				ShowTarget(pair);
				RefreshTarget(pair);
			};

			pair.Services = new ToolStripSplitButton
			{
				Name = "tbTranslatorServices",
				DisplayStyle = ToolStripItemDisplayStyle.Image,
				AutoToolTip = false,
				Text = "Translator and Ollama"
			};
			pair.Services.ButtonClick += delegate { TranslationServices.ToggleAll(Main); };
			pair.StateItem = new ToolStripMenuItem { Enabled = false, Image = ActionIcon(TranslationAction.Info, iconSize) };
			pair.HelperItem = new ToolStripMenuItem();
			pair.HelperItem.Click += delegate { TranslationServices.ToggleHelper(Main); };
			pair.OllamaItem = new ToolStripMenuItem();
			pair.OllamaItem.Click += delegate { TranslationServices.ToggleOllama(); };
			pair.Services.DropDownItems.Add(pair.StateItem);
			pair.Services.DropDownItems.Add(new ToolStripSeparator());
			pair.Services.DropDownItems.Add(pair.HelperItem);
			pair.Services.DropDownItems.Add(pair.OllamaItem);
			pair.Services.DropDownItems.Add(new ToolStripSeparator());
			pair.ExitItem = new ToolStripMenuItem("Stop both when ComicRack closes", ActionIcon(TranslationAction.StopOnExit, iconSize));
			pair.ExitItem.Click += delegate { Program.Settings.TranslatorStopOnExit = !Program.Settings.TranslatorStopOnExit; };
			pair.Services.DropDownItems.Add(pair.ExitItem);
			pair.Services.DropDownOpening += delegate
			{
				pair.ExitItem.Checked = Program.Settings.TranslatorStopOnExit;
				Update(pair);
			};

			pair.Ahead = new ToolStripSplitButton
			{
				Name = "tbTranslateAhead",
				DisplayStyle = ToolStripItemDisplayStyle.Image,
				AutoToolTip = false,
				Text = "Translate pages ahead",
				ToolTipText = "Translate pages ahead of reading"
			};
			pair.AheadState = new ToolStripMenuItem { Enabled = false };
			pair.Ahead.DropDownItems.Add(pair.AheadState);
			pair.Ahead.DropDownItems.Add(new ToolStripSeparator());
			int menuSize = Math.Max(16, strip.ImageScalingSize.Height);
			foreach (int n in new[] { 5, 10, 15, 25, 50 })
			{
				int count = n;
				pair.Ahead.DropDownItems.Add(new ToolStripMenuItem("Next " + count + " pages", GetMenuIcon(MenuIcon.Pages, count.ToString(), menuSize), delegate { Main?.TranslationReader?.StartPreTranslate(count); }));
			}
			pair.Ahead.DropDownItems.Add(new ToolStripMenuItem("The rest of the book", GetMenuIcon(MenuIcon.Rest, null, menuSize), delegate { Main?.TranslationReader?.StartPreTranslate(0); }));
			pair.Ahead.DropDownItems.Add(new ToolStripMenuItem("The whole book", GetMenuIcon(MenuIcon.Book, null, menuSize), delegate { Main?.TranslationReader?.StartPreTranslate(0, fromStart: true); }));
			pair.Ahead.DropDownItems.Add(new ToolStripSeparator());
			pair.AheadStop = new ToolStripMenuItem("Stop translating ahead", GetMenuIcon(MenuIcon.Stop, null, menuSize), delegate { Main?.TranslationReader?.StopPreTranslate(); });
			pair.Ahead.DropDownItems.Add(pair.AheadStop);
			pair.Ahead.DropDownItems.Add(new ToolStripSeparator());
			//Several books at once: the books selected in the library are translated whole, one after the other, in the background.
			pair.BatchStart = new ToolStripMenuItem("Translate the selected books (whole books)", GetMenuIcon(MenuIcon.Book, null, menuSize), delegate
			{
				ComicBook[] books = Main?.GetBooksForTranslation() ?? new ComicBook[0];
				if (books.Length > 0)
				{
					TranslationBatch.Start(books);
				}
			});
			pair.BatchState = new ToolStripMenuItem { Enabled = false, Visible = false };
			pair.BatchPause = new ToolStripMenuItem("Pause the batch", GetMenuIcon(MenuIcon.Stop, null, menuSize), delegate { TranslationBatch.Pause(); });
			pair.BatchContinue = new ToolStripMenuItem("Continue translating previously selected books");
			pair.BatchContinue.Click += delegate { TranslationBatch.Resume(); };
			pair.BatchCancel = new ToolStripMenuItem("Forget the queue", GetMenuIcon(MenuIcon.Stop, null, menuSize), delegate { TranslationBatch.Cancel(); });
			pair.Ahead.DropDownItems.AddRange(new ToolStripItem[] { pair.BatchStart, pair.BatchState, pair.BatchPause, pair.BatchContinue, pair.BatchCancel });
			pair.Ahead.DropDownOpening += delegate { Update(pair); };
			pair.Ahead.ButtonClick += delegate
			{
				if (CanContinueBatch)
				{
					TranslationBatch.Resume();
					//It did not start (the helper is not running): the menu says why.
					if (!TranslationBatch.IsRunning)
					{
						pair.Ahead.ShowDropDown();
					}
				}
				else
				{
					pair.Ahead.ShowDropDown();
				}
			};

			//After a button or one of its menu entries was used, the keyboard goes back to the reader (the toolbar kept it,
			//and then shortcut keys such as E no longer reached the reader).
			foreach (ToolStripItem button in new ToolStripItem[] { pair.Translator, pair.Services, pair.Ahead })
			{
				ToolStripDropDownItem dropDownButton = (ToolStripDropDownItem)button;
				dropDownButton.DropDownClosed += delegate { GiveKeyboardBack(strip); };
				dropDownButton.DropDownItemClicked += delegate { GiveKeyboardBack(strip); };
				dropDownButton.Click += delegate { GiveKeyboardBack(strip); };
			}

			List<ToolStripItem> items = new List<ToolStripItem>();
			if (separatorBefore)
			{
				items.Add(new ToolStripSeparator());
			}
			items.Add(pair.Translator);
			items.Add(pair.Services);
			items.Add(pair.Ahead);
			if (separatorAfter)
			{
				items.Add(new ToolStripSeparator());
			}
			index = Math.Max(0, Math.Min(index, strip.Items.Count));
			foreach (ToolStripItem item in items)
			{
				strip.Items.Insert(index++, item);
			}
			lock (pairs)
			{
				pairs.Add(pair);
			}
			strip.Disposed += delegate
			{
				lock (pairs)
				{
					pairs.Remove(pair);
				}
			};
			if (!hooked)
			{
				hooked = true;
				TranslationServices.Changed += delegate { RefreshAll(); };
				TranslationBatch.Changed += delegate { RefreshAll(); };
				TranslationServices.StartWatching();
			}
			Update(pair);
		}

		private static void GiveKeyboardBack(ToolStrip strip)
		{
			try
			{
				if (!strip.IsDisposed && strip.IsHandleCreated)
				{
					strip.BeginInvoke((Action)delegate { Main?.RestoreKeyboardFocus(); });
				}
			}
			catch (Exception)
			{
			}
		}

		private static ToolStripStatusLabel batchStatus;

		private static int batchStatusLed = -1;

		/// <summary>Adds the batch's progress to the status bar, before the given item: the book picture in the state's colour and
		/// the book being translated with its pages; only while a batch runs or has books waiting. A click opens the batch menu.</summary>
		public static void InstallBatchStatus(StatusStrip strip, ToolStripItem before)
		{
			batchStatus = new ToolStripStatusLabel
			{
				Name = "tsTranslationBatch",
				BorderSides = ToolStripStatusLabelBorderSides.All,
				BorderStyle = Border3DStyle.SunkenOuter,
				DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
				ImageAlign = ContentAlignment.MiddleLeft,
				TextImageRelation = TextImageRelation.ImageBeforeText,
				Visible = false
			};
			batchStatus.Click += delegate
			{
				Pair p;
				lock (pairs)
				{
					p = pairs.FirstOrDefault((Pair x) => !x.Strip.IsDisposed && x.Strip.Visible && x.Ahead.Visible);
				}
				p?.Ahead.ShowDropDown();
			};
			int index = before != null ? strip.Items.IndexOf(before) : -1;
			if (index >= 0)
			{
				strip.Items.Insert(index, batchStatus);
			}
			else
			{
				strip.Items.Add(batchStatus);
			}
			//The theme gave the status bar's other entries their border when the window was made (the dark theme draws a faded
			//grey one in place of the white sunken edge); it is applied again so this entry gets the same. Entries already
			//themed are left as they are.
			try
			{
				strip.Theme();
			}
			catch (Exception)
			{
			}
			UpdateBatchStatus();
		}

		private static void UpdateBatchStatus()
		{
			ToolStripStatusLabel label = batchStatus;
			if (label == null || label.Owner == null || label.Owner.IsDisposed)
			{
				return;
			}
			List<TranslationBatch.Item> waiting = TranslationBatch.Waiting;
			bool running = TranslationBatch.IsRunning;
			TranslationBatch.Item current = TranslationBatch.Current;
			string problem = TranslationBatch.Problem;
			string notice = TranslationBatch.Notice;
			bool show = running || waiting.Count > 0 || notice != null;
			TranslationLed led = running ? TranslationLed.Busy : (problem != null || notice != null ? TranslationLed.Problem : TranslationLed.On);
			string text = "";
			string tip = "";
			if (running && current != null)
			{
				int pages = current.Pages;
				string done = pages > 0 ? Math.Min(current.Translated, pages) + "/" + pages : PercentText(current);
				int others = waiting.Count((TranslationBatch.Item i) => i != current);
				text = (TranslationBatch.Yielding ? "Batch waiting: " : "Batch: ") + ShortTitle(current.Title) + "  " + done + (others > 0 ? "  (+" + others + (others == 1 ? " book" : " books") + ")" : "");
				tip = (TranslationBatch.Yielding ? "Batch translation waiting while the reader translates pages." : "Batch translation in the background.") + "\n" + BookListText(waiting, current) + "\nClick for the batch menu.";
			}
			else if (running)
			{
				text = "Batch: starting...";
				tip = "Batch translation starting. Click for the batch menu.";
			}
			else if (waiting.Count > 0 && TranslationBatch.DoneWithSkips)
			{
				int skipped = waiting.Sum((TranslationBatch.Item i) => i.Skipped.Count);
				text = "Batch done: " + skipped + (skipped == 1 ? " page" : " pages") + " skipped";
				tip = problem + (notice != null ? "\n" + notice : "") + "\n" + BookListText(waiting, null) + "\nClick to try the skipped pages again or forget the queue.";
			}
			else if (waiting.Count == 0 && notice != null)
			{
				int pages = TranslationBatch.Noted.Sum((TranslationBatch.Item i) => i.LeftAsDrawn.Count);
				text = "Batch done: " + pages + (pages == 1 ? " page needs" : " pages need") + " a look";
				tip = notice + "\nClick for the batch menu.";
			}
			else if (waiting.Count > 0)
			{
				text = (problem != null ? "Batch stopped: " : "Batch paused: ") + waiting.Count + (waiting.Count == 1 ? " book" : " books") + " waiting";
				tip = text + "." + (problem != null ? " " + problem : "") + "\n" + BookListText(waiting, null) + "\nClick to continue or forget the queue.";
			}
			int size = Math.Max(16, label.Owner.ImageScalingSize.Height);
			if ((int)led != batchStatusLed || label.Image == null)
			{
				batchStatusLed = (int)led;
				label.Image = GetBatchIcon(led, size);
			}
			if (label.Text != text)
			{
				label.Text = text;
			}
			label.ToolTipText = tip;
			label.Visible = show;
		}

		//At most this many books are listed by name in the batch menu and the tooltips; the rest are counted.
		private const int ListedBooks = 15;

		/// <summary>The books of the batch, one per line: the one being translated first, then those waiting.</summary>
		private static string BookListText(List<TranslationBatch.Item> waiting, TranslationBatch.Item current)
		{
			StringBuilder sb = new StringBuilder();
			if (current != null)
			{
				sb.Append("Now translating: ").Append(Shorten(current.Title)).Append(" (").Append(ProgressText(current)).Append(")");
			}
			List<TranslationBatch.Item> next = waiting.Where((TranslationBatch.Item i) => i != current).ToList();
			if (next.Count > 0)
			{
				if (sb.Length > 0)
				{
					sb.Append("\n");
				}
				sb.Append(current != null ? "Next:" : "Waiting:");
				foreach (TranslationBatch.Item item in next.Take(ListedBooks))
				{
					sb.Append("\n    ").Append(Shorten(item.Title)).Append(" (").Append(PercentText(item)).Append(item.Skipped.Count > 0 ? ", " + TranslationBatch.SkippedText(item) + " skipped" : "").Append(item.LeftAsDrawn.Count > 0 ? ", a box left as drawn on " + TranslationBatch.PagesText(item.LeftAsDrawn) : "").Append(")");
				}
				if (next.Count > ListedBooks)
				{
					sb.Append("\n    ... and ").Append(next.Count - ListedBooks).Append(" more");
				}
			}
			return sb.ToString();
		}

		/// <summary>"12/40 pages" when the book's page count is known, else the percent.</summary>
		private static string ProgressText(TranslationBatch.Item item)
		{
			int pages = item.Pages;
			return pages > 0 ? Math.Min(item.Translated, pages) + "/" + pages + " pages" : PercentText(item);
		}

		private static string ShortTitle(string text)
		{
			text = text ?? "";
			return text.Length > 40 ? text.Substring(0, 38) + "..." : text;
		}

		/// <summary>Brings every button up to date; safe to call from any thread.</summary>
		public static void RefreshAll()
		{
			ToolStripStatusLabel label = batchStatus;
			if (label?.Owner != null && !label.Owner.IsDisposed)
			{
				try
				{
					if (label.Owner.InvokeRequired)
					{
						if (label.Owner.IsHandleCreated)
						{
							label.Owner.BeginInvoke((Action)UpdateBatchStatus);
						}
					}
					else
					{
						UpdateBatchStatus();
					}
				}
				catch (Exception)
				{
					//The window is closing.
				}
			}
			Pair[] copy;
			lock (pairs)
			{
				copy = pairs.ToArray();
			}
			foreach (Pair pair in copy)
			{
				Pair p = pair;
				try
				{
					if (p.Strip.IsDisposed)
					{
						continue;
					}
					if (p.Strip.InvokeRequired)
					{
						if (p.Strip.IsHandleCreated)
						{
							p.Strip.BeginInvoke((Action)delegate { Update(p); });
						}
					}
					else
					{
						Update(p);
					}
				}
				catch (Exception)
				{
					//The window is closing.
				}
			}
		}

		private static void Update(Pair p)
		{
			if (p.Strip.IsDisposed)
			{
				return;
			}
			int size = Math.Max(16, p.Strip.ImageScalingSize.Height);
			ComicDisplayControl reader = Main?.TranslationReader;

			TranslationLed led = TranslationLed.Off;
			string tip = "Translate pages (E): off. Click to turn on.";
			if (reader != null && reader.TranslatePages)
			{
				reader.GetTranslationStatus(out int translated, out int total, out bool busy, out string problem);
				if (!string.IsNullOrEmpty(problem))
				{
					led = TranslationLed.Problem;
					tip = "Translate pages: " + problem;
				}
				else if (busy && reader.TranslationLoadingFromCache && !reader.TranslatingInBackground)
				{
					led = TranslationLed.Loading;
					tip = "Translate pages: loading translated pages from the cache... " + translated + " / " + total + " pages done. Click to turn off.";
				}
				else if (busy)
				{
					led = TranslationLed.Busy;
					tip = "Translate pages: " + (reader.TranslationRedrawing ? "drawing translated pages again (new settings or a new picture version; nothing is read or translated)... " : "translating... ") + translated + " / " + total + " pages done. Click to turn off.";
				}
				else if (reader.TranslatingInBackground)
				{
					//The reader has nothing to do, but a batch is translating: amber, as for any real translating.
					led = TranslationLed.Busy;
					string title = TranslationBatch.Current?.Title;
					tip = "Translate pages: on, " + translated + " / " + total + " pages of this book translated. Batch translating" + (string.IsNullOrEmpty(title) ? "" : " \"" + title + "\"") + " in the background. Click to turn off.";
				}
				else
				{
					led = TranslationLed.On;
					tip = "Translate pages: on" + (total > 0 ? ", " + translated + " / " + total + " pages translated" : "") + ". Click to turn off.";
				}
			}
			if (led != p.TranslatorLed || size != p.Size)
			{
				p.TranslatorLed = led;
				p.Translator.Image = GetIcon(true, led, size);
			}
			p.Translator.ToolTipText = tip;

			bool helper = TranslationServices.HelperUp;
			bool ollama = TranslationServices.OllamaUp;
			TranslationLed sled = TranslationServices.Busy ? TranslationLed.Busy : (helper && ollama ? TranslationLed.On : (helper || ollama ? TranslationLed.Busy : TranslationLed.Off));
			if (sled != p.ServicesLed || size != p.Size)
			{
				p.ServicesLed = sled;
				p.Services.Image = GetIcon(false, sled, size);
			}
			bool ahead = (reader != null && reader.IsPreTranslating) || TranslationBatch.IsRunning;
			//The icon's colour tells the state: plain when idle, amber while translating ahead or a batch, blue while a
			//batch is paused with books waiting, red when something stopped it.
			TranslationLed aheadLed = ahead ? TranslationLed.Busy : (TranslationBatch.Problem != null && TranslationBatch.HasWaiting ? TranslationLed.Problem : (TranslationBatch.HasWaiting ? TranslationLed.On : TranslationLed.Off));
			//A batch (running or with books waiting) shows the book of "Translate the selected books"; translating ahead in the
			//book being read shows the pages with the arrow.
			bool batch = TranslationBatch.IsRunning || TranslationBatch.HasWaiting;
			//A batch waiting to be continued (blue: paused, red: stopped) gets the small play mark: a click on the icon continues it.
			bool play = batch && CanContinueBatch;
			int aheadState = (int)aheadLed + (batch ? 100 : 0) + (play ? 1000 : 0);
			if (aheadState != p.AheadLed || size != p.Size)
			{
				p.AheadLed = aheadState;
				p.Ahead.Image = batch ? GetBatchIcon(aheadLed, size, play) : GetAheadIcon(aheadLed, size);
			}
			if (reader != null)
			{
				reader.GetPreTranslateProgress(out int aheadDone, out int aheadTotal);
				p.AheadState.Text = ahead ? "Translating ahead: " + aheadDone + " of " + aheadTotal + " pages" : "Translate the pages after this one before you get to them";
				p.Ahead.ToolTipText = ahead ? "Translating ahead: " + aheadDone + " of " + aheadTotal + " pages. Click to change or stop." : "Translate pages ahead of reading (5, 10, 15... or the whole book)";
			}
			p.AheadStop.Enabled = ahead;
			UpdateBatchMenu(p);
			p.Size = size;
			string state = "Translator helper: " + (helper ? "running" : "stopped") + ".  Ollama: " + (ollama ? "running" : "stopped") + ".";
			p.StateItem.Text = state;
			p.HelperItem.Text = helper ? "Stop Translator Helper" : "Start Translator Helper";
			p.OllamaItem.Text = ollama ? "Stop Ollama" : "Start Ollama";
			//what a click does: a red square stops, a green triangle starts
			int itemSize = Math.Max(16, p.Services.Owner?.ImageScalingSize.Height ?? 16);
			p.HelperItem.Image = ActionIcon(helper ? TranslationAction.HelperStop : TranslationAction.HelperStart, itemSize);
			p.OllamaItem.Image = ActionIcon(ollama ? TranslationAction.OllamaStop : TranslationAction.OllamaStart, itemSize);
			p.HelperItem.Enabled = p.OllamaItem.Enabled = !TranslationServices.Busy;
			string action = TranslationServices.Busy ? "working..." : (helper || ollama ? "Click to stop both." : "Click to start both.");
			p.Services.ToolTipText = state + " " + action + (string.IsNullOrEmpty(TranslationServices.Problem) ? "" : "  Problem: " + TranslationServices.Problem);
		}

		private static void UpdateBatchMenu(Pair p)
		{
			int selected = Main?.GetBooksForTranslation().Length ?? 0;
			p.BatchStart.Text = selected > 1 ? "Translate the " + selected + " selected books (whole books)" : "Translate the selected book (whole book)";
			p.BatchStart.Enabled = selected > 0;
			List<TranslationBatch.Item> waiting = TranslationBatch.Waiting;
			bool running = TranslationBatch.IsRunning;
			TranslationBatch.Item current = TranslationBatch.Current;
			string problem = TranslationBatch.Problem;
			string notice = TranslationBatch.Notice;
			p.BatchState.Visible = running || waiting.Count > 0 || problem != null || notice != null;
			if (running && current != null)
			{
				p.BatchState.Text = "Batch: " + (TranslationBatch.Yielding ? "waiting while the reader translates pages, " : "translating, ") + waiting.Count + (waiting.Count == 1 ? " book" : " books") + " left";
			}
			else if (running)
			{
				p.BatchState.Text = "Batch: starting, " + waiting.Count + (waiting.Count == 1 ? " book" : " books") + " left";
			}
			else if (waiting.Count > 0 && TranslationBatch.DoneWithSkips)
			{
				int skipped = waiting.Sum((TranslationBatch.Item i) => i.Skipped.Count);
				p.BatchState.Text = "Batch done: " + skipped + (skipped == 1 ? " page" : " pages") + " could not be translated and " + (skipped == 1 ? "was" : "were") + " skipped";
			}
			else if (waiting.Count > 0)
			{
				p.BatchState.Text = "Batch paused: " + waiting.Count + (waiting.Count == 1 ? " book" : " books") + " left" + (problem != null ? ". " + problem : "");
			}
			else if (notice != null)
			{
				int pages = TranslationBatch.Noted.Sum((TranslationBatch.Item i) => i.LeftAsDrawn.Count);
				p.BatchState.Text = "Batch done: on " + pages + (pages == 1 ? " page" : " pages") + " a box could not be lettered (translator error) and was left as drawn";
			}
			else
			{
				p.BatchState.Text = problem ?? "";
			}
			p.BatchPause.Visible = running;
			p.BatchCancel.Visible = running || waiting.Count > 0 || notice != null;
			p.BatchCancel.Text = running || waiting.Count > 0 ? "Forget the queue" : "Clear this message";
			p.BatchContinue.Visible = !running && waiting.Count > 0;
			p.BatchContinue.Text = TranslationBatch.DoneWithSkips && waiting.All((TranslationBatch.Item i) => i.Skipped.Count > 0)
				? "Try the skipped pages again (" + waiting.Count + (waiting.Count == 1 ? " book" : " books") + ")"
				: "Continue translating previously selected books (" + waiting.Count + (waiting.Count == 1 ? " book" : " books") + " left)";
			//After a batch, the books with a box left as drawn are listed with their pages.
			UpdateBatchBooks(p, running || waiting.Count > 0 || notice == null ? waiting : TranslationBatch.Noted, running ? current : null);
			if (running)
			{
				p.Ahead.ToolTipText = (TranslationBatch.Yielding ? "Batch waiting while the reader translates pages." : "Batch translating books in the background.") + "\n" + BookListText(waiting, current) + "\nClick for the batch menu.";
			}
			else if (waiting.Count > 0 && TranslationBatch.DoneWithSkips)
			{
				p.Ahead.ToolTipText = problem + "\n" + BookListText(waiting, null) + "\nClick to try the skipped pages again; the arrow opens the menu.";
			}
			else if (waiting.Count > 0)
			{
				p.Ahead.ToolTipText = "Batch paused: " + waiting.Count + (waiting.Count == 1 ? " book" : " books") + " waiting." + (problem != null ? " " + problem : "") + "\n" + BookListText(waiting, null) + "\nClick to continue; the arrow opens the menu.";
			}
			else if (notice != null)
			{
				p.Ahead.ToolTipText = notice;
			}
		}

		/// <summary>Lists the batch's books under its state line: the one being translated in bold with the batch's picture and its
		/// pages, then those waiting with how much of each is done. The entries are kept and their text changed, so the open menu
		/// does not flicker while the pages are counted.</summary>
		private static void UpdateBatchBooks(Pair p, List<TranslationBatch.Item> waiting, TranslationBatch.Item current)
		{
			List<TranslationBatch.Item> order = new List<TranslationBatch.Item>();
			if (current != null)
			{
				order.Add(current);
			}
			order.AddRange(waiting.Where((TranslationBatch.Item i) => i != current));
			int listed = Math.Min(order.Count, ListedBooks);
			int lines = listed + (order.Count > listed ? 1 : 0);
			ToolStripItemCollection menu = p.Ahead.DropDownItems;
			while (p.BatchBooks.Count > lines)
			{
				ToolStripMenuItem gone = p.BatchBooks[p.BatchBooks.Count - 1];
				p.BatchBooks.RemoveAt(p.BatchBooks.Count - 1);
				menu.Remove(gone);
				gone.Dispose();
			}
			while (p.BatchBooks.Count < lines)
			{
				ToolStripMenuItem line = new ToolStripMenuItem { Enabled = false };
				menu.Insert(menu.IndexOf(p.BatchState) + 1 + p.BatchBooks.Count, line);
				p.BatchBooks.Add(line);
			}
			int size = Math.Max(16, p.Strip.ImageScalingSize.Height);
			for (int i = 0; i < lines; i++)
			{
				ToolStripMenuItem line = p.BatchBooks[i];
				bool now = i == 0 && current != null;
				string text;
				if (i >= listed)
				{
					text = "... and " + (order.Count - listed) + " more";
				}
				else if (now)
				{
					text = Shorten(order[i].Title) + "   (" + ProgressText(order[i]) + ")";
				}
				else
				{
					text = PercentText(order[i]).PadLeft(6) + "   " + Shorten(order[i].Title) + (order[i].Skipped.Count > 0 ? "   (" + TranslationBatch.SkippedText(order[i]) + " skipped)" : "")
						+ (order[i].LeftAsDrawn.Count > 0 ? "   (a box left as drawn on " + TranslationBatch.PagesText(order[i].LeftAsDrawn) + ")" : "");
				}
				if (line.Text != text)
				{
					line.Text = text;
				}
				//The book being translated is not greyed out like the queue, so it stands out; clicking it does nothing.
				line.Enabled = now;
				if (now)
				{
					if (p.BatchCurrentFont == null)
					{
						p.BatchCurrentFont = new Font(p.Ahead.DropDown.Font, FontStyle.Bold);
					}
					line.Font = p.BatchCurrentFont;
					line.Image = GetBatchIcon(TranslationBatch.Yielding ? TranslationLed.On : TranslationLed.Busy, size);
					line.ToolTipText = TranslationBatch.Yielding ? "Waiting while the reader translates pages" : "Being translated now";
				}
				else
				{
					line.Font = null;
					line.Image = null;
					line.ToolTipText = null;
				}
			}
		}

		private static string PercentText(TranslationBatch.Item item)
		{
			int percent = item.Percent;
			return percent < 0 ? "queued" : percent + "%";
		}

		private static string Shorten(string text)
		{
			text = text ?? "";
			return text.Length > 70 ? text.Substring(0, 67) + "..." : text;
		}

		// ---- pictures ----

		private static Color ColorOf(TranslationLed led)
		{
			switch (led)
			{
			case TranslationLed.On:
				return Color.FromArgb(70, 200, 95);
			case TranslationLed.Busy:
				return Color.FromArgb(245, 175, 30);
			case TranslationLed.Problem:
				return Color.FromArgb(230, 65, 55);
			case TranslationLed.Loading:
				return Color.FromArgb(70, 150, 245);
			}
			return Color.FromArgb(135, 135, 140);
		}

		/// <summary>The translator button's picture (a speech bubble) in the colour of a state, for status lights elsewhere. The caller owns the copy.</summary>
		public static Bitmap StatusIcon(TranslationLed led, int size)
		{
			return new Bitmap(GetIcon(true, led, size));
		}

		private static Bitmap GetIcon(bool translator, TranslationLed led, int size)
		{
			string key = (translator ? "t" : "s") + (int)led + "-" + size;
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
					if (translator)
					{
						DrawSpeechBubble(g, size, ColorOf(led));
					}
					else
					{
						DrawPower(g, size, ColorOf(led));
					}
				}
				icons[key] = bmp;
				return bmp;
			}
		}

		/// <summary>The translator's menu entries, each with a picture of its own.</summary>
		public enum TranslationAction
		{
			StatusPanel,
			Edit,
			DeleteCache,
			Export,
			Settings,
			Info,
			HelperStart,
			HelperStop,
			OllamaStart,
			OllamaStop,
			StopOnExit,
			Translate
		}

		/// <summary>The picture of a translator menu entry (shared, do not dispose): drawn to be readable on a light or a dark menu.</summary>
		public static Bitmap ActionIcon(TranslationAction kind, int size)
		{
			//its own names in the shared store ("a2-16" was also the translate-ahead button's amber picture)
			string key = "act" + (int)kind + "-" + size;
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
					g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
					DrawAction(g, kind, size);
				}
				icons[key] = bmp;
				return bmp;
			}
		}

		private static void DrawAction(Graphics g, TranslationAction kind, int size)
		{
			float u = size / 16f;
			Color paper = Color.FromArgb(240, 240, 244);
			Color rim = Color.FromArgb(70, 70, 78);
			Color amber = Color.FromArgb(245, 175, 30);
			Color blue = Color.FromArgb(70, 150, 245);
			Color red = Color.FromArgb(220, 65, 55);
			Color green = Color.FromArgb(60, 180, 90);
			using (Pen rimPen = new Pen(rim, Math.Max(1f, u)) { LineJoin = LineJoin.Round })
			using (Pen faint = new Pen(Color.FromArgb(150, rim), Math.Max(1f, u)))
			using (SolidBrush paperBrush = new SolidBrush(paper))
			{
				switch (kind)
				{
				case TranslationAction.StatusPanel:
					{
						//a status bar with the translator's bubble and its text
						RectangleF bar = new RectangleF(0.8f * u, 4.5f * u, 14.4f * u, 7 * u);
						g.FillRectangle(paperBrush, bar);
						g.DrawRectangle(rimPen, bar.X, bar.Y, bar.Width, bar.Height);
						using (SolidBrush a = new SolidBrush(amber))
						{
							g.FillEllipse(a, 2.2f * u, 5.8f * u, 4.6f * u, 4 * u);
							g.FillPolygon(a, new[] { new PointF(3 * u, 9 * u), new PointF(2.6f * u, 10.8f * u), new PointF(4.6f * u, 9.4f * u) });
						}
						g.DrawLine(faint, 8.3f * u, 7 * u, 13.6f * u, 7 * u);
						g.DrawLine(faint, 8.3f * u, 9 * u, 12 * u, 9 * u);
						break;
					}
				case TranslationAction.Edit:
					{
						//a page of text and a pencil
						RectangleF page = new RectangleF(1.5f * u, 1 * u, 9.5f * u, 13.5f * u);
						g.FillRectangle(paperBrush, page);
						g.DrawRectangle(rimPen, page.X, page.Y, page.Width, page.Height);
						for (int i = 0; i < 4; i++)
						{
							g.DrawLine(faint, 3.2f * u, (4 + 2.4f * i) * u, (i == 3 ? 6.5f : 9.3f) * u, (4 + 2.4f * i) * u);
						}
						using (Pen pencil = new Pen(amber, Math.Max(2f, 2.6f * u)) { StartCap = LineCap.Flat, EndCap = LineCap.Triangle })
						{
							g.DrawLine(pencil, 14.8f * u, 4.2f * u, 8.6f * u, 12.6f * u);
						}
						using (Pen tip = new Pen(rim, Math.Max(1f, 1.2f * u)))
						{
							g.DrawLine(tip, 8.9f * u, 12.2f * u, 8 * u, 13.6f * u);
						}
						break;
					}
				case TranslationAction.DeleteCache:
					{
						//a bin with its lid
						PointF[] body = { new PointF(3.5f * u, 5 * u), new PointF(12.5f * u, 5 * u), new PointF(11.5f * u, 15 * u), new PointF(4.5f * u, 15 * u) };
						g.FillPolygon(paperBrush, body);
						g.DrawPolygon(rimPen, body);
						g.DrawLine(faint, 6.5f * u, 7 * u, 6.8f * u, 13 * u);
						g.DrawLine(faint, 9.5f * u, 7 * u, 9.2f * u, 13 * u);
						using (Pen lid = new Pen(red, Math.Max(1.6f, 2 * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
						{
							g.DrawLine(lid, 2 * u, 3.4f * u, 14 * u, 3.4f * u);
							g.DrawLine(lid, 6.3f * u, 1.4f * u, 9.7f * u, 1.4f * u);
						}
						break;
					}
				case TranslationAction.Export:
					{
						//an open book, and a blue arrow taking it out (saved as a file)
						PointF[] left = { new PointF(0.6f * u, 5 * u), new PointF(6.6f * u, 6.3f * u), new PointF(6.6f * u, 15 * u), new PointF(0.6f * u, 13.7f * u) };
						PointF[] right = { new PointF(12.6f * u, 5 * u), new PointF(6.6f * u, 6.3f * u), new PointF(6.6f * u, 15 * u), new PointF(12.6f * u, 13.7f * u) };
						g.FillPolygon(paperBrush, left);
						g.FillPolygon(paperBrush, right);
						g.DrawPolygon(rimPen, left);
						g.DrawPolygon(rimPen, right);
						g.DrawLine(faint, 2 * u, 8.3f * u, 5.2f * u, 9 * u);
						g.DrawLine(faint, 2 * u, 10.6f * u, 5.2f * u, 11.3f * u);
						using (Pen arrow = new Pen(blue, Math.Max(1.6f, 2.2f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
						{
							g.DrawLine(arrow, 9.2f * u, 6.8f * u, 14.8f * u, 1.2f * u);
							g.DrawLines(arrow, new[] { new PointF(10.6f * u, 1.2f * u), new PointF(14.8f * u, 1.2f * u), new PointF(14.8f * u, 5.4f * u) });
						}
						break;
					}
				case TranslationAction.Settings:
					{
						//a gear
						PointF c = new PointF(8 * u, 8 * u);
						List<PointF> teeth = new List<PointF>();
						for (int i = 0; i < 32; i++)
						{
							double angle = Math.PI * 2 * i / 32;
							float r = (i % 4 == 0 || i % 4 == 1) ? 7.2f * u : 5.4f * u;
							teeth.Add(new PointF(c.X + (float)Math.Cos(angle) * r, c.Y + (float)Math.Sin(angle) * r));
						}
						using (SolidBrush steel = new SolidBrush(Color.FromArgb(165, 165, 175)))
						{
							g.FillPolygon(steel, teeth.ToArray());
						}
						g.DrawPolygon(rimPen, teeth.ToArray());
						g.FillEllipse(paperBrush, c.X - 2.4f * u, c.Y - 2.4f * u, 4.8f * u, 4.8f * u);
						g.DrawEllipse(rimPen, c.X - 2.4f * u, c.Y - 2.4f * u, 4.8f * u, 4.8f * u);
						break;
					}
				case TranslationAction.Info:
					{
						using (SolidBrush b = new SolidBrush(blue))
						{
							g.FillEllipse(b, 1 * u, 1 * u, 14 * u, 14 * u);
						}
						using (SolidBrush white = new SolidBrush(Color.White))
						{
							g.FillEllipse(white, 7 * u, 3.4f * u, 2.2f * u, 2.2f * u);
							g.FillRectangle(white, 7.05f * u, 6.6f * u, 2.1f * u, 6 * u);
						}
						break;
					}
				case TranslationAction.HelperStart:
				case TranslationAction.HelperStop:
					{
						//the translator's bubble, with what a click does
						GraphicsState state = g.Save();
						g.ScaleTransform(0.78f, 0.78f);
						DrawSpeechBubble(g, size, amber);
						g.Restore(state);
						DrawBadge(g, u, kind == TranslationAction.HelperStart, green, red);
						break;
					}
				case TranslationAction.OllamaStart:
				case TranslationAction.OllamaStop:
					{
						//a chip (the language model), with what a click does
						RectangleF chip = new RectangleF(2.5f * u, 2 * u, 8.5f * u, 8.5f * u);
						using (Pen pins = new Pen(Color.FromArgb(150, 150, 160), Math.Max(1f, u)))
						{
							for (int i = 0; i < 3; i++)
							{
								float p = (3.9f + 2.8f * i) * u;
								g.DrawLine(pins, p, 0.5f * u, p, 2 * u);
								g.DrawLine(pins, p, 10.5f * u, p, 12 * u);
								g.DrawLine(pins, 1 * u, p - 1.4f * u + 0.6f * u, 2.5f * u, p - 1.4f * u + 0.6f * u);
								g.DrawLine(pins, 11 * u, p - 1.4f * u + 0.6f * u, 12.5f * u, p - 1.4f * u + 0.6f * u);
							}
						}
						using (SolidBrush body = new SolidBrush(Color.FromArgb(95, 95, 108)))
						{
							g.FillRectangle(body, chip);
						}
						g.DrawRectangle(rimPen, chip.X, chip.Y, chip.Width, chip.Height);
						using (Font font = new Font("Segoe UI", Math.Max(5f, 5.4f * u), FontStyle.Bold, GraphicsUnit.Pixel))
						using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
						{
							g.DrawString("AI", font, Brushes.White, chip, sf);
						}
						DrawBadge(g, u, kind == TranslationAction.OllamaStart, green, red);
						break;
					}
				case TranslationAction.StopOnExit:
					{
						//a door standing open in its frame, and an arrow out of it
						using (Pen frame = new Pen(Color.FromArgb(150, 150, 160), Math.Max(1f, 1.2f * u)))
						{
							g.DrawRectangle(frame, 1.2f * u, 1 * u, 8.6f * u, 14 * u);
						}
						PointF[] door = { new PointF(1.2f * u, 1 * u), new PointF(6.6f * u, 2.6f * u), new PointF(6.6f * u, 13.4f * u), new PointF(1.2f * u, 15 * u) };
						g.FillPolygon(paperBrush, door);
						g.DrawPolygon(rimPen, door);
						using (SolidBrush knob = new SolidBrush(rim))
						{
							g.FillEllipse(knob, 4.6f * u, 7.4f * u, 1.5f * u, 1.5f * u);
						}
						using (Pen arrow = new Pen(red, Math.Max(1.6f, 2.2f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
						{
							g.DrawLine(arrow, 8.5f * u, 11.5f * u, 14.8f * u, 11.5f * u);
							g.DrawLines(arrow, new[] { new PointF(12.3f * u, 9 * u), new PointF(14.8f * u, 11.5f * u), new PointF(12.3f * u, 14 * u) });
						}
						break;
					}
				default:
					DrawSpeechBubble(g, size, amber);
					break;
				}
			}
		}

		/// <summary>A small round badge at the lower right: a green triangle (starts) or a red square (stops).</summary>
		private static void DrawBadge(Graphics g, float u, bool start, Color green, Color red)
		{
			RectangleF circle = new RectangleF(8.4f * u, 8.4f * u, 7.4f * u, 7.4f * u);
			using (SolidBrush fill = new SolidBrush(start ? green : red))
			using (Pen edge = new Pen(Color.White, Math.Max(1f, 0.9f * u)))
			{
				g.FillEllipse(fill, circle);
				g.DrawEllipse(edge, circle);
			}
			using (SolidBrush white = new SolidBrush(Color.White))
			{
				if (start)
				{
					g.FillPolygon(white, new[] { new PointF(11.2f * u, 10.3f * u), new PointF(11.2f * u, 13.9f * u), new PointF(14.1f * u, 12.1f * u) });
				}
				else
				{
					g.FillRectangle(white, 10.6f * u, 10.6f * u, 3 * u, 3 * u);
				}
			}
		}

		private enum MenuIcon
		{
			Pages,
			Rest,
			Book,
			Stop,
			Language,
			LanguageOn
		}

		//The language the helper translates into (its target_code), as last seen; the menu shows a tick on it.
		private static string targetCode = "";

		private static void ShowTarget(Pair pair)
		{
			bool english = targetCode == "en", chinese = targetCode.StartsWith("zh");
			int size = Math.Max(16, pair.Strip.ImageScalingSize.Height);
			pair.English.Checked = english;
			pair.Chinese.Checked = chinese;
			//the chosen language shows on its icon too: a bright badge with a tick, the other one is grey
			pair.English.Image = GetMenuIcon(english ? MenuIcon.LanguageOn : MenuIcon.Language, "EN", size);
			pair.Chinese.Image = GetMenuIcon(chinese ? MenuIcon.LanguageOn : MenuIcon.Language, "中", size);
		}

		/// <summary>Asks the helper which language it translates into (off the interface thread) and ticks it in the menu.</summary>
		private static void RefreshTarget(Pair pair)
		{
			string url = Main?.TranslationHelperUrl;
			if (string.IsNullOrEmpty(url))
			{
				return;
			}
			ThreadPool.QueueUserWorkItem(delegate
			{
				string code = null;
				try
				{
					code = new TranslatorHelperClient(url).GetState().GetString("target_code");
				}
				catch (Exception)
				{
				}
				if (string.IsNullOrEmpty(code))
				{
					return;
				}
				targetCode = code;
				try
				{
					pair.Strip.BeginInvoke(new Action(delegate { ShowTarget(pair); }));
				}
				catch (Exception)
				{
				}
			});
		}

		/// <summary>The language was changed here: the ticks follow at once.</summary>
		public static void TargetChanged(string code)
		{
			targetCode = code ?? string.Empty;
			Pair[] copy;
			lock (pairs)
			{
				copy = pairs.ToArray();
			}
			foreach (Pair pair in copy)
			{
				if (!pair.Strip.IsDisposed)
				{
					ShowTarget(pair);
				}
			}
		}

		//Small pictures for the translate-ahead menu: readable on a light or a dark menu.
		private static Bitmap GetMenuIcon(MenuIcon kind, string number, int size)
		{
			string key = "m" + (int)kind + (number ?? "") + "-" + size;
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
					g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
					float u = size / 16f;
					Color paper = Color.FromArgb(240, 240, 244);
					Color rim = Color.FromArgb(70, 70, 78);
					Color accent = Color.FromArgb(245, 175, 30);
					using (Pen rimPen = new Pen(rim, Math.Max(1f, u)))
					using (SolidBrush paperBrush = new SolidBrush(paper))
					using (SolidBrush accentBrush = new SolidBrush(accent))
					using (Pen accentPen = new Pen(accent, Math.Max(1.6f, 2f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
					{
						switch (kind)
						{
						case MenuIcon.Pages:
							{
								//A page with the number of pages on it.
								RectangleF back = new RectangleF(4 * u, 0.8f * u, 10.5f * u, 13.5f * u);
								RectangleF front = new RectangleF(1 * u, 2.8f * u, 10.5f * u, 13f * u);
								g.FillRectangle(paperBrush, back);
								g.DrawRectangle(rimPen, back.X, back.Y, back.Width, back.Height);
								g.FillRectangle(paperBrush, front);
								g.DrawRectangle(rimPen, front.X, front.Y, front.Width, front.Height);
								using (Font font = new Font("Segoe UI", Math.Max(6f, (number.Length > 1 ? 7.6f : 10f) * u), FontStyle.Bold, GraphicsUnit.Pixel))
								using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
								using (SolidBrush ink = new SolidBrush(Color.FromArgb(40, 40, 46)))
								{
									g.DrawString(number, font, ink, new RectangleF(front.X, front.Y + 0.5f * u, front.Width, front.Height), sf);
								}
								break;
							}
						case MenuIcon.Rest:
							{
								//One page, an arrow and a bar: on to the end.
								RectangleF page = new RectangleF(1 * u, 2 * u, 7 * u, 12 * u);
								g.FillRectangle(paperBrush, page);
								g.DrawRectangle(rimPen, page.X, page.Y, page.Width, page.Height);
								g.DrawLine(accentPen, 9.5f * u, 8 * u, 13 * u, 8 * u);
								g.DrawLine(accentPen, 11 * u, 5.8f * u, 13.2f * u, 8 * u);
								g.DrawLine(accentPen, 11 * u, 10.2f * u, 13.2f * u, 8 * u);
								g.DrawLine(accentPen, 15 * u, 4.5f * u, 15 * u, 11.5f * u);
								break;
							}
						case MenuIcon.Book:
							{
								PointF[] left = { new PointF(0.8f * u, 3.5f * u), new PointF(7.7f * u, 5 * u), new PointF(7.7f * u, 14 * u), new PointF(0.8f * u, 12.5f * u) };
								PointF[] right = { new PointF(15.2f * u, 3.5f * u), new PointF(8.3f * u, 5 * u), new PointF(8.3f * u, 14 * u), new PointF(15.2f * u, 12.5f * u) };
								g.FillPolygon(paperBrush, left);
								g.FillPolygon(paperBrush, right);
								g.DrawPolygon(rimPen, left);
								g.DrawPolygon(rimPen, right);
								using (Pen line = new Pen(Color.FromArgb(150, rim), Math.Max(1f, u)))
								{
									g.DrawLine(line, 2.5f * u, 6.5f * u, 6 * u, 7.3f * u);
									g.DrawLine(line, 2.5f * u, 9 * u, 6 * u, 9.8f * u);
									g.DrawLine(line, 13.5f * u, 6.5f * u, 10 * u, 7.3f * u);
									g.DrawLine(line, 13.5f * u, 9 * u, 10 * u, 9.8f * u);
								}
								break;
							}
						case MenuIcon.Language:
						case MenuIcon.LanguageOn:
							{
								//A rounded badge with the language's letters: EN, or a Chinese character.
								RectangleF badge = new RectangleF(0.8f * u, 2.5f * u, 14.4f * u, 11f * u);
								using (GraphicsPath path = new GraphicsPath())
								using (SolidBrush fill = new SolidBrush(kind == MenuIcon.LanguageOn ? Color.FromArgb(40, 120, 235) : Color.FromArgb(120, 126, 138)))
								{
									float r = 3f * u;
									path.AddArc(badge.X, badge.Y, r * 2, r * 2, 180, 90);
									path.AddArc(badge.Right - r * 2, badge.Y, r * 2, r * 2, 270, 90);
									path.AddArc(badge.Right - r * 2, badge.Bottom - r * 2, r * 2, r * 2, 0, 90);
									path.AddArc(badge.X, badge.Bottom - r * 2, r * 2, r * 2, 90, 90);
									path.CloseFigure();
									g.FillPath(fill, path);
									g.DrawPath(rimPen, path);
								}
								bool wide = number.Length > 0 && number[0] > 0x2E7F;
								using (Font font = new Font(wide ? "Microsoft YaHei UI" : "Segoe UI", Math.Max(6f, (wide ? 9.5f : 7.6f) * u), FontStyle.Bold, GraphicsUnit.Pixel))
								using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
								using (SolidBrush ink = new SolidBrush(Color.White))
								{
									g.DrawString(number, font, ink, new RectangleF(badge.X, badge.Y + 0.3f * u, badge.Width, badge.Height), sf);
								}
								if (kind == MenuIcon.LanguageOn)
								{
									//a green disc with a tick at the corner: this is the language in use
									RectangleF disc = new RectangleF(9f * u, 8.6f * u, 7f * u, 7f * u);
									using (SolidBrush green = new SolidBrush(Color.FromArgb(40, 175, 80)))
									using (Pen ring = new Pen(Color.White, Math.Max(1f, 0.9f * u)))
									using (Pen tick = new Pen(Color.White, Math.Max(1.2f, 1.3f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
									{
										g.FillEllipse(green, disc);
										g.DrawEllipse(ring, disc);
										g.DrawLines(tick, new[] { new PointF(10.6f * u, 12.2f * u), new PointF(12f * u, 13.6f * u), new PointF(14.4f * u, 10.4f * u) });
									}
								}
								break;
							}
						default:
							{
								using (SolidBrush red = new SolidBrush(Color.FromArgb(220, 65, 55)))
								{
									g.FillRectangle(red, 3.5f * u, 3.5f * u, 9 * u, 9 * u);
									g.DrawRectangle(rimPen, 3.5f * u, 3.5f * u, 9 * u, 9 * u);
								}
								break;
							}
						}
					}
				}
				icons[key] = bmp;
				return bmp;
			}
		}

		/// <summary>The batch's picture: an open book in the colour of the state (amber translating, blue paused, red stopped).</summary>
		/// <summary>True while a batch has books waiting and is not running (paused, or stopped by a problem): it can be continued.</summary>
		private static bool CanContinueBatch => !TranslationBatch.IsRunning && TranslationBatch.HasWaiting;

		public static Bitmap GetBatchIcon(TranslationLed led, int size)
		{
			return GetBatchIcon(led, size, false);
		}

		/// <summary>The batch's picture; `play` adds a small green play mark at the bottom right (the batch can be continued with a click).</summary>
		public static Bitmap GetBatchIcon(TranslationLed led, int size, bool play)
		{
			string key = "batch" + (int)led + "-" + size + (play ? "-play" : "");
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
					Color state = led == TranslationLed.Off ? Color.FromArgb(244, 244, 248) : (led == TranslationLed.On ? Color.FromArgb(95, 175, 235) : ColorOf(led));
					PointF[] left = { new PointF(0.8f * u, 3.5f * u), new PointF(7.7f * u, 5 * u), new PointF(7.7f * u, 14 * u), new PointF(0.8f * u, 12.5f * u) };
					PointF[] right = { new PointF(15.2f * u, 3.5f * u), new PointF(8.3f * u, 5 * u), new PointF(8.3f * u, 14 * u), new PointF(15.2f * u, 12.5f * u) };
					using (SolidBrush fill = new SolidBrush(state))
					using (Pen rim = new Pen(Color.FromArgb(40, 40, 46), Math.Max(1f, 0.9f * u)))
					using (Pen line = new Pen(Color.FromArgb(170, 40, 40, 46), Math.Max(1f, u)))
					{
						g.FillPolygon(fill, left);
						g.FillPolygon(fill, right);
						g.DrawPolygon(rim, left);
						g.DrawPolygon(rim, right);
						g.DrawLine(line, 2.5f * u, 6.5f * u, 6 * u, 7.3f * u);
						g.DrawLine(line, 2.5f * u, 9 * u, 6 * u, 9.8f * u);
						g.DrawLine(line, 13.5f * u, 6.5f * u, 10 * u, 7.3f * u);
						g.DrawLine(line, 13.5f * u, 9 * u, 10 * u, 9.8f * u);
					}
					if (play)
					{
						//over the bottom right corner of the book, with a dark rim so it shows on the blue and the red book alike
						PointF[] mark = { new PointF(9.4f * u, 8.6f * u), new PointF(9.4f * u, 15.6f * u), new PointF(15.6f * u, 12.1f * u) };
						using (SolidBrush green = new SolidBrush(Color.FromArgb(60, 205, 90)))
						using (Pen rim = new Pen(Color.FromArgb(25, 60, 30), Math.Max(1f, 0.9f * u)) { LineJoin = LineJoin.Round })
						{
							g.FillPolygon(green, mark);
							g.DrawPolygon(rim, mark);
						}
					}
				}
				icons[key] = bmp;
				return bmp;
			}
		}

		private static Bitmap GetAheadIcon(TranslationLed led, int size)
		{
			string key = "a" + (int)led + "-" + size;
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
					DrawPagesAhead(g, size, ColorOf(led));
				}
				icons[key] = bmp;
				return bmp;
			}
		}

		private static Color Blend(Color a, Color b, float amount)
		{
			return Color.FromArgb((int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
		}

		//Two light pages with an arrow running on past them: the pages ahead. The pages stay light whatever the state,
		//so they show on a dark bar; the arrow carries the state (blue idle, amber while running).
		private static void DrawPagesAhead(Graphics g, int s, Color state)
		{
			float u = s / 16f;
			bool idle = state == ColorOf(TranslationLed.Off);
			bool paused = state == ColorOf(TranslationLed.On);
			Color arrowColor = idle || paused ? Color.FromArgb(95, 175, 235) : state;
			Color tint = paused ? Color.FromArgb(95, 175, 235) : state;
			float mix = idle ? 0f : 0.6f;
			RectangleF back = new RectangleF(5.5f * u, 1f * u, 8.5f * u, 11.5f * u);
			RectangleF front = new RectangleF(1.5f * u, 4f * u, 8.5f * u, 11.5f * u);
			using (SolidBrush backFill = new SolidBrush(Blend(Color.FromArgb(205, 205, 212), tint, mix * 0.8f)))
			using (SolidBrush frontFill = new SolidBrush(Blend(Color.FromArgb(244, 244, 248), tint, mix)))
			using (Pen rim = new Pen(Color.FromArgb(60, 60, 68), Math.Max(1f, 0.9f * u)))
			{
				g.FillRectangle(backFill, back);
				g.DrawRectangle(rim, back.X, back.Y, back.Width, back.Height);
				g.FillRectangle(frontFill, front);
				g.DrawRectangle(rim, front.X, front.Y, front.Width, front.Height);
			}
			using (Pen line = new Pen(Color.FromArgb(150, 90, 90, 100), Math.Max(1f, u)))
			{
				g.DrawLine(line, 3.2f * u, 6.5f * u, 8.3f * u, 6.5f * u);
				g.DrawLine(line, 3.2f * u, 8.5f * u, 8.3f * u, 8.5f * u);
			}
			using (Pen halo = new Pen(Color.FromArgb(200, 20, 20, 24), Math.Max(2.4f, 3.2f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
			using (Pen arrow = new Pen(arrowColor, Math.Max(1.6f, 2f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
			{
				foreach (Pen pen in new[] { halo, arrow })
				{
					g.DrawLine(pen, 7f * u, 12.3f * u, 14f * u, 12.3f * u);
					g.DrawLine(pen, 11.4f * u, 9.7f * u, 14.2f * u, 12.3f * u);
					g.DrawLine(pen, 11.4f * u, 14.9f * u, 14.2f * u, 12.3f * u);
				}
			}
		}

		//A speech bubble with two lines of text, filled with the state colour.
		private static void DrawSpeechBubble(Graphics g, int s, Color color)
		{
			float u = s / 16f;
			using (GraphicsPath path = new GraphicsPath())
			{
				RectangleF r = new RectangleF(1 * u, 1.5f * u, 14 * u, 9.5f * u);
				float d = 4 * u;
				path.AddArc(r.Left, r.Top, d, d, 180, 90);
				path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
				path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
				path.AddLine(r.Right - d, r.Bottom, 8 * u, r.Bottom);
				path.AddLine(8 * u, r.Bottom, 4 * u, 15 * u);
				path.AddLine(4 * u, 15 * u, 5 * u, r.Bottom);
				path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
				path.CloseFigure();
				using (SolidBrush fill = new SolidBrush(color))
				using (Pen rim = new Pen(Color.FromArgb(150, 20, 20, 20), Math.Max(1f, u * 0.8f)))
				{
					g.FillPath(fill, path);
					g.DrawPath(rim, path);
				}
			}
			using (Pen line = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(1f, 1.4f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
			{
				g.DrawLine(line, 4 * u, 4.8f * u, 12 * u, 4.8f * u);
				g.DrawLine(line, 4 * u, 8 * u, 9.5f * u, 8 * u);
			}
		}

		//The power symbol (a ring open at the top with a bar through the gap) in the state colour.
		private static void DrawPower(Graphics g, int s, Color color)
		{
			float u = s / 16f;
			float w = Math.Max(1.6f, 2.2f * u);
			using (Pen halo = new Pen(Color.FromArgb(110, 20, 20, 20), w + Math.Max(1f, u)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
			using (Pen pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
			{
				RectangleF ring = new RectangleF(2.5f * u, 3.5f * u, 11 * u, 11 * u);
				foreach (Pen p in new[] { halo, pen })
				{
					g.DrawArc(p, ring, -50, 280);
					g.DrawLine(p, 8 * u, 1.8f * u, 8 * u, 8 * u);
				}
			}
		}
	}
}
