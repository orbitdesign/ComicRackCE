using System.Drawing;
using System.Windows.Forms;
using cYo.Common.Drawing;

namespace cYo.Projects.ComicRack.Engine.Display
{
	public interface IComicDisplayConfig
	{
		ImageBackgroundMode ImageBackgroundMode
		{
			get;
			set;
		}

		Color BackColor
		{
			get;
			set;
		}

		string BackgroundTexture
		{
			get;
			set;
		}

		ImageLayout BackgroundImageLayout
		{
			get;
			set;
		}

		string PaperTexture
		{
			get;
			set;
		}

		float PaperTextureStrength
		{
			get;
			set;
		}

		ImageLayout PaperTextureLayout
		{
			get;
			set;
		}

		bool PageMargin
		{
			get;
			set;
		}

		float PageMarginPercentWidth
		{
			get;
			set;
		}

		PageLayoutMode PageLayout
		{
			get;
			set;
		}

		float DoublePageOverlap
		{
			get;
			set;
		}

		bool ImageAutoRotate
		{
			get;
			set;
		}

		ImageRotation ImageRotation
		{
			get;
			set;
		}

		float ImageZoom
		{
			get;
			set;
		}

		bool ImageFitOnlyIfOversized
		{
			get;
			set;
		}

		ImageFitMode ImageFitMode
		{
			get;
			set;
		}

		ImageDisplayOptions ImageDisplayOptions
		{
			get;
			set;
		}

		bool RealisticPages
		{
			get;
			set;
		}

		bool AutoHideCursor
		{
			get;
			set;
		}

		bool LeftRightMovementReversed
		{
			get;
			set;
		}

		bool RightToLeftReading
		{
			get;
			set;
		}

		RightToLeftReadingMode RightToLeftReadingMode
		{
			get;
			set;
		}

		bool TwoPageNavigation
		{
			get;
			set;
		}

		bool BlendWhilePaging
		{
			get;
			set;
		}

		float MagnifierOpacity
		{
			get;
			set;
		}

		Size MagnifierSize
		{
			get;
			set;
		}

		bool MagnifierVisible
		{
			get;
			set;
		}

		float MagnifierZoom
		{
			get;
			set;
		}

		MagnifierStyle MagnifierStyle
		{
			get;
			set;
		}

		bool AutoHideMagnifier
		{
			get;
			set;
		}

		bool AutoMagnifier
		{
			get;
			set;
		}

		float InfoOverlayScaling
		{
			get;
			set;
		}

		InfoOverlays VisibleInfoOverlays
		{
			get;
			set;
		}

		bool SmoothScrolling
		{
			get;
			set;
		}

		PageTransitionEffect PageTransitionEffect
		{
			get;
			set;
		}

		bool DisplayChangeAnimation
		{
			get;
			set;
		}

		bool FlowingMouseScrolling
		{
			get;
			set;
		}

		bool DragPageTurning
		{
			get;
			set;
		}

		int ReadAheadPages
		{
			get;
			set;
		}

		float PageCurlAmount
		{
			get;
			set;
		}

		float PageCurlShadowStrength
		{
			get;
			set;
		}

		float PageCurlGrabArea
		{
			get;
			set;
		}

		int PageCurlDuration
		{
			get;
			set;
		}

		/// <summary>
		/// One-shot override for the next page turn transition's duration, in milliseconds. 0
		/// means no override: use PageCurlDuration as normal. Consumed (reset to 0) the moment
		/// that transition starts, so it can never affect a later, unrelated page turn.
		/// </summary>
		int NextPageTurnDuration
		{
			get;
			set;
		}

		/// <summary>
		/// Remembers the page displayed right now as the far side of an upcoming fold, and turns
		/// off the ordinary per-turn fold while the caller steps through several pages one by
		/// one. Pair with EndRiffle once the caller has finished stepping.
		/// </summary>
		void BeginRiffle();

		/// <summary>
		/// Turns the ordinary per-turn fold back on, and - if the page actually moved since the
		/// matching BeginRiffle - shows one fold running from the page remembered then straight
		/// to wherever the page is now, however many pages that turned out to be. duration
		/// overrides the normal transition length for this one fold; 0 keeps the configured
		/// length.
		/// </summary>
		void EndRiffle(int duration);

		bool SoftwareFiltering
		{
			get;
			set;
		}

		bool HardwareFiltering
		{
			get;
			set;
		}
	}
}
