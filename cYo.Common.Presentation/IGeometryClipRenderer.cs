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
