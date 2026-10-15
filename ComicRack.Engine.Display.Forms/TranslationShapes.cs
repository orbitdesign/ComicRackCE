using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace cYo.Projects.ComicRack.Engine.Display.Forms
{
	/// <summary>
	/// The shapes a translation box can have besides the plain rectangle: rounded corners (up to a pill, an oval or a
	/// circle), cut corners (up to an octagon or a diamond) and triangles. A shape part is an int array:
	/// [x1, y1, x2, y2] for a plain rectangle, or [x1, y1, x2, y2, kind, tl, tr, br, bl, circular, angle] (the angle in tenths of a degree, clockwise, about the
	/// centre of the rectangle, so every shape can be turned). The four numbers are
	/// each corner's size in percent; for a triangle the first is where its point sits along its side. Rounded corners
	/// scale with the box (an oval at 100) or, when circular, with its shorter side (a pill, or a circle in a square, at
	/// 100). The helper (render.py) draws exactly the same outlines.
	/// </summary>
	public static class TranslationShapes
	{
		public const int Round = 0;

		public const int Cut = 1;

		public const int TriangleUp = 2;

		public const int TriangleDown = 3;

		public const int TriangleLeft = 4;

		public const int TriangleRight = 5;

		/// <summary>A free polygon: its corner points (x, y pairs in page pixels) follow the first eleven numbers of the part.</summary>
		public const int FreePolygon = 6;

		public const int PartLength = 10;

		public const int AnglePart = 11;

		public static bool IsTriangle(int kind)
		{
			return kind >= TriangleUp && kind <= TriangleRight;
		}

		/// <summary>True when the form is the plain rectangle.</summary>
		public static bool IsPlain(int kind, int[] corners)
		{
			return kind == Round && (corners == null || corners.All((int c) => c <= 0));
		}

		/// <summary>The part for a rectangle with a form; the bare rectangle when the form is plain.</summary>
		public static int[] MakePart(int[] rect, int kind, int[] corners, bool circular, double angle = 0, int[] points = null)
		{
			if (kind == FreePolygon && points != null && points.Length >= 6)
			{
				int[] poly = new int[AnglePart + points.Length];
				poly[0] = rect[0];
				poly[1] = rect[1];
				poly[2] = rect[2];
				poly[3] = rect[3];
				poly[4] = FreePolygon;
				poly[10] = (int)Math.Round(angle * 10.0);
				Array.Copy(points, 0, poly, AnglePart, points.Length);
				return poly;
			}
			if (IsPlain(kind, corners) && Math.Abs(angle) < 0.05)
			{
				return new[] { rect[0], rect[1], rect[2], rect[3] };
			}
			int[] c = corners ?? new int[4];
			return new[] { rect[0], rect[1], rect[2], rect[3], kind, c[0], c[1], c[2], c[3], circular ? 1 : 0, (int)Math.Round(angle * 10.0) };
		}

		/// <summary>The angle of a part in degrees (clockwise).</summary>
		public static double AngleOf(int[] part)
		{
			return part.Length >= AnglePart ? part[10] / 10.0 : 0.0;
		}

		/// <summary>The box around a part's outline (turned parts reach beyond their rectangle).</summary>
		public static int[] Bounds(int[] part)
		{
			if (part.Length < PartLength || Math.Abs(AngleOf(part)) < 0.05)
			{
				return new[] { part[0], part[1], part[2], part[3] };
			}
			PointF[] poly = Polygon(part);
			return new[] { (int)Math.Floor(poly.Min((PointF p) => p.X)), (int)Math.Floor(poly.Min((PointF p) => p.Y)), (int)Math.Ceiling(poly.Max((PointF p) => p.X)), (int)Math.Ceiling(poly.Max((PointF p) => p.Y)) };
		}

		public static int[] PartRect(int[] part)
		{
			return new[] { part[0], part[1], part[2], part[3] };
		}

		/// <summary>The outline of a part in page pixels (the same corner as the helper uses).</summary>
		public static PointF[] Polygon(int[] part)
		{
			PointF[] points = UnturnedPolygon(part);
			double angle = AngleOf(part);
			if (Math.Abs(angle) < 0.05)
			{
				return points;
			}
			double cx = (part[0] + part[2]) / 2.0, cy = (part[1] + part[3]) / 2.0;
			double s = Math.Sin(angle * Math.PI / 180.0), c = Math.Cos(angle * Math.PI / 180.0);
			return points.Select((PointF p) => new PointF((float)(cx + (p.X - cx) * c - (p.Y - cy) * s), (float)(cy + (p.X - cx) * s + (p.Y - cy) * c))).ToArray();
		}

		/// <summary>The outline of a part before it is turned.</summary>
		public static PointF[] UnturnedPolygon(int[] part)
		{
			if (part.Length >= AnglePart + 6 && part[4] == FreePolygon)
			{
				List<PointF> corners = new List<PointF>();
				for (int i = AnglePart; i + 1 < part.Length; i += 2)
				{
					corners.Add(new PointF(part[i], part[i + 1]));
				}
				return corners.ToArray();
			}
			double x1 = part[0], y1 = part[1], x2 = part[2], y2 = part[3];
			double w = Math.Max(1, x2 - x1), h = Math.Max(1, y2 - y1);
			if (part.Length < PartLength)
			{
				return new[] { new PointF((float)x1, (float)y1), new PointF((float)x2, (float)y1), new PointF((float)x2, (float)y2), new PointF((float)x1, (float)y2) };
			}
			int kind = part[4];
			double[] corner = new double[4];
			for (int i = 0; i < 4; i++)
			{
				corner[i] = Math.Max(0, Math.Min(100, part[5 + i])) / 100.0;
			}
			bool circular = part[9] != 0;
			if (IsTriangle(kind))
			{
				double t = corner[0] > 0 ? corner[0] : 0.5;
				switch (kind)
				{
				case TriangleUp:
					return new[] { new PointF((float)(x1 + t * w), (float)y1), new PointF((float)x2, (float)y2), new PointF((float)x1, (float)y2) };
				case TriangleDown:
					return new[] { new PointF((float)x1, (float)y1), new PointF((float)x2, (float)y1), new PointF((float)(x1 + t * w), (float)y2) };
				case TriangleLeft:
					return new[] { new PointF((float)x1, (float)(y1 + t * h)), new PointF((float)x2, (float)y1), new PointF((float)x2, (float)y2) };
				default:
					return new[] { new PointF((float)x1, (float)y1), new PointF((float)x2, (float)(y1 + t * h)), new PointF((float)x1, (float)y2) };
				}
			}
			double[] rx = new double[4];
			double[] ry = new double[4];
			for (int i = 0; i < 4; i++)
			{
				if (circular)
				{
					rx[i] = ry[i] = corner[i] * Math.Min(w, h) / 2.0;
				}
				else
				{
					rx[i] = corner[i] * w / 2.0;
					ry[i] = corner[i] * h / 2.0;
				}
			}
			//Neighbouring corners must not take more than the side they share.
			double scale = 1.0;
			scale = Math.Min(scale, Fit(rx[0] + rx[1], w));
			scale = Math.Min(scale, Fit(rx[3] + rx[2], w));
			scale = Math.Min(scale, Fit(ry[0] + ry[3], h));
			scale = Math.Min(scale, Fit(ry[1] + ry[2], h));
			for (int i = 0; i < 4; i++)
			{
				rx[i] *= scale;
				ry[i] *= scale;
			}
			double[] cx = { x1 + rx[0], x2 - rx[1], x2 - rx[2], x1 + rx[3] };
			double[] cy = { y1 + ry[0], y1 + ry[1], y2 - ry[2], y2 - ry[3] };
			double[] start = { 180, 270, 0, 90 };
			List<PointF> points = new List<PointF>();
			for (int i = 0; i < 4; i++)
			{
				if (rx[i] < 0.5 && ry[i] < 0.5)
				{
					points.Add(new PointF((float)cx[i], (float)cy[i]));
				}
				else if (kind == Cut)
				{
					double a0 = start[i] * Math.PI / 180.0;
					double a1 = (start[i] + 90) * Math.PI / 180.0;
					points.Add(new PointF((float)(cx[i] + rx[i] * Math.Cos(a0)), (float)(cy[i] + ry[i] * Math.Sin(a0))));
					points.Add(new PointF((float)(cx[i] + rx[i] * Math.Cos(a1)), (float)(cy[i] + ry[i] * Math.Sin(a1))));
				}
				else
				{
					int steps = Math.Max(6, (int)Math.Min(24, (rx[i] + ry[i]) / 4));
					for (int s = 0; s <= steps; s++)
					{
						double a = (start[i] + 90.0 * s / steps) * Math.PI / 180.0;
						points.Add(new PointF((float)(cx[i] + rx[i] * Math.Cos(a)), (float)(cy[i] + ry[i] * Math.Sin(a))));
					}
				}
			}
			return points.ToArray();
		}

		private static double Fit(double total, double side)
		{
			return total > side && total > 0 ? side / total : 1.0;
		}

		public static bool Contains(int[] part, PointF p)
		{
			if (part.Length < PartLength)
			{
				return p.X >= part[0] && p.X <= part[2] && p.Y >= part[1] && p.Y <= part[3];
			}
			PointF[] poly = Polygon(part);
			bool inside = false;
			for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
			{
				if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
				{
					inside = !inside;
				}
			}
			return inside;
		}

		/// <summary>The size (in percent) of a corner that sits where the pointer is, measured inwards from the corner.</summary>
		public static int CornerFromPointer(int[] rect, double insideX, double insideY, bool circular)
		{
			double w = Math.Max(1, rect[2] - rect[0]);
			double h = Math.Max(1, rect[3] - rect[1]);
			double c;
			if (circular)
			{
				c = (Math.Max(0, insideX) + Math.Max(0, insideY)) / 2.0 / (Math.Min(w, h) / 2.0);
			}
			else
			{
				c = (Math.Max(0, insideX) / (w / 2.0) + Math.Max(0, insideY) / (h / 2.0)) / 2.0;
			}
			return (int)Math.Max(0, Math.Min(100, Math.Round(c * 100.0)));
		}

		/// <summary>How far in from the corner the rounding reaches, in page pixels (x and y).</summary>
		public static void CornerReach(int[] rect, int percent, bool circular, out double reachX, out double reachY)
		{
			double w = Math.Max(1, rect[2] - rect[0]);
			double h = Math.Max(1, rect[3] - rect[1]);
			double c = Math.Max(0, Math.Min(100, percent)) / 100.0;
			if (circular)
			{
				reachX = reachY = c * Math.Min(w, h) / 2.0;
			}
			else
			{
				reachX = c * w / 2.0;
				reachY = c * h / 2.0;
			}
		}

		public static string[] PresetNames => new[] { "Rectangle", "Rounded rectangle", "Pill", "Oval", "Circle", "Octagon", "Diamond", "Triangle up", "Triangle down", "Triangle left", "Triangle right", "Free polygon" };

		public const int PolygonPreset = 11;

		/// <summary>The corner points (flat x, y list) of a polygon that has the outline of a form, for starting to edit it freely.</summary>
		public static int[] OutlinePoints(int[] rect, int kind, int[] corners, bool circular)
		{
			if (kind == FreePolygon)
			{
				return null;
			}
			PointF[] outline = UnturnedPolygon(MakePart(rect, kind, corners, circular));
			int step = Math.Max(1, (int)Math.Ceiling(outline.Length / 16.0));
			List<int> flat = new List<int>();
			for (int i = 0; i < outline.Length; i += step)
			{
				flat.Add((int)Math.Round(outline[i].X));
				flat.Add((int)Math.Round(outline[i].Y));
			}
			return flat.ToArray();
		}

		/// <summary>The box around corner points.</summary>
		public static int[] PointsBounds(int[] points)
		{
			int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
			for (int i = 0; i + 1 < points.Length; i += 2)
			{
				x1 = Math.Min(x1, points[i]);
				x2 = Math.Max(x2, points[i]);
				y1 = Math.Min(y1, points[i + 1]);
				y2 = Math.Max(y2, points[i + 1]);
			}
			return new[] { x1, y1, Math.Max(x2, x1 + 8), Math.Max(y2, y1 + 8) };
		}

		/// <summary>The form of a preset: kind, corner sizes and whether the corners are circular.</summary>
		public static void Preset(int index, out int kind, out int[] corners, out bool circular)
		{
			circular = false;
			switch (index)
			{
			case 1:
				kind = Round;
				corners = new[] { 30, 30, 30, 30 };
				circular = true;
				break;
			case 2:
				kind = Round;
				corners = new[] { 100, 100, 100, 100 };
				circular = true;
				break;
			case 3:
			case 4:
				kind = Round;
				corners = new[] { 100, 100, 100, 100 };
				break;
			case 5:
				kind = Cut;
				corners = new[] { 59, 59, 59, 59 };
				circular = true;
				break;
			case 6:
				kind = Cut;
				corners = new[] { 100, 100, 100, 100 };
				break;
			case 7:
				kind = TriangleUp;
				corners = new[] { 50, 0, 0, 0 };
				break;
			case 8:
				kind = TriangleDown;
				corners = new[] { 50, 0, 0, 0 };
				break;
			case 9:
				kind = TriangleLeft;
				corners = new[] { 50, 0, 0, 0 };
				break;
			case 10:
				kind = TriangleRight;
				corners = new[] { 50, 0, 0, 0 };
				break;
			case 11:
				kind = FreePolygon;
				corners = new[] { 0, 0, 0, 0 };
				break;
			default:
				kind = Round;
				corners = new[] { 0, 0, 0, 0 };
				break;
			}
		}

		/// <summary>The preset (index in PresetNames) that matches a form best, or -1 for a custom one.</summary>
		public static int PresetOf(int kind, int[] corners, bool circular)
		{
			for (int i = 0; i < PresetNames.Length; i++)
			{
				Preset(i, out int k, out int[] c, out bool circ);
				if (i == 4)
				{
					continue;
				}
				if (k == kind && c.SequenceEqual(corners ?? new int[4]) && (IsTriangle(kind) || circ == circular || IsPlain(kind, corners)))
				{
					return i;
				}
			}
			return -1;
		}
	}
}
