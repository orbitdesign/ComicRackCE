using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using cYo.Projects.ComicRack.Engine;
using cYo.Projects.ComicRack.Engine.Display.Forms;
using cYo.Projects.ComicRack.Engine.IO;
using cYo.Projects.ComicRack.Engine.IO.Provider;

namespace cYo.Projects.ComicRack.Viewer
{
	/// <summary>
	/// Translates whole books one after the other in the background, without opening them in the reader: the books
	/// selected in the library are queued, each page is handed to the helper (which keeps what it translated, and the
	/// store records it), and the percentage of each book is the share of its pages recorded as translated. Opening a
	/// book in the reader pauses the batch so the page being read gets the helper to itself; the queue stays and can be
	/// continued from the translator menu.
	/// </summary>
	public static class TranslationBatch
	{
		public class Item
		{
			public ComicBook Comic;

			public string BookKey;

			public string Title;

			/// <summary>Pages in the book; 0 until the book was opened for the first time (then the book's own count is used).</summary>
			public int Total;

			public bool Finished;

			/// <summary>Pages were left that could not be translated; the batch stopped here and the book can be continued.</summary>
			public bool Stalled;

			/// <summary>The pages (0 based) the helper could not translate on the last run, skipped so the batch could go on; the book
			/// stays in the queue, and continuing tries only these again.</summary>
			public List<int> Skipped = new List<int>();

			public string Problem;

			public int Translated => TranslationStore.Shared.TranslatedPageCount(BookKey);

			public int Pages => Total > 0 ? Total : Math.Max(0, Comic.PageCount);

			/// <summary>0 to 100, or -1 when the number of pages is not known yet.</summary>
			public int Percent
			{
				get
				{
					int pages = Pages;
					if (pages <= 0)
					{
						return Finished ? 100 : -1;
					}
					return Math.Min(100, (int)Math.Round(100.0 * Translated / pages));
				}
			}
		}

		private static readonly object sync = new object();

		private static readonly List<Item> items = new List<Item>();

		private static CancellationTokenSource cts;

		private static PageTranslationService service;

		private static DateTime lastChanged = DateTime.MinValue;

		public static event EventHandler Changed;

		/// <summary>Tells whether the reader is translating pages itself; the batch waits while it is.</summary>
		public static Func<bool> ReaderBusy { get; set; }

		/// <summary>True while the batch is holding back for the reader.</summary>
		public static bool Yielding { get; private set; }

		/// <summary>The last thing that stopped the batch (the helper was not running, ...), or null.</summary>
		public static string Problem { get; private set; }

		/// <summary>The batch went through every book, but some pages could not be translated and were skipped (see Item.Skipped).</summary>
		public static bool DoneWithSkips { get; private set; }

		/// <summary>"page 38" or "pages 12, 38": the skipped pages of a book, numbered from 1.</summary>
		public static string SkippedText(Item item)
		{
			List<int> pages = item?.Skipped;
			if (pages == null || pages.Count == 0)
			{
				return "";
			}
			string list = string.Join(", ", pages.Take(8).Select((int p) => (p + 1).ToString()));
			return (pages.Count == 1 ? "page " : "pages ") + list + (pages.Count > 8 ? " and " + (pages.Count - 8) + " more" : "");
		}

		public static bool IsRunning
		{
			get
			{
				lock (sync)
				{
					return cts != null;
				}
			}
		}

		/// <summary>The books not finished yet, in the order they are translated.</summary>
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

		public static bool HasWaiting => Waiting.Count > 0;

		/// <summary>The book being translated now, or null.</summary>
		public static Item Current { get; private set; }

		/// <summary>True while the batch is translating in the background (running, and not holding back for the reader): the
		/// translator's light is amber, as for any real translating.</summary>
		public static bool IsWorking => IsRunning && !Yielding;

		/// <summary>Adds books to the queue (those already in it are left where they are) and starts translating.</summary>
		public static void Start(IEnumerable<ComicBook> books)
		{
			lock (sync)
			{
				items.RemoveAll((Item i) => i.Finished);
				foreach (ComicBook comic in books)
				{
					string key = TranslationStore.BookKeyOf(comic);
					if (key == null || items.Any((Item i) => i.BookKey == key))
					{
						continue;
					}
					items.Add(new Item { Comic = comic, BookKey = key, Title = comic.Caption });
				}
			}
			Resume();
		}

		/// <summary>Continues with the books that are left.</summary>
		public static void Resume()
		{
			lock (sync)
			{
				if (cts != null || !items.Any((Item i) => !i.Finished))
				{
					RaiseChanged(true);
					return;
				}
				if (!TranslationServices.HelperUp)
				{
					Problem = "The translator helper is not running. Start it with the power button, then continue.";
					RaiseChanged(true);
					return;
				}
				Problem = null;
				DoneWithSkips = false;
				if (service == null)
				{
					service = new PageTranslationService(TranslationStore.Shared);
				}
				service.ServerUrl = string.IsNullOrWhiteSpace(Program.Settings.TranslatorServerUrl) ? "http://127.0.0.1:8790" : Program.Settings.TranslatorServerUrl.Trim();
				CancellationTokenSource mine = new CancellationTokenSource();
				cts = mine;
				Thread thread = new Thread((ThreadStart)delegate { Run(mine); })
				{
					IsBackground = true,
					Name = "Batch translation",
					Priority = ThreadPriority.BelowNormal
				};
				thread.Start();
			}
			RaiseChanged(true);
		}

		/// <summary>Stops translating for now; the queue stays.</summary>
		public static void Pause()
		{
			CancellationTokenSource mine;
			lock (sync)
			{
				mine = cts;
				cts = null;
			}
			if (mine != null)
			{
				mine.Cancel();
				service?.Clear();
			}
			RaiseChanged(true);
		}

		/// <summary>Forgets the queue (pages already translated stay translated).</summary>
		public static void Cancel()
		{
			Pause();
			lock (sync)
			{
				items.Clear();
				Current = null;
				Problem = null;
				DoneWithSkips = false;
			}
			RaiseChanged(true);
		}

		private static void Run(CancellationTokenSource mine)
		{
			//The books gone through on this run: a book with skipped pages stays in the queue but is not taken again until continued.
			HashSet<Item> tried = new HashSet<Item>();
			bool stopped = false;
			try
			{
				while (!mine.IsCancellationRequested)
				{
					Item item;
					lock (sync)
					{
						item = items.FirstOrDefault((Item i) => !i.Finished && !tried.Contains(i));
					}
					if (item == null)
					{
						break;
					}
					tried.Add(item);
					Current = item;
					RaiseChanged(true);
					DoBook(item, mine.Token);
					if (mine.IsCancellationRequested)
					{
						stopped = true;
						break;
					}
					if (item.Stalled)
					{
						//Stopped on a book that would not go through (the helper was not running, the book failed); the other books
						//wait until this one is continued.
						item.Stalled = false;
						stopped = true;
						break;
					}
				}
				if (!stopped && !mine.IsCancellationRequested)
				{
					List<Item> skipped;
					lock (sync)
					{
						skipped = items.Where((Item i) => !i.Finished && i.Skipped.Count > 0).ToList();
					}
					if (skipped.Count > 0)
					{
						int count = skipped.Sum((Item i) => i.Skipped.Count);
						DoneWithSkips = true;
						Problem = "Batch done, but " + count + (count == 1 ? " page" : " pages") + " could not be translated and " + (count == 1 ? "was" : "were") + " skipped: "
							+ string.Join("; ", skipped.Select((Item i) => "\"" + i.Title + "\" " + SkippedText(i))) + ". Continue to try " + (count == 1 ? "it" : "them") + " again.";
					}
				}
			}
			catch (Exception ex)
			{
				Problem = "Batch translation stopped: " + ex.Message;
			}
			finally
			{
				lock (sync)
				{
					if (cts == mine)
					{
						cts = null;
					}
				}
				Current = null;
				Yielding = false;
				RaiseChanged(true);
			}
		}

		private static bool IsReaderBusy()
		{
			try
			{
				return ReaderBusy != null && ReaderBusy();
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>
		/// While the reader translates pages (the book being read has pages that are not translated yet), the batch lets it
		/// have the helper: the pages the batch still has waiting are dropped, and it carries on once the reader has been
		/// idle for a couple of seconds. Returns true when it had to wait.
		/// </summary>
		private static bool YieldToReader(CancellationToken token, PageTranslationService svc)
		{
			if (!IsReaderBusy())
			{
				return false;
			}
			svc.DropPending();
			Yielding = true;
			RaiseChanged(true);
			int idle = 0;
			while (!token.IsCancellationRequested && idle < 4)
			{
				Thread.Sleep(500);
				idle = IsReaderBusy() ? 0 : idle + 1;
			}
			Yielding = false;
			RaiseChanged(true);
			return true;
		}

		private static void DoBook(Item item, CancellationToken token)
		{
			PageTranslationService svc = service;
			TranslationStore store = TranslationStore.Shared;
			ImageProvider provider = null;
			try
			{
				provider = item.Comic.OpenProvider();
				if (provider == null)
				{
					item.Problem = "The book could not be opened.";
					item.Finished = true;
					return;
				}
				item.Total = provider.Count;
				item.Skipped.Clear();
				//Pages the helper could not do (it was busy, or did not answer for a moment) are asked for again, a few times.
				for (int pass = 0; pass < 4 && !token.IsCancellationRequested; pass++)
				{
					bool yielded = false;
					if (pass > 0)
					{
						//The service remembers a failed page for a while; wait that out, and for the helper to be back.
						for (int waited = 0; waited < 40 && !token.IsCancellationRequested; waited++)
						{
							Thread.Sleep(500);
						}
						if (token.IsCancellationRequested)
						{
							return;
						}
					}
					for (int index = 0; index < provider.Count && !token.IsCancellationRequested; index++)
					{
						//A few pages at a time, so the queue and its page images stay small.
						while (!token.IsCancellationRequested && svc.PendingCount >= 3)
						{
							Thread.Sleep(400);
							yielded |= YieldToReader(token, svc);
						}
						yielded |= YieldToReader(token, svc);
						if (token.IsCancellationRequested)
						{
							return;
						}
						PageKey key = item.Comic.GetImageKey(index) as PageKey;
						if (key == null || store.IsTranslated(item.BookKey, index) || svc.IsKnown(key))
						{
							continue;
						}
						byte[] data = provider.GetByteImage(index);
						if (data == null)
						{
							continue;
						}
						PageImage image = PageImage.CreateFrom(data);
						try
						{
							svc.Request(key, image, 9, item.Comic, keep: true);
						}
						finally
						{
							(image as IDisposable)?.Dispose();
						}
						RaiseChanged(false);
					}
					while (!token.IsCancellationRequested && svc.PendingCount > 0)
					{
						Thread.Sleep(400);
						yielded |= YieldToReader(token, svc);
						RaiseChanged(false);
					}
					if (token.IsCancellationRequested)
					{
						return;
					}
					if (item.Total - item.Translated <= 0)
					{
						break;
					}
					if (yielded)
					{
						//Pages were set aside for the reader: go through the book again; this does not count as a retry.
						pass--;
					}
				}
				int left = item.Total - item.Translated;
				if (left > 0)
				{
					item.Problem = left + (left == 1 ? " page" : " pages") + " could not be translated" + (string.IsNullOrEmpty(svc.Status) ? "." : ": " + svc.Status);
					if (!TranslationServices.HelperUp)
					{
						//The helper is not there: every page would fail. Not finished: the book stays in the queue so it can be
						//continued, and the batch stops here.
						Problem = item.Problem;
						item.Stalled = true;
						return;
					}
					//The helper is running but these pages would not go through (a page it fails on): they are skipped and the batch
					//goes on with the next book. The book stays in the queue; continuing tries only these pages again.
					for (int index = 0; index < item.Total; index++)
					{
						if (!store.IsTranslated(item.BookKey, index))
						{
							item.Skipped.Add(index);
						}
					}
					return;
				}
				item.Problem = null;
				item.Finished = true;
			}
			catch (Exception ex)
			{
				item.Problem = ex.Message;
				item.Stalled = true;
				Problem = ex.Message;
			}
			finally
			{
				provider?.Dispose();
			}
		}

		private static void RaiseChanged(bool force)
		{
			DateTime now = DateTime.UtcNow;
			if (!force && now - lastChanged < TimeSpan.FromMilliseconds(700))
			{
				return;
			}
			lastChanged = now;
			Changed?.Invoke(null, EventArgs.Empty);
		}
	}
}
