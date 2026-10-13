using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using cYo.Common.Drawing;
using cYo.Projects.ComicRack.Engine.IO;

namespace cYo.Projects.ComicRack.Engine.Display.Forms
{
	/// <summary>
	/// Talks to the ComicTranslator helper (a small local web service): sends it page images
	/// and keeps the translated pages it sends back. One background thread does the work, the
	/// page being read first and the pages after it next, so by the time a page is turned its
	/// translation is usually ready.
	///
	/// Everything here is safe to call from the paint code: asking for a page that is not
	/// translated yet only records the request and returns straight away.
	/// </summary>
	public class PageTranslationService : IDisposable
	{
		private enum State
		{
			Pending,
			Working,
			Done,
			NoText,
			Failed
		}

		private class Entry
		{
			public State State;

			public byte[] Source;

			public PageImage Result;

			public int Priority;

			public long Order;

			public DateTime Finished;

			public DateTime LastUsed;
		}

		//How many translated pages to keep in memory (about 1 MB each).
		private const int MaxDone = 40;

		//How many untranslated pages may wait; older read-ahead requests give way.
		private const int MaxPending = 6;

		private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(15);

		private readonly Dictionary<PageKey, Entry> entries = new Dictionary<PageKey, Entry>();

		private readonly object sync = new object();

		private readonly AutoResetEvent signal = new AutoResetEvent(false);

		private Thread worker;

		private volatile bool stopping;

		private long order;

		private string status;

		private DateTime serverDownUntil = DateTime.MinValue;

		public string ServerUrl { get; set; } = "http://127.0.0.1:8790";

		/// <summary>
		/// Raised on the background thread when a page has been translated (or found to
		/// have no text, or failed), with the page's key.
		/// </summary>
		public event EventHandler<PageKeyEventArgs> PageFinished;

		public class PageKeyEventArgs : EventArgs
		{
			public PageKey Key { get; }

			public PageKeyEventArgs(PageKey key)
			{
				Key = key;
			}
		}

		/// <summary>
		/// The last problem talking to the helper, or null while all is well.
		/// </summary>
		public string Status
		{
			get
			{
				lock (sync)
				{
					return status;
				}
			}
		}

		/// <summary>
		/// The translated page, or null when it is not ready (or has no text to translate).
		/// </summary>
		public PageImage GetResult(PageKey key)
		{
			if (key == null)
			{
				return null;
			}
			lock (sync)
			{
				if (entries.TryGetValue(key, out Entry entry) && entry.State == State.Done)
				{
					entry.LastUsed = DateTime.UtcNow;
					return entry.Result;
				}
			}
			return null;
		}

		public bool IsWaiting(PageKey key)
		{
			if (key == null)
			{
				return false;
			}
			lock (sync)
			{
				return entries.TryGetValue(key, out Entry entry) && (entry.State == State.Pending || entry.State == State.Working);
			}
		}

		/// <summary>
		/// True when the page needs no request: already done, waiting, or recently failed.
		/// Lets callers skip the work of getting the page's bytes.
		/// </summary>
		public bool IsKnown(PageKey key)
		{
			lock (sync)
			{
				if (!entries.TryGetValue(key, out Entry entry))
				{
					return false;
				}
				return entry.State != State.Failed || DateTime.UtcNow - entry.Finished < RetryAfter;
			}
		}

		/// <summary>
		/// Asks for a page to be translated. Lower priority numbers go first; asking again
		/// for a waiting page with a lower number moves it forward.
		/// </summary>
		public void Request(PageKey key, PageImage image, int priority)
		{
			if (key == null || image == null || stopping)
			{
				return;
			}
			lock (sync)
			{
				if (entries.TryGetValue(key, out Entry existing))
				{
					if (existing.State == State.Pending && priority < existing.Priority)
					{
						existing.Priority = priority;
						existing.Order = ++order;
					}
					if (existing.State != State.Failed || DateTime.UtcNow - existing.Finished < RetryAfter)
					{
						return;
					}
				}
			}
			//Outside the lock: may encode the page when its original format is not one the
			//helper can read.
			byte[] data = GetUploadBytes(image);
			if (data == null)
			{
				return;
			}
			lock (sync)
			{
				entries[key] = new Entry
				{
					State = State.Pending,
					Source = data,
					Priority = priority,
					Order = ++order,
					LastUsed = DateTime.UtcNow
				};
				TrimPending();
				EnsureWorker();
			}
			signal.Set();
		}

		/// <summary>
		/// Forgets every translated and waiting page, e.g. when translation is turned off,
		/// so turning it on again picks up changed settings.
		/// </summary>
		public void Clear()
		{
			lock (sync)
			{
				entries.Clear();
				status = null;
				serverDownUntil = DateTime.MinValue;
			}
		}

		public void Dispose()
		{
			stopping = true;
			signal.Set();
		}

		private static byte[] GetUploadBytes(PageImage image)
		{
			try
			{
				byte[] data = image.Data;
				if (IsReadableFormat(data))
				{
					return data;
				}
				return image.Bitmap?.ImageToJpegBytes(95);
			}
			catch (Exception)
			{
				return null;
			}
		}

		//JPEG, PNG, WebP, BMP and GIF are read by the helper as they are; anything else
		//(JPEG XL, AVIF, HEIF...) is sent as a JPEG made from the decoded page.
		private static bool IsReadableFormat(byte[] d)
		{
			if (d == null || d.Length < 12)
			{
				return false;
			}
			if (d[0] == 0xFF && d[1] == 0xD8)
			{
				return true;
			}
			if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
			{
				return true;
			}
			if (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
			{
				return true;
			}
			if (d[0] == 'B' && d[1] == 'M')
			{
				return true;
			}
			return d[0] == 'G' && d[1] == 'I' && d[2] == 'F';
		}

		private void TrimPending()
		{
			List<KeyValuePair<PageKey, Entry>> pending = entries.Where((KeyValuePair<PageKey, Entry> kv) => kv.Value.State == State.Pending).OrderBy((KeyValuePair<PageKey, Entry> kv) => kv.Value.Priority).ThenByDescending((KeyValuePair<PageKey, Entry> kv) => kv.Value.Order).ToList();
			foreach (KeyValuePair<PageKey, Entry> kv in pending.Skip(MaxPending))
			{
				entries.Remove(kv.Key);
			}
			List<KeyValuePair<PageKey, Entry>> done = entries.Where((KeyValuePair<PageKey, Entry> kv) => kv.Value.State == State.Done || kv.Value.State == State.NoText).OrderByDescending((KeyValuePair<PageKey, Entry> kv) => kv.Value.LastUsed).ToList();
			foreach (KeyValuePair<PageKey, Entry> kv in done.Skip(MaxDone))
			{
				entries.Remove(kv.Key);
			}
		}

		private void EnsureWorker()
		{
			if (worker != null)
			{
				return;
			}
			worker = new Thread(WorkerLoop)
			{
				IsBackground = true,
				Name = "Page translation",
				Priority = ThreadPriority.BelowNormal
			};
			worker.Start();
		}

		private void WorkerLoop()
		{
			while (!stopping)
			{
				PageKey key = null;
				Entry entry = null;
				lock (sync)
				{
					if (DateTime.UtcNow >= serverDownUntil)
					{
						KeyValuePair<PageKey, Entry> next = entries.Where((KeyValuePair<PageKey, Entry> kv) => kv.Value.State == State.Pending).OrderBy((KeyValuePair<PageKey, Entry> kv) => kv.Value.Priority).ThenByDescending((KeyValuePair<PageKey, Entry> kv) => kv.Value.Order).FirstOrDefault();
						if (next.Value != null)
						{
							key = next.Key;
							entry = next.Value;
							entry.State = State.Working;
						}
					}
				}
				if (entry == null)
				{
					signal.WaitOne(1000);
					continue;
				}
				Translate(key, entry);
				PageFinished?.Invoke(this, new PageKeyEventArgs(key));
			}
		}

		private void Translate(PageKey key, Entry entry)
		{
			State result = State.Failed;
			PageImage image = null;
			string problem = null;
			bool serverDown = false;
			try
			{
				HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ServerUrl.TrimEnd('/') + "/translate");
				request.Method = "POST";
				request.ContentType = "application/octet-stream";
				request.Proxy = null;
				request.Timeout = 300000;
				request.ReadWriteTimeout = 300000;
				request.ContentLength = entry.Source.Length;
				using (Stream s = request.GetRequestStream())
				{
					s.Write(entry.Source, 0, entry.Source.Length);
				}
				using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
				{
					if (response.StatusCode == HttpStatusCode.NoContent)
					{
						result = State.NoText;
					}
					else
					{
						byte[] bytes = ReadAll(response.GetResponseStream());
						if (bytes.Length > 0)
						{
							image = PageImage.CreateFrom(bytes);
							result = State.Done;
						}
						else
						{
							result = State.NoText;
						}
					}
				}
			}
			catch (WebException ex)
			{
				if (ex.Response is HttpWebResponse response)
				{
					using (response)
					{
						try
						{
							problem = Encoding.UTF8.GetString(ReadAll(response.GetResponseStream())).Trim();
						}
						catch (Exception)
						{
						}
						if (string.IsNullOrEmpty(problem))
						{
							problem = "Translation failed (" + (int)response.StatusCode + ")";
						}
					}
				}
				else
				{
					serverDown = true;
					problem = "Translator not running at " + ServerUrl + " - start ComicTranslator\\run.bat";
				}
			}
			catch (Exception ex)
			{
				problem = "Translation failed: " + ex.Message;
			}
			lock (sync)
			{
				//Cleared (translation turned off) while this page was out: forget it.
				if (!entries.TryGetValue(key, out Entry current) || current != entry)
				{
					return;
				}
				entry.State = result;
				entry.Result = image;
				entry.Source = (result == State.Failed) ? entry.Source : null;
				entry.Finished = DateTime.UtcNow;
				entry.LastUsed = DateTime.UtcNow;
				status = problem;
				if (serverDown)
				{
					//Every waiting page would fail the same way; try again a little later.
					serverDownUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
					foreach (Entry e in entries.Values.Where((Entry p) => p.State == State.Pending))
					{
						e.State = State.Failed;
						e.Finished = DateTime.UtcNow;
					}
				}
				TrimPending();
			}
		}

		private static byte[] ReadAll(Stream stream)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				stream.CopyTo(ms);
				return ms.ToArray();
			}
		}
	}
}
