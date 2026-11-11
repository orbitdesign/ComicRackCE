using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;

namespace ComicRackLite
{
	/// <summary>How the pages are fitted into the window.</summary>
	internal sealed class ViewParams
	{
		public FitMode Fit;

		public bool OnlyFitOversized;

		public float Zoom = 1f;

		public Size Client;

		public int Rotation;

		public bool RightToLeft;

		/// <summary>Lay the paper texture over the pages.</summary>
		public bool Paper;

		public ViewParams Clone()
		{
			return (ViewParams)MemberwiseClone();
		}

		/// <summary>The scale the pages (their natural, rotated size) are shown at.</summary>
		public float ScaleFor(Size natural)
		{
			if (natural.Width <= 0 || natural.Height <= 0)
			{
				return 1f;
			}
			float cw = Math.Max(1, Client.Width);
			float ch = Math.Max(1, Client.Height);
			float s;
			switch (Fit)
			{
				case FitMode.FitWidth:
					s = cw / natural.Width;
					break;
				case FitMode.FitHeight:
					s = ch / natural.Height;
					break;
				case FitMode.Original:
					s = 1f;
					break;
				default:
					s = Math.Min(cw / natural.Width, ch / natural.Height);
					break;
			}
			if (OnlyFitOversized && s > 1f)
			{
				s = 1f;
			}
			s *= Zoom;
			//keep a zoomed picture within reason (100 million pixels)
			double pixels = (double)natural.Width * s * natural.Height * s;
			if (pixels > 100e6)
			{
				s = (float)Math.Sqrt(100e6 / ((double)natural.Width * natural.Height));
			}
			return Math.Max(s, 0.01f);
		}

		public Size DisplaySize(Size natural)
		{
			float s = ScaleFor(natural);
			return new Size(Math.Max(1, (int)Math.Round(natural.Width * s)), Math.Max(1, (int)Math.Round(natural.Height * s)));
		}
	}

	internal sealed class RenderJob
	{
		public PageSource Source;

		public int[] Pages;

		public ViewParams View;

		public string Key;

		public bool Current;
	}

	/// <summary>A spread (one or two pages) drawn at the size it is shown at.</summary>
	internal sealed class RenderResult
	{
		public PageSource Source;

		public string Key;

		public int[] Pages;

		public Bitmap Bitmap;

		/// <summary>The spread's size at 100% (pages scaled to the same height, then rotated).</summary>
		public Size Natural;

		public string Error;
	}

	/// <summary>
	/// Decodes and draws spreads on a background thread, so turning a page only shows a picture that is ready: the spread
	/// asked for comes first, then the ones before and after it. The last few decoded pages are kept.
	/// </summary>
	internal sealed class Renderer : IDisposable
	{
		private readonly List<RenderJob> jobs = new List<RenderJob>();

		private readonly Action<RenderResult> done;

		private readonly Thread thread;

		private readonly LinkedList<KeyValuePair<int, Bitmap>> decoded = new LinkedList<KeyValuePair<int, Bitmap>>();

		private PageSource decodedSource;

		private volatile bool disposed;

		private bool held;

		//a spread is kept decoded (to draw it again at another size); the drawn spreads are what turning pages uses
		private const int KeepDecoded = 2;

		private readonly PaperTexture paper;

		public Renderer(Action<RenderResult> done, PaperTexture paper)
		{
			this.done = done;
			this.paper = paper;
			thread = new Thread(Run) { IsBackground = true, Name = "Render" };
			thread.Start();
		}

		public static string KeyOf(int[] pages, ViewParams view)
		{
			return string.Join(",", pages) + "|" + view.Rotation + "|" + (view.RightToLeft ? "r" : "l") + (view.Paper ? "|p" : "");
		}

		/// <summary>Asks for a spread. The current one goes first and drops the jobs asked for before it.</summary>
		public void Request(RenderJob job)
		{
			lock (jobs)
			{
				if (job.Current)
				{
					jobs.Clear();
					jobs.Insert(0, job);
				}
				else if (!jobs.Any((RenderJob j) => j.Key == job.Key && j.Source == job.Source))
				{
					jobs.Add(job);
				}
				Monitor.PulseAll(jobs);
			}
		}

		private void Run()
		{
			while (!disposed)
			{
				RenderJob job;
				lock (jobs)
				{
					while ((jobs.Count == 0 || (held && !jobs[0].Current)) && !disposed)
					{
						Monitor.Wait(jobs);
					}
					if (disposed)
					{
						break;
					}
					job = jobs[0];
					jobs.RemoveAt(0);
				}
				RenderResult result = new RenderResult { Source = job.Source, Key = job.Key, Pages = job.Pages };
				try
				{
					Render(job, result);
				}
				catch (Exception ex)
				{
					result.Bitmap?.Dispose();
					result.Bitmap = null;
					result.Error = ex.Message;
				}
				if (disposed)
				{
					result.Bitmap?.Dispose();
					break;
				}
				done(result);
			}
			ClearDecoded();
		}

		private void ClearDecoded()
		{
			foreach (KeyValuePair<int, Bitmap> d in decoded)
			{
				d.Value.Dispose();
			}
			decoded.Clear();
		}

		private Bitmap Decoded(PageSource source, int page)
		{
			if (decodedSource != source)
			{
				ClearDecoded();
				decodedSource = source;
			}
			for (LinkedListNode<KeyValuePair<int, Bitmap>> n = decoded.First; n != null; n = n.Next)
			{
				if (n.Value.Key == page)
				{
					decoded.Remove(n);
					decoded.AddFirst(n);
					return n.Value.Value;
				}
			}
			Bitmap bitmap = ImageDecoder.Decode(source.Read(page));
			source.SetSize(page, bitmap.Size);
			decoded.AddFirst(new KeyValuePair<int, Bitmap>(page, bitmap));
			while (decoded.Count > KeepDecoded)
			{
				decoded.Last.Value.Value.Dispose();
				decoded.RemoveLast();
			}
			return bitmap;
		}

		private void Render(RenderJob job, RenderResult result)
		{
			Bitmap[] pages = job.Pages.Select((int p) => Decoded(job.Source, p)).ToArray();
			float h0 = pages.Max((Bitmap b) => b.Height);
			float[] widths = pages.Select((Bitmap b) => b.Width * h0 / b.Height).ToArray();
			float w0 = widths.Sum();
			bool turned = job.View.Rotation % 180 != 0;
			result.Natural = turned ? new Size((int)Math.Round(h0), (int)Math.Round(w0)) : new Size((int)Math.Round(w0), (int)Math.Round(h0));
			Size target = job.View.DisplaySize(result.Natural);
			Bitmap bitmap = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppPArgb);
			result.Bitmap = bitmap;
			float sx = target.Width / (float)result.Natural.Width;
			float sy = target.Height / (float)result.Natural.Height;
			using (Graphics g = Graphics.FromImage(bitmap))
			using (ImageAttributes edges = new ImageAttributes())
			{
				edges.SetWrapMode(WrapMode.TileFlipXY);
				g.Clear(Color.White);
				g.InterpolationMode = InterpolationMode.HighQualityBicubic;
				g.PixelOffsetMode = PixelOffsetMode.HighQuality;
				g.CompositingQuality = CompositingQuality.HighSpeed;
				g.TranslateTransform(target.Width / 2f, target.Height / 2f);
				g.RotateTransform(job.View.Rotation);
				g.ScaleTransform(turned ? sy : sx, turned ? sx : sy);
				g.TranslateTransform(-w0 / 2f, -h0 / 2f);
				int[] order = Enumerable.Range(0, pages.Length).ToArray();
				if (job.View.RightToLeft)
				{
					Array.Reverse(order);
				}
				float x = 0f;
				foreach (int i in order)
				{
					Rectangle dest = Rectangle.Round(new RectangleF(x, 0f, widths[i], h0));
					g.DrawImage(pages[i], new[] { new Point(dest.Left, dest.Top), new Point(dest.Right, dest.Top), new Point(dest.Left, dest.Bottom) }, new Rectangle(0, 0, pages[i].Width, pages[i].Height), GraphicsUnit.Pixel, edges);
					x += widths[i];
				}
			}
			if (job.View.Paper && paper != null)
			{
				paper.Apply(bitmap);
			}
		}

		/// <summary>While a page turn plays, the pages ahead wait (they would slow the turn down); asked for pages do not.</summary>
		public void Hold(bool hold)
		{
			lock (jobs)
			{
				held = hold;
				Monitor.PulseAll(jobs);
			}
		}

		public void Dispose()
		{
			disposed = true;
			lock (jobs)
			{
				jobs.Clear();
				Monitor.PulseAll(jobs);
			}
		}
	}
}
