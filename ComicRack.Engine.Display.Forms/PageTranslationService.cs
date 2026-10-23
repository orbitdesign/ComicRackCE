using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using cYo.Common.Drawing;
using cYo.Projects.ComicRack.Engine.IO;

namespace cYo.Projects.ComicRack.Engine.Display.Forms
{
	/// <summary>
	/// What the editor needs to know about a translated page: its boxes and its finished picture.
	/// </summary>
	public class TranslationPageState
	{
		public int Width { get; set; }

		public int Height { get; set; }

		public string Sha { get; set; }

		public List<TranslationBox> Boxes { get; set; } = new List<TranslationBox>();

		/// <summary>The finished page (a JPEG), or null when the page is shown as it is.</summary>
		public byte[] Image { get; set; }

		/// <summary>True while the page is waiting to be translated or drawn again.</summary>
		public bool Waiting { get; set; }
	}

	/// <summary>
	/// Talks to the ComicTranslator helper (a small local web service): sends it page images
	/// and keeps the translated pages it sends back. One background thread does the work, the
	/// page being read first and the pages after it next, so by the time a page is turned its
	/// translation is usually ready.
	///
	/// Besides the finished picture the service keeps each page's text boxes. Changes the user makes
	/// to them are saved with the book (see TranslationStore) and drawn again by the helper.
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

			public byte[] ResultBytes;

			public int Priority;

			public long Order;

			public DateTime Finished;

			public DateTime LastUsed;

			public List<TranslationBox> Boxes;

			public string Sha;

			public int Width;

			public int Height;

			//Set when the page only has to be drawn again from Boxes.
			public bool RenderOnly;

			//Counts edits, so a result that was overtaken by a newer edit is not kept.
			public int Version;

			public string BookKey;

			//Asked for by pre-translating: not dropped to make room for the page being read.
			public bool Keep;

			//Whether the helper already has this page stored (only loading it, no reading or translating); null until asked.
			public bool? FromCache;

			/// <summary>Read and translated before, but its picture is drawn again (other fonts, a new picture version, the user's
			/// edits): no reading or translating, but it takes a while, so the light is amber, not blue.</summary>
			public bool Redraw;

			public int ImageIndex;

			/// <summary>The book's front cover: the helper letters its title as large as the original's place allows.</summary>
			public bool Cover;

			public bool BackCover;
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

		/// <summary>The language pages are translated into ("en", "zh-CN"), or null for the helper's own (the reader's). The batch sets
		/// it to the language each book was queued with (helper 1.72).</summary>
		public string Target { get; set; }

		/// <summary>
		/// The book's own bytes of a page (set by the reader). The page on screen can carry the reader's colour adjustment (sharpen...),
		/// which makes another picture of the same page than the book holds: the helper keys everything it stores on the picture it is
		/// given, so reading, translating ahead, the batch and the export would each translate the page again. The translator is
		/// therefore always given the book's own bytes.
		/// </summary>
		public Func<PageKey, byte[]> OriginalBytes { get; set; }

		private byte[] UploadBytesFor(PageKey key, PageImage image)
		{
			Func<PageKey, byte[]> original = OriginalBytes;
			if (original != null && key != null && !key.Adjustment.IsEmpty && (int)key.Rotation == 0)
			{
				try
				{
					byte[] raw = original(key);
					if (IsReadableFormat(raw))
					{
						return raw;
					}
				}
				catch (Exception)
				{
				}
			}
			return GetUploadBytes(image);
		}

		/// <summary>Where the user's changes and the list of translated pages are kept.</summary>
		public TranslationStore Store { get; }

		public PageTranslationService(TranslationStore store = null)
		{
			Store = store ?? TranslationStore.Shared;
		}

		/// <summary>
		/// Raised on the background thread when a page has been translated (or found to
		/// have no text, or failed), with the page's key.
		/// </summary>
		public event EventHandler<PageKeyEventArgs> PageFinished;

		/// <summary>
		/// Raised on a background thread when pages were forgotten, so the reader asks for them again.
		/// </summary>
		public event EventHandler StateChanged;

		/// <summary>
		/// Raised on a background thread when the helper has said whether a requested page is already stored: the status light can
		/// change colour (blue while pages only load from the cache, amber while they are drawn again or translated). Nothing else
		/// about the pages changed, so nothing needs to be asked for again.
		/// </summary>
		public event EventHandler LightChanged;

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

		/// <summary>How many pages are waiting or being translated.</summary>
		public int PendingCount
		{
			get
			{
				lock (sync)
				{
					return entries.Values.Count((Entry e) => e.State == State.Pending || e.State == State.Working);
				}
			}
		}

		/// <summary>
		/// True while pages are only being loaded from what the helper stored: everything that is waiting or being drawn is already
		/// translated, so nothing is really being read or translated. False as soon as one page needs real work, or when it is not known yet.
		/// </summary>
		public bool IsLoadingFromCache
		{
			get
			{
				lock (sync)
				{
					List<Entry> active = entries.Values.Where(IsShownByLight).ToList();
					return active.Count > 0 && active.All((Entry e) => !e.RenderOnly && e.FromCache == true);
				}
			}
		}

		/// <summary>
		/// True while the pages being worked on were all read and translated before and at least one of them is only being drawn again
		/// (its stored picture does not suit any more): the light is amber and says "Redrawing", not "Translating".
		/// </summary>
		public bool IsRedrawing
		{
			get
			{
				lock (sync)
				{
					List<Entry> active = entries.Values.Where(IsShownByLight).ToList();
					return active.Count > 0 && active.Any((Entry e) => e.Redraw || e.RenderOnly) && active.All((Entry e) => e.RenderOnly || e.FromCache == true || e.Redraw);
				}
			}
		}

		/// <summary>
		/// True while anything is being read, translated, drawn or loaded from what was stored, the pages loaded ahead in the background
		/// included: the status light stays on (blue while it is only stored pages, amber when something needs real work) until all of it is done.
		/// </summary>
		public bool IsBusyForLight
		{
			get
			{
				lock (sync)
				{
					return entries.Values.Any(IsShownByLight);
				}
			}
		}

		private static bool IsShownByLight(Entry e)
		{
			return e.State == State.Pending || e.State == State.Working;
		}

		private static string Fingerprint(byte[] data)
		{
			using (System.Security.Cryptography.SHA1 sha = System.Security.Cryptography.SHA1.Create())
			{
				return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
			}
		}

		/// <summary>Asks the helper, off the worker thread, whether a page that was just requested is already stored, so the light shows it from the start.</summary>
		private void CheckCache(Entry entry, byte[] data)
		{
			string url = ServerUrl;
			ThreadPool.QueueUserWorkItem(delegate
			{
				bool cached = false, picture = false, edited = false;
				try
				{
					StyleFonts fonts = Store.GetFonts(entry.BookKey, entry.ImageIndex);
					cached = new TranslationApi(url) { Book = entry.BookKey, BackCover = entry.BackCover, Target = Target }.IsCached(Fingerprint(data), fonts, entry.Cover, out picture);
					//the user's edits and protect strokes are drawn by the helper each time the page is shown
					edited = cached && (Store.GetEdits(entry.BookKey, entry.ImageIndex).Count > 0 || Store.GetProtect(entry.BookKey, entry.ImageIndex).Count > 0);
				}
				catch (Exception)
				{
				}
				bool known = false;
				lock (sync)
				{
					if (entry.FromCache == null)
					{
						entry.FromCache = cached && picture && !edited;
						entry.Redraw = cached && (!picture || edited);
						known = entry.State == State.Pending || entry.State == State.Working;
					}
				}
				//The light was set when the page was asked for, before this answer came; a page loaded from the cache is often done
				//before the next look at the light, so it never turned blue.
				if (known)
				{
					LightChanged?.Invoke(this, EventArgs.Empty);
				}
			});
		}

		/// <summary>True while any page is being translated or waiting to be.</summary>
		public bool IsBusy
		{
			get
			{
				lock (sync)
				{
					return entries.Values.Any((Entry e) => e.State == State.Pending || e.State == State.Working);
				}
			}
		}

		/// <summary>
		/// The translated page, or null when it is not ready (or has no text to translate). While a
		/// page is drawn again after an edit, the previous picture stays on screen.
		/// </summary>
		public PageImage GetResult(PageKey key)
		{
			if (key == null)
			{
				return null;
			}
			lock (sync)
			{
				if (entries.TryGetValue(key, out Entry entry) && entry.State != State.Failed && entry.State != State.NoText && entry.Result != null)
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
		/// The boxes and picture of a page that has been translated, for the editor; null when the
		/// page is not translated yet.
		/// </summary>
		public TranslationPageState GetState(PageKey key)
		{
			if (key == null)
			{
				return null;
			}
			lock (sync)
			{
				if (!entries.TryGetValue(key, out Entry entry) || entry.Boxes == null)
				{
					return null;
				}
				entry.LastUsed = DateTime.UtcNow;
				return new TranslationPageState
				{
					Width = entry.Width,
					Height = entry.Height,
					Sha = entry.Sha,
					Boxes = entry.Boxes.Select((TranslationBox b) => b.Clone()).ToList(),
					Image = entry.ResultBytes,
					Waiting = entry.State == State.Pending || entry.State == State.Working
				};
			}
		}

		/// <summary>
		/// Asks for a page to be translated. Lower priority numbers go first; asking again
		/// for a waiting page with a lower number moves it forward.
		/// </summary>
		/// <summary>True when the image is the book's back cover: its last page (when that is not its front cover too).</summary>
		public static bool IsBackCover(ComicBook book, int imageIndex)
		{
			if (book == null || book.PageCount < 2)
			{
				return false;
			}
			try
			{
				return book.TranslatePageToImageIndex(book.PageCount - 1) == imageIndex && !IsFrontCover(book, imageIndex);
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>True when the image is the book's front cover (the page marked as such, else the first one).</summary>
		public static bool IsFrontCover(ComicBook book, int imageIndex)
		{
			if (book == null)
			{
				return false;
			}
			try
			{
				return book.TranslatePageToImageIndex(book.FrontCoverPageIndex) == imageIndex;
			}
			catch (Exception)
			{
				return false;
			}
		}

		public void Request(PageKey key, PageImage image, int priority, ComicBook book = null, bool keep = false)
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
			byte[] data = UploadBytesFor(key, image);
			if (data == null)
			{
				return;
			}
			string bookKey = TranslationStore.BookKeyOf(book);
			if (bookKey != null)
			{
				Store.Describe(bookKey, book.Caption, book.FilePath);
			}
			lock (sync)
			{
				Entry fresh = new Entry
				{
					State = State.Pending,
					Source = data,
					Priority = priority,
					Order = ++order,
					LastUsed = DateTime.UtcNow,
					BookKey = bookKey,
					ImageIndex = key.Index,
					Keep = keep,
					Cover = IsFrontCover(book, key.Index),
					BackCover = IsBackCover(book, key.Index)
				};
				entries[key] = fresh;
				CheckCache(fresh, data);
				TrimPending();
				EnsureWorker();
			}
			signal.Set();
		}

		/// <summary>
		/// Saves the user's changes of a translated page with the book and draws the page again.
		/// The page's original image is needed to draw it. Returns false when the page is not known.
		/// </summary>
		public bool Edit(PageKey key, PageImage original, IEnumerable<TranslationBox> boxes)
		{
			if (key == null || original == null || stopping)
			{
				return false;
			}
			byte[] data = UploadBytesFor(key, original);
			if (data == null)
			{
				return false;
			}
			List<TranslationBox> list = boxes.Select((TranslationBox b) => b.Clone()).ToList();
			lock (sync)
			{
				if (!entries.TryGetValue(key, out Entry entry) || entry.Boxes == null)
				{
					return false;
				}
				entry.Boxes = list;
				entry.Source = data;
				entry.RenderOnly = true;
				entry.FromCache = false;
				entry.Version++;
				entry.Priority = -1;
				entry.Order = ++order;
				entry.State = State.Pending;
				Store.SetEdits(entry.BookKey, entry.ImageIndex, entry.Sha, list);
				EnsureWorker();
			}
			signal.Set();
			return true;
		}

		/// <summary>The fonts chosen for a page itself, or for its whole book.</summary>
		public StyleFonts GetStyleFonts(PageKey key, bool wholeBook)
		{
			string bookKey;
			int index;
			lock (sync)
			{
				if (key == null || !entries.TryGetValue(key, out Entry entry))
				{
					return new StyleFonts();
				}
				bookKey = entry.BookKey;
				index = entry.ImageIndex;
			}
			return wholeBook ? Store.GetBookFonts(bookKey) : Store.GetPageFonts(bookKey, index);
		}

		/// <summary>
		/// Sets the font of one kind of lettering (capitals or mixed case) for a page or its whole book (an empty name goes back to the
		/// translator's own choice), and has the pages it concerns drawn again. Returns false when the page is not known.
		/// </summary>
		public bool SetStyleFont(PageKey key, bool capitals, string file, bool wholeBook)
		{
			string bookKey;
			int index;
			lock (sync)
			{
				if (key == null || !entries.TryGetValue(key, out Entry entry) || entry.BookKey == null)
				{
					return false;
				}
				bookKey = entry.BookKey;
				index = entry.ImageIndex;
			}
			StyleFonts fonts = wholeBook ? Store.GetBookFonts(bookKey) : Store.GetPageFonts(bookKey, index);
			if (capitals)
			{
				fonts.Caps = file ?? "";
			}
			else
			{
				fonts.Mixed = file ?? "";
			}
			if (wholeBook)
			{
				Store.SetBookFonts(bookKey, fonts);
			}
			else
			{
				Store.SetPageFonts(bookKey, index, fonts);
			}
			RedrawInMemory(bookKey, wholeBook ? -1 : index);
			return true;
		}

		/// <summary>
		/// Puts the font choices of a page and of its book back to given ones (undo and redo) and has the pages they concern drawn again.
		/// Returns true when anything was different.
		/// </summary>
		public bool RestoreStyleFonts(PageKey key, StyleFonts pageFonts, StyleFonts bookFonts)
		{
			string bookKey;
			int index;
			lock (sync)
			{
				if (key == null || !entries.TryGetValue(key, out Entry entry) || entry.BookKey == null)
				{
					return false;
				}
				bookKey = entry.BookKey;
				index = entry.ImageIndex;
			}
			bool pageChanged = pageFonts != null && !pageFonts.Same(Store.GetPageFonts(bookKey, index));
			bool bookChanged = bookFonts != null && !bookFonts.Same(Store.GetBookFonts(bookKey));
			if (!pageChanged && !bookChanged)
			{
				return false;
			}
			if (pageChanged)
			{
				Store.SetPageFonts(bookKey, index, pageFonts);
			}
			if (bookChanged)
			{
				Store.SetBookFonts(bookKey, bookFonts);
			}
			RedrawInMemory(bookKey, bookChanged ? -1 : index);
			return true;
		}

		/// <summary>What the last StateChanged was about: "fonts" (pages drawn again with other fonts) or "forget" (translations deleted).</summary>
		public string LastStateChange { get; private set; } = "";

		/// <summary>Drops the pictures kept of a book's pages (or one page), so that they are asked for, and drawn, again when shown.</summary>
		public void RedrawInMemory(string bookKey, int page = -1)
		{
			lock (sync)
			{
				foreach (PageKey k in entries.Where((KeyValuePair<PageKey, Entry> kv) => kv.Value.BookKey == bookKey && (page < 0 || kv.Value.ImageIndex == page)).Select((KeyValuePair<PageKey, Entry> kv) => kv.Key).ToList())
				{
					entries.Remove(k);
				}
			}
			LastStateChange = "fonts";
			StateChanged?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>The protect brush strokes of a page (copies); empty when there are none.</summary>
		public List<ProtectStroke> GetProtect(PageKey key)
		{
			if (key == null)
			{
				return new List<ProtectStroke>();
			}
			lock (sync)
			{
				return entries.TryGetValue(key, out Entry entry) ? Store.GetProtect(entry.BookKey, entry.ImageIndex) : new List<ProtectStroke>();
			}
		}

		/// <summary>
		/// Saves the protect brush strokes of a page and draws the page again with them: the painted artwork is not wiped and
		/// is not lettered over. Returns false when the page is not translated yet.
		/// </summary>
		public bool SetProtect(PageKey key, PageImage original, IEnumerable<ProtectStroke> strokes)
		{
			if (key == null)
			{
				return false;
			}
			List<TranslationBox> boxes;
			lock (sync)
			{
				if (!entries.TryGetValue(key, out Entry entry) || entry.Boxes == null)
				{
					return false;
				}
				Store.SetProtect(entry.BookKey, entry.ImageIndex, strokes);
				boxes = entry.Boxes.Select((TranslationBox b) => b.Clone()).ToList();
			}
			return Edit(key, original, boxes);
		}

		/// <summary>
		/// Reads and translates the text inside a rectangle of the page (in page pixels). The result
		/// arrives on a background thread, with an error text when it failed.
		/// </summary>
		public void ReadRegion(PageImage original, int[] rect, Action<TranslationRegion, string> done, bool sign = false)
		{
			byte[] data = original == null ? null : GetUploadBytes(original);
			string url = ServerUrl;
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					if (data == null)
					{
						throw new TranslationApiException("The page could not be read.", serverDown: false);
					}
					done(new TranslationApi(url).ReadRegion(data, rect, sign), null);
				}
				catch (Exception ex)
				{
					done(null, ex.Message);
				}
			});
		}

		/// <summary>Looks for the font of the lettering inside a rectangle of the page. The answer arrives on a background thread.</summary>
		public void IdentifyFont(PageImage original, int[] rect, string text, bool online, Action<FontIdentification, string> done)
		{
			byte[] data = original == null ? null : GetUploadBytes(original);
			string url = ServerUrl;
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					if (data == null)
					{
						throw new TranslationApiException("The page could not be read.", serverDown: false);
					}
					done(new TranslationApi(url).IdentifyFont(data, rect, text, online), null);
				}
				catch (Exception ex)
				{
					done(null, ex.Message);
				}
			});
		}

		/// <summary>Looks for the font of all the given boxes of one page together. The answer arrives on a background thread.</summary>
		public void IdentifyPageFont(PageImage original, List<int[]> rects, string text, bool online, Action<FontIdentification, string> done, List<string> texts = null)
		{
			byte[] data = original == null ? null : GetUploadBytes(original);
			string url = ServerUrl;
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					if (data == null)
					{
						throw new TranslationApiException("The page could not be read.", serverDown: false);
					}
					done(new TranslationApi(url).IdentifyPageFont(data, rects, text, online, texts), null);
				}
				catch (Exception ex)
				{
					done(null, ex.Message);
				}
			});
		}

		/// <summary>Asks what the font search is doing; the answer arrives on a background thread (null when the helper does not say).</summary>
		public void FontSearchProgress(Action<FontProgress> done)
		{
			string url = ServerUrl;
			ThreadPool.QueueUserWorkItem(delegate
			{
				FontProgress progress = null;
				try
				{
					progress = new TranslationApi(url).FontSearchProgress();
				}
				catch (Exception)
				{
				}
				done(progress);
			});
		}

		/// <summary>Stops the font search that is running.</summary>
		public void CancelFontSearch()
		{
			string url = ServerUrl;
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					new TranslationApi(url).CancelFontSearch();
				}
				catch (Exception)
				{
				}
			});
		}

		/// <summary>
		/// Draws the page as it would look with the given fonts, to look at (nothing is saved). The picture (a JPEG) arrives on a background
		/// thread.
		/// </summary>
		public void PreviewWithFonts(PageKey key, PageImage original, StyleFonts fonts, Action<byte[], string> done)
		{
			byte[] data = original == null ? null : UploadBytesFor(key, original);
			string url = ServerUrl;
			List<TranslationBox> boxes;
			string bookKey;
			int index;
			bool cover;
			bool backCover;
			lock (sync)
			{
				if (key == null || !entries.TryGetValue(key, out Entry entry) || entry.Boxes == null)
				{
					done(null, "This page is not translated yet.");
					return;
				}
				boxes = entry.Boxes.Select((TranslationBox b) => b.Clone()).ToList();
				bookKey = entry.BookKey;
				index = entry.ImageIndex;
				cover = entry.Cover;
				backCover = entry.BackCover;
			}
			List<ProtectStroke> protect = Store.GetProtect(bookKey, index);
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					if (data == null)
					{
						throw new TranslationApiException("The page could not be read.", serverDown: false);
					}
					done(new TranslationApi(url) { Book = bookKey, BackCover = backCover, Target = Target }.Render(data, boxes, protect, fonts, cover).Image, null);
				}
				catch (Exception ex)
				{
					done(null, ex.Message);
				}
			});
		}

		/// <summary>The font files on this computer the translator can use. The answer arrives on a background thread.</summary>
		public void ListFonts(Action<List<FontFile>, string> done)
		{
			string url = ServerUrl;
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					done(new TranslationApi(url).FontsOnPc(), null);
				}
				catch (Exception ex)
				{
					done(null, ex.Message);
				}
			});
		}

		/// <summary>The bytes of a page as they are sent to the helper (so that it finds what it stored for it).</summary>
		public byte[] UploadBytesOf(PageImage image)
		{
			return image == null ? null : GetUploadBytes(image);
		}

		/// <summary>Looks for the font of a book from a sample of its pages. The answer arrives on a background thread.</summary>
		public void IdentifyBookFont(List<byte[]> pages, bool online, Action<FontIdentification, string> done)
		{
			string url = ServerUrl;
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					if (pages == null || pages.Count == 0)
					{
						throw new TranslationApiException("No translated pages of this book could be read.", serverDown: false);
					}
					done(new TranslationApi(url).IdentifyBookFont(pages, online), null);
				}
				catch (Exception ex)
				{
					done(null, ex.Message);
				}
			});
		}

		/// <summary>Installs a font the search found into the helper (and uses it for the lettering when asked). The answer arrives on a background thread.</summary>
		public void InstallFont(string family, bool use, Action<string, string> done)
		{
			string url = ServerUrl;
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					done(new TranslationApi(url).InstallFont(family, use), null);
				}
				catch (Exception ex)
				{
					done(null, ex.Message);
				}
			});
		}

		/// <summary>
		/// Forgets one page: the helper's stored copy, what is kept in memory and the book's record of
		/// it (including the user's changes unless they are kept). The page is translated again when next shown.
		/// </summary>
		public void ForgetPage(PageKey key, string bookKeyOfPage, bool keepEdits)
		{
			if (key == null)
			{
				return;
			}
			string bookKey;
			string sha;
			lock (sync)
			{
				entries.TryGetValue(key, out Entry entry);
				bookKey = entry?.BookKey ?? bookKeyOfPage;
				sha = entry?.Sha ?? Store.GetSha(bookKey, key.Index);
			}
			int index = key.Index;
			Forget(sha == null ? new string[0] : new[] { sha }, delegate
			{
				lock (sync)
				{
					entries.Remove(key);
				}
				Store.ForgetPage(bookKey, index, keepEdits);
			});
		}

		/// <summary>
		/// Forgets every translated page of a book, in the helper, in memory and in the book's record.
		/// </summary>
		public void ForgetBook(string bookKey, bool keepEdits)
		{
			if (bookKey == null)
			{
				return;
			}
			List<string> shas = Store.GetShas(bookKey);
			lock (sync)
			{
				shas.AddRange(entries.Values.Where((Entry e) => e.BookKey == bookKey && e.Sha != null).Select((Entry e) => e.Sha));
			}
			Forget(shas.Distinct(), delegate
			{
				lock (sync)
				{
					foreach (PageKey k in entries.Where((KeyValuePair<PageKey, Entry> kv) => kv.Value.BookKey == bookKey).Select((KeyValuePair<PageKey, Entry> kv) => kv.Key).ToList())
					{
						entries.Remove(k);
					}
				}
				Store.ForgetBook(bookKey, keepEdits);
			});
		}

		private void Forget(IEnumerable<string> shas, Action then)
		{
			List<string> list = shas.ToList();
			string url = ServerUrl;
			ThreadPool.QueueUserWorkItem(delegate
			{
				string problem = null;
				try
				{
					if (list.Count > 0)
					{
						new TranslationApi(url).Forget(list);
					}
				}
				catch (Exception ex)
				{
					problem = ex.Message;
				}
				then();
				lock (sync)
				{
					status = problem;
				}
				LastStateChange = "forget";
				StateChanged?.Invoke(this, EventArgs.Empty);
			});
		}

		/// <summary>
		/// Forgets the pages that are waiting and not yet being translated (the one being translated is left to finish),
		/// so something more urgent can have the helper next. They can simply be asked for again later.
		/// </summary>
		public void DropPending()
		{
			lock (sync)
			{
				foreach (PageKey key in entries.Where((KeyValuePair<PageKey, Entry> kv) => kv.Value.State == State.Pending && !kv.Value.RenderOnly).Select((KeyValuePair<PageKey, Entry> kv) => kv.Key).ToList())
				{
					entries.Remove(key);
				}
			}
		}

		/// <summary>
		/// Forgets every translated and waiting page from memory, e.g. when translation is turned off,
		/// so turning it on again picks up changed settings. What is saved with books stays.
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

		/// <summary>The bytes of a page as the helper reads them: the original file data when it is a common format.</summary>
		public static byte[] GetUploadBytes(PageImage image)
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
			List<KeyValuePair<PageKey, Entry>> pending = entries.Where((KeyValuePair<PageKey, Entry> kv) => kv.Value.State == State.Pending && !kv.Value.RenderOnly && !kv.Value.Keep).OrderBy((KeyValuePair<PageKey, Entry> kv) => kv.Value.Priority).ThenByDescending((KeyValuePair<PageKey, Entry> kv) => kv.Value.Order).ToList();
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
			PageAnalysis page = null;
			string problem = null;
			bool serverDown = false;
			byte[] source;
			bool renderOnly;
			bool cover;
			List<TranslationBox> boxes;
			int version;
			lock (sync)
			{
				source = entry.Source;
				renderOnly = entry.RenderOnly;
				cover = entry.Cover;
				boxes = entry.Boxes?.Select((TranslationBox b) => b.Clone()).ToList();
				version = entry.Version;
			}
			try
			{
				TranslationApi api = new TranslationApi(ServerUrl) { Book = entry.BookKey, BackCover = entry.BackCover, Target = Target };
				List<ProtectStroke> protect = Store.GetProtect(entry.BookKey, entry.ImageIndex);
				StyleFonts fonts = Store.GetFonts(entry.BookKey, entry.ImageIndex);
				if (renderOnly)
				{
					page = api.Render(source, boxes, protect, fonts, cover);
				}
				else
				{
					page = api.Analyze(source, fonts, cover);
					List<TranslationBox> edits = Store.GetEdits(entry.BookKey, entry.ImageIndex);
					if ((edits.Count > 0 && TranslationStore.Merge(page.Boxes, edits)) || protect.Count > 0)
					{
						page = api.Render(source, page.Boxes, protect, fonts, cover);
					}
				}
				result = page.Changed ? State.Done : State.NoText;
			}
			catch (TranslationApiException ex)
			{
				serverDown = ex.ServerDown;
				problem = ex.Message;
			}
			catch (Exception ex)
			{
				problem = "Translation failed: " + ex.Message;
			}
			PageImage image = null;
			if (result == State.Done)
			{
				try
				{
					image = PageImage.CreateFrom(page.Image);
				}
				catch (Exception ex)
				{
					result = State.Failed;
					problem = "The translated page could not be read: " + ex.Message;
				}
			}
			lock (sync)
			{
				//Cleared (translation turned off) while this page was out: forget it.
				if (!entries.TryGetValue(key, out Entry current) || current != entry)
				{
					return;
				}
				entry.Finished = DateTime.UtcNow;
				entry.LastUsed = DateTime.UtcNow;
				status = problem;
				if (entry.Version != version)
				{
					//Edited while it was being drawn: that edit is already waiting, this picture is out of date.
					entry.State = State.Pending;
					return;
				}
				if (page != null && result != State.Failed)
				{
					entry.Boxes = page.Boxes;
					entry.Sha = page.Sha;
					entry.Width = page.Width;
					entry.Height = page.Height;
					entry.Result = image;
					entry.ResultBytes = result == State.Done ? page.Image : null;
					entry.Source = null;
					entry.RenderOnly = false;
					//the language the helper says it used (an older helper does not say: then the one asked for, if any)
					Store.RecordPage(entry.BookKey, entry.ImageIndex, page.Sha, string.IsNullOrEmpty(page.Language) ? Target : page.Language);
				}
				else if (renderOnly)
				{
					//A failed redraw keeps the old picture and boxes; the edit itself is saved.
					entry.RenderOnly = false;
					entry.Source = null;
					result = entry.Result != null ? State.Done : State.NoText;
				}
				entry.State = result;
				if (result == State.Failed && serverDown)
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
	}
}
