using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using cYo.Common.ComponentModel;
using cYo.Common.Drawing;
using cYo.Common.Windows.Forms;
using cYo.Common.Windows.Forms.Theme;
using cYo.Projects.ComicRack.Engine;
using cYo.Projects.ComicRack.Engine.Display;
using cYo.Projects.ComicRack.Engine.Display.Forms;
using cYo.Projects.ComicRack.Engine.IO;

namespace cYo.Projects.ComicRack.Viewer.Dialogs
{
	/// <summary>
	/// A window beside the reader for correcting the translated page on screen: every piece of text is
	/// outlined on the page and can be moved, resized and rotated with the mouse, its text, font size and
	/// alignment can be changed, boxes can be added where the OCR missed text, and the translation of a
	/// box can be forgotten. Every change is saved with the book and the reader shows the redrawn page.
	/// </summary>
	public class TranslationEditorForm : FormEx
	{
		private enum Drag
		{
			None,
			Move,
			Resize,
			Rotate,
			Pan,
			Draw
		}

		/// <summary>The picture of the page with the outlines of its boxes on top.</summary>
		private class PageCanvas : Control
		{
			private const int HandleSize = 9;

			private readonly Color back = Color.FromArgb(58, 58, 62);

			private Bitmap image;

			private SizeF imageSize;

			private float zoom = 1f;

			private PointF pan;

			private Drag drag;

			private int handle;

			private PointF dragStart;

			private int[] startRect;

			private double startRotation;

			private PointF panStart;

			private Rectangle drawing;

			public List<TranslationBox> Boxes { get; set; } = new List<TranslationBox>();

			public TranslationBox Selected { get; set; }

			/// <summary>Further selected boxes besides Selected (Ctrl or Shift click adds or removes a box).</summary>
			public HashSet<TranslationBox> Extra { get; } = new HashSet<TranslationBox>();

			public IEnumerable<TranslationBox> AllSelected
			{
				get
				{
					if (Selected != null)
					{
						yield return Selected;
					}
					foreach (TranslationBox b in Extra)
					{
						if (b != Selected)
						{
							yield return b;
						}
					}
				}
			}

			public event Action<Point> ContextRequested;

			private bool panMoved;

			public bool AddMode { get; set; }

			/// <summary>The boxes that show: those folded into a merged box are hidden.</summary>
			public IEnumerable<TranslationBox> Visible => Boxes.Where((TranslationBox b) => !b.IsHidden);

			private List<int[]> startShape;

			public bool IsDragging => drag != Drag.None;

			public string Message { get; set; }

			public event EventHandler SelectionChanged;

			public event EventHandler BoxChanging;

			public event EventHandler BoxChanged;

			public event Action<int[]> RegionDrawn;

			public PageCanvas()
			{
				SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
				TabStop = true;
				BackColor = back;
			}

			public void SetImage(Bitmap bitmap, int width, int height, bool keepView)
			{
				Bitmap old = image;
				image = bitmap;
				old?.Dispose();
				SizeF size = bitmap != null ? new SizeF(bitmap.Width, bitmap.Height) : new SizeF(Math.Max(width, 1), Math.Max(height, 1));
				bool changed = size != imageSize;
				imageSize = size;
				if (!keepView || changed)
				{
					Fit();
				}
				Invalidate();
			}

			public void Fit()
			{
				if (imageSize.Width < 1 || ClientSize.Width < 20 || ClientSize.Height < 20)
				{
					return;
				}
				zoom = Math.Min((ClientSize.Width - 16) / imageSize.Width, (ClientSize.Height - 16) / imageSize.Height);
				pan = new PointF((ClientSize.Width - imageSize.Width * zoom) / 2f, (ClientSize.Height - imageSize.Height * zoom) / 2f);
				Invalidate();
			}

			protected override void OnResize(EventArgs e)
			{
				base.OnResize(e);
				if (image != null && zoom <= 0.0001f)
				{
					Fit();
				}
			}

			protected override void Dispose(bool disposing)
			{
				if (disposing)
				{
					image?.Dispose();
					image = null;
				}
				base.Dispose(disposing);
			}

			private PointF ToImage(Point p)
			{
				return new PointF((p.X - pan.X) / zoom, (p.Y - pan.Y) / zoom);
			}

			private RectangleF ToScreen(int[] r)
			{
				return new RectangleF(r[0] * zoom + pan.X, r[1] * zoom + pan.Y, (r[2] - r[0]) * zoom, (r[3] - r[1]) * zoom);
			}

			private static int[] AreaOf(TranslationBox b)
			{
				return b.Area;
			}

			private PointF RotationHandle(TranslationBox b)
			{
				RectangleF r = ToScreen(AreaOf(b));
				return new PointF(r.Left + r.Width / 2f, r.Top - 26f);
			}

			private PointF[] HandlePoints(RectangleF r)
			{
				float cx = r.Left + r.Width / 2f;
				float cy = r.Top + r.Height / 2f;
				return new[]
				{
					new PointF(r.Left, r.Top), new PointF(cx, r.Top), new PointF(r.Right, r.Top),
					new PointF(r.Right, cy), new PointF(r.Right, r.Bottom), new PointF(cx, r.Bottom),
					new PointF(r.Left, r.Bottom), new PointF(r.Left, cy)
				};
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				Graphics g = e.Graphics;
				g.Clear(back);
				if (image != null)
				{
					g.InterpolationMode = zoom < 1f ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
					g.PixelOffsetMode = PixelOffsetMode.Half;
					g.DrawImage(image, new RectangleF(pan.X, pan.Y, imageSize.Width * zoom, imageSize.Height * zoom));
				}
				g.SmoothingMode = SmoothingMode.AntiAlias;
				int number = 0;
				for (int i = 0; i < Boxes.Count; i++)
				{
					TranslationBox box = Boxes[i];
					if (box.IsHidden)
					{
						continue;
					}
					number++;
					bool selected = box == Selected || Extra.Contains(box);
					RectangleF r = ToScreen(box.Skip ? box.Src : AreaOf(box));
					Color color = box.Skip ? Color.Gray : (box.Manual ? Color.FromArgb(70, 150, 255) : Color.FromArgb(60, 200, 90));
					if (selected)
					{
						color = Color.FromArgb(255, 150, 20);
					}
					using (Pen pen = new Pen(color, selected ? 2.5f : 1.5f))
					{
						if (box.Skip)
						{
							pen.DashStyle = DashStyle.Dash;
						}
						if (box.Rotation != 0 && !box.Skip)
						{
							using (Matrix m = new Matrix())
							{
								m.RotateAt((float)box.Rotation, new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f));
								PointF[] pts = { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom) };
								m.TransformPoints(pts);
								g.DrawPolygon(pen, pts);
							}
							using (Pen faint = new Pen(Color.FromArgb(110, color), 1f) { DashStyle = DashStyle.Dot })
							{
								g.DrawRectangle(faint, r.X, r.Y, r.Width, r.Height);
							}
						}
						else if (box.Shape != null && !box.Skip)
						{
							foreach (int[] part in box.Shape)
							{
								RectangleF pr = ToScreen(part);
								g.DrawRectangle(pen, pr.X, pr.Y, pr.Width, pr.Height);
							}
						}
						else
						{
							g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
						}
					}
					string tag = number.ToString();
					SizeF ts = g.MeasureString(tag, Font);
					using (SolidBrush fill = new SolidBrush(color))
					{
						g.FillRectangle(fill, r.Left, r.Top - ts.Height + 1, ts.Width + 2, ts.Height - 1);
					}
					g.DrawString(tag, Font, Brushes.Black, r.Left + 1, r.Top - ts.Height);
					if (box == Selected && Extra.Count == 0 && !box.Skip && box.Shape == null)
					{
						PointF top = new PointF(r.Left + r.Width / 2f, r.Top);
						PointF knob = RotationHandle(box);
						using (Pen line = new Pen(color, 1f))
						{
							g.DrawLine(line, top, knob);
						}
						g.FillEllipse(Brushes.White, knob.X - 6, knob.Y - 6, 12, 12);
						using (Pen ring = new Pen(color, 2f))
						{
							g.DrawEllipse(ring, knob.X - 6, knob.Y - 6, 12, 12);
						}
						foreach (PointF h in HandlePoints(r))
						{
							g.FillRectangle(Brushes.White, h.X - HandleSize / 2f, h.Y - HandleSize / 2f, HandleSize, HandleSize);
							using (Pen ring = new Pen(color, 1.5f))
							{
								g.DrawRectangle(ring, h.X - HandleSize / 2f, h.Y - HandleSize / 2f, HandleSize, HandleSize);
							}
						}
					}
				}
				if (drag == Drag.Draw)
				{
					using (Pen pen = new Pen(Color.FromArgb(70, 150, 255), 2f) { DashStyle = DashStyle.Dash })
					using (SolidBrush fill = new SolidBrush(Color.FromArgb(40, 70, 150, 255)))
					{
						g.FillRectangle(fill, drawing);
						g.DrawRectangle(pen, drawing);
					}
				}
				if (!string.IsNullOrEmpty(Message))
				{
					SizeF ms = g.MeasureString(Message, Font);
					RectangleF box = new RectangleF((Width - ms.Width) / 2f - 10, 12, ms.Width + 20, ms.Height + 8);
					using (SolidBrush fill = new SolidBrush(Color.FromArgb(200, 30, 30, 34)))
					{
						g.FillRectangle(fill, box);
					}
					g.DrawString(Message, Font, Brushes.White, box.Left + 10, box.Top + 4);
				}
			}

			// ---- mouse ----

			private int HitHandle(TranslationBox b, Point p)
			{
				RectangleF r = ToScreen(AreaOf(b));
				PointF[] pts = HandlePoints(r);
				for (int i = 0; i < pts.Length; i++)
				{
					if (Math.Abs(p.X - pts[i].X) <= HandleSize && Math.Abs(p.Y - pts[i].Y) <= HandleSize)
					{
						return i + 1;
					}
				}
				return 0;
			}

			private bool HitRotation(TranslationBox b, Point p)
			{
				PointF k = RotationHandle(b);
				return Math.Abs(p.X - k.X) <= 9 && Math.Abs(p.Y - k.Y) <= 9;
			}

			private TranslationBox HitBox(Point p)
			{
				PointF ip = ToImage(p);
				TranslationBox best = null;
				double bestArea = double.MaxValue;
				foreach (TranslationBox b in Boxes)
				{
					if (b.IsHidden)
					{
						continue;
					}
					int[] r = b.Skip ? b.Src : AreaOf(b);
					if (ip.X >= r[0] && ip.X <= r[2] && ip.Y >= r[1] && ip.Y <= r[3])
					{
						double area = (double)(r[2] - r[0]) * (r[3] - r[1]);
						if (area < bestArea)
						{
							best = b;
							bestArea = area;
						}
					}
				}
				return best;
			}

			protected override void OnMouseDown(MouseEventArgs e)
			{
				base.OnMouseDown(e);
				Focus();
				if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
				{
					if (e.Button == MouseButtons.Right && !AddMode)
					{
						//A box under the pointer joins the selection when it is not in it yet.
						TranslationBox under = HitBox(e.Location);
						if (under != null && under != Selected && !Extra.Contains(under))
						{
							Selected = under;
							Extra.Clear();
							SelectionChanged?.Invoke(this, EventArgs.Empty);
							Invalidate();
						}
					}
					drag = Drag.Pan;
					panMoved = false;
					dragStart = e.Location;
					panStart = pan;
					return;
				}
				if (e.Button != MouseButtons.Left || image == null)
				{
					return;
				}
				if (AddMode)
				{
					drag = Drag.Draw;
					dragStart = e.Location;
					drawing = new Rectangle(e.Location, Size.Empty);
					return;
				}
				if ((ModifierKeys & (Keys.Control | Keys.Shift)) != 0)
				{
					TranslationBox toggled = HitBox(e.Location);
					if (toggled != null)
					{
						if (Selected == null)
						{
							Selected = toggled;
						}
						else if (toggled == Selected)
						{
							Selected = Extra.FirstOrDefault();
							if (Selected != null)
							{
								Extra.Remove(Selected);
							}
						}
						else if (!Extra.Remove(toggled))
						{
							Extra.Add(toggled);
						}
						SelectionChanged?.Invoke(this, EventArgs.Empty);
						Invalidate();
					}
					return;
				}
				if (Selected != null && Extra.Count == 0 && !Selected.Skip && Selected.Shape == null)
				{
					if (HitRotation(Selected, e.Location))
					{
						drag = Drag.Rotate;
						startRotation = Selected.Rotation;
						return;
					}
					int h = HitHandle(Selected, e.Location);
					if (h > 0)
					{
						BeginEdit(Drag.Resize, e.Location);
						handle = h;
						return;
					}
				}
				TranslationBox hit = HitBox(e.Location);
				if (hit != Selected || Extra.Count > 0)
				{
					Selected = hit;
					Extra.Clear();
					SelectionChanged?.Invoke(this, EventArgs.Empty);
					Invalidate();
				}
				if (hit != null && !hit.Skip)
				{
					BeginEdit(Drag.Move, e.Location);
				}
			}

			private void BeginEdit(Drag kind, Point at)
			{
				drag = kind;
				dragStart = ToImage(at);
				startRect = (int[])AreaOf(Selected).Clone();
				startShape = Selected.Shape?.Select((int[] part) => (int[])part.Clone()).ToList();
			}

			protected override void OnMouseMove(MouseEventArgs e)
			{
				base.OnMouseMove(e);
				switch (drag)
				{
				case Drag.Pan:
					if (!panMoved && Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y) < 5)
					{
						return;
					}
					panMoved = true;
					Cursor = Cursors.SizeAll;
					pan = new PointF(panStart.X + e.X - dragStart.X, panStart.Y + e.Y - dragStart.Y);
					Invalidate();
					return;
				case Drag.Draw:
					drawing = Rectangle.FromLTRB(Math.Min(e.X, (int)dragStart.X), Math.Min(e.Y, (int)dragStart.Y), Math.Max(e.X, (int)dragStart.X), Math.Max(e.Y, (int)dragStart.Y));
					Invalidate();
					return;
				case Drag.Move:
				case Drag.Resize:
					ApplyDrag(ToImage(e.Location));
					return;
				case Drag.Rotate:
					RotateTo(e.Location);
					return;
				}
				Cursor = AddMode ? Cursors.Cross : CursorAt(e.Location);
			}

			private Cursor CursorAt(Point p)
			{
				if (Selected != null && !Selected.Skip && Selected.Shape == null)
				{
					if (HitRotation(Selected, p))
					{
						return Cursors.Hand;
					}
					switch (HitHandle(Selected, p))
					{
					case 1:
					case 5:
						return Cursors.SizeNWSE;
					case 3:
					case 7:
						return Cursors.SizeNESW;
					case 2:
					case 6:
						return Cursors.SizeNS;
					case 4:
					case 8:
						return Cursors.SizeWE;
					}
				}
				TranslationBox hit = HitBox(p);
				return hit != null && !hit.Skip ? Cursors.SizeAll : Cursors.Default;
			}

			private void ApplyDrag(PointF at)
			{
				if (Selected == null)
				{
					return;
				}
				int dx = (int)Math.Round(at.X - dragStart.X);
				int dy = (int)Math.Round(at.Y - dragStart.Y);
				int[] r = (int[])startRect.Clone();
				if (drag == Drag.Move && startShape != null)
				{
					Selected.Shape = startShape.Select((int[] part) => new[] { part[0] + dx, part[1] + dy, part[2] + dx, part[3] + dy }).ToList();
					BoxChanging?.Invoke(this, EventArgs.Empty);
					Invalidate();
					return;
				}
				if (drag == Drag.Move)
				{
					r[0] += dx;
					r[2] += dx;
					r[1] += dy;
					r[3] += dy;
				}
				else
				{
					//Handles are numbered clockwise from the top left corner.
					if (handle == 1 || handle == 7 || handle == 8)
					{
						r[0] += dx;
					}
					if (handle == 3 || handle == 4 || handle == 5)
					{
						r[2] += dx;
					}
					if (handle == 1 || handle == 2 || handle == 3)
					{
						r[1] += dy;
					}
					if (handle == 5 || handle == 6 || handle == 7)
					{
						r[3] += dy;
					}
					if (r[2] - r[0] < 24)
					{
						if (handle == 1 || handle == 7 || handle == 8)
						{
							r[0] = r[2] - 24;
						}
						else
						{
							r[2] = r[0] + 24;
						}
					}
					if (r[3] - r[1] < 24)
					{
						if (handle == 1 || handle == 2 || handle == 3)
						{
							r[1] = r[3] - 24;
						}
						else
						{
							r[3] = r[1] + 24;
						}
					}
				}
				Selected.Rect = r;
				BoxChanging?.Invoke(this, EventArgs.Empty);
				Invalidate();
			}

			private void RotateTo(Point p)
			{
				RectangleF r = ToScreen(AreaOf(Selected));
				double angle = Math.Atan2(p.X - (r.Left + r.Width / 2f), -(p.Y - (r.Top + r.Height / 2f))) * 180.0 / Math.PI;
				double step = (ModifierKeys & Keys.Shift) != 0 ? 5.0 : 0.5;
				angle = Math.Round(angle / step) * step;
				if (Math.Abs(angle) < 1.0 && step > 1.0)
				{
					angle = 0;
				}
				Selected.Rotation = angle;
				BoxChanging?.Invoke(this, EventArgs.Empty);
				Invalidate();
			}

			protected override void OnMouseUp(MouseEventArgs e)
			{
				base.OnMouseUp(e);
				Drag kind = drag;
				drag = Drag.None;
				Cursor = Cursors.Default;
				if (kind == Drag.Pan && e.Button == MouseButtons.Right && !panMoved && Selected != null)
				{
					ContextRequested?.Invoke(e.Location);
					return;
				}
				switch (kind)
				{
				case Drag.Move:
				case Drag.Resize:
				case Drag.Rotate:
					BoxChanged?.Invoke(this, EventArgs.Empty);
					break;
				case Drag.Draw:
					{
						PointF a = ToImage(drawing.Location);
						PointF b = ToImage(new Point(drawing.Right, drawing.Bottom));
						int[] rect =
						{
							(int)Math.Max(0, Math.Round(a.X)), (int)Math.Max(0, Math.Round(a.Y)),
							(int)Math.Min(imageSize.Width, Math.Round(b.X)), (int)Math.Min(imageSize.Height, Math.Round(b.Y))
						};
						Invalidate();
						if (rect[2] - rect[0] >= 16 && rect[3] - rect[1] >= 16)
						{
							RegionDrawn?.Invoke(rect);
						}
						break;
					}
				}
				Invalidate();
			}

			protected override void OnMouseWheel(MouseEventArgs e)
			{
				base.OnMouseWheel(e);
				if (image == null)
				{
					return;
				}
				PointF before = ToImage(e.Location);
				float factor = e.Delta > 0 ? 1.2f : 1f / 1.2f;
				zoom = Math.Max(0.02f, Math.Min(8f, zoom * factor));
				pan = new PointF(e.X - before.X * zoom, e.Y - before.Y * zoom);
				Invalidate();
			}

			protected override bool IsInputKey(Keys keyData)
			{
				switch (keyData & Keys.KeyCode)
				{
				case Keys.Left:
				case Keys.Right:
				case Keys.Up:
				case Keys.Down:
					return true;
				}
				return base.IsInputKey(keyData);
			}

			protected override void OnKeyDown(KeyEventArgs e)
			{
				base.OnKeyDown(e);
				if (Selected == null || Selected.Skip)
				{
					return;
				}
				int step = Math.Max(1, (int)Math.Round(3f / zoom)) * (e.Shift ? 10 : 1);
				int dx = e.KeyCode == Keys.Left ? -step : e.KeyCode == Keys.Right ? step : 0;
				int dy = e.KeyCode == Keys.Up ? -step : e.KeyCode == Keys.Down ? step : 0;
				if (dx == 0 && dy == 0)
				{
					return;
				}
				Selected.MoveBy(dx, dy);
				e.Handled = true;
				BoxChanging?.Invoke(this, EventArgs.Empty);
				BoxChanged?.Invoke(this, EventArgs.Empty);
				Invalidate();
			}
		}

		private readonly ComicDisplayControl reader;

		private readonly bool dark = ThemeManager.IsDarkModeEnabled;

		private readonly System.Windows.Forms.Timer commitTimer = new System.Windows.Forms.Timer { Interval = 450 };

		private PageCanvas canvas;

		private Label lblPage;

		private Button btPrev;

		private Button btNext;

		private Button btFit;

		private Button btAdd;

		private Button btDelete;

		private ListBox lstBoxes;

		private TextBox txtTranslation;

		private CheckBox chkAuto;

		private NumericUpDown nudSize;

		private Label lblAutoSize;

		private ComboBox cboAlign;

		private NumericUpDown nudRotation;

		private CheckBox chkSkip;

		private Button btReset;

		private Button btAgain;

		private ChoiceTile tileBox;

		private ChoiceTile tilePage;

		private ChoiceTile tileBook;

		private ChoiceTile tileEdits;

		private Label lblCacheInfo;

		private Button btDeleteCache;

		private Button btExport;

		private Label lblStatus;

		private Label lblLed;

		private PictureBox picLed;

		private int page = -1;

		private PageKey key;

		private TranslationPageState state;

		private bool loading;

		private bool pendingCommit;

		private bool disposed;

		private PageTranslationService service;

		public override UIComponent UIComponent => UIComponent.Content;

		public TranslationEditorForm(ComicDisplayControl reader)
		{
			this.reader = reader;
			Text = "Translation Editor";
			AutoScaleDimensions = new SizeF(6f, 13f);
			AutoScaleMode = AutoScaleMode.Font;
			StartPosition = FormStartPosition.Manual;
			ShowInTaskbar = false;
			MinimumSize = new Size(820, 600);
			ClientSize = new Size(1180, 800);
			Rectangle area = Screen.FromControl(reader).WorkingArea;
			Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
			BuildControls();
			commitTimer.Tick += delegate
			{
				commitTimer.Stop();
				Commit();
			};
			Shown += delegate
			{
				Attach();
			};
			FormClosed += delegate
			{
				Detach();
			};
		}

		// ---- layout --------------------------------------------------------------------

		private void BuildControls()
		{
			Panel side = new Panel { Dock = DockStyle.Right, Width = 340 };
			Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 28 };
			canvas = new PageCanvas { Dock = DockStyle.Fill, Name = "canvas" };
			Controls.Add(canvas);
			Controls.Add(side);
			Controls.Add(bottom);
			canvas.BringToFront();

			picLed = new PictureBox { Location = new Point(10, 7), Size = new Size(14, 14), Name = "picLed" };
			lblLed = new Label { Location = new Point(30, 5), Size = new Size(280, 18), Name = "lblLed", TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
			lblStatus = new Label { Location = new Point(316, 5), Size = new Size(600, 18), Name = "lblStatus", AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
			bottom.Controls.AddRange(new Control[] { picLed, lblLed, lblStatus });

			int y = 8;
			lblPage = new Label { Location = new Point(10, y + 4), Size = new Size(120, 18), Name = "lblPage" };
			btPrev = new Button { Text = "<", Location = new Point(134, y), Size = new Size(34, 26), Name = "btPrev" };
			btNext = new Button { Text = ">", Location = new Point(172, y), Size = new Size(34, 26), Name = "btNext" };
			btFit = new Button { Text = "Fit page", Location = new Point(212, y), Size = new Size(70, 26), Name = "btFit" };
			side.Controls.AddRange(new Control[] { lblPage, btPrev, btNext, btFit });
			y += 36;
			btAdd = new Button { Text = "Add text box", Location = new Point(10, y), Size = new Size(150, 28), Name = "btAdd" };
			btDelete = new Button { Text = "Delete box", Location = new Point(166, y), Size = new Size(100, 28), Name = "btDelete" };
			side.Controls.AddRange(new Control[] { btAdd, btDelete });
			y += 38;
			side.Controls.Add(new Label { Text = "Text boxes on this page:", Location = new Point(10, y), Size = new Size(300, 16) });
			y += 18;
			lstBoxes = new ListBox { Location = new Point(10, y), Size = new Size(318, 96), Name = "lstBoxes", IntegralHeight = false, HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended };
			side.Controls.Add(lstBoxes);
			y += 104;
			side.Controls.Add(new Label { Text = "Translation (you can retype it):", Location = new Point(10, y), Size = new Size(300, 16) });
			y += 18;
			txtTranslation = new TextBox { Location = new Point(10, y), Size = new Size(318, 64), Multiline = true, ScrollBars = ScrollBars.Vertical, Name = "txtTranslation", AcceptsReturn = true };
			side.Controls.Add(txtTranslation);
			y += 74;
			side.Controls.Add(new Label { Text = "Font size:", Location = new Point(10, y + 3), Size = new Size(64, 16) });
			chkAuto = new CheckBox { Text = "Auto", Location = new Point(78, y), Size = new Size(54, 22), Name = "chkAuto" };
			nudSize = new NumericUpDown { Location = new Point(136, y), Size = new Size(80, 22), DecimalPlaces = 1, Increment = 0.5m, Minimum = 3m, Maximum = 2000m, Value = 40m, Name = "nudSize" };
			lblAutoSize = new Label { Location = new Point(222, y + 3), Size = new Size(110, 16), Name = "lblAutoSize", ForeColor = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110) };
			side.Controls.AddRange(new Control[] { chkAuto, nudSize, lblAutoSize });
			y += 30;
			side.Controls.Add(new Label { Text = "Align:", Location = new Point(10, y + 3), Size = new Size(64, 16) });
			cboAlign = new ComboBoxEx { Location = new Point(78, y), Size = new Size(138, 21), DropDownStyle = ComboBoxStyle.DropDownList, Name = "cboAlign" };
			cboAlign.Items.AddRange(new object[] { "Left", "Center", "Right" });
			side.Controls.Add(cboAlign);
			y += 30;
			side.Controls.Add(new Label { Text = "Rotate:", Location = new Point(10, y + 3), Size = new Size(64, 16) });
			nudRotation = new NumericUpDown { Location = new Point(78, y), Size = new Size(80, 22), DecimalPlaces = 1, Increment = 0.5m, Minimum = -180m, Maximum = 180m, Name = "nudRotation" };
			side.Controls.Add(nudRotation);
			side.Controls.Add(new Label { Text = "degrees", Location = new Point(164, y + 3), Size = new Size(60, 16) });
			y += 30;
			chkSkip = new CheckBox { Text = "Leave the original text (do not translate)", Location = new Point(10, y), Size = new Size(318, 22), Name = "chkSkip" };
			side.Controls.Add(chkSkip);
			y += 30;
			btReset = new Button { Text = "Reset layout", Location = new Point(10, y), Size = new Size(150, 28), Name = "btReset" };
			btAgain = new Button { Text = "Read && translate again", Location = new Point(166, y), Size = new Size(162, 28), Name = "btAgain" };
			side.Controls.AddRange(new Control[] { btReset, btAgain });
			y += 42;
			btExport = new Button { Text = "Export book...", Location = new Point(10, y), Size = new Size(318, 28), Name = "btExport" };
			side.Controls.Add(btExport);
			y += 38;
			side.Controls.Add(new Label { Text = "Delete translation cache (read and translate again):", Location = new Point(10, y), Size = new Size(318, 16) });
			y += 18;
			tileBox = new ChoiceTile(ChoiceTile.IconKind.Box, "This box") { Location = new Point(10, y), Size = new Size(70, 66), Enabled = false, Name = "tileBox" };
			tilePage = new ChoiceTile(ChoiceTile.IconKind.Page, "This page") { Location = new Point(84, y), Size = new Size(70, 66), Checked = true, Name = "tilePage" };
			tileBook = new ChoiceTile(ChoiceTile.IconKind.Book, "Whole book") { Location = new Point(158, y), Size = new Size(70, 66), Name = "tileBook" };
			tileEdits = new ChoiceTile(ChoiceTile.IconKind.Edits, "My edits too") { Location = new Point(258, y), Size = new Size(70, 66), IsSwitch = true, Name = "tileEdits" };
			side.Controls.AddRange(new Control[] { tileBox, tilePage, tileBook, tileEdits });
			side.Controls.Add(new Label { Text = "+", Location = new Point(230, y + 20), Size = new Size(26, 24), TextAlign = ContentAlignment.MiddleCenter, ForeColor = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110) });
			tileBox.Description = "This box: the selected box is read and translated again. Its position, size and style stay unless My edits too is on. (Select a box first.)";
			tilePage.Description = "This page: the page on screen is read again by the vision model and translated again.";
			tileBook.Description = "Whole book: every page is read and translated again as you read it. This can take a while.";
			tileEdits.Description = "My edits too: also delete what you changed by hand (moved, resized, retyped and added boxes). Off keeps them and applies them again.";
			y += 72;
			lblCacheInfo = new Label { Location = new Point(10, y), Size = new Size(318, 44), Name = "lblCacheInfo", ForeColor = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110) };
			side.Controls.Add(lblCacheInfo);
			y += 46;
			btDeleteCache = new Button { Text = "Delete selected cache", Location = new Point(10, y), Size = new Size(318, 28), Name = "btDeleteCache" };
			side.Controls.Add(btDeleteCache);
			y += 38;
			foreach (ChoiceTile tile in new[] { tileBox, tilePage, tileBook, tileEdits })
			{
				ChoiceTile t = tile;
				t.Hovered += delegate { ShowCacheInfo(); };
				t.CheckedChanged += delegate
				{
					if (!t.IsSwitch && t.Checked)
					{
						foreach (ChoiceTile other in new[] { tileBox, tilePage, tileBook })
						{
							if (other != t)
							{
								other.Checked = false;
							}
						}
					}
					ShowCacheInfo();
				};
			}
			ShowCacheInfo();
			side.Controls.Add(new Label
			{
				Text = "Drag a box to move it, its square handles to resize, the round handle to rotate (Shift: 5 degree steps). Ctrl-click adds boxes to the selection; right-click for options. Arrow keys nudge. Wheel zooms, right-drag pans. Saved with the book.",
				Location = new Point(10, y),
				Size = new Size(318, 60),
				ForeColor = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110)
			});

			canvas.SelectionChanged += delegate
			{
				SyncList();
				SyncControls();
			};
			canvas.BoxChanging += delegate
			{
				pendingCommit = true;
				SyncControls();
			};
			canvas.BoxChanged += delegate
			{
				commitTimer.Stop();
				Commit();
			};
			canvas.RegionDrawn += OnRegionDrawn;
			canvas.ContextRequested += ShowBoxMenu;
			lstBoxes.SelectedIndexChanged += delegate
			{
				if (loading)
				{
					return;
				}
				List<TranslationBox> shown = canvas.Visible.ToList();
				List<TranslationBox> picked = lstBoxes.SelectedIndices.Cast<int>().Where((int i) => i < shown.Count).Select((int i) => shown[i]).ToList();
				canvas.Selected = picked.FirstOrDefault();
				canvas.Extra.Clear();
				foreach (TranslationBox other in picked.Skip(1))
				{
					canvas.Extra.Add(other);
				}
				SyncControls();
				canvas.Invalidate();
			};
			btPrev.Click += delegate { MovePage(-1); };
			btNext.Click += delegate { MovePage(1); };
			btFit.Click += delegate { canvas.Fit(); };
			btAdd.Click += delegate { SetAddMode(!canvas.AddMode); };
			btDelete.Click += delegate { DeleteSelected(); };
			txtTranslation.TextChanged += delegate
			{
				TranslationBox box = canvas.Selected;
				if (loading || box == null)
				{
					return;
				}
				box.Translation = txtTranslation.Text;
				box.TextEdited = true;
				Touch();
			};
			chkAuto.CheckedChanged += delegate
			{
				TranslationBox box = canvas.Selected;
				if (loading || box == null)
				{
					return;
				}
				box.Size = chkAuto.Checked ? 0 : (double)nudSize.Value;
				SyncControls();
				Touch();
			};
			nudSize.ValueChanged += delegate
			{
				TranslationBox box = canvas.Selected;
				if (loading || box == null || chkAuto.Checked)
				{
					return;
				}
				box.Size = (double)nudSize.Value;
				Touch();
			};
			cboAlign.SelectedIndexChanged += delegate
			{
				TranslationBox box = canvas.Selected;
				if (loading || box == null)
				{
					return;
				}
				box.Align = new[] { "left", "center", "right" }[Math.Max(0, cboAlign.SelectedIndex)];
				Touch();
			};
			nudRotation.ValueChanged += delegate
			{
				TranslationBox box = canvas.Selected;
				if (loading || box == null)
				{
					return;
				}
				box.Rotation = (double)nudRotation.Value;
				canvas.Invalidate();
				Touch();
			};
			chkSkip.CheckedChanged += delegate
			{
				TranslationBox box = canvas.Selected;
				if (loading || box == null)
				{
					return;
				}
				box.Skip = chkSkip.Checked;
				SyncList();
				SyncControls();
				canvas.Invalidate();
				Touch();
			};
			btReset.Click += delegate
			{
				TranslationBox box = canvas.Selected;
				if (box != null)
				{
					box.ResetSettings();
					SyncControls();
					canvas.Invalidate();
					CommitNow();
				}
			};
			btAgain.Click += delegate { ReadAgain(canvas.Selected, resetLayout: false); };
			btDeleteCache.Click += delegate
			{
				TranslationCacheScope scope = tileBox.Checked ? TranslationCacheScope.Box : (tileBook.Checked ? TranslationCacheScope.Book : TranslationCacheScope.Page);
				TranslationTools.ApplyCache(this, reader, scope, tileEdits.Checked, canvas.Selected, ReadAgain);
			};
			btExport.Click += delegate { TranslationTools.ShowExportDialog(this, reader); };
		}

		// ---- following the reader --------------------------------------------------------

		private void Attach()
		{
			reader.TranslatePages = true;
			service = reader.TranslationService;
			if (service != null)
			{
				service.PageFinished += service_PageFinished;
				service.StateChanged += service_StateChanged;
			}
			reader.PageChanged += reader_PageChanged;
			reader.BookChanged += reader_BookChanged;
			reader.TranslationStatusChanged += reader_StatusChanged;
			LoadPage(reader.CurrentPage);
			UpdateLed();
		}

		private void Detach()
		{
			disposed = true;
			commitTimer.Stop();
			if (pendingCommit)
			{
				Commit();
			}
			if (service != null)
			{
				service.PageFinished -= service_PageFinished;
				service.StateChanged -= service_StateChanged;
			}
			reader.PageChanged -= reader_PageChanged;
			reader.BookChanged -= reader_BookChanged;
			reader.TranslationStatusChanged -= reader_StatusChanged;
			commitTimer.Dispose();
		}

		private void reader_PageChanged(object sender, BookPageEventArgs e)
		{
			if (pendingCommit)
			{
				commitTimer.Stop();
				Commit();
			}
			LoadPage(reader.CurrentPage);
		}

		private void reader_BookChanged(object sender, EventArgs e)
		{
			pendingCommit = false;
			commitTimer.Stop();
			LoadPage(reader.CurrentPage);
		}

		private void reader_StatusChanged(object sender, EventArgs e)
		{
			UpdateLed();
		}

		private void service_PageFinished(object sender, PageTranslationService.PageKeyEventArgs e)
		{
			if (disposed || !IsHandleCreated)
			{
				return;
			}
			try
			{
				BeginInvoke((Action)delegate
				{
					if (!disposed && object.Equals(e.Key, key))
					{
						ReloadPage(keepView: true);
					}
				});
			}
			catch (InvalidOperationException)
			{
			}
		}

		//Pages were forgotten (cache deleted): ask for this page again.
		private void service_StateChanged(object sender, EventArgs e)
		{
			BeginInvokeSafe(delegate
			{
				pendingCommit = false;
				LoadPage(reader.CurrentPage);
			});
		}

		private void MovePage(int offset)
		{
			if (reader.Book == null)
			{
				return;
			}
			int target = Math.Max(0, Math.Min(reader.Book.ProviderPageCount - 1, reader.CurrentPage + offset));
			reader.Book.CurrentPage = target;
		}

		private void LoadPage(int newPage)
		{
			page = newPage;
			key = newPage >= 0 ? reader.GetTranslationPageKey(newPage) : null;
			canvas.Boxes = new List<TranslationBox>();
			canvas.Selected = null;
			canvas.Extra.Clear();
			state = null;
			pendingCommit = false;
			SetAddMode(false);
			int total = reader.Book?.ProviderPageCount ?? 0;
			lblPage.Text = key == null ? "No book open" : "Page " + (newPage + 1) + " / " + total;
			if (key == null || service == null)
			{
				canvas.SetImage(null, 100, 100, keepView: false);
				SyncList();
				SyncControls();
				return;
			}
			//Make sure the page is on its way, then show what there is.
			using (IItemLock<PageImage> original = reader.GetOriginalPage(newPage))
			{
				if (original?.Item != null && !service.IsKnown(key))
				{
					service.Request(key, original.Item, 0, reader.Book.Comic);
				}
			}
			ReloadPage(keepView: false);
		}

		/// <summary>Takes the page's picture and boxes from the service (the boxes only when nothing of the user's is waiting).</summary>
		private void ReloadPage(bool keepView)
		{
			if (key == null || service == null)
			{
				return;
			}
			state = service.GetState(key);
			Bitmap picture = null;
			int width = 100, height = 100;
			if (state != null && state.Image != null)
			{
				picture = Decode(state.Image);
			}
			if (picture == null)
			{
				using (IItemLock<PageImage> original = reader.GetOriginalPage(page))
				{
					if (original?.Item != null)
					{
						picture = Decode(original.Item.Data) ?? (original.Item.Bitmap != null ? new Bitmap(original.Item.Bitmap) : null);
					}
				}
			}
			if (state != null)
			{
				width = state.Width;
				height = state.Height;
			}
			canvas.SetImage(picture, width, height, keepView);
			if (state != null && !canvas.IsDragging && !pendingCommit)
			{
				List<string> selectedIds = canvas.AllSelected.Select((TranslationBox b) => b.Id).ToList();
				canvas.Boxes = state.Boxes;
				List<TranslationBox> again = state.Boxes.Where((TranslationBox b) => selectedIds.Contains(b.Id)).ToList();
				canvas.Selected = again.FirstOrDefault();
				canvas.Extra.Clear();
				foreach (TranslationBox other in again.Skip(1))
				{
					canvas.Extra.Add(other);
				}
				SyncList();
				SyncControls();
			}
			canvas.Message = state == null ? "Waiting for this page to be translated..." : (state.Waiting ? "Drawing the page..." : null);
			SetStatus(state == null ? "Waiting for the translation of this page..." : (state.Waiting ? "Drawing the page..." : (state.Boxes.Count == 0 ? "No text found on this page. Use Add text box to translate text yourself." : "Changes are saved with the book.")));
			canvas.Invalidate();
		}

		private static Bitmap Decode(byte[] data)
		{
			try
			{
				using (MemoryStream ms = new MemoryStream(data))
				using (Image decoded = Image.FromStream(ms))
				{
					return new Bitmap(decoded);
				}
			}
			catch (Exception)
			{
				return null;
			}
		}

		// ---- editing -----------------------------------------------------------------------

		//The tile under the mouse says what it does; otherwise the current choice does.
		private void ShowCacheInfo()
		{
			ChoiceTile hot = null;
			foreach (ChoiceTile tile in new[] { tileBox, tilePage, tileBook, tileEdits })
			{
				if (tile.ClientRectangle.Contains(tile.PointToClient(Cursor.Position)) && tile.IsHot)
				{
					hot = tile;
				}
			}
			ChoiceTile shown = hot ?? (tileBox.Checked ? tileBox : (tileBook.Checked ? tileBook : tilePage));
			lblCacheInfo.Text = shown.Description;
		}

		private void SetStatus(string text)
		{
			lblStatus.Text = text;
		}

		private void UpdateLed()
		{
			reader.GetTranslationStatus(out int translated, out int total, out bool busy, out string problem);
			Color color = !string.IsNullOrEmpty(problem) ? Color.FromArgb(220, 60, 50) : (busy ? Color.FromArgb(240, 170, 30) : Color.FromArgb(70, 190, 90));
			Bitmap led = new Bitmap(14, 14);
			using (Graphics g = Graphics.FromImage(led))
			using (SolidBrush brush = new SolidBrush(color))
			{
				g.SmoothingMode = SmoothingMode.AntiAlias;
				g.FillEllipse(brush, 1, 1, 11, 11);
			}
			Image old = picLed.Image;
			picLed.Image = led;
			old?.Dispose();
			lblLed.Text = !string.IsNullOrEmpty(problem) ? problem : (busy ? "Translating... " : "") + translated + " / " + total + " pages translated";
		}

		private void SyncList()
		{
			bool was = loading;
			loading = true;
			lstBoxes.BeginUpdate();
			lstBoxes.Items.Clear();
			List<TranslationBox> listed = canvas.Visible.ToList();
			for (int i = 0; i < listed.Count; i++)
			{
				TranslationBox b = listed[i];
				string text = (b.Translation ?? "").Replace("\r", " ").Replace("\n", " ");
				lstBoxes.Items.Add((i + 1) + ".  " + (b.Skip ? "[original] " : "") + (b.Shape != null ? "[merged] " : b.Manual ? "[added] " : "") + (text.Length > 60 ? text.Substring(0, 60) + "..." : text));
			}
			lstBoxes.ClearSelected();
			foreach (TranslationBox sel in canvas.AllSelected)
			{
				int at = listed.IndexOf(sel);
				if (at >= 0)
				{
					lstBoxes.SetSelected(at, true);
				}
			}
			lstBoxes.EndUpdate();
			loading = was;
		}

		private void SyncControls()
		{
			bool was = loading;
			loading = true;
			TranslationBox box = canvas.Selected;
			bool has = box != null;
			txtTranslation.Enabled = chkAuto.Enabled = cboAlign.Enabled = nudRotation.Enabled = chkSkip.Enabled = btReset.Enabled = btAgain.Enabled = has;
			btDelete.Enabled = has && box.Manual;
			if (tileBox != null)
			{
				tileBox.Enabled = has;
				if (!has && tileBox.Checked)
				{
					tilePage.Checked = true;
				}
			}
			nudSize.Enabled = has && !chkAuto.Checked;
			if (has)
			{
				if (txtTranslation.Text != box.Translation)
				{
					txtTranslation.Text = box.Translation ?? "";
				}
				chkAuto.Checked = box.Size <= 0;
				decimal shown = (decimal)(box.Size > 0 ? box.Size : (box.AutoSize > 0 ? box.AutoSize : 40));
				nudSize.Value = Math.Max(nudSize.Minimum, Math.Min(nudSize.Maximum, Math.Round(shown * 2m) / 2m));
				nudSize.Enabled = !chkAuto.Checked;
				lblAutoSize.Text = box.AutoSize > 0 ? "(auto picks " + box.AutoSize.ToString("0.0") + ")" : "";
				cboAlign.SelectedIndex = box.Align == "left" ? 0 : box.Align == "right" ? 2 : 1;
				nudRotation.Value = Math.Max(nudRotation.Minimum, Math.Min(nudRotation.Maximum, (decimal)Math.Round(box.Rotation * 2.0) / 2m));
				chkSkip.Checked = box.Skip;
			}
			else
			{
				txtTranslation.Text = "";
				chkAuto.Checked = true;
				lblAutoSize.Text = "";
				cboAlign.SelectedIndex = 1;
				nudRotation.Value = 0;
				chkSkip.Checked = false;
			}
			loading = was;
		}

		/// <summary>A change was made: draw the page again shortly (many small changes become one redraw).</summary>
		private void Touch()
		{
			pendingCommit = true;
			commitTimer.Stop();
			commitTimer.Start();
			SetStatus("Changed...");
		}

		private void CommitNow()
		{
			commitTimer.Stop();
			pendingCommit = true;
			Commit();
		}

		private void Commit()
		{
			if (!pendingCommit || key == null || service == null)
			{
				return;
			}
			pendingCommit = false;
			bool accepted;
			using (IItemLock<PageImage> original = reader.GetOriginalPage(page))
			{
				accepted = original?.Item != null && service.Edit(key, original.Item, canvas.Boxes);
			}
			SetStatus(accepted ? "Drawing the page..." : "This page is not translated yet; the change was not applied.");
			SyncList();
		}

		private void SetAddMode(bool on)
		{
			canvas.AddMode = on;
			btAdd.Text = on ? "Draw the box on the page..." : "Add text box";
			canvas.Cursor = on ? Cursors.Cross : Cursors.Default;
			if (on)
			{
				SetStatus("Drag a rectangle around the text to translate it.");
			}
		}

		private void OnRegionDrawn(int[] rect)
		{
			SetAddMode(false);
			if (service == null || key == null)
			{
				return;
			}
			SetStatus("Reading and translating the text...");
			canvas.Message = "Reading and translating the text...";
			canvas.Invalidate();
			PageKey requested = key;
			using (IItemLock<PageImage> original = reader.GetOriginalPage(page))
			{
				service.ReadRegion(original?.Item, rect, delegate (TranslationRegion region, string problem)
				{
					BeginInvokeSafe(delegate
					{
						canvas.Message = null;
						if (!object.Equals(requested, key))
						{
							return;
						}
						if (problem != null)
						{
							SetStatus(problem);
							canvas.Invalidate();
							return;
						}
						TranslationBox box = new TranslationBox
						{
							Id = TranslationStore.NewBoxId(),
							Src = rect,
							Rect = (int[])rect.Clone(),
							Text = region.Text,
							Translation = region.Translation,
							Manual = true
						};
						canvas.Boxes.Add(box);
						canvas.Selected = box;
						SyncList();
						SyncControls();
						CommitNow();
						if (string.IsNullOrWhiteSpace(region.Translation))
						{
							SetStatus("No text was read there. Type the translation in the box on the right.");
						}
					});
				});
			}
		}

		private ContextMenuStrip boxMenu;

		/// <summary>Right click on the page: what can be done to the selected box or boxes.</summary>
		private void ShowBoxMenu(Point at)
		{
			List<TranslationBox> boxes = canvas.AllSelected.ToList();
			if (boxes.Count == 0)
			{
				return;
			}
			string many = boxes.Count > 1 ? " (" + boxes.Count + " boxes)" : "";
			boxMenu?.Dispose();
			ContextMenuStrip menu = boxMenu = new ContextMenuStrip();
			ToolStripMenuItem leave = new ToolStripMenuItem("Leave the original text" + many) { Checked = boxes.All((TranslationBox b) => b.Skip) };
			leave.Click += delegate
			{
				bool skip = !boxes.All((TranslationBox b) => b.Skip);
				foreach (TranslationBox b in boxes)
				{
					b.Skip = skip;
				}
				SyncList();
				SyncControls();
				canvas.Invalidate();
				CommitNow();
			};
			ToolStripMenuItem again = new ToolStripMenuItem("Read && translate again" + many);
			again.Click += delegate
			{
				foreach (TranslationBox b in boxes)
				{
					ReadAgain(b, resetLayout: false);
				}
			};
			menu.Items.Add(leave);
			menu.Items.Add(again);
			if (boxes.Count >= 2 && boxes.All((TranslationBox b) => b.Shape == null))
			{
				ToolStripMenuItem merge = new ToolStripMenuItem("Merge into one box (" + boxes.Count + " boxes)");
				merge.Click += delegate { MergeBoxes(boxes); };
				menu.Items.Add(merge);
			}
			if (boxes.Any((TranslationBox b) => b.Shape != null))
			{
				ToolStripMenuItem split = new ToolStripMenuItem("Split the merged box again");
				split.Click += delegate
				{
					foreach (TranslationBox b in boxes.Where((TranslationBox x) => x.Shape != null).ToList())
					{
						Unmerge(b);
					}
				};
				menu.Items.Add(split);
			}
			menu.Show(canvas, at);
		}

		/// <summary>
		/// Folds several boxes into one whose area is the sum of theirs, so the translation wraps inside a bubble made of
		/// two (or more). The new box starts with the translations in reading order; the old ones are hidden and left alone.
		/// </summary>
		private void MergeBoxes(List<TranslationBox> boxes)
		{
			List<TranslationBox> ordered = boxes.OrderBy((TranslationBox b) => b.Src[1]).ThenBy((TranslationBox b) => b.Src[0]).ToList();
			TranslationBox merged = new TranslationBox
			{
				Id = TranslationStore.NewBoxId(),
				Manual = true,
				Src = new[] { ordered.Min((TranslationBox b) => b.Src[0]), ordered.Min((TranslationBox b) => b.Src[1]), ordered.Max((TranslationBox b) => b.Src[2]), ordered.Max((TranslationBox b) => b.Src[3]) },
				Text = string.Join(" ", ordered.Select((TranslationBox b) => b.Text).Where((string t) => !string.IsNullOrWhiteSpace(t))),
				Translation = string.Join(" ", ordered.Select((TranslationBox b) => (b.Translation ?? "").Trim()).Where((string t) => t.Length > 0)),
				Shape = ordered.Select((TranslationBox b) => (int[])b.Area.Clone()).ToList()
			};
			int at = canvas.Boxes.IndexOf(ordered[0]);
			foreach (TranslationBox b in ordered)
			{
				if (b.Manual)
				{
					canvas.Boxes.Remove(b);
				}
				else
				{
					b.Skip = true;
					b.MergedInto = merged.Id;
				}
			}
			canvas.Boxes.Insert(Math.Max(0, Math.Min(at, canvas.Boxes.Count)), merged);
			canvas.Selected = merged;
			canvas.Extra.Clear();
			SyncList();
			SyncControls();
			canvas.Invalidate();
			SetStatus("Merged " + boxes.Count + " boxes. Drag it to move; Read && translate again reads the whole area.".Replace("&&", "&"));
			CommitNow();
		}

		private void Unmerge(TranslationBox merged)
		{
			foreach (TranslationBox b in canvas.Boxes.Where((TranslationBox x) => x.MergedInto == merged.Id).ToList())
			{
				b.MergedInto = "";
				b.Skip = false;
			}
			canvas.Boxes.Remove(merged);
			canvas.Selected = null;
			canvas.Extra.Clear();
			SyncList();
			SyncControls();
			canvas.Invalidate();
			CommitNow();
		}

		private void DeleteSelected()
		{
			TranslationBox box = canvas.Selected;
			if (box == null || !box.Manual)
			{
				return;
			}
			if (box.Shape != null)
			{
				Unmerge(box);
				return;
			}
			canvas.Boxes.Remove(box);
			canvas.Selected = null;
			SyncList();
			SyncControls();
			canvas.Invalidate();
			CommitNow();
		}

		/// <summary>
		/// Reads the original text of a box again and translates it, e.g. after a bad translation. The
		/// layout settings are kept unless asked to be reset.
		/// </summary>
		private void ReadAgain(TranslationBox box, bool resetLayout)
		{
			if (box == null || service == null || key == null)
			{
				return;
			}
			SetStatus("Reading and translating the text again...");
			PageKey requested = key;
			using (IItemLock<PageImage> original = reader.GetOriginalPage(page))
			{
				service.ReadRegion(original?.Item, box.Src, delegate (TranslationRegion region, string problem)
				{
					BeginInvokeSafe(delegate
					{
						if (!object.Equals(requested, key) || !canvas.Boxes.Contains(box))
						{
							return;
						}
						if (problem != null)
						{
							SetStatus(problem);
							return;
						}
						box.Text = region.Text;
						box.Translation = region.Translation;
						box.TextEdited = false;
						if (resetLayout)
						{
							box.ResetSettings();
						}
						SyncList();
						SyncControls();
						canvas.Invalidate();
						CommitNow();
					});
				});
			}
		}

		private void BeginInvokeSafe(Action action)
		{
			if (disposed || !IsHandleCreated)
			{
				return;
			}
			try
			{
				BeginInvoke(action);
			}
			catch (InvalidOperationException)
			{
			}
		}

		protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
		{
			if (keyData == Keys.Delete && canvas.Focused)
			{
				DeleteSelected();
				return true;
			}
			if (keyData == Keys.Escape && canvas.AddMode)
			{
				SetAddMode(false);
				return true;
			}
			return base.ProcessCmdKey(ref msg, keyData);
		}
	}
}
