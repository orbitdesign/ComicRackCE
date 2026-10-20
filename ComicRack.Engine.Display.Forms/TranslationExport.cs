using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using cYo.Common;
using cYo.Common.Drawing;
using cYo.Common.IO;
using cYo.Projects.ComicRack.Engine.IO;
using cYo.Projects.ComicRack.Engine.IO.Cache;
using cYo.Projects.ComicRack.Engine.IO.Provider;
using cYo.Projects.ComicRack.Engine.IO.Provider.Readers;

namespace cYo.Projects.ComicRack.Engine.Display.Forms
{
	/// <summary>
	/// Writes a copy of a book in which every page is translated: the pages the helper has already
	/// done come straight from its stored copies, the others are translated on the way, and the
	/// changes the user saved with the book are applied. The file itself is written by ComicRack's
	/// own book writers (CBZ, CB7, CBT, PDF...), so the book's information and page list are kept.
	/// </summary>
	public class TranslationExport
	{
		private class TranslatedProvider : IImageProvider, IDisposable
		{
			public class Prepared
			{
				public byte[] Data;

				public string Name;
			}

			private readonly IImageProvider inner;

			private readonly TranslationExport owner;

			private readonly Dictionary<int, Prepared> ready = new Dictionary<int, Prepared>();

			//What is known about pages whose data has been handed over already.
			private readonly Dictionary<int, ProviderImageInfo> infos = new Dictionary<int, ProviderImageInfo>();

			private readonly object sync = new object();

			private readonly object serial = new object();

			public TranslatedProvider(IImageProvider inner, TranslationExport owner)
			{
				this.inner = inner;
				this.owner = owner;
			}

			public bool IsSlow => true;

			public string Source => inner.Source;

			public int Count => inner.Count;

			public ProviderImageInfo GetImageInfo(int index)
			{
				lock (sync)
				{
					if (infos.TryGetValue(index, out ProviderImageInfo known))
					{
						return known;
					}
				}
				Prepared page = Prepare(index);
				return new ProviderImageInfo(index, page.Name, page.Data.Length);
			}

			public byte[] GetByteImage(int index)
			{
				return Take(index);
			}

			public ExportImageContainer GetByteImageForExport(int index)
			{
				return new ExportImageContainer
				{
					Data = Take(index),
					NeedsToConvert = false
				};
			}

			public Bitmap GetImage(int index)
			{
				return BitmapExtensions.BitmapFromBytes(Take(index));
			}

			public ThumbnailImage GetThumbnail(int index)
			{
				return inner.GetThumbnail(index);
			}

			public void Dispose()
			{
				inner.Dispose();
			}

			private byte[] Take(int index)
			{
				Prepared page = Prepare(index);
				lock (sync)
				{
					ready.Remove(index);
					infos[index] = new ProviderImageInfo(index, page.Name, page.Data.Length);
				}
				return page.Data;
			}

			private Prepared Prepare(int index)
			{
				lock (sync)
				{
					if (ready.TryGetValue(index, out Prepared found))
					{
						return found;
					}
				}
				//One page at a time: the helper (and the model behind it) works on one page at a time anyway.
				lock (serial)
				{
					lock (sync)
					{
						if (ready.TryGetValue(index, out Prepared found))
						{
							return found;
						}
					}
					owner.CheckCancelled();
					Prepared page = owner.Translate(inner, index);
					lock (sync)
					{
						ready[index] = page;
					}
					owner.PageDone();
					return page;
				}
			}
		}

		private readonly ComicBook book;

		private readonly ExportSetting setting;

		private readonly IPagePool pagePool;

		private readonly TranslationApi api;

		private readonly TranslationStore store;

		private readonly string bookKey;

		private volatile bool cancelled;

		private int done;

		private int total;

		public TranslationExport(ComicBook book, ExportSetting setting, IPagePool pagePool, string serverUrl, TranslationStore store)
		{
			this.book = book;
			this.setting = setting;
			this.pagePool = pagePool;
			this.store = store;
			api = new TranslationApi(serverUrl);
			bookKey = TranslationStore.BookKeyOf(book);
			api.Book = bookKey;
		}

		/// <summary>Raised after each page with (pages done, pages in the book); on a background thread.</summary>
		public event Action<int, int> Progress;

		/// <summary>
		/// When set (the full path of WinRAR's Rar.exe), the book is packed as a .cbr: ComicRack cannot
		/// write RAR files itself, so the pages are written as a .cbz first and repacked.
		/// </summary>
		public string RarPath { get; set; }

		/// <summary>The file the export writes.</summary>
		public string TargetPath
		{
			get
			{
				string path = setting.GetTargetPath(book, 0);
				return string.IsNullOrEmpty(RarPath) ? path : Path.ChangeExtension(path, ".cbr");
			}
		}

		/// <summary>Where WinRAR's Rar.exe is installed, or null when it is not.</summary>
		public static string FindRar()
		{
			foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
			{
				string path = Path.Combine(Environment.GetFolderPath(folder), "WinRAR", "Rar.exe");
				if (File.Exists(path))
				{
					return path;
				}
			}
			return null;
		}

		public void Cancel()
		{
			cancelled = true;
		}

		/// <summary>Writes the translated copy and returns its path. Throws when it fails or is cancelled.</summary>
		public string Run()
		{
			string target = TargetPath;
			if (File.Exists(target) && !setting.Overwrite)
			{
				throw new InvalidOperationException("The file already exists: " + target);
			}
			FileFormat format = string.IsNullOrEmpty(RarPath) ? setting.GetFileFormat(book) : Providers.Writers.GetSourceFormats().First((FileFormat ff) => ff.Id == KnownFileFormats.CBZ);
			string temp = EngineConfiguration.Default.GetTempFileName();
			try
			{
				using (IImageProvider provider = new TranslatedProvider(CombinedComics.OpenProvider(new[] { book }, pagePool), this))
				using (StorageProvider writer = Providers.Writers.CreateFormatProvider(format.Name))
				{
					if (writer == null)
					{
						throw new InvalidOperationException("ComicRack cannot write " + format.Name + " files.");
					}
					total = provider.Count;
					writer.Progress += delegate(object sender, StorageProgressEventArgs e)
					{
						e.Cancel = cancelled;
					};
					writer.Store(provider, book.Clone<ComicBook>(), temp, setting);
				}
				CheckCancelled();
				if (!string.IsNullOrEmpty(RarPath))
				{
					Repack(temp, RarPath);
				}
				Directory.CreateDirectory(Path.GetDirectoryName(target));
				if (File.Exists(target))
				{
					File.Delete(target);
				}
				File.Move(temp, target);
				return target;
			}
			finally
			{
				FileUtility.SafeDelete(temp);
			}
		}

		//Unpacks the written .cbz and packs its files again into a RAR archive (stored, as the pages are JPEGs already).
		private void Repack(string file, string rar)
		{
			string folder = Path.Combine(Path.GetTempPath(), "ComicRackTranslation-" + Guid.NewGuid().ToString("N"));
			string packed = file + ".rar";
			try
			{
				System.IO.Compression.ZipFile.ExtractToDirectory(file, folder);
				System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo(rar, "a -r -m0 -ma4 -idq -ep1 \"" + packed + "\" *")
				{
					WorkingDirectory = folder,
					UseShellExecute = false,
					CreateNoWindow = true
				};
				using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(start))
				{
					process.WaitForExit();
					if (process.ExitCode != 0 || !File.Exists(packed))
					{
						throw new InvalidOperationException("Rar.exe could not pack the book (exit code " + process.ExitCode + ").");
					}
				}
				File.Delete(file);
				File.Move(packed, file);
			}
			finally
			{
				FileUtility.SafeDelete(packed);
				try
				{
					Directory.Delete(folder, true);
				}
				catch (Exception)
				{
				}
			}
		}

		private void CheckCancelled()
		{
			if (cancelled)
			{
				throw new OperationCanceledException("The export was cancelled.");
			}
		}

		private void PageDone()
		{
			Progress?.Invoke(Interlocked.Increment(ref done), total);
		}

		private TranslatedProvider.Prepared Translate(IImageProvider inner, int index)
		{
			ProviderImageInfo info = inner.GetImageInfo(index);
			string name = info?.Name ?? "page.jpg";
			byte[] data = inner.GetByteImage(index);
			if (!IsReadableFormat(data))
			{
				using (Bitmap bitmap = inner.GetImage(index))
				{
					data = bitmap.ImageToJpegBytes(95);
				}
			}
			PageAnalysis page = null;
			for (int attempt = 1; ; attempt++)
			{
				try
				{
					StyleFonts fonts = store.GetFonts(bookKey, index);
					page = api.Analyze(data, fonts);
					List<TranslationBox> edits = store.GetEdits(bookKey, index);
					List<ProtectStroke> protect = store.GetProtect(bookKey, index);
					if ((edits.Count > 0 && TranslationStore.Merge(page.Boxes, edits)) || protect.Count > 0)
					{
						page = api.Render(data, page.Boxes, protect, fonts);
					}
					break;
				}
				catch (TranslationApiException) when (attempt < 3 && !cancelled)
				{
					Thread.Sleep(2000 * attempt);
				}
			}
			store.RecordPage(bookKey, index, page.Sha);
			if (!page.Changed)
			{
				return new TranslatedProvider.Prepared { Data = data, Name = name };
			}
			return new TranslatedProvider.Prepared { Data = page.Image, Name = Path.ChangeExtension(name, ".jpg") };
		}

		private static bool IsReadableFormat(byte[] d)
		{
			if (d == null || d.Length < 12)
			{
				return false;
			}
			return (d[0] == 0xFF && d[1] == 0xD8) || (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) || (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') || (d[0] == 'B' && d[1] == 'M') || (d[0] == 'G' && d[1] == 'I' && d[2] == 'F');
		}
	}
}
