using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace ComicRackLite
{
	/// <summary>
	/// The reader window, laid out like ComicRack's: the menu on the left of the top bar, the reader's tool bar on the right
	/// (pages, page layout, fit, zoom, rotate, full screen), plus a button that opens the book in ComicRack too.
	/// </summary>
	internal sealed class ViewerForm : Form
	{
		private sealed class Command
		{
			public string Text;

			public Image Image;

			public Action Run;

			public Func<bool> Checked;

			public Func<bool> Enabled;

			public string Keys;
		}

		private enum OpenAt
		{
			Remembered,
			End
		}

		private readonly LiteSettings settings = LiteSettings.Load();

		private readonly PageView view = new PageView();

		private readonly Panel topBar = new Panel();

		private readonly MenuStrip menu = new MenuStrip();

		private readonly ToolStrip tools = new ToolStrip();

		private readonly ContextMenuStrip context = new ContextMenuStrip();

		private readonly List<KeyValuePair<ToolStripItem, Command>> bound = new List<KeyValuePair<ToolStripItem, Command>>();

		private readonly Dictionary<Keys, Action> keys = new Dictionary<Keys, Action>();

		private readonly LinkedList<RenderResult> rendered = new LinkedList<RenderResult>();

		private readonly System.Windows.Forms.Timer resizeTimer = new System.Windows.Forms.Timer { Interval = 120 };

		private readonly Renderer renderer;

		/// <summary>ComicRack's paper texture, or null when it has none.</summary>
		private readonly PaperTexture paper;

		private readonly string startFile;

		/// <summary>The page (0 based) asked for with -p, used for the first book only; -1 when none.</summary>
		private int requestedPage;

		private ToolStripLabel pageLabel;

		private ToolStripDropDownButton tbLayout;

		private ToolStripDropDownButton tbFit;

		private PageSource source;

		private string bookPath;

		private int[] spread = new int[0];

		private int rotation;

		private float zoom = 1f;

		private bool dark;

		private bool fullScreen;

		private Rectangle normalBounds;

		private FormWindowState normalState;

		private bool scrollPending;

		private bool scrollToEnd;

		private int wheelRest;

		/// <summary>Set by a page turn (1 forward, -1 back) until its spread is shown, which plays the page turn effect.</summary>
		private int turnDirection;

		private string pendingBook;

		private DateTime pendingUntil;

		private int openGeneration;

		private const int KeepRendered = 5;

		private int Count => source?.Count ?? 0;

		private string BookKey => source is FolderSource ? source.Path : bookPath;

		public ViewerForm(string file, int startPage = -1)
		{
			startFile = file;
			requestedPage = startPage;
			Text = Program.NameAndVersion;
			try
			{
				Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
			}
			catch (Exception)
			{
			}
			Font = new Font("Segoe UI", 9f);
			AutoScaleMode = AutoScaleMode.None;
			BackColor = Color.Black;
			AllowDrop = true;
			MinimumSize = new Size(360, 300);
			StartPosition = FormStartPosition.Manual;
			Rectangle work = Screen.PrimaryScreen.WorkingArea;
			if (settings.Bounds != Rectangle.Empty && Screen.AllScreens.Any((Screen s) => s.WorkingArea.IntersectsWith(settings.Bounds)))
			{
				Bounds = settings.Bounds;
			}
			else
			{
				int h = work.Height * 9 / 10;
				int w = Math.Min(work.Width, h * 3 / 4);
				Bounds = new Rectangle(work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h);
			}
			if (settings.Maximized)
			{
				WindowState = FormWindowState.Maximized;
			}
			paper = PaperTexture.FromComicRack();
			renderer = new Renderer(delegate (RenderResult r)
			{
				try
				{
					BeginInvoke(new Action(delegate
					{
						OnRendered(r);
					}));
				}
				catch (Exception)
				{
					r.Bitmap?.Dispose();
				}
			}, paper);
			BuildUi();
			ApplyTheme();
			UpdateUi();
		}

		#region Building the window

		private static readonly Dictionary<string, Image> icons = new Dictionary<string, Image>();

		/// <summary>Which icon an image is (light or dark version), to swap it when the theme changes.</summary>
		private static readonly Dictionary<Image, string> iconNames = new Dictionary<Image, string>();

		private static Image LoadIcon(string resource, string name)
		{
			if (icons.TryGetValue(resource, out Image icon))
			{
				return icon;
			}
			using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
			{
				if (s == null)
				{
					icons[resource] = null;
					return null;
				}
				//a copy: an image read from a stream needs the stream for as long as it lives
				using (Image read = Image.FromStream(s))
				{
					icon = new Bitmap(read);
				}
			}
			icons[resource] = icon;
			iconNames[icon] = name;
			return icon;
		}

		private static Image Icon16(string name)
		{
			return LoadIcon("Icons." + name + ".png", name);
		}

		/// <summary>The icon for the theme: ComicRack's dark version where it has one (the page arrows).</summary>
		private Image Themed(string name)
		{
			return (dark ? LoadIcon("IconsDark." + name + ".png", name) : null) ?? Icon16(name);
		}

		private void ThemeIcons(ToolStripItemCollection items)
		{
			foreach (ToolStripItem item in items)
			{
				if (item.Image != null && iconNames.TryGetValue(item.Image, out string name))
				{
					item.Image = Themed(name);
				}
				if (item is ToolStripDropDownItem dd)
				{
					ThemeIcons(dd.DropDownItems);
				}
			}
		}

		private static Image ComicRackIcon()
		{
			using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Icons.ComicRack.ico"))
			{
				if (s == null)
				{
					return null;
				}
				using (Icon icon = new Icon(s, 16, 16))
				{
					return icon.ToBitmap();
				}
			}
		}

		private Command C(string text, Image image, Action run, string keyText = null, Func<bool> isChecked = null, Func<bool> enabled = null)
		{
			return new Command { Text = text, Image = image, Run = run, Keys = keyText, Checked = isChecked, Enabled = enabled };
		}

		private ToolStripMenuItem Item(Command c)
		{
			ToolStripMenuItem mi = new ToolStripMenuItem(c.Text, c.Image, delegate
			{
				c.Run();
			});
			if (c.Keys != null)
			{
				mi.ShortcutKeyDisplayString = c.Keys;
			}
			bound.Add(new KeyValuePair<ToolStripItem, Command>(mi, c));
			return mi;
		}

		private ToolStripButton Button(Command c, bool withText = false)
		{
			ToolStripButton b = new ToolStripButton(withText ? c.Text.Replace("&", "") : null, c.Image, delegate
			{
				c.Run();
			})
			{
				DisplayStyle = withText ? ToolStripItemDisplayStyle.ImageAndText : ToolStripItemDisplayStyle.Image,
				ToolTipText = c.Text.Replace("&", "") + (c.Keys != null ? " (" + c.Keys + ")" : "")
			};
			bound.Add(new KeyValuePair<ToolStripItem, Command>(b, c));
			return b;
		}

		private void Bind(Action action, params Keys[] keyList)
		{
			foreach (Keys k in keyList)
			{
				keys[k] = action;
			}
		}

		private void BuildUi()
		{
			bool Rtl() => settings.RightToLeft;
			Func<bool> hasBook = () => source != null && Count > 0;
			Func<bool> hasFile = () => bookPath != null;

			Command open = C("&Open Book...", Icon16("Open"), OpenDialog, "Ctrl+O");
			Command comicRack = C("Open in &ComicRack", ComicRackIcon(), OpenInComicRack, "Ctrl+Enter");
			Command explorer = C("Show in &Explorer", null, ShowInExplorer, null, null, hasFile);
			Command register = C("Use ComicRack Lite for Comic &Files...", null, RegisterShell);
			Command exit = C("E&xit", null, Close, "Ctrl+Q");

			Command first = C("&First Page", Icon16("GoFirst"), delegate { GoTo(0); }, "Home", null, hasBook);
			Command prev = C("&Previous Page", Icon16("GoPrevious"), delegate { Prev(); }, "PgUp", null, hasBook);
			Command next = C("&Next Page", Icon16("GoNext"), Next, "PgDn", null, hasBook);
			Command last = C("&Last Page", Icon16("GoLast"), delegate { GoTo(Count - 1); }, "End", null, hasBook);
			Command prevBook = C("Pre&vious Book", Icon16("PrevFromList"), delegate { OpenNeighbor(-1); }, "Ctrl+PgUp", null, hasFile);
			Command nextBook = C("Ne&xt Book", Icon16("NextFromList"), delegate { OpenNeighbor(1); }, "Ctrl+PgDn", null, hasFile);
			Command goTo = C("&Go to Page...", null, GoToDialog, "Ctrl+G", null, hasBook);

			Command single = C("&Single Page", Icon16("SinglePage"), delegate { SetLayout(PageLayout.Single); }, null, () => settings.Layout == PageLayout.Single);
			Command two = C("&Two Pages", Icon16("TwoPageForced"), delegate { SetLayout(PageLayout.TwoPages); }, null, () => settings.Layout == PageLayout.TwoPages);
			Command adaptive = C("Two Pages (&Adaptive)", Icon16("TwoPage"), delegate { SetLayout(PageLayout.TwoPagesAdaptive); }, null, () => settings.Layout == PageLayout.TwoPagesAdaptive);
			Command rtl = C("&Right to Left", Icon16("RightToLeft"), delegate { ChangeView(delegate { settings.RightToLeft = !settings.RightToLeft; }); }, null, Rtl);
			Command original = C("&Original Size", Icon16("Original"), delegate { SetFit(FitMode.Original); }, "4", () => settings.Fit == FitMode.Original);
			Command fitAll = C("Fit &All", Icon16("FitAll"), delegate { SetFit(FitMode.FitAll); }, "1", () => settings.Fit == FitMode.FitAll);
			Command fitWidth = C("Fit &Width", Icon16("FitWidth"), delegate { SetFit(FitMode.FitWidth); }, "2", () => settings.Fit == FitMode.FitWidth);
			Command fitHeight = C("Fit &Height", Icon16("FitHeight"), delegate { SetFit(FitMode.FitHeight); }, "3", () => settings.Fit == FitMode.FitHeight);
			Command oversized = C("Only Fit if &Oversized", Icon16("Oversized"), delegate { ChangeView(delegate { settings.OnlyFitOversized = !settings.OnlyFitOversized; }); }, null, () => settings.OnlyFitOversized);
			Command zoomIn = C("Zoom &In", Icon16("ZoomIn"), ZoomIn, "+");
			Command zoomOut = C("Zoom O&ut", Icon16("ZoomOut"), ZoomOut, "-");
			Command zoomReset = C("&Reset Zoom", null, delegate { ChangeView(delegate { zoom = 1f; }); }, "0", null, () => Math.Abs(zoom - 1f) > 0.001f);
			Command rotLeft = C("Rotate &Left", Icon16("RotateLeft"), delegate { Rotate(-90); }, "Shift+R");
			Command rotRight = C("Rotate Ri&ght", Icon16("RotateRight"), delegate { Rotate(90); }, "R");
			Command full = C("&Full Screen", Icon16("FullScreen"), ToggleFullScreen, "F", () => fullScreen);
			Command minimal = C("&Minimal User Interface", Icon16("MenuToggle"), ToggleToolbar, "K", () => !settings.ToolbarVisible);
			Command paperTexture = C(paper != null ? "&Paper Texture" : "&Paper Texture (none set in ComicRack)", null, delegate { ChangeView(delegate { settings.PaperTexture = !settings.PaperTexture; }); }, null, () => settings.PaperTexture && paper != null, () => paper != null);
			Command effectNone = C("&None", null, delegate { settings.PageEffect = PageEffect.None; UpdateUi(); }, null, () => settings.PageEffect == PageEffect.None);
			Command effectSlide = C("&Slide", null, delegate { settings.PageEffect = PageEffect.Slide; UpdateUi(); }, null, () => settings.PageEffect == PageEffect.Slide);
			Command effectFade = C("&Fade", null, delegate { settings.PageEffect = PageEffect.Fade; UpdateUi(); }, null, () => settings.PageEffect == PageEffect.Fade);
			Command curlLight = C("Curl &Light (as ComicRack)", null, delegate { settings.CurlAmount = CurlAmount.Light; UpdateUi(); }, null, () => settings.CurlAmount == CurlAmount.Light);
			Command curlMore = C("Curl &More", null, delegate { settings.CurlAmount = CurlAmount.More; UpdateUi(); }, null, () => settings.CurlAmount == CurlAmount.More);
			Command curlMost = C("Curl Mos&t", null, delegate { settings.CurlAmount = CurlAmount.Most; UpdateUi(); }, null, () => settings.CurlAmount == CurlAmount.Most);
			Command effectCurl = C("Page &Curl", null, delegate { settings.PageEffect = PageEffect.Curl; UpdateUi(); }, null, () => settings.PageEffect == PageEffect.Curl);
			Command clickTurn = C("&Click Page Sides to Turn Pages", null, delegate { settings.ClickTurnsPages = !settings.ClickTurnsPages; UpdateUi(); }, null, () => settings.ClickTurnsPages);
			Command themeCr = C("Like &ComicRack", null, delegate { SetTheme(ThemeChoice.FollowComicRack); }, null, () => settings.Theme == ThemeChoice.FollowComicRack);
			Command themeLight = C("&Light", null, delegate { SetTheme(ThemeChoice.Light); }, null, () => settings.Theme == ThemeChoice.Light);
			Command themeDark = C("&Dark", null, delegate { SetTheme(ThemeChoice.Dark); }, null, () => settings.Theme == ThemeChoice.Dark);

			Command keyHelp = C("&Keyboard and Mouse", null, ShowKeys, "F1");
			Command about = C("&About ComicRack Lite", null, ShowAbout);

			//the menu, as ComicRack's
			ToolStripMenuItem file = new ToolStripMenuItem("&File");
			file.DropDownItems.AddRange(new ToolStripItem[] { Item(open), Item(comicRack), Item(explorer), new ToolStripSeparator(), Item(register), new ToolStripSeparator(), Item(exit) });
			ToolStripMenuItem read = new ToolStripMenuItem("&Read");
			read.DropDownItems.AddRange(new ToolStripItem[] { Item(first), Item(prev), Item(next), Item(last), new ToolStripSeparator(), Item(prevBook), Item(nextBook), new ToolStripSeparator(), Item(goTo) });
			ToolStripMenuItem theme = new ToolStripMenuItem("T&heme");
			theme.DropDownItems.AddRange(new ToolStripItem[] { Item(themeCr), Item(themeLight), Item(themeDark) });
			ToolStripMenuItem effects = new ToolStripMenuItem("Page Turn &Effect");
			effects.DropDownItems.AddRange(new ToolStripItem[] { Item(effectNone), Item(effectSlide), Item(effectFade), Item(effectCurl), new ToolStripSeparator(), Item(curlLight), Item(curlMore), Item(curlMost) });
			ToolStripMenuItem display = new ToolStripMenuItem("&Display");
			display.DropDownItems.AddRange(new ToolStripItem[]
			{
				Item(single), Item(two), Item(adaptive), Item(rtl), new ToolStripSeparator(),
				Item(original), Item(fitAll), Item(fitWidth), Item(fitHeight), Item(oversized), new ToolStripSeparator(),
				Item(zoomIn), Item(zoomOut), Item(zoomReset), new ToolStripSeparator(),
				Item(rotLeft), Item(rotRight), new ToolStripSeparator(),
				Item(paperTexture), effects, new ToolStripSeparator(),
				Item(full), Item(minimal), Item(clickTurn), new ToolStripSeparator(), theme
			});
			ToolStripMenuItem help = new ToolStripMenuItem("&Help");
			help.DropDownItems.AddRange(new ToolStripItem[] { Item(keyHelp), new ToolStripSeparator(), Item(about) });
			menu.Items.AddRange(new ToolStripItem[] { file, read, display, help });
			foreach (ToolStripMenuItem top in menu.Items)
			{
				top.DropDownOpening += delegate { UpdateUi(); };
			}
			menu.Dock = DockStyle.None;
			menu.AutoSize = true;
			menu.GripStyle = ToolStripGripStyle.Hidden;
			menu.Padding = new Padding(2, 2, 0, 2);
			menu.Location = new Point(0, 0);

			//the reader's tool bar, as ComicRack's (pages, layout, fit, zoom, rotate, full screen)
			ToolStripSplitButton tbPrev = new ToolStripSplitButton(null, Icon16("GoPrevious"), delegate { Prev(); }) { ToolTipText = "Previous Page (PgUp)" };
			tbPrev.DropDownItems.AddRange(new ToolStripItem[] { Item(first), Item(prevBook) });
			ToolStripSplitButton tbNext = new ToolStripSplitButton(null, Icon16("GoNext"), delegate { Next(); }) { ToolTipText = "Next Page (PgDn)" };
			tbNext.DropDownItems.AddRange(new ToolStripItem[] { Item(last), Item(nextBook) });
			pageLabel = new ToolStripLabel { Padding = new Padding(4, 0, 4, 0), ToolTipText = "Go to Page (Ctrl+G)", IsLink = false };
			pageLabel.Click += delegate { GoToDialog(); };
			tbLayout = new ToolStripDropDownButton(null, Icon16("SinglePage")) { ToolTipText = "Page Layout (T)" };
			tbLayout.DropDownItems.AddRange(new ToolStripItem[] { Item(single), Item(two), Item(adaptive), new ToolStripSeparator(), Item(rtl) });
			tbFit = new ToolStripDropDownButton(null, Icon16("FitAll")) { ToolTipText = "Page Fit" };
			tbFit.DropDownItems.AddRange(new ToolStripItem[] { Item(original), Item(fitAll), Item(fitWidth), Item(fitHeight), new ToolStripSeparator(), Item(oversized) });
			ToolStripDropDownButton tbZoom = new ToolStripDropDownButton(null, Icon16("ZoomIn")) { ToolTipText = "Zoom" };
			tbZoom.DropDownItems.AddRange(new ToolStripItem[] { Item(zoomIn), Item(zoomOut), Item(zoomReset) });
			ToolStripSplitButton tbRotate = new ToolStripSplitButton(null, Icon16("RotateRight"), delegate { Rotate(90); }) { ToolTipText = "Rotate Right (R)" };
			tbRotate.DropDownItems.AddRange(new ToolStripItem[] { Item(rotLeft), Item(rotRight) });
			foreach (ToolStripDropDownItem dd in new ToolStripDropDownItem[] { tbPrev, tbNext, tbLayout, tbFit, tbZoom, tbRotate })
			{
				dd.DropDownOpening += delegate { UpdateUi(); };
			}
			tools.Items.AddRange(new ToolStripItem[]
			{
				Button(comicRack, withText: true), new ToolStripSeparator(),
				tbPrev, pageLabel, tbNext, new ToolStripSeparator(),
				tbLayout, tbFit, tbZoom, tbRotate, new ToolStripSeparator(),
				Button(full), Button(minimal)
			});
			tools.Dock = DockStyle.None;
			tools.AutoSize = true;
			tools.GripStyle = ToolStripGripStyle.Hidden;
			tools.LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;
			tools.Padding = new Padding(0, 1, 2, 1);
			tools.CanOverflow = true;

			topBar.Dock = DockStyle.Top;
			topBar.Height = Math.Max(menu.PreferredSize.Height, tools.PreferredSize.Height) + 2;
			topBar.Controls.Add(menu);
			topBar.Controls.Add(tools);
			topBar.Resize += delegate { PlaceToolBar(); };
			tools.SizeChanged += delegate { PlaceToolBar(); };
			menu.SizeChanged += delegate { PlaceToolBar(); };

			//right-click on the page
			context.Items.AddRange(new ToolStripItem[]
			{
				Item(comicRack), new ToolStripSeparator(),
				Item(prev), Item(next), new ToolStripSeparator(),
				Item(single), Item(two), Item(adaptive), Item(rtl), new ToolStripSeparator(),
				Item(fitAll), Item(fitWidth), Item(fitHeight), Item(original), new ToolStripSeparator(),
				Item(full), Item(minimal), new ToolStripSeparator(),
				Item(open), Item(exit)
			});
			context.Opening += delegate { UpdateUi(); };

			view.Dock = DockStyle.Fill;
			view.DisplaySizeOf = (Size natural) => Params().DisplaySize(natural);
			view.ContextMenuStrip = context;
			view.TransitionEnded += delegate (RenderResult from)
			{
				renderer.Hold(false);
				Release(from);
			};
			view.MouseWheel += OnWheel;
			view.MouseUp += delegate (object s, MouseEventArgs e)
			{
				if (e.Button == MouseButtons.XButton1)
				{
					Prev();
				}
				else if (e.Button == MouseButtons.XButton2)
				{
					Next();
				}
			};
			view.MouseDoubleClick += delegate (object s, MouseEventArgs e)
			{
				if (e.Button == MouseButtons.Left && !settings.ClickTurnsPages)
				{
					ToggleFullScreen();
				}
			};
			view.PageClicked += delegate (float x)
			{
				if (!settings.ClickTurnsPages)
				{
					return;
				}
				if (x < 0.4f)
				{
					if (settings.RightToLeft)
					{
						Next();
					}
					else
					{
						Prev();
					}
				}
				else if (x > 0.6f)
				{
					if (settings.RightToLeft)
					{
						Prev();
					}
					else
					{
						Next();
					}
				}
			};
			view.Resize += delegate
			{
				resizeTimer.Stop();
				resizeTimer.Start();
			};
			resizeTimer.Tick += delegate
			{
				resizeTimer.Stop();
				ShowCurrent();
			};
			Controls.Add(view);
			Controls.Add(topBar);

			//keys (ComicRack's where it has them)
			Bind(delegate { if (settings.RightToLeft) { Prev(); } else { Next(); } }, Keys.Right);
			Bind(delegate { if (settings.RightToLeft) { Next(); } else { Prev(); } }, Keys.Left);
			Bind(Next, Keys.PageDown, Keys.N);
			Bind(delegate { Prev(); }, Keys.PageUp, Keys.Back, Keys.P);
			Bind(delegate { if (!view.ScrollBy(0, view.ClientSize.Height * 85 / 100)) { Next(); } }, Keys.Space);
			Bind(delegate { if (!view.ScrollBy(0, -view.ClientSize.Height * 85 / 100)) { Prev(atEnd: true); } }, Keys.Shift | Keys.Space);
			Bind(delegate { if (!view.ScrollBy(0, Math.Max(30, view.ClientSize.Height / 8))) { Next(); } }, Keys.Down);
			Bind(delegate { if (!view.ScrollBy(0, -Math.Max(30, view.ClientSize.Height / 8))) { Prev(atEnd: true); } }, Keys.Up);
			Bind(first.Run, Keys.Home);
			Bind(last.Run, Keys.End);
			Bind(nextBook.Run, Keys.Control | Keys.PageDown);
			Bind(prevBook.Run, Keys.Control | Keys.PageUp);
			Bind(ToggleFullScreen, Keys.F, Keys.F11, Keys.Alt | Keys.Enter);
			Bind(delegate { if (fullScreen) { ToggleFullScreen(); } else { Close(); } }, Keys.Escape);
			Bind(CycleLayout, Keys.T);
			Bind(rotRight.Run, Keys.R);
			Bind(rotLeft.Run, Keys.Shift | Keys.R);
			Bind(ToggleToolbar, Keys.K, Keys.F10);
			Bind(fitAll.Run, Keys.D1, Keys.NumPad1);
			Bind(fitWidth.Run, Keys.D2, Keys.NumPad2);
			Bind(fitHeight.Run, Keys.D3, Keys.NumPad3);
			Bind(original.Run, Keys.D4, Keys.NumPad4);
			Bind(ZoomIn, Keys.Oemplus, Keys.Shift | Keys.Oemplus, Keys.Add);
			Bind(ZoomOut, Keys.OemMinus, Keys.Subtract);
			Bind(zoomReset.Run, Keys.D0, Keys.NumPad0);
			Bind(OpenDialog, Keys.Control | Keys.O);
			Bind(OpenInComicRack, Keys.Control | Keys.Enter);
			Bind(GoToDialog, Keys.Control | Keys.G);
			Bind(ShowKeys, Keys.F1);
			Bind(Close, Keys.Control | Keys.Q, Keys.Control | Keys.W);
		}

		private void PlaceToolBar()
		{
			int h = topBar.ClientSize.Height;
			menu.Location = new Point(0, Math.Max(0, (h - menu.Height) / 2));
			tools.Location = new Point(Math.Max(menu.Right, topBar.ClientSize.Width - tools.Width), Math.Max(0, (h - tools.Height) / 2));
		}

		protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
		{
			if (keys.TryGetValue(keyData, out Action action))
			{
				action();
				return true;
			}
			return base.ProcessCmdKey(ref msg, keyData);
		}

		#endregion

		#region State and theme

		private ViewParams Params()
		{
			return new ViewParams
			{
				Fit = settings.Fit,
				OnlyFitOversized = settings.OnlyFitOversized,
				Zoom = zoom,
				Client = view.ClientSize,
				Rotation = rotation,
				RightToLeft = settings.RightToLeft,
				Paper = settings.PaperTexture && paper != null
			};
		}

		private void UpdateUi()
		{
			foreach (KeyValuePair<ToolStripItem, Command> b in bound)
			{
				if (b.Value.Checked != null)
				{
					bool on = b.Value.Checked();
					if (b.Key is ToolStripMenuItem mi)
					{
						mi.Checked = on;
					}
					else if (b.Key is ToolStripButton tb)
					{
						tb.Checked = on;
					}
				}
				if (b.Value.Enabled != null)
				{
					b.Key.Enabled = b.Value.Enabled();
				}
			}
			if (tbLayout != null)
			{
				tbLayout.Image = Themed(settings.Layout == PageLayout.Single ? "SinglePage" : settings.Layout == PageLayout.TwoPages ? "TwoPageForced" : "TwoPage");
				tbFit.Image = Themed(settings.Fit == FitMode.FitWidth ? "FitWidth" : settings.Fit == FitMode.FitHeight ? "FitHeight" : settings.Fit == FitMode.Original ? "Original" : "FitAll");
			}
			pageLabel.Text = source == null || Count == 0 ? "" : PageText();
			topBar.Visible = settings.ToolbarVisible && !fullScreen;
		}

		private void ApplyTheme()
		{
			dark = settings.IsDark();
			ToolStripRenderer r = Theme.Renderer(dark);
			menu.Renderer = r;
			tools.Renderer = r;
			context.Renderer = r;
			Color fore = dark ? Theme.DarkText : SystemColors.ControlText;
			menu.ForeColor = fore;
			tools.ForeColor = fore;
			context.ForeColor = fore;
			ThemeIcons(menu.Items);
			ThemeIcons(tools.Items);
			ThemeIcons(context.Items);
			topBar.BackColor = dark ? Theme.DarkBar : SystemColors.Control;
			menu.BackColor = topBar.BackColor;
			tools.BackColor = topBar.BackColor;
			if (IsHandleCreated)
			{
				Theme.SetTitleBar(Handle, dark);
			}
			topBar.Invalidate(invalidateChildren: true);
		}

		private void SetTheme(ThemeChoice choice)
		{
			settings.Theme = choice;
			ApplyTheme();
			UpdateUi();
			if (WindowState == FormWindowState.Normal && !fullScreen)
			{
				//Windows 10 repaints the title bar's colour when the window is activated again
				Size s = Size;
				Size = new Size(s.Width + 1, s.Height);
				Size = s;
			}
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			Theme.SetTitleBar(Handle, dark);
		}

		#endregion

		#region Opening books

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			view.Focus();
			if (startFile != null)
			{
				OpenBook(startFile);
			}
			else
			{
				view.CenterText = "Open a comic book (Ctrl+O), or drop one here.";
				view.Invalidate();
			}
		}

		private void OpenBook(string path, OpenAt at = OpenAt.Remembered)
		{
			SavePosition();
			int generation = ++openGeneration;
			pendingBook = null;
			Text = Path.GetFileName(path.TrimEnd('\\')) + " - " + Program.NameAndVersion;
			view.EndTransition();
			RenderResult old = view.Image;
			view.SetImage(null);
			Release(old);
			view.CenterIsError = false;
			view.CenterText = "Opening " + Path.GetFileName(path.TrimEnd('\\')) + "...";
			view.HideMessage();
			ThreadPool.QueueUserWorkItem(delegate
			{
				PageSource opened = null;
				Exception error = null;
				try
				{
					opened = Program.TakePreload(path) ?? PageSource.Open(path);
				}
				catch (Exception ex)
				{
					error = ex;
				}
				try
				{
					BeginInvoke(new Action(delegate
					{
						Opened(generation, path, opened, error, at);
					}));
				}
				catch (Exception)
				{
					opened?.Dispose();
				}
			});
		}

		private void Opened(int generation, string path, PageSource opened, Exception error, OpenAt at)
		{
			if (generation != openGeneration || IsDisposed)
			{
				opened?.Dispose();
				return;
			}
			source?.Dispose();
			source = null;
			ClearRendered();
			bookPath = path;
			spread = new int[0];
			rotation = 0;
			zoom = 1f;
			try
			{
				settings.LastFolder = ShellRegistration.FolderOf(path) ?? "";
			}
			catch (Exception)
			{
			}
			if (error != null || opened.Count == 0)
			{
				opened?.Dispose();
				view.CenterIsError = true;
				view.CenterText = (error != null ? "This book cannot be opened here: " + error.Message : "There are no pages in this book.") + "\n\nOpen in ComicRack: Ctrl+Enter";
				view.Invalidate();
				UpdateUi();
				return;
			}
			source = opened;
			view.CenterText = null;
			int page;
			if (at == OpenAt.End)
			{
				page = Count - 1;
			}
			else if (requestedPage >= 0)
			{
				page = Math.Min(requestedPage, Count - 1);
			}
			else if (PageSource.IsImageName(path))
			{
				page = source.StartPage;
			}
			else
			{
				int remembered = settings.GetPage(BookKey);
				page = remembered >= 0 && remembered < Count ? remembered : 0;
			}
			requestedPage = -1;
			GoTo(page, at == OpenAt.End);
		}

		private void SavePosition()
		{
			if (source != null && spread.Length > 0 && BookKey != null)
			{
				settings.SetPage(BookKey, spread[0]);
			}
		}

		private string NeighborBook(int direction)
		{
			if (bookPath == null || Directory.Exists(bookPath) || !PageSource.IsBookName(bookPath))
			{
				return null;
			}
			try
			{
				string folder = Path.GetDirectoryName(bookPath);
				List<string> books = Directory.EnumerateFiles(folder).Where(PageSource.IsBookName).ToList();
				books.Sort((string a, string b) => NaturalCompare.Compare(Path.GetFileName(a), Path.GetFileName(b)));
				int i = books.FindIndex((string b) => string.Equals(b, bookPath, StringComparison.OrdinalIgnoreCase));
				int j = i + direction;
				return i >= 0 && j >= 0 && j < books.Count ? books[j] : null;
			}
			catch (Exception)
			{
				return null;
			}
		}

		private void OpenNeighbor(int direction)
		{
			string other = NeighborBook(direction);
			if (other == null)
			{
				view.ShowMessage(direction > 0 ? "This is the last book in its folder." : "This is the first book in its folder.", 2000);
				return;
			}
			OpenBook(other, direction < 0 ? OpenAt.End : OpenAt.Remembered);
		}

		/// <summary>Paging past the last (or first) page: the first press says which book is next, the second opens it.</summary>
		private void EndOfBook(int direction)
		{
			string other = NeighborBook(direction);
			if (other == null)
			{
				view.ShowMessage(direction > 0 ? "Last page" : "First page", 1500);
				return;
			}
			if (pendingBook == other && DateTime.Now < pendingUntil)
			{
				pendingBook = null;
				view.HideMessage();
				OpenBook(other, direction < 0 ? OpenAt.End : OpenAt.Remembered);
				return;
			}
			pendingBook = other;
			pendingUntil = DateTime.Now.AddSeconds(5);
			view.ShowMessage((direction > 0 ? "Last page. Turn again for the next book:\n" : "First page. Turn again for the previous book:\n") + Path.GetFileNameWithoutExtension(other), 5000);
		}

		#endregion

		#region Pages and spreads

		private bool Wide(int page)
		{
			Size s = source.GetSize(page);
			return s.Width > s.Height;
		}

		/// <summary>
		/// The pages shown with this one. Two Pages: the cover alone, then 2-3, 4-5... Adaptive: as Two Pages, but a wide page
		/// (a double page scan) is shown alone, and pairing goes on after it.
		/// </summary>
		private int[] SpreadAt(int page)
		{
			switch (settings.Layout)
			{
				case PageLayout.TwoPages:
				{
					int start = page == 0 ? 0 : page - (page - 1) % 2;
					return start == 0 || start + 1 >= Count ? new[] { start } : new[] { start, start + 1 };
				}
				case PageLayout.TwoPagesAdaptive:
					if (page == 0 || Wide(page) || page + 1 >= Count || Wide(page + 1))
					{
						return new[] { page };
					}
					return new[] { page, page + 1 };
				default:
					return new[] { page };
			}
		}

		private int PreviousStart(int first)
		{
			int p = first - 1;
			switch (settings.Layout)
			{
				case PageLayout.TwoPages:
					return p == 0 ? 0 : p - (p - 1) % 2;
				case PageLayout.TwoPagesAdaptive:
					if (p == 0 || Wide(p))
					{
						return p;
					}
					return p - 1 >= 1 && !Wide(p - 1) ? p - 1 : p;
				default:
					return p;
			}
		}

		private string PageText()
		{
			if (spread.Length == 0)
			{
				return "";
			}
			return (spread.Length == 2 ? (spread[0] + 1) + "-" + (spread[1] + 1) : (spread[0] + 1).ToString()) + " / " + Count;
		}

		private void GoTo(int page, bool atEnd = false, int turn = 0)
		{
			if (source == null || Count == 0)
			{
				return;
			}
			page = Math.Max(0, Math.Min(Count - 1, page));
			pendingBook = null;
			spread = SpreadAt(page);
			turnDirection = turn;
			scrollPending = true;
			scrollToEnd = atEnd;
			ShowCurrent();
			view.ShowPageText(PageText());
			UpdateUi();
		}

		private void Next()
		{
			if (source == null || spread.Length == 0)
			{
				return;
			}
			int n = spread[spread.Length - 1] + 1;
			if (n < Count)
			{
				GoTo(n, turn: 1);
			}
			else
			{
				EndOfBook(1);
			}
		}

		private void Prev(bool atEnd = false)
		{
			if (source == null || spread.Length == 0)
			{
				return;
			}
			if (spread[0] > 0)
			{
				GoTo(PreviousStart(spread[0]), atEnd, -1);
			}
			else
			{
				EndOfBook(-1);
			}
		}

		#endregion

		#region Drawing

		private RenderResult Find(string key)
		{
			for (LinkedListNode<RenderResult> n = rendered.First; n != null; n = n.Next)
			{
				if (n.Value.Key == key)
				{
					rendered.Remove(n);
					rendered.AddFirst(n);
					return n.Value;
				}
			}
			return null;
		}

		private void Store(RenderResult result)
		{
			for (LinkedListNode<RenderResult> n = rendered.First; n != null; n = n.Next)
			{
				if (n.Value.Key == result.Key)
				{
					rendered.Remove(n);
					Release(n.Value);
					break;
				}
			}
			rendered.AddFirst(result);
			while (rendered.Count > KeepRendered)
			{
				RenderResult last = rendered.Last.Value;
				rendered.RemoveLast();
				Release(last);
			}
		}

		/// <summary>Frees a drawn spread unless it is on screen or kept.</summary>
		private void Release(RenderResult r)
		{
			if (r != null && r != view.Image && r != view.TransitionFrom && !rendered.Contains(r))
			{
				r.Bitmap?.Dispose();
			}
		}

		private void ClearRendered()
		{
			view.EndTransition();
			List<RenderResult> all = rendered.ToList();
			rendered.Clear();
			RenderResult shown = view.Image;
			view.SetImage(null);
			foreach (RenderResult r in all)
			{
				Release(r);
			}
			Release(shown);
		}

		private bool SizeRight(RenderResult r, ViewParams p)
		{
			return r.Bitmap != null && r.Bitmap.Size == p.DisplaySize(r.Natural);
		}

		/// <summary>Shows the current spread (drawn already, or asked for) and has the ones around it drawn.</summary>
		private void ShowCurrent()
		{
			if (source == null || spread.Length == 0 || view.ClientSize.Width < 2 || view.ClientSize.Height < 2)
			{
				return;
			}
			ViewParams p = Params();
			string key = Renderer.KeyOf(spread, p);
			RenderResult have = Find(key);
			if (have != null)
			{
				Display(have);
				if (!SizeRight(have, p))
				{
					renderer.Request(new RenderJob { Source = source, Pages = spread, View = p, Key = key, Current = true });
				}
			}
			else
			{
				renderer.Request(new RenderJob { Source = source, Pages = spread, View = p, Key = key, Current = true });
			}
			Prefetch(p);
			view.Invalidate();
		}

		private void Prefetch(ViewParams p)
		{
			try
			{
				int n = spread[spread.Length - 1] + 1;
				if (n < Count)
				{
					RequestIfMissing(SpreadAt(n), p);
				}
				if (spread[0] > 0)
				{
					RequestIfMissing(SpreadAt(PreviousStart(spread[0])), p);
				}
			}
			catch (Exception)
			{
				//a page that cannot be read shows its error when it is turned to
			}
		}

		private void RequestIfMissing(int[] pages, ViewParams p)
		{
			string key = Renderer.KeyOf(pages, p);
			RenderResult have = rendered.FirstOrDefault((RenderResult r) => r.Key == key);
			if (have == null || !SizeRight(have, p))
			{
				renderer.Request(new RenderJob { Source = source, Pages = pages, View = p.Clone(), Key = key, Current = false });
			}
		}

		private void Display(RenderResult result)
		{
			RenderResult old = view.Image;
			Rectangle oldRect = view.ImageRectangle;
			//a page turned while a turn still plays shows at once
			bool wasPlaying = view.Animating;
			view.EndTransition();
			view.CenterText = null;
			view.SetImage(result);
			if (scrollPending)
			{
				scrollPending = false;
				view.ResetScroll(scrollToEnd, settings.RightToLeft);
			}
			if (turnDirection != 0 && !wasPlaying && old != null && old != result)
			{
				view.PageCurlBend = PageView.BendOf(settings.CurlAmount);
				view.StartTransition(settings.PageEffect, old, oldRect, turnDirection > 0, settings.RightToLeft, settings.Layout != PageLayout.Single);
				if (view.Animating)
				{
					renderer.Hold(true);
				}
			}
			turnDirection = 0;
			if (old != result)
			{
				Release(old);
			}
		}

		private void OnRendered(RenderResult result)
		{
			if (IsDisposed || result.Source != source)
			{
				result.Bitmap?.Dispose();
				return;
			}
			bool current = spread.Length > 0 && result.Key == Renderer.KeyOf(spread, Params());
			if (result.Error != null)
			{
				if (current)
				{
					RenderResult old = view.Image;
					view.SetImage(null);
					Release(old);
					view.CenterIsError = true;
					view.CenterText = "Page " + (result.Pages[0] + 1) + " cannot be shown: " + result.Error;
					view.Invalidate();
				}
				return;
			}
			Store(result);
			if (current)
			{
				Display(result);
			}
		}

		private void ChangeView(Action change)
		{
			change();
			ShowCurrent();
			UpdateUi();
		}

		private void SetFit(FitMode fit)
		{
			ChangeView(delegate
			{
				settings.Fit = fit;
				zoom = 1f;
			});
		}

		private void ZoomIn()
		{
			ChangeView(delegate
			{
				zoom = Math.Min(8f, zoom * 1.25f);
			});
		}

		private void ZoomOut()
		{
			ChangeView(delegate
			{
				zoom = Math.Max(0.1f, zoom / 1.25f);
			});
		}

		private void Rotate(int degrees)
		{
			ChangeView(delegate
			{
				rotation = ((rotation + degrees) % 360 + 360) % 360;
			});
		}

		private void SetLayout(PageLayout layout)
		{
			settings.Layout = layout;
			if (source != null && spread.Length > 0)
			{
				GoTo(spread[0]);
			}
			UpdateUi();
		}

		private void CycleLayout()
		{
			SetLayout(settings.Layout == PageLayout.Single ? PageLayout.TwoPages : settings.Layout == PageLayout.TwoPages ? PageLayout.TwoPagesAdaptive : PageLayout.Single);
			view.ShowMessage(settings.Layout == PageLayout.Single ? "Single Page" : settings.Layout == PageLayout.TwoPages ? "Two Pages" : "Two Pages (Adaptive)", 1200);
		}

		private void OnWheel(object sender, MouseEventArgs e)
		{
			if ((ModifierKeys & Keys.Control) != 0)
			{
				if (e.Delta > 0)
				{
					ZoomIn();
				}
				else
				{
					ZoomOut();
				}
				return;
			}
			int step = Math.Max(40, view.ClientSize.Height / 5);
			int d = -e.Delta * step / 120;
			bool moved = (ModifierKeys & Keys.Shift) != 0 ? view.ScrollBy(d, 0) : view.ScrollBy(0, d);
			if (d != 0 && moved)
			{
				wheelRest = 0;
				return;
			}
			//at the page's end (or when it fits): a full wheel notch turns the page
			wheelRest += e.Delta;
			if (wheelRest <= -120)
			{
				wheelRest = 0;
				Next();
			}
			else if (wheelRest >= 120)
			{
				wheelRest = 0;
				Prev(atEnd: true);
			}
		}

		#endregion

		#region Commands

		private void ToggleFullScreen()
		{
			if (!fullScreen)
			{
				normalState = WindowState;
				normalBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
				fullScreen = true;
				topBar.Visible = false;
				FormBorderStyle = FormBorderStyle.None;
				WindowState = FormWindowState.Normal;
				Bounds = Screen.FromControl(this).Bounds;
			}
			else
			{
				fullScreen = false;
				FormBorderStyle = FormBorderStyle.Sizable;
				Bounds = normalBounds;
				WindowState = normalState;
			}
			UpdateUi();
		}

		private void ToggleToolbar()
		{
			settings.ToolbarVisible = !settings.ToolbarVisible;
			if (fullScreen && settings.ToolbarVisible)
			{
				//in full screen the bar comes back only while asked for
				topBar.Visible = true;
				return;
			}
			UpdateUi();
		}

		private void OpenDialog()
		{
			using (OpenFileDialog dlg = new OpenFileDialog
			{
				Title = "Open Book",
				Filter = "Comic books|*.cbz;*.cbr;*.cb7;*.cbt;*.zip;*.rar;*.7z;*.tar|Pictures|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp;*.tif;*.tiff;*.avif;*.heic;*.jxl|All files|*.*",
				InitialDirectory = bookPath != null ? ShellRegistration.FolderOf(bookPath) : settings.LastFolder
			})
			{
				if (dlg.ShowDialog(this) == DialogResult.OK)
				{
					OpenBook(dlg.FileName);
				}
			}
		}

		/// <summary>Opens the book in ComicRack too, at the page shown; Lite stays open on it.</summary>
		private void OpenInComicRack()
		{
			string exe = Path.Combine(Program.AppFolder, "ComicRack.exe");
			if (!File.Exists(exe))
			{
				Dialogs.ShowText(this, Program.Name, "ComicRack.exe is not in " + Program.AppFolder + ".\n\nComicRack Lite opens books in ComicRack when the two are in the same folder.", dark, 460, 170);
				return;
			}
			bool running = Process.GetProcessesByName("ComicRack").Length > 0;
			try
			{
				string args = bookPath == null ? "" : "\"" + bookPath.TrimEnd('\\') + "\"" + (spread.Length > 0 ? " -p " + (spread[0] + 1) : "");
				Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Program.AppFolder });
			}
			catch (Exception ex)
			{
				Dialogs.ShowText(this, Program.Name, "ComicRack could not be started: " + ex.Message, dark, 460, 170);
				return;
			}
			//Lite stays open on the book: ComicRack takes a few seconds to come up
			SavePosition();
			view.ShowMessage(running ? "Opened in ComicRack" : "Opening in ComicRack...", 3000);
		}

		private void ShowInExplorer()
		{
			if (bookPath == null)
			{
				return;
			}
			try
			{
				Process.Start("explorer.exe", "/select,\"" + bookPath.TrimEnd('\\') + "\"");
			}
			catch (Exception)
			{
			}
		}

		private void GoToDialog()
		{
			if (source == null || Count == 0)
			{
				return;
			}
			int page = Dialogs.AskPage(this, spread.Length > 0 ? spread[0] : 0, Count, dark);
			if (page >= 0)
			{
				GoTo(page);
			}
		}

		private void RegisterShell()
		{
			if (!ShellRegistration.Register())
			{
				Dialogs.ShowText(this, Program.Name, "ComicRack Lite could not be added to Explorer's \"Open with\" list.", dark, 460, 160);
				return;
			}
			bool canAsk = bookPath != null && File.Exists(bookPath) && PageSource.IsBookName(bookPath);
			Dialogs.ShowText(this, "Use ComicRack Lite for Comic Files",
				"ComicRack Lite is now in Explorer's \"Open with\" list for .cbz, .cbr, .cb7 and .cbt files.\n\n" +
				"Windows lets only you choose the program a double-click uses, once for each kind of file:\n\n" +
				"   Right-click a .cbz book > Open with > Choose another app > ComicRack Lite,\n   tick \"Always use this app to open .cbz files\", OK.\n" +
				"   Then the same for a .cbr book (and .cb7 / .cbt if you have them).\n\n" +
				(canAsk ? "That window opens for this book when you close this one.\n\n" : "") +
				"To go back, do the same and choose CDisplayEx or ComicRack instead.\n\n" +
				"In ComicRack Lite, Open in ComicRack (Ctrl+Enter, or the ComicRack button) opens the book in ComicRack at the same page; Lite stays open on it.",
				dark, 560, 330);
			if (canAsk)
			{
				ShellRegistration.ShowOpenWith(bookPath);
			}
		}

		private void ShowKeys()
		{
			Dialogs.ShowText(this, "Keyboard and Mouse",
				"Next page\tPgDn, Right, N, Space (scrolls first), mouse wheel\n" +
				"Previous page\tPgUp, Left, P, Backspace\n" +
				"Scroll\tUp, Down, Space, wheel (Shift+wheel: sideways), drag the page\n" +
				"First / last page\tHome / End\n" +
				"Previous / next book\tCtrl+PgUp / Ctrl+PgDn (or turn past the first or last page twice)\n" +
				"Go to page\tCtrl+G (or click the page number)\n" +
				"\n" +
				"Page layout\tT (single, two pages, two pages adaptive)\n" +
				"Fit\t1 all, 2 width, 3 height, 4 original size\n" +
				"Zoom\t+ / - (or Ctrl+wheel), 0 resets\n" +
				"Rotate\tR right, Shift+R left\n" +
				"Full screen\tF, F11, Alt+Enter or double-click; Esc leaves it\n" +
				"Menu and tool bar\tK or F10 (also in the right-click menu)\n" +
				"Mouse side buttons\tprevious / next page\n" +
				"\n" +
				"Open in ComicRack\tCtrl+Enter (the same book, at the same page)\n" +
				"Open a book\tCtrl+O, or drop it on the window\n" +
				"Close\tEsc (when not full screen), Ctrl+W, Ctrl+Q\n" +
				"\n" +
				"Right to left (manga) reading swaps Left and Right. With \"Click Page Sides to Turn Pages\" (Display menu) a click on the left or right part of the page turns it.\n" +
				"Each book opens where you left it.",
				dark, 640, 480);
		}

		private void ShowAbout()
		{
			Dialogs.ShowText(this, "About " + Program.Name,
				Program.NameAndVersion + " (comes with ComicRack orbitdesign " + Program.ComicRackBuild + ")\n\n" +
				"A small, fast reader for comic books opened from Explorer. It uses ComicRack's archive and picture libraries but none of its library, so a book is on screen at once. " +
				"Open in ComicRack opens the book in the full program at the same page; Lite stays open on it.\n\n" +
				"Reads cbz/zip, cbr/rar, cb7/7z, cbt/tar and folders of pictures (JPEG, PNG, GIF, BMP, TIFF, WebP, and AVIF, HEIC or JPEG XL when Windows has their codec).\n\n" +
				"Settings: " + Path.Combine(LiteSettings.Folder, "ComicRackLite.ini"),
				dark, 520, 270);
		}

		#endregion

		protected override void OnDragEnter(DragEventArgs e)
		{
			base.OnDragEnter(e);
			e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
		}

		protected override void OnDragDrop(DragEventArgs e)
		{
			base.OnDragDrop(e);
			if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
			{
				OpenBook(files[0]);
			}
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			base.OnFormClosing(e);
			if (fullScreen)
			{
				settings.Bounds = normalBounds;
				settings.Maximized = normalState == FormWindowState.Maximized;
			}
			else
			{
				settings.Bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
				settings.Maximized = WindowState == FormWindowState.Maximized;
			}
			SavePosition();
			settings.Save();
			renderer.Dispose();
			resizeTimer.Stop();
		}

		protected override void OnFormClosed(FormClosedEventArgs e)
		{
			base.OnFormClosed(e);
			source?.Dispose();
			source = null;
			ClearRendered();
		}
	}
}
