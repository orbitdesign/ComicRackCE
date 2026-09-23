using System.Drawing;

namespace cYo.Common.Presentation
{
	/// <summary>
	/// Optional extra drawing a renderer can offer: clipping to and filling arbitrary polygons.
	/// Needed for page folds at an angle. All coordinates are in screen (device) space and are
	/// not affected by the renderer's current transform. Colors are used with their own alpha.
	/// </summary>
	public interface IGeometryClipRenderer
	{
		void PushPolygonClip(PointF[] polygon);

		/// <summary>
		/// As above, but lets the caller ask for hard edges. Shapes that are drawn side by side to
		/// make up one surface must use hard edges: with smoothed edges each shape only partly
		/// covers the pixels along the join, and the seam shows as a thin line.
		/// </summary>
		void PushPolygonClip(PointF[] polygon, bool smoothEdges);

		void PopPolygonClip();

		void FillPolygon(PointF[] polygon, Color color);

		/// <summary>
		/// Fills the shape pushed by the last PushPolygonClip, without building it again.
		/// Does nothing when no polygon clip is active.
		/// </summary>
		void FillCurrentClip(Color color);

		void FillPolygonGradient(PointF[] polygon, PointF start, Color startColor, PointF end, Color endColor);
	}
}
