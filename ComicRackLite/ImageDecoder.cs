using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ComicRackLite
{
	/// <summary>
	/// Turns a page file into a bitmap: GDI+ for JPEG, PNG, GIF, BMP and TIFF, ComicRack's libwebp for WebP, and Windows'
	/// own codecs (WIC) for anything else Windows can read (AVIF, HEIC, JPEG XL... when their extension is installed).
	/// </summary>
	internal static class ImageDecoder
	{
		private static class NativeMethods
		{
			[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
			public static extern IntPtr LoadLibraryEx(string path, IntPtr file, int flags);

			//look for the DLL's own dependencies (libsharpyuv.dll) in its folder
			public const int LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;

			[DllImport("libwebp.dll", CallingConvention = CallingConvention.Cdecl)]
			public static extern int WebPGetInfo(IntPtr data, UIntPtr dataSize, out int width, out int height);

			[DllImport("libwebp.dll", CallingConvention = CallingConvention.Cdecl)]
			public static extern IntPtr WebPDecodeBGRAInto(IntPtr data, UIntPtr dataSize, IntPtr output, UIntPtr outputSize, int outputStride);
		}

		private static readonly object webpLock = new object();

		private static bool webpLoaded;

		public static bool IsWebp(byte[] data)
		{
			return data.Length > 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P';
		}

		private static bool IsGdiFormat(byte[] data)
		{
			if (data.Length < 4)
			{
				return false;
			}
			return (data[0] == 0xFF && data[1] == 0xD8) //JPEG
				|| (data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
				|| (data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
				|| (data[0] == 'B' && data[1] == 'M')
				|| (data[0] == 'I' && data[1] == 'I' && data[2] == 42) || (data[0] == 'M' && data[1] == 'M' && data[3] == 42);
		}

		/// <summary>The page as a bitmap the reader can draw quickly (32 bit, premultiplied).</summary>
		public static Bitmap Decode(byte[] data)
		{
			if (IsWebp(data))
			{
				return DecodeWebp(data);
			}
			if (IsGdiFormat(data))
			{
				try
				{
					using (MemoryStream ms = new MemoryStream(data))
					using (Image image = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true))
					{
						Bitmap bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
						using (Graphics g = Graphics.FromImage(bitmap))
						{
							g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
							g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
							g.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height));
						}
						return bitmap;
					}
				}
				catch (ArgumentException)
				{
					//GDI+ says "Parameter is not valid" for files it cannot read: Windows' codecs may still know them
				}
			}
			return DecodeWic(data);
		}

		/// <summary>The page's size, read from the file's header only (to pair pages in the two page layouts).</summary>
		public static Size GetSize(byte[] data)
		{
			try
			{
				if (IsWebp(data))
				{
					LoadWebp();
					GCHandle h = GCHandle.Alloc(data, GCHandleType.Pinned);
					try
					{
						return NativeMethods.WebPGetInfo(h.AddrOfPinnedObject(), (UIntPtr)data.Length, out int w, out int ht) != 0 ? new Size(w, ht) : Size.Empty;
					}
					finally
					{
						h.Free();
					}
				}
				if (IsGdiFormat(data))
				{
					using (MemoryStream ms = new MemoryStream(data))
					using (Image image = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false))
					{
						return image.Size;
					}
				}
				return SizeWic(data);
			}
			catch (Exception)
			{
				return Size.Empty;
			}
		}

		private static void LoadWebp()
		{
			lock (webpLock)
			{
				if (!webpLoaded)
				{
					//the same libwebp ComicRack uses; loaded by its full path, so the DllImports find it
					NativeMethods.LoadLibraryEx(Path.Combine(Program.AppFolder, "Resources", Environment.Is64BitProcess ? "x64" : "x86", "libwebp.dll"), IntPtr.Zero, NativeMethods.LOAD_WITH_ALTERED_SEARCH_PATH);
					webpLoaded = true;
				}
			}
		}

		private static Bitmap DecodeWebp(byte[] data)
		{
			LoadWebp();
			GCHandle h = GCHandle.Alloc(data, GCHandleType.Pinned);
			try
			{
				IntPtr ptr = h.AddrOfPinnedObject();
				if (NativeMethods.WebPGetInfo(ptr, (UIntPtr)data.Length, out int width, out int height) == 0)
				{
					throw new InvalidDataException("This WebP picture could not be read.");
				}
				Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
				BitmapData bd = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
				try
				{
					if (NativeMethods.WebPDecodeBGRAInto(ptr, (UIntPtr)data.Length, bd.Scan0, (UIntPtr)(bd.Stride * height), bd.Stride) == IntPtr.Zero)
					{
						throw new InvalidDataException("This WebP picture could not be read.");
					}
				}
				finally
				{
					bitmap.UnlockBits(bd);
				}
				return bitmap;
			}
			finally
			{
				h.Free();
			}
		}

		//WPF (PresentationCore) is loaded only for pages that need it
		[MethodImpl(MethodImplOptions.NoInlining)]
		private static Bitmap DecodeWic(byte[] data)
		{
			using (MemoryStream ms = new MemoryStream(data))
			{
				System.Windows.Media.Imaging.BitmapDecoder decoder;
				try
				{
					decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(ms, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
				}
				catch (Exception ex)
				{
					throw new InvalidDataException("This page's picture format cannot be read here (" + ex.Message.TrimEnd('.') + "). Open the book in ComicRack.", ex);
				}
				System.Windows.Media.Imaging.BitmapSource source = decoder.Frames[0];
				if (source.Format != System.Windows.Media.PixelFormats.Pbgra32)
				{
					source = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
				}
				Bitmap bitmap = new Bitmap(source.PixelWidth, source.PixelHeight, PixelFormat.Format32bppPArgb);
				BitmapData bd = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
				try
				{
					source.CopyPixels(System.Windows.Int32Rect.Empty, bd.Scan0, bd.Stride * bd.Height, bd.Stride);
				}
				finally
				{
					bitmap.UnlockBits(bd);
				}
				return bitmap;
			}
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private static Size SizeWic(byte[] data)
		{
			using (MemoryStream ms = new MemoryStream(data))
			{
				System.Windows.Media.Imaging.BitmapFrame frame = System.Windows.Media.Imaging.BitmapFrame.Create(ms, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
				return new Size(frame.PixelWidth, frame.PixelHeight);
			}
		}
	}
}
