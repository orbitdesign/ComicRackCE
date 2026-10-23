using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using cYo.Projects.ComicRack.Engine;

namespace cYo.Projects.ComicRack.Engine.Display.Forms
{
	/// <summary>
	/// Keeps what the user changed on translated pages (moved, resized, rotated or retyped boxes and
	/// boxes added by hand) together with the book, in one small JSON file per book. The file is
	/// found again through the book's library id, or its path for books that are not in the library.
	/// It also remembers which pages of the book were translated (and the page's fingerprint, which
	/// is how the helper's stored copy of the page is found and forgotten).
	/// </summary>
	public class TranslationStore
	{
		private class StoredPage
		{
			public string Sha = "";

			//The languages the page was translated into ("en", "zh-CN"); empty for pages noted before 1.168 (language not known).
			public List<string> Languages = new List<string>();

			public List<TranslationBox> Edits = new List<TranslationBox>();

			//Artwork the user painted over with the protect brush.
			public List<ProtectStroke> Protect = new List<ProtectStroke>();

			//Fonts chosen for this page's lettering (they come before the book's).
			public StyleFonts Fonts = new StyleFonts();

			public bool HasUserData => Edits.Count > 0 || Protect.Count > 0 || !Fonts.IsEmpty;
		}

		private class BookData
		{
			public string Title = "";

			public string Path = "";

			//Fonts chosen for the whole book's lettering.
			public StyleFonts Fonts = new StyleFonts();

			public readonly SortedDictionary<int, StoredPage> Pages = new SortedDictionary<int, StoredPage>();

			public bool Dirty;
		}

		private readonly string folder;

		private readonly object sync = new object();

		private readonly Dictionary<string, BookData> books = new Dictionary<string, BookData>();

		public TranslationStore(string folder)
		{
			this.folder = folder;
		}

		private static TranslationStore shared;

		/// <summary>
		/// The one store the reader and the batch translation both use, so what one records (translated pages, edits)
		/// is what the other reads; two stores over the same files would overwrite each other.
		/// </summary>
		public static TranslationStore Shared
		{
			get
			{
				if (shared == null)
				{
					shared = new TranslationStore(DefaultFolder);
				}
				return shared;
			}
		}

		public static string DefaultFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cYo", "ComicRack Community Edition", "Translations");

		public string Folder => folder;

		/// <summary>The name the book's data is filed under.</summary>
		public static string BookKeyOf(ComicBook book)
		{
			if (book == null)
			{
				return null;
			}
			if (book.Id != Guid.Empty)
			{
				return "book-" + book.Id.ToString("N");
			}
			string path = book.FilePath;
			if (string.IsNullOrEmpty(path))
			{
				return null;
			}
			using (SHA1 sha = SHA1.Create())
			{
				return "file-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()))).Replace("-", "").ToLowerInvariant();
			}
		}

		/// <summary>Tells the store what the book is called, so the file can be recognised by a person.</summary>
		public void Describe(string bookKey, string title, string path)
		{
			lock (sync)
			{
				BookData book = Load(bookKey);
				if (book.Title != (title ?? "") || book.Path != (path ?? ""))
				{
					book.Title = title ?? "";
					book.Path = path ?? "";
					book.Dirty = true;
				}
			}
		}

		/// <summary>Notes that a page was translated, what its fingerprint is and (when known) the language it was translated into.</summary>
		public void RecordPage(string bookKey, int page, string sha, string language = null)
		{
			if (bookKey == null || string.IsNullOrEmpty(sha))
			{
				return;
			}
			lock (sync)
			{
				BookData book = Load(bookKey);
				if (!book.Pages.TryGetValue(page, out StoredPage stored))
				{
					book.Pages[page] = stored = new StoredPage();
				}
				if (stored.Sha != sha)
				{
					//another picture of the page: what it was translated into before is about the old one
					stored.Sha = sha;
					stored.Languages.Clear();
					book.Dirty = true;
				}
				if (!string.IsNullOrEmpty(language) && !stored.Languages.Contains(language))
				{
					stored.Languages.Add(language);
					book.Dirty = true;
				}
				Save(bookKey, book);
			}
		}

		/// <summary>The boxes the user changed or added on a page (copies).</summary>
		public List<TranslationBox> GetEdits(string bookKey, int page)
		{
			if (bookKey == null)
			{
				return new List<TranslationBox>();
			}
			lock (sync)
			{
				BookData book = Load(bookKey);
				return book.Pages.TryGetValue(page, out StoredPage stored) ? stored.Edits.Select((TranslationBox b) => b.Clone()).ToList() : new List<TranslationBox>();
			}
		}

		/// <summary>Saves the user's changes of a page, given all the page's boxes: only the changed or added ones are kept.</summary>
		public void SetEdits(string bookKey, int page, string sha, IEnumerable<TranslationBox> boxes)
		{
			if (bookKey == null)
			{
				return;
			}
			lock (sync)
			{
				BookData book = Load(bookKey);
				if (!book.Pages.TryGetValue(page, out StoredPage stored))
				{
					book.Pages[page] = stored = new StoredPage();
				}
				if (!string.IsNullOrEmpty(sha))
				{
					stored.Sha = sha;
				}
				stored.Edits = boxes.Where((TranslationBox b) => b.IsEdited).Select((TranslationBox b) => b.Clone()).ToList();
				book.Dirty = true;
				Save(bookKey, book);
			}
		}

		/// <summary>The fonts the lettering of a page is drawn with: the page's own choice for each kind of lettering, else the book's.</summary>
		public StyleFonts GetFonts(string bookKey, int page)
		{
			if (bookKey == null)
			{
				return new StyleFonts();
			}
			lock (sync)
			{
				BookData book = Load(bookKey);
				StyleFonts own = book.Pages.TryGetValue(page, out StoredPage stored) ? stored.Fonts : new StyleFonts();
				return new StyleFonts
				{
					Caps = string.IsNullOrEmpty(own.Caps) ? book.Fonts.Caps ?? "" : own.Caps,
					Mixed = string.IsNullOrEmpty(own.Mixed) ? book.Fonts.Mixed ?? "" : own.Mixed
				};
			}
		}

		/// <summary>The fonts chosen for a page itself (not the book's).</summary>
		public StyleFonts GetPageFonts(string bookKey, int page)
		{
			if (bookKey == null)
			{
				return new StyleFonts();
			}
			lock (sync)
			{
				return Load(bookKey).Pages.TryGetValue(page, out StoredPage stored) ? stored.Fonts.Clone() : new StyleFonts();
			}
		}

		public StyleFonts GetBookFonts(string bookKey)
		{
			if (bookKey == null)
			{
				return new StyleFonts();
			}
			lock (sync)
			{
				return Load(bookKey).Fonts.Clone();
			}
		}

		public void SetPageFonts(string bookKey, int page, StyleFonts fonts)
		{
			if (bookKey == null)
			{
				return;
			}
			lock (sync)
			{
				BookData book = Load(bookKey);
				if (!book.Pages.TryGetValue(page, out StoredPage stored))
				{
					book.Pages[page] = stored = new StoredPage();
				}
				stored.Fonts = (fonts ?? new StyleFonts()).Clone();
				if (!stored.HasUserData && string.IsNullOrEmpty(stored.Sha))
				{
					book.Pages.Remove(page);
				}
				book.Dirty = true;
				Save(bookKey, book);
			}
		}

		public void SetBookFonts(string bookKey, StyleFonts fonts)
		{
			if (bookKey == null)
			{
				return;
			}
			lock (sync)
			{
				BookData book = Load(bookKey);
				book.Fonts = (fonts ?? new StyleFonts()).Clone();
				book.Dirty = true;
				Save(bookKey, book);
			}
		}

		/// <summary>The protect brush strokes of a page (copies).</summary>
		public List<ProtectStroke> GetProtect(string bookKey, int page)
		{
			if (bookKey == null)
			{
				return new List<ProtectStroke>();
			}
			lock (sync)
			{
				BookData book = Load(bookKey);
				return book.Pages.TryGetValue(page, out StoredPage stored) ? stored.Protect.Select((ProtectStroke p) => p.Clone()).ToList() : new List<ProtectStroke>();
			}
		}

		/// <summary>Saves the protect brush strokes of a page.</summary>
		public void SetProtect(string bookKey, int page, IEnumerable<ProtectStroke> strokes)
		{
			if (bookKey == null)
			{
				return;
			}
			lock (sync)
			{
				BookData book = Load(bookKey);
				if (!book.Pages.TryGetValue(page, out StoredPage stored))
				{
					book.Pages[page] = stored = new StoredPage();
				}
				stored.Protect = strokes.Where((ProtectStroke p) => p.Points.Length >= 2).Select((ProtectStroke p) => p.Clone()).ToList();
				if (!stored.HasUserData && string.IsNullOrEmpty(stored.Sha))
				{
					book.Pages.Remove(page);
				}
				book.Dirty = true;
				Save(bookKey, book);
			}
		}

		public int EditedPageCount(string bookKey)
		{
			if (bookKey == null)
			{
				return 0;
			}
			lock (sync)
			{
				return Load(bookKey).Pages.Values.Count((StoredPage p) => p.Edits.Count > 0 || p.Protect.Count > 0);
			}
		}

		/// <summary>The pages translated (into the language given, when one is: a page noted before languages were, is not counted
		/// for any).</summary>
		public int TranslatedPageCount(string bookKey, string language = null)
		{
			if (bookKey == null)
			{
				return 0;
			}
			lock (sync)
			{
				return Load(bookKey).Pages.Values.Count((StoredPage p) => IsDone(p, language));
			}
		}

		public bool IsTranslated(string bookKey, int page, string language = null)
		{
			if (bookKey == null)
			{
				return false;
			}
			lock (sync)
			{
				return Load(bookKey).Pages.TryGetValue(page, out StoredPage stored) && IsDone(stored, language);
			}
		}

		private static bool IsDone(StoredPage page, string language)
		{
			return page.Sha.Length > 0 && (string.IsNullOrEmpty(language) || page.Languages.Contains(language));
		}

		/// <summary>The fingerprint of one page, or null when it was never translated.</summary>
		public string GetSha(string bookKey, int page)
		{
			if (bookKey == null)
			{
				return null;
			}
			lock (sync)
			{
				return Load(bookKey).Pages.TryGetValue(page, out StoredPage stored) && stored.Sha.Length > 0 ? stored.Sha : null;
			}
		}

		/// <summary>The fingerprints of every translated page of the book.</summary>
		public List<string> GetShas(string bookKey)
		{
			if (bookKey == null)
			{
				return new List<string>();
			}
			lock (sync)
			{
				return Load(bookKey).Pages.Values.Where((StoredPage p) => p.Sha.Length > 0).Select((StoredPage p) => p.Sha).Distinct().ToList();
			}
		}

		/// <summary>Forgets a page: that it was translated and, unless they are kept, the user's changes to it.</summary>
		public void ForgetPage(string bookKey, int page, bool keepEdits)
		{
			if (bookKey == null)
			{
				return;
			}
			lock (sync)
			{
				BookData book = Load(bookKey);
				if (book.Pages.TryGetValue(page, out StoredPage stored))
				{
					stored.Sha = "";
					stored.Languages.Clear();
					if (!keepEdits)
					{
						stored.Edits.Clear();
						stored.Protect.Clear();
						stored.Fonts = new StyleFonts();
					}
					if (!stored.HasUserData)
					{
						book.Pages.Remove(page);
					}
					book.Dirty = true;
					Save(bookKey, book);
				}
			}
		}

		public void ForgetBook(string bookKey, bool keepEdits)
		{
			if (bookKey == null)
			{
				return;
			}
			lock (sync)
			{
				BookData book = Load(bookKey);
				foreach (int page in book.Pages.Keys.ToList())
				{
					StoredPage stored = book.Pages[page];
					stored.Sha = "";
					stored.Languages.Clear();
					if (!keepEdits)
					{
						stored.Edits.Clear();
						stored.Protect.Clear();
						stored.Fonts = new StyleFonts();
					}
					if (!stored.HasUserData)
					{
						book.Pages.Remove(page);
					}
				}
				book.Dirty = true;
				Save(bookKey, book);
			}
		}

		/// <summary>Puts the user's changes of a page onto freshly read boxes. Returns true when anything was applied.</summary>
		public static bool Merge(List<TranslationBox> fresh, List<TranslationBox> edits)
		{
			bool any = false;
			HashSet<TranslationBox> used = new HashSet<TranslationBox>();
			foreach (TranslationBox edit in edits)
			{
				if (edit.Manual)
				{
					fresh.Add(edit.Clone());
					any = true;
					continue;
				}
				TranslationBox match = fresh.Where((TranslationBox f) => !f.Manual && !used.Contains(f)).Select((TranslationBox f) => new { Box = f, Overlap = Overlap(f.Src, edit.Src), SameId = f.Id == edit.Id }).Where((x) => x.Overlap > (x.SameId ? 0.3 : 0.5)).OrderByDescending((x) => x.Overlap).Select((x) => x.Box).FirstOrDefault();
				if (match == null)
				{
					continue;
				}
				used.Add(match);
				if (edit.TextEdited)
				{
					match.Translation = edit.Translation;
					match.TextEdited = true;
				}
				match.Rect = (int[])edit.Rect?.Clone();
				match.Form = edit.Form;
				match.ShapeAngle = edit.ShapeAngle;
				match.Points = (int[])edit.Points?.Clone();
				match.Corners = (int[])(edit.Corners ?? new int[4]).Clone();
				match.Circular = edit.Circular;
				match.Size = edit.Size;
				match.FontChoice = edit.FontChoice;
				match.Align = edit.Align;
				match.Rotation = edit.Rotation;
				match.Skip = edit.Skip;
				match.AsSign = edit.AsSign;
				match.AsCover = edit.AsCover;
				match.MergedInto = edit.MergedInto;
				any = true;
			}
			return any;
		}

		private static double Overlap(int[] a, int[] b)
		{
			long w = Math.Min(a[2], b[2]) - Math.Max(a[0], b[0]);
			long h = Math.Min(a[3], b[3]) - Math.Max(a[1], b[1]);
			if (w <= 0 || h <= 0)
			{
				return 0;
			}
			double inter = w * h;
			double union = (double)(a[2] - a[0]) * (a[3] - a[1]) + (double)(b[2] - b[0]) * (b[3] - b[1]) - inter;
			return union <= 0 ? 0 : inter / union;
		}

		/// <summary>A fresh id for a box the user adds.</summary>
		public static string NewBoxId()
		{
			return "m" + Guid.NewGuid().ToString("N").Substring(0, 8);
		}

		private string FileOf(string bookKey)
		{
			return System.IO.Path.Combine(folder, bookKey + ".json");
		}

		private BookData Load(string bookKey)
		{
			if (books.TryGetValue(bookKey, out BookData cached))
			{
				return cached;
			}
			BookData book = new BookData();
			try
			{
				string file = FileOf(bookKey);
				if (File.Exists(file))
				{
					using (JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(file)))
					{
						JsonElement root = doc.RootElement;
						book.Title = TranslationBox.GetString(root, "title");
						book.Path = TranslationBox.GetString(root, "path");
						if (root.TryGetProperty("fonts", out JsonElement bookFonts) && bookFonts.ValueKind == JsonValueKind.Object)
						{
							book.Fonts = StyleFonts.Read(bookFonts);
						}
						if (root.TryGetProperty("pages", out JsonElement pages) && pages.ValueKind == JsonValueKind.Array)
						{
							foreach (JsonElement p in pages.EnumerateArray())
							{
								StoredPage stored = new StoredPage { Sha = TranslationBox.GetString(p, "sha") };
								if (p.TryGetProperty("languages", out JsonElement languages) && languages.ValueKind == JsonValueKind.Array)
								{
									stored.Languages = languages.EnumerateArray().Where((JsonElement l) => l.ValueKind == JsonValueKind.String).Select((JsonElement l) => l.GetString()).Where((string l) => !string.IsNullOrEmpty(l)).Distinct().ToList();
								}
								if (p.TryGetProperty("edits", out JsonElement edits) && edits.ValueKind == JsonValueKind.Array)
								{
									stored.Edits = edits.EnumerateArray().Select(TranslationBox.Read).ToList();
								}
								if (p.TryGetProperty("fonts", out JsonElement pageFonts) && pageFonts.ValueKind == JsonValueKind.Object)
								{
									stored.Fonts = StyleFonts.Read(pageFonts);
								}
								if (p.TryGetProperty("protect", out JsonElement strokes) && strokes.ValueKind == JsonValueKind.Array)
								{
									stored.Protect = strokes.EnumerateArray().Select(ProtectStroke.Read).Where((ProtectStroke x) => x != null).ToList();
								}
								book.Pages[(int)TranslationBox.GetDouble(p, "page")] = stored;
							}
						}
					}
				}
			}
			catch (Exception)
			{
				//A damaged file is treated as empty; it is replaced at the next save.
				book = new BookData();
			}
			books[bookKey] = book;
			return book;
		}

		private void Save(string bookKey, BookData book)
		{
			if (!book.Dirty)
			{
				return;
			}
			try
			{
				string file = FileOf(bookKey);
				if (book.Pages.Count == 0 && book.Fonts.IsEmpty)
				{
					if (File.Exists(file))
					{
						File.Delete(file);
					}
					book.Dirty = false;
					return;
				}
				Directory.CreateDirectory(folder);
				string temp = file + ".tmp";
				using (FileStream fs = File.Create(temp))
				using (Utf8JsonWriter w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
				{
					w.WriteStartObject();
					w.WriteNumber("version", 1);
					w.WriteString("title", book.Title);
					w.WriteString("path", book.Path);
					if (!book.Fonts.IsEmpty)
					{
						book.Fonts.Write(w, "fonts");
					}
					w.WriteStartArray("pages");
					foreach (KeyValuePair<int, StoredPage> kv in book.Pages)
					{
						w.WriteStartObject();
						w.WriteNumber("page", kv.Key);
						w.WriteString("sha", kv.Value.Sha);
						if (kv.Value.Languages.Count > 0)
						{
							w.WriteStartArray("languages");
							foreach (string language in kv.Value.Languages)
							{
								w.WriteStringValue(language);
							}
							w.WriteEndArray();
						}
						w.WriteStartArray("edits");
						foreach (TranslationBox box in kv.Value.Edits)
						{
							box.Write(w);
						}
						w.WriteEndArray();
						if (!kv.Value.Fonts.IsEmpty)
						{
							kv.Value.Fonts.Write(w, "fonts");
						}
						if (kv.Value.Protect.Count > 0)
						{
							w.WriteStartArray("protect");
							foreach (ProtectStroke stroke in kv.Value.Protect)
							{
								stroke.Write(w);
							}
							w.WriteEndArray();
						}
						w.WriteEndObject();
					}
					w.WriteEndArray();
					w.WriteEndObject();
				}
				if (File.Exists(file))
				{
					File.Delete(file);
				}
				File.Move(temp, file);
				book.Dirty = false;
			}
			catch (Exception)
			{
				//Saving is best effort; the next change tries again.
			}
		}
	}
}
