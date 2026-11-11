using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ComicRackLite
{
	/// <summary>Light and dark looks; the dark one uses the colours of ComicRack's dark theme.</summary>
	internal static class Theme
	{
		public static readonly Color DarkBar = Color.FromArgb(46, 46, 46);

		public static readonly Color DarkWindow = Color.FromArgb(32, 32, 32);

		public static readonly Color DarkEdit = Color.FromArgb(50, 50, 50);

		public static readonly Color DarkText = Color.FromArgb(220, 220, 220);

		public static readonly Color DarkGrayText = Color.FromArgb(128, 128, 128);

		public static readonly Color DarkBorder = Color.FromArgb(90, 90, 90);

		[DllImport("dwmapi.dll")]
		private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

		/// <summary>A dark or light title bar (Windows 10 1809 and later).</summary>
		public static void SetTitleBar(IntPtr handle, bool dark)
		{
			try
			{
				int on = dark ? 1 : 0;
				if (DwmSetWindowAttribute(handle, 20, ref on, 4) != 0)
				{
					DwmSetWindowAttribute(handle, 19, ref on, 4);
				}
			}
			catch (Exception)
			{
			}
		}

		public static ToolStripRenderer Renderer(bool dark)
		{
			return dark ? new DarkRenderer() : (ToolStripRenderer)new ToolStripProfessionalRenderer { RoundedEdges = false };
		}

		/// <summary>Colours a small dialog and its controls.</summary>
		public static void Apply(Control root, bool dark)
		{
			if (!dark)
			{
				return;
			}
			root.BackColor = DarkWindow;
			root.ForeColor = DarkText;
			foreach (Control c in root.Controls)
			{
				switch (c)
				{
					case Button b:
						b.FlatStyle = FlatStyle.Flat;
						b.FlatAppearance.BorderColor = DarkBorder;
						b.BackColor = DarkEdit;
						b.ForeColor = DarkText;
						break;
					case TextBox t:
						t.BackColor = DarkEdit;
						t.ForeColor = DarkText;
						t.BorderStyle = BorderStyle.FixedSingle;
						break;
					case NumericUpDown n:
						n.BackColor = DarkEdit;
						n.ForeColor = DarkText;
						n.BorderStyle = BorderStyle.FixedSingle;
						break;
					default:
						c.ForeColor = DarkText;
						Apply(c, dark);
						break;
				}
			}
		}

		private sealed class DarkColors : ProfessionalColorTable
		{
			private static readonly Color Bar = DarkBar;

			private static readonly Color DropDown = Color.FromArgb(43, 43, 43);

			private static readonly Color Margin = Color.FromArgb(50, 50, 50);

			private static readonly Color Hot = Color.FromArgb(70, 70, 70);

			private static readonly Color Pressed = Color.FromArgb(85, 85, 85);

			private static readonly Color Checked = Color.FromArgb(80, 80, 80);

			public DarkColors()
			{
				UseSystemColors = false;
			}

			public override Color ToolStripGradientBegin => Bar;

			public override Color ToolStripGradientMiddle => Bar;

			public override Color ToolStripGradientEnd => Bar;

			public override Color ToolStripBorder => Bar;

			public override Color ToolStripContentPanelGradientBegin => Bar;

			public override Color ToolStripContentPanelGradientEnd => Bar;

			public override Color ToolStripPanelGradientBegin => Bar;

			public override Color ToolStripPanelGradientEnd => Bar;

			public override Color MenuStripGradientBegin => Bar;

			public override Color MenuStripGradientEnd => Bar;

			public override Color ToolStripDropDownBackground => DropDown;

			public override Color ImageMarginGradientBegin => Margin;

			public override Color ImageMarginGradientMiddle => Margin;

			public override Color ImageMarginGradientEnd => Margin;

			public override Color MenuBorder => DarkBorder;

			public override Color MenuItemBorder => Color.FromArgb(100, 100, 100);

			public override Color MenuItemSelected => Hot;

			public override Color MenuItemSelectedGradientBegin => Hot;

			public override Color MenuItemSelectedGradientEnd => Hot;

			public override Color MenuItemPressedGradientBegin => DropDown;

			public override Color MenuItemPressedGradientMiddle => DropDown;

			public override Color MenuItemPressedGradientEnd => DropDown;

			public override Color ButtonSelectedHighlight => Hot;

			public override Color ButtonSelectedHighlightBorder => DarkBorder;

			public override Color ButtonSelectedBorder => DarkBorder;

			public override Color ButtonSelectedGradientBegin => Hot;

			public override Color ButtonSelectedGradientMiddle => Hot;

			public override Color ButtonSelectedGradientEnd => Hot;

			public override Color ButtonPressedHighlight => Pressed;

			public override Color ButtonPressedHighlightBorder => DarkBorder;

			public override Color ButtonPressedBorder => DarkBorder;

			public override Color ButtonPressedGradientBegin => Pressed;

			public override Color ButtonPressedGradientMiddle => Pressed;

			public override Color ButtonPressedGradientEnd => Pressed;

			public override Color ButtonCheckedHighlight => Checked;

			public override Color ButtonCheckedHighlightBorder => DarkBorder;

			public override Color ButtonCheckedGradientBegin => Checked;

			public override Color ButtonCheckedGradientMiddle => Checked;

			public override Color ButtonCheckedGradientEnd => Checked;

			public override Color CheckBackground => Checked;

			public override Color CheckSelectedBackground => Pressed;

			public override Color CheckPressedBackground => Pressed;

			public override Color SeparatorDark => Color.FromArgb(28, 28, 28);

			public override Color SeparatorLight => Color.FromArgb(70, 70, 70);

			public override Color OverflowButtonGradientBegin => Bar;

			public override Color OverflowButtonGradientMiddle => Bar;

			public override Color OverflowButtonGradientEnd => Bar;

			public override Color GripDark => Color.FromArgb(28, 28, 28);

			public override Color GripLight => Color.FromArgb(80, 80, 80);
		}

		private sealed class DarkRenderer : ToolStripProfessionalRenderer
		{
			public DarkRenderer()
				: base(new DarkColors())
			{
				RoundedEdges = false;
			}

			protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
			{
				e.TextColor = e.Item.Enabled ? DarkText : DarkGrayText;
				base.OnRenderItemText(e);
			}

			protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
			{
				e.ArrowColor = e.Item == null || e.Item.Enabled ? DarkText : DarkGrayText;
				base.OnRenderArrow(e);
			}

			protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
			{
				if (e.Item.Image != null)
				{
					base.OnRenderItemCheck(e);
					return;
				}
				//the standard tick is black: draw a light one
				Rectangle r = e.ImageRectangle;
				r.Inflate(1, 1);
				using (Brush back = new SolidBrush(Color.FromArgb(80, 80, 80)))
				{
					e.Graphics.FillRectangle(back, r);
				}
				SmoothingMode before = e.Graphics.SmoothingMode;
				e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
				using (Pen pen = new Pen(DarkText, 1.6f))
				{
					float x = r.Left + r.Width * 0.25f;
					float y = r.Top + r.Height * 0.52f;
					e.Graphics.DrawLines(pen, new[]
					{
						new PointF(x, y),
						new PointF(r.Left + r.Width * 0.43f, r.Top + r.Height * 0.70f),
						new PointF(r.Left + r.Width * 0.76f, r.Top + r.Height * 0.30f)
					});
				}
				e.Graphics.SmoothingMode = before;
			}

			protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
			{
				Rectangle r = new Rectangle(Point.Empty, e.Item.Size);
				using (Pen pen = new Pen(Color.FromArgb(75, 75, 75)))
				{
					if (e.Vertical)
					{
						int x = r.Width / 2;
						e.Graphics.DrawLine(pen, x, r.Top + 4, x, r.Bottom - 4);
					}
					else
					{
						int y = r.Height / 2;
						e.Graphics.DrawLine(pen, r.Left + 28, y, r.Right - 2, y);
					}
				}
			}
		}
	}
}
