using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using cYo.Common;
using cYo.Common.Collections;
using cYo.Common.ComponentModel;
using cYo.Common.Drawing;
using cYo.Common.Localize;
using cYo.Common.Mathematics;
using cYo.Common.Presentation;
using cYo.Common.Presentation.Panels;
using cYo.Common.Runtime;
using cYo.Common.Text;
using cYo.Common.Threading;
using cYo.Common.Windows.Forms;
using cYo.Projects.ComicRack.Engine.Display.Forms.Properties;
using cYo.Projects.ComicRack.Engine.Drawing;
using cYo.Projects.ComicRack.Engine.IO;
using cYo.Projects.ComicRack.Engine.IO.Cache;
using cYo.Projects.ComicRack.Engine.IO.Provider;

namespace cYo.Projects.ComicRack.Engine.Display.Forms
{
	public class ComicDisplayControl : ImageDisplayControl, IComicDisplay, IComicDisplayConfig
	{
		public enum BlendAnimationMode
		{
			Default,
			CurrentAsNew,
			CurrentAsOld
		}

		public class ImageInfo
		{
			public int Width
			{
				get;
				set;
			}

			public int Height
			{
				get;
				set;
			}

			public int ImageCount
			{
				get;
				set;
			}

			public bool IsForcedDoublePage
			{
				get;
				set;
			}

			public bool IsSingleImage => ImageCount == 1;

			public bool IsDoubleImage => ImageCount > 1;

			public bool IsDoublePage => Width > Height;

			public bool IsValid => !Size.IsEmpty;

			public Size Size
			{
				get
				{
					return new Size(Width, Height);
				}
				set
				{
					Width = value.Width;
					Height = value.Height;
				}
			}
		}

		public delegate void BlendAnimationHandler(IBitmapRenderer hr, int oldPage, DisplayOutput oldOut, DisplayOutput display, float percent);

		private class ScaledPageKey
		{
			private WeakReference<PageImage> wrf;

			public float ScaleX
			{
				get;
				set;
			}

			public float ScaleY
			{
				get;
				set;
			}

			public PageImage Bitmap
			{
				get
				{
					return wrf.GetData();
				}
				set
				{
					wrf = new WeakReference<PageImage>(value);
				}
			}

			public override bool Equals(object obj)
			{
				ScaledPageKey scaledPageKey = obj as ScaledPageKey;
				if (scaledPageKey != null && scaledPageKey.ScaleX == ScaleX && scaledPageKey.ScaleY == ScaleY)
				{
					return scaledPageKey.Bitmap == Bitmap;
				}
				return false;
			}

			public override int GetHashCode()
			{
				return ScaleX.GetHashCode() ^ ScaleY.GetHashCode();
			}
		}

		private class ScaledPageItem : MemoryOptimizedImage
		{
			public long Ticks
			{
				get;
				set;
			}

			public ScaledPageItem()
				: base((Bitmap)null)
			{
			}
		}

		private struct Magnifier
		{
			public Bitmap Bitmap;

			public Rectangle Inner;

			public Rectangle Outer;
		}

		private class MoveAnimator : Animator
		{
			public MoveAnimator(int time, Point fromPoint, Point toPoint, bool outTransition)
			{
				if (!outTransition)
				{
					base.Delay = 500;
				}
				base.Span = time;
				base.AnimationValueGenerator = Animator.SinusRise;
				Point dp = new Point(toPoint.X - fromPoint.X, toPoint.Y - fromPoint.Y);
				base.AnimationHandler = delegate(OverlayPanel p, float t, float d)
				{
					p.X = fromPoint.X + (int)(t * (float)dp.X);
					p.Y = fromPoint.Y + (int)(t * (float)dp.Y);
					if (outTransition && t >= 1f)
					{
						p.Visible = false;
					}
				};
			}
		}

		private class SizeAnimator : Animator
		{
			public SizeAnimator(int time, Point toPoint, bool outTransition)
			{
				base.Span = time;
				base.AnimationValueGenerator = Animator.LinearRise;
				base.AnimationHandler = delegate(OverlayPanel p, float t, float d)
				{
					p.Scale = (outTransition ? (1f - t) : t);
					Rectangle bounds = p.Bounds;
					Rectangle physicalBounds = p.PhysicalBounds;
					p.X = toPoint.X + (physicalBounds.Width - bounds.Width) / 2;
					p.Y = toPoint.Y + physicalBounds.Height - bounds.Height;
					if (outTransition && t >= 1f)
					{
						p.Visible = false;
					}
				};
			}
		}

		public const int DefaultFadeTime = 100;

		private const int ContinuousFallbackWidth = 1000;

		private static readonly int NavigationOverlayDefaultHeight = FormUtility.ScaleDpiY(200);

		private static readonly Size pageInfoSize = new Size(150, 25).ScaleDpi();

		private static readonly Size partInfoSize = new Size(200, 200).ScaleDpi();

		private static readonly Size loadInfoSize = new Size(200, 30).ScaleDpi();

		private static readonly Size messageSize = new Size(300, 30).ScaleDpi();

		public static readonly Cursor EmptyCursor = new Cursor(new MemoryStream(Resources.EmptyCursor));

		private readonly OverlayManager overlayManager;

		private readonly TextOverlay currentPageOverlay;

		private readonly TextOverlay loadPageOverlay;

		private readonly TextOverlay messageOverlay;

		private readonly OverlayPanel visiblePartOverlay;

		private readonly OverlayPanel magnifierOverlay;

		private readonly NavigationOverlay navigationOverlay;

		private readonly OverlayPanel gestureOverlay;

		private bool firstPageHasBeenLoaded = true;

		private bool preCache = true;

		private bool navigationOverlayVisible;

		private volatile IPagePool pagePool;

		private ComicBookNavigator book;

		private readonly Color blindOutColor = Color.FromArgb(192, Color.Black);

		private bool blindOut;

		private bool showStatusMessage;

		private bool leftRightMovementReversed;

		private volatile PageLayoutMode pageLayout;

		private readonly Dictionary<int, Size> continuousPageSizes = new Dictionary<int, Size>();

		private ContinuousPageLayout continuousLayout;

		private int continuousContentWidth;

		private ContinuousPageLayout.Anchor continuousViewportAnchor;

		private ImageFitMode continuousImageFitMode;

		private bool continuousFitOnlyIfOversized;

		private bool continuousLayoutRebuildPending;

		private ContinuousPageLayout.Anchor continuousLayoutRebuildAnchor;

		private bool continuousViewportRestorePending;

		private bool continuousFitResetPending;

		private bool continuousNavigationSync;

		private volatile float doublePageOverlap;

		private float magnifierZoom = 2f;

		private bool magnifierVisible;

		private bool autoHideMagnifier = true;

		private bool autoMagnifier = true;

		private bool realisticPages = true;

		private float infoOverlayScaling = 1f;

		private InfoOverlays visibleInfoOverlays;

		private PageTransitionEffect pageTransitionEffect = PageTransitionEffect.Fade;

		private bool disableBlending;

		private bool softwareFiltering = true;

		private MagnifierStyle magnifierStyle;

		private Bitmap paperTextureBitmap;

		private ImageLayout paperTextureLayout;

		private float paperTextureStrength = 1f;

		private string paperTexture;

		private int displayHash;

		private volatile int currentPage;

		private int currentMousePage = -1;

		private Bitmap workingPaperTexture;

		private static readonly Bitmap pageBowLeft = PageRendering.CreatePageBow(new Size(256, 256), 180f);

		private static readonly Bitmap pageBowRight = PageRendering.CreatePageBow(new Size(256, 256), 0f);

		private Bitmap shadowBitmap;

		private float innerBowLeftOffsetInPercent;

		private float innerBowRightOffsetInPercent;

		private int[] displayedPages = new int[0];

		private Rectangle[] displayedPageAreas = new Rectangle[0];

		private RectangleF displayedPageBounds;

		private PageKey lastValidKey;

		private bool drawBlankPagesOverride;

		private Cache<ScaledPageKey, ScaledPageItem> scaledCache;

		private Point mouseDown;

		private bool panMagnifier;

		private Point continuousPanStartLocation;

		private Point continuousPanStartOffset;

		private bool continuousPanDirectionLocked;

		private bool continuousPanVertical;

		private int cachedPartOverlay = -1;

		private Point cachedPartOffset;

		private int currentPageOverlayHash;

		private static Magnifier[] magnifiers;

		private RectangleF partRect;

		private int currentPartHash;

		private Bitmap smallBitmap;

		private long lastBlend;

		private const bool moveAnim = true;

		private bool inBlendAnmation;

		private IContainer components;

		private System.Windows.Forms.Timer imageScaleTimer;

		private System.Windows.Forms.Timer longClickTimer;

		private System.Windows.Forms.Timer cacheUpdateTimer;

		[DefaultValue(true)]
		public bool PreCache
		{
			get
			{
				return preCache;
			}
			set
			{
				preCache = value;
			}
		}

		[DefaultValue(false)]
		public bool NavigationOverlayVisible
		{
			get
			{
				return navigationOverlayVisible;
			}
			set
			{
				if (navigationOverlayVisible != value)
				{
					navigationOverlayVisible = value;
					UpdateNavigationOverlay();
				}
			}
		}

		[DefaultValue(null)]
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		public IPagePool PagePool
		{
			get
			{
				return pagePool;
			}
			set
			{
				if (value == null)
				{
					throw new ArgumentNullException();
				}
				if (pagePool != value)
				{
					if (pagePool != null)
					{
						pagePool.PageCached -= MemoryPageCache_ItemAdded;
					}
					pagePool = value;
					value.PageCached += MemoryPageCache_ItemAdded;
				}
			}
		}

		[DefaultValue(null)]
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		public IThumbnailPool ThumbnailPool
		{
			get
			{
				return navigationOverlay.Pool;
			}
			set
			{
				navigationOverlay.Pool = value;
			}
		}

		[DefaultValue(null)]
		public ComicBookNavigator Book
		{
			get
			{
				return book;
			}
			set
			{
				ComicBookNavigator comicBookNavigator = Book;
				if (comicBookNavigator == value)
				{
					return;
				}
				StopPendingImageCacheUpdate();
				if (comicBookNavigator != null)
				{
					comicBookNavigator.Disposing -= book_Disposing;
					comicBookNavigator.Navigation -= book_Navigation;
					comicBookNavigator.IndexOfPageReady -= book_IndexOfPageReady;
					comicBookNavigator.ColorAdjustmentChanged -= book_ColorAdjustmentChanged;
					comicBookNavigator.RightToLeftReadingChanged -= book_RightToLeftReadingChanged;
					comicBookNavigator.IndexRetrievalCompleted -= book_IndexRetrievalCompleted;
					comicBookNavigator.PageFilterChanged -= book_PageFilterOrPagesChanged;
					comicBookNavigator.PagesChanged -= book_PageFilterOrPagesChanged;
					comicBookNavigator.Comic.BookChanged -= Comic_BookChanged;
					comicBookNavigator.PagePart = (PageLayout == PageLayoutMode.Continuous) ? ImagePartInfo.Empty : base.ImageVisiblePart;
					comicBookNavigator.RightToLeftReading = (base.RightToLeftReading ? YesNo.Yes : YesNo.No);
				}
				book = value;
				continuousPageSizes.Clear();
				continuousLayout = null;
				continuousContentWidth = 0;
				OnBookChanged();
				lastValidKey = null;
				if (value != null)
				{
					value.Disposing += book_Disposing;
					value.Navigation += book_Navigation;
					value.ColorAdjustmentChanged += book_ColorAdjustmentChanged;
					value.RightToLeftReadingChanged += book_RightToLeftReadingChanged;
					value.IndexRetrievalCompleted += book_IndexRetrievalCompleted;
					value.IndexOfPageReady += book_IndexOfPageReady;
					value.PageFilterChanged += book_PageFilterOrPagesChanged;
					value.PagesChanged += book_PageFilterOrPagesChanged;
					value.Comic.BookChanged += Comic_BookChanged;
					CurrentPage = value.CurrentPage;
					base.ImageVisiblePart = (PageLayout == PageLayoutMode.Continuous) ? ImagePartInfo.Empty : value.PagePart;
					if (value.RightToLeftReading != YesNo.Unknown)
					{
						base.RightToLeftReading = value.RightToLeftReading == YesNo.Yes;
					}
				}
				if (PageLayout == PageLayoutMode.Continuous)
				{
					RebuildContinuousLayout(new ContinuousPageLayout.Anchor(CurrentPage, 0));
				}
				navigationOverlay.Provider = value;
				navigationOverlay.ImageKeyProvider = value;
				UpdateNavigationOverlay(redraw: true);
				Invalidate();
			}
		}

		[DefaultValue(typeof(Color), "192, 0, 0, 0")]
		public Color BlindOutColor
		{
			get
			{
				return blindOutColor;
			}
			set
			{
				if (!(blindOutColor == value) && blindOut)
				{
					Invalidate();
				}
			}
		}

		[DefaultValue(false)]
		public bool BlindOut
		{
			get
			{
				return blindOut;
			}
			set
			{
				if (blindOut != value)
				{
					blindOut = value;
					Invalidate();
				}
			}
		}

		[DefaultValue(false)]
		public bool ShowStatusMessage
		{
			get
			{
				return showStatusMessage;
			}
			set
			{
				if (showStatusMessage != value)
				{
					showStatusMessage = value;
					Invalidate();
				}
			}
		}

		[DefaultValue(false)]
		public bool LeftRightMovementReversed
		{
			get
			{
				return leftRightMovementReversed;
			}
			set
			{
				if (leftRightMovementReversed != value)
				{
					leftRightMovementReversed = value;
					OnReadingModeChanged();
				}
			}
		}

		[DefaultValue(PageLayoutMode.Single)]
		public PageLayoutMode PageLayout
		{
			get
			{
				return pageLayout;
			}
			set
			{
				if (pageLayout != value)
				{
					PageLayoutMode oldLayout = pageLayout;
					pageLayout = value;
					if (value == PageLayoutMode.Continuous)
					{
						RebuildContinuousLayout(new ContinuousPageLayout.Anchor(CurrentPage, 0));
					}
					else if (oldLayout == PageLayoutMode.Continuous)
					{
						continuousLayout = null;
						base.ImageVisiblePart = ImagePartInfo.Empty;
					}
					OnDisplayChanged();
					Invalidate();
				}
			}
		}

		[Browsable(false)]
		[DefaultValue(0f)]
		public float DoublePageOverlap
		{
			get
			{
				return doublePageOverlap;
			}
			set
			{
				if (doublePageOverlap != value)
				{
					doublePageOverlap = value;
				}
			}
		}

		[DefaultValue(2f)]
		public float MagnifierZoom
		{
			get
			{
				return magnifierZoom;
			}
			set
			{
				if (magnifierZoom != value)
				{
					magnifierZoom = value;
					if (MagnifierVisible)
					{
						Invalidate();
					}
				}
			}
		}

		[DefaultValue(false)]
		public bool MagnifierVisible
		{
			get
			{
				return magnifierVisible;
			}
			set
			{
				if (magnifierVisible != value)
				{
					magnifierVisible = value;
					UpdateMagnifierVisibility();
				}
			}
		}

		[DefaultValue(1f)]
		public float MagnifierOpacity
		{
			get
			{
				return magnifierOverlay.Opacity;
			}
			set
			{
				magnifierOverlay.Opacity = value;
			}
		}

		[DefaultValue(typeof(Size), "200, 200")]
		public Size MagnifierSize
		{
			get
			{
				return magnifierOverlay.Size;
			}
			set
			{
				magnifierOverlay.Size = value;
			}
		}

		[DefaultValue(true)]
		public bool AutoHideMagnifier
		{
			get
			{
				return autoHideMagnifier;
			}
			set
			{
				if (autoHideMagnifier != value)
				{
					autoHideMagnifier = value;
					UpdateMagnifierVisibility();
				}
			}
		}

		[DefaultValue(true)]
		public bool AutoMagnifier
		{
			get
			{
				return autoMagnifier;
			}
			set
			{
				if (autoMagnifier != value)
				{
					autoMagnifier = value;
					UpdateMagnifierVisibility();
				}
			}
		}

		[DefaultValue(true)]
		public bool RealisticPages
		{
			get
			{
				return realisticPages;
			}
			set
			{
				if (realisticPages != value)
				{
					realisticPages = value;
					Invalidate();
				}
			}
		}

		[DefaultValue(1f)]
		public float InfoOverlayScaling
		{
			get
			{
				return infoOverlayScaling;
			}
			set
			{
				if (infoOverlayScaling != value)
				{
					infoOverlayScaling = value;
					TextOverlay textOverlay = currentPageOverlay;
					OverlayPanel overlayPanel = visiblePartOverlay;
					TextOverlay textOverlay2 = loadPageOverlay;
					float num2 = (messageOverlay.Scale = infoOverlayScaling);
					float num4 = (textOverlay2.Scale = num2);
					float num7 = (textOverlay.Scale = (overlayPanel.Scale = num4));
					navigationOverlay.Size = CalcNavigationOverlaySize();
				}
			}
		}

		[DefaultValue(InfoOverlays.None)]
		public InfoOverlays VisibleInfoOverlays
		{
			get
			{
				return visibleInfoOverlays;
			}
			set
			{
				if (visibleInfoOverlays != value)
				{
					visibleInfoOverlays = value;
					UpdateNavigationOverlay();
					OnVisibleInfoOverlaysChanged();
				}
			}
		}

		[DefaultValue(PageTransitionEffect.Fade)]
		public PageTransitionEffect PageTransitionEffect
		{
			get
			{
				return pageTransitionEffect;
			}
			set
			{
				pageTransitionEffect = value;
			}
		}

		[Browsable(false)]
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		public BlendAnimationHandler Blender
		{
			get;
			set;
		}

		[DefaultValue(false)]
		public bool DisableBlending
		{
			get
			{
				return disableBlending;
			}
			set
			{
				disableBlending = value;
			}
		}

		[DefaultValue(true)]
		public bool SoftwareFiltering
		{
			get
			{
				return softwareFiltering;
			}
			set
			{
				softwareFiltering = value;
			}
		}

		public bool IsFlipped
		{
			get
			{
				if (base.RightToLeftReading)
				{
					return base.RightToLeftReadingMode == RightToLeftReadingMode.FlipParts;
				}
				return false;
			}
		}

		public bool IsMovementFlipped
		{
			get
			{
				if (PageLayout != PageLayoutMode.Continuous && LeftRightMovementReversed)
				{
					return IsFlipped;
				}
				return false;
			}
		}

		[DefaultValue(false)]
		public bool BlendWhilePaging
		{
			get;
			set;
		}

		[DefaultValue(MagnifierStyle.Glass)]
		public MagnifierStyle MagnifierStyle
		{
			get
			{
				return magnifierStyle;
			}
			set
			{
				magnifierStyle = value;
			}
		}

		public Color BlankPageColor => EngineConfiguration.Default.BlankPageColor;

		[Category("Appearance")]
		[Description("Paper overlay texture")]
		[DefaultValue(null)]
		public Bitmap PaperTextureBitmap
		{
			get
			{
				return paperTextureBitmap;
			}
			set
			{
				if (paperTextureBitmap != value)
				{
					paperTextureBitmap = value;
					CreateWorkingPaperTexture();
					Invalidate();
				}
			}
		}

		[Category("Appearance")]
		[Description("Paper texture layout")]
		[DefaultValue(null)]
		public ImageLayout PaperTextureLayout
		{
			get
			{
				return paperTextureLayout;
			}
			set
			{
				if (paperTextureLayout != value)
				{
					paperTextureLayout = value;
					Invalidate();
				}
			}
		}

		[Category("Appearance")]
		[Description("Paper texture alpha value")]
		[DefaultValue(1f)]
		public float PaperTextureStrength
		{
			get
			{
				return paperTextureStrength;
			}
			set
			{
				if (value != paperTextureStrength)
				{
					paperTextureStrength = value;
					CreateWorkingPaperTexture();
					Invalidate();
				}
			}
		}

		[Category("Appearance")]
		[Description("Paper texture file")]
		[DefaultValue(null)]
		public string PaperTexture
		{
			get
			{
				return paperTexture;
			}
			set
			{
				if (!(paperTexture == value))
				{
					paperTexture = value;
					Image obj = PaperTextureBitmap;
					try
					{
						PaperTextureBitmap = (Bitmap)Image.FromFile(paperTexture);
					}
					catch (Exception)
					{
						PaperTextureBitmap = null;
					}
					obj.SafeDispose();
					OnImageDisplayOptionsChanged();
					Invalidate();
				}
			}
		}

		public override bool IsValid
		{
			get
			{
				if (Book != null)
				{
					return PagePool != null;
				}
				return false;
			}
		}

		public int DisplayHash => displayHash;

		public int CurrentPage
		{
			get
			{
				return currentPage;
			}
			protected set
			{
				currentPage = value;
			}
		}

		public int CurrentMousePage => currentMousePage;

		protected int NextPage
		{
			get
			{
				if (!TwoPageDisplay)
				{
					return -1;
				}
				return SeekPage(CurrentPage, 1);
			}
		}

		public bool TwoPageDisplay => PageLayout == PageLayoutMode.Double || PageLayout == PageLayoutMode.DoubleAdaptive;

		public bool ShouldPagingBlend
		{
			get;
			private set;
		}

		protected override bool MouseHandled
		{
			get
			{
				if (!overlayManager.MouseHandled)
				{
					return base.MouseActionHappened;
				}
				return true;
			}
		}

		private int[] DisplayedPages => displayedPages;

		private Rectangle[] DisplayedPageAreas => displayedPageAreas;

		private RectangleF DisplayedPageBounds => displayedPageBounds;

		private bool IsCurrentPageOverlayEnabled => (visibleInfoOverlays & InfoOverlays.CurrentPage) != 0;

		private bool IsPartInfoOverlayEnabled => (visibleInfoOverlays & InfoOverlays.PartInfo) != 0;

		private bool IsLoadPageOverlayEnabled => (visibleInfoOverlays & InfoOverlays.LoadPage) != 0;

		private bool IsNavigationOverlayEnabled => (visibleInfoOverlays & InfoOverlays.PageBrowser) != 0;

		private bool IsPageBrowsersOnTop => (visibleInfoOverlays & InfoOverlays.PageBrowserOnTop) != 0;

		private bool CurrentPageShowsName => (visibleInfoOverlays & InfoOverlays.CurrentPageShowsName) != 0;

		public override int PageScrollingTime
		{
			get
			{
				return EngineConfiguration.Default.PageScrollingDuration;
			}
			set
			{
				base.PageScrollingTime = value;
			}
		}

		public override bool IsDoubleImage => PageLayout != PageLayoutMode.Continuous && GetImageInfo().IsDoubleImage;

		private int NavigationOverlayVisibleY
		{
			get
			{
				Rectangle clientRectangle = base.ClientRectangle;
				Rectangle bounds = navigationOverlay.Bounds;
				if (!IsPageBrowsersOnTop)
				{
					return clientRectangle.Height - bounds.Height - base.Margin.Bottom;
				}
				return base.Margin.Top;
			}
		}

		public event EventHandler BookChanged;

		public event EventHandler DrawnPageCountChanged;

		public event EventHandler<BrowseEventArgs> Browse;

		public event EventHandler<BookPageEventArgs> PageChange;

		public event EventHandler<BookPageEventArgs> PageChanged;

		public event EventHandler VisibleInfoOverlaysChanged;

		public ComicDisplayControl()
		{
			InitializeComponent();
			cacheUpdateTimer.Interval = EngineConfiguration.Default.PageCachingDelay;
			overlayManager = new OverlayManager(this)
			{
				AnimationEnabled = true
			};
			components.Add(overlayManager);
			Size size = new Size(300, 200).ScaleDpi();
			magnifierOverlay = new OverlayPanel(size)
			{
				Opacity = 1f,
				Visible = false,
				Enabled = false
			};
			magnifierOverlay.RenderSurface += magnifierOverlay_RenderSurface;
			overlayManager.Panels.Add(magnifierOverlay);
			visiblePartOverlay = new OverlayPanel(partInfoSize.Width, partInfoSize.Height, ContentAlignment.BottomRight, new Animator[1]
			{
				new FadeAnimator(DefaultFadeTime, 2000, DefaultFadeTime)
			})
			{
				Opacity = 0f,
				AutoAlign = true,
				Enabled = true,
				HitTestType = HitTestType.Disabled
			};
			visiblePartOverlay.Drawing += visiblePartOverlay_Drawing;
			visiblePartOverlay.RenderSurface += visiblePartOverlay_RenderSurface;
			if (!EngineConfiguration.Default.HideVisiblePartOverlayClose)
			{
				SimpleButtonPanel simpleButtonPanel = new SimpleButtonPanel(new Size(16, 16).ScaleDpi())
				{
					Margin = Padding.Empty,
					Alignment = ContentAlignment.TopRight,
					Icon = Resources.Close,
					AutoAlign = true,
					AlignmentOffset = new Point(-3, -2).ScaleDpi()
				};
				simpleButtonPanel.Click += delegate
				{
					visiblePartOverlay.Opacity = 0f;
					VisibleInfoOverlays &= ~InfoOverlays.PartInfo;
				};
				visiblePartOverlay.Panels.Add(simpleButtonPanel);
			}
			overlayManager.Panels.Add(visiblePartOverlay);
			currentPageOverlay = new TextOverlay(pageInfoSize.Width, pageInfoSize.Height, ContentAlignment.TopRight, Font)
			{
				Enabled = false,
				Opacity = 0f,
				AutoAlign = true,
				Html = true
			};
			currentPageOverlay.Animators.Add(new FadeAnimator(100, 2000, 100));
			overlayManager.Panels.Add(currentPageOverlay);
			loadPageOverlay = new TextOverlay(loadInfoSize.Width, loadInfoSize.Height, ContentAlignment.MiddleCenter, Font)
			{
				Enabled = false,
				AutoAlign = true,
				Visible = false
			};
			overlayManager.Panels.Add(loadPageOverlay);
			messageOverlay = new TextOverlay(messageSize.Width, messageSize.Height, ContentAlignment.MiddleCenter, Font)
			{
				Enabled = false,
				AutoAlign = true,
				Visible = false
			};
			overlayManager.Panels.Add(messageOverlay);
			navigationOverlay = new NavigationOverlay(new Size(500, NavigationOverlayDefaultHeight).ScaleDpi())
			{
				Visible = false
			};
			navigationOverlay.Browse += delegate(object x, BrowseEventArgs y)
			{
				OnBrowse(y);
			};
			overlayManager.Panels.Add(navigationOverlay);
			gestureOverlay = new GestureOverlay
			{
				Enabled = false,
				Opacity = 0f,
				AutoAlign = true,
				IgnoreParentMargin = true
			};
			gestureOverlay.Animators.Add(new FadeAnimator(0, 500, DefaultFadeTime));
			overlayManager.Panels.Add(gestureOverlay);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				Book = null;
			}
			overlayManager.Panels.Dispose();
			overlayManager.Dispose();
			base.Dispose(disposing);
		}

		protected virtual void OnBookChanged()
		{
			if (this.BookChanged != null)
			{
				this.BookChanged(this, EventArgs.Empty);
			}
		}

		protected virtual void OnDrawnPageCountChanged()
		{
			UpdateCurrentPageOverlay();
			if (this.DrawnPageCountChanged != null)
			{
				this.DrawnPageCountChanged(this, EventArgs.Empty);
			}
		}

		protected virtual void OnBrowse(BrowseEventArgs e)
		{
			if (this.Browse != null)
			{
				this.Browse(this, e);
			}
		}

		protected virtual void OnPageChange(BookPageEventArgs e)
		{
			if (this.PageChange != null)
			{
				this.PageChange(this, e);
			}
		}

		protected virtual void OnPageChanged(BookPageEventArgs e)
		{
			currentMousePage = -1;
			if (this.PageChanged != null)
			{
				this.PageChanged(this, e);
			}
		}

		protected virtual void OnVisibleInfoOverlaysChanged()
		{
			if (this.VisibleInfoOverlaysChanged != null)
			{
				this.VisibleInfoOverlaysChanged(this, EventArgs.Empty);
			}
		}

		private void CreateWorkingPaperTexture()
		{
			workingPaperTexture.SafeDispose();
			workingPaperTexture = null;
			if (paperTextureBitmap != null && !(paperTextureStrength < 0.05f))
			{
				workingPaperTexture = new Bitmap(paperTextureBitmap.Width, paperTextureBitmap.Height);
				using (Graphics graphics = Graphics.FromImage(workingPaperTexture))
				{
					graphics.Clear(Color.White);
					graphics.DrawImage(PaperTextureBitmap, 0, 0, paperTextureStrength);
				}
			}
		}

		private int GetPageFromPoint(Point pt)
		{
			pt = ClientToImage(pt, withOffset: false);
			for (int i = 0; i < DisplayedPageAreas.Length; i++)
			{
				Rectangle rectangle = DisplayedPageAreas[i];
				if (rectangle.Contains(pt))
				{
					return DisplayedPages[i];
				}
			}
			return CurrentPage;
		}

		private Size CalcNavigationOverlaySize()
		{
			float num = (EngineConfiguration.Default.NavigationPanelWidth * infoOverlayScaling).Clamp(0.2f, 1f);
			return new Size(Math.Max(200, (int)((float)base.ClientRectangle.Width * num)), Math.Max((int)((float)NavigationOverlayDefaultHeight * infoOverlayScaling), 120));
		}

		private static void DrawPageBow(IBitmapRenderer gr, RectangleF rect, bool rightSide)
		{
			rect.Inflate(2f, 0f);
			gr.DrawImage(rightSide ? pageBowRight : pageBowLeft, rect, new RectangleF(0f, 0f, pageBowLeft.Width - 1, pageBowLeft.Height - 1), BitmapAdjustment.Empty, 1f);
		}

		private RectangleF DrawPageOrnaments(IBitmapRenderer gr, Rectangle destination, Rectangle source, RectangleF ri1, RectangleF ri2, bool leftOk, bool rightOk, bool fillLeft, bool fillRight)
		{
			float pageBowWidth = EngineConfiguration.Default.PageBowWidth;
			bool pageBowBorder = EngineConfiguration.Default.PageBowBorder;
			bool pageBowCenter = EngineConfiguration.Default.PageBowCenter;
			float scaleX = (float)destination.Width / (float)source.Width;
			float scaleY = (float)destination.Height / (float)source.Height;
			float num = ri1.Top - (float)source.Y;
			float height = ri1.Height;
			RectangleF rectangleF = Rectangle.Empty;
			if (leftOk)
			{
				RectangleF rectangleF2 = new RectangleF((float)destination.X + ri1.Left - (float)source.X, (float)destination.Y + num, ri1.Width, ri1.Height).Scale(scaleX, scaleY);
				rectangleF = rectangleF.Union(rectangleF2);
				if (fillLeft)
				{
					gr.FillRectangle(rectangleF2, BlankPageColor);
				}
				if (RealisticPages)
				{
					if (pageBowBorder)
					{
						DrawPageBow(gr, new RectangleF((float)destination.X + ri1.Left - (float)source.X, (float)destination.Y + num, ri1.Width * pageBowWidth, height).Scale(scaleX, scaleY), rightSide: false);
					}
					if (pageBowCenter || !rightOk)
					{
						DrawPageBow(gr, new RectangleF((float)destination.X + (ri1.Right - (float)source.X) * (1f - innerBowLeftOffsetInPercent) - ri1.Width * pageBowWidth, (float)destination.Y + num, ri1.Width * pageBowWidth, height).Scale(scaleX, scaleY), rightSide: true);
					}
					gr.DrawRectangle(rectangleF2, Color.Black, 1f);
				}
			}
			if (rightOk)
			{
				RectangleF rectangleF3 = new RectangleF((float)destination.X + ri1.Right + ri2.Right - ri2.Width - (float)source.X, (float)destination.Y + num, ri2.Width, ri1.Height).Scale(scaleX, scaleY);
				rectangleF = rectangleF.Union(rectangleF3);
				if (fillRight)
				{
					gr.FillRectangle(rectangleF3, BlankPageColor);
				}
				if (RealisticPages)
				{
					if (pageBowBorder)
					{
						DrawPageBow(gr, new RectangleF((float)destination.X + ri1.Right + ri2.Right - ri2.Width * pageBowWidth - (float)source.X, (float)destination.Y + num, ri2.Width * pageBowWidth, height).Scale(scaleX, scaleY), rightSide: true);
					}
					if (pageBowCenter || !leftOk)
					{
						float num2 = ri2.Width * pageBowWidth;
						float num3 = ri2.Width * innerBowRightOffsetInPercent;
						if (ri1.Width - num3 < num2)
						{
							num3 = 0f;
						}
						RectangleF rect = new RectangleF((float)destination.X + ri1.Right - (float)source.X + num3, (float)destination.Y + num, num2, height).Scale(scaleX, scaleY);
						DrawPageBow(gr, rect, rightSide: false);
					}
					gr.DrawRectangle(rectangleF3, Color.Black, 1f);
				}
			}
			if (RealisticPages && !rectangleF.IsEmpty)
			{
				int num4 = (int)(Math.Min(rectangleF.Width, rectangleF.Height) * EngineConfiguration.Default.PageShadowWidthPercentage / 100f).Clamp(0f, 255f);
				if (num4 != 0)
				{
					float maxOpacity = EngineConfiguration.Default.PageShadowOpacity.Clamp(0f, 1f);
					if (shadowBitmap == null)
					{
						shadowBitmap = GraphicsExtensions.CreateShadowBitmap(BlurShadowType.Outside, Color.Black, 64, maxOpacity);
					}
					gr.DrawShadow(rectangleF.Pad(-num4), shadowBitmap, num4, BlurShadowParts.Edges);
				}
			}
			return rectangleF;
		}

		private ImageInfo GetImageInfo(int page, PageImage image1, PageImage image2)
		{
			ImageInfo imageInfo = new ImageInfo();
			if (!IsValid)
			{
				return imageInfo;
			}
			try
			{
				int num = SeekPage(page, 1);
				if (!TwoPageDisplay || IsPageSingleType(page) || num == -1 || (image1 != null && image1.Width > image1.Height) || image2 == null || image2.Width > image2.Height || IsPageSingleType(num))
				{
					if (image1 != null)
					{
						Size size = image1.Size;
						if (PageLayout == PageLayoutMode.Double && size.Height > size.Width)
						{
							imageInfo.IsForcedDoublePage = true;
							size.Width += (int)((float)size.Width * (1f - DoublePageOverlap));
						}
						imageInfo.ImageCount = 1;
						imageInfo.Size = size;
						return imageInfo;
					}
					return imageInfo;
				}
				if (image1 != null)
				{
					if (image2 != null)
					{
						int num2 = Math.Max(image1.Height, image2.Height);
						int num3 = image1.Width * num2 / image1.Height;
						int num4 = image2.Width * num2 / image2.Height;
						imageInfo.ImageCount = 2;
						imageInfo.Size = new Size(num3 + num4 - (int)(DoublePageOverlap * (float)num3), num2);
						return imageInfo;
					}
					return imageInfo;
				}
				return imageInfo;
			}
			catch
			{
				return imageInfo;
			}
		}

		private ImageInfo GetImageInfo(int page)
		{
			using (IItemLock<PageImage> itemLock3 = GetImage(page))
			{
				IItemLock<PageImage> itemLock2;
				if (!TwoPageDisplay)
				{
					IItemLock<PageImage> itemLock = new ItemLock<PageImage>(null);
					itemLock2 = itemLock;
				}
				else
				{
					itemLock2 = GetImage(SeekPage(page, 1));
				}
				using (IItemLock<PageImage> itemLock4 = itemLock2)
				{
					return GetImageInfo(page, itemLock3.Item, itemLock4.Item);
				}
			}
		}

		private ImageInfo GetImageInfo()
		{
			return GetImageInfo(CurrentPage);
		}

		private bool IsPageSingleType(int page)
		{
			if (!IsValid)
			{
				return false;
			}
			try
			{
				if (page >= Book.Comic.PageCount)
				{
					return false;
				}
				return Book.Comic.GetPage(page).IsSinglePageType;
			}
			catch
			{
				return false;
			}
		}

		private bool IsPageSingleRightType(int page)
		{
			if (!IsValid)
			{
				return false;
			}
			try
			{
				if (page >= Book.Comic.PageCount)
				{
					return false;
				}
				return Book.Comic.GetPage(page).IsSingleRightPageType;
			}
			catch
			{
				return false;
			}
		}

		private void PositionMagnifier(Point location)
		{
			magnifierOverlay.CenterLocation = location;
			UpdateMagnifierVisibility();
		}

		private void UpdateMagnifierVisibility()
		{
			if (true.Equals(magnifierOverlay.Tag))
			{
				magnifierOverlay.Visible = MagnifierVisible;
			}
			else
			{
				Rectangle clientRectangle = base.ClientRectangle;
				clientRectangle.Inflate(-32, -32);
				magnifierOverlay.Visible = MagnifierVisible && (!autoHideMagnifier || clientRectangle.Contains(magnifierOverlay.CenterLocation));
			}
			if (magnifierOverlay.Visible)
			{
				navigationOverlayVisible = false;
			}
		}

		private void PositionMagnifier()
		{
			PositionMagnifier(PointToClient(Cursor.Position));
		}

		private PageKey GetPageKey(int page)
		{
			if (!IsValid)
			{
				return null;
			}
			PageKey pageKey = Book.GetPageKey(page);
			pageKey.Source = this;
			return pageKey;
		}

		private Size GetContinuousPageSize(int page)
		{
			if (continuousPageSizes.TryGetValue(page, out Size size) && size.Width > 0 && size.Height > 0)
			{
				return size;
			}
			try
			{
				ComicPageInfo pageInfo = Book.Comic.GetPage(page);
				size = new Size(pageInfo.ImageWidth, pageInfo.ImageHeight);
				if (size.Width > 0 && size.Height > 0)
				{
					return size;
				}
			}
			catch
			{
			}
			return new Size(ContinuousFallbackWidth, ContinuousFallbackWidth);
		}

		private int GetContinuousContentWidth()
		{
			if (continuousContentWidth > 0)
			{
				return continuousContentWidth;
			}
			Size size = Size.Empty;
			if (IsValid && CurrentPage >= 0 && CurrentPage < Book.ProviderPageCount)
			{
				using (IItemLock<PageImage> image = pagePool.GetPage(GetPageKey(CurrentPage), onlyMemory: true))
				{
					size = image?.Item?.Size ?? Size.Empty;
				}
			}
			if (size.Width <= 0)
			{
				size = GetContinuousPageSize(CurrentPage);
			}
			continuousContentWidth = size.Width > 0 ? size.Width : ContinuousFallbackWidth;
			return continuousContentWidth;
		}

		private ContinuousPageLayout.Anchor CaptureContinuousViewportAnchor()
		{
			if (continuousViewportRestorePending)
			{
				return continuousViewportAnchor;
			}
			if (TryGetContinuousViewport(out Rectangle viewport))
			{
				return continuousLayout.CaptureAnchor(viewport.Top);
			}
			if (continuousLayout != null)
			{
				return continuousViewportAnchor;
			}
			return new ContinuousPageLayout.Anchor(CurrentPage, 0);
		}

		private bool TryGetContinuousViewport(out Rectangle viewport)
		{
			viewport = Rectangle.Empty;
			if (continuousLayout == null || base.ClientSize.Width <= 0 || base.ClientSize.Height <= 0)
			{
				return false;
			}
			viewport = base.PagePartBounds;
			return viewport.Width > 0 && viewport.Height > 0;
		}

		private long CaptureContinuousHorizontalAnchor()
		{
			Rectangle viewport = base.PagePartBounds;
			int contentWidth = continuousLayout?.TotalSize.Width ?? viewport.Width;
			// Keep twice the center offset so odd source widths retain half-pixel alignment.
			return 2L * viewport.Left + viewport.Width - contentWidth;
		}

		private int ResolveContinuousHorizontalAnchor(long anchor)
		{
			Rectangle viewport = base.PagePartBounds;
			int contentWidth = continuousLayout?.TotalSize.Width ?? viewport.Width;
			long position = (contentWidth + anchor - viewport.Width) / 2L;
			long maximum = Math.Max(0L, (long)contentWidth - viewport.Width);
			return (int)Math.Max(0L, Math.Min(position, maximum));
		}

		private void RebuildContinuousLayout(ContinuousPageLayout.Anchor anchor, bool centerHorizontally = false)
		{
			if (PageLayout != PageLayoutMode.Continuous)
			{
				return;
			}
			long horizontalAnchor = centerHorizontally || continuousLayout == null ? 0L : CaptureContinuousHorizontalAnchor();
			List<ContinuousPageLayout.SourcePage> pages = new List<ContinuousPageLayout.SourcePage>();
			if (Book != null)
			{
				try
				{
					foreach (int page in Book.GetPages())
					{
						pages.Add(new ContinuousPageLayout.SourcePage(page, GetContinuousPageSize(page)));
					}
				}
				catch
				{
				}
			}
			bool preserveSourceSize = base.ImageFitMode == ImageFitMode.Original;
			int contentWidth = preserveSourceSize
				? pages.Where((ContinuousPageLayout.SourcePage page) => page.SourceSize.Width > 0).Select((ContinuousPageLayout.SourcePage page) => page.SourceSize.Width).DefaultIfEmpty(ContinuousFallbackWidth).Max()
				: GetContinuousContentWidth();
			continuousLayout = new ContinuousPageLayout(pages, contentWidth, preserveSourceSize);
			continuousImageFitMode = base.ImageFitMode;
			continuousFitOnlyIfOversized = base.ImageFitOnlyIfOversized;
			int y = continuousLayout.ResolveAnchor(anchor);
			continuousViewportAnchor = continuousLayout.CaptureAnchor(y);
			base.ImageVisiblePart = new ImagePartInfo(0, ResolveContinuousHorizontalAnchor(horizontalAnchor), y);
			Invalidate();
		}

		private void ScheduleContinuousLayoutRebuild(ContinuousPageLayout.Anchor anchor)
		{
			if (PageLayout != PageLayoutMode.Continuous || IsDisposed)
			{
				return;
			}
			continuousLayoutRebuildAnchor = anchor;
			continuousViewportRestorePending = true;
			if (continuousLayoutRebuildPending)
			{
				return;
			}
			ContinuousPageLayout scheduledLayout = continuousLayout;
			continuousLayoutRebuildPending = true;
			MethodInvoker rebuild = delegate
			{
				continuousLayoutRebuildPending = false;
				try
				{
					if (PageLayout == PageLayoutMode.Continuous && !IsDisposed && continuousLayout == scheduledLayout)
					{
						RebuildContinuousLayout(continuousLayoutRebuildAnchor);
					}
				}
				finally
				{
					continuousViewportRestorePending = false;
				}
			};
			if (!IsHandleCreated)
			{
				rebuild();
				return;
			}
			try
			{
				BeginInvoke(rebuild);
			}
			catch (InvalidOperationException)
			{
				continuousLayoutRebuildPending = false;
				continuousViewportRestorePending = false;
			}
		}

		private IItemLock<PageImage> GetContinuousImage(int page)
		{
			if (!IsValid || page < 0 || page >= Book.ProviderPageCount)
			{
				return new ItemLock<PageImage>(null);
			}
			PageKey pageKey = GetPageKey(page);
			IItemLock<PageImage> image = pagePool.GetPage(pageKey, onlyMemory: true);
			if (image == null)
			{
				pagePool.CachePage(pageKey, fastMem: true, Book, bottom: false);
				return new ItemLock<PageImage>(null);
			}
			image.Tag = true;
			firstPageHasBeenLoaded = true;
			return image;
		}

		private bool IsPageInCache(int page, int offset = 0, bool fastMem = true, bool putInCache = true)
		{
			int num = SeekPage(page, offset);
			if (num == -1 || num == page)
			{
				return true;
			}
			PageKey pageKey = GetPageKey(num);
			using (IItemLock<PageImage> itemLock = pagePool.GetPage(pageKey, fastMem))
			{
				if (itemLock != null && itemLock.Item != null)
				{
					return true;
				}
			}
			if (putInCache && Book != null)
			{
				pagePool.CachePage(pageKey, fastMem, Book, bottom: false);
			}
			return false;
		}

		private int CachePage(int page, int offset, bool fastMem, bool bottom)
		{
			int num = SeekPage(page, offset);
			if (num == page)
			{
				num = -1;
			}
			if (num != -1)
			{
				pagePool.CachePage(GetPageKey(num), fastMem, Book, bottom);
			}
			return num;
		}

		private bool CacheBackPage(ref int page, int offset)
		{
			int num = CachePage(page, offset, fastMem: true, bottom: true);
			if (num == -1)
			{
				return false;
			}
			page = num;
			return true;
		}

		private int SeekPage(int page, int offset)
		{
			if (Book == null)
			{
				return -1;
			}
			return Book.SeekNextPage(page, Math.Abs(offset), Math.Sign(offset));
		}

		public IItemLock<PageImage> GetImage(int page, bool withCaching = false)
		{
			bool flag = PreCache && withCaching;
			if (!IsValid || page < 0 || page > Book.ProviderPageCount)
			{
				return new ItemLock<PageImage>(null);
			}
			PageKey pageKey = GetPageKey(page);
			IItemLock<PageImage> page2 = pagePool.GetPage(pageKey, onlyMemory: true);
			if (page2 == null)
			{
				PagePool.CachePage(pageKey, fastMem: true, book, bottom: false);
			}
			if (flag)
			{
				CachePage(page, 1, fastMem: true, bottom: false);
				InvalidatePendingImageCacheUpdate();
			}
			if (page2 != null)
			{
				lastValidKey = pageKey;
				firstPageHasBeenLoaded = true;
				page2.Tag = true;
				return page2;
			}
			if (lastValidKey != null)
			{
				page2 = pagePool.GetPage(lastValidKey, onlyMemory: false);
			}
			return page2 ?? new ItemLock<PageImage>(null);
		}

		private void InvalidatePendingImageCacheUpdate()
		{
			cacheUpdateTimer.Stop();
			cacheUpdateTimer.Start();
		}

		private void StopPendingImageCacheUpdate()
		{
			cacheUpdateTimer.Stop();
		}

		private void cacheUpdateTimer_Tick(object sender, EventArgs e)
		{
			cacheUpdateTimer.Stop();
			if (!IsValid)
			{
				return;
			}
			try
			{
				int num = CurrentPage;
				int num2 = (pagePool.MaximumMemoryItems - 15) / 2;
				int page = CachePage(num, 1, fastMem: true, bottom: false);
				int page2 = num;
				bool flag = page != -1;
				bool flag2 = true;
				while (num2 > 0 && (!flag || !(flag = CacheBackPage(ref page, 1)) || --num2 != 0) && (!flag || !(flag = CacheBackPage(ref page, 1)) || --num2 != 0) && (!flag2 || !(flag2 = CacheBackPage(ref page2, -1)) || --num2 != 0) && (flag || flag2))
				{
				}
			}
			catch (Exception)
			{
			}
		}

		private Matrix4 GetMatrix(System.Drawing.Drawing2D.Matrix matrix)
		{
			float[] elements = matrix.Elements;
			return new Matrix4(elements[0], elements[2], 0f, elements[4], elements[1], elements[3], 0f, elements[5], 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f);
		}

		private System.Drawing.Drawing2D.Matrix GetMatrix(Matrix4 matrix)
		{
			return new System.Drawing.Drawing2D.Matrix(matrix[0], matrix[1], matrix[4], matrix[5], matrix[12], matrix[13]);
		}

		private bool DrawPage(IBitmapRenderer gr, IItemLock<PageImage> il, RectangleF rd, RectangleF rs)
		{
			float opacity = 1f;
			if (il.Tag is bool && (bool)il.Tag)
			{
				if (drawBlankPagesOverride)
				{
					gr.FillRectangle(rd, EngineConfiguration.Default.BlankPageColor);
					opacity = 0.1f;
				}
				try
				{
					bool flag = false;
					if (gr.IsHardware && SoftwareFiltering && !inBlendAnmation)
					{
						Matrix4 matrix = GetMatrix(gr.Transform);
						Matrix4.Decompose(matrix, out var _, out var scaling, out var _);
						float num = (float)Math.Round(scaling[0], 4);
						float num2 = (float)Math.Round(scaling[1], 4);
						float softwareFilterMinScale = EngineConfiguration.Default.SoftwareFilterMinScale;
						if (num >= softwareFilterMinScale && num2 >= softwareFilterMinScale && (num < 1f || num2 < 1f))
						{
							using (IItemLock<ScaledPageItem> itemLock = GetScaledPage(il.Item, num, num2))
							{
								if (itemLock != null && itemLock.Item.IsValid)
								{
									ScaledPageItem item = itemLock.Item;
									Size size = il.Item.Size;
									num = (float)item.Width / (float)size.Width;
									num2 = (float)item.Height / (float)size.Height;
									rs.X *= num;
									rs.Y *= num2;
									rs.Width *= num;
									rs.Height *= num2;
									rd.Width *= num / scaling[0];
									rd.Height *= num2 / scaling[1];
									((IHardwareRenderer)gr).OptimizedTextures = true;
									try
									{
										gr.DrawImage(item, rd, rs, BitmapAdjustment.Empty, opacity);
									}
									finally
									{
										((IHardwareRenderer)gr).OptimizedTextures = false;
									}
									flag = true;
								}
							}
						}
					}
					if (!flag)
					{
						gr.DrawImage(il.Item, rd, rs, BitmapAdjustment.Empty, opacity);
					}
				}
				catch (Exception)
				{
				}
				return true;
			}
			gr.FillRectangle(rd, Color.FromArgb(128, BackColor));
			return false;
		}

		private IItemLock<ScaledPageItem> GetScaledPage(PageImage bmp, float sx, float sy)
		{
			if (bmp == null)
			{
				return null;
			}
			if (scaledCache == null)
			{
				scaledCache = new Cache<ScaledPageKey, ScaledPageItem>(2);
				scaledCache.MinimalTimeInCache = 0;
				scaledCache.ItemRemoved += delegate(object s, CacheItemEventArgs<ScaledPageKey, ScaledPageItem> e)
				{
					e.Item.Dispose();
				};
			}
			ScaledPageKey key = new ScaledPageKey
			{
				ScaleX = sx,
				ScaleY = sy,
				Bitmap = bmp
			};
			IItemLock<ScaledPageItem> itemLock = scaledCache.LockItem(key, (ScaledPageKey b) => new ScaledPageItem());
			int num = EngineConfiguration.Default.SoftwareFilterDelay.Clamp(100, 5000);
			long ticks = Machine.Ticks;
			if (!itemLock.Item.IsValid)
			{
				if (itemLock.Item.Ticks != 0L && ticks - itemLock.Item.Ticks > num)
				{
					Size size = new Size((int)Math.Round((float)bmp.Width * sx), (int)Math.Round((float)bmp.Height * sy));
					itemLock.Item.Optimized = PageImage.MemoryOptimized;
					itemLock.Item.Bitmap = bmp.Bitmap.Resize(size, EngineConfiguration.Default.SoftwareFilter);
				}
				else
				{
					itemLock.Item.Ticks = ticks;
					imageScaleTimer.Interval = num + 100;
					imageScaleTimer.Stop();
					imageScaleTimer.Start();
				}
			}
			return itemLock;
		}

		private void imageScaleTimer_Tick(object sender, EventArgs e)
		{
			imageScaleTimer.Stop();
			Invalidate();
		}

		public override Bitmap CreatePageImage()
		{
			if (PageLayout == PageLayoutMode.Continuous)
			{
				using (IItemLock<PageImage> image = GetContinuousImage(CurrentPage))
				{
					Bitmap bitmap = image?.Item?.Bitmap;
					return bitmap == null ? null : new Bitmap(bitmap);
				}
			}
			bool flag = RealisticPages;
			RealisticPages = false;
			try
			{
				return base.CreatePageImage();
			}
			finally
			{
				RealisticPages = flag;
			}
		}

		private void DrawContinuousImage(IBitmapRenderer gr, Rectangle destination, Rectangle source)
		{
			ContinuousPageLayout layout = continuousLayout;
			if (layout == null || source.Width <= 0 || source.Height <= 0)
			{
				return;
			}
			if (!continuousViewportRestorePending)
			{
				continuousViewportAnchor = layout.CaptureAnchor(source.Top);
			}
			float destinationScaleX = (float)destination.Width / source.Width;
			float destinationScaleY = (float)destination.Height / source.Height;
			List<int> visiblePages = new List<int>();
			List<Rectangle> visibleAreas = new List<Rectangle>();
			int hash = 17;
			foreach (ContinuousPageLayout.PageEntry page in layout.GetVisible(source))
			{
				Rectangle intersection = Rectangle.Intersect(page.Bounds, source);
				if (intersection.Width <= 0 || intersection.Height <= 0)
				{
					continue;
				}
				RectangleF target = new RectangleF(destination.X + (intersection.X - source.X) * destinationScaleX, destination.Y + (intersection.Y - source.Y) * destinationScaleY, intersection.Width * destinationScaleX, intersection.Height * destinationScaleY);
				using (IItemLock<PageImage> image = GetContinuousImage(page.Page))
				{
					if (image.Item == null)
					{
						DrawPage(gr, image, target, RectangleF.Empty);
					}
					else
					{
						Size actualSize = image.Item.Size;
						if (actualSize.Width > 0 && actualSize.Height > 0)
						{
							if (actualSize != page.SourceSize)
							{
								continuousPageSizes[page.Page] = actualSize;
								continuousContentWidth = 0;
								ScheduleContinuousLayoutRebuild(continuousViewportAnchor);
							}
							float sourceScaleX = (float)actualSize.Width / page.Bounds.Width;
							float sourceScaleY = (float)actualSize.Height / page.Bounds.Height;
							RectangleF pageSource = new RectangleF((intersection.X - page.Bounds.X) * sourceScaleX, (intersection.Y - page.Bounds.Y) * sourceScaleY, intersection.Width * sourceScaleX, intersection.Height * sourceScaleY);
							DrawPage(gr, image, target, pageSource);
							hash = unchecked(hash * 31 + image.Item.GetHashCode());
						}
					}
				}
				visiblePages.Add(page.Page);
				visibleAreas.Add(target.Round());
			}
			if (visiblePages.Count != 0)
			{
				CachePage(visiblePages[0], -1, fastMem: true, bottom: false);
				CachePage(visiblePages[visiblePages.Count - 1], 1, fastMem: true, bottom: false);
				int lastVisiblePage = visiblePages.Max();
				if (Book != null && lastVisiblePage > Book.LastPageRead)
				{
					Book.LastPageRead = lastVisiblePage;
				}
			}
			int previousHash = displayHash;
			displayHash = hash;
			displayedPages = visiblePages.ToArray();
			displayedPageAreas = visibleAreas.ToArray();
			if (previousHash != displayHash)
			{
				OnDisplayChanged();
			}
		}

		protected override void DrawImage(IBitmapRenderer gr, Rectangle destination, Rectangle source, bool clipToDestination)
		{
			if (!IsValid)
			{
				return;
			}
			if (PageLayout == PageLayoutMode.Continuous)
			{
				DrawContinuousImage(gr, destination, source);
				return;
			}
			int num = displayHash;
			int num2 = CurrentPage;
			int nextPage = NextPage;
			int[] array = new int[0];
			Rectangle[] array2 = new Rectangle[0];
			using (IItemLock<PageImage> itemLock = GetImage(num2, withCaching: true))
			{
				using (IItemLock<PageImage> itemLock2 = GetImage(nextPage, withCaching: true))
				{
					if (itemLock.Item == null)
					{
						displayHash = 0;
					}
					else
					{
						ImageInfo imageInfo = GetImageInfo(num2, itemLock.Item, itemLock2.Item);
						SizeF sizeF = imageInfo.Size;
						if (!clipToDestination)
						{
							float num3 = (float)source.Width / (float)destination.Width;
							float num4 = (float)source.Height / (float)destination.Height;
							destination = destination.Pad(-(int)(num3 * (float)source.X), -(int)(num4 * (float)source.Top), -(int)(num3 * (sizeF.Width - (float)source.Right)), -(int)(num4 * (sizeF.Height - (float)source.Bottom)));
							source = new Rectangle(Point.Empty, sizeF.ToSize());
						}
						if (imageInfo.IsSingleImage && !imageInfo.IsForcedDoublePage)
						{
							DrawPage(gr, itemLock, destination, source);
							//Always work out where the page ended up on screen. DrawPageOrnaments only
							//draws bows, borders and shadows when Realistic Pages is on, but it also
							//returns the page bounds, which the paper texture needs either way. Leaving
							//this out kept the bounds from the previously shown page, so the texture
							//covered the wrong area until the layout was changed.
							if (imageInfo.IsDoublePage)
							{
								RectangleF rectangleF = new RectangleF(0f, 0f, (float)itemLock.Item.Width / 2f, itemLock.Item.Height);
								displayedPageBounds = DrawPageOrnaments(gr, destination, source, rectangleF, rectangleF, leftOk: true, rightOk: true, fillLeft: false, fillRight: false);
							}
							else
							{
								RectangleF rectangleF2 = new RectangleF(0f, 0f, itemLock.Item.Width, itemLock.Item.Height);
								displayedPageBounds = DrawPageOrnaments(gr, destination, source, rectangleF2, rectangleF2, leftOk: true, rightOk: false, fillLeft: false, fillRight: false);
							}
							displayHash = itemLock.Item.GetHashCode();
							array = new int[1]
							{
								num2
							};
							array2 = new Rectangle[1]
							{
								destination
							};
						}
						else
						{
							bool flag = base.RightToLeftReading && base.RightToLeftReadingMode == RightToLeftReadingMode.FlipPages;
							bool flag2 = IsPageSingleType(num2) && nextPage == -1;
							bool flag3 = ((num2 != 0 && !IsPageSingleRightType(num2) && flag) || flag2) ^ IsFlipped;
							bool a = !imageInfo.IsSingleImage;
							bool b = true;
							bool flag4 = false;
							bool flag5 = false;
							IItemLock<PageImage> a2 = itemLock;
							IItemLock<PageImage> b2 = itemLock2;
							if (imageInfo.IsSingleImage)
							{
								b2 = a2;
							}
							if (flag3)
							{
								CloneUtility.Swap(ref a2, ref b2);
								CloneUtility.Swap(ref a, ref b);
							}
							Size size = a2.Item.Size;
							Size size2 = b2.Item.Size;
							float num5 = sizeF.Height / (float)size.Height;
							float num6 = sizeF.Height / (float)size2.Height;
							RectangleF ri = new RectangleF(0f, 0f, size.Width, size.Height).Scale(num5);
							RectangleF ri2 = new RectangleF(0f, 0f, size2.Width, size2.Height).Scale(num6);
							float num7 = DoublePageOverlap * ri.Width;
							if (num2 == 0 || flag2)
							{
								flag3 = !flag3;
							}
							if (flag3)
							{
								ri.Width -= num7;
							}
							else
							{
								ri2.Width -= num7;
							}
							RectangleF rect = new RectangleF(source.X, source.Y, Math.Min(ri.Right, source.Right) - (float)source.X, source.Height);
							RectangleF rect2 = new RectangleF(rect.Right - ri.Width, rect.Y, (float)source.Right - rect.Right, rect.Height);
							if (!flag3)
							{
								rect2.X += num7;
							}
							RectangleF rect3 = new RectangleF((float)destination.X + rect.Left / (float)source.Width, destination.Y, (float)destination.Width * rect.Width / (float)source.Width, destination.Height);
							RectangleF rect4 = new RectangleF(rect3.Right, destination.Y, (float)destination.Right - rect3.Right, destination.Height);
							rect = rect.Scale(1f / num5);
							rect2 = rect2.Scale(1f / num6);
							using (gr.SaveState())
							{
								if (a)
								{
									gr.ScaleTransform(num5, num5);
									a = DrawPage(gr, a2, rect3.Scale(1f / num5), rect);
								}
								else
								{
									flag4 = num2 != 0 && !flag2;
								}
							}
							using (gr.SaveState())
							{
								if (b)
								{
									gr.ScaleTransform(num6, num6);
									b = DrawPage(gr, b2, rect4.Scale(1f / num6), rect2);
								}
								else
								{
									flag5 = num2 != 0 && !flag2;
								}
							}
							displayedPageBounds = DrawPageOrnaments(gr, destination, source, ri, ri2, a || flag4, b || flag5, flag4, flag5);
							displayHash = a2.Item.GetHashCode() ^ (b2.Item.GetHashCode() << 1);
							List<int> list = new List<int>();
							List<Rectangle> list2 = new List<Rectangle>();
							if (a)
							{
								list.Add(num2);
								list2.Add(rect3.Round());
							}
							if (b)
							{
								list.Add(a ? nextPage : num2);
								list2.Add(rect4.Round());
							}
							array = list.ToArray();
							array2 = list2.ToArray();
							if (flag3)
							{
								Array.Reverse(array);
							}
						}
					}
				}
			}
			if (num != displayHash)
			{
				OnDisplayChanged();
			}
			ComicBookNavigator comicBookNavigator = Book;
			if (comicBookNavigator != null)
			{
				int num8 = ((nextPage != -1) ? nextPage : num2);
				if (num8 > comicBookNavigator.LastPageRead)
				{
					comicBookNavigator.LastPageRead = num8;
				}
			}
			displayedPages = array;
			displayedPageAreas = array2;
		}

		protected override void RenderImageEffect(IBitmapRenderer bitmapRenderer, DisplayOutput display)
		{
			if (PageLayout == PageLayoutMode.Continuous)
			{
				return;
			}
			base.RenderImageEffect(bitmapRenderer, display);
			if (bitmapRenderer.IsHardware && workingPaperTexture != null)
			{
				IHardwareRenderer hardwareRenderer = bitmapRenderer as IHardwareRenderer;
				hardwareRenderer.BlendingOperation = BlendingOperation.Multiply;
				try
				{
					hardwareRenderer.FillRectangle(workingPaperTexture, PaperTextureLayout, displayedPageBounds, paperTextureBitmap.Size.ToRectangle(), BitmapAdjustment.Empty, 1f);
				}
				catch (Exception)
				{
				}
				hardwareRenderer.BlendingOperation = BlendingOperation.Blend;
			}
		}

		protected virtual void OnDisplayChanged()
		{
			if (!base.DisplayEventsDisabled)
			{
				UpdatePartOverlay(always: true);
				navigationOverlay.IsDoublePage = IsDoubleImage;
			}
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			DragTurnMouseDown(e);
			if (e.Button == MouseButtons.Left && IsMouseOk(e.Location))
			{
				if (!MagnifierVisible)
				{
					mouseDown = e.Location;
					longClickTimer.Start();
				}
				if (NavigationOverlayVisible)
				{
					NavigationOverlayVisible = false;
					base.MouseActionHappened = true;
				}
				ShowGestureIndicator(e.Location);
			}
			currentMousePage = GetPageFromPoint(e.Location);
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			if (!mouseDown.IsEmpty)
			{
				mouseDown = Point.Empty;
				longClickTimer.Stop();
			}
			if (true.Equals(magnifierOverlay.Tag))
			{
				MagnifierVisible = false;
				magnifierOverlay.Tag = false;
				base.DisableScrolling = false;
			}
			base.OnMouseUp(e);
			DragTurnMouseUp();
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			DragTurnMouseMove(e);
			if (!mouseDown.IsEmpty && (Math.Abs(mouseDown.X - e.X) > 5 || Math.Abs(mouseDown.Y - e.Y) > 5))
			{
				longClickTimer.Stop();
				mouseDown = Point.Empty;
			}
			if (magnifierOverlay.Visible)
			{
				Cursor = (Cursor.Current = EmptyCursor);
			}
			else
			{
				Cursor = (Cursor.Current = Cursors.Default);
			}
			UpdateNavigationOverlay();
			PositionMagnifier(e.Location);
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			base.OnMouseLeave(e);
			longClickTimer.Stop();
			UpdateNavigationOverlay(new Point(-1, -1));
			PositionMagnifier();
		}

		protected override void OnGestureStart()
		{
			base.MouseActionHappened = false;
			base.OnGestureStart();
			if (magnifierOverlay.Visible && !magnifierOverlay.Bounds.Contains(base.GestureLocation))
			{
				MagnifierVisible = false;
			}
		}

		protected override void OnPanStart()
		{
			base.OnPanStart();
			panMagnifier = false;
			if (magnifierOverlay.Visible && magnifierOverlay.Bounds.Contains(base.GestureLocation))
			{
				panMagnifier = true;
				base.MouseActionHappened = true;
			}
			continuousPanStartLocation = base.GestureLocation;
			continuousPanStartOffset = base.ImageVisiblePart.Offset;
			continuousPanDirectionLocked = false;
			continuousPanVertical = false;
		}

		protected override void OnPan()
		{
			base.OnPan();
			if (panMagnifier)
			{
				PositionMagnifier(base.PanLocation);
				base.MouseActionHappened = true;
			}
		}

		protected override void OnPageDisplayModeChanged()
		{
			bool continuous = PageLayout == PageLayoutMode.Continuous && continuousLayout != null;
			bool fitSettingsChanged = continuous && (continuousImageFitMode != base.ImageFitMode || continuousFitOnlyIfOversized != base.ImageFitOnlyIfOversized);
			ContinuousPageLayout.Anchor anchor = fitSettingsChanged ? continuousViewportAnchor : default;
			if (fitSettingsChanged)
			{
				continuousFitResetPending = false;
				continuousViewportRestorePending = true;
			}
			if (continuous)
			{
				continuousImageFitMode = base.ImageFitMode;
				continuousFitOnlyIfOversized = base.ImageFitOnlyIfOversized;
			}
			base.OnPageDisplayModeChanged();
			if (fitSettingsChanged && PageLayout == PageLayoutMode.Continuous && continuousLayout != null)
			{
				RebuildContinuousLayout(anchor, centerHorizontally: true);
				// ImageDisplayControl clears the visible part after this callback. If
				// that would move the viewport, replace the reset synchronously in
				// OnVisiblePartChanged so the page-start frame is never rendered.
				continuousFitResetPending = base.ImageVisiblePart != Display.GetBestPartFit(ImagePartInfo.Empty);
				if (!continuousFitResetPending)
				{
					continuousViewportRestorePending = continuousLayoutRebuildPending;
				}
			}
			else if (fitSettingsChanged)
			{
				continuousFitResetPending = false;
				continuousViewportRestorePending = false;
			}
			else if (PageLayout == PageLayoutMode.Continuous && continuousLayout != null && !continuousViewportRestorePending)
			{
				Rectangle viewport = base.PagePartBounds;
				int centeredLeft = ResolveContinuousHorizontalAnchor(0L);
				if (viewport.Left != centeredLeft)
				{
					base.ImageVisiblePart = new ImagePartInfo(0, centeredLeft, viewport.Top);
				}
			}
			UpdatePartOverlay(always: true);
		}

		protected override void OnVisiblePartChanged()
		{
			if (continuousFitResetPending)
			{
				continuousFitResetPending = false;
				if (PageLayout == PageLayoutMode.Continuous && continuousLayout != null)
				{
					int y = continuousLayout.ResolveAnchor(continuousViewportAnchor);
					base.ImageVisiblePart = new ImagePartInfo(0, ResolveContinuousHorizontalAnchor(0L), y);
					continuousViewportRestorePending = continuousLayoutRebuildPending;
					return;
				}
				continuousViewportRestorePending = false;
			}
			base.OnVisiblePartChanged();
			if (PageLayout == PageLayoutMode.Continuous && !continuousViewportRestorePending && TryGetContinuousViewport(out Rectangle viewport))
			{
				ContinuousPageLayout.PageEntry page = continuousLayout.HitTest(viewport.Top);
				if (page != null)
				{
					continuousViewportAnchor = continuousLayout.CaptureAnchor(viewport.Top);
					if (!continuousNavigationSync && Book != null && page.Page != CurrentPage)
					{
						continuousNavigationSync = true;
						try
						{
							Book.Navigate(page.Page, PageSeekOrigin.Absolute);
						}
						finally
						{
							continuousNavigationSync = false;
						}
					}
				}
			}
			UpdatePartOverlay(always: false);
		}

		protected override void OnResize(EventArgs e)
		{
			bool restoreContinuousAnchor = PageLayout == PageLayoutMode.Continuous && continuousLayout != null;
			ContinuousPageLayout.Anchor anchor = restoreContinuousAnchor ? continuousViewportAnchor : default;
			if (restoreContinuousAnchor)
			{
				continuousViewportRestorePending = true;
			}
			base.OnResize(e);
			navigationOverlay.Size = CalcNavigationOverlaySize();
			if (navigationOverlay.Visible)
			{
				navigationOverlay.Y = NavigationOverlayVisibleY;
				navigationOverlay.X = (base.ClientRectangle.Width - navigationOverlay.Width) / 2;
			}
			UpdatePartOverlay(always: true);
			if (restoreContinuousAnchor && TryGetContinuousViewport(out _))
			{
				int y = continuousLayout.ResolveAnchor(anchor);
				continuousViewportAnchor = continuousLayout.CaptureAnchor(y);
				base.ImageVisiblePart = new ImagePartInfo(0, ResolveContinuousHorizontalAnchor(0L), y);
				continuousViewportRestorePending = continuousLayoutRebuildPending;
			}
			else if (!restoreContinuousAnchor)
			{
				continuousViewportRestorePending = false;
			}
		}

		protected override Point AdjustPanOffset(Point offset)
		{
			offset = base.AdjustPanOffset(offset);
			if (PageLayout != PageLayoutMode.Continuous || panMagnifier)
			{
				return offset;
			}

			int horizontalDistance = Math.Abs(base.PanLocation.X - continuousPanStartLocation.X);
			int verticalDistance = Math.Abs(base.PanLocation.Y - continuousPanStartLocation.Y);
			if (!continuousPanDirectionLocked && Math.Max(horizontalDistance, verticalDistance) >= Math.Max(4, SystemInformation.DragSize.Width))
			{
				continuousPanDirectionLocked = true;
				// Favor the strip's reading axis unless the gesture is clearly horizontal.
				continuousPanVertical = verticalDistance * 4 >= horizontalDistance * 3;
			}
			if (!continuousPanDirectionLocked || continuousPanVertical)
			{
				offset.X = continuousPanStartOffset.X;
			}
			return offset;
		}

		protected override void OnRenderImageOverlay(RenderEventArgs e)
		{
			base.OnRenderImageOverlay(e);
			UpdateCurrentPageOverlay();
			UpdateMessageOverlay();
			if (messageOverlay.Visible)
			{
				loadPageOverlay.Visible = false;
			}
			else
			{
				UpdateLoadPageOverlay();
			}
			overlayManager.Draw(e.Graphics);
			if (blindOut)
			{
				e.Graphics.FillRectangle(base.ClientRectangle, blindOutColor);
			}
		}

		protected override bool IsInputKey(Keys keyData)
		{
			switch (keyData)
			{
			case Keys.Tab:
			case Keys.End:
			case Keys.Home:
			case Keys.Left:
			case Keys.Up:
			case Keys.Right:
			case Keys.Down:
			case Keys.Tab | Keys.Shift:
				return true;
			default:
				return base.IsInputKey(keyData);
			}
		}

		protected override Color GetAutoBackgroundColor()
		{
			try
			{
				if (IsValid)
				{
					using (IItemLock<PageImage> itemLock = GetImage(CurrentPage))
					{
						if (itemLock.Item.BackgrounColor.IsEmpty)
						{
							Bitmap bitmap = ((itemLock.Item != null) ? itemLock.Item.Bitmap : null);
							if (bitmap != null)
							{
								Color[] array = new Color[4]
								{
									bitmap.GetAverageColor(2, 2, 4),
									bitmap.GetAverageColor(bitmap.Width - 2 - 4, 2, 4),
									bitmap.GetAverageColor(bitmap.Width - 2 - 4, bitmap.Height - 2 - 4, 4),
									bitmap.GetAverageColor(2, bitmap.Height - 2 - 4, 4)
								};
								if (array.GetAverage().GetBrightness() < 0.5f)
								{
									itemLock.Item.BackgrounColor = array.Max((Color a, Color b) => a.GetBrightness().CompareTo(b.GetBrightness()));
								}
								else
								{
									itemLock.Item.BackgrounColor = array.Max((Color a, Color b) => b.GetBrightness().CompareTo(a.GetBrightness()));
								}
							}
						}
						return itemLock.Item.BackgrounColor;
					}
				}
			}
			catch
			{
			}
			return Color.Empty;
		}

		protected override bool IsImageValid()
		{
			if (PageLayout == PageLayoutMode.Continuous)
			{
				return continuousLayout != null && continuousLayout.TotalHeight > 0L;
			}
			return GetImageInfo().IsValid;
		}

		protected override DisplayOutputConfig GetEffectiveDisplayConfig(DisplayOutputConfig config)
		{
			if (PageLayout != PageLayoutMode.Continuous)
			{
				return config;
			}
			ImageFitMode imageDisplayMode = config.ImageDisplayMode;
			bool fitOnlyIfOversized = config.FitOnlyIfOversized;
			if (imageDisplayMode == ImageFitMode.BestFit || imageDisplayMode == ImageFitMode.FitHeight || imageDisplayMode == ImageFitMode.Fit)
			{
				imageDisplayMode = ImageFitMode.FitWidth;
			}
			// RTL still controls page navigation, but a vertical strip must not mirror
			// its horizontal viewport.
			return new DisplayOutputConfig(config.ViewSize, config.ImageSize, imageDisplayMode, fitOnlyIfOversized, config.RightToLeftReadingMode, rightToLeftReading: false, config.Part, config.ImageZoom, config.ImageZoom, ImageRotation.None, twoPageAutoScroll: false);
		}

		protected override Size GetImageSize()
		{
			if (PageLayout == PageLayoutMode.Continuous)
			{
				return continuousLayout?.TotalSize ?? Size.Empty;
			}
			return GetImageInfo().Size;
		}

		protected override void OnDoubleClick(EventArgs e)
		{
			if (!MouseHandled)
			{
				base.OnDoubleClick(e);
			}
		}

		protected override bool IsMouseOk(Point point)
		{
			return overlayManager.Panels.Find((OverlayPanel x) => x.HasMouse) == null;
		}

		protected override void OnImageDisplayOptionsChanged()
		{
			UpdateNavigationOverlay(redraw: false);
		}

		protected override void OnReadingModeChanged()
		{
			base.OnReadingModeChanged();
			navigationOverlay.Mirror = IsMovementFlipped;
		}

		private void ShowGestureIndicator(Point pt)
		{
			if (!EngineConfiguration.Default.ShowGestureHint)
			{
				return;
			}
			GestureArea gestureArea = GestureHitTest(pt);
			if (gestureArea != null)
			{
				GestureEventArgs gestureEventArgs = new GestureEventArgs(GestureType.Touch)
				{
					Area = gestureArea.Alignment,
					AreaBounds = gestureArea.Area,
					Double = false
				};
				OnPreviewGesture(gestureEventArgs);
				if (!gestureEventArgs.Handled)
				{
					gestureEventArgs.Double = true;
					OnPreviewGesture(gestureEventArgs);
				}
				if (gestureEventArgs.Handled)
				{
					gestureOverlay.Alignment = gestureArea.Alignment;
					gestureOverlay.Size = gestureArea.Area.Size;
					gestureOverlay.Opacity = 1f;
					Update();
					gestureOverlay.Animators[0].Start();
				}
			}
		}

		public void DisplayOpenMessage()
		{
			firstPageHasBeenLoaded = false;
		}

		private void UpdateNavigationOverlay(bool redraw)
		{
			if (IsValid)
			{
				navigationOverlay.Pages = Book.GetPages().ToArray();
				if ((Control.MouseButtons & MouseButtons.Left) == 0)
				{
					navigationOverlay.DisplayedPageIndex = ((IList)navigationOverlay.Pages).IndexOf((object)CurrentPage);
				}
				navigationOverlay.IsDoublePage = IsDoubleImage;
				navigationOverlay.Caption = book.Comic.Caption;
				if (redraw)
				{
					navigationOverlay.Invalidate();
				}
			}
		}

		private void UpdatePartOverlay(bool always)
		{
			if (PageLayout == PageLayoutMode.Continuous)
			{
				visiblePartOverlay.Visible = false;
				return;
			}
			int num = CurrentPage * 100 + base.ImageVisiblePart.Part;
			Point offset = base.ImageVisiblePart.Offset;
			if (num == cachedPartOverlay && cachedPartOffset == offset && !always)
			{
				return;
			}
			using (ItemMonitor.Lock(visiblePartOverlay))
			{
				if (ImagePartCount != 1 || visiblePartOverlay.IsVisible)
				{
					if (!IsImageValid())
					{
						smallBitmap.SafeDispose();
						smallBitmap = null;
					}
					else if (IsPartInfoOverlayEnabled)
					{
						visiblePartOverlay.Animators[0].Start();
					}
				}
			}
			cachedPartOverlay = num;
			cachedPartOffset = offset;
		}

		private void UpdateCurrentPageOverlay()
		{
			UpdateCurrentPageOverlay(DisplayedPages);
		}

		private void UpdateCurrentPageOverlay(IEnumerable<int> pageNumbers)
		{
			if (Book == null || pageNumbers == null)
			{
				return;
			}
			int[] array = pageNumbers.Where((int n) => n >= 0).ToArray();
			if (array.Length == 0 || currentPageOverlayHash == DisplayHash)
			{
				return;
			}
			currentPageOverlayHash = DisplayHash;
			string number = ((array.Length == 1) ? (array[0] + 1).ToString() : $"{array[0] + 1}/{array[1] + 1}");
			string text = ComicBook.FormatNumber(number, Book.IsIndexRetrievalCompleted ? Book.ProviderPageCount : (-1));
			if (CurrentPageShowsName)
			{
				text += "<small>";
				text = text.AppendWithSeparator("<br/>", Book.GetImageName(Book.Comic.TranslatePageToImageIndex(array[0]), noPath: true).ToXmlString());
				if (array.Length > 1)
				{
					text = text.AppendWithSeparator("<br/>", Book.GetImageName(Book.Comic.TranslatePageToImageIndex(array[1]), noPath: true).ToXmlString());
				}
				text += "</small>";
			}
			currentPageOverlay.Text = text;
			if (IsCurrentPageOverlayEnabled)
			{
				currentPageOverlay.Animators[0].Start();
			}
		}

		private void UpdateLoadPageOverlay()
		{
			if (!IsLoadPageOverlayEnabled || !IsValid || CurrentPage < 0)
			{
				loadPageOverlay.Visible = false;
				return;
			}
			int page = CurrentPage;
			int nextPage = NextPage;
			bool flag = false;
			bool flag2 = false;
			using (IItemLock<PageImage> itemLock = GetImage(page))
			{
				using (IItemLock<PageImage> itemLock2 = GetImage(NextPage))
				{
					ImageInfo imageInfo = GetImageInfo(page, itemLock.Item, itemLock2.Item);
					flag = itemLock.Item == null || !true.Equals(itemLock.Tag);
					if (!imageInfo.IsSingleImage)
					{
						flag2 = itemLock2.Item == null || !true.Equals(itemLock2.Tag);
					}
				}
			}
			if (!flag && !flag2)
			{
				loadPageOverlay.Visible = false;
				return;
			}
			string text = string.Empty;
			if (flag)
			{
				text = (CurrentPage + 1).ToString();
			}
			if (flag2 && nextPage != -1)
			{
				text = text.AppendWithSeparator(", ", (nextPage + 1).ToString());
			}
			if (!string.IsNullOrEmpty(text))
			{
				UpdateLoadPageOverlay(text);
			}
		}

		private void UpdateLoadPageOverlay(string pageText)
		{
			if (Book != null && !string.IsNullOrEmpty(pageText))
			{
				loadPageOverlay.Text = string.Format(TR.Messages["LoadingPage", "Loading Page {0}..."], pageText);
				loadPageOverlay.Visible = true;
			}
		}

		private void UpdateMessageOverlay()
		{
			string text = null;
			Bitmap bitmap = null;
			if (Book == null)
			{
				text = TR.Messages["NoComicOpen", "No book is open"];
			}
			else
			{
				if (Book.ProviderStatus == ImageProviderStatus.Error)
				{
					text = StringUtility.Format(TR.Messages["OpenError", "Could not open the book '{0}'!"], Book.Comic.DisplayFileLocation);
				}
				else if (!firstPageHasBeenLoaded)
				{
					text = StringUtility.Format(TR.Messages["OpeningComic", "Opening the book '{0}'..."], Book.Comic.DisplayFileLocation);
				}
				if (text != null && ThumbnailPool != null)
				{
					IItemLock<ThumbnailImage> thumbnail;
					using (thumbnail = ThumbnailPool.GetThumbnail(Book.Comic.GetFrontCoverThumbnailKey(), onlyMemory: true))
					{
						if (thumbnail != null && thumbnail.Item != null)
						{
							if (messageOverlay.Tag == thumbnail.Item && messageOverlay.Icon != null)
							{
								bitmap = messageOverlay.Icon;
							}
							else
							{
								messageOverlay.Tag = thumbnail.Item;
								bitmap = ComicBox3D.CreateDefaultBook(thumbnail.Item.GetThumbnail(128), null, new Size(128, 128), Book.Comic.PageCount);
							}
						}
					}
				}
			}
			if (messageOverlay.Icon != bitmap)
			{
				Bitmap icon = messageOverlay.Icon;
				messageOverlay.Icon = bitmap;
				icon?.Dispose();
			}
			messageOverlay.Text = text;
			messageOverlay.Visible = !string.IsNullOrEmpty(text) && showStatusMessage;
		}

		private void DrawMagnifier(IBitmapRenderer gr, Point location, Rectangle mrc, float zoom)
		{
			DisplayOutput displayOutput = base.LastRenderedDisplay ?? base.Display;
			Rectangle rectangle = mrc;
			int num3 = (mrc.Width = (mrc.Height = Math.Max(mrc.Height, mrc.Width)));
			Rectangle source = mrc;
			source.Width = (int)((float)source.Width / displayOutput.Scale.Width / zoom);
			source.Height = (int)((float)source.Height / displayOutput.Scale.Height / zoom);
			source.Offset(ClientToImage(displayOutput, location));
			source.Offset(-source.Width / 2, -source.Height / 2);
			using (gr.SaveState())
			{
				using (gr.SaveState())
				{
					gr.TranslateTransform((float)rectangle.Width / 2f, (float)rectangle.Height / 2f);
					gr.ScaleTransform(zoom, zoom);
					gr.TranslateTransform(-location.X, -location.Y);
					RenderImageBackground(gr, null);
				}
				gr.TranslateTransform((float)mrc.Width / 2f - (float)(mrc.Width - rectangle.Width) / 2f, (float)mrc.Height / 2f - (float)(mrc.Height - rectangle.Height) / 2f);
				gr.RotateTransform(displayOutput.Config.Rotation.ToDegrees());
				gr.TranslateTransform((float)(-mrc.Width) / 2f, (float)(-mrc.Height) / 2f);
				DrawImage(gr, mrc, source, clipToDestination: true);
				RenderImageEffect(gr, null);
			}
		}

		private void magnifierOverlay_RenderSurface(object sender, PanelRenderEventArgs e)
		{
			Magnifier magnifier = magnifiers[(int)MagnifierStyle];
			IBitmapRenderer renderer = e.Renderer;
			Padding padding = magnifier.Outer.GetPadding(magnifier.Bitmap.Size);
			Padding padding2 = magnifier.Inner.GetPadding(magnifier.Bitmap.Size);
			Rectangle rectangle = magnifierOverlay.ClientRectangle.Pad(padding);
			Point location = magnifierOverlay.Location;
			location.Offset(rectangle.Location);
			location.Offset(rectangle.Width / 2, rectangle.Height / 2);
			location = location.Clip(base.ClientRectangle);
			float zoom = MagnifierZoom;
			renderer.Opacity = MagnifierOpacity;
			RectangleF clip = renderer.Clip;
			renderer.Clip = rectangle;
			DrawMagnifier(renderer, location, rectangle, zoom);
			renderer.Clip = clip;
			renderer.Opacity = 1f;
			ScalableBitmap.Draw(renderer, magnifier.Bitmap, magnifierOverlay.ClientRectangle, padding2, 1f);
		}

		private void visiblePartOverlay_Drawing(object sender, EventArgs e)
		{
			Size size = base.ImageSize.ToRectangle(partInfoSize, RectangleScaleMode.None).Size;
			size.Width += 16;
			size.Height += 16;
			if (!(visiblePartOverlay.Size == size))
			{
				visiblePartOverlay.Size = size;
				using (PanelSurface panelSurface = visiblePartOverlay.CreateSurface())
				{
					panelSurface.Graphics.Clear(Color.Transparent);
					partRect = PanelRenderer.DrawGraphics(panelSurface.Graphics, new Rectangle(Point.Empty, size), 1f);
				}
			}
		}

		private void visiblePartOverlay_RenderSurface(object sender, PanelRenderEventArgs e)
		{
			IBitmapRenderer renderer = e.Renderer;
			Size targetSize = partRect.Size.ToSize();
			Size imageSize = GetImageSize();
			if (imageSize.IsEmpty)
			{
				return;
			}
			Rectangle r = imageSize.ToRectangle(targetSize);
			r.Offset((int)partRect.Left, (int)partRect.Top);
			if (DoublePageOverlap == 0f && (currentPartHash != displayHash || smallBitmap == null))
			{
				Image image = smallBitmap;
				try
				{
					smallBitmap = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
					Bitmap bitmap = (((base.ImageDisplayOptions & ImageDisplayOptions.HighQuality) != 0) ? new Bitmap(smallBitmap.Width * 2, smallBitmap.Height * 2) : smallBitmap);
					using (Graphics graphics = Graphics.FromImage(bitmap))
					{
						graphics.ScaleTransform((float)bitmap.Width / (float)imageSize.Width, (float)bitmap.Height / (float)imageSize.Height);
						DrawImage(new BitmapGdiRenderer(graphics)
						{
							LowQualityInterpolation = InterpolationMode.Low
						}, imageSize.ToRectangle(), imageSize.ToRectangle());
					}
					if (bitmap != smallBitmap)
					{
						using (Graphics graphics2 = Graphics.FromImage(smallBitmap))
						{
							graphics2.InterpolationMode = InterpolationMode.HighQualityBicubic;
							graphics2.DrawImage(bitmap, smallBitmap.Size.ToRectangle());
						}
						bitmap.Dispose();
					}
				}
				catch (Exception)
				{
				}
				image?.Dispose();
				currentPartHash = displayHash;
			}
			Rectangle pagePartBounds = base.PagePartBounds;
			Rectangle rectangle = pagePartBounds.Scale(imageSize.GetScale(targetSize));
			Rectangle r2 = rectangle;
			r2.Offset(r.Location);
			renderer.Opacity = visiblePartOverlay.Opacity;
			if (smallBitmap != null)
			{
				try
				{
					renderer.DrawImage(smallBitmap, r, new Rectangle(0, 0, smallBitmap.Width, smallBitmap.Height), BitmapAdjustment.Empty, 0.3f);
					renderer.DrawImage(smallBitmap, r2, rectangle, BitmapAdjustment.Empty, 1f);
				}
				catch (Exception)
				{
				}
			}
			renderer.Opacity = 1f;
		}

		private void MemoryPageCache_ItemAdded(object sender, CacheItemEventArgs<ImageKey, PageImage> e)
		{
			if (IsValid && (object.Equals(e.Key, GetPageKey(CurrentPage)) || (TwoPageDisplay && object.Equals(e.Key, GetPageKey(NextPage))) || !DisplayedPages.Where((int dp) => object.Equals(e.Key, GetPageKey(dp))).IsEmpty()))
			{
				Invalidate();
			}
		}

		private void book_Disposing(object sender, EventArgs e)
		{
			Book = null;
		}

		private void book_Navigation(object sender, BookPageEventArgs e)
		{
			if (PageLayout == PageLayoutMode.Continuous)
			{
				Blender = null;
				ShouldPagingBlend = false;
				OnPageChange(e);
				currentPage = Book.CurrentPage;
				if (!continuousNavigationSync)
				{
					long horizontalAnchor = CaptureContinuousHorizontalAnchor();
					int y = continuousLayout?.ResolveAnchor(new ContinuousPageLayout.Anchor(currentPage, 0)) ?? 0;
					continuousViewportAnchor = new ContinuousPageLayout.Anchor(currentPage, 0);
					base.ImageVisiblePart = new ImagePartInfo(0, ResolveContinuousHorizontalAnchor(horizontalAnchor), y);
				}
				Invalidate();
				OnPageChanged(e);
				UpdateNavigationOverlay(redraw: false);
				lastBlend = Machine.Ticks;
				return;
			}
			bool flag = e.OldPage < e.Page;
			if (IsFlipped)
			{
				flag = !flag;
			}
			switch (PageTransitionEffect)
			{
			default:
				Blender = null;
				break;
			case PageTransitionEffect.Fade:
				Blender = FadeInBlending;
				break;
			case PageTransitionEffect.LeftRight:
				if (flag)
				{
					Blender = ScrollToLeftBlending;
				}
				else
				{
					Blender = ScrollToRightBlending;
				}
				break;
			case PageTransitionEffect.TopDown:
				if (flag)
				{
					Blender = ScrollToTopBlending;
				}
				else
				{
					Blender = ScrollToBottomBlending;
				}
				break;
			case PageTransitionEffect.Paging:
				if (flag)
				{
					Blender = PageForward;
				}
				else
				{
					Blender = PageBackward;
				}
				break;
			case PageTransitionEffect.PageCurl:
				//Works out direction and reading order itself.
				Blender = PageCurlBlending;
				break;
			}
			ShouldPagingBlend = !suppressNavigationBlend && !base.InvokeRequired && (BlendWhilePaging || Machine.Ticks - lastBlend > 100);
			OnPageChange(e);
			int oldPage = CurrentPage;
			DisplayOutputConfig displayConfig = base.DisplayConfig;
			currentPage = Book.CurrentPage;
			int part = 0;
			int num = ((!TwoPageDisplay) ? 1 : 2);
			if (e.OldPage != -1 && Math.Abs(e.OldPage - e.Page) <= num)
			{
				part = ((e.OldPage >= e.Page) ? (ImagePartCount - 1) : 0);
			}
			base.ImageVisiblePart = new ImagePartInfo(part);
			if (ShouldPagingBlend)
			{
				BlendAnimation(oldPage, displayConfig);
			}
			else
			{
				Invalidate();
			}
			OnPageChanged(e);
			UpdateNavigationOverlay(redraw: false);
			Update();
			lastBlend = Machine.Ticks;
		}

		private void book_PageFilterOrPagesChanged(object sender, EventArgs e)
		{
			if (PageLayout == PageLayoutMode.Continuous)
			{
				continuousPageSizes.Clear();
				continuousContentWidth = 0;
				RebuildContinuousLayout(CaptureContinuousViewportAnchor());
			}
			Invalidate();
			UpdateNavigationOverlay(redraw: true);
		}

		private void Comic_BookChanged(object sender, BookChangedEventArgs e)
		{
			navigationOverlay.Caption = book.Comic.Caption;
		}

		private void book_IndexOfPageReady(object sender, BookPageEventArgs e)
		{
			try
			{
				UpdateNavigationOverlay(redraw: true);
				if (e.Page == CurrentPage)
				{
					UpdateCurrentPageOverlay();
					Invalidate();
				}
			}
			catch (Exception)
			{
			}
		}

		private void book_IndexRetrievalCompleted(object sender, EventArgs e)
		{
			if (PageLayout == PageLayoutMode.Continuous)
			{
				continuousContentWidth = 0;
				RebuildContinuousLayout(CaptureContinuousViewportAnchor());
			}
			UpdateNavigationOverlay(redraw: true);
			Invalidate();
		}

		private void book_ColorAdjustmentChanged(object sender, EventArgs e)
		{
			Invalidate();
		}

		private void book_RightToLeftReadingChanged(object sender, EventArgs e)
		{
			if (Book.RightToLeftReading != YesNo.Unknown)
			{
				base.RightToLeftReading = Book.RightToLeftReading == YesNo.Yes;
			}
		}

		private void longClickTimer_Tick(object sender, EventArgs e)
		{
			if (AutoMagnifier)
			{
				magnifierOverlay.Tag = true;
				MagnifierVisible = true;
				base.MouseActionHappened = true;
				base.DisableScrolling = true;
			}
			longClickTimer.Stop();
		}

		private void UpdateNavigationOverlay()
		{
			UpdateNavigationOverlay(PointToClient(Cursor.Position));
		}

		private void UpdateNavigationOverlay(Point pt)
		{
			if (!IsValid)
			{
				navigationOverlay.Visible = false;
				return;
			}
			Rectangle clientRectangle = base.ClientRectangle;
			Rectangle bounds = navigationOverlay.Bounds;
			Size size = new Size(500, 50);
			if (IsPageBrowsersOnTop)
			{
				UpdateNavigationOverlay(pt, navigationOverlay, new Point((clientRectangle.Width - bounds.Width) / 2, -bounds.Height), new Point((clientRectangle.Width - bounds.Width) / 2, NavigationOverlayVisibleY), new Rectangle((clientRectangle.Width - size.Width) / 2, 0, size.Width, size.Height));
			}
			else
			{
				UpdateNavigationOverlay(pt, navigationOverlay, new Point((clientRectangle.Width - bounds.Width) / 2, clientRectangle.Height), new Point((clientRectangle.Width - bounds.Width) / 2, NavigationOverlayVisibleY), new Rectangle((clientRectangle.Width - size.Width) / 2, clientRectangle.Height - size.Height, size.Width, size.Height));
			}
		}

		private void UpdateNavigationOverlay(Point pt, OverlayPanel panel, Point start, Point end, Rectangle hotBounds)
		{
			bool flag = IsImageValid() && (NavigationOverlayVisible || (IsNavigationOverlayEnabled && (panel.HasMouse || (Control.MouseButtons == MouseButtons.None && !magnifierOverlay.Visible && hotBounds.Contains(pt)))));
			if (!flag.Equals(panel.Tag) && (flag || panel.Visible))
			{
				overlayManager.AnimationEnabled = true;
				panel.Animators.Clear();
				if (!flag)
				{
					CloneUtility.Swap(ref start, ref end);
				}
				if (!panel.Visible)
				{
					panel.Location = start;
					panel.Visible = true;
				}
				panel.Animators.Add(new MoveAnimator(flag ? 300 : 200, panel.Location, end, !flag));
				panel.Tag = flag;
				panel.Animators.Start();
			}
		}

		public void BlendAnimation(int oldPage, DisplayOutputConfig oldConfig, BlendAnimationHandler blender, BlendAnimationMode mode = BlendAnimationMode.Default)
		{
			if (renderer != null && renderer.IsHardware && !disableBlending && blender != null && base.Visible)
			{
				base.DisplayEventsDisabled = true;
				try
				{
					if (mode != 0)
					{
						oldPage = currentPage;
					}
					int num = 50;
					while (!IsPageInCache(oldPage) || !IsPageInCache(oldPage, 1) || !IsPageInCache(currentPage) || !IsPageInCache(currentPage, 1))
					{
						if (--num < 0)
						{
							return;
						}
						Thread.Sleep(50);
					}
					DisplayOutputConfig displayConfig = base.DisplayConfig;
					displayConfig.Rotation = base.LastRenderedDisplay.Config.Rotation;
					DisplayOutput display = DisplayOutput.Create(displayConfig, base.CurrentAnamorphicTolerance);
					DisplayOutput oldOut = (oldConfig.IsEmpty ? display : DisplayOutput.Create(oldConfig, base.CurrentAnamorphicTolerance));
					switch (mode)
					{
					case BlendAnimationMode.CurrentAsNew:
						oldOut = null;
						break;
					case BlendAnimationMode.CurrentAsOld:
						oldOut = display;
						display = null;
						break;
					}
					inBlendAnmation = true;
					renderer.BeginScene(null);
					try
					{
						using (renderer.SaveState())
						{
							blender(renderer, oldPage, oldOut, display, 0f);
						}
						RenderImageOverlay(renderer, display ?? oldOut);
					}
					finally
					{
						renderer.EndScene();
					}
					ThreadUtility.Animate(GetBlendDuration(blender), delegate(float x)
					{
						IBitmapRenderer bitmapRenderer = renderer;
						try
						{
							bitmapRenderer.BeginScene(null);
							using (bitmapRenderer.SaveState())
							{
								blender(bitmapRenderer, oldPage, oldOut, display, x);
							}
							RenderImageOverlay(bitmapRenderer, display ?? oldOut);
						}
						catch (Exception e2)
						{
							if (HandleRendererError(e2))
							{
								bitmapRenderer = null;
							}
						}
						finally
						{
							try
							{
								bitmapRenderer.EndScene();
							}
							catch
							{
							}
						}
					});
				}
				catch (Exception e)
				{
					HandleRendererError(e);
				}
				finally
				{
					base.DisplayEventsDisabled = false;
					inBlendAnmation = false;
				}
			}
			Invalidate();
		}

		public void BlendAnimation(int oldPage, DisplayOutputConfig oldConfig)
		{
			BlendAnimation(oldPage, oldConfig, Blender);
		}

		private void RenderImageBackground(IBitmapRenderer bitmapRenderer, DisplayOutput output, int page = -1)
		{
			int num = currentPage;
			if (page != -1)
			{
				currentPage = page;
			}
			try
			{
				base.RenderImageBackground(bitmapRenderer, output);
			}
			finally
			{
				currentPage = num;
			}
		}

		public void FadeInBlending(IBitmapRenderer hr, int oldPage, DisplayOutput oldOut, DisplayOutput display, float percent)
		{
			float opacity = hr.Opacity;
			float num = percent.Clamp(0.05f, 1f);
			RectangleF clip = hr.Clip;
			if (currentPage < oldPage)
			{
				RenderImageBackground(hr, display);
				if (!base.IsConstantBackground)
				{
					hr.Opacity = 1f - num;
					RenderImageBackground(hr, oldOut, oldPage);
				}
				hr.Opacity = num;
				RenderImageSafe(hr, display, withBackground: false);
				hr.Opacity = 1f;
				hr.Clip = oldOut.OutputBoundsScreen;
				RenderImageSafe(hr, display, withBackground: false);
				hr.Clip = clip;
				hr.Opacity = 1f - num;
				RenderImageSafe(hr, oldOut, oldPage, withBackground: false);
			}
			else
			{
				RenderImageBackground(hr, oldOut, oldPage);
				if (!base.IsConstantBackground)
				{
					hr.Opacity = num;
					RenderImageBackground(hr, display);
				}
				hr.Opacity = 1f - num;
				RenderImageSafe(hr, oldOut, oldPage, withBackground: false);
				hr.Opacity = 1f;
				hr.Clip = display.OutputBoundsScreen;
				RenderImageSafe(hr, oldOut, oldPage, withBackground: false);
				hr.Clip = clip;
				hr.Opacity = num;
				RenderImageSafe(hr, display, withBackground: false);
			}
			hr.Opacity = opacity;
		}

		public void PageForward(IBitmapRenderer hr, int oldPage, DisplayOutput oldOut, DisplayOutput display, float percent)
		{
			bool flag = true;
			bool flag2 = oldOut.OutputBoundsScreen.Width >= oldOut.OutputBoundsScreen.Height;
			bool flag3 = oldOut.Config.Rotation != 0 || display.Config.Rotation != ImageRotation.None;
			bool flag4 = !flag2 || flag3;
			Rectangle rectangle;
			if (!base.IsConstantBackground || flag3)
			{
				rectangle = base.ClientRectangle;
			}
			else
			{
				rectangle = Rectangle.Union(display.OutputBoundsScreen, oldOut.OutputBoundsScreen).Pad(-10);
				RenderImageBackground(hr, null);
				flag = false;
				hr.Clip = rectangle;
			}
			Rectangle rectangle2 = rectangle;
			float num = (float)rectangle2.Width * percent;
			RenderImageSafe(hr, oldOut, oldPage, flag ? RenderType.Default : RenderType.WithoutBackground);
			if (flag4)
			{
				num *= 2f;
				drawBlankPagesOverride = true;
			}
			innerBowLeftOffsetInPercent = 1f - percent;
			hr.TranslateTransform((float)rectangle2.Width - num, 0f);
			RenderImageSafe(hr, display, flag);
			hr.TranslateTransform(0f - ((float)rectangle2.Width - num), 0f);
			innerBowLeftOffsetInPercent = 0f;
			drawBlankPagesOverride = false;
			if (num > 5f)
			{
				hr.Clip = new RectangleF((float)rectangle2.Right - num / 2f, rectangle2.Top, num / 2f + 1f, rectangle2.Height + 1);
				RenderImageSafe(hr, display);
			}
			hr.Clip = Rectangle.Empty;
		}

		public void PageBackward(IBitmapRenderer hr, int oldPage, DisplayOutput oldOut, DisplayOutput display, float percent)
		{
			if (EngineConfiguration.Default.MirroredPageTurnAnimation)
			{
				int oldPage2 = currentPage;
				currentPage = oldPage;
				PageForward(hr, oldPage2, display, oldOut, 1f - percent);
				currentPage = oldPage2;
				return;
			}
			bool withBackground = true;
			bool flag = oldOut.OutputBoundsScreen.Width >= oldOut.OutputBoundsScreen.Height;
			bool flag2 = oldOut.Config.Rotation != 0 || display.Config.Rotation != ImageRotation.None;
			bool flag3 = !flag || flag2;
			Rectangle rectangle;
			if (!base.IsConstantBackground || flag2)
			{
				rectangle = base.ClientRectangle;
			}
			else
			{
				rectangle = Rectangle.Union(display.OutputBoundsScreen, oldOut.OutputBoundsScreen).Pad(-10);
				RenderImageBackground(hr, null);
				hr.Clip = rectangle;
				withBackground = false;
			}
			Rectangle rectangle2 = rectangle;
			float num = (float)rectangle2.Width * percent;
			RenderImageSafe(hr, oldOut, oldPage, withBackground);
			if (flag3)
			{
				num *= 2f;
				drawBlankPagesOverride = true;
			}
			innerBowRightOffsetInPercent = 1f - percent;
			hr.TranslateTransform(0f - ((float)rectangle2.Width - num), 0f);
			RenderImageSafe(hr, display, withBackground);
			hr.TranslateTransform((float)rectangle2.Width - num, 0f);
			drawBlankPagesOverride = false;
			innerBowRightOffsetInPercent = 0f;
			if (num > 5f)
			{
				hr.Clip = new RectangleF(rectangle2.Left, rectangle2.Top, num / 2f + 1f, rectangle2.Height + 1);
				RenderImageSafe(hr, display);
			}
			hr.Clip = Rectangle.Empty;
		}

		#region Realistic page curl

		//The turning sheet is modelled as a strip of paper hinged at the spine. It is cut into thin
		//vertical slices; every slice gets its own 3D angle (so the sheet can bend), a perspective
		//size, and a shade from its angle to the viewer. Each slice is then drawn as the matching
		//column of the page with an ordinary scale transform and a clip, which is all the renderer
		//interface offers, so this works on the Direct2D and the OpenGL renderer alike.

		private const float PageCurlBend = 0.9f;

		private static readonly Color PageCurlPaperColor = Color.FromArgb(246, 243, 236);

		public void PageCurlBlending(IBitmapRenderer hr, int oldPage, DisplayOutput oldOut, DisplayOutput display, float percent)
		{
			if (oldOut == null || display == null)
			{
				FadeInBlending(hr, oldPage, oldOut, display, percent);
				return;
			}
			float t = percent.Clamp(0f, 1f);
			//Any right-to-left book turns from the left, whichever way its spreads are arranged.
			bool mirrored = base.RightToLeftReading;
			if (hr is IGeometryClipRenderer clipper)
			{
				//Same paper as a mouse drag, with the page taken by an invisible hand: the corner
				//is carried across to the far side along a slight arc, so the sheet bends and
				//lifts instead of pivoting like card.
				bool peelRight = (currentPage >= oldPage) != mirrored;
				GetPeelGeometry(oldOut, peelRight, out bool _, out RectangleF sheet, out RectangleF _, out float spine);
				if (sheet.Width > 8f && sheet.Height > 8f)
				{
					PointF corner = new PointF(peelRight ? sheet.Right : sheet.Left, sheet.Top + sheet.Height * 0.72f);
					PointF landed = new PointF(2f * spine - corner.X, corner.Y);
					float eased = t * t * (3f - 2f * t);
					//A hand lifts the page a little as it carries it over.
					float arc = 0f - sheet.Height * 0.12f * (float)Math.Sin(Math.PI * eased);
					PointF hand = new PointF(corner.X + (landed.X - corner.X) * eased, corner.Y + (landed.Y - corner.Y) * eased + arc);
					hand = ConstrainPeel(hand, corner, sheet, spine);
					RenderCornerPeel(hr, clipper, oldOut, oldPage, display, currentPage, peelRight, corner, hand);
					return;
				}
			}
			if (currentPage >= oldPage)
			{
				//Forward: the old page is the sheet that turns away and uncovers the new one.
				RenderPageCurl(hr, oldOut, oldPage, display, currentPage, t, mirrored);
			}
			else
			{
				//Backward: the same motion played in reverse, so the previous page swings back
				//over the current one, just like turning back in a real book.
				RenderPageCurl(hr, display, currentPage, oldOut, oldPage, 1f - t, mirrored);
			}
		}

		private void GetCurlGeometry(DisplayOutput sheetOut, bool mirrored, out bool spread, out int sgn, out float spine, out float width)
		{
			Rectangle page = sheetOut.OutputBoundsScreen;
			//A spread (two pages side by side) turns around its middle, a single page around its
			//edge. Adaptive layouts can show a lone portrait page in two page mode, which the
			//aspect ratio check catches.
			spread = TwoPageDisplay && page.Width >= page.Height * 0.9f;
			sgn = mirrored ? -1 : 1;
			spine = spread ? (page.Left + page.Width / 2f) : (mirrored ? page.Right : page.Left);
			width = spread ? (page.Width / 2f) : page.Width;
		}

		private void RenderPageCurl(IBitmapRenderer hr, DisplayOutput sheetOut, int sheetPage, DisplayOutput underOut, int underPage, float t, bool mirrored, bool ease = true)
		{
			Rectangle client = base.ClientRectangle;
			Rectangle page = sheetOut.OutputBoundsScreen;
			GetCurlGeometry(sheetOut, mirrored, out bool spread, out int sgn, out float spine, out float width);
			float opacity = hr.Opacity;
			System.Drawing.Drawing2D.Matrix baseTransform = hr.Transform;
			try
			{
				if (width < 8f || page.Height < 8)
				{
					RenderImageSafe(hr, underOut, underPage, RenderType.Default);
					return;
				}
				//Animations ease in and out; a page held by the mouse follows the mouse exactly.
				float eased = ease ? (t * t * (3f - 2f * t)) : t.Clamp(0f, 1f);
				float theta = (float)Math.PI * eased;
				float sinTheta = (float)Math.Sin(theta);
				RectangleF allowed = RectangleF.FromLTRB(page.Left, client.Top, page.Right, client.Bottom);

				//1. What lies underneath: the new page where the sheet came from, and in a spread
				//   the facing page of the old spread that the sheet will land on.
				RenderImageBackground(hr, underOut, underPage);
				if (spread)
				{
					RectangleF uncovered = (sgn > 0) ? RectangleF.FromLTRB(spine, client.Top, client.Right, client.Bottom) : RectangleF.FromLTRB(client.Left, client.Top, spine, client.Bottom);
					RectangleF facing = (sgn > 0) ? RectangleF.FromLTRB(client.Left, client.Top, spine, client.Bottom) : RectangleF.FromLTRB(spine, client.Top, client.Right, client.Bottom);
					SetCurlClip(hr, baseTransform, uncovered);
					RenderImageSafe(hr, underOut, underPage, RenderType.WithoutBackground);
					SetCurlClip(hr, baseTransform, facing);
					RenderImageSafe(hr, sheetOut, sheetPage, RenderType.WithoutBackground);
				}
				else
				{
					SetCurlClip(hr, baseTransform, RectangleF.Empty);
					RenderImageSafe(hr, underOut, underPage, RenderType.WithoutBackground);
				}

				//2. Shape of the sheet. Arc length u runs from the spine to the free edge. The free
				//   edge leads, like a page lifted by its edge, and the bend straightens out again
				//   as the sheet lands.
				int slices = Math.Max(20, Math.Min(48, (int)(width / 14f)));
				float step = width / slices;
				float[] along = new float[slices + 1];
				float[] height = new float[slices + 1];
				float[] angle = new float[slices];
				for (int i = 0; i < slices; i++)
				{
					float u = (i + 0.5f) / slices;
					float phi = Math.Min((float)Math.PI, theta + PageCurlBend * sinTheta * u * u);
					angle[i] = phi;
					along[i + 1] = along[i] + (float)Math.Cos(phi) * step;
					height[i + 1] = height[i] + (float)Math.Sin(phi) * step;
				}
				float centerX = client.Left + client.Width / 2f;
				float centerY = page.Top + page.Height / 2f;
				float eye = 4f * Math.Max(width, page.Height);
				float[] screenX = new float[slices + 1];
				float[] scale = new float[slices + 1];
				for (int i = 0; i <= slices; i++)
				{
					scale[i] = eye / Math.Max(eye * 0.25f, eye - height[i]);
					screenX[i] = centerX + (spine + sgn * along[i] - centerX) * scale[i];
				}

				//3. Soft shadow the lifted sheet throws next to its free edge.
				if (sinTheta > 0.02f)
				{
					float edge = screenX[slices];
					int side = Math.Sign(edge - spine);
					if (side == 0)
					{
						side = sgn;
					}
					float shadowWidth = width * 0.18f * sinTheta;
					float strength = 0.45f * sinTheta;
					SetCurlClip(hr, baseTransform, allowed);
					const int bands = 10;
					for (int k = 0; k < bands; k++)
					{
						float a = edge + side * shadowWidth * k / bands;
						float b = edge + side * shadowWidth * (k + 1) / bands;
						float fade = 1f - (float)k / bands;
						hr.Opacity = strength * fade * fade;
						hr.FillRectangle(RectangleF.FromLTRB(Math.Min(a, b), page.Top, Math.Max(a, b), page.Bottom), Color.Black);
					}
				}

				//4. The sheet itself, slice by slice, furthest from the viewer first.
				int[] order = Enumerable.Range(0, slices).OrderBy((int i) => height[i] + height[i + 1]).ToArray();
				foreach (int i in order)
				{
					float x0 = screenX[i];
					float x1 = screenX[i + 1];
					if (Math.Abs(x1 - x0) < 0.25f)
					{
						continue;
					}
					float s = (scale[i] + scale[i + 1]) / 2f;
					bool front = (x1 - x0) * sgn > 0f;
					RectangleF target = RectangleF.FromLTRB(Math.Min(x0, x1) - 0.35f, centerY + (page.Top - centerY) * s, Math.Max(x0, x1) + 0.35f, centerY + (page.Bottom - centerY) * s);
					target.Intersect(allowed);
					if (target.Width <= 0f || target.Height <= 0f)
					{
						continue;
					}
					float u0 = step * i;
					float u1 = step * (i + 1);
					//The back of a sheet in a spread is the first page of the next spread, which
					//sits on the other side of the spine once the sheet has landed.
					bool useBack = !front && spread;
					float src0 = useBack ? (spine - sgn * u0) : (spine + sgn * u0);
					float src1 = useBack ? (spine - sgn * u1) : (spine + sgn * u1);
					float scaleX = (x1 - x0) / (src1 - src0);
					SetCurlClip(hr, baseTransform, target);
					if (!front && !spread)
					{
						//A single page has no next page on its back: show paper with the print
						//faintly showing through.
						hr.Opacity = 1f;
						hr.FillRectangle(target, PageCurlPaperColor);
					}
					using (System.Drawing.Drawing2D.Matrix slice = new System.Drawing.Drawing2D.Matrix(scaleX, 0f, 0f, s, x0 - scaleX * src0, centerY - s * centerY))
					{
						System.Drawing.Drawing2D.Matrix m = baseTransform.Clone();
						m.Multiply(slice);
						hr.Transform = m;
						hr.Opacity = (front || spread) ? 1f : 0.12f;
						RenderImageSafe(hr, useBack ? underOut : sheetOut, useBack ? underPage : sheetPage, RenderType.WithoutBackground);
						hr.Transform = baseTransform;
						m.Dispose();
					}
					//Light: facing the viewer is fully lit, edge on is darkest.
					float shade = (1f - Math.Abs((float)Math.Cos(angle[i]))) * 0.6f;
					if (shade > 0.02f)
					{
						hr.Opacity = shade;
						hr.FillRectangle(target, Color.Black);
					}
				}
			}
			finally
			{
				hr.Transform = baseTransform;
				hr.Clip = RectangleF.Empty;
				hr.Opacity = opacity;
			}
		}

		private static void SetCurlClip(IBitmapRenderer hr, System.Drawing.Drawing2D.Matrix baseTransform, RectangleF rect)
		{
			//Clips are set in screen space, before any slice transform is applied. Setting them
			//under a mirrored transform confuses the OpenGL renderer's scissor math.
			hr.Transform = baseTransform;
			hr.Clip = rect;
		}

		private int GetBlendDuration(BlendAnimationHandler blender)
		{
			if (blender != null && blender.Target == this && blender.Method.Name == nameof(PageCurlBlending))
			{
				return EngineConfiguration.Default.PageCurlDuration;
			}
			return EngineConfiguration.Default.BlendDuration;
		}

		#endregion

		#region Turning pages by dragging

		//Grab a page near its outer edge and drag it towards the spine: the page follows the mouse
		//with the same curl as the Realistic Page Curl transition. Let go past about a third of the
		//way (or flick) and the turn completes; otherwise the page falls back.
		//
		//The book is actually moved to the new page as soon as the drag starts, with the usual
		//transition switched off, so both sides of the sheet are known. A cancelled drag moves it
		//back the same way.

		private enum DragTurnState
		{
			None,
			Pending,
			Active
		}

		private const int DragTurnStartDistance = 8;

		private DragTurnState dragTurnState;

		private Point dragTurnStart;

		private bool dragTurnForward;

		private int dragTurnOriginalPage;

		private DisplayOutput dragSheetOut;

		private DisplayOutput dragUnderOut;

		private int dragSheetPage;

		private int dragUnderPage;

		private float dragTurnT;

		private float dragTurnOffset;

		private float dragTurnVelocity;

		private long dragTurnLastTicks;

		private bool dragTurnSuppressPaint;

		private bool suppressNavigationBlend;

		[DefaultValue(true)]
		public bool DragPageTurning
		{
			get;
			set;
		} = true;

		private bool CanDragTurn()
		{
			return DragPageTurning && renderer != null && renderer.IsHardware && Book != null && PageLayout != PageLayoutMode.Continuous && !MagnifierVisible && !inBlendAnmation && base.Display != null && base.Display.IsAllVisible;
		}

		/// <summary>
		/// +1 when the point is on the edge that turns to the next page, -1 on the edge that turns
		/// back, 0 elsewhere.
		/// </summary>
		private int HitDragTurnZone(Point pt)
		{
			Rectangle page = base.Display.OutputBoundsScreen;
			if (page.Width < 40 || !page.Contains(pt))
			{
				return 0;
			}
			//Roughly the outer 60% of each page can be grabbed. In a spread that is 30% of the
			//whole width, measured from each outer edge, so the inner thirds near the gutter are
			//left alone.
			float zone = Math.Max(40f, page.Width * (TwoPageDisplay ? 0.3f : 0.6f));
			bool right = pt.X >= page.Right - zone;
			bool left = pt.X <= page.Left + zone;
			if (!right && !left)
			{
				return 0;
			}
			if (right && left)
			{
				//Wide zones overlap in the middle: grab whichever edge is nearer.
				right = page.Right - pt.X <= pt.X - page.Left;
				left = !right;
			}
			bool forward = right != base.RightToLeftReading;
			if (!Book.CanNavigate(forward ? 1 : -1))
			{
				return 0;
			}
			return forward ? 1 : -1;
		}

		private void DragTurnMouseDown(MouseEventArgs e)
		{
			dragTurnState = DragTurnState.None;
			if (e.Button != MouseButtons.Left || !CanDragTurn())
			{
				return;
			}
			int zone = HitDragTurnZone(e.Location);
			if (zone != 0)
			{
				dragTurnState = DragTurnState.Pending;
				dragTurnStart = e.Location;
				dragTurnForward = zone > 0;
			}
		}

		private void DragTurnMouseMove(MouseEventArgs e)
		{
			if (dragTurnState == DragTurnState.Pending)
			{
				int dx = e.X - dragTurnStart.X;
				int dy = e.Y - dragTurnStart.Y;
				//Inward means away from the grabbed edge, towards the spine.
				bool grabbedRight = dragTurnForward != base.RightToLeftReading;
				int inward = grabbedRight ? -dx : dx;
				if (Math.Abs(dy) > DragTurnStartDistance && Math.Abs(dy) > Math.Abs(dx))
				{
					//A vertical drag is not a page turn.
					dragTurnState = DragTurnState.None;
				}
				else if (inward > DragTurnStartDistance)
				{
					BeginDragTurn(e.Location);
				}
			}
			else if (dragTurnState == DragTurnState.Active)
			{
				UpdateDragTurn(e.Location);
			}
		}

		private bool DragTurnMouseUp()
		{
			DragTurnState state = dragTurnState;
			dragTurnState = DragTurnState.None;
			if (state != DragTurnState.Active)
			{
				return false;
			}
			EndDragTurn();
			return true;
		}

		private void BeginDragTurn(Point location)
		{
			DisplayOutputConfig oldConfig = base.DisplayConfig;
			int oldPage = currentPage;
			bool moved = false;
			dragTurnSuppressPaint = true;
			suppressNavigationBlend = true;
			try
			{
				moved = NavigateForDragTurn(dragTurnForward) && currentPage != oldPage;
			}
			catch
			{
				moved = false;
			}
			finally
			{
				suppressNavigationBlend = false;
			}
			if (!moved)
			{
				dragTurnSuppressPaint = false;
				dragTurnState = DragTurnState.None;
				Invalidate();
				return;
			}
			try
			{
				int wait = 20;
				while ((!IsPageInCache(oldPage) || !IsPageInCache(oldPage, 1) || !IsPageInCache(currentPage) || !IsPageInCache(currentPage, 1)) && --wait > 0)
				{
					Thread.Sleep(25);
				}
				DisplayOutputConfig newConfig = base.DisplayConfig;
				newConfig.Rotation = oldConfig.Rotation;
				DisplayOutput newOut = DisplayOutput.Create(newConfig, base.CurrentAnamorphicTolerance);
				DisplayOutput oldOut = DisplayOutput.Create(oldConfig, base.CurrentAnamorphicTolerance);
				dragTurnOriginalPage = oldPage;
				dragOldOut = oldOut;
				dragNewOut = newOut;
				dragOldPage = oldPage;
				dragNewPage = currentPage;
				dragCornerMode = renderer is IGeometryClipRenderer;
				if (dragCornerMode)
				{
					//The grabbed edge is the one being peeled, whichever way the book is read.
					dragPeelRight = dragTurnForward != base.RightToLeftReading;
					GetPeelGeometry(oldOut, dragPeelRight, out bool _, out RectangleF sheet, out RectangleF _, out float _);
					dragCorner = new PointF(dragPeelRight ? sheet.Right : sheet.Left, ((float)dragTurnStart.Y).Clamp(sheet.Top, sheet.Bottom));
					dragGrabOffset = new PointF(dragCorner.X - dragTurnStart.X, dragCorner.Y - dragTurnStart.Y);
					dragMouse = dragCorner;
					dragProgress = 0f;
				}
				if (dragTurnForward)
				{
					dragSheetOut = oldOut;
					dragSheetPage = oldPage;
					dragUnderOut = newOut;
					dragUnderPage = currentPage;
				}
				else
				{
					dragSheetOut = newOut;
					dragSheetPage = currentPage;
					dragUnderOut = oldOut;
					dragUnderPage = oldPage;
				}
				//Start exactly where the page lies, whatever point inside the edge zone was grabbed.
				dragTurnOffset = 0f;
				dragTurnOffset = (dragTurnForward ? 0f : 1f) - PointToDragTurn(dragTurnStart);
				dragTurnT = dragTurnForward ? 0f : 1f;
				dragTurnVelocity = 0f;
				dragTurnLastTicks = Machine.Ticks;
				dragTurnState = DragTurnState.Active;
				base.MouseActionHappened = true;
			}
			finally
			{
				dragTurnSuppressPaint = false;
			}
			UpdateDragTurn(location);
		}

		private float PointToDragTurn(Point pt)
		{
			GetCurlGeometry(dragSheetOut, base.RightToLeftReading, out bool spread, out int sgn, out float spine, out float width);
			float rel = (pt.X - spine) * sgn / Math.Max(1f, width);
			float t = spread ? (float)(Math.Acos(rel.Clamp(-1f, 1f)) / Math.PI) : (1f - rel);
			return (t + dragTurnOffset).Clamp(0f, 1f);
		}

		private void UpdateDragTurn(Point pt)
		{
			if (dragCornerMode)
			{
				SetPeelMouse(new PointF(pt.X + dragGrabOffset.X, pt.Y + dragGrabOffset.Y), trackVelocity: true);
				RenderDragTurnFrame();
				return;
			}
			float t = PointToDragTurn(pt);
			long now = Machine.Ticks;
			long elapsed = now - dragTurnLastTicks;
			if (elapsed > 0)
			{
				float velocity = (t - dragTurnT) * 1000f / elapsed;
				//Smooth it so one jittery mouse event does not decide the outcome.
				dragTurnVelocity = dragTurnVelocity * 0.6f + velocity * 0.4f;
			}
			dragTurnLastTicks = now;
			dragTurnT = t;
			RenderDragTurnFrame();
		}

		private void SetPeelMouse(PointF mouse, bool trackVelocity)
		{
			GetPeelGeometry(dragOldOut, dragPeelRight, out bool _, out RectangleF sheet, out RectangleF _, out float spine);
			dragMouse = ConstrainPeel(mouse, dragCorner, sheet, spine);
			//Progress: 0 with the page lying flat, 1 once the grabbed point has reached its mirror
			//image on the other side of the spine.
			float inward = Math.Sign(spine - dragCorner.X);
			float travel = Math.Max(1f, 2f * Math.Abs(spine - dragCorner.X));
			float progress = ((dragMouse.X - dragCorner.X) * inward / travel).Clamp(0f, 1f);
			if (trackVelocity)
			{
				long now = Machine.Ticks;
				long elapsed = now - dragTurnLastTicks;
				if (elapsed > 0)
				{
					float velocity = (progress - dragProgress) * 1000f / elapsed;
					dragTurnVelocity = dragTurnVelocity * 0.6f + velocity * 0.4f;
				}
				dragTurnLastTicks = now;
			}
			dragProgress = progress;
		}

		private bool EndCornerDragTurn()
		{
			bool complete = dragTurnVelocity > 1f || (dragTurnVelocity > -1f && dragProgress > 0.35f);
			GetPeelGeometry(dragOldOut, dragPeelRight, out bool _, out RectangleF _, out RectangleF _, out float spine);
			PointF from = dragMouse;
			PointF to = complete ? new PointF(2f * spine - dragCorner.X, dragCorner.Y) : dragCorner;
			int duration = Math.Max(80, (int)(EngineConfiguration.Default.PageCurlDuration * Math.Abs(complete ? (1f - dragProgress) : dragProgress)));
			try
			{
				dragTurnState = DragTurnState.Active;
				ThreadUtility.Animate(duration, delegate(float p)
				{
					float eased = 1f - (1f - p) * (1f - p);
					SetPeelMouse(new PointF(from.X + (to.X - from.X) * eased, from.Y + (to.Y - from.Y) * eased), trackVelocity: false);
					RenderDragTurnFrame();
				});
			}
			catch
			{
			}
			finally
			{
				dragTurnState = DragTurnState.None;
			}
			return complete;
		}

		private void EndDragTurn()
		{
			if (dragCornerMode)
			{
				FinishDragTurn(EndCornerDragTurn());
				return;
			}
			bool complete;
			if (dragTurnForward)
			{
				complete = dragTurnVelocity > 1f || (dragTurnVelocity > -1f && dragTurnT > 0.35f);
			}
			else
			{
				complete = dragTurnVelocity < -1f || (dragTurnVelocity < 1f && dragTurnT < 0.65f);
			}
			float target = (dragTurnForward == complete) ? 1f : 0f;
			float from = dragTurnT;
			int duration = Math.Max(60, (int)(EngineConfiguration.Default.PageCurlDuration * Math.Abs(target - from)));
			try
			{
				dragTurnState = DragTurnState.Active;
				ThreadUtility.Animate(duration, delegate(float p)
				{
					float eased = 1f - (1f - p) * (1f - p);
					dragTurnT = from + (target - from) * eased;
					RenderDragTurnFrame();
				});
			}
			catch
			{
			}
			finally
			{
				dragTurnState = DragTurnState.None;
			}
			FinishDragTurn(complete);
		}

		private void FinishDragTurn(bool complete)
		{
			if (!complete)
			{
				dragTurnSuppressPaint = true;
				suppressNavigationBlend = true;
				try
				{
					Book.Navigate(dragTurnOriginalPage, PageSeekOrigin.Absolute);
				}
				catch
				{
				}
				finally
				{
					suppressNavigationBlend = false;
					dragTurnSuppressPaint = false;
				}
			}
			//Sheet/under and old/new are the same two outputs in a different order.
			dragOldOut?.Dispose();
			dragNewOut?.Dispose();
			dragOldOut = null;
			dragNewOut = null;
			dragSheetOut = null;
			dragUnderOut = null;
			dragCornerMode = false;
			Invalidate();
		}

		private bool NavigateForDragTurn(bool forward)
		{
			//Same page steps as ComicDisplay.DisplayNextPage / DisplayPreviousPage, which live in
			//the engine and can not be called from here.
			int offset;
			if (forward)
			{
				offset = ((TwoPageDisplay && IsDoubleImage) ? 2 : 1);
				if (offset == 2)
				{
					int next = Book.SeekNewPage(1, PageSeekOrigin.Current);
					if (next >= 0 && Book.Comic.GetPage(next).PagePosition == ComicPagePosition.Near)
					{
						offset = 1;
					}
				}
			}
			else
			{
				int previous = Book.SeekNewPage(-1, PageSeekOrigin.Current);
				int previous2 = Book.SeekNewPage(-2, PageSeekOrigin.Current);
				if (!TwoPageDisplay || previous == -1 || previous2 == -1)
				{
					offset = -1;
				}
				else
				{
					ComicPageInfo a = Book.Comic.GetPage(previous);
					ComicPageInfo b = Book.Comic.GetPage(previous2);
					offset = ((a.IsSinglePageType || a.IsDoublePage || b.IsSinglePageType || b.IsDoublePage || (a.PagePosition == ComicPagePosition.Near && b.PagePosition != ComicPagePosition.Far)) ? (-1) : (-2));
				}
			}
			return Book.Navigate(offset);
		}

		private bool RenderDragTurnFrame()
		{
			IBitmapRenderer bitmapRenderer = renderer;
			if (bitmapRenderer == null || !bitmapRenderer.IsHardware || dragSheetOut == null || dragUnderOut == null)
			{
				return false;
			}
			try
			{
				if (bitmapRenderer.BeginScene(null))
				{
					using (bitmapRenderer.SaveState())
					{
						if (dragCornerMode && bitmapRenderer is IGeometryClipRenderer clipper)
						{
							RenderCornerPeel(bitmapRenderer, clipper, dragOldOut, dragOldPage, dragNewOut, dragNewPage, dragPeelRight, dragCorner, dragMouse);
						}
						else
						{
							RenderPageCurl(bitmapRenderer, dragSheetOut, dragSheetPage, dragUnderOut, dragUnderPage, dragTurnT, base.RightToLeftReading, ease: false);
						}
					}
					RenderImageOverlay(bitmapRenderer, dragNewOut ?? dragUnderOut);
				}
			}
			catch (Exception e)
			{
				HandleRendererError(e);
			}
			finally
			{
				try
				{
					bitmapRenderer.EndScene();
				}
				catch
				{
				}
			}
			return true;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			if (dragTurnSuppressPaint)
			{
				//The page is being switched behind the scenes; the curl frame follows right after.
				return;
			}
			if (dragTurnState == DragTurnState.Active && RenderDragTurnFrame())
			{
				return;
			}
			base.OnPaint(e);
		}

		#endregion

		#region Corner fold (drag in any direction)

		//When the renderer can clip to polygons (Direct2D), a dragged page folds like paper: the
		//grabbed point follows the mouse, and the fold line is where the paper would crease, halfway
		//between the grabbed point's resting place and the mouse, at right angles to the line
		//joining them. The page splits along that line into the part still lying flat and a flap
		//folded back over it, with the page underneath showing through where the flap lifted off.
		//
		//The paper can not stretch, so the grabbed point can never be further from the two spine
		//corners than it started. That keeps the page attached to the book.

		private bool dragCornerMode;

		private bool dragPeelRight;

		private PointF dragCorner;

		private PointF dragGrabOffset;

		private PointF dragMouse;

		private float dragProgress;

		private DisplayOutput dragOldOut;

		private DisplayOutput dragNewOut;

		private int dragOldPage;

		private int dragNewPage;

		private void GetPeelGeometry(DisplayOutput output, bool peelRight, out bool spread, out RectangleF sheet, out RectangleF visible, out float spine)
		{
			Rectangle page = output.OutputBoundsScreen;
			spread = TwoPageDisplay && page.Width >= page.Height * 0.9f;
			visible = page;
			if (spread)
			{
				spine = page.Left + page.Width / 2f;
				sheet = peelRight ? RectangleF.FromLTRB(spine, page.Top, page.Right, page.Bottom) : RectangleF.FromLTRB(page.Left, page.Top, spine, page.Bottom);
			}
			else
			{
				//A single page pivots on its far edge and never leaves its own rectangle.
				spine = peelRight ? page.Left : page.Right;
				sheet = page;
			}
		}

		private static PointF ConstrainPeel(PointF mouse, PointF dragCorner, RectangleF sheet, float spine)
		{
			PointF top = new PointF(spine, sheet.Top);
			PointF bottom = new PointF(spine, sheet.Bottom);
			float rTop = Distance(dragCorner, top);
			float rBottom = Distance(dragCorner, bottom);
			for (int i = 0; i < 3; i++)
			{
				mouse = KeepWithin(mouse, top, rTop);
				mouse = KeepWithin(mouse, bottom, rBottom);
			}
			return mouse;
		}

		private static PointF KeepWithin(PointF p, PointF center, float radius)
		{
			float d = Distance(p, center);
			if (d <= radius || d < 0.001f)
			{
				return p;
			}
			float f = radius / d;
			return new PointF(center.X + (p.X - center.X) * f, center.Y + (p.Y - center.Y) * f);
		}

		private static float Distance(PointF a, PointF b)
		{
			float dx = a.X - b.X;
			float dy = a.Y - b.Y;
			return (float)Math.Sqrt(dx * dx + dy * dy);
		}

		private static PointF[] RectPolygon(RectangleF r)
		{
			return new PointF[4]
			{
				new PointF(r.Left, r.Top),
				new PointF(r.Right, r.Top),
				new PointF(r.Right, r.Bottom),
				new PointF(r.Left, r.Bottom)
			};
		}

		/// <summary>
		/// Sutherland-Hodgman: keeps the part of a convex polygon where (p - origin) . normal is
		/// negative (keepNegative) or positive.
		/// </summary>
		private static PointF[] ClipHalfPlane(PointF[] polygon, PointF origin, PointF normal, bool keepNegative)
		{
			List<PointF> result = new List<PointF>();
			int n = polygon.Length;
			for (int i = 0; i < n; i++)
			{
				PointF a = polygon[i];
				PointF b = polygon[(i + 1) % n];
				float da = (a.X - origin.X) * normal.X + (a.Y - origin.Y) * normal.Y;
				float db = (b.X - origin.X) * normal.X + (b.Y - origin.Y) * normal.Y;
				if (!keepNegative)
				{
					da = -da;
					db = -db;
				}
				bool ina = da <= 0f;
				bool inb = db <= 0f;
				if (ina)
				{
					result.Add(a);
				}
				if (ina != inb)
				{
					float t = da / (da - db);
					result.Add(new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
				}
			}
			return result.ToArray();
		}

		private static PointF[] ClipToRect(PointF[] polygon, RectangleF r)
		{
			polygon = ClipHalfPlane(polygon, new PointF(r.Left, 0f), new PointF(-1f, 0f), keepNegative: true);
			polygon = ClipHalfPlane(polygon, new PointF(r.Right, 0f), new PointF(1f, 0f), keepNegative: true);
			polygon = ClipHalfPlane(polygon, new PointF(0f, r.Top), new PointF(0f, -1f), keepNegative: true);
			return ClipHalfPlane(polygon, new PointF(0f, r.Bottom), new PointF(0f, 1f), keepNegative: true);
		}

		private static PointF Reflect(PointF p, PointF origin, PointF normal)
		{
			float d = (p.X - origin.X) * normal.X + (p.Y - origin.Y) * normal.Y;
			return new PointF(p.X - 2f * d * normal.X, p.Y - 2f * d * normal.Y);
		}

		private static PointF[] OffsetPolygon(PointF[] polygon, float dx, float dy)
		{
			return polygon.Select((PointF p) => new PointF(p.X + dx, p.Y + dy)).ToArray();
		}

		/// <summary>
		/// Where paper at distance u beyond the fold ends up, measured along the fold normal.
		/// Up to "roll" the paper curves over a half cylinder; past that it lies flat again.
		/// A sharp fold would simply be -u.
		/// </summary>
		private static float RollOffset(float u, float roll)
		{
			if (roll < 0.01f)
			{
				return 0f - u;
			}
			if (u <= roll)
			{
				return roll / (float)Math.PI * (float)Math.Sin(Math.PI * u / roll);
			}
			return 0f - (u - roll);
		}

		/// <summary>
		/// Moves a point of the lifted paper onto the roll, exactly (used for the outline).
		/// </summary>
		private static PointF MapRoll(PointF p, PointF origin, PointF normal, float unused, float roll)
		{
			float u = (p.X - origin.X) * normal.X + (p.Y - origin.Y) * normal.Y;
			float x = RollOffset(u, roll);
			return new PointF(p.X + (x - u) * normal.X, p.Y + (x - u) * normal.Y);
		}

		/// <summary>
		/// Same, but with one slice's straight-line approximation of the curve.
		/// </summary>
		private static PointF MapRollSlice(PointF p, PointF origin, PointF normal, float slope, float shift)
		{
			float u = (p.X - origin.X) * normal.X + (p.Y - origin.Y) * normal.Y;
			float x = slope * u + shift;
			return new PointF(p.X + (x - u) * normal.X, p.Y + (x - u) * normal.Y);
		}

		/// <summary>
		/// One slice's mapping as a System.Drawing matrix: along the fold normal the paper is
		/// scaled by slope and shifted, across it nothing changes.
		/// </summary>
		private static System.Drawing.Drawing2D.Matrix RollMatrix(PointF origin, PointF normal, float slope, float shift)
		{
			float nx = normal.X;
			float ny = normal.Y;
			float k = slope - 1f;
			float a00 = 1f + k * nx * nx;
			float a01 = k * nx * ny;
			float a11 = 1f + k * ny * ny;
			//Translation: keep the fold line where it is, then apply the slice's own shift.
			float ox = origin.X - (a00 * origin.X + a01 * origin.Y) + shift * nx;
			float oy = origin.Y - (a01 * origin.X + a11 * origin.Y) + shift * ny;
			return new System.Drawing.Drawing2D.Matrix(a00, a01, a01, a11, ox, oy);
		}

		/// <summary>
		/// Reflection across the fold line, as a System.Drawing matrix.
		/// </summary>
		private static System.Drawing.Drawing2D.Matrix ReflectionMatrix(PointF origin, PointF normal)
		{
			float nx = normal.X;
			float ny = normal.Y;
			float a00 = 1f - 2f * nx * nx;
			float a01 = -2f * nx * ny;
			float a11 = 1f - 2f * ny * ny;
			float d = 2f * (origin.X * nx + origin.Y * ny);
			//System.Drawing maps (x, y) to (x*m11 + y*m21 + dx, x*m12 + y*m22 + dy).
			return new System.Drawing.Drawing2D.Matrix(a00, a01, a01, a11, d * nx, d * ny);
		}

		/// <summary>
		/// Draws one frame of a page being peeled off: used both while dragging with the mouse and
		/// by the Realistic Page Curl transition, which simply moves the grabbed point itself.
		/// </summary>
		private void RenderCornerPeel(IBitmapRenderer hr, IGeometryClipRenderer clipper, DisplayOutput oldOut, int oldPageIndex, DisplayOutput newOut, int newPageIndex, bool peelRight, PointF corner, PointF mouse)
		{
			GetPeelGeometry(oldOut, peelRight, out bool spread, out RectangleF sheet, out RectangleF visible, out float spine);
			Rectangle client = base.ClientRectangle;
			System.Drawing.Drawing2D.Matrix baseTransform = hr.Transform;
			float opacity = hr.Opacity;
			//While the page is moving, sample it the cheap way. High quality sampling of a very
			//large scan, a dozen times per frame, is what makes big books stutter, and the
			//difference is invisible on a page in motion.
			IHardwareRenderer hardware = hr as IHardwareRenderer;
			bool wasOptimized = hardware != null && hardware.OptimizedTextures;
			if (hardware != null)
			{
				hardware.OptimizedTextures = true;
			}
			try
			{
				//1. What is underneath: the new page on the side being peeled, and in a spread the
				//   old facing page on the other side, where the flap will come to rest.
				RenderImageBackground(hr, newOut, newPageIndex);
				if (spread)
				{
					RectangleF peelSide = peelRight ? RectangleF.FromLTRB(spine, client.Top, client.Right, client.Bottom) : RectangleF.FromLTRB(client.Left, client.Top, spine, client.Bottom);
					RectangleF otherSide = peelRight ? RectangleF.FromLTRB(client.Left, client.Top, spine, client.Bottom) : RectangleF.FromLTRB(spine, client.Top, client.Right, client.Bottom);
					SetCurlClip(hr, baseTransform, peelSide);
					RenderImageSafe(hr, newOut, newPageIndex, RenderType.WithoutBackground);
					SetCurlClip(hr, baseTransform, otherSide);
					RenderImageSafe(hr, oldOut, oldPageIndex, RenderType.WithoutBackground);
				}
				else
				{
					SetCurlClip(hr, baseTransform, RectangleF.Empty);
					RenderImageSafe(hr, newOut, newPageIndex, RenderType.WithoutBackground);
				}
				SetCurlClip(hr, baseTransform, RectangleF.Empty);
				float lift = Distance(corner, mouse);
				PointF[] sheetPolygon = RectPolygon(sheet);
				if (lift < 0.5f)
				{
					clipper.PushPolygonClip(sheetPolygon);
					RenderImageSafe(hr, oldOut, oldPageIndex, RenderType.WithoutBackground);
					clipper.PopPolygonClip();
					return;
				}
				float width = sheet.Width;
				//The paper does not crease: it rolls. The roll takes up some of the sheet, so the
				//fold sits a little further from the corner than the halfway point, which is what
				//keeps the corner exactly under the mouse.
				float roll = Math.Min(width * 0.12f, lift * 0.8f);
				float creaseDistance = (lift + roll) / 2f;
				PointF normal = new PointF((corner.X - mouse.X) / lift, (corner.Y - mouse.Y) / lift);
				PointF mid = new PointF(corner.X - normal.X * creaseDistance, corner.Y - normal.Y * creaseDistance);
				PointF[] flat = ClipHalfPlane(sheetPolygon, mid, normal, keepNegative: true);
				PointF[] lifted = ClipHalfPlane(sheetPolygon, mid, normal, keepNegative: false);
				//How far the lifted part reaches, measured from the fold.
				float reach = 0f;
				foreach (PointF p in lifted)
				{
					reach = Math.Max(reach, (p.X - mid.X) * normal.X + (p.Y - mid.Y) * normal.Y);
				}
				PointF[] flap = ClipToRect(lifted.Select((PointF p) => MapRoll(p, mid, normal, -1f, roll)).ToArray(), visible);
				float shadowLength = Math.Min(width * 0.25f, lift * 0.5f) + 4f;
				float strength = Math.Min(1f, lift / (width * 0.3f));

				//2. The part of the old page still lying flat.
				if (flat.Length >= 3)
				{
					clipper.PushPolygonClip(flat);
					RenderImageSafe(hr, oldOut, oldPageIndex, RenderType.WithoutBackground);
					clipper.PopPolygonClip();
				}
				//3. Shadow the lifted flap throws on the uncovered page, darkest at the crease.
				if (lifted.Length >= 3)
				{
					clipper.FillPolygonGradient(lifted, mid, Color.FromArgb((int)(115 * strength), Color.Black), new PointF(mid.X + normal.X * shadowLength, mid.Y + normal.Y * shadowLength), Color.FromArgb(0, Color.Black));
				}
				if (flap.Length >= 3)
				{
					//4. Soft drop shadow of the rolled part onto the page below it.
					PointF[] drop = ClipToRect(OffsetPolygon(flap, -normal.X * 5f, -normal.Y * 5f + 2f), visible);
					if (drop.Length >= 3)
					{
						clipper.FillPolygon(drop, Color.FromArgb((int)(60 * strength), Color.Black));
					}
				}
				//5. The lifted part, drawn as slices across the roll. Each slice gets its own
				//   position along the curve and its own shading, so the paper bends instead of
				//   creasing. Slices are drawn from the fold outwards, which is also furthest
				//   from the viewer first.
				//Each slice is a separate pass over the page, so big scans get fewer of them.
				int sourceWidth = Math.Max(oldOut.OutputBounds.Width, newOut.OutputBounds.Width);
				int curved = ((sourceWidth > 4000) ? 5 : ((sourceWidth > 2000) ? 7 : 10));
				float rollEnd = Math.Min(roll, reach);
				for (int k = 0; k <= curved; k++)
				{
					float u0;
					float u1;
					if (k < curved)
					{
						u0 = rollEnd * k / curved;
						u1 = rollEnd * (k + 1) / curved;
					}
					else
					{
						//Past the roll the paper is flat again, lying back over the page.
						u0 = rollEnd;
						u1 = reach;
					}
					if (u1 - u0 < 0.01f)
					{
						continue;
					}
					PointF[] band = ClipHalfPlane(lifted, new PointF(mid.X + normal.X * u0, mid.Y + normal.Y * u0), normal, keepNegative: false);
					band = ClipHalfPlane(band, new PointF(mid.X + normal.X * u1, mid.Y + normal.Y * u1), normal, keepNegative: true);
					if (band.Length < 3)
					{
						continue;
					}
					float x0 = RollOffset(u0, roll);
					float x1 = RollOffset(u1, roll);
					float slope = (x1 - x0) / (u1 - u0);
					float shift = x0 - slope * u0;
					PointF[] slice = ClipToRect(band.Select((PointF p) => MapRollSlice(p, mid, normal, slope, shift)).ToArray(), visible);
					if (slice.Length < 3)
					{
						continue;
					}
					clipper.PushPolygonClip(slice);
					using (System.Drawing.Drawing2D.Matrix fold = RollMatrix(mid, normal, slope, shift))
					{
						System.Drawing.Drawing2D.Matrix m = baseTransform.Clone();
						if (spread)
						{
							//The back of this sheet is the facing page of the new spread, found on
							//the other side of the spine: mirror across the spine, then bend.
							using (System.Drawing.Drawing2D.Matrix mirror = new System.Drawing.Drawing2D.Matrix(-1f, 0f, 0f, 1f, 2f * spine, 0f))
							{
								System.Drawing.Drawing2D.Matrix combined = fold.Clone();
								combined.Multiply(mirror);
								m.Multiply(combined);
								combined.Dispose();
							}
							hr.Transform = m;
							hr.Opacity = 1f;
							RenderImageSafe(hr, newOut, newPageIndex, RenderType.WithoutBackground);
						}
						else
						{
							//A single page has plain paper on its back, with the print faintly
							//showing through.
							clipper.FillPolygon(slice, PageCurlPaperColor);
							m.Multiply(fold);
							hr.Transform = m;
							hr.Opacity = 0.12f;
							RenderImageSafe(hr, oldOut, oldPageIndex, RenderType.WithoutBackground);
						}
						hr.Transform = baseTransform;
						hr.Opacity = opacity;
						m.Dispose();
					}
					//Light: paper facing the viewer is bright, paper turned edge on is dark.
					float angle = (float)Math.PI * Math.Min(1f, (u0 + u1) / 2f / Math.Max(0.01f, roll));
					float shade = (1f - Math.Abs((float)Math.Cos(angle))) * 0.55f * strength;
					if (shade > 0.01f)
					{
						clipper.FillPolygon(slice, Color.FromArgb((int)(255f * shade), Color.Black));
					}
					clipper.PopPolygonClip();
				}
			}
			finally
			{
				hr.Transform = baseTransform;
				hr.Clip = RectangleF.Empty;
				hr.Opacity = opacity;
				if (hardware != null)
				{
					hardware.OptimizedTextures = wasOptimized;
				}
			}
		}

		#endregion

		public void ScrollToLeftBlending(IBitmapRenderer hr, int oldPage, DisplayOutput oldOut, DisplayOutput display, float percent)
		{
			RenderImageBackground(hr, null);
			hr.TranslateTransform((float)(-base.ClientRectangle.Width) * percent, 0f);
			RenderImageSafe(hr, oldOut, oldPage, !base.IsConstantBackground);
			hr.TranslateTransform(base.ClientRectangle.Width, 0f);
			RenderImageSafe(renderer, display, !base.IsConstantBackground);
		}

		public void ScrollToRightBlending(IBitmapRenderer hr, int oldPage, DisplayOutput oldOut, DisplayOutput display, float percent)
		{
			RenderImageBackground(hr, null);
			hr.TranslateTransform((float)base.ClientRectangle.Width * percent, 0f);
			RenderImageSafe(hr, oldOut, oldPage, !base.IsConstantBackground);
			hr.TranslateTransform(-base.ClientRectangle.Width, 0f);
			RenderImageSafe(renderer, display, !base.IsConstantBackground);
		}

		public void ScrollToBottomBlending(IBitmapRenderer hr, int oldPage, DisplayOutput oldOut, DisplayOutput display, float percent)
		{
			RenderImageBackground(hr, null);
			hr.TranslateTransform(0f, (float)base.ClientRectangle.Height * percent);
			RenderImageSafe(hr, oldOut, oldPage, !base.IsConstantBackground);
			hr.TranslateTransform(0f, -base.ClientRectangle.Height);
			RenderImageSafe(renderer, display, !base.IsConstantBackground);
		}

		public void ScrollToTopBlending(IBitmapRenderer hr, int oldPage, DisplayOutput oldOut, DisplayOutput display, float percent)
		{
			RenderImageBackground(hr, null);
			hr.TranslateTransform(0f, (float)(-base.ClientRectangle.Height) * percent);
			RenderImageSafe(hr, oldOut, oldPage, !base.IsConstantBackground);
			hr.TranslateTransform(0f, base.ClientRectangle.Height);
			RenderImageSafe(renderer, display, !base.IsConstantBackground);
		}

		private void RenderImageSafe(IBitmapRenderer bitmapRenderer, DisplayOutput output, int page, RenderType renderType = RenderType.Default)
		{
			if (output != null)
			{
				int num = currentPage;
				currentPage = page;
				try
				{
					RenderImageSafe(bitmapRenderer, output, renderType);
				}
				finally
				{
					currentPage = num;
				}
			}
		}

		private void RenderImageSafe(IBitmapRenderer bitmapRenderer, DisplayOutput output, int page, bool withBackground)
		{
			RenderImageSafe(bitmapRenderer, output, page, withBackground ? RenderType.Default : RenderType.WithoutBackground);
		}

		private void RenderImageSafe(IBitmapRenderer bitmapRenderer, DisplayOutput output, bool withBackground)
		{
			RenderImageSafe(bitmapRenderer, output, withBackground ? RenderType.Default : RenderType.WithoutBackground);
		}

		private void InitializeComponent()
		{
			components = new System.ComponentModel.Container();
			imageScaleTimer = new System.Windows.Forms.Timer(components);
			longClickTimer = new System.Windows.Forms.Timer(components);
			cacheUpdateTimer = new System.Windows.Forms.Timer(components);
			SuspendLayout();
			imageScaleTimer.Interval = 500;
			imageScaleTimer.Tick += new System.EventHandler(imageScaleTimer_Tick);
			longClickTimer.Interval = 500;
			longClickTimer.Tick += new System.EventHandler(longClickTimer_Tick);
			cacheUpdateTimer.Interval = 2000;
			cacheUpdateTimer.Tick += new System.EventHandler(cacheUpdateTimer_Tick);
			ResumeLayout(false);
		}

		static ComicDisplayControl()
		{
			Magnifier[] array = new Magnifier[2];
			Magnifier magnifier = new Magnifier
			{
				Bitmap = Resources.Magnifier,
				Inner = new Rectangle(20, 20, 573, 327),
				Outer = new Rectangle(5, 5, 592, 350)
			};
			array[0] = magnifier;
			magnifier = new Magnifier
			{
				Bitmap = Resources.MagnifierLight,
				Inner = new Rectangle(6, 5, 102, 56),
				Outer = new Rectangle(3, 2, 108, 61)
			};
			array[1] = magnifier;
			magnifiers = array;
		}
	}
}
