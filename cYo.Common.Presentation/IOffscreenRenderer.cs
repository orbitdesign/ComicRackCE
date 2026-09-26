using System;
using System.Drawing;

namespace cYo.Common.Presentation
{
	/// <summary>
	/// Optional extra drawing a renderer can offer: capturing what would be drawn into an
	/// offscreen surface and then drawing that surface repeatedly.
	///
	/// Page folding uses it to draw each page once per frame instead of once per slice of the
	/// fold, which matters when a page is a very large scan.
	/// </summary>
	public interface IOffscreenRenderer
	{
		/// <summary>
		/// Creates a surface the size of an area of the window, or null when it cannot be made.
		/// Dispose it with DisposeOffscreen.
		/// </summary>
		object CreateOffscreen(Size size);

		/// <summary>
		/// Starts drawing into the surface, which is cleared first. Returns false when the
		/// surface is no longer usable (after a graphics device reset, say), in which case the
		/// caller should dispose it and make a new one. Does not nest.
		/// </summary>
		bool BeginOffscreen(object surface);

		/// <summary>
		/// Finishes drawing into the surface and goes back to drawing on the window.
		/// </summary>
		void EndOffscreen();

		/// <summary>
		/// Draws a captured surface. Source and destination are in screen coordinates; the
		/// renderer's current transform applies as usual.
		/// </summary>
		void DrawOffscreen(object surface, RectangleF dest, RectangleF src, float opacity);

		void DisposeOffscreen(object surface);
	}
}
