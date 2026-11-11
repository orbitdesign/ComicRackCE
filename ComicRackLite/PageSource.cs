using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using cYo.Common.Compression.SevenZip;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace ComicRackLite
{
	/// <summary>The pages of a book (an archive, or a folder of pictures), in ComicRack's order. Read is thread safe.</summary>
	internal abstract class PageSource : IDisposable
	{
		private static readonly string[] imageExtensions = { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".avif", ".heic", ".heif", ".jxl", ".jp2", ".j2k", ".jpx" };

		public static readonly string[] BookExtensions = { ".cbz", ".cbr", ".cb7", ".cbt", ".zip", ".rar", ".7z", ".tar" };

		private readonly object cacheLock = new object();

		private readonly LinkedList<KeyValuePair<int, byte[]>> recent = new LinkedList<KeyValuePair<int, byte[]>>();

		private readonly Dictionary<int, Size> sizes = new Dictionary<int, Size>();

		public string Path { get; protected set; }

		/// <summary>The page to start on when the book is a picture opened from Explorer (its folder is the book).</summary>
		public int StartPage { get; protected set; }

		public List<string> Names { get; } = new List<string>();

		public int Count => Names.Count;

		public static bool IsImageName(string name)
		{
			string ext = System.IO.Path.GetExtension(name ?? "");
			return imageExtensions.Any((string e) => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
		}

		public static bool IsBookName(string name)
		{
			string ext = System.IO.Path.GetExtension(name ?? "");
			return BookExtensions.Any((string e) => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
		}

		public static PageSource Open(string path)
		{
			if (Directory.Exists(path))
			{
				return new FolderSource(path, null);
			}
			if (IsImageName(path))
			{
				return new FolderSource(System.IO.Path.GetDirectoryName(path), path);
			}
			//RAR and 7z through 7-Zip (as ComicRack and CDisplayEx), the rest through SharpCompress; the file's
			//signature decides, so a .cbr that is really a zip still opens
			KnownSevenZipFormat? format = SevenZipSource.FormatOf(path);
			if (format.HasValue && SevenZipSource.Available)
			{
				try
				{
					return new SevenZipSource(path, format.Value);
				}
				catch (Exception)
				{
					//7-Zip could not read it: SharpCompress may
				}
			}
			return new ArchiveSource(path);
		}

		/// <summary>The page's file (the last few are kept, so pairing pages and drawing them read each only once).</summary>
		public byte[] Read(int index)
		{
			lock (cacheLock)
			{
				for (LinkedListNode<KeyValuePair<int, byte[]>> n = recent.First; n != null; n = n.Next)
				{
					if (n.Value.Key == index)
					{
						recent.Remove(n);
						recent.AddFirst(n);
						return n.Value.Value;
					}
				}
			}
			byte[] data = ReadPage(index);
			lock (cacheLock)
			{
				recent.AddFirst(new KeyValuePair<int, byte[]>(index, data));
				while (recent.Count > 8)
				{
					recent.RemoveLast();
				}
			}
			return data;
		}

		/// <summary>The page's size in pixels (Size.Empty when unknown).</summary>
		public Size GetSize(int index)
		{
			lock (cacheLock)
			{
				if (sizes.TryGetValue(index, out Size known))
				{
					return known;
				}
			}
			Size size;
			try
			{
				size = ImageDecoder.GetSize(Read(index));
			}
			catch (Exception)
			{
				size = Size.Empty;
			}
			SetSize(index, size);
			return size;
		}

		public void SetSize(int index, Size size)
		{
			lock (cacheLock)
			{
				sizes[index] = size;
			}
		}

		protected abstract byte[] ReadPage(int index);

		public virtual void Dispose()
		{
		}

		protected static bool Ignored(string name)
		{
			string n = name.Replace('\\', '/');
			return n.Contains("__MACOSX/") || n.Contains(".DS_Store/") || System.IO.Path.GetFileName(n).StartsWith("._");
		}
	}

	internal sealed class FolderSource : PageSource
	{
		private readonly List<string> files;

		public FolderSource(string folder, string picture)
		{
			Path = folder;
			files = Directory.EnumerateFiles(folder).Where((string f) => IsImageName(f) && !Ignored(f)).ToList();
			files.Sort((string a, string b) => NaturalCompare.Compare(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));
			Names.AddRange(files.Select(System.IO.Path.GetFileName));
			if (picture != null)
			{
				StartPage = Math.Max(0, files.FindIndex((string f) => string.Equals(f, picture, StringComparison.OrdinalIgnoreCase)));
			}
		}

		protected override byte[] ReadPage(int index)
		{
			return File.ReadAllBytes(files[index]);
		}
	}

	/// <summary>
	/// A zip, rar, 7z or tar book (through SharpCompress, as ComicRack). Pages are read when needed; a solid archive (and
	/// 7z) can only be read from its start, so its pages are unpacked once, in order, in the background, and a page asked
	/// for waits until it is there.
	/// </summary>
	internal sealed class ArchiveSource : PageSource
	{
		private readonly IArchive archive;

		private readonly List<IArchiveEntry> entries;

		private readonly object archiveLock = new object();

		private readonly bool sequential;

		private readonly byte[][] unpacked;

		private bool unpackDone;

		private Exception unpackError;

		private volatile bool disposed;

		public ArchiveSource(string path)
		{
			Path = path;
			archive = ArchiveFactory.OpenArchive(path, new ReaderOptions());
			try
			{
				if (archive.IsEncrypted)
				{
					throw new InvalidDataException("This book is protected with a password.");
				}
				entries = archive.Entries.Where((IArchiveEntry e) => !e.IsDirectory && e.Key != null && IsImageName(e.Key) && !Ignored(e.Key)).ToList();
				entries.Sort((IArchiveEntry a, IArchiveEntry b) => NaturalCompare.Compare(a.Key, b.Key));
				Names.AddRange(entries.Select((IArchiveEntry e) => e.Key));
				sequential = archive.IsSolid || archive.Type == ArchiveType.SevenZip;
				if (sequential)
				{
					unpacked = new byte[entries.Count][];
					Thread thread = new Thread(Unpack) { IsBackground = true, Name = "Unpack", Priority = ThreadPriority.BelowNormal };
					thread.Start();
				}
			}
			catch
			{
				archive.Dispose();
				throw;
			}
		}

		private void Unpack()
		{
			Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);
			for (int i = 0; i < entries.Count; i++)
			{
				index[entries[i].Key] = i;
			}
			try
			{
				lock (archiveLock)
				{
					using (IReader reader = archive.ExtractAllEntries())
					{
						while (!disposed && reader.MoveToNextEntry())
						{
							if (reader.Entry.IsDirectory || reader.Entry.Key == null || !index.TryGetValue(reader.Entry.Key, out int i))
							{
								continue;
							}
							byte[] data;
							using (Stream s = reader.OpenEntryStream())
							{
								data = ReadAll(s, reader.Entry.Size);
							}
							lock (unpacked)
							{
								unpacked[i] = data;
								Monitor.PulseAll(unpacked);
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				unpackError = ex;
			}
			lock (unpacked)
			{
				unpackDone = true;
				Monitor.PulseAll(unpacked);
			}
		}

		protected override byte[] ReadPage(int index)
		{
			if (sequential)
			{
				lock (unpacked)
				{
					while (unpacked[index] == null && !unpackDone && !disposed)
					{
						Monitor.Wait(unpacked, 200);
					}
					if (unpacked[index] == null)
					{
						throw new InvalidDataException("This page could not be unpacked" + (unpackError != null ? ": " + unpackError.Message : "."));
					}
					return unpacked[index];
				}
			}
			lock (archiveLock)
			{
				if (disposed)
				{
					throw new ObjectDisposedException("book");
				}
				using (Stream s = entries[index].OpenEntryStream())
				{
					return ReadAll(s, entries[index].Size);
				}
			}
		}

		private static byte[] ReadAll(Stream s, long size)
		{
			using (MemoryStream ms = new MemoryStream(size > 0 && size < int.MaxValue ? (int)size : 1 << 20))
			{
				s.CopyTo(ms, 81920);
				return ms.Length == ms.Capacity ? ms.GetBuffer() : ms.ToArray();
			}
		}

		public override void Dispose()
		{
			disposed = true;
			if (sequential)
			{
				lock (unpacked)
				{
					Monitor.PulseAll(unpacked);
				}
			}
			//the unpacking thread holds the archive while it runs: it stops at the next page and the archive goes then
			ThreadPool.QueueUserWorkItem(delegate
			{
				lock (archiveLock)
				{
					archive.Dispose();
				}
			});
		}
	}
}
