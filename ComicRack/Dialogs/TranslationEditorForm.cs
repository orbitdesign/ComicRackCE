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
			Draw,
			Corner,
			Vertex,
			Brush
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

			private int cornerIndex;

			private int vertexIndex;

			private int[] startPoints;

			private const float CornerOffset = 16f;

			/// <summary>The shape new boxes are drawn with (an index of TranslationShapes.PresetNames); only used for the preview.</summary>
			public bool NewShaped { get; set; }

			public int NewKind { get; set; }

			public int[] NewCorners { get; set; } = new int[4];

			public bool NewCircular { get; set; }

			/// <summary>Turning a shape turns its lettering too; off keeps the text upright inside the turned shape.</summary>
			public bool TextFollowsShape { get; set; }

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

			public enum BrushKind
			{
				Off,
				Paint,
				Erase
			}

			/// <summary>The protect brush: Paint marks artwork to be left as it is, Erase takes strokes away again.</summary>
			public BrushKind Brush { get; set; }

			/// <summary>The brush's radius in page pixels.</summary>
			public int BrushRadius { get; set; } = 24;

			/// <summary>The strokes of the protect brush on this page.</summary>
			public List<ProtectStroke> Protect { get; set; } = new List<ProtectStroke>();

			/// <summary>A stroke was painted or erased: the page has to be drawn again.</summary>
			public event EventHandler ProtectChanged;

			private List<int> brushPoints;

			private Point? brushAt;

			private bool brushErased;

			/// <summary>The boxes that show: those folded into a merged box are hidden.</summary>
			public IEnumerable<TranslationBox> Visible => Boxes.Where((TranslationBox b) => !b.IsHidden);

			private List<int[]> startShape;

			public bool IsDragging => drag != Drag.None;

			public string Message { get; set; }

			public event EventHandler SelectionChanged;

			public event EventHandler BoxChanging;

			public event EventHandler BoxChanged;

			public event Action<int[]> RegionDrawn;

			/// <summary>The corner dots of a box were dragged: its shape is now the one to remember.</summary>
			public event Action<TranslationBox> CornersEdited;

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
				PointF centre = new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f);
				return Turn(new PointF(centre.X, r.Top - 26f), centre, b.ShapeAngle);
			}

			private PointF[] HandlePoints(RectangleF r, double angle)
			{
				float cx = r.Left + r.Width / 2f;
				float cy = r.Top + r.Height / 2f;
				PointF[] points =
				{
					new PointF(r.Left, r.Top), new PointF(cx, r.Top), new PointF(r.Right, r.Top),
					new PointF(r.Right, cy), new PointF(r.Right, r.Bottom), new PointF(cx, r.Bottom),
					new PointF(r.Left, r.Bottom), new PointF(r.Left, cy)
				};
				return points.Select((PointF p) => Turn(p, new PointF(cx, cy), angle)).ToArray();
			}

			/// <summary>A point turned clockwise about a centre (in the units of the point).</summary>
			private static PointF Turn(PointF p, PointF centre, double degrees)
			{
				if (Math.Abs(degrees) < 0.05)
				{
					return p;
				}
				double s = Math.Sin(degrees * Math.PI / 180.0), c = Math.Cos(degrees * Math.PI / 180.0);
				return new PointF((float)(centre.X + (p.X - centre.X) * c - (p.Y - centre.Y) * s), (float)(centre.Y + (p.X - centre.X) * s + (p.Y - centre.Y) * c));
			}

			/// <summary>The part (rectangle plus form and angle) a plain box stands for.</summary>
			public static int[] PartOf(TranslationBox b)
			{
				if (b.Skip)
				{
					return AreaOf(b);
				}
				if (b.HasShape)
				{
					return TranslationShapes.MakePart(AreaOf(b), b.Form, b.Corners, b.Circular, b.ShapeAngle, b.Form == TranslationShapes.FreePolygon ? b.Points : null);
				}
				if (HasAutoOutline(b))
				{
					return TranslationShapes.MakePart(AreaOf(b), TranslationShapes.FreePolygon, new int[4], false, 0, b.Outline);
				}
				return AreaOf(b);
			}

			/// <summary>True for a box the helper lettered in a shape of its own (a traced bubble, an oval) that the user has not changed.</summary>
			public static bool HasAutoOutline(TranslationBox b)
			{
				return b != null && b.Outline != null && b.Outline.Length >= 6 && !b.HasShape && b.Shape == null && !b.Skip && b.Rect == null;
			}

			/// <summary>
			/// Makes the helper's own area of a box the box's own shape, so that it can be moved, resized and its points dragged:
			/// an oval becomes an oval box, any other shape a free polygon. Called when the user starts to change such a box.
			/// </summary>
			public static void AdoptAutoShape(TranslationBox b, bool asPolygon)
			{
				if (!HasAutoOutline(b))
				{
					return;
				}
				if (b.AutoKind == "oval" && !asPolygon)
				{
					b.Rect = (int[])b.Area.Clone();
					b.Form = TranslationShapes.Round;
					b.Corners = new[] { 100, 100, 100, 100 };
					b.Circular = false;
				}
				else
				{
					b.Points = (int[])b.Outline.Clone();
					b.Form = TranslationShapes.FreePolygon;
					b.Corners = new int[4];
					b.Circular = false;
					b.Rect = TranslationShapes.PointsBounds(b.Points);
				}
			}

			private PointF[] ScreenPolygon(int[] part)
			{
				return TranslationShapes.Polygon(part).Select((PointF p) => new PointF(p.X * zoom + pan.X, p.Y * zoom + pan.Y)).ToArray();
			}

			private static PointF CentreOf(int[] r)
			{
				return new PointF((r[0] + r[2]) / 2f, (r[1] + r[3]) / 2f);
			}

			/// <summary>Where the handle of a corner sits (screen), or of the point of a triangle.</summary>
			private PointF CornerHandle(TranslationBox b, int i)
			{
				int[] a = AreaOf(b);
				RectangleF r = ToScreen(a);
				PointF centre = new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f);
				PointF at;
				if (TranslationShapes.IsTriangle(b.Form))
				{
					double t = b.Corners[0] > 0 ? b.Corners[0] / 100.0 : 0.5;
					switch (b.Form)
					{
					case TranslationShapes.TriangleUp:
						at = new PointF((float)(r.Left + t * r.Width), r.Top);
						break;
					case TranslationShapes.TriangleDown:
						at = new PointF((float)(r.Left + t * r.Width), r.Bottom);
						break;
					case TranslationShapes.TriangleLeft:
						at = new PointF(r.Left, (float)(r.Top + t * r.Height));
						break;
					default:
						at = new PointF(r.Right, (float)(r.Top + t * r.Height));
						break;
					}
					return Turn(at, centre, b.ShapeAngle);
				}
				TranslationShapes.CornerReach(a, b.Corners[i], b.Circular, out double rx, out double ry);
				float dx = (float)(rx * zoom) + CornerOffset;
				float dy = (float)(ry * zoom) + CornerOffset;
				switch (i)
				{
				case 0:
					at = new PointF(r.Left + dx, r.Top + dy);
					break;
				case 1:
					at = new PointF(r.Right - dx, r.Top + dy);
					break;
				case 2:
					at = new PointF(r.Right - dx, r.Bottom - dy);
					break;
				default:
					at = new PointF(r.Left + dx, r.Bottom - dy);
					break;
				}
				return Turn(at, centre, b.ShapeAngle);
			}

			/// <summary>True for a single free polygon whose points can be dragged.</summary>
			public static bool HasVertices(TranslationBox b)
			{
				return b != null && !b.Skip && b.Shape == null && ((b.Form == TranslationShapes.FreePolygon && b.Points != null && b.Points.Length >= 6) || HasAutoOutline(b));
			}

			/// <summary>The corner points to show: the box's own, or those of the helper's area.</summary>
			private static int[] PointsOf(TranslationBox b)
			{
				return b.Form == TranslationShapes.FreePolygon && b.Points != null ? b.Points : b.Outline;
			}

			/// <summary>Where the points of a polygon are on screen (turned with the shape).</summary>
			private PointF[] VertexScreen(TranslationBox b)
			{
				RectangleF r = ToScreen(AreaOf(b));
				PointF centre = new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f);
				List<PointF> list = new List<PointF>();
				int[] shown = PointsOf(b);
				for (int i = 0; i + 1 < shown.Length; i += 2)
				{
					list.Add(Turn(new PointF(shown[i] * zoom + pan.X, shown[i + 1] * zoom + pan.Y), centre, b.ShapeAngle));
				}
				return list.ToArray();
			}

			/// <summary>The index of the polygon point under the pointer, or -1.</summary>
			public int VertexAt(Point p)
			{
				if (!HasVertices(Selected) || Extra.Count > 0)
				{
					return -1;
				}
				PointF[] v = VertexScreen(Selected);
				for (int i = 0; i < v.Length; i++)
				{
					if (Math.Abs(p.X - v[i].X) <= 7 && Math.Abs(p.Y - v[i].Y) <= 7)
					{
						return i;
					}
				}
				return -1;
			}

			/// <summary>True when the pointer is on an edge of the polygon (not on a point); after is the index of the point the edge starts at.</summary>
			public bool EdgeAt(Point p, out int after)
			{
				after = -1;
				if (!HasVertices(Selected) || Extra.Count > 0 || VertexAt(p) >= 0)
				{
					return false;
				}
				PointF[] v = VertexScreen(Selected);
				for (int i = 0; i < v.Length; i++)
				{
					PointF a = v[i], b = v[(i + 1) % v.Length];
					double dx = b.X - a.X, dy = b.Y - a.Y;
					double len2 = dx * dx + dy * dy;
					if (len2 < 1)
					{
						continue;
					}
					double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
					double px = a.X + t * dx, py = a.Y + t * dy;
					if ((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py) <= 49)
					{
						after = i;
						return true;
					}
				}
				return false;
			}

			/// <summary>Turns the angle of a polygon into its points (so a point can be edited in page coordinates).</summary>
			private static void BakeAngle(TranslationBox b)
			{
				if (b.Points == null || Math.Abs(b.ShapeAngle) < 0.05)
				{
					return;
				}
				PointF centre = CentreOf(b.Rect ?? TranslationShapes.PointsBounds(b.Points));
				int[] pts = (int[])b.Points.Clone();
				for (int i = 0; i + 1 < pts.Length; i += 2)
				{
					PointF q = Turn(new PointF(pts[i], pts[i + 1]), centre, b.ShapeAngle);
					pts[i] = (int)Math.Round(q.X);
					pts[i + 1] = (int)Math.Round(q.Y);
				}
				if (Math.Abs(b.Rotation - b.ShapeAngle) < 0.05)
				{
					b.Rotation = 0;
				}
				b.ShapeAngle = 0;
				b.Points = pts;
				b.Rect = TranslationShapes.PointsBounds(pts);
			}

			/// <summary>Adds a point on the edge that starts at point `after`, where the pointer is.</summary>
			public void InsertVertex(int after, Point at)
			{
				if (!HasVertices(Selected))
				{
					return;
				}
				AdoptAutoShape(Selected, true);
				BakeAngle(Selected);
				PointF q = ToImage(at);
				int[] pts = Selected.Points;
				int n = pts.Length / 2;
				int ax = pts[2 * after], ay = pts[2 * after + 1];
				int bx = pts[2 * ((after + 1) % n)], by = pts[2 * ((after + 1) % n) + 1];
				double dx = bx - ax, dy = by - ay;
				double len2 = Math.Max(1, dx * dx + dy * dy);
				double t = Math.Max(0.1, Math.Min(0.9, ((q.X - ax) * dx + (q.Y - ay) * dy) / len2));
				List<int> list = pts.ToList();
				list.InsertRange(2 * (after + 1), new[] { (int)Math.Round(ax + t * dx), (int)Math.Round(ay + t * dy) });
				Selected.Points = list.ToArray();
				Selected.Rect = TranslationShapes.PointsBounds(Selected.Points);
				BoxChanging?.Invoke(this, EventArgs.Empty);
				BoxChanged?.Invoke(this, EventArgs.Empty);
				Invalidate();
			}

			/// <summary>Takes a point away (a polygon keeps at least three).</summary>
			public void RemoveVertex(int index)
			{
				if (!HasVertices(Selected) || PointsOf(Selected).Length <= 6 || index < 0 || 2 * index + 1 >= PointsOf(Selected).Length)
				{
					return;
				}
				AdoptAutoShape(Selected, true);
				BakeAngle(Selected);
				List<int> list = Selected.Points.ToList();
				list.RemoveRange(2 * index, 2);
				Selected.Points = list.ToArray();
				Selected.Rect = TranslationShapes.PointsBounds(Selected.Points);
				BoxChanging?.Invoke(this, EventArgs.Empty);
				BoxChanged?.Invoke(this, EventArgs.Empty);
				Invalidate();
			}

			private static int[] RemapPoints(int[] points, int[] from, int[] to)
			{
				double sx = (double)(to[2] - to[0]) / Math.Max(1, from[2] - from[0]);
				double sy = (double)(to[3] - to[1]) / Math.Max(1, from[3] - from[1]);
				int[] result = new int[points.Length];
				for (int i = 0; i + 1 < points.Length; i += 2)
				{
					result[i] = (int)Math.Round(to[0] + (points[i] - from[0]) * sx);
					result[i + 1] = (int)Math.Round(to[1] + (points[i + 1] - from[1]) * sy);
				}
				return result;
			}

			private void DragVertex(PointF at)
			{
				if (startPoints == null || vertexIndex < 0 || 2 * vertexIndex + 1 >= startPoints.Length)
				{
					return;
				}
				int[] pts = (int[])startPoints.Clone();
				pts[2 * vertexIndex] = (int)Math.Max(0, Math.Min(imageSize.Width, Math.Round(at.X)));
				pts[2 * vertexIndex + 1] = (int)Math.Max(0, Math.Min(imageSize.Height, Math.Round(at.Y)));
				Selected.Points = pts;
				Selected.Rect = TranslationShapes.PointsBounds(pts);
				BoxChanging?.Invoke(this, EventArgs.Empty);
				Invalidate();
			}

			private bool CanShapeCorners(TranslationBox b)
			{
				if (b == null || b.Skip || b.Shape != null || b.Form == TranslationShapes.FreePolygon || HasAutoOutline(b))
				{
					return false;
				}
				int[] a = AreaOf(b);
				return (a[2] - a[0]) * zoom >= 80 && (a[3] - a[1]) * zoom >= 80;
			}

			private int HitCorner(TranslationBox b, Point p)
			{
				if (!CanShapeCorners(b))
				{
					return -1;
				}
				int count = TranslationShapes.IsTriangle(b.Form) ? 1 : 4;
				for (int i = 0; i < count; i++)
				{
					PointF h = CornerHandle(b, i);
					if (Math.Abs(p.X - h.X) <= 8 && Math.Abs(p.Y - h.Y) <= 8)
					{
						return i;
					}
				}
				return -1;
			}


			/// <summary>Draws one protect stroke over the page as a translucent blue band.</summary>
			private void PaintStroke(Graphics g, int[] points, int radius)
			{
				if (points == null || points.Length < 2)
				{
					return;
				}
				float width = Math.Max(3f, 2f * radius * zoom);
				PointF[] line = new PointF[points.Length / 2];
				for (int i = 0; i < line.Length; i++)
				{
					line[i] = new PointF(points[2 * i] * zoom + pan.X, points[2 * i + 1] * zoom + pan.Y);
				}
				Color color = Color.FromArgb(105, 40, 130, 255);
				if (line.Length == 1)
				{
					using (SolidBrush fill = new SolidBrush(color))
					{
						g.FillEllipse(fill, line[0].X - width / 2f, line[0].Y - width / 2f, width, width);
					}
					return;
				}
				using (Pen pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
				{
					g.DrawLines(pen, line);
				}
			}

			private void BrushTo(PointF at)
			{
				if (Brush == BrushKind.Erase)
				{
					int removed = Protect.RemoveAll((ProtectStroke s) => s.Touches(at.X, at.Y, BrushRadius));
					if (removed > 0)
					{
						brushErased = true;
					}
					return;
				}
				if (brushPoints == null)
				{
					return;
				}
				int n = brushPoints.Count;
				int x = (int)Math.Round(at.X), y = (int)Math.Round(at.Y);
				if (n >= 2)
				{
					double step = Math.Max(2.0, BrushRadius / 3.0);
					double dx = x - brushPoints[n - 2], dy = y - brushPoints[n - 1];
					if (dx * dx + dy * dy < step * step)
					{
						return;
					}
				}
				brushPoints.Add(x);
				brushPoints.Add(y);
			}

			protected override void OnMouseLeave(EventArgs e)
			{
				base.OnMouseLeave(e);
				if (brushAt.HasValue)
				{
					brushAt = null;
					Invalidate();
				}
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
				foreach (ProtectStroke stroke in Protect)
				{
					PaintStroke(g, stroke.Points, stroke.Radius);
				}
				if (drag == Drag.Brush && Brush == BrushKind.Paint && brushPoints != null)
				{
					PaintStroke(g, brushPoints.ToArray(), BrushRadius);
				}
				if (Brush != BrushKind.Off && brushAt.HasValue && image != null)
				{
					float r = Math.Max(3f, BrushRadius * zoom);
					using (Pen ring = new Pen(Brush == BrushKind.Erase ? Color.FromArgb(235, 255, 80, 80) : Color.FromArgb(235, 255, 255, 255), 1.5f))
					using (Pen shadow = new Pen(Color.FromArgb(160, 0, 0, 0), 3f))
					{
						g.DrawEllipse(shadow, brushAt.Value.X - r, brushAt.Value.Y - r, 2 * r, 2 * r);
						g.DrawEllipse(ring, brushAt.Value.X - r, brushAt.Value.Y - r, 2 * r, 2 * r);
					}
				}
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
						if (box.Shape != null && !box.Skip)
						{
							foreach (int[] part in box.Shape)
							{
								if (part.Length >= TranslationShapes.PartLength)
								{
									g.DrawPolygon(pen, ScreenPolygon(part));
								}
								else
								{
									RectangleF pr = ToScreen(part);
									g.DrawRectangle(pen, pr.X, pr.Y, pr.Width, pr.Height);
								}
							}
						}
						else if ((box.HasShape || HasAutoOutline(box)) && !box.Skip)
						{
							g.DrawPolygon(pen, ScreenPolygon(PartOf(box)));
						}
						else
						{
							g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
						}
						if (box.Rotation != 0 && !box.Skip && box.Shape == null && Math.Abs(box.Rotation - box.ShapeAngle) >= 0.05)
						{
							//The lettering is turned differently from the shape: show the direction of the text.
							using (Matrix m = new Matrix())
							using (Pen faint = new Pen(Color.FromArgb(150, color), 1f) { DashStyle = DashStyle.Dot })
							{
								m.RotateAt((float)box.Rotation, new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f));
								PointF[] pts = { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom) };
								m.TransformPoints(pts);
								g.DrawPolygon(faint, pts);
							}
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
						PointF top = Turn(new PointF(r.Left + r.Width / 2f, r.Top), new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f), box.ShapeAngle);
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
						foreach (PointF h in HandlePoints(r, box.ShapeAngle))
						{
							g.FillRectangle(Brushes.White, h.X - HandleSize / 2f, h.Y - HandleSize / 2f, HandleSize, HandleSize);
							using (Pen ring = new Pen(color, 1.5f))
							{
								g.DrawRectangle(ring, h.X - HandleSize / 2f, h.Y - HandleSize / 2f, HandleSize, HandleSize);
							}
						}
						if (HasVertices(box))
						{
							//A free polygon: a square dot on every point (drag it); double-click an edge to add a point, right-click a point to remove it.
							foreach (PointF v in VertexScreen(box))
							{
								g.FillRectangle(Brushes.White, v.X - 4, v.Y - 4, 8, 8);
								using (Pen ring = new Pen(Color.FromArgb(255, 150, 20), 2f))
								{
									g.DrawRectangle(ring, v.X - 4, v.Y - 4, 8, 8);
								}
							}
						}
						if (CanShapeCorners(box))
						{
							//Round handles: drag one to round, cut or move the point of the corner it belongs to (Shift: all corners).
							int count = TranslationShapes.IsTriangle(box.Form) ? 1 : 4;
							for (int ci = 0; ci < count; ci++)
							{
								PointF h = CornerHandle(box, ci);
								using (SolidBrush dot = new SolidBrush(Color.FromArgb(255, 150, 20)))
								{
									g.FillEllipse(dot, h.X - 5, h.Y - 5, 10, 10);
								}
								g.DrawEllipse(Pens.White, h.X - 5, h.Y - 5, 10, 10);
							}
						}
					}
				}
				if (drag == Drag.Draw)
				{
					using (Pen pen = new Pen(Color.FromArgb(70, 150, 255), 2f) { DashStyle = DashStyle.Dash })
					using (SolidBrush fill = new SolidBrush(Color.FromArgb(40, 70, 150, 255)))
					{
						if (NewShaped && drawing.Width > 4 && drawing.Height > 4)
							{
								PointF a = ToImage(drawing.Location);
								PointF z = ToImage(new Point(drawing.Right, drawing.Bottom));
								PointF[] poly = ScreenPolygon(TranslationShapes.MakePart(new[] { (int)a.X, (int)a.Y, (int)z.X, (int)z.Y }, NewKind == TranslationShapes.FreePolygon ? TranslationShapes.Round : NewKind, NewKind == TranslationShapes.FreePolygon ? new int[4] : NewCorners, NewCircular));
							g.FillPolygon(fill, poly);
							g.DrawPolygon(pen, poly);
						}
						else
						{
							g.FillRectangle(fill, drawing);
							g.DrawRectangle(pen, drawing);
						}
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
				PointF[] pts = HandlePoints(r, b.ShapeAngle);
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
					bool inside = b.Skip ? ip.X >= r[0] && ip.X <= r[2] && ip.Y >= r[1] && ip.Y <= r[3]
						: (b.Shape != null ? b.Shape.Any((int[] part) => TranslationShapes.Contains(part, ip)) : TranslationShapes.Contains(PartOf(b), ip));
					if (inside)
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
				if (Brush != BrushKind.Off)
				{
					drag = Drag.Brush;
					brushErased = false;
					brushPoints = Brush == BrushKind.Paint ? new List<int>() : null;
					BrushTo(ToImage(e.Location));
					brushAt = e.Location;
					Invalidate();
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
					int vertex = VertexAt(e.Location);
						if (vertex >= 0)
						{
							AdoptAutoShape(Selected, true);
							BakeAngle(Selected);
						BeginEdit(Drag.Vertex, e.Location);
						vertexIndex = vertex;
						return;
					}
					int corner = HitCorner(Selected, e.Location);
					if (corner >= 0)
					{
						BeginEdit(Drag.Corner, e.Location);
						cornerIndex = corner;
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
				startPoints = (int[])Selected.Points?.Clone();
			}

			protected override void OnMouseMove(MouseEventArgs e)
			{
				base.OnMouseMove(e);
				if (Brush != BrushKind.Off)
				{
					brushAt = e.Location;
					if (drag == Drag.Brush)
					{
						BrushTo(ToImage(e.Location));
					}
					Invalidate();
					if (drag == Drag.Brush || drag == Drag.None)
					{
						Cursor = Cursors.Cross;
					}
				}
				switch (drag)
				{
				case Drag.Brush:
					return;
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
				case Drag.Corner:
				case Drag.Vertex:
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
					if (HitRotation(Selected, p) || HitCorner(Selected, p) >= 0 || VertexAt(p) >= 0)
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
				if ((drag == Drag.Move || drag == Drag.Resize || drag == Drag.Corner) && HasAutoOutline(Selected))
				{
					//The helper's own area is the box's shape from now on; what is dragged starts from it.
					AdoptAutoShape(Selected, false);
					startRect = (int[])AreaOf(Selected).Clone();
					startPoints = (int[])Selected.Points?.Clone();
				}
				int dx = (int)Math.Round(at.X - dragStart.X);
				int dy = (int)Math.Round(at.Y - dragStart.Y);
				int[] r = (int[])startRect.Clone();
				if (drag == Drag.Move && startShape != null)
				{
					Selected.Shape = startShape.Select((int[] part) => { int[] moved = (int[])part.Clone(); moved[0] += dx; moved[1] += dy; moved[2] += dx; moved[3] += dy; return moved; }).ToList();
					BoxChanging?.Invoke(this, EventArgs.Empty);
					Invalidate();
					return;
				}
				if (drag == Drag.Vertex)
				{
					DragVertex(at);
					return;
				}
				if (drag == Drag.Corner)
				{
					DragCorner(at, r);
					return;
				}
				if (drag == Drag.Resize)
				{
					ResizeTurned(dx, dy, r);
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
				if (drag == Drag.Move && Selected.Form == TranslationShapes.FreePolygon && startPoints != null)
				{
					Selected.Points = startPoints.Select((int v, int i) => v + (i % 2 == 0 ? r[0] - startRect[0] : r[1] - startRect[1])).ToArray();
				}
				Selected.Rect = r;
				BoxChanging?.Invoke(this, EventArgs.Empty);
				Invalidate();
			}

			/// <summary>
			/// Resizes the selected box by a handle. The box may be turned, so the pointer's movement is taken along the
			/// box's own sides, and the side opposite the handle stays where it is on the page.
			/// </summary>
			private void ResizeTurned(int dx, int dy, int[] r)
			{
				double a = Selected.ShapeAngle * Math.PI / 180.0;
				double cos = Math.Cos(a), sin = Math.Sin(a);
				double ldx = dx * cos + dy * sin;
				double ldy = -dx * sin + dy * cos;
				//Handles are numbered clockwise from the top left corner.
				int sx = handle == 1 || handle == 7 || handle == 8 ? -1 : (handle == 3 || handle == 4 || handle == 5 ? 1 : 0);
				int sy = handle == 1 || handle == 2 || handle == 3 ? -1 : (handle == 5 || handle == 6 || handle == 7 ? 1 : 0);
				double w0 = r[2] - r[0], h0 = r[3] - r[1];
				double w = sx != 0 ? Math.Max(24, w0 + sx * ldx) : w0;
				double h = sy != 0 ? Math.Max(24, h0 + sy * ldy) : h0;
				double lcx = sx * (w - w0) / 2.0, lcy = sy * (h - h0) / 2.0;
				double cx = (r[0] + r[2]) / 2.0 + lcx * cos - lcy * sin;
				double cy = (r[1] + r[3]) / 2.0 + lcx * sin + lcy * cos;
				int[] resized = { (int)Math.Round(cx - w / 2.0), (int)Math.Round(cy - h / 2.0), (int)Math.Round(cx + w / 2.0), (int)Math.Round(cy + h / 2.0) };
				if (Selected.Form == TranslationShapes.FreePolygon && startPoints != null)
				{
					Selected.Points = RemapPoints(startPoints, r, resized);
				}
				Selected.Rect = resized;
				BoxChanging?.Invoke(this, EventArgs.Empty);
				Invalidate();
			}

			/// <summary>The pointer is on a corner handle: set that corner's size (Shift: all four), or move a triangle's point.</summary>
			private void DragCorner(PointF at, int[] r)
			{
				double off = CornerOffset / zoom;
				if (Selected.Rect == null)
				{
					Selected.Rect = (int[])r.Clone();
				}
				if (Selected.Corners == null || Selected.Corners.Length != 4)
				{
					Selected.Corners = new int[4];
				}
				//Work in the box's own frame.
				at = Turn(at, CentreOf(r), -Selected.ShapeAngle);
				if (TranslationShapes.IsTriangle(Selected.Form))
				{
					bool horizontal = Selected.Form == TranslationShapes.TriangleUp || Selected.Form == TranslationShapes.TriangleDown;
					double span = horizontal ? r[2] - r[0] : r[3] - r[1];
					double along = horizontal ? at.X - r[0] : at.Y - r[1];
					Selected.Corners[0] = (int)Math.Max(5, Math.Min(95, Math.Round(100.0 * along / Math.Max(1, span))));
				}
				else
				{
					double insideX = cornerIndex == 0 || cornerIndex == 3 ? at.X - r[0] : r[2] - at.X;
					double insideY = cornerIndex <= 1 ? at.Y - r[1] : r[3] - at.Y;
					int c = TranslationShapes.CornerFromPointer(r, insideX - off, insideY - off, Selected.Circular);
					if ((ModifierKeys & Keys.Shift) != 0)
					{
						Selected.Corners = new[] { c, c, c, c };
					}
					else
					{
						Selected.Corners[cornerIndex] = c;
					}
				}
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
				AdoptAutoShape(Selected, false);
				//The shape turns; the lettering turns with it only when asked to, otherwise it stays upright inside the turned shape.
				if (Selected.Rect == null)
				{
					Selected.Rect = (int[])AreaOf(Selected).Clone();
				}
				Selected.ShapeAngle = angle;
				if (TextFollowsShape)
				{
					Selected.Rotation = angle;
				}
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
				case Drag.Brush:
					{
						bool changed = brushErased;
						if (Brush == BrushKind.Paint && brushPoints != null && brushPoints.Count >= 2)
						{
							Protect.Add(new ProtectStroke { Radius = BrushRadius, Points = brushPoints.ToArray() });
							changed = true;
						}
						brushPoints = null;
						brushErased = false;
						Cursor = Cursors.Cross;
						Invalidate();
						if (changed)
						{
							ProtectChanged?.Invoke(this, EventArgs.Empty);
						}
						break;
					}
				case Drag.Move:
				case Drag.Resize:
				case Drag.Rotate:
				case Drag.Corner:
				case Drag.Vertex:
					if (kind == Drag.Corner)
					{
						CornersEdited?.Invoke(Selected);
					}
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

			protected override void OnMouseDoubleClick(MouseEventArgs e)
			{
				base.OnMouseDoubleClick(e);
				if (e.Button == MouseButtons.Left && !AddMode && EdgeAt(e.Location, out int after))
				{
					InsertVertex(after, e.Location);
				}
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

		private Button btBrush;

		private Button btIdentify;

		private Button btEraser;

		private Button btClearProtect;

		private TrackBar tbBrush;

		private Label lblBrush;

		private Button btAddShape;

		private int newPreset;

		//The shape new boxes are drawn with: the last one used (a preset, or -1 for corners set by hand). Kept in the settings.
		private int newKind;

		private int[] newCorners = new int[4];

		private bool newCircular;

		private ComboBox cboShape;

		private CheckBox chkCircular;

		private NumericUpDown[] nudCorner = new NumericUpDown[4];

		private NumericUpDown nudShapeAngle;

		private CheckBox chkTurnText;

		private Button btDelete;

		private Button btRestore;

		private const string DeletedMark = "deleted";

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
			Panel side = new Panel { Dock = DockStyle.Right, Width = 360, AutoScroll = true };
			Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 28 };
			canvas = new PageCanvas { Dock = DockStyle.Fill, Name = "canvas" };
			Controls.Add(canvas);
			Controls.Add(side);
			Controls.Add(bottom);
			canvas.BringToFront();

			picLed = new PictureBox { Location = new Point(8, 4), Size = new Size(20, 20), Name = "picLed", SizeMode = PictureBoxSizeMode.CenterImage };
			lblLed = new Label { Location = new Point(34, 5), Size = new Size(276, 18), Name = "lblLed", TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
			lblStatus = new Label { Location = new Point(316, 5), Size = new Size(600, 18), Name = "lblStatus", AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
			bottom.Controls.AddRange(new Control[] { picLed, lblLed, lblStatus });

			int y = 8;
			lblPage = new Label { Location = new Point(10, y + 4), Size = new Size(120, 18), Name = "lblPage" };
			btPrev = new Button { Text = "<", Location = new Point(134, y), Size = new Size(34, 26), Name = "btPrev" };
			btNext = new Button { Text = ">", Location = new Point(172, y), Size = new Size(34, 26), Name = "btNext" };
			btFit = new Button { Text = "Fit page", Location = new Point(212, y), Size = new Size(70, 26), Name = "btFit" };
			side.Controls.AddRange(new Control[] { lblPage, btPrev, btNext, btFit });
			y += 36;
			btAdd = new Button { Text = "Add text box", Location = new Point(10, y), Size = new Size(118, 28), Name = "btAdd" };
			btAddShape = new Button { Text = "\u25be", Location = new Point(130, y), Size = new Size(30, 28), Name = "btAddShape" };
			btDelete = new Button { Text = "Delete box", Location = new Point(166, y), Size = new Size(100, 28), Name = "btDelete" };
			btRestore = new Button { Text = "Restore", Location = new Point(270, y), Size = new Size(58, 28), Name = "btRestore" };
			new ToolTip().SetToolTip(btDelete, "Delete the selected boxes (Delete key). Their original text is left as drawn. Restore brings deleted boxes back.");
			new ToolTip().SetToolTip(btRestore, "Bring back the boxes deleted on this page");
			side.Controls.AddRange(new Control[] { btAdd, btAddShape, btDelete, btRestore });
			y += 38;
			btBrush = new Button { Text = "Protect brush", Location = new Point(10, y), Size = new Size(118, 28), Name = "btBrush" };
			btEraser = new Button { Text = "Erase strokes", Location = new Point(130, y), Size = new Size(96, 28), Name = "btEraser" };
			btClearProtect = new Button { Text = "Clear all", Location = new Point(230, y), Size = new Size(98, 28), Name = "btClearProtect" };
			new ToolTip().SetToolTip(btBrush, "Paint over artwork that must stay as it is (a head, a hand): the helper does not wipe it as text and does not letter over it. The bubble outline follows.");
			new ToolTip().SetToolTip(btEraser, "Rub out protect strokes you painted");
			new ToolTip().SetToolTip(btClearProtect, "Remove every protect stroke on this page");
			side.Controls.AddRange(new Control[] { btBrush, btEraser, btClearProtect });
			y += 34;
			side.Controls.Add(new Label { Text = "Brush size:", Location = new Point(10, y + 4), Size = new Size(64, 16) });
			tbBrush = new TrackBar { Location = new Point(78, y), Size = new Size(190, 30), Minimum = 4, Maximum = 120, TickFrequency = 20, Value = 24, SmallChange = 2, LargeChange = 10, Name = "tbBrush", AutoSize = false };
			lblBrush = new Label { Location = new Point(272, y + 4), Size = new Size(56, 16), Name = "lblBrush", Text = "24 px" };
			side.Controls.AddRange(new Control[] { tbBrush, lblBrush });
			y += 36;
			btIdentify = new Button { Text = "Match the font (box, page or book)...", Location = new Point(10, y), Size = new Size(318, 28), Name = "btIdentify" };
			new ToolTip().SetToolTip(btIdentify, "Finds the fonts that look most like the selected box's original lettering (installed fonts and Google Fonts), shows them and lets you install one");
			side.Controls.Add(btIdentify);
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
			side.Controls.Add(new Label { Text = "Text angle:", Location = new Point(10, y + 3), Size = new Size(68, 16) });
			nudRotation = new NumericUpDown { Location = new Point(78, y), Size = new Size(80, 22), DecimalPlaces = 1, Increment = 0.5m, Minimum = -180m, Maximum = 180m, Name = "nudRotation" };
			side.Controls.Add(nudRotation);
			side.Controls.Add(new Label { Text = "degrees", Location = new Point(164, y + 3), Size = new Size(60, 16) });
			y += 30;
			side.Controls.Add(new Label { Text = "Shape:", Location = new Point(10, y + 3), Size = new Size(64, 16) });
			cboShape = new ComboBoxEx { Location = new Point(78, y), Size = new Size(138, 21), DropDownStyle = ComboBoxStyle.DropDownList, Name = "cboShape" };
			cboShape.Items.AddRange(TranslationShapes.PresetNames.Cast<object>().Concat(new object[] { "Custom" }).ToArray());
			chkCircular = new CheckBox { Text = "Circular corners", Location = new Point(222, y), Size = new Size(130, 22), Name = "chkCircular" };
			side.Controls.AddRange(new Control[] { cboShape, chkCircular });
			y += 30;
			side.Controls.Add(new Label { Text = "Corners %:", Location = new Point(10, y + 3), Size = new Size(68, 16) });
			string[] cornerNames = { "Top left", "Top right", "Bottom right", "Bottom left" };
			for (int i = 0; i < 4; i++)
			{
				nudCorner[i] = new NumericUpDown { Location = new Point(78 + i * 56, y), Size = new Size(52, 22), Minimum = 0m, Maximum = 100m, Name = "nudCorner" + i };
				side.Controls.Add(nudCorner[i]);
				new ToolTip().SetToolTip(nudCorner[i], cornerNames[i] + " corner, in percent (100 = fully round or cut; for a triangle, where its point is)");
			}
			y += 30;
			side.Controls.Add(new Label { Text = "Shape angle:", Location = new Point(10, y + 3), Size = new Size(68, 16) });
			nudShapeAngle = new NumericUpDown { Location = new Point(78, y), Size = new Size(70, 22), DecimalPlaces = 1, Increment = 0.5m, Minimum = -180m, Maximum = 180m, Name = "nudShapeAngle" };
			chkTurnText = new CheckBox { Text = "Turn the text too", Location = new Point(156, y), Size = new Size(190, 22), Name = "chkTurnText" };
			side.Controls.AddRange(new Control[] { nudShapeAngle, chkTurnText });
			new ToolTip().SetToolTip(chkTurnText, "Off: the shape turns but the text stays upright inside it. On: the text turns with the shape.");
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
				Text = "Drag a box to move it, its square handles to resize, the round handle to rotate (Shift: 5 degree steps). Ctrl-click adds boxes to the selection; right-click for options.Arrow keys nudge. Orange dots round or cut a corner (Shift: all four);the round handle turns the shape. Free polygon: drag its square dots, double-click an edge to add a point, right-click a point to remove it. Wheel zooms, right-drag pans. Saved with the book.",
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
			canvas.CornersEdited += RememberShapeOf;
			LoadRememberedShape();
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
			btIdentify.Click += delegate { IdentifyFont(); };
			btBrush.Click += delegate { SetBrushMode(canvas.Brush == PageCanvas.BrushKind.Paint ? PageCanvas.BrushKind.Off : PageCanvas.BrushKind.Paint); };
			btEraser.Click += delegate { SetBrushMode(canvas.Brush == PageCanvas.BrushKind.Erase ? PageCanvas.BrushKind.Off : PageCanvas.BrushKind.Erase); };
			btClearProtect.Click += delegate
			{
				if (canvas.Protect.Count > 0)
				{
					canvas.Protect.Clear();
					canvas.Invalidate();
					CommitProtect();
				}
			};
			try
			{
				//The size used last time.
				tbBrush.Value = Math.Max(tbBrush.Minimum, Math.Min(tbBrush.Maximum, Program.Settings.TranslatorBrushSize));
			}
			catch (Exception)
			{
			}
			canvas.BrushRadius = tbBrush.Value;
			lblBrush.Text = tbBrush.Value + " px";
			tbBrush.ValueChanged += delegate
			{
				canvas.BrushRadius = tbBrush.Value;
				lblBrush.Text = tbBrush.Value + " px";
				canvas.Invalidate();
				try
				{
					Program.Settings.TranslatorBrushSize = tbBrush.Value;
				}
				catch (Exception)
				{
				}
			};
			canvas.ProtectChanged += delegate { CommitProtect(); };
			btAddShape.Click += delegate { ShowAddShapeMenu(); };
			cboShape.SelectedIndexChanged += delegate
			{
				if (loading || canvas.Selected == null || cboShape.SelectedIndex < 0 || cboShape.SelectedIndex >= TranslationShapes.PresetNames.Length)
				{
					return;
				}
				ApplyPreset(cboShape.SelectedIndex, canvas.AllSelected.ToList());
			};
			chkCircular.CheckedChanged += delegate
			{
				TranslationBox box = canvas.Selected;
				if (loading || box == null)
				{
					return;
				}
				EnsureRect(box);
				box.Circular = chkCircular.Checked;
				RememberShapeOf(box);
				SyncControls();
				canvas.Invalidate();
				Touch();
			};
			for (int i = 0; i < 4; i++)
			{
				int corner = i;
				nudCorner[i].ValueChanged += delegate
				{
					TranslationBox box = canvas.Selected;
					if (loading || box == null)
					{
						return;
					}
					EnsureRect(box);
					box.Corners[corner] = (int)nudCorner[corner].Value;
					RememberShapeOf(box);
					SyncControls();
					canvas.Invalidate();
					Touch();
				};
			}
			nudShapeAngle.ValueChanged += delegate
			{
				TranslationBox box = canvas.Selected;
				if (loading || box == null)
				{
					return;
				}
				EnsureRect(box);
				box.ShapeAngle = (double)nudShapeAngle.Value;
				if (chkTurnText.Checked)
				{
					box.Rotation = box.ShapeAngle;
				}
				SyncControls();
				canvas.Invalidate();
				Touch();
			};
			chkTurnText.CheckedChanged += delegate { canvas.TextFollowsShape = chkTurnText.Checked; };
			btDelete.Click += delegate { DeleteSelected(); };
			btRestore.Click += delegate { RestoreDeleted(); };
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
			canvas.Protect = new List<ProtectStroke>();
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
			if (!canvas.IsDragging)
			{
				canvas.Protect = service.GetProtect(key);
			}
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
			//The translator button's own picture, coloured by the state (grey off, green on, amber busy, red problem).
			bool fromCache = busy && reader.TranslationLoadingFromCache;
			Bitmap led = TranslationButtons.StatusIcon(!string.IsNullOrEmpty(problem) ? TranslationLed.Problem : (fromCache ? TranslationLed.Loading : (busy ? TranslationLed.Busy : TranslationLed.On)), 18);
			Image old = picLed.Image;
			picLed.Image = led;
			old?.Dispose();
			lblLed.Text = !string.IsNullOrEmpty(problem) ? problem :(busy ? (fromCache ? "Loading... " : "Translating... ") : "") + translated + " / " + total + " pages translated";
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
			btDelete.Enabled = has;
			if (btRestore != null)
			{
				int deleted = canvas.Boxes.Count((TranslationBox b) => b.MergedInto == DeletedMark);
				btRestore.Enabled = deleted > 0;
				btRestore.Text = deleted > 0 ? "Restore " + deleted : "Restore";
				btRestore.Width = deleted > 9 ? 70 : 58;
			}
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
			SyncShapeControls(box);
			loading = was;
		}

		private void EnsureRect(TranslationBox box)
		{
			if (box.Rect == null)
			{
				box.Rect = (int[])box.Area.Clone();
			}
			if (box.Corners == null || box.Corners.Length != 4)
			{
				box.Corners = new int[4];
			}
		}

		/// <summary>Shows the shape of the selected box in the shape controls (only for a single, unmerged box).</summary>
		private void SyncShapeControls(TranslationBox box)
		{
			bool shapable = box != null && box.Shape == null && !box.Skip;
			cboShape.Enabled = chkCircular.Enabled = nudShapeAngle.Enabled = shapable;
			bool triangle = shapable && TranslationShapes.IsTriangle(box.Form);
			bool polygon = shapable && box.Form == TranslationShapes.FreePolygon;
			chkCircular.Enabled = shapable && !polygon;
			for (int i = 0; i < 4; i++)
			{
				nudCorner[i].Enabled = shapable && !polygon && (!triangle || i == 0);
			}
			if (!shapable)
			{
				cboShape.SelectedIndex = box != null && box.Shape != null ? cboShape.Items.Count - 1 : 0;
				chkCircular.Checked = false;
				nudShapeAngle.Value = 0;
				foreach (NumericUpDown n in nudCorner)
				{
					n.Value = 0;
				}
				return;
			}
			int[] corners = box.Corners ?? new int[4];
			int preset = PageCanvas.HasAutoOutline(box) ? -1 : TranslationShapes.PresetOf(box.Form, corners, box.Circular);
			int[] a = box.Area;
			if (preset == 3 && Math.Abs((a[2] - a[0]) - (a[3] - a[1])) <= 2)
			{
				preset = 4;
			}
			cboShape.SelectedIndex = preset >= 0 ? preset : cboShape.Items.Count - 1;
			chkCircular.Checked = box.Circular;
			for (int i = 0; i < 4; i++)
			{
				nudCorner[i].Value = Math.Max(0, Math.Min(100, corners[i]));
			}
			nudShapeAngle.Value = Math.Max(nudShapeAngle.Minimum, Math.Min(nudShapeAngle.Maximum, (decimal)Math.Round(box.ShapeAngle * 2.0) / 2m));
			chkTurnText.Checked = canvas.TextFollowsShape;
		}

		/// <summary>Gives the boxes the shape of a preset (rectangle, rounded, pill, oval, circle, octagon, diamond, triangles).</summary>
		private void ApplyPreset(int index, List<TranslationBox> boxes)
		{
			TranslationShapes.Preset(index, out int rememberKind, out int[] rememberCorners, out bool rememberCircular);
			RememberShape(index, rememberKind, rememberCorners, rememberCircular);
			foreach (TranslationBox box in boxes.Where((TranslationBox b) => b.Shape == null && !b.Skip))
			{
				EnsureRect(box);
				TranslationShapes.Preset(index, out int kind, out int[] corners, out bool circular);
				int[] previous = box.Form == TranslationShapes.FreePolygon ? box.Points : (PageCanvas.HasAutoOutline(box) ? box.Outline : null);
				int[] outline = index == TranslationShapes.PolygonPreset ? (previous ?? TranslationShapes.OutlinePoints(box.Rect, box.Form, box.Corners, box.Circular)) : null;
				box.Form = kind;
				box.Corners = corners;
				box.Circular = circular;
				box.Points = outline;
				if (outline != null)
				{
					//A free polygon starts as the outline of the shape it replaces; its points can then be dragged one by one.
					box.Rect = TranslationShapes.PointsBounds(outline);
				}
				if (index == 4)
				{
					//A circle needs a square: keep the centre and the shorter side.
					int[] r = box.Rect;
					int side = Math.Min(r[2] - r[0], r[3] - r[1]);
					int cx = (r[0] + r[2]) / 2, cy = (r[1] + r[3]) / 2;
					box.Rect = new[] { cx - side / 2, cy - side / 2, cx - side / 2 + side, cy - side / 2 + side };
				}
			}
			SyncControls();
			canvas.Invalidate();
			CommitNow();
		}

		/// <summary>Remembers a shape as the one new boxes are drawn with, and keeps it for the next time ComicRack runs.</summary>
		private void RememberShape(int preset, int kind, int[] corners, bool circular)
		{
			newPreset = preset;
			newKind = kind;
			newCorners = (int[])(corners ?? new int[4]).Clone();
			newCircular = circular;
			canvas.NewShaped = !(preset == 0 || (preset < 0 && TranslationShapes.IsPlain(kind, newCorners)));
			canvas.NewKind = newKind;
			canvas.NewCorners = (int[])newCorners.Clone();
			canvas.NewCircular = newCircular;
			try
			{
				Program.Settings.TranslatorLastShape = preset + "|" + kind + "|" + string.Join(",", newCorners) + "|" + (circular ? 1 : 0);
			}
			catch (Exception)
			{
			}
		}

		/// <summary>Takes the shape used last time from the settings (a plain rectangle when there is none).</summary>
		private void LoadRememberedShape()
		{
			int preset = 0, kind = TranslationShapes.Round;
			int[] corners = new int[4];
			bool circular = false;
			try
			{
				string[] parts = (Program.Settings.TranslatorLastShape ?? "").Split('|');
				if (parts.Length == 4)
				{
					int p = int.Parse(parts[0]);
					int k = int.Parse(parts[1]);
					int[] c = parts[2].Split(',').Select((string v) => Math.Max(0, Math.Min(100, int.Parse(v)))).ToArray();
					if (c.Length == 4 && p >= -1 && p < TranslationShapes.PresetNames.Length && k >= TranslationShapes.Round && k <= TranslationShapes.FreePolygon)
					{
						preset = p;
						kind = k;
						corners = c;
						circular = parts[3] == "1";
					}
				}
			}
			catch (Exception)
			{
			}
			RememberShape(preset, kind, corners, circular);
		}

		/// <summary>Gives a box just drawn the remembered shape.</summary>
		private void ApplyRememberedShape(TranslationBox box, int[] rect)
		{
			if (!canvas.NewShaped)
			{
				return;
			}
			box.Form = newKind;
			box.Corners = (int[])newCorners.Clone();
			box.Circular = newCircular;
			if (newKind == TranslationShapes.FreePolygon)
			{
				box.Points = TranslationShapes.OutlinePoints(rect, TranslationShapes.Round, new int[4], false);
			}
			if (newPreset == 4)
			{
				int side = Math.Min(rect[2] - rect[0], rect[3] - rect[1]);
				int cx = (rect[0] + rect[2]) / 2, cy = (rect[1] + rect[3]) / 2;
				box.Rect = new[] { cx - side / 2, cy - side / 2, cx - side / 2 + side, cy - side / 2 + side };
			}
		}

		/// <summary>Called when the shape of the selected box was set by hand (corner sizes, circular corners): that is the shape remembered.</summary>
		private void RememberShapeOf(TranslationBox box)
		{
			if (box != null && box.Shape == null && box.Form != TranslationShapes.FreePolygon)
			{
				int preset = TranslationShapes.PresetOf(box.Form, box.Corners ?? new int[4], box.Circular);
				RememberShape(preset, box.Form, box.Corners, box.Circular);
			}
		}

		/// <summary>The little arrow beside "Add text box": pick the shape the next box is drawn with.</summary>
		private void ShowAddShapeMenu()
		{
			ContextMenuStrip menu = new ContextMenuStrip();
			string[] names = TranslationShapes.PresetNames;
			for (int i = 0; i < names.Length; i++)
			{
				int index = i;
				ToolStripMenuItem item = new ToolStripMenuItem(names[i]) { Checked = i == newPreset };
				item.Click += delegate
				{
					TranslationShapes.Preset(index, out int kind, out int[] corners, out bool circular);
					RememberShape(index, kind, corners, circular);
					SetAddMode(true);
					SetStatus("Drag a rectangle around the text; the new box is a " + names[index].ToLowerInvariant() + ".");
				};
				menu.Items.Add(item);
			}
			menu.Show(btAddShape, new Point(0, btAddShape.Height));
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

		/// <summary>Turns the protect brush (or its eraser) on or off; it takes the place of drawing a new box.</summary>
		private void SetBrushMode(PageCanvas.BrushKind kind)
		{
			if (kind != PageCanvas.BrushKind.Off)
			{
				SetAddMode(false);
			}
			canvas.Brush = kind;
			btBrush.Text = kind == PageCanvas.BrushKind.Paint ? "Painting... (click to stop)" : "Protect brush";
			btEraser.Text = kind == PageCanvas.BrushKind.Erase ? "Erasing... (stop)" : "Erase strokes";
			canvas.Cursor = kind != PageCanvas.BrushKind.Off ? Cursors.Cross : Cursors.Default;
			if (kind == PageCanvas.BrushKind.Paint)
			{
				SetStatus("Paint over the artwork that must stay as it is. The page is drawn again when you let go.");
			}
			else if (kind == PageCanvas.BrushKind.Erase)
			{
				SetStatus("Rub over a protect stroke to take it away.");
			}
			canvas.Invalidate();
		}

		/// <summary>Opens the window that finds the fonts closest to the selected box's lettering.</summary>
		private void IdentifyFont()
		{
			if (key == null || service == null)
			{
				SetStatus("Open a translated page first.");
				return;
			}
			TranslationBox box = canvas.Selected;
			bool boxSelected = box != null && box.Src != null && box.Src.Length == 4;
			int[] rect = boxSelected ? (int[])box.Src.Clone() : null;
			string boxText = boxSelected ? box.Text ?? "" : "";
			List<TranslationBox> onPage = canvas.Boxes.Where((TranslationBox b) => !b.IsHidden && !string.IsNullOrWhiteSpace(b.Text) && b.Src != null && b.Src.Length == 4).ToList();
			List<int[]> pageRects = onPage.Select((TranslationBox b) => (int[])b.Src.Clone()).ToList();
			string pageText = string.Join(" ", onPage.OrderByDescending((TranslationBox b) => (b.Text ?? "").Length).Take(2).Select((TranslationBox b) => b.Text));
			int shownPage = page;
			string bookKey = reader.TranslationBookKey;
			int pageCount = reader.Book?.ProviderPageCount ?? 0;
			Action<TranslationFontDialog.Scope, bool, Action<FontIdentification, string>> search = delegate (TranslationFontDialog.Scope scope, bool online, Action<FontIdentification, string> done)
			{
				if (scope == TranslationFontDialog.Scope.Book)
				{
					//Up to 12 translated pages spread evenly over the book (the helper reads the boxes it stored for each).
					List<int> translated = Enumerable.Range(0, pageCount).Where((int i) => service.Store.IsTranslated(bookKey, i)).ToList();
					List<int> sample = translated.Count <= 12 ? translated : Enumerable.Range(0, 12).Select((int n) => translated[n * (translated.Count - 1) / 11]).Distinct().ToList();
					List<byte[]> pages = new List<byte[]>();
					foreach (int i in sample)
					{
						using (IItemLock<PageImage> item = reader.GetOriginalPage(i))
						{
							byte[] bytes = item?.Item != null ? service.UploadBytesOf(item.Item) : null;
							if (bytes != null)
							{
								pages.Add(bytes);
							}
						}
					}
					if (pages.Count == 0)
					{
						done(null, "No page of this book is translated yet. Translate some pages first, then match again.");
						return;
					}
					service.IdentifyBookFont(pages, online, done);
					return;
				}
				using (IItemLock<PageImage> original = reader.GetOriginalPage(shownPage))
				{
					if (original?.Item == null)
					{
						done(null, "The page could not be read.");
						return;
					}
					if (scope == TranslationFontDialog.Scope.Box && rect != null)
					{
						service.IdentifyFont(original.Item, rect, boxText, online, done);
					}
					else if (pageRects.Count > 0)
					{
						service.IdentifyPageFont(original.Item, pageRects, pageText, online, done);
					}
					else
					{
						done(null, "This page has no text boxes to look at.");
					}
				}
			};
			Action<string, bool, Action<string, string>> install = delegate (string family, bool use, Action<string, string> done)
			{
				service.InstallFont(family, use, done);
			};
			using (TranslationFontDialog dialog = new TranslationFontDialog(search, install, boxSelected))
			{
				dialog.ShowDialog(this);
			}
		}

		/// <summary>Saves the page's protect strokes and has the page drawn again with them.</summary>
		private void CommitProtect()
		{
			if (key == null || service == null)
			{
				return;
			}
			bool accepted;
			using (IItemLock<PageImage> original = reader.GetOriginalPage(page))
			{
				accepted = original?.Item != null && service.SetProtect(key, original.Item, canvas.Protect);
			}
			SetStatus(accepted ? "Drawing the page..." : "This page is not translated yet; the protected area was not applied.");
		}

		private void SetAddMode(bool on)
		{
			if (on && canvas.Brush != PageCanvas.BrushKind.Off)
			{
				canvas.Brush = PageCanvas.BrushKind.Off;
				btBrush.Text = "Protect brush";
				btEraser.Text = "Erase strokes";
			}
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
						ApplyRememberedShape(box, rect);
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
			if (boxes.Count == 1 && PageCanvas.HasVertices(boxes[0]))
			{
				int vertex = canvas.VertexAt(at);
				if (vertex >= 0 && boxes[0].Points.Length > 6)
				{
					ToolStripMenuItem remove = new ToolStripMenuItem("Remove this point");
					remove.Click += delegate { canvas.RemoveVertex(vertex); };
					menu.Items.Add(remove);
				}
				else if (canvas.EdgeAt(at, out int after))
				{
					ToolStripMenuItem add = new ToolStripMenuItem("Add a point here");
					add.Click += delegate { canvas.InsertVertex(after, at); };
					menu.Items.Add(add);
				}
			}
			if (boxes.Any((TranslationBox b) => b.Shape == null && !b.Skip))
			{
				ToolStripMenuItem shape = new ToolStripMenuItem("Shape");
				string[] names = TranslationShapes.PresetNames;
				for (int i = 0; i < names.Length; i++)
				{
					int index = i;
					ToolStripMenuItem item = new ToolStripMenuItem(names[i]);
					item.Click += delegate { ApplyPreset(index, boxes); };
					shape.DropDownItems.Add(item);
				}
				menu.Items.Add(shape);
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
				Shape = ordered.Select((TranslationBox b) =>PageCanvas.PartOf(b)).Select((int[] part) => (int[])part.Clone()).ToList()
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

		/// <summary>
		/// Deletes the selected boxes. A box added by hand is removed; a box the helper found is hidden and its original
		/// text is left as the artist drew it (so a title read as two boxes can be replaced by one). A merged box is
		/// split again.
		/// </summary>
		private void DeleteSelected()
		{
			List<TranslationBox> boxes = canvas.AllSelected.ToList();
			if (boxes.Count == 0)
			{
				return;
			}
			foreach (TranslationBox box in boxes)
			{
				if (box.Shape != null)
				{
					Unmerge(box);
				}
				else if (box.Manual)
				{
					canvas.Boxes.Remove(box);
				}
				else
				{
					box.Skip = true;
					box.MergedInto = DeletedMark;
				}
			}
			canvas.Selected = null;
			canvas.Extra.Clear();
			SyncList();
			SyncControls();
			canvas.Invalidate();
			SetStatus(boxes.Count == 1 ? "Box deleted. Restore brings it back." : boxes.Count + " boxes deleted. Restore brings them back.");
			CommitNow();
		}

		private void RestoreDeleted()
		{
			foreach (TranslationBox box in canvas.Boxes.Where((TranslationBox b) => b.MergedInto == DeletedMark).ToList())
			{
				box.MergedInto = "";
				box.Skip = false;
			}
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
			if (keyData == Keys.Delete && (canvas.Focused || lstBoxes.Focused))
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
