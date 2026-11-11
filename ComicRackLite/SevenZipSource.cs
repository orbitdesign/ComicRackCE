using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using cYo.Common.Compression.SevenZip;

namespace ComicRackLite
{
	/// <summary>
	/// A RAR or 7z book read with 7-Zip (Resources\x64\7z.dll, through ComicRack's own wrapper in cYo.Common), as ComicRack
	/// and CDisplayEx read them: the list of pages comes straight from the archive's headers. 7-Zip is used from one thread
	/// of its own. Pages are unpacked when asked for; in a solid archive (one stream for all files) they are all unpacked
	/// once, in order, in the background, and a page asked for waits until it is there.
	/// </summary>
	internal sealed class SevenZipSource : PageSource
	{
		private sealed class UnpackCallback : IProgress, IArchiveExtractCallback
		{
			private readonly SevenZipSource owner;

			private int page = -1;

			private MemoryStream stream;

			private OutStreamWrapper wrapper;

			public UnpackCallback(SevenZipSource owner)
			{
				this.owner = owner;
			}

			public void SetTotal(long total)
			{
			}

			public void SetCompleted(ref long completeValue)
			{
			}

			public int GetStream(int index, out ISequentialOutStream outStream, AskMode askExtractMode)
			{
				outStream = null;
				if (owner.disposed)
				{
					return unchecked((int)0x80004004); //E_ABORT: stop unpacking
				}
				if (askExtractMode != AskMode.kExtract || !owner.pageOfItem.TryGetValue(index, out page))
				{
					page = -1;
					return 0;
				}
				stream = new MemoryStream();
				wrapper = new OutStreamWrapper(stream);
				outStream = wrapper;
				return 0;
			}

			public void PrepareOperation(AskMode askExtractMode)
			{
			}

			public void SetOperationResult(OperationResult result)
			{
				if (page < 0 || stream == null)
				{
					return;
				}
				byte[] data = stream.ToArray();
				wrapper.Dispose();
				stream = null;
				owner.Unpacked(page, result == OperationResult.kOK ? data : null, result);
				page = -1;
			}
		}

		private static readonly object factoryLock = new object();

		private static SevenZipFactory factory;

		private readonly BlockingCollection<Action> work = new BlockingCollection<Action>();

		private readonly Dictionary<int, int> pageOfItem = new Dictionary<int, int>();

		private readonly List<int> itemOfPage = new List<int>();

		private IInArchive archive;

		private InStreamWrapper archiveStream;

		private readonly bool solid;

		private byte[][] unpacked;

		private string[] unpackErrors;

		private bool unpackDone;

		private volatile bool disposed;

		private static string LibraryPath => System.IO.Path.Combine(Program.AppFolder, "Resources", Environment.Is64BitProcess ? "x64" : "x86", "7z.dll");

		public static bool Available => File.Exists(LibraryPath);

		/// <summary>RAR (4 or 5) or 7z by the file's signature, or null.</summary>
		public static KnownSevenZipFormat? FormatOf(string path)
		{
			try
			{
				byte[] head = new byte[8];
				using (FileStream f = File.OpenRead(path))
				{
					if (f.Read(head, 0, 8) < 7)
					{
						return null;
					}
				}
				if (head[0] == 'R' && head[1] == 'a' && head[2] == 'r' && head[3] == '!' && head[4] == 0x1A && head[5] == 0x07)
				{
					return head[6] == 0x01 ? KnownSevenZipFormat.Rar5 : KnownSevenZipFormat.Rar;
				}
				if (head[0] == '7' && head[1] == 'z' && head[2] == 0xBC && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C)
				{
					return KnownSevenZipFormat.SevenZip;
				}
			}
			catch (Exception)
			{
			}
			return null;
		}

		public SevenZipSource(string path, KnownSevenZipFormat format)
		{
			Path = path;
			Thread thread = new Thread(Work) { IsBackground = true, Name = "7-Zip" };
			thread.SetApartmentState(ApartmentState.MTA);
			thread.Start();
			try
			{
				solid = Invoke(delegate
				{
					return Open(path, format);
				});
			}
			catch
			{
				Dispose();
				throw;
			}
			if (solid)
			{
				unpacked = new byte[Count][];
				unpackErrors = new string[Count];
				work.Add(UnpackAll);
			}
		}

		private void Work()
		{
			try
			{
				foreach (Action a in work.GetConsumingEnumerable())
				{
					a();
				}
			}
			catch (Exception)
			{
			}
			Close();
		}

		private T Invoke<T>(Func<T> f)
		{
			T result = default(T);
			Exception error = null;
			using (ManualResetEventSlim done = new ManualResetEventSlim())
			{
				work.Add(delegate
				{
					try
					{
						result = f();
					}
					catch (Exception ex)
					{
						error = ex;
					}
					finally
					{
						done.Set();
					}
				});
				done.Wait();
			}
			if (error != null)
			{
				throw new InvalidDataException(error.Message, error);
			}
			return result;
		}

		/// <summary>Opens the archive and lists its pictures (on the 7-Zip thread). True when it is solid.</summary>
		private bool Open(string path, KnownSevenZipFormat format)
		{
			lock (factoryLock)
			{
				if (factory == null)
				{
					factory = new SevenZipFactory(LibraryPath);
				}
				archive = factory.CreateInArchive(format);
			}
			if (archive == null)
			{
				throw new InvalidDataException("7-Zip cannot read this kind of archive.");
			}
			archiveStream = new InStreamWrapper(File.OpenRead(path));
			long maxCheck = 1 << 20;
			if (archive.Open(archiveStream, ref maxCheck, new StubOpenCallback()) != 0)
			{
				throw new InvalidDataException("This book is damaged, or protected with a password.");
			}
			int count = archive.GetNumberOfItems();
			List<KeyValuePair<string, int>> pictures = new List<KeyValuePair<string, int>>();
			for (int i = 0; i < count; i++)
			{
				PropVariant name = default(PropVariant);
				PropVariant folder = default(PropVariant);
				archive.GetProperty(i, ItemPropId.kpidPath, ref name);
				archive.GetProperty(i, ItemPropId.kpidIsFolder, ref folder);
				string key = name.GetObject() as string;
				bool isFolder = folder.VarType == VarEnum.VT_BOOL && folder.shortValue != 0;
				if (!isFolder && key != null && IsImageName(key) && !Ignored(key))
				{
					pictures.Add(new KeyValuePair<string, int>(key, i));
				}
			}
			pictures.Sort((KeyValuePair<string, int> a, KeyValuePair<string, int> b) => NaturalCompare.Compare(a.Key, b.Key));
			foreach (KeyValuePair<string, int> p in pictures)
			{
				pageOfItem[p.Value] = Names.Count;
				itemOfPage.Add(p.Value);
				Names.Add(p.Key);
			}
			PropVariant solidValue = default(PropVariant);
			try
			{
				archive.GetArchiveProperty((int)ItemPropId.kpidSolid, ref solidValue);
			}
			catch (Exception)
			{
			}
			return solidValue.VarType == VarEnum.VT_BOOL && solidValue.shortValue != 0;
		}

		private void UnpackAll()
		{
			try
			{
				int[] items = itemOfPage.OrderBy((int i) => i).ToArray();
				if (items.Length > 0 && !disposed)
				{
					archive.Extract(items, items.Length, 0, new UnpackCallback(this));
				}
			}
			catch (Exception)
			{
			}
			lock (unpacked)
			{
				unpackDone = true;
				Monitor.PulseAll(unpacked);
			}
		}

		private void Unpacked(int page, byte[] data, OperationResult result)
		{
			lock (unpacked)
			{
				unpacked[page] = data;
				if (data == null)
				{
					unpackErrors[page] = result.ToString();
				}
				Monitor.PulseAll(unpacked);
			}
		}

		protected override byte[] ReadPage(int index)
		{
			if (solid)
			{
				lock (unpacked)
				{
					while (unpacked[index] == null && unpackErrors[index] == null && !unpackDone && !disposed)
					{
						Monitor.Wait(unpacked, 200);
					}
					if (unpacked[index] == null)
					{
						throw new InvalidDataException("This page could not be unpacked" + (unpackErrors[index] != null ? " (" + unpackErrors[index] + ")." : "."));
					}
					return unpacked[index];
				}
			}
			return Invoke(delegate
			{
				int item = itemOfPage[index];
				using (MemoryStream ms = new MemoryStream())
				{
					using (OutStreamWrapper wrapper = new OutStreamWrapper(new NonClosingStream(ms)))
					{
						int hr = archive.Extract(new[] { item }, 1, 0, new SingleCallback(item, wrapper));
						if (hr != 0)
						{
							throw new InvalidDataException("This page could not be unpacked.");
						}
					}
					if (ms.Length == 0)
					{
						throw new InvalidDataException("This page could not be unpacked.");
					}
					return ms.ToArray();
				}
			});
		}

		private sealed class SingleCallback : IProgress, IArchiveExtractCallback
		{
			private readonly int item;

			private readonly OutStreamWrapper target;

			public SingleCallback(int item, OutStreamWrapper target)
			{
				this.item = item;
				this.target = target;
			}

			public void SetTotal(long total)
			{
			}

			public void SetCompleted(ref long completeValue)
			{
			}

			public int GetStream(int index, out ISequentialOutStream outStream, AskMode askExtractMode)
			{
				outStream = index == item && askExtractMode == AskMode.kExtract ? target : null;
				return 0;
			}

			public void PrepareOperation(AskMode askExtractMode)
			{
			}

			public void SetOperationResult(OperationResult resultEOperationResult)
			{
			}
		}

		/// <summary>Keeps the page's memory open when 7-Zip's stream wrapper is closed.</summary>
		private sealed class NonClosingStream : Stream
		{
			private readonly Stream inner;

			public NonClosingStream(Stream inner)
			{
				this.inner = inner;
			}

			public override bool CanRead => false;

			public override bool CanSeek => inner.CanSeek;

			public override bool CanWrite => true;

			public override long Length => inner.Length;

			public override long Position
			{
				get => inner.Position;
				set => inner.Position = value;
			}

			public override void Flush()
			{
				inner.Flush();
			}

			public override int Read(byte[] buffer, int offset, int count)
			{
				throw new NotSupportedException();
			}

			public override long Seek(long offset, SeekOrigin origin)
			{
				return inner.Seek(offset, origin);
			}

			public override void SetLength(long value)
			{
				inner.SetLength(value);
			}

			public override void Write(byte[] buffer, int offset, int count)
			{
				inner.Write(buffer, offset, count);
			}
		}

		private void Close()
		{
			try
			{
				if (archive != null)
				{
					archive.Close();
					Marshal.ReleaseComObject(archive);
					archive = null;
				}
			}
			catch (Exception)
			{
			}
			archiveStream?.Dispose();
			archiveStream = null;
		}

		public override void Dispose()
		{
			if (disposed)
			{
				return;
			}
			disposed = true;
			if (unpacked != null)
			{
				lock (unpacked)
				{
					Monitor.PulseAll(unpacked);
				}
			}
			try
			{
				work.CompleteAdding();
			}
			catch (Exception)
			{
			}
		}
	}
}
