using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace cYo.Projects.ComicRack.Viewer
{
	/// <summary>
	/// Notes what ComicRack's window was doing when it stops answering: every couple of seconds a background thread asks the window
	/// thread to answer, and when it has not for HangSeconds, the window thread's calls (its stack) are written to a report in the
	/// HangReports folder, with what a running scan was doing (its stack and the folder it was in). When the window answers again,
	/// the report says after how long. A window busy for a moment is not reported. The newest MaxReports reports are kept.
	/// </summary>
	public static class HangWatcher
	{
		private const int CheckEverySeconds = 2;

		private const int HangSeconds = 10;

		private const int MaxReports = 30;

		private static Thread watcher;

		public static string ReportFolder => Path.Combine(Program.Paths.ApplicationDataPath, "HangReports");

		/// <summary>Starts watching the window; called on the window thread (once).</summary>
		public static void Start(Form window)
		{
			if (watcher != null || window == null)
			{
				return;
			}
			Thread windowThread = Thread.CurrentThread;
			watcher = new Thread(() => Watch(window, windowThread))
			{
				IsBackground = true,
				Name = "Hang watcher",
				Priority = ThreadPriority.BelowNormal
			};
			watcher.Start();
		}

		private static void Watch(Form window, Thread windowThread)
		{
			while (true)
			{
				Thread.Sleep(CheckEverySeconds * 1000);
				ManualResetEvent answered = new ManualResetEvent(false);
				try
				{
					if (window.IsDisposed || !window.IsHandleCreated)
					{
						return;
					}
					window.BeginInvoke((Action)delegate { answered.Set(); });
				}
				catch (Exception)
				{
					return;         //the window is closing
				}
				if (answered.WaitOne(HangSeconds * 1000))
				{
					continue;
				}
				DateTime since = DateTime.Now.AddSeconds(-HangSeconds);
				string file = null;
				try
				{
					file = Write(since, windowThread);
				}
				catch (Exception)
				{
				}
				//one report for one hang: wait until the window answers again, then say how long it was
				answered.WaitOne();
				try
				{
					if (file != null)
					{
						File.AppendAllText(file, string.Format("{0}The window answered again at {1:HH:mm:ss}, after about {2:0} seconds.{0}", Environment.NewLine, DateTime.Now, (DateTime.Now - since).TotalSeconds));
					}
				}
				catch (Exception)
				{
				}
			}
		}

		private static string Write(DateTime since, Thread windowThread)
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine(string.Format("ComicRack stopped answering at about {0:yyyy-MM-dd HH:mm:ss} (no answer for {1} seconds).", since, HangSeconds));
			sb.AppendLine("Version: " + Application.ProductVersion);
			sb.AppendLine();
			sb.AppendLine("What the window was doing (its calls, the innermost first):");
			sb.AppendLine(StackOf(windowThread));
			try
			{
				var scanner = Program.QueueManager?.Scanner;
				Thread scan = scanner?.ScanningThread;
				if (scan != null && scan.IsAlive)
				{
					sb.AppendLine("A scan was running, in: " + (scanner.CurrentLocation ?? "(unknown)"));
					sb.AppendLine("What the scan was doing:");
					sb.AppendLine(StackOf(scan));
				}
				else
				{
					sb.AppendLine("No scan was running.");
				}
			}
			catch (Exception ex)
			{
				sb.AppendLine("The scan could not be looked at: " + ex.Message);
			}
			Directory.CreateDirectory(ReportFolder);
			string file = Path.Combine(ReportFolder, "hang-" + since.ToString("yyyyMMdd-HHmmss") + ".txt");
			File.WriteAllText(file, sb.ToString());
			foreach (string old in Directory.GetFiles(ReportFolder, "hang-*.txt").OrderByDescending((string f) => f).Skip(MaxReports))
			{
				try
				{
					File.Delete(old);
				}
				catch (Exception)
				{
				}
			}
			return file;
		}

		/// <summary>
		/// Another thread's calls: the thread is held for the moment it takes to read them and let go again (Thread.Suspend; the only
		/// way to read another thread's stack in .NET Framework, used here only once a thread has been stuck for seconds).
		/// </summary>
		private static string StackOf(Thread thread)
		{
#pragma warning disable 618
			try
			{
				thread.Suspend();
				try
				{
					return new StackTrace(thread, true).ToString();
				}
				finally
				{
					thread.Resume();
				}
			}
			catch (Exception ex)
			{
				return "(could not be read: " + ex.GetType().Name + ": " + ex.Message + ")";
			}
#pragma warning restore 618
		}
	}
}
