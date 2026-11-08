using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using cYo.Projects.ComicRack.Engine;
using cYo.Projects.ComicRack.Engine.Display.Forms;

namespace cYo.Projects.ComicRack.Viewer
{
	/// <summary>
	/// Writes translated copies of books one after the other in the background, like the batch translation: Export Translated
	/// Books queues them and closes, the status bar shows the book being written and its pages, and a click there stops the
	/// export or, when it is done, clears the message; the Skip and Stop buttons beside it skip the book being written or stop
	/// the export. Books added while it runs are written after the others.
	/// </summary>
	public static class TranslationExportQueue
	{
		public class Item
		{
			public string Title;

			public TranslationExport Export;

			public string TargetPath;

			/// <summary>Written from what the translator stored (nothing is translated); else the missing pages are translated too.</summary>
			public bool Complete;

			public bool AddToLibrary;

			public int Done;

			public int Total;

			public bool Finished;

			/// <summary>The file written, once it is.</summary>
			public string Written;

			/// <summary>Why it was not written, or null.</summary>
			public string Problem;

			public bool Cancelled;

			/// <summary>"Skip this book" was used on it: it is not written, and the export goes on with the next.</summary>
			public bool SkipAsked;
		}

		private static readonly object sync = new object();

		private static readonly List<Item> items = new List<Item>();

		private static Thread worker;

		private static volatile bool stopping;

		private static DateTime lastChanged = DateTime.MinValue;

		public static event EventHandler Changed;

		public static bool IsRunning
		{
			get
			{
				lock (sync)
				{
					return worker != null;
				}
			}
		}

		/// <summary>The book being written now, or null.</summary>
		public static Item Current { get; private set; }

		/// <summary>The books of this export (written, failed, waiting), in order.</summary>
		public static List<Item> Items
		{
			get
			{
				lock (sync)
				{
					return items.ToList();
				}
			}
		}

		public static List<Item> Waiting
		{
			get
			{
				lock (sync)
				{
					return items.Where((Item i) => !i.Finished).ToList();
				}
			}
		}

		/// <summary>True when the file is written by the queue already, or waits to be.</summary>
		public static bool IsQueued(string path)
		{
			lock (sync)
			{
				return items.Any((Item i) => !i.Finished && string.Equals(i.TargetPath, path, StringComparison.OrdinalIgnoreCase));
			}
		}

		/// <summary>Adds the books (a file already queued is left where it is) and starts writing them; returns how many were added.</summary>
		public static int Start(IEnumerable<Item> books)
		{
			int added = 0;
			lock (sync)
			{
				//a new export after the last one ended: the last one's results are cleared
				if (worker == null)
				{
					items.RemoveAll((Item i) => i.Finished);
				}
				foreach (Item item in books)
				{
					item.TargetPath = item.TargetPath ?? item.Export.TargetPath;
					if (items.Any((Item i) => !i.Finished && string.Equals(i.TargetPath, item.TargetPath, StringComparison.OrdinalIgnoreCase)))
					{
						continue;
					}
					items.Add(item);
					added++;
				}
				if (worker == null && items.Any((Item i) => !i.Finished))
				{
					stopping = false;
					worker = new Thread(Run)
					{
						IsBackground = true,
						Name = "Export translated books",
						Priority = ThreadPriority.BelowNormal
					};
					worker.Start();
				}
			}
			RaiseChanged(true);
			return added;
		}

		/// <summary>Stops the book being written and forgets those waiting; the books written so far stay.</summary>
		public static void Stop()
		{
			lock (sync)
			{
				if (worker == null)
				{
					return;
				}
				stopping = true;
				foreach (Item item in items.Where((Item i) => !i.Finished && i != Current))
				{
					item.Finished = true;
					item.Cancelled = true;
				}
			}
			Current?.Export.Cancel();
			RaiseChanged(true);
		}

		/// <summary>Gives up the book being written (its file is not written) and goes on with the next.</summary>
		public static void SkipCurrent()
		{
			Item item = Current;
			if (item == null || item.Finished)
			{
				return;
			}
			item.SkipAsked = true;
			item.Export.Cancel();
			RaiseChanged(true);
		}

		/// <summary>Forgets the results of an export that ended.</summary>
		public static void Clear()
		{
			lock (sync)
			{
				if (worker != null)
				{
					return;
				}
				items.Clear();
			}
			RaiseChanged(true);
		}

		private static void Run()
		{
			try
			{
				while (!stopping)
				{
					Item item;
					lock (sync)
					{
						item = items.FirstOrDefault((Item i) => !i.Finished);
					}
					if (item == null)
					{
						break;
					}
					Current = item;
					RaiseChanged(true);
					item.Export.Progress += delegate (int done, int total)
					{
						item.Done = done;
						item.Total = total;
						RaiseChanged(false);
					};
					try
					{
						if (!TranslationServices.HelperUp)
						{
							throw new InvalidOperationException("The translator is not running.");
						}
						item.Written = item.Export.Run();
						if (item.AddToLibrary)
						{
							Program.BookFactory.Create(item.Written, CreateBookOption.AddToStorage);
						}
					}
					catch (OperationCanceledException)
					{
						item.Cancelled = !item.SkipAsked;
					}
					catch (Exception ex)
					{
						//skipped or stopped from the status bar: the export's failing then is what was asked for
						if (stopping && !item.SkipAsked)
						{
							item.Cancelled = true;
						}
						else if (!item.SkipAsked)
						{
							item.Problem = ex.Message;
						}
					}
					item.Finished = true;
					RaiseChanged(true);
				}
			}
			finally
			{
				lock (sync)
				{
					worker = null;
					Current = null;
				}
				RaiseChanged(true);
			}
		}

		private static void RaiseChanged(bool force)
		{
			DateTime now = DateTime.UtcNow;
			if (!force && now - lastChanged < TimeSpan.FromMilliseconds(500))
			{
				return;
			}
			lastChanged = now;
			Changed?.Invoke(null, EventArgs.Empty);
		}
	}
}
