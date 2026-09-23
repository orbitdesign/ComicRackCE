using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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

			//How much the page was shrunk before being sent to the graphics card. 1 is full size,
			//2 is half in each direction, and so on. Tile bounds are in these reduced pixels.
			public int Reduction = 1;

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
		/// <param name="reduction">
		/// How much detail the caller needs: 1 for full size, 2 for half, and so on. A page kept at
		/// less detail than this is uploaded again.
		/// </param>
		public Entry Get(D2D.RenderTarget target, RendererImage image, BitmapAdjustment adjustment, int maxTileSize, int reduction = 1)
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
				if (node.Value.Reduction <= reduction)
				{
					lru.Remove(node);
					lru.AddFirst(node);
					return node.Value;
				}
				//Zoomed in since it was uploaded: this copy is too coarse now.
				Remove(node);
			}
			Entry entry = Upload(target, image, adjustment, maxTileSize, reduction);
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

		private Entry Upload(D2D.RenderTarget target, RendererImage image, BitmapAdjustment adjustment, int maxTileSize, int reduction)
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
						Entry entry = TryUpload(target, source, maxTileSize, reduction);
						if (entry == null)
						{
							//Most likely out of video memory. Make room and try once more.
							Clear();
							entry = TryUpload(target, source, maxTileSize, reduction);
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

		private static Entry TryUpload(D2D.RenderTarget target, Bitmap source, int maxTileSize, int reduction)
		{
			Bitmap converted = null;
			IntPtr reduced = IntPtr.Zero;
			try
			{
				int fullWidth = source.Width;
				int fullHeight = source.Height;
				if (fullWidth <= 0 || fullHeight <= 0)
				{
					return null;
				}
				reduction = Math.Max(1, Math.Min(reduction, Math.Min(fullWidth, fullHeight)));
				Rectangle all = new Rectangle(0, 0, fullWidth, fullHeight);
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
				int width = fullWidth;
				int height = fullHeight;
				IntPtr pixels = data.Scan0;
				int stride = data.Stride;
				Entry entry = new Entry
				{
					Reduction = reduction
				};
				try
				{
					if (reduction > 1)
					{
						//Shrinking here rather than sending the whole scan to the graphics card
						//is the difference between a few megabytes and a few hundred for one page.
						width = Math.Max(1, fullWidth / reduction);
						height = Math.Max(1, fullHeight / reduction);
						int reducedStride = width * 4;
						reduced = Marshal.AllocHGlobal(reducedStride * height);
						Shrink(pixels, stride, fullWidth, fullHeight, reduced, reducedStride, width, height, reduction);
						pixels = reduced;
						stride = reducedStride;
					}
					entry.Size = new Size(width, height);
					D2D.BitmapProperties properties = new D2D.BitmapProperties(new D2D.PixelFormat(DXGI.Format.B8G8R8A8_UNorm, D2D.AlphaMode.Premultiplied));
					for (int y = 0; y < height; y += maxTileSize)
					{
						for (int x = 0; x < width; x += maxTileSize)
						{
							int w = Math.Min(maxTileSize, width - x);
							int h = Math.Min(maxTileSize, height - y);
							IntPtr start = new IntPtr(pixels.ToInt64() + (long)y * stride + (long)x * 4);
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
				if (reduced != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(reduced);
				}
				converted?.Dispose();
			}
		}

		/// <summary>
		/// Averages blocks of pixels down by a whole number factor. Premultiplied colours average
		/// correctly, so this needs no other handling.
		/// </summary>
		private static unsafe void Shrink(IntPtr source, int sourceStride, int sourceWidth, int sourceHeight, IntPtr destination, int destinationStride, int width, int height, int factor)
		{
			byte* src = (byte*)source.ToPointer();
			byte* dst = (byte*)destination.ToPointer();
			int count = factor * factor;
			//Rows are independent, and there are a lot of them on a large scan.
			System.Threading.Tasks.Parallel.For(0, height, delegate(int y)
			{
				byte* outRow = dst + (long)y * destinationStride;
				int sy0 = y * factor;
				int sy1 = Math.Min(sy0 + factor, sourceHeight);
				for (int x = 0; x < width; x++)
				{
					int sx0 = x * factor;
					int sx1 = Math.Min(sx0 + factor, sourceWidth);
					int b = 0;
					int g = 0;
					int r = 0;
					int a = 0;
					for (int sy = sy0; sy < sy1; sy++)
					{
						byte* inRow = src + (long)sy * sourceStride + (long)sx0 * 4;
						for (int sx = sx0; sx < sx1; sx++)
						{
							b += inRow[0];
							g += inRow[1];
							r += inRow[2];
							a += inRow[3];
							inRow += 4;
						}
					}
					int taken = (sy1 - sy0) * (sx1 - sx0);
					if (taken <= 0)
					{
						taken = count;
					}
					outRow[0] = (byte)(b / taken);
					outRow[1] = (byte)(g / taken);
					outRow[2] = (byte)(r / taken);
					outRow[3] = (byte)(a / taken);
					outRow += 4;
				}
			});
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
