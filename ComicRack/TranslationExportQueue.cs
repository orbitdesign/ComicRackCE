using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using cYo.Projects.ComicRack.Engine;
using cYo.Projects.ComicRack.Engine.Display.Forms;
using cYo.Projects.ComicRack.Engine.IO;
using cYo.Projects.ComicRack.Viewer.Config;

namespace cYo.Projects.ComicRack.Viewer
{
	/// <summary>
	/// Writes translated copies of books one after the other in the background, like the batch translation: Export Translated
	/// Books queues them and closes, the status bar shows the book being written and its pages, and a click there stops the
	/// export or, when it is done, clears the message; the Skip and Stop buttons beside it skip the book being written or stop
	/// the export. Books added while it runs are written after the others. The books not written yet are kept in a file, so
	/// when ComicRack is closed during an export they wait, paused, to be continued the next time.
	/// </summary>
	public static class TranslationExportQueue
	{
		/// <summary>The ending given to the name of each translated copy, before the language.</summary>
		public const string NameEnding = " - AI Translated";

		public class Item
		{
			public ComicBook Book;

			public string Title;

			/// <summary>The language the copy is written in ("en", "zh-CN"), or null for the translator's own.</summary>
			public string Target;

			/// <summary>Written from what the translator stored (nothing is translated); else the missing pages are translated too.</summary>
			public bool Complete;

			public bool AddToLibrary;

			public string Folder;

			public string Name;

			public int FormatId;

			public bool Rar;

			public bool Overwrite;

			public string TargetPath;

			public TranslationExport Export;

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

			/// <summary>A book to be written with the choices last used in Export Translated Books (format, folder, adding to the
			/// library): next to its original or in that folder, named after it with NameEnding and the language.</summary>
			public static Item For(ComicBook book, string target, bool complete, bool overwrite)
			{
				string language = TranslationButtons.LanguageName(target);
				Settings settings = Program.Settings;
				Item item = new Item
				{
					Book = book,
					Title = book.Caption + (language != null ? " (" + language + ")" : ""),
					Target = target,
					Complete = complete,
					AddToLibrary = settings.TranslatorExportAddToLibrary,
					Folder = settings.TranslatorExportBeside || string.IsNullOrWhiteSpace(settings.TranslatorExportFolder) ? Path.GetDirectoryName(book.FilePath) : settings.TranslatorExportFolder.Trim(),
					Name = book.FileName + NameEnding + (language != null ? " (" + language + ")" : ""),
					FormatId = settings.TranslatorExportFormatId,
					Rar = settings.TranslatorExportRar && TranslationExport.FindRar() != null,
					Overwrite = overwrite
				};
				item.TargetPath = item.CreateExport().TargetPath;
				return item;
			}

			public TranslationExport CreateExport()
			{
				ExportSetting setting = new ExportSetting
				{
					Target = ExportTarget.NewFolder,
					TargetFolder = Folder,
					Naming = ExportNaming.Custom,
					CustomName = Name,
					CustomNamingStart = 0,
					FormatId = FormatId,
					Overwrite = Overwrite,
					EmbedComicInfo = true
				};
				return new TranslationExport(Book, setting, Program.CacheManager?.ImagePool, Program.Settings.TranslatorServerUrl, TranslationStore.Shared)
				{
					Target = Target,
					//a translated book is written from what the translator stored; an unfinished one has the rest translated
					StoredOnly = Complete,
					RarPath = Rar ? TranslationExport.FindRar() : null
				};
			}
		}

		private static readonly object sync = new object();

		private static readonly List<Item> items = new List<Item>();

		private static Thread worker;

		private static volatile bool stopping;

		private static DateTime lastChanged = DateTime.MinValue;

		public static event EventHandler Changed;

		private static string QueueFile => Path.Combine(Path.GetDirectoryName(TranslationStore.DefaultFolder), "TranslationExportQueue.json");

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

		/// <summary>Books wait to be written, but the export does not run (it was left when ComicRack closed).</summary>
		public static bool IsPaused => !IsRunning && Waiting.Count > 0;

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
					if (items.Any((Item i) => !i.Finished && string.Equals(i.TargetPath, item.TargetPath, StringComparison.OrdinalIgnoreCase)))
					{
						continue;
					}
					items.Add(item);
					added++;
				}
				SaveWaiting();
			}
			Resume();
			return added;
		}

		/// <summary>Starts writing the books that wait (after a restart, or added just now).</summary>
		public static void Resume()
		{
			lock (sync)
			{
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
				SaveWaiting();
			}
			Current?.Export?.Cancel();
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
			item.Export?.Cancel();
			RaiseChanged(true);
		}

		/// <summary>Forgets the results of an export that ended, and the books of a paused one.</summary>
		public static void Clear()
		{
			lock (sync)
			{
				if (worker != null)
				{
					return;
				}
				items.Clear();
				SaveWaiting();
			}
			RaiseChanged(true);
		}

		/// <summary>Takes back the books an export left unwritten when ComicRack closed: they wait, paused, until continued.</summary>
		public static void Restore(Func<Guid, string, ComicBook> findBook)
		{
			List<Item> found = new List<Item>();
			try
			{
				if (!File.Exists(QueueFile))
				{
					return;
				}
				using (JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(QueueFile)))
				{
					if (!doc.RootElement.TryGetProperty("books", out JsonElement books) || books.ValueKind != JsonValueKind.Array)
					{
						return;
					}
					foreach (JsonElement b in books.EnumerateArray())
					{
						Guid.TryParse(Str(b, "id"), out Guid id);
						ComicBook book = findBook(id, Str(b, "file"));
						if (book == null || string.IsNullOrEmpty(book.FilePath))
						{
							continue;
						}
						string target = Str(b, "target");
						found.Add(new Item
						{
							Book = book,
							Title = Str(b, "title"),
							Target = string.IsNullOrEmpty(target) ? null : target,
							Complete = Flag(b, "complete"),
							AddToLibrary = Flag(b, "add"),
							Folder = Str(b, "folder"),
							Name = Str(b, "name"),
							FormatId = Number(b, "format"),
							Rar = Flag(b, "rar"),
							Overwrite = Flag(b, "overwrite"),
							TargetPath = Str(b, "path")
						});
					}
				}
			}
			catch (Exception)
			{
				//a damaged file: nothing waits
			}
			lock (sync)
			{
				if (worker != null || found.Count == 0)
				{
					return;
				}
				items.Clear();
				items.AddRange(found);
			}
			RaiseChanged(true);
		}

		private static string Str(JsonElement e, string name)
		{
			return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
		}

		private static bool Flag(JsonElement e, string name)
		{
			return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
		}

		private static int Number(JsonElement e, string name)
		{
			return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
		}

		/// <summary>Keeps the books not written yet (the one being written too) in the queue's file; with none, the file goes.</summary>
		private static void SaveWaiting()
		{
			try
			{
				List<Item> waiting = items.Where((Item i) => !i.Finished).ToList();
				if (waiting.Count == 0)
				{
					if (File.Exists(QueueFile))
					{
						File.Delete(QueueFile);
					}
					return;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(QueueFile));
				using (FileStream fs = File.Create(QueueFile))
				using (Utf8JsonWriter w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
				{
					w.WriteStartObject();
					w.WriteStartArray("books");
					foreach (Item item in waiting)
					{
						w.WriteStartObject();
						w.WriteString("id", item.Book.Id.ToString());
						w.WriteString("file", item.Book.FilePath ?? "");
						w.WriteString("title", item.Title ?? "");
						w.WriteString("target", item.Target ?? "");
						w.WriteBoolean("complete", item.Complete);
						w.WriteBoolean("add", item.AddToLibrary);
						w.WriteString("folder", item.Folder ?? "");
						w.WriteString("name", item.Name ?? "");
						w.WriteNumber("format", item.FormatId);
						w.WriteBoolean("rar", item.Rar);
						w.WriteBoolean("overwrite", item.Overwrite);
						w.WriteString("path", item.TargetPath ?? "");
						w.WriteEndObject();
					}
					w.WriteEndArray();
					w.WriteEndObject();
				}
			}
			catch (Exception)
			{
				//best effort: an export that cannot be kept is only lost when ComicRack closes during it
			}
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
					try
					{
						if (!TranslationServices.HelperUp)
						{
							throw new InvalidOperationException("The translator is not running.");
						}
						item.Export = item.CreateExport();
						item.Export.Progress += delegate (int done, int total)
						{
							item.Done = done;
							item.Total = total;
							RaiseChanged(false);
						};
						if (item.SkipAsked)
						{
							throw new OperationCanceledException();
						}
						item.Written = item.Export.Run();
						TranslationStore.Shared.RecordExport(TranslationStore.BookKeyOf(item.Book), item.Target, item.Written);
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
					lock (sync)
					{
						SaveWaiting();
					}
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
