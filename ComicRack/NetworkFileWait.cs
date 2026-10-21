using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace cYo.Projects.ComicRack.Viewer
{
	/// <summary>
	/// Opening a book whose file is on a network folder: a share that went to sleep (an old disk spinning up) can take many seconds to
	/// answer, and the window used to wait for it (hang reports: the window stuck in CreateFile while opening a book). The file is
	/// opened once in the background first; the book is then opened on the window thread as usual, by then without waiting. A file on
	/// this computer is opened at once, as before.
	/// </summary>
	public static class NetworkFileWait
	{
		private static readonly HashSet<string> waiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public static void WhenReady(Control window, string path, Action open)
		{
			if (window == null || string.IsNullOrEmpty(path) || !IsOnNetwork(path))
			{
				open();
				return;
			}
			lock (waiting)
			{
				if (!waiting.Add(path))
				{
					return;         //asked for again while it is still on its way (a second double click)
				}
			}
			Cursor was = window.Cursor;
			window.Cursor = Cursors.AppStarting;
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
					{
					}
				}
				catch (Exception)
				{
					//not there or not readable: opening it says so as before
				}
				try
				{
					if (!window.IsDisposed && window.IsHandleCreated)
					{
						window.BeginInvoke((Action)delegate
						{
							lock (waiting)
							{
								waiting.Remove(path);
							}
							window.Cursor = was;
							open();
						});
						return;
					}
				}
				catch (Exception)
				{
				}
				lock (waiting)
				{
					waiting.Remove(path);
				}
			});
		}

		/// <summary>A network path (\\server\share\...) or a file on a mapped network drive; Windows knows a drive's kind without asking the server.</summary>
		public static bool IsOnNetwork(string path)
		{
			try
			{
				if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
				if (path.StartsWith(@"\\?\"))
				{
					path = path.Substring(4);       //a long local path (\\?\C:\...)
				}
				else if (path.StartsWith(@"\\") || path.StartsWith("//"))
				{
					return true;
				}
				string root = Path.GetPathRoot(path);
				return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
