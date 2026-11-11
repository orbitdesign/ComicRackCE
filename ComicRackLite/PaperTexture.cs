using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace ComicRackLite
{
	/// <summary>
	/// ComicRack's paper texture (Lite 1.2): the texture, strength and layout set in ComicRack (its Config.xml), laid over the
	/// pages as ComicRack does: white mixed with the texture by the strength, multiplied with the page. Used on the render
	/// thread only.
	/// </summary>
	internal sealed class PaperTexture
	{
		public string File { get; private set; }

		public float Strength { get; private set; } = 1f;

		/// <summary>ComicRack's layout: Tile (its default), Stretch, Zoom, Center or None.</summary>
		public string Layout { get; private set; } = "Tile";

		private int[] pixels;

		private int width;

		private int height;

		private bool loaded;

		private string error;

		public bool IsSet => !string.IsNullOrEmpty(File) && Strength >= 0.05f;

		/// <summary>The paper texture of ComicRack's current display settings, or null when ComicRack has none.</summary>
		public static PaperTexture FromComicRack()
		{
			try
			{
				string config = System.IO.File.ReadAllText(Path.Combine(LiteSettings.Folder, "Config.xml"));
				Match workspace = Regex.Match(config, "<CurrentWorkspace>(.*?)</CurrentWorkspace>", RegexOptions.Singleline);
				string ws = workspace.Success ? workspace.Groups[1].Value : config;
				Match file = Regex.Match(ws, "<PaperTexture>([^<]*)</PaperTexture>");
				if (!file.Success || string.IsNullOrWhiteSpace(file.Groups[1].Value))
				{
					return null;
				}
				PaperTexture paper = new PaperTexture { File = System.Net.WebUtility.HtmlDecode(file.Groups[1].Value.Trim()) };
				Match strength = Regex.Match(ws, "<PaperTextureStrength>([^<]*)</PaperTextureStrength>");
				if (strength.Success && float.TryParse(strength.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float s))
				{
					paper.Strength = Math.Max(0f, Math.Min(1f, s));
				}
				Match layout = Regex.Match(ws, "<PaperTextureLayout>([^<]*)</PaperTextureLayout>");
				if (layout.Success)
				{
					paper.Layout = layout.Groups[1].Value.Trim();
				}
				return paper.IsSet && System.IO.File.Exists(paper.File) ? paper : null;
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// <summary>Reads the texture once and mixes it with white by the strength (as ComicRack's working texture).</summary>
		private bool Load()
		{
			if (loaded)
			{
				return pixels != null;
			}
			loaded = true;
			try
			{
				using (Bitmap texture = ImageDecoder.Decode(System.IO.File.ReadAllBytes(File)))
				using (Bitmap working = new Bitmap(texture.Width, texture.Height, PixelFormat.Format32bppArgb))
				{
					using (Graphics g = Graphics.FromImage(working))
					using (ImageAttributes faded = new ImageAttributes())
					{
						g.Clear(Color.White);
						faded.SetColorMatrix(new ColorMatrix { Matrix33 = Strength });
						g.DrawImage(texture, new Rectangle(0, 0, texture.Width, texture.Height), 0, 0, texture.Width, texture.Height, GraphicsUnit.Pixel, faded);
					}
					width = working.Width;
					height = working.Height;
					pixels = new int[width * height];
					BitmapData bd = working.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
					try
					{
						for (int y = 0; y < height; y++)
						{
							System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, pixels, y * width, width);
						}
					}
					finally
					{
						working.UnlockBits(bd);
					}
				}
				return true;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				pixels = null;
				return false;
			}
		}

		/// <summary>Multiplies the texture into the drawn spread (opaque, 32 bit premultiplied).</summary>
		public unsafe void Apply(Bitmap page)
		{
			if (!Load())
			{
				return;
			}
			int w = page.Width;
			int h = page.Height;
			bool stretch = Layout.Equals("Stretch", StringComparison.OrdinalIgnoreCase) || Layout.Equals("Zoom", StringComparison.OrdinalIgnoreCase);
			//for stretching: which texture column/row each page column/row takes
			int[] tx = new int[w];
			int[] ty = new int[h];
			for (int x = 0; x < w; x++)
			{
				tx[x] = stretch ? Math.Min(width - 1, (int)((long)x * width / w)) : x % width;
			}
			for (int y = 0; y < h; y++)
			{
				ty[y] = stretch ? Math.Min(height - 1, (int)((long)y * height / h)) : y % height;
			}
			BitmapData bd = page.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
			try
			{
				fixed (int* tex = pixels)
				{
					for (int y = 0; y < h; y++)
					{
						byte* row = (byte*)bd.Scan0 + y * bd.Stride;
						int* trow = tex + ty[y] * width;
						for (int x = 0; x < w; x++)
						{
							int t = trow[tx[x]];
							byte* p = row + x * 4;
							p[0] = (byte)((p[0] * (t & 0xFF) + 127) / 255);
							p[1] = (byte)((p[1] * ((t >> 8) & 0xFF) + 127) / 255);
							p[2] = (byte)((p[2] * ((t >> 16) & 0xFF) + 127) / 255);
						}
					}
				}
			}
			finally
			{
				page.UnlockBits(bd);
			}
		}

		public string Error => error;
	}
}
