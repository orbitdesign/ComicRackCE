using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;
using cYo.Common.ComponentModel;
using cYo.Common.Drawing;
using cYo.Common.Presentation.Tao;
using SharpDX.Mathematics.Interop;
using D2D = SharpDX.Direct2D1;
using DXGI = SharpDX.DXGI;

namespace cYo.Common.Presentation.Direct2D
{
	/// <summary>
	/// Thrown when the Direct2D renderer has given up, so ImageDisplayControl falls back to GDI+.
	/// </summary>
	public class Direct2DRendererException : Exception
	{
		public Direct2DRendererException(string message, Exception inner = null)
			: base(message, inner)
		{
		}
	}

	/// <summary>
	/// Hardware renderer for the page display, built on Direct2D.
	///
	/// It is a drop in replacement for <see cref="ControlOpenGlRenderer"/> and implements the same
	/// interfaces, so ImageDisplayControl and ComicDisplayControl need no changes beyond picking it.
	///
	/// Differences from the OpenGL renderer worth knowing about:
	/// - Device loss (driver update, TDR, remote desktop, GPU switch) is detected at EndDraw and all
	///   GPU resources are recreated on the next frame instead of drawing garbage or crashing.
	/// - A window handle that gets recreated is followed, instead of rendering into a dead handle.
	/// - "Hardware filters" means high quality cubic sampling, which is done by Direct2D itself.
	///   There are no hand built mip maps any more, which is where the OpenGL path crashed.
	/// - Large images are split into tiles only when they exceed the device limit, so a typical
	///   page is a single GPU bitmap instead of dozens of 512x512 textures.
	/// </summary>
	public class ControlDirect2DRenderer : DisposableObject, IControlRenderer, IHardwareRenderer, IGeometryClipRenderer
	{
		private struct PendingMultiply
		{
			public RendererImage Image;

			public RectangleF Dest;

			public RectangleF Src;

			public BitmapAdjustment Adjustment;

			public float Opacity;

			public float[] Transform;
		}

		private const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);

		//D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC. Written as a number so the code does not
		//depend on the enum member name, and so it can be switched off if the OS rejects it.
		private const D2D.InterpolationMode HighQualityCubic = (D2D.InterpolationMode)5;

		private const int MaxConsecutiveFailures = 3;

		private readonly Direct2DBitmapCache cache;

		private D2D.Factory factory;

		private D2D.WindowRenderTarget target;

		private D2D.DeviceContext context;

		private D2D.SolidColorBrush brush;

		private D2D.Effects.Blend multiplyEffect;

		private D2D.Bitmap multiplyScratch;

		private D2D.BitmapRenderTarget multiplyLayer;

		private Size multiplySize;

		private Size targetSize;

		private IntPtr targetHandle;

		private int maxTileSize = 4096;

		private int sceneCounter;

		private bool drawing;

		private bool releaseRequested;

		private Matrix transform = new Matrix();

		private readonly Stack<Matrix> transformStack = new Stack<Matrix>();

		private RectangleF clip = RectangleF.Empty;

		private RectangleF clipDevice = RectangleF.Empty;

		private bool clipPushed;

		private float opacity = 1f;

		private BlendingOperation blendingOperation = BlendingOperation.Blend;

		private readonly List<PendingMultiply> pendingMultiply = new List<PendingMultiply>();

		private RectangleF pendingMultiplyBounds = RectangleF.Empty;

		private bool multiplyFailed;

		private bool highQualityCubicFailed;

		private bool usedHighQualityCubic;

		private int consecutiveFailures;

		private readonly List<IDisposable> frameTemporaries = new List<IDisposable>();

		private readonly List<D2D.Layer> layerPool = new List<D2D.Layer>();

		//Geometry of each layer currently pushed, so they can be popped and pushed again around
		//operations that Direct2D refuses to do inside a layer.
		private readonly List<D2D.PathGeometry> activeLayerGeometries = new List<D2D.PathGeometry>();

		//Bounding box of each active layer, so work can be limited to the part of the frame the
		//layers actually let through.
		private readonly List<RectangleF> activeLayerBounds = new List<RectangleF>();

		//One entry per PushPolygonClip: true when a Direct2D layer was really pushed, false for a
		//no-op push, so every PopPolygonClip undoes exactly its own push.
		private readonly Stack<bool> clipLayers = new Stack<bool>();

		private int layerDepth;

		public Control Control
		{
			get;
			private set;
		}

		public TextureManagerSettings Settings
		{
			get;
			private set;
		}

		public Size Size => Control?.ClientRectangle.Size ?? Size.Empty;

		public bool HighQuality
		{
			get;
			set;
		}

		public Matrix Transform
		{
			get
			{
				return transform.Clone();
			}
			set
			{
				Matrix old = transform;
				transform = (value != null) ? value.Clone() : new Matrix();
				old.Dispose();
				ApplyTransform();
			}
		}

		public float Opacity
		{
			get
			{
				return opacity;
			}
			set
			{
				opacity = value;
			}
		}

		public CompositingMode CompositingMode
		{
			get;
			set;
		}

		public RectangleF Clip
		{
			get
			{
				return clip;
			}
			set
			{
				FlushMultiply();
				clip = value;
				if (!drawing)
				{
					return;
				}
				PopClip();
				if (!value.IsEmpty)
				{
					clipDevice = TransformBounds(value);
					PushClip();
				}
			}
		}

		public bool IsHardware => true;

		public bool IsLocked => sceneCounter > 0;

		public bool IsSoftwareRenderer => false;

		public StencilMode StencilMode
		{
			//Nothing outside the OpenGL renderer uses the stencil, so this only remembers the value.
			get;
			set;
		}

		public bool OptimizedTextures
		{
			get;
			set;
		}

		public bool EnableFilter
		{
			get;
			set;
		}

		public BlendingOperation BlendingOperation
		{
			get
			{
				return blendingOperation;
			}
			set
			{
				if (value != blendingOperation)
				{
					if (blendingOperation == BlendingOperation.Multiply)
					{
						FlushMultiply();
					}
					blendingOperation = value;
				}
			}
		}

		public event EventHandler Paint;

		public ControlDirect2DRenderer(Control window, bool registerPaint, TextureManagerSettings settings)
		{
			if (window == null)
			{
				throw new ArgumentNullException(nameof(window));
			}
			Settings = settings ?? new TextureManagerSettings();
			cache = new Direct2DBitmapCache((long)Settings.MaxTextureMemoryMB * 1024 * 1024, Settings.MaxTextureCount);
			try
			{
				factory = new D2D.Factory1(D2D.FactoryType.SingleThreaded);
			}
			catch
			{
				//Direct2D 1.1 is missing (Windows 7 without the platform update). Plain Direct2D
				//still works, just without high quality cubic sampling and paper textures.
				factory = new D2D.Factory(D2D.FactoryType.SingleThreaded);
			}
			Control = window;
			if (Control.Handle == IntPtr.Zero)
			{
				throw new InvalidOperationException("Window not created");
			}
			SetStyle(window, ControlStyles.ResizeRedraw, enable: true);
			//Direct2D presents straight to the window. A WinForms back buffer would be blitted on
			//top of it afterwards and wipe the frame out.
			SetStyle(window, ControlStyles.OptimizedDoubleBuffer, enable: false);
			SetStyle(window, ControlStyles.AllPaintingInWmPaint, enable: true);
			SetStyle(window, ControlStyles.UserPaint, enable: true);
			//Create the device right away. If Direct2D is not usable on this machine this throws,
			//and ImageDisplayControl falls back to the next renderer.
			try
			{
				CreateDeviceResources(Control.Handle, Control.ClientSize);
			}
			catch
			{
				SafeDispose(factory);
				factory = null;
				throw;
			}
			if (registerPaint)
			{
				window.Paint += window_Paint;
			}
			window.Disposed += window_Disposed;
			window.HandleCreated += window_HandleChanged;
			window.HandleDestroyed += window_HandleChanged;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (Control != null)
				{
					Control.Paint -= window_Paint;
					Control.Disposed -= window_Disposed;
					Control.HandleCreated -= window_HandleChanged;
					Control.HandleDestroyed -= window_HandleChanged;
					Control = null;
				}
				ReleaseDeviceResources();
				SafeDispose(factory);
				factory = null;
				transform.Dispose();
			}
			base.Dispose(disposing);
		}

		public void Draw()
		{
			Control?.Invalidate();
		}

		public bool BeginScene(Graphics gr)
		{
			if (sceneCounter == 0)
			{
				if (consecutiveFailures >= MaxConsecutiveFailures)
				{
					//Thrown inside ImageDisplayControl.RenderScene, whose error handler swaps in GDI+.
					throw new Direct2DRendererException("Direct2D rendering keeps failing, giving up on hardware acceleration.");
				}
				if (!EnsureTarget())
				{
					return false;
				}
				cache.Sweep();
				transformStack.Clear();
				transform.Reset();
				clip = RectangleF.Empty;
				clipPushed = false;
				usedHighQualityCubic = false;
				target.BeginDraw();
				drawing = true;
				target.Transform = Identity;
				SetPrimitiveBlend(copy: false);
				Clear(Control.BackColor);
			}
			sceneCounter++;
			return true;
		}

		public void EndScene()
		{
			if (sceneCounter == 0)
			{
				return;
			}
			try
			{
				if (sceneCounter == 1 && drawing)
				{
					FinishFrame();
				}
			}
			finally
			{
				sceneCounter--;
			}
		}

		public void Clear(Color color)
		{
			if (!drawing)
			{
				return;
			}
			FlushMultiply();
			target.Clear(ToColor4(color));
		}

		public void DrawImage(RendererImage image, RectangleF dest, RectangleF src, BitmapAdjustment ajustment, float opacity)
		{
			opacity *= Opacity;
			if (!drawing || image == null || opacity < 0.05f || !IsDrawable(dest) || !IsDrawable(src))
			{
				return;
			}
			if (BlendingOperation == BlendingOperation.Multiply)
			{
				QueueMultiply(image, dest, src, ajustment, opacity);
				return;
			}
			FlushMultiply();
			Direct2DBitmapCache.Entry entry = cache.Get(target, image, ajustment, maxTileSize);
			if (entry == null)
			{
				return;
			}
			bool copy = CompositingMode == CompositingMode.SourceCopy;
			if (copy)
			{
				SetPrimitiveBlend(copy: true);
			}
			try
			{
				D2D.InterpolationMode mode = ChooseInterpolation(dest, src);
				foreach (TilePart part in GetTileParts(entry, dest, src))
				{
					if (context != null)
					{
						context.DrawBitmap(part.Bitmap, part.Dest, opacity, mode, part.Src, null);
					}
					else
					{
						target.DrawBitmap(part.Bitmap, part.Dest, opacity, D2D.BitmapInterpolationMode.Linear, part.Src);
					}
				}
			}
			finally
			{
				if (copy)
				{
					SetPrimitiveBlend(copy: false);
				}
			}
		}

		public void DrawBlurredImage(RendererImage image, RectangleF dest, RectangleF src, float blur)
		{
			//Same as the OpenGL renderer, which never blurred either.
			DrawImage(image, dest, src, BitmapAdjustment.Empty, 1f);
		}

		public void FillRectangle(RectangleF bounds, Color color)
		{
			if (!drawing || bounds.Width <= 0f || bounds.Height <= 0f)
			{
				return;
			}
			FlushMultiply();
			//Matches both existing renderers: the global opacity replaces the colour's own alpha.
			color = Color.FromArgb(ToAlpha(opacity), color);
			if (color.A < 5)
			{
				return;
			}
			brush.Color = ToColor4(color);
			bool copy = CompositingMode == CompositingMode.SourceCopy;
			if (copy)
			{
				SetPrimitiveBlend(copy: true);
			}
			try
			{
				target.FillRectangle(ToRect(bounds), brush);
			}
			finally
			{
				if (copy)
				{
					SetPrimitiveBlend(copy: false);
				}
			}
		}

		public void DrawLine(IEnumerable<PointF> points, Color color, float width)
		{
			if (!drawing || points == null)
			{
				return;
			}
			FlushMultiply();
			color = Color.FromArgb(ToAlpha(opacity), color);
			if (color.A < 5)
			{
				return;
			}
			brush.Color = ToColor4(color);
			bool first = true;
			PointF last = PointF.Empty;
			foreach (PointF point in points)
			{
				if (!first)
				{
					target.DrawLine(new RawVector2(last.X, last.Y), new RawVector2(point.X, point.Y), brush, width);
				}
				last = point;
				first = false;
			}
		}

		public bool IsVisible(RectangleF bounds)
		{
			return true;
		}

		public IDisposable SaveState()
		{
			transformStack.Push(transform.Clone());
			return new Disposer(delegate
			{
				if (transformStack.Count > 0)
				{
					Matrix saved = transformStack.Pop();
					Matrix old = transform;
					transform = saved;
					old.Dispose();
					ApplyTransform();
				}
			}, eatErrors: true);
		}

		public void TranslateTransform(float dx, float dy)
		{
			transform.Translate(dx, dy);
			ApplyTransform();
		}

		public void ScaleTransform(float dx, float dy)
		{
			transform.Scale(dx, dy);
			ApplyTransform();
		}

		public void RotateTransform(float angel)
		{
			transform.Rotate(angel);
			ApplyTransform();
		}

		public Bitmap GetFramebuffer(Rectangle rc, bool flip)
		{
			//Only the OpenGL renderer ever implemented this, and nothing in ComicRack calls it.
			throw new NotSupportedException("GetFramebuffer is not supported by the Direct2D renderer.");
		}

		public void ClearStencil()
		{
		}

		#region Polygon clipping and fills

		public void PushPolygonClip(PointF[] polygon)
		{
			if (!drawing || polygon == null || polygon.Length < 3)
			{
				clipLayers.Push(false);
				return;
			}
			FlushMultiply();
			D2D.PathGeometry geometry = CreatePolygon(polygon);
			frameTemporaries.Add(geometry);
			while (layerPool.Count <= layerDepth)
			{
				layerPool.Add(new D2D.Layer(target));
			}
			RectangleF area = PolygonBounds(polygon);
			area.Inflate(1f, 1f);
			D2D.LayerParameters parameters = new D2D.LayerParameters
			{
				//Bounding the layer matters: with unbounded content Direct2D sets aside an
				//intermediate surface the size of the whole window for every layer, which is
				//ruinous when a page is drawn as a dozen thin slices.
				ContentBounds = ToRect(area),
				GeometricMask = geometry,
				MaskAntialiasMode = D2D.AntialiasMode.PerPrimitive,
				MaskTransform = Identity,
				Opacity = 1f
			};
			//The mask is transformed by the world transform, so push it in screen space.
			target.Transform = Identity;
			target.PushLayer(ref parameters, layerPool[layerDepth]);
			target.Transform = ToRaw(transform.Elements, 0f, 0f);
			activeLayerGeometries.Add(geometry);
			activeLayerBounds.Add(PolygonBounds(polygon));
			layerDepth++;
			clipLayers.Push(true);
		}

		public void PopPolygonClip()
		{
			if (clipLayers.Count == 0)
			{
				return;
			}
			if (!clipLayers.Pop() || layerDepth == 0)
			{
				return;
			}
			if (drawing)
			{
				FlushMultiply();
				target.PopLayer();
			}
			layerDepth--;
			if (activeLayerGeometries.Count > layerDepth)
			{
				activeLayerGeometries.RemoveAt(activeLayerGeometries.Count - 1);
				activeLayerBounds.RemoveAt(activeLayerBounds.Count - 1);
			}
		}

		public void FillPolygon(PointF[] polygon, Color color)
		{
			if (!drawing || polygon == null || polygon.Length < 3 || color.A == 0)
			{
				return;
			}
			FlushMultiply();
			D2D.PathGeometry geometry = CreatePolygon(polygon);
			frameTemporaries.Add(geometry);
			brush.Color = ToColor4(color);
			target.Transform = Identity;
			target.FillGeometry(geometry, brush);
			target.Transform = ToRaw(transform.Elements, 0f, 0f);
		}

		public void FillPolygonGradient(PointF[] polygon, PointF start, Color startColor, PointF end, Color endColor)
		{
			if (!drawing || polygon == null || polygon.Length < 3)
			{
				return;
			}
			FlushMultiply();
			D2D.PathGeometry geometry = CreatePolygon(polygon);
			frameTemporaries.Add(geometry);
			D2D.GradientStop[] stops = new D2D.GradientStop[2]
			{
				new D2D.GradientStop
				{
					Position = 0f,
					Color = ToColor4(startColor)
				},
				new D2D.GradientStop
				{
					Position = 1f,
					Color = ToColor4(endColor)
				}
			};
			D2D.GradientStopCollection collection = new D2D.GradientStopCollection(target, stops);
			frameTemporaries.Add(collection);
			D2D.LinearGradientBrush gradient = new D2D.LinearGradientBrush(target, new D2D.LinearGradientBrushProperties
			{
				StartPoint = new RawVector2(start.X, start.Y),
				EndPoint = new RawVector2(end.X, end.Y)
			}, collection);
			frameTemporaries.Add(gradient);
			target.Transform = Identity;
			target.FillGeometry(geometry, gradient);
			target.Transform = ToRaw(transform.Elements, 0f, 0f);
		}

		/// <summary>
		/// Pops every layer, remembering them, and returns how many were popped.
		/// </summary>
		private static RectangleF PolygonBounds(PointF[] polygon)
		{
			float minX = float.MaxValue;
			float minY = float.MaxValue;
			float maxX = float.MinValue;
			float maxY = float.MinValue;
			foreach (PointF p in polygon)
			{
				minX = Math.Min(minX, p.X);
				minY = Math.Min(minY, p.Y);
				maxX = Math.Max(maxX, p.X);
				maxY = Math.Max(maxY, p.Y);
			}
			return RectangleF.FromLTRB(minX, minY, maxX, maxY);
		}

		private int SuspendLayers()
		{
			int count = layerDepth;
			for (int i = 0; i < count; i++)
			{
				target.PopLayer();
			}
			layerDepth = 0;
			return count;
		}

		/// <summary>
		/// Pushes the layers again, in the order they were originally pushed.
		/// </summary>
		private void ResumeLayers(int count)
		{
			RawMatrix3x2 current = ToRaw(transform.Elements, 0f, 0f);
			for (int i = 0; i < count && i < activeLayerGeometries.Count; i++)
			{
				RectangleF area = activeLayerBounds[i];
				area.Inflate(1f, 1f);
				D2D.LayerParameters parameters = new D2D.LayerParameters
				{
					ContentBounds = ToRect(area),
					GeometricMask = activeLayerGeometries[i],
					MaskAntialiasMode = D2D.AntialiasMode.PerPrimitive,
					MaskTransform = Identity,
					Opacity = 1f
				};
				target.Transform = Identity;
				target.PushLayer(ref parameters, layerPool[i]);
				layerDepth++;
			}
			target.Transform = current;
		}

		private D2D.PathGeometry CreatePolygon(PointF[] polygon)
		{
			D2D.PathGeometry geometry = new D2D.PathGeometry(factory);
			using (D2D.GeometrySink sink = geometry.Open())
			{
				sink.BeginFigure(new RawVector2(polygon[0].X, polygon[0].Y), D2D.FigureBegin.Filled);
				RawVector2[] rest = new RawVector2[polygon.Length - 1];
				for (int i = 1; i < polygon.Length; i++)
				{
					rest[i - 1] = new RawVector2(polygon[i].X, polygon[i].Y);
				}
				sink.AddLines(rest);
				sink.EndFigure(D2D.FigureEnd.Closed);
				sink.Close();
			}
			return geometry;
		}

		#endregion

		private void FinishFrame()
		{
			try
			{
				FlushMultiply();
				//Layers left open by a caller would make EndDraw fail, so close them first.
				while (layerDepth > 0)
				{
					target.PopLayer();
					layerDepth--;
				}
				activeLayerGeometries.Clear();
				activeLayerBounds.Clear();
				clipLayers.Clear();
				PopClip();
				try
				{
					target.EndDraw();
					consecutiveFailures = 0;
				}
				catch (SharpDX.SharpDXException ex)
				{
					if (ex.ResultCode.Code == D2DERR_RECREATE_TARGET)
					{
						//The GPU was reset or the driver changed. Everything we uploaded is gone;
						//rebuild on the next frame and repaint.
						releaseRequested = true;
						Control?.Invalidate();
					}
					else if (usedHighQualityCubic && !highQualityCubicFailed)
					{
						//Older Direct2D versions reject high quality cubic sampling. Stop using it
						//and repaint with linear sampling instead.
						highQualityCubicFailed = true;
						Control?.Invalidate();
					}
					else
					{
						consecutiveFailures++;
						releaseRequested = true;
						Control?.Invalidate();
					}
				}
			}
			finally
			{
				drawing = false;
				foreach (IDisposable disposable in frameTemporaries)
				{
					SafeDispose(disposable);
				}
				frameTemporaries.Clear();
				if (releaseRequested)
				{
					ReleaseDeviceResources();
				}
			}
		}

		private bool EnsureTarget()
		{
			Control control = Control;
			if (control == null || control.IsDisposed || !control.IsHandleCreated)
			{
				return false;
			}
			Size size = control.ClientSize;
			if (size.Width <= 0 || size.Height <= 0)
			{
				return false;
			}
			if (target != null && (releaseRequested || targetHandle != control.Handle))
			{
				ReleaseDeviceResources();
			}
			try
			{
				if (target == null)
				{
					CreateDeviceResources(control.Handle, size);
				}
				else if (size != targetSize)
				{
					target.Resize(new SharpDX.Size2(size.Width, size.Height));
					targetSize = size;
				}
			}
			catch (SharpDX.SharpDXException)
			{
				consecutiveFailures++;
				ReleaseDeviceResources();
				return false;
			}
			return target != null;
		}

		private void CreateDeviceResources(IntPtr handle, Size size)
		{
			D2D.RenderTargetProperties renderTargetProperties = new D2D.RenderTargetProperties
			{
				PixelFormat = new D2D.PixelFormat(DXGI.Format.B8G8R8A8_UNorm, D2D.AlphaMode.Premultiplied),
				//96 DPI keeps one Direct2D unit equal to one pixel, which is what all the layout
				//code in ComicRack works in.
				DpiX = 96f,
				DpiY = 96f
			};
			D2D.HwndRenderTargetProperties hwndProperties = new D2D.HwndRenderTargetProperties
			{
				Hwnd = handle,
				PixelSize = new SharpDX.Size2(Math.Max(1, size.Width), Math.Max(1, size.Height)),
				PresentOptions = D2D.PresentOptions.None
			};
			target = new D2D.WindowRenderTarget(factory, renderTargetProperties, hwndProperties);
			try
			{
				target.AntialiasMode = D2D.AntialiasMode.PerPrimitive;
				context = target.QueryInterfaceOrNull<D2D.DeviceContext>();
				brush = new D2D.SolidColorBrush(target, new RawColor4(0f, 0f, 0f, 1f));
				maxTileSize = Math.Max(512, Math.Min(target.MaximumBitmapSize, 16384));
				targetHandle = handle;
				targetSize = size;
				releaseRequested = false;
			}
			catch
			{
				ReleaseDeviceResources();
				throw;
			}
		}

		private void ReleaseDeviceResources()
		{
			if (sceneCounter > 0 && drawing)
			{
				//Never pull resources out from under a frame in progress.
				releaseRequested = true;
				return;
			}
			pendingMultiply.Clear();
			pendingMultiplyBounds = RectangleF.Empty;
			cache.Clear();
			ReleaseMultiplyResources();
			foreach (D2D.Layer layer in layerPool)
			{
				SafeDispose(layer);
			}
			layerPool.Clear();
			activeLayerGeometries.Clear();
			activeLayerBounds.Clear();
			layerDepth = 0;
			clipLayers.Clear();
			SafeDispose(multiplyEffect);
			multiplyEffect = null;
			SafeDispose(brush);
			brush = null;
			SafeDispose(context);
			context = null;
			SafeDispose(target);
			target = null;
			targetHandle = IntPtr.Zero;
			targetSize = Size.Empty;
			clipPushed = false;
			releaseRequested = false;
		}

		private void ReleaseMultiplyResources()
		{
			SafeDispose(multiplyScratch);
			multiplyScratch = null;
			SafeDispose(multiplyLayer);
			multiplyLayer = null;
			multiplySize = Size.Empty;
		}

		private D2D.InterpolationMode ChooseInterpolation(RectangleF dest, RectangleF src)
		{
			if (OptimizedTextures || highQualityCubicFailed)
			{
				//Something is moving, or the driver rejected cubic sampling.
				return D2D.InterpolationMode.Linear;
			}
			bool shrinking = src.Width > 0f && src.Height > 0f && (dest.Width < src.Width * 0.95f || dest.Height < src.Height * 0.95f);
			//Shrinking a page is where sampling quality shows, so use the good filter there even
			//when hardware filters are switched off. Direct2D does this on the GPU, unlike the old
			//OpenGL renderer where the option paid for hand built mip maps.
			if (EnableFilter || shrinking)
			{
				usedHighQualityCubic = true;
				return HighQualityCubic;
			}
			return D2D.InterpolationMode.Linear;
		}

		private void SetPrimitiveBlend(bool copy)
		{
			if (context != null)
			{
				context.PrimitiveBlend = copy ? D2D.PrimitiveBlend.Copy : D2D.PrimitiveBlend.SourceOver;
			}
		}

		private void ApplyTransform()
		{
			if (drawing && target != null)
			{
				target.Transform = ToRaw(transform.Elements, 0f, 0f);
			}
		}

		private void PushClip()
		{
			//Pushed in device space with an identity transform, so it can be popped and pushed
			//again later (CopyFromRenderTarget refuses to run while a clip is active).
			target.Transform = Identity;
			target.PushAxisAlignedClip(ToRect(clipDevice), D2D.AntialiasMode.Aliased);
			target.Transform = ToRaw(transform.Elements, 0f, 0f);
			clipPushed = true;
		}

		private void PopClip()
		{
			if (clipPushed)
			{
				target.PopAxisAlignedClip();
				clipPushed = false;
			}
		}

		#region Multiply blending (paper textures)

		//Direct2D has no multiply blend state, so paper textures are done in one batch per frame:
		//copy the covered part of the frame, draw the texture draws into a transparent layer, and
		//combine both with the Blend effect in multiply mode. Tiled textures arrive as dozens of
		//small draws, which is why they are queued rather than blended one by one.

		private void QueueMultiply(RendererImage image, RectangleF dest, RectangleF src, BitmapAdjustment adjustment, float opacity)
		{
			if (context == null || multiplyFailed)
			{
				//No Direct2D 1.1, or it failed before: leave the paper texture out rather than
				//painting it over the page as a normal image.
				return;
			}
			float[] elements = transform.Elements;
			pendingMultiply.Add(new PendingMultiply
			{
				Image = image,
				Dest = dest,
				Src = src,
				Adjustment = adjustment,
				Opacity = opacity,
				Transform = elements
			});
			RectangleF bounds = TransformBounds(dest, elements);
			pendingMultiplyBounds = pendingMultiplyBounds.IsEmpty ? bounds : RectangleF.Union(pendingMultiplyBounds, bounds);
		}

		private void FlushMultiply()
		{
			if (pendingMultiply.Count == 0)
			{
				return;
			}

			try
			{
				RectangleF bounds = pendingMultiplyBounds;
				if (clipPushed)
				{
					bounds.Intersect(clipDevice);
				}
				foreach (RectangleF layerBounds in activeLayerBounds)
				{
					//Only the part the layers let through can be affected, so only that part has to
					//be copied and blended.
					bounds.Intersect(layerBounds);
				}
				bounds.Intersect(new RectangleF(0f, 0f, targetSize.Width, targetSize.Height));
				Rectangle region = Rectangle.FromLTRB((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top), (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));
				region.Intersect(new Rectangle(Point.Empty, targetSize));
				if (region.Width <= 0 || region.Height <= 0)
				{
					return;
				}
				EnsureMultiplyResources(region.Size);
				//Copying from the target is refused while any clip or layer is active, so take them
				//off for the copy and put them straight back. The blend itself is then drawn inside
				//them again, which is what keeps paper textures working on a folded page.
				bool hadClip = clipPushed;
				int suspended = SuspendLayers();
				PopClip();
				multiplyScratch.CopyFromRenderTarget(target, new RawPoint(0, 0), new RawRectangle(region.Left, region.Top, region.Right, region.Bottom));
				if (hadClip)
				{
					PushClip();
				}
				ResumeLayers(suspended);
				multiplyLayer.BeginDraw();
				try
				{
					multiplyLayer.Clear(new RawColor4(0f, 0f, 0f, 0f));
					foreach (PendingMultiply item in pendingMultiply)
					{
						Direct2DBitmapCache.Entry entry = cache.Get(target, item.Image, item.Adjustment, maxTileSize);
						if (entry == null)
						{
							continue;
						}
						multiplyLayer.Transform = ToRaw(item.Transform, -region.X, -region.Y);
						foreach (TilePart part in GetTileParts(entry, item.Dest, item.Src))
						{
							multiplyLayer.DrawBitmap(part.Bitmap, part.Dest, item.Opacity, D2D.BitmapInterpolationMode.Linear, part.Src);
						}
					}
				}
				finally
				{
					multiplyLayer.EndDraw();
				}
				D2D.Bitmap layerBitmap = multiplyLayer.Bitmap;
				frameTemporaries.Add(layerBitmap);
				if (multiplyEffect == null)
				{
					multiplyEffect = new D2D.Effects.Blend(context);
					multiplyEffect.Mode = D2D.BlendMode.Multiply;
				}
				//Input 0 is the destination (what is on screen), input 1 the source (the texture).
				multiplyEffect.SetInput(0, multiplyScratch, true);
				multiplyEffect.SetInput(1, layerBitmap, true);
				target.Transform = Identity;
				context.DrawImage(multiplyEffect, new RawVector2(region.X, region.Y), D2D.InterpolationMode.NearestNeighbor, D2D.CompositeMode.SourceOver);
				target.Transform = ToRaw(transform.Elements, 0f, 0f);
			}
			catch (Exception)
			{
				//Paper textures are decoration. If this machine can not do them, turn them off
				//for this session instead of losing hardware acceleration.
				multiplyFailed = true;
				ReleaseMultiplyResources();
				if (drawing && target != null)
				{
					target.Transform = ToRaw(transform.Elements, 0f, 0f);
				}
			}
			finally
			{
				pendingMultiply.Clear();
				pendingMultiplyBounds = RectangleF.Empty;
			}
		}

		private void EnsureMultiplyResources(Size size)
		{
			if (multiplyScratch != null && multiplyLayer != null && multiplySize == size)
			{
				return;
			}
			ReleaseMultiplyResources();
			//The scratch copy must match the target's pixel format for CopyFromRenderTarget.
			multiplyScratch = new D2D.Bitmap(target, new SharpDX.Size2(size.Width, size.Height), new D2D.BitmapProperties(target.PixelFormat));
			multiplyLayer = new D2D.BitmapRenderTarget(target, D2D.CompatibleRenderTargetOptions.None, new SharpDX.Size2F(size.Width, size.Height));
			multiplySize = size;
		}

		#endregion

		#region Geometry helpers

		private struct TilePart
		{
			public D2D.Bitmap Bitmap;

			public RawRectangleF Dest;

			public RawRectangleF Src;
		}

		private static IEnumerable<TilePart> GetTileParts(Direct2DBitmapCache.Entry entry, RectangleF dest, RectangleF src)
		{
			float scaleX = dest.Width / src.Width;
			float scaleY = dest.Height / src.Height;
			foreach (Direct2DBitmapCache.Tile tile in entry.Tiles)
			{
				RectangleF part = RectangleF.Intersect(tile.Bounds, src);
				if (part.Width <= 0f || part.Height <= 0f)
				{
					continue;
				}
				RectangleF partDest = new RectangleF(dest.X + (part.X - src.X) * scaleX, dest.Y + (part.Y - src.Y) * scaleY, part.Width * scaleX, part.Height * scaleY);
				RectangleF partSrc = new RectangleF(part.X - tile.Bounds.X, part.Y - tile.Bounds.Y, part.Width, part.Height);
				yield return new TilePart
				{
					Bitmap = tile.Bitmap,
					Dest = ToRect(partDest),
					Src = ToRect(partSrc)
				};
			}
		}

		private RectangleF TransformBounds(RectangleF r)
		{
			return TransformBounds(r, transform.Elements);
		}

		private static RectangleF TransformBounds(RectangleF r, float[] m)
		{
			float minX = float.MaxValue;
			float minY = float.MaxValue;
			float maxX = float.MinValue;
			float maxY = float.MinValue;
			PointF[] corners = new PointF[4]
			{
				new PointF(r.Left, r.Top),
				new PointF(r.Right, r.Top),
				new PointF(r.Right, r.Bottom),
				new PointF(r.Left, r.Bottom)
			};
			foreach (PointF p in corners)
			{
				float x = p.X * m[0] + p.Y * m[2] + m[4];
				float y = p.X * m[1] + p.Y * m[3] + m[5];
				minX = Math.Min(minX, x);
				minY = Math.Min(minY, y);
				maxX = Math.Max(maxX, x);
				maxY = Math.Max(maxY, y);
			}
			return RectangleF.FromLTRB(minX, minY, maxX, maxY);
		}

		private static bool IsDrawable(RectangleF r)
		{
			return r.Width > 0f && r.Height > 0f && !float.IsNaN(r.Width) && !float.IsNaN(r.Height) && !float.IsInfinity(r.Width) && !float.IsInfinity(r.Height);
		}

		private static readonly RawMatrix3x2 Identity = new RawMatrix3x2
		{
			M11 = 1f,
			M12 = 0f,
			M21 = 0f,
			M22 = 1f,
			M31 = 0f,
			M32 = 0f
		};

		private static RawMatrix3x2 ToRaw(float[] m, float offsetX, float offsetY)
		{
			//System.Drawing matrix elements are [m11, m12, m21, m22, dx, dy], the same row vector
			//convention Direct2D uses, so they map across one to one.
			return new RawMatrix3x2
			{
				M11 = m[0],
				M12 = m[1],
				M21 = m[2],
				M22 = m[3],
				M31 = m[4] + offsetX,
				M32 = m[5] + offsetY
			};
		}

		private static RawRectangleF ToRect(RectangleF r)
		{
			return new RawRectangleF
			{
				Left = r.Left,
				Top = r.Top,
				Right = r.Right,
				Bottom = r.Bottom
			};
		}

		private static RawColor4 ToColor4(Color color)
		{
			return new RawColor4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
		}

		private static int ToAlpha(float opacity)
		{
			return (int)(255f * Math.Max(0f, Math.Min(1f, opacity)));
		}

		#endregion

		private void window_Paint(object sender, PaintEventArgs e)
		{
			if (BeginScene(null))
			{
				try
				{
					Paint?.Invoke(this, EventArgs.Empty);
				}
				finally
				{
					EndScene();
				}
			}
			else
			{
				EndScene();
			}
		}

		private void window_Disposed(object sender, EventArgs e)
		{
			Dispose();
		}

		private void window_HandleChanged(object sender, EventArgs e)
		{
			//The render target is bound to one window handle. When WinForms recreates the handle
			//(docking, full screen, some style changes) the old target has to go.
			ReleaseDeviceResources();
		}

		private static void SetStyle(Control window, ControlStyles styles, bool enable)
		{
			typeof(Control).GetMethod("SetStyle", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[2]
			{
				styles,
				enable
			});
		}

		private static void SafeDispose(IDisposable disposable)
		{
			try
			{
				disposable?.Dispose();
			}
			catch
			{
			}
		}
	}
}
