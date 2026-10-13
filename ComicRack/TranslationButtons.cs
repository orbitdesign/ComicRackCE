using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
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

		private static void Poll()
		{
			bool helper = Get(HelperUrl + "/api/config", out string body);
			if (helper)
			{
				Match m = Regex.Match(body ?? "", "\"ollama_url\"\\s*:\\s*\"([^\"]+)\"");
				if (m.Success)
				{
					ollamaUrl = m.Groups[1].Value.TrimEnd('/');
				}
			}
			bool ollama = Get(ollamaUrl + "/api/tags", out _);
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

		private static bool Get(string url, out string body)
		{
			body = null;
			try
			{
				HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
				request.Proxy = null;
				request.Timeout = 1500;
				request.ReadWriteTimeout = 1500;
				using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
				using (StreamReader reader = new StreamReader(response.GetResponseStream()))
				{
					body = reader.ReadToEnd();
					return true;
				}
			}
			catch (Exception)
			{
				return false;
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
			return Get(HelperUrl + "/api/config", out _);
		}

		private static bool OllamaPolled()
		{
			return Get(ollamaUrl + "/api/tags", out _);
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

		private static string FindHelperFolder(IWin32Window owner)
		{
			string folder = Program.Settings.TranslatorHelperFolder;
			if (!string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, "run.bat")))
			{
				return folder;
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
		Problem
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

			public ToolStripMenuItem HelperItem;

			public ToolStripMenuItem OllamaItem;

			public ToolStripMenuItem StateItem;

			public ToolStripMenuItem ExitItem;

			public ToolStripDropDownButton Ahead;

			public ToolStripMenuItem AheadState;

			public ToolStripMenuItem AheadStop;

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
			pair.StatusPanel = new ToolStripMenuItem("Show Translation Status Panel");
			pair.StatusPanel.Click += delegate
			{
				ComicDisplayControl reader = Main?.TranslationReader;
				if (reader != null)
				{
					reader.TranslationStatusVisible = !reader.TranslationStatusVisible;
					Program.Settings.TranslatorShowStatus = reader.TranslationStatusVisible;
				}
			};
			pair.Translator.DropDownItems.Add(pair.StatusPanel);
			pair.Translator.DropDownItems.Add(new ToolStripSeparator());
			pair.Translator.DropDownItems.Add(new ToolStripMenuItem("Edit Translated Text...", null, delegate { Main?.OpenTranslationEditor(); }));
			pair.Translator.DropDownItems.Add(new ToolStripMenuItem("Delete Translation Cache...", null, delegate { Main?.OpenTranslationCache(); }));
			pair.Translator.DropDownItems.Add(new ToolStripMenuItem("Export Translated Book...", null, delegate { Main?.OpenTranslationExport(); }));
			pair.Translator.DropDownItems.Add(new ToolStripSeparator());
			pair.Translator.DropDownItems.Add(new ToolStripMenuItem("Translation Settings...", null, delegate { Main?.OpenTranslationSettings(); }));
			pair.Translator.DropDownOpening += delegate
			{
				pair.StatusPanel.Checked = Main?.TranslationReader?.TranslationStatusVisible ?? true;
			};

			pair.Services = new ToolStripSplitButton
			{
				Name = "tbTranslatorServices",
				DisplayStyle = ToolStripItemDisplayStyle.Image,
				AutoToolTip = false,
				Text = "Translator and Ollama"
			};
			pair.Services.ButtonClick += delegate { TranslationServices.ToggleAll(Main); };
			pair.StateItem = new ToolStripMenuItem { Enabled = false };
			pair.HelperItem = new ToolStripMenuItem();
			pair.HelperItem.Click += delegate { TranslationServices.ToggleHelper(Main); };
			pair.OllamaItem = new ToolStripMenuItem();
			pair.OllamaItem.Click += delegate { TranslationServices.ToggleOllama(); };
			pair.Services.DropDownItems.Add(pair.StateItem);
			pair.Services.DropDownItems.Add(new ToolStripSeparator());
			pair.Services.DropDownItems.Add(pair.HelperItem);
			pair.Services.DropDownItems.Add(pair.OllamaItem);
			pair.Services.DropDownItems.Add(new ToolStripSeparator());
			pair.ExitItem = new ToolStripMenuItem("Stop both when ComicRack closes");
			pair.ExitItem.Click += delegate { Program.Settings.TranslatorStopOnExit = !Program.Settings.TranslatorStopOnExit; };
			pair.Services.DropDownItems.Add(pair.ExitItem);
			pair.Services.DropDownOpening += delegate
			{
				pair.ExitItem.Checked = Program.Settings.TranslatorStopOnExit;
				Update(pair);
			};

			pair.Ahead = new ToolStripDropDownButton
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
			pair.Ahead.DropDownOpening += delegate { Update(pair); };

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
				TranslationServices.StartWatching();
			}
			Update(pair);
		}

		/// <summary>Brings every button up to date; safe to call from any thread.</summary>
		public static void RefreshAll()
		{
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
				else if (busy)
				{
					led = TranslationLed.Busy;
					tip = "Translate pages: translating... " + translated + " / " + total + " pages done. Click to turn off.";
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
			bool ahead = reader != null && reader.IsPreTranslating;
			int aheadState = ahead ? 1 : 0;
			if (aheadState != p.AheadLed || size != p.Size)
			{
				p.AheadLed = aheadState;
				p.Ahead.Image = GetAheadIcon(ahead ? TranslationLed.Busy : TranslationLed.Off, size);
			}
			if (reader != null)
			{
				reader.GetPreTranslateProgress(out int aheadDone, out int aheadTotal);
				p.AheadState.Text = ahead ? "Translating ahead: " + aheadDone + " of " + aheadTotal + " pages" : "Translate the pages after this one before you get to them";
				p.Ahead.ToolTipText = ahead ? "Translating ahead: " + aheadDone + " of " + aheadTotal + " pages. Click to change or stop." : "Translate pages ahead of reading (5, 10, 15... or the whole book)";
			}
			p.AheadStop.Enabled = ahead;
			p.Size = size;
			string state = "Translator helper: " + (helper ? "running" : "stopped") + ".  Ollama: " + (ollama ? "running" : "stopped") + ".";
			p.StateItem.Text = state;
			p.HelperItem.Text = helper ? "Stop Translator Helper" : "Start Translator Helper";
			p.OllamaItem.Text = ollama ? "Stop Ollama" : "Start Ollama";
			p.HelperItem.Enabled = p.OllamaItem.Enabled = !TranslationServices.Busy;
			string action = TranslationServices.Busy ? "working..." : (helper || ollama ? "Click to stop both." : "Click to start both.");
			p.Services.ToolTipText = state + " " + action + (string.IsNullOrEmpty(TranslationServices.Problem) ? "" : "  Problem: " + TranslationServices.Problem);
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
			}
			return Color.FromArgb(135, 135, 140);
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

		private enum MenuIcon
		{
			Pages,
			Rest,
			Book,
			Stop
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

		//Two pages with an arrow running on past them: the pages ahead.
		private static void DrawPagesAhead(Graphics g, int s, Color color)
		{
			float u = s / 16f;
			using (SolidBrush fill = new SolidBrush(color))
			using (Pen rim = new Pen(Color.FromArgb(150, 20, 20, 20), Math.Max(1f, u * 0.8f)))
			{
				RectangleF back = new RectangleF(5 * u, 1.5f * u, 8 * u, 11 * u);
				RectangleF front = new RectangleF(1.5f * u, 4 * u, 8 * u, 11 * u);
				using (SolidBrush faint = new SolidBrush(Color.FromArgb(120, color)))
				{
					g.FillRectangle(faint, back);
				}
				g.DrawRectangle(rim, back.X, back.Y, back.Width, back.Height);
				g.FillRectangle(fill, front);
				g.DrawRectangle(rim, front.X, front.Y, front.Width, front.Height);
			}
			using (Pen arrow = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(1.2f, 1.5f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
			{
				g.DrawLine(arrow, 3.5f * u, 9.5f * u, 8 * u, 9.5f * u);
				g.DrawLine(arrow, 6.5f * u, 7.3f * u, 8.2f * u, 9.5f * u);
				g.DrawLine(arrow, 6.5f * u, 11.7f * u, 8.2f * u, 9.5f * u);
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
