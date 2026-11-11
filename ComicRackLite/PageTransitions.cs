using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace ComicRackLite
{
	internal enum PageEffect
	{
		None,
		Slide,
		Fade,
		Curl
	}

	/// <summary>
	/// Page turn effects (Lite 1.4): slide, fade and ComicRack's realistic page curl. They play between two spreads that are
	/// drawn already, so they cost nothing until a page is turned. The curl is ComicRack's (ComicDisplayControl's
	/// RenderPageCurl): the turning sheet is cut into thin vertical slices, each with its own 3D angle, perspective size and
	/// shade, and each slice is drawn as the matching strip of the page, which GDI+ does fast enough without the graphics card.
	/// </summary>
	internal sealed partial class PageView
	{
		private const float PageCurlBend = 0.9f;

		private static readonly Color PageCurlPaperColor = Color.FromArgb(246, 243, 236);

		private readonly Timer frameTimer = new Timer { Interval = 10 };

		private readonly Stopwatch transitionClock = new Stopwatch();

		private PageEffect effect;

		private RenderResult fromImage;

		private Rectangle fromRect;

		private bool forward;

		private bool rightToLeft;

		private bool twoPages;

		private int duration;

		/// <summary>The spread a page turn starts from, while it plays (it must not be freed meanwhile).</summary>
		public RenderResult TransitionFrom => transitionClock.IsRunning ? fromImage : null;

		public bool Animating => transitionClock.IsRunning;

		/// <summary>The spread the turn started from, given back when the turn is over.</summary>
		public event Action<RenderResult> TransitionEnded;

		private void InitTransitions()
		{
			frameTimer.Tick += delegate
			{
				Invalidate();
			};
		}

		public static int DurationOf(PageEffect effect)
		{
			switch (effect)
			{
				case PageEffect.Slide:
					return 260;
				case PageEffect.Fade:
					return 220;
				case PageEffect.Curl:
					return 600; //ComicRack's PageCurlDuration
				default:
					return 0;
			}
		}

		/// <summary>Plays a page turn from the spread shown before (at the place it was shown) to the one shown now.</summary>
		public void StartTransition(PageEffect pageEffect, RenderResult from, Rectangle fromRectangle, bool isForward, bool isRightToLeft, bool isTwoPages)
		{
			EndTransition();
			if (pageEffect == PageEffect.None || from?.Bitmap == null || image?.Bitmap == null || from == image)
			{
				return;
			}
			effect = pageEffect;
			fromImage = from;
			fromRect = fromRectangle;
			forward = isForward;
			rightToLeft = isRightToLeft;
			twoPages = isTwoPages;
			duration = DurationOf(pageEffect);
			transitionClock.Restart();
			frameTimer.Start();
			Invalidate();
		}

		public void EndTransition()
		{
			if (!transitionClock.IsRunning)
			{
				return;
			}
			transitionClock.Reset();
			frameTimer.Stop();
			RenderResult from = fromImage;
			fromImage = null;
			ClearStrips();
			Invalidate();
			TransitionEnded?.Invoke(from);
		}

		/// <summary>Draws the page turn in progress; false when none plays (the spread is drawn as usual).</summary>
		private bool PaintTransition(Graphics g)
		{
			if (!transitionClock.IsRunning)
			{
				return false;
			}
			float t = duration <= 0 ? 1f : transitionClock.ElapsedMilliseconds / (float)duration;
			if (t >= 1f || fromImage?.Bitmap == null || image?.Bitmap == null)
			{
				//over: the next paint shows the new spread as usual
				BeginInvoke(new Action(EndTransition));
				return false;
			}
			g.InterpolationMode = InterpolationMode.Bilinear;
			g.PixelOffsetMode = PixelOffsetMode.Half;
			g.CompositingQuality = CompositingQuality.HighSpeed;
			Rectangle toRect = ImageRectangle;
			switch (effect)
			{
				case PageEffect.Fade:
					PaintFade(g, toRect, t);
					break;
				case PageEffect.Slide:
					PaintSlide(g, toRect, t);
					break;
				case PageEffect.Curl:
					if (forward)
					{
						//forward: the old page is the sheet that turns away and uncovers the new one
						PaintCurl(g, fromImage, fromRect, image, toRect, t);
					}
					else
					{
						//backward: the same motion played in reverse, the previous page swings back over
						PaintCurl(g, image, toRect, fromImage, fromRect, 1f - t);
					}
					break;
			}
			return true;
		}

		private static float Ease(float t)
		{
			return t * t * (3f - 2f * t);
		}

		private void PaintFade(Graphics g, Rectangle toRect, float t)
		{
			DrawSpread(g, fromImage, fromRect);
			using (ImageAttributes alpha = new ImageAttributes())
			{
				alpha.SetColorMatrix(new ColorMatrix { Matrix33 = Ease(t) });
				g.DrawImage(image.Bitmap, toRect, 0, 0, image.Bitmap.Width, image.Bitmap.Height, GraphicsUnit.Pixel, alpha);
			}
		}

		private void PaintSlide(Graphics g, Rectangle toRect, float t)
		{
			//forward in a left to right book: the new spread comes in from the right and pushes the old one out
			int dir = forward != rightToLeft ? -1 : 1;
			int shift = (int)(ClientSize.Width * Ease(t));
			Rectangle oldAt = fromRect;
			oldAt.Offset(dir * shift, 0);
			Rectangle newAt = toRect;
			newAt.Offset(-dir * (ClientSize.Width - shift), 0);
			DrawSpread(g, fromImage, oldAt);
			DrawSpread(g, image, newAt);
		}

		/// <summary>
		/// ComicRack's realistic page curl: the sheet (hinged at the spine) turns over the page under it. t is how far it
		/// has turned (0 lying flat on its own side, 1 landed on the other side).
		/// </summary>
		private void PaintCurl(Graphics g, RenderResult sheet, Rectangle page, RenderResult under, Rectangle underRect, float t)
		{
			Rectangle client = ClientRectangle;
			//a spread (two pages side by side) turns around its middle, a single page around its edge
			bool spread = twoPages && page.Width >= page.Height * 0.9f;
			bool mirrored = rightToLeft;
			int sgn = mirrored ? -1 : 1;
			float spine = spread ? (page.Left + page.Width / 2f) : (mirrored ? page.Right : page.Left);
			float width = spread ? (page.Width / 2f) : page.Width;
			if (width < 8f || page.Height < 8)
			{
				DrawSpread(g, under, underRect);
				return;
			}
			float eased = Ease(Math.Max(0f, Math.Min(1f, t)));
			float theta = (float)Math.PI * eased;
			float sinTheta = (float)Math.Sin(theta);
			RectangleF allowed = RectangleF.FromLTRB(page.Left, client.Top, page.Right, client.Bottom);

			//1. What lies underneath: the new page where the sheet came from, and in a spread the facing page of the old
			//   spread that the sheet will land on
			if (spread)
			{
				RectangleF uncovered = sgn > 0 ? RectangleF.FromLTRB(spine, client.Top, client.Right, client.Bottom) : RectangleF.FromLTRB(client.Left, client.Top, spine, client.Bottom);
				RectangleF facing = sgn > 0 ? RectangleF.FromLTRB(client.Left, client.Top, spine, client.Bottom) : RectangleF.FromLTRB(spine, client.Top, client.Right, client.Bottom);
				DrawPart(g, under, underRect, uncovered);
				DrawPart(g, sheet, page, facing);
			}
			else
			{
				DrawSpread(g, under, underRect);
			}

			//2. Shape of the sheet. Arc length u runs from the spine to the free edge; the free edge leads, like a page
			//   lifted by its edge, and the bend straightens out again as the sheet lands
			int slices = Math.Max(20, Math.Min(48, (int)(width / 14f)));
			float step = width / slices;
			float[] along = new float[slices + 1];
			float[] height = new float[slices + 1];
			float[] angle = new float[slices];
			for (int i = 0; i < slices; i++)
			{
				float u = (i + 0.5f) / slices;
				float phi = Math.Min((float)Math.PI, theta + PageCurlBend * sinTheta * u * u);
				angle[i] = phi;
				along[i + 1] = along[i] + (float)Math.Cos(phi) * step;
				height[i + 1] = height[i] + (float)Math.Sin(phi) * step;
			}
			float centerX = client.Left + client.Width / 2f;
			float centerY = page.Top + page.Height / 2f;
			float eye = 4f * Math.Max(width, page.Height);
			float[] screenX = new float[slices + 1];
			float[] scale = new float[slices + 1];
			for (int i = 0; i <= slices; i++)
			{
				scale[i] = eye / Math.Max(eye * 0.25f, eye - height[i]);
				screenX[i] = centerX + (spine + sgn * along[i] - centerX) * scale[i];
			}

			//3. Soft shadow the lifted sheet throws next to its free edge
			if (sinTheta > 0.02f)
			{
				float edge = screenX[slices];
				int side = Math.Sign(edge - spine);
				if (side == 0)
				{
					side = sgn;
				}
				float shadowWidth = width * 0.18f * sinTheta;
				float strength = 0.45f * sinTheta;
				g.SetClip(allowed);
				const int bands = 10;
				for (int k = 0; k < bands; k++)
				{
					float a = edge + side * shadowWidth * k / bands;
					float b = edge + side * shadowWidth * (k + 1) / bands;
					float fade = 1f - (float)k / bands;
					using (Brush shadow = new SolidBrush(Color.FromArgb((int)(255 * strength * fade * fade), Color.Black)))
					{
						g.FillRectangle(shadow, RectangleF.FromLTRB(Math.Min(a, b), page.Top, Math.Max(a, b), page.Bottom));
					}
				}
				g.ResetClip();
			}

			//4. The sheet itself, slice by slice, furthest from the viewer first
			int[] order = Enumerable.Range(0, slices).OrderBy((int i) => height[i] + height[i + 1]).ToArray();
			using (ImageAttributes showThrough = new ImageAttributes())
			using (Brush paper = new SolidBrush(PageCurlPaperColor))
			{
				showThrough.SetColorMatrix(new ColorMatrix { Matrix33 = 0.12f });
				foreach (int i in order)
				{
					float x0 = screenX[i];
					float x1 = screenX[i + 1];
					if (Math.Abs(x1 - x0) < 0.25f)
					{
						continue;
					}
					float s = (scale[i] + scale[i + 1]) / 2f;
					bool front = (x1 - x0) * sgn > 0f;
					RectangleF target = RectangleF.FromLTRB(Math.Min(x0, x1) - 0.35f, centerY + (page.Top - centerY) * s, Math.Max(x0, x1) + 0.35f, centerY + (page.Bottom - centerY) * s);
					target.Intersect(allowed);
					if (target.Width <= 0f || target.Height <= 0f)
					{
						continue;
					}
					float u0 = step * i;
					float u1 = step * (i + 1);
					//the back of a sheet in a spread is the first page of the next spread, which sits on the other side of
					//the spine once the sheet has landed
					bool useBack = !front && spread;
					float src0 = useBack ? (spine - sgn * u0) : (spine + sgn * u0);
					float src1 = useBack ? (spine - sgn * u1) : (spine + sgn * u1);
					float scaleX = (x1 - x0) / (src1 - src0);
					g.SetClip(target);
					if (!front && !spread)
					{
						//a single page has no next page on its back: paper with the print faintly showing through
						g.FillRectangle(paper, target);
					}
					DrawStrip(g, useBack ? under : sheet, useBack ? underRect : page, src0, src1, scaleX, x0 - scaleX * src0, s, centerY - s * centerY, front || spread ? null : showThrough);
					//light: facing the viewer is fully lit, edge on is darkest
					float shade = (1f - Math.Abs((float)Math.Cos(angle[i]))) * 0.6f;
					if (shade > 0.02f)
					{
						using (Brush dark = new SolidBrush(Color.FromArgb((int)(255 * shade), Color.Black)))
						{
							g.FillRectangle(dark, target);
						}
					}
				}
				g.ResetClip();
			}
		}

		/// <summary>Draws the part of a spread inside the area (only that part of the picture is read).</summary>
		private static void DrawPart(Graphics g, RenderResult r, Rectangle rect, RectangleF area)
		{
			RectangleF dest = RectangleF.Intersect(rect, area);
			if (dest.Width <= 0f || dest.Height <= 0f)
			{
				return;
			}
			float kx = r.Bitmap.Width / (float)rect.Width;
			float ky = r.Bitmap.Height / (float)rect.Height;
			RectangleF source = new RectangleF((dest.X - rect.X) * kx, (dest.Y - rect.Y) * ky, dest.Width * kx, dest.Height * ky);
			g.DrawImage(r.Bitmap, dest, source, GraphicsUnit.Pixel);
		}

		/// <summary>
		/// The strips of the pages cut out once per turn: a slice always shows the same strip of its page, and GDI+ draws a
		/// small picture far faster than a strip of a large one (it works through the whole picture for each strip).
		/// </summary>
		private readonly System.Collections.Generic.Dictionary<long, Bitmap> strips = new System.Collections.Generic.Dictionary<long, Bitmap>();

		private RenderResult stripsOfFront;

		private RenderResult stripsOfBack;

		private void ClearStrips()
		{
			foreach (Bitmap b in strips.Values)
			{
				b.Dispose();
			}
			strips.Clear();
			stripsOfFront = null;
			stripsOfBack = null;
		}

		/// <summary>
		/// Draws the strip of a spread between the screen columns a and b (where it lies flat), mapped by x' = scaleX * x +
		/// offsetX and y' = scaleY * y + offsetY: ComicRack's slice transform.
		/// </summary>
		private void DrawStrip(Graphics g, RenderResult r, Rectangle rect, float a, float b, float scaleX, float offsetX, float scaleY, float offsetY, ImageAttributes attributes)
		{
			int lo = (int)Math.Floor(Math.Max(rect.Left, Math.Min(a, b) - 1f));
			int hi = (int)Math.Ceiling(Math.Min(rect.Right, Math.Max(a, b) + 1f));
			if (hi <= lo)
			{
				return;
			}
			//the cut strips belong to the two spreads of this turn
			if (r != stripsOfFront && r != stripsOfBack)
			{
				if (stripsOfFront == null)
				{
					stripsOfFront = r;
				}
				else if (stripsOfBack == null)
				{
					stripsOfBack = r;
				}
				else
				{
					ClearStrips();
					stripsOfFront = r;
				}
			}
			long key = ((long)(r == stripsOfFront ? 1 : 2) << 40) | ((long)(lo - rect.Left + 100000) << 20) | (long)(hi - lo);
			if (!strips.TryGetValue(key, out Bitmap strip))
			{
				float kx = r.Bitmap.Width / (float)rect.Width;
				int sx = Math.Max(0, (int)Math.Floor((lo - rect.Left) * kx));
				int sw = Math.Max(1, Math.Min(r.Bitmap.Width - sx, (int)Math.Ceiling((hi - lo) * kx)));
				strip = new Bitmap(sw, r.Bitmap.Height, PixelFormat.Format32bppPArgb);
				using (Graphics sg = Graphics.FromImage(strip))
				{
					sg.CompositingMode = CompositingMode.SourceCopy;
					sg.DrawImage(r.Bitmap, new Rectangle(0, 0, sw, r.Bitmap.Height), new Rectangle(sx, 0, sw, r.Bitmap.Height), GraphicsUnit.Pixel);
				}
				strips[key] = strip;
			}
			float top = scaleY * rect.Top + offsetY;
			float bottom = scaleY * rect.Bottom + offsetY;
			PointF[] dest =
			{
				new PointF(scaleX * lo + offsetX, top),
				new PointF(scaleX * hi + offsetX, top),
				new PointF(scaleX * lo + offsetX, bottom)
			};
			g.DrawImage(strip, dest, new RectangleF(0, 0, strip.Width, strip.Height), GraphicsUnit.Pixel, attributes);
		}
	}
}
