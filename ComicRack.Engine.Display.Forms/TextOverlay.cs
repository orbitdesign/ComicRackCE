using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using cYo.Common.Drawing;
using cYo.Common.Presentation.Ceco;
using cYo.Common.Presentation.Panels;

namespace cYo.Projects.ComicRack.Engine.Display.Forms
{
	public class TextOverlay : OverlayPanel
	{
		private Bitmap icon;

		private string text;

		private Size maxSize = new Size(400, 400);

		private Size minSize;

		private Font font;

		private static Bitmap hbm = new Bitmap(16, 16);

		/// <summary>The icon sits right beside the text (small gap, text starts next to it) and the panel is exactly as wide as both.</summary>
		public bool TightIcon { get; set; }

		public Bitmap Icon
		{
			get
			{
				return icon;
			}
			set
			{
				if (icon != value)
				{
					icon = value;
					Resize();
				}
			}
		}

		public string Text
		{
			get
			{
				return text;
			}
			set
			{
				if (!(text == value))
				{
					text = value;
					Resize();
				}
			}
		}

		public Size MaxSize
		{
			get
			{
				return maxSize;
			}
			set
			{
				maxSize = value;
				if (maxSize.Width < base.Width || maxSize.Height < base.Height)
				{
					Resize();
				}
			}
		}

		public Size MinSize
		{
			get
			{
				return minSize;
			}
			set
			{
				minSize = value;
				if (minSize.Width > base.Width || minSize.Height > base.Height)
				{
					Resize();
				}
			}
		}

		public Font Font
		{
			get
			{
				return font;
			}
			set
			{
				if (font != value)
				{
					font = value;
					Resize();
				}
			}
		}

		public bool Html
		{
			get;
			set;
		}

		public TextOverlay(int width, int height, ContentAlignment align, Font font)
			: base(width, height, align)
		{
			this.font = font;
			minSize = new Size(width, height);
		}

		private void Resize()
		{
			Padding margin = PanelRenderer.GetMargin(base.ClientRectangle);
			Size size = default(Size);
			int num = 0;
			if (icon != null)
			{
				size = icon.Size;
				num = margin.Horizontal;
			}
			Size size2;
			using (Graphics graphics = Graphics.FromImage(hbm))
			{
				size2 = ((!Html) ? graphics.MeasureString(text, font, MaxSize.Width - size.Width - 2 * num).ToSize() : XHtmlRenderer.MeasureString(graphics, text, font, MaxSize.Width - size.Width - 2 * num));
			}
			Size size3 = new Size(size.Width + num + size2.Width + margin.Horizontal, Math.Max(size.Height, size2.Height) + margin.Vertical);
			if (icon != null && TightIcon)
			{
				size3.Width = margin.Horizontal + icon.Width + 4 + size2.Width + 8;
			}
			size3.Width = Math.Max(minSize.Width, size3.Width);
			size3.Height = Math.Max(minSize.Height, size3.Height);
			base.Size = size3;
			Invalidate();
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			Padding margin = PanelRenderer.GetMargin(base.ClientRectangle);
			Rectangle clientRectangle = base.ClientRectangle;
			using (StringFormat stringFormat = new StringFormat
			{
				Alignment = StringAlignment.Center,
				LineAlignment = StringAlignment.Center
			})
			{
				clientRectangle = Rectangle.Round(PanelRenderer.DrawGraphics(graphics, clientRectangle, 1f));
				if (icon != null && TightIcon)
				{
					graphics.DrawImage(icon, clientRectangle.Left, clientRectangle.Top + (clientRectangle.Height - icon.Height) / 2);
					clientRectangle = new Rectangle(clientRectangle.Left + icon.Width + 4, clientRectangle.Top, Math.Max(1, clientRectangle.Width - icon.Width - 4), clientRectangle.Height);
					stringFormat.Alignment = StringAlignment.Near;
				}
				else if (icon != null)
				{
					graphics.DrawImage(icon, clientRectangle.Location);
					clientRectangle = clientRectangle.Pad(icon.Width + margin.Horizontal, 0);
				}
				using (graphics.TextRendering(TextRenderingHint.AntiAliasGridFit))
				{
					if (Html)
					{
						XHtmlRenderer.DrawString(graphics, text, font, PanelRenderer.GetForeColor(), clientRectangle, stringFormat);
						return;
					}
					using (SolidBrush brush = new SolidBrush(PanelRenderer.GetForeColor()))
					{
						graphics.DrawString(text, font, brush, clientRectangle, stringFormat);
					}
				}
			}
		}
	}
}
