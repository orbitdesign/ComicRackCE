using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using cYo.Common.Drawing;
using cYo.Common.Threading;
using D2D = SharpDX.Direct2D1;
using DXGI = SharpDX.DXGI;

namespace cYo.Common.Presentation.Direct2D
{
	/// <summary>
	/// Keeps GPU copies of the page and overlay bitmaps drawn by <see cref="ControlDirect2DRenderer"/>.
	///
	/// Entries are keyed the same way the OpenGL TextureManager keyed its textures: by
	/// <see cref="RendererImage"/> equality. That matters, because page images are wrapped in
	/// RendererMemoryOptimizedImage, whose Bitmap property may hand out a different Bitmap object
	/// on every access. Keying on the Bitmap reference would re-upload every page on every frame.
	///
	/// The cache never keeps an image alive. It only holds the RendererImage wrapper, which itself
	/// only holds weak references, and entries whose image has gone are swept at the start of each
	/// frame.
	/// </summary>
	internal sealed class Direct2DBitmapCache : IDisposable
	{
		public sealed class Tile
		{
			public Rectangle Bounds;

			public D2D.Bitmap Bitmap;
		}

		public sealed class Entry
		{
			public CacheKey Key;

			public readonly List<Tile> Tiles = new List<Tile>();

			public Size Size;

			public long Bytes;
		}

		public sealed class CacheKey
		{
			private readonly int hash;

			public readonly RendererImage Image;

			public readonly BitmapAdjustment Adjustment;

			public CacheKey(RendererImage image, BitmapAdjustment adjustment, int hash)
			{
				Image = image;
				Adjustment = adjustment;
				this.hash = hash;
			}

			public override int GetHashCode()
			{
				//Precomputed: RendererGdiImage.GetHashCode throws once its bitmap has been collected,
				//and a dictionary recomputes the hash of a key when it is removed.
				return hash;
			}

			public override bool Equals(object obj)
			{
				if (ReferenceEquals(this, obj))
				{
					return true;
				}
				CacheKey other = obj as CacheKey;
				if (other == null || other.hash != hash)
				{
					return false;
				}
				try
				{
					return other.Adjustment.Equals(Adjustment) && other.Image.Equals(Image);
				}
				catch
				{
					return false;
				}
			}
		}

		private readonly Dictionary<CacheKey, LinkedListNode<Entry>> lookup = new Dictionary<CacheKey, LinkedListNode<Entry>>();

		private readonly LinkedList<Entry> lru = new LinkedList<Entry>();

		private long totalBytes;

		public long MaxBytes
		{
			get;
			set;
		}

		public int MaxCount
		{
			get;
			set;
		}

		public long TotalBytes => totalBytes;

		public int Count => lru.Count;

		public Direct2DBitmapCache(long maxBytes, int maxCount)
		{
			MaxBytes = Math.Max(maxBytes, 16L * 1024 * 1024);
			MaxCount = Math.Max(maxCount, 16);
		}

		/// <summary>
		/// Returns the GPU copy of an image, uploading it on first use. Returns null when the image
		/// is gone or cannot be uploaded, in which case the caller simply skips drawing it.
		/// </summary>
		public Entry Get(D2D.RenderTarget target, RendererImage image, BitmapAdjustment adjustment, int maxTileSize)
		{
			if (image == null)
			{
				return null;
			}
			CacheKey key;
			try
			{
				if (!image.IsValid)
				{
					return null;
				}
				key = new CacheKey(image, adjustment, image.GetHashCode() ^ adjustment.GetHashCode());
			}
			catch
			{
				return null;
			}
			if (lookup.TryGetValue(key, out LinkedListNode<Entry> node))
			{
				lru.Remove(node);
				lru.AddFirst(node);
				return node.Value;
			}
			Entry entry = Upload(target, image, adjustment, maxTileSize);
			if (entry == null)
			{
				return null;
			}
			entry.Key = key;
			lookup[key] = lru.AddFirst(entry);
			totalBytes += entry.Bytes;
			Trim(entry);
			return entry;
		}

		/// <summary>
		/// Drops entries whose images have been disposed or collected.
		/// </summary>
		public void Sweep()
		{
			LinkedListNode<Entry> node = lru.Last;
			while (node != null)
			{
				LinkedListNode<Entry> previous = node.Previous;
				bool valid;
				try
				{
					valid = node.Value.Key.Image.IsValid;
				}
				catch
				{
					valid = false;
				}
				if (!valid)
				{
					Remove(node);
				}
				node = previous;
			}
		}

		public void Clear()
		{
			while (lru.Last != null)
			{
				Remove(lru.Last);
			}
		}

		public void Dispose()
		{
			Clear();
		}

		private void Trim(Entry keep)
		{
			while (lru.Last != null && (totalBytes > MaxBytes || lru.Count > MaxCount))
			{
				if (lru.Last.Value == keep)
				{
					//Never evict what the caller is about to draw, even if it alone is over budget.
					break;
				}
				Remove(lru.Last);
			}
		}

		private void Remove(LinkedListNode<Entry> node)
		{
			Entry entry = node.Value;
			lru.Remove(node);
			if (entry.Key != null)
			{
				lookup.Remove(entry.Key);
			}
			totalBytes -= entry.Bytes;
			foreach (Tile tile in entry.Tiles)
			{
				SafeDispose(tile.Bitmap);
			}
			entry.Tiles.Clear();
		}

		private Entry Upload(D2D.RenderTarget target, RendererImage image, BitmapAdjustment adjustment, int maxTileSize)
		{
			Bitmap bitmap;
			try
			{
				bitmap = image.Bitmap;
			}
			catch
			{
				return null;
			}
			if (bitmap == null)
			{
				return null;
			}
			using (ItemMonitor.Lock(image))
			{
				using (ItemMonitor.Lock(bitmap))
				{
					Bitmap adjusted = null;
					try
					{
						Bitmap source = bitmap;
						if (!adjustment.IsEmpty)
						{
							adjusted = bitmap.CreateAdjustedBitmap(adjustment, PixelFormat.Format32bppArgb, alwaysClone: true);
							source = adjusted;
						}
						Entry entry = TryUpload(target, source, maxTileSize);
						if (entry == null)
						{
							//Most likely out of video memory. Make room and try once more.
							Clear();
							entry = TryUpload(target, source, maxTileSize);
						}
						return entry;
					}
					catch
					{
						//Disposed while we were looking at it, locked elsewhere, or a format GDI+
						//can not convert. Skipping one draw is better than taking the reader down.
						return null;
					}
					finally
					{
						adjusted?.Dispose();
					}
				}
			}
		}

		private static Entry TryUpload(D2D.RenderTarget target, Bitmap source, int maxTileSize)
		{
			Bitmap converted = null;
			try
			{
				int width = source.Width;
				int height = source.Height;
				if (width <= 0 || height <= 0)
				{
					return null;
				}
				Rectangle all = new Rectangle(0, 0, width, height);
				BitmapData data;
				try
				{
					//Premultiplied BGRA is exactly what Direct2D wants, and GDI+ converts to it
					//while locking, so no manual pixel loops are needed.
					data = source.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
				}
				catch (ArgumentException)
				{
					//Some formats (a few indexed or 16bit ones) can not be converted by LockBits.
					converted = source.CreateCopy(PixelFormat.Format32bppArgb);
					source = converted;
					data = source.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
				}
				Entry entry = new Entry
				{
					Size = new Size(width, height)
				};
				try
				{
					D2D.BitmapProperties properties = new D2D.BitmapProperties(new D2D.PixelFormat(DXGI.Format.B8G8R8A8_UNorm, D2D.AlphaMode.Premultiplied));
					int stride = data.Stride;
					for (int y = 0; y < height; y += maxTileSize)
					{
						for (int x = 0; x < width; x += maxTileSize)
						{
							int w = Math.Min(maxTileSize, width - x);
							int h = Math.Min(maxTileSize, height - y);
							IntPtr start = new IntPtr(data.Scan0.ToInt64() + (long)y * stride + (long)x * 4);
							int length = (h - 1) * stride + w * 4;
							D2D.Bitmap tileBitmap = new D2D.Bitmap(target, new SharpDX.Size2(w, h), new SharpDX.DataPointer(start, length), stride, properties);
							entry.Tiles.Add(new Tile
							{
								Bounds = new Rectangle(x, y, w, h),
								Bitmap = tileBitmap
							});
							entry.Bytes += (long)w * h * 4;
						}
					}
				}
				catch (SharpDX.SharpDXException)
				{
					foreach (Tile tile in entry.Tiles)
					{
						SafeDispose(tile.Bitmap);
					}
					return null;
				}
				finally
				{
					source.UnlockBits(data);
				}
				return entry;
			}
			finally
			{
				converted?.Dispose();
			}
		}

		private static void SafeDispose(IDisposable disposable)
		{
			try
			{
				disposable?.Dispose();
			}
			catch
			{
			}
		}
	}
}
