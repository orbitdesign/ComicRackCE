using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ComicRackLite
{
	/// <summary>
	/// Shows the current spread: centred when it fits, scrolled (wheel, keys, or dragging with the mouse) when it is larger
	/// than the window. Draws the page number for a moment after a page turn, and short messages.
	/// </summary>
	internal sealed class PageView : Control
	{
		private RenderResult image;

		private Point offset;

		private Point dragStart;

		private Point dragOffset;

		private bool dragging;

		private bool dragMoved;

		private string pageText;

		private DateTime pageTextUntil;

		private string message;

		private DateTime messageUntil;

		private readonly Timer overlayTimer = new Timer { Interval = 150 };

		/// <summary>The size a spread of this natural size is shown at now.</summary>
		public Func<Size, Size> DisplaySizeOf = (Size s) => s;

		public string CenterText { get; set; }

		public bool CenterIsError { get; set; }

		/// <summary>A click (no drag) on the page, at this x as a share of the width (0..1).</summary>
		public event Action<float> PageClicked;

		public RenderResult Image => image;

		public PageView()
		{
			SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, value: true);
			BackColor = Color.Black;
			overlayTimer.Tick += delegate
			{
				DateTime now = DateTime.Now;
				if ((pageText != null && now > pageTextUntil) || (message != null && now > messageUntil))
				{
					if (now > pageTextUntil)
					{
						pageText = null;
					}
					if (now > messageUntil)
					{
						message = null;
					}
					Invalidate();
				}
				if (pageText == null && message == null)
				{
					overlayTimer.Stop();
				}
			};
		}

		public void SetImage(RenderResult result)
		{
			image = result;
			ClampOffset();
			Invalidate();
		}

		/// <summary>Where a new spread starts: at its top (or its bottom, paging backwards), at the side reading starts.</summary>
		public void ResetScroll(bool atEnd, bool rightToLeft)
		{
			offset = new Point(rightToLeft ? int.MaxValue : 0, atEnd ? int.MaxValue : 0);
			ClampOffset();
			Invalidate();
		}

		public void ShowPageText(string text)
		{
			pageText = text;
			pageTextUntil = DateTime.Now.AddMilliseconds(1400);
			overlayTimer.Start();
			Invalidate();
		}

		public void ShowMessage(string text, int ms = 3500)
		{
			message = text;
			messageUntil = DateTime.Now.AddMilliseconds(ms);
			overlayTimer.Start();
			Invalidate();
		}

		public void HideMessage()
		{
			message = null;
			Invalidate();
		}

		private Size Shown => image?.Bitmap == null ? Size.Empty : DisplaySizeOf(image.Natural);

		private Size MaxOffset
		{
			get
			{
				Size s = Shown;
				return new Size(Math.Max(0, s.Width - ClientSize.Width), Math.Max(0, s.Height - ClientSize.Height));
			}
		}

		private void ClampOffset()
		{
			Size max = MaxOffset;
			offset = new Point(Math.Max(0, Math.Min(offset.X, max.Width)), Math.Max(0, Math.Min(offset.Y, max.Height)));
		}

		/// <summary>Scrolls the spread; false when it could not move that way (so the caller turns the page instead).</summary>
		public bool ScrollBy(int dx, int dy)
		{
			Point before = offset;
			offset = new Point(offset.X + dx, offset.Y + dy);
			ClampOffset();
			if (offset == before)
			{
				return false;
			}
			Invalidate();
			return true;
		}

		protected override void OnResize(EventArgs e)
		{
			base.OnResize(e);
			ClampOffset();
		}

		protected override bool IsInputKey(Keys keyData)
		{
			return true;
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			Focus();
			if (e.Button == MouseButtons.Left)
			{
				dragging = true;
				dragMoved = false;
				dragStart = e.Location;
				dragOffset = offset;
			}
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			if (!dragging)
			{
				return;
			}
			if (Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y) > 4)
			{
				dragMoved = true;
			}
			if (dragMoved)
			{
				Size max = MaxOffset;
				if (max.Width > 0 || max.Height > 0)
				{
					Cursor = Cursors.SizeAll;
					offset = new Point(dragOffset.X - (e.X - dragStart.X), dragOffset.Y - (e.Y - dragStart.Y));
					ClampOffset();
					Invalidate();
				}
			}
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			base.OnMouseUp(e);
			if (e.Button == MouseButtons.Left && dragging)
			{
				dragging = false;
				Cursor = Cursors.Default;
				if (!dragMoved && ClientSize.Width > 0)
				{
					PageClicked?.Invoke(e.X / (float)ClientSize.Width);
				}
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			g.Clear(BackColor);
			if (image?.Bitmap != null)
			{
				Size shown = Shown;
				int x = shown.Width <= ClientSize.Width ? (ClientSize.Width - shown.Width) / 2 : -offset.X;
				int y = shown.Height <= ClientSize.Height ? (ClientSize.Height - shown.Height) / 2 : -offset.Y;
				if (shown == image.Bitmap.Size)
				{
					g.DrawImageUnscaled(image.Bitmap, x, y);
				}
				else
				{
					//shown at another size until the spread is drawn again for it (window resized, zoomed)
					g.InterpolationMode = InterpolationMode.Bilinear;
					g.PixelOffsetMode = PixelOffsetMode.Half;
					g.DrawImage(image.Bitmap, new Rectangle(x, y, shown.Width, shown.Height));
				}
			}
			if (!string.IsNullOrEmpty(CenterText))
			{
				DrawBubble(g, CenterText, ContentAlignment.MiddleCenter, CenterIsError ? Color.FromArgb(255, 150, 150) : Color.Gainsboro);
			}
			if (message != null)
			{
				DrawBubble(g, message, ContentAlignment.BottomCenter, Color.White);
			}
			if (pageText != null)
			{
				DrawBubble(g, pageText, ContentAlignment.BottomRight, Color.White);
			}
		}

		private void DrawBubble(Graphics g, string text, ContentAlignment where, Color color)
		{
			const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
			Font font = Font;
			int maxWidth = Math.Max(100, Math.Min(ClientSize.Width - 40, 640));
			Size size = TextRenderer.MeasureText(g, text, font, new Size(maxWidth, int.MaxValue), flags);
			size = new Size(size.Width + 24, size.Height + 14);
			int x;
			int y;
			switch (where)
			{
				case ContentAlignment.BottomRight:
					x = ClientSize.Width - size.Width - 16;
					y = ClientSize.Height - size.Height - 16;
					break;
				case ContentAlignment.BottomCenter:
					x = (ClientSize.Width - size.Width) / 2;
					y = ClientSize.Height - size.Height - 24;
					break;
				default:
					x = (ClientSize.Width - size.Width) / 2;
					y = (ClientSize.Height - size.Height) / 2;
					break;
			}
			Rectangle r = new Rectangle(x, y, size.Width, size.Height);
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (GraphicsPath path = Rounded(r, 6))
			using (Brush back = new SolidBrush(Color.FromArgb(190, 24, 24, 24)))
			using (Pen border = new Pen(Color.FromArgb(120, 110, 110, 110)))
			{
				g.FillPath(back, path);
				g.DrawPath(border, path);
			}
			g.SmoothingMode = SmoothingMode.None;
			TextRenderer.DrawText(g, text, font, r, color, flags);
		}

		private static GraphicsPath Rounded(Rectangle r, int radius)
		{
			GraphicsPath path = new GraphicsPath();
			int d = radius * 2;
			path.AddArc(r.Left, r.Top, d, d, 180, 90);
			path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
			path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
			path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
			path.CloseFigure();
			return path;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				overlayTimer.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
