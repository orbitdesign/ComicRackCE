using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using cYo.Common.Windows.Forms;
using cYo.Common.Windows.Forms.Theme;

namespace cYo.Projects.ComicRack.Viewer.Dialogs
{
	/// <summary>
	/// The options window for page translation: which models read and translate the text, the
	/// languages, the lettering and the speed/cache limits. The settings themselves live in the
	/// ComicTranslator helper, so this window reads them from the helper and saves them back.
	/// Built in code (like PresetNameDialog) rather than as designer markup.
	/// </summary>
	public class TranslationSettingsDialog : FormEx
	{
		private const string AutoDetect = "Auto-detect";

		private const string UseVisionModel = "(same as the vision model)";

		private const string DefaultFontText = "(default)";

		private static readonly Dictionary<string, string> SettingNames = new Dictionary<string, string>
		{
			["ocr_engine"] = "Reading engine",
			["translator"] = "Translation engine",
			["ollama_url"] = "Ollama address",
			["vision_model"] = "Vision model",
			["translate_model"] = "Translation model",
			["source_language"] = "Source language",
			["target_code"] = "Translate into",
			["right_to_left"] = "Reading direction",
			["font"] = "Font",
			["uppercase"] = "Upper case",
			["match_style"] = "Match the original lettering",
			["batch_size"] = "Pages per request",
			["ollama_timeout"] = "Wait up to",
			["jpeg_quality"] = "Image quality",
			["cache_mb"] = "Maximum cache size",
			["cache_dir"] = "Cache folder",
			["start_minimized"] = "Start minimized",
			["minimize_to_tray"] = "Minimize to the tray",
			["inpainter"] = "Text over artwork",
			["vertical_chinese"] = "Vertical Chinese"
		};

		private readonly System.Windows.Forms.Timer startPoll = new System.Windows.Forms.Timer { Interval = 1500 };

		private readonly bool dark = ThemeManager.IsDarkModeEnabled;

		private readonly Color grayText;

		private readonly Color goodText;

		private readonly Color warnText;

		private readonly Color badText;

		private readonly List<Label> offlineNotes = new List<Label>();

		private TabControl tabs;

		private GroupBox helperGroup;

		private TextBox txtHelperUrl;

		private Button btConnect;

		private Button btStartHelper;

		private ComboBox cboOcr;

		private ComboBox cboVision;

		private Label lblOcrHint;

		private ComboBox cboTranslator;

		private ComboBox cboTranslateModel;

		private Label lblTranslatorHint;

		private Button btTest;

		private Label lblTestResult;

		private TextBox txtOllamaUrl;

		private Button btRefreshModels;

		private Label lblOllamaStatus;

		private ComboBox cboSource;

		private ComboBox cboTarget;

		private ComboBox cboDirection;
		private ComboBox cboInpaint;
		private Label lblInpaintHint;

		private ComboBox cboFont;

		private Button btBrowseFont;

		private CheckBox chkUpper;

		private CheckBox chkMatch;
		private CheckBox chkVertical;
		private CheckBox chkMinimized;
		private CheckBox chkTray;

		private NumericUpDown nudBatch;

		private NumericUpDown nudTimeout;

		private NumericUpDown nudQuality;

		//ComicRack's own setting (not the helper's): pages translated ahead while reading, and by a click on the translate-ahead button
		private NumericUpDown nudAhead;

		private NumericUpDown nudCacheMb;

		private Label lblCacheInfo;

		private Button btClearCache;

		private Label lblCacheGb;

		private TextBox txtCacheDir;

		private Button btBrowseCache;

		private Button btDefaultCache;

		private Button btCopyCache;

		private Button btStopCopy;

		private Label lblFree;

		private Label lblCopy;

		/// <summary>The folder in use in the helper now, the default one, and the folder the stored pages were last copied to here (compared, not shown).</summary>
		private string cacheActive;

		private string cacheDefault;

		private string copiedTo;

		private string copyTarget;

		private bool copyRunning;

		private bool copyPollBusy;

		private readonly System.Windows.Forms.Timer copyPoll = new System.Windows.Forms.Timer { Interval = 600 };
		private readonly System.Windows.Forms.Timer inpaintPoll = new System.Windows.Forms.Timer { Interval = 1500 };
		private bool inpaintPollBusy;

		private readonly System.Windows.Forms.Timer freeCheck = new System.Windows.Forms.Timer { Interval = 600 };

		private int freeToken;

		private long freeBytes = -1;

		private long totalBytes = -1;

		private Label lblStatus;

		private Label lblProblems;

		private Button btBrowser;

		private Button btOk;

		private Button btCancel;

		private Button btApply;

		private HelperState state;

		private string connectedUrl;

		private bool loading;

		private bool dirty;

		private bool busy;

		private bool pollInFlight;

		private DateTime pollUntil;

		public override UIComponent UIComponent => UIComponent.Content;

		/// <summary>The helper this window is connected to (empty until the first successful connection).</summary>
		public string ServerUrl => connectedUrl ?? string.Empty;

		/// <summary>Folder holding the helper's run.bat, remembered so "Start helper" works next time.</summary>
		public string HelperFolder { get; set; }

		/// <summary>Raised after settings were saved (or the cache cleared), so open pages can be translated again.</summary>
		public event EventHandler SettingsApplied;

		public TranslationSettingsDialog(string serverUrl, string helperFolder)
		{
			HelperFolder = helperFolder ?? string.Empty;
			grayText = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);
			goodText = dark ? Color.FromArgb(120, 210, 120) : Color.FromArgb(0, 120, 0);
			warnText = dark ? Color.FromArgb(240, 170, 80) : Color.FromArgb(190, 90, 0);
			badText = dark ? Color.FromArgb(255, 115, 115) : Color.FromArgb(180, 0, 0);

			Text = "Translation Settings";
			AutoScaleDimensions = new SizeF(6f, 13f);
			AutoScaleMode = AutoScaleMode.Font;
			FormBorderStyle = FormBorderStyle.FixedDialog;
			StartPosition = FormStartPosition.CenterParent;
			MaximizeBox = false;
			MinimizeBox = false;
			ShowInTaskbar = false;
			ClientSize = new Size(600, 662);

			BuildControls(string.IsNullOrWhiteSpace(serverUrl) ? "http://127.0.0.1:8790" : serverUrl.Trim());
			SetConnected(false);
			startPoll.Tick += startPoll_Tick;
			copyPoll.Tick += copyPoll_Tick;
			inpaintPoll.Tick += inpaintPoll_Tick;
			freeCheck.Tick += freeCheck_Tick;
			Shown += delegate
			{
				Connect(txtHelperUrl.Text);
			};
			FormClosed += delegate
			{
				startPoll.Stop();
				startPoll.Dispose();
				copyPoll.Stop();
				copyPoll.Dispose();
				inpaintPoll.Stop();
				inpaintPoll.Dispose();
				freeCheck.Stop();
				freeCheck.Dispose();
			};
		}

		// ---- layout --------------------------------------------------------------------

		private void BuildControls(string url)
		{
			tabs = new TabControl { Location = new Point(10, 10), Size = new Size(580, 536) };
			TabPage models = new TabPage("Models");
			TabPage languages = new TabPage("Language and lettering");
			TabPage storage = new TabPage("Speed and storage");
			tabs.TabPages.AddRange(new[] { models, languages, storage });
			foreach (TabPage page in tabs.TabPages)
			{
				Label note = new Label
				{
					Text = "These settings appear here once the helper is running.",
					Location = new Point(14, page == models ? 78 : 22),
					Size = new Size(540, 20),
					ForeColor = grayText
				};
				page.Controls.Add(note);
				offlineNotes.Add(note);
			}

			// --- Models ---
			GroupBox helper = helperGroup = Group(models, "ComicTranslator helper", 8, 8, 556, 56);
			Place(helper, "Address:", 14, 25);
			txtHelperUrl = new TextBox { Location = new Point(70, 22), Size = new Size(290, 20), Text = url, Name = "txtHelperUrl" };
			btConnect = new Button { Text = "Connect", Location = new Point(368, 20), Size = new Size(84, 24), Name = "btConnect" };
			btStartHelper = new Button { Text = "Start helper", Location = new Point(458, 20), Size = new Size(88, 24), Name = "btStartHelper" };
			helper.Controls.AddRange(new Control[] { txtHelperUrl, btConnect, btStartHelper });
			btConnect.Click += delegate { Connect(txtHelperUrl.Text); };
			btStartHelper.Click += delegate { StartHelper(); };
			txtHelperUrl.KeyDown += delegate (object s, KeyEventArgs e)
			{
				if (e.KeyCode == Keys.Enter)
				{
					e.SuppressKeyPress = true;
					Connect(txtHelperUrl.Text);
				}
			};

			GroupBox reading = Group(models, "Reading the text (OCR)", 8, 70, 556, 112);
			Place(reading, "Engine:", 14, 27);
			cboOcr = DropDown(reading, 100, 24, 440, "cboOcr");
			Place(reading, "Vision model:", 14, 55);
			cboVision = new ComboBoxEx { Location = new Point(100, 52), Size = new Size(440, 21), DropDownStyle = ComboBoxStyle.DropDown, Name = "cboVision" };
			reading.Controls.Add(cboVision);
			lblOcrHint = Hint(reading, 100, 78, 440, 30);
			lblOcrHint.Name = "lblOcrHint";
			cboOcr.SelectedIndexChanged += delegate { Edited(); UpdateHints(); };
			cboVision.TextChanged += delegate { Edited(); };

			GroupBox translating = Group(models, "Translating", 8, 188, 556, 156);
			Place(translating, "Engine:", 14, 27);
			cboTranslator = DropDown(translating, 100, 24, 440, "cboTranslator");
			Place(translating, "Translation model:", 14, 55);
			cboTranslateModel = new ComboBoxEx { Location = new Point(122, 52), Size = new Size(418, 21), DropDownStyle = ComboBoxStyle.DropDown, Name = "cboTranslateModel" };
			translating.Controls.Add(cboTranslateModel);
			lblTranslatorHint = Hint(translating, 122, 78, 418, 30);
			lblTranslatorHint.Name = "lblTranslatorHint";
			btTest = new Button { Text = "Test translator", Location = new Point(14, 112), Size = new Size(100, 26), Name = "btTest" };
			lblTestResult = new Label { Location = new Point(122, 108), Size = new Size(418, 40), Name = "lblTestResult", AutoEllipsis = true };
			translating.Controls.AddRange(new Control[] { btTest, lblTestResult });
			cboTranslator.SelectedIndexChanged += delegate { Edited(); UpdateHints(); };
			cboTranslateModel.TextChanged += delegate { Edited(); };
			btTest.Click += delegate { TestTranslator(); };

			GroupBox ollama = Group(models, "Ollama (local models)", 8, 350, 556, 76);
			Place(ollama, "Address:", 14, 25);
			txtOllamaUrl = new TextBox { Location = new Point(70, 22), Size = new Size(290, 20), Name = "txtOllamaUrl" };
			btRefreshModels = new Button { Text = "Refresh models", Location = new Point(368, 20), Size = new Size(120, 24), Name = "btRefreshModels" };
			lblOllamaStatus = new Label { Location = new Point(14, 48), Size = new Size(530, 24), Name = "lblOllamaStatus", AutoEllipsis = true };
			ollama.Controls.AddRange(new Control[] { txtOllamaUrl, btRefreshModels, lblOllamaStatus });
			txtOllamaUrl.TextChanged += delegate { Edited(); };
			btRefreshModels.Click += delegate { RefreshModels(); };

			GroupBox window = Group(models, "Helper window", 8, 432, 556, 72);
			chkMinimized = new CheckBox { Text = "Start minimized (takes effect the next time the helper starts)", Location = new Point(14, 20), AutoSize = true, Name = "chkMinimized" };
			chkTray = new CheckBox { Text = "Minimize to the system tray (next to the clock) instead of the taskbar", Location = new Point(14, 44), AutoSize = true, Name = "chkTray" };
			window.Controls.AddRange(new Control[] { chkMinimized, chkTray });
			chkMinimized.CheckedChanged += delegate { Edited(); };
			chkTray.CheckedChanged += delegate { Edited(); };

			// --- Language and lettering ---
			GroupBox lang = Group(languages, "Languages", 8, 8, 556, 124);
			Place(lang, "Source language:", 14, 27);
			cboSource = new ComboBoxEx { Location = new Point(130, 24), Size = new Size(410, 21), DropDownStyle = ComboBoxStyle.DropDown, Name = "cboSource" };
			lang.Controls.Add(cboSource);
			Place(lang, "Translate into:", 14, 57);
			cboTarget = DropDown(lang, 130, 54, 410, "cboTarget");
			Place(lang, "Reading direction:", 14, 87);
			cboDirection = DropDown(lang, 130, 84, 410, "cboDirection");
			cboSource.TextChanged += delegate { Edited(); };
			cboTarget.SelectedIndexChanged += delegate { Edited(); };
			cboDirection.SelectedIndexChanged += delegate { Edited(); };

			GroupBox lettering = Group(languages, "Lettering", 8, 138, 556, 184);
			Place(lettering, "Font:", 14, 27);
			cboFont = new ComboBoxEx { Location = new Point(70, 24), Size = new Size(402, 21), DropDownStyle = ComboBoxStyle.DropDown, Name = "cboFont" };
			btBrowseFont = new Button { Text = "Browse...", Location = new Point(478, 22), Size = new Size(68, 24), Name = "btBrowseFont" };
			chkUpper = new CheckBox { Text = "Upper case, like comic lettering", Location = new Point(70, 54), AutoSize = true, Name = "chkUpper" };
			chkMatch = new CheckBox { Text = "Match the original lettering (size, weight, style, colour)", Location = new Point(70, 78), AutoSize = true, Name = "chkMatch" };
			chkVertical = new CheckBox { Text = "Chinese: set the text vertically (top to bottom, columns from the right)", Location = new Point(70, 156), AutoSize = true, Name = "chkVertical" };
			lettering.Controls.AddRange(new Control[] { cboFont, btBrowseFont, chkUpper, chkMatch, chkVertical });
			chkVertical.CheckedChanged += delegate { Edited(); };
			Hint(lettering, 70, 104, 470, 48).Text = "With matching on, each text keeps the size and look of the original and the closest installed font is chosen (leave Font empty; fonts you put in the helper's fonts folder are tried too). Signs and titles are set along their original lines.";
			cboFont.TextChanged += delegate { Edited(); };
			chkUpper.CheckedChanged += delegate { Edited(); };
			chkMatch.CheckedChanged += delegate { Edited(); };
			btBrowseFont.Click += delegate { BrowseFont(); };

			GroupBox removing = Group(languages, "Removing the original text", 8, 328, 556, 104);
			Place(removing, "Text over artwork:", 14, 27);
			cboInpaint = DropDown(removing, 130, 24, 410, "cboInpaint");
			lblInpaintHint = Hint(removing, 130, 52, 410, 44);
			lblInpaintHint.Name = "lblInpaintHint";
			cboInpaint.SelectedIndexChanged += delegate { Edited(); UpdateInpaintHint(); };

			// --- Speed and storage ---
			GroupBox speed = Group(storage, "Speed", 8, 8, 556, 162);
			Place(speed, "Text blocks per request:", 14, 29);
			nudBatch = Number(speed, 170, 26, 1, 32, "nudBatch");
			Hint(speed, 250, 29, 296, 28).Text = "Higher is faster but a small model may lose count.";
			Place(speed, "Wait for Ollama up to:", 14, 61);
			nudTimeout = Number(speed, 170, 58, 10, 1800, "nudTimeout");
			Place(speed, "seconds", 250, 61);
			Place(speed, "Translated image quality:", 14, 93);
			nudQuality = Number(speed, 170, 90, 50, 100, "nudQuality");
			Place(speed, "%", 250, 93);
			Place(speed, "Translate ahead:", 14, 125);
			nudAhead = Number(speed, 170, 122, 1, 100, "nudAhead");
			nudAhead.Value = Math.Max(nudAhead.Minimum, Math.Min(nudAhead.Maximum, Program.Settings.TranslatorAheadPages));
			Place(speed, "pages", 250, 125);
			Hint(speed, 300, 125, 246, 30).Text = "Kept ready after the page being read; a click on the ahead button does this many.";

			GroupBox cache = Group(storage, "Translated page cache", 8, 176, 556, 280);
			Place(cache, "Keep up to:", 14, 29);
			nudCacheMb = Number(cache, 170, 26, 50, 2000000, "nudCacheMb");
			Place(cache, "MB", 250, 29);
			lblCacheGb = Hint(cache, 290, 29, 250, 18);
			lblCacheGb.Name = "lblCacheGb";
			lblCacheInfo = new Label { Location = new Point(14, 60), Size = new Size(530, 18), Name = "lblCacheInfo" };
			btClearCache = new Button { Text = "Clear cache", Location = new Point(14, 80), Size = new Size(100, 24), Name = "btClearCache" };
			cache.Controls.AddRange(new Control[] { lblCacheInfo, btClearCache });
			Hint(cache, 124, 83, 420, 26).Text = "Translated pages are remembered so a book opens instantly the second time.";
			btClearCache.Click += delegate { ClearCache(); };

			// where the pages are kept
			Place(cache, "Cache folder:", 14, 125);
			txtCacheDir = new TextBox { Location = new Point(100, 122), Size = new Size(314, 21), Name = "txtCacheDir" };
			btBrowseCache = new Button { Text = "Browse...", Location = new Point(420, 120), Size = new Size(70, 24), Name = "btBrowseCache" };
			btDefaultCache = new Button { Text = "Default", Location = new Point(496, 120), Size = new Size(48, 24), Name = "btDefaultCache" };
			cache.Controls.AddRange(new Control[] { txtCacheDir, btBrowseCache, btDefaultCache });
			lblFree = Hint(cache, 100, 148, 444, 18);
			lblFree.Name = "lblFree";
			btCopyCache = new Button { Text = "Copy the stored pages to this folder", Location = new Point(14, 174), Size = new Size(236, 24), Name = "btCopyCache" };
			btStopCopy = new Button { Text = "Stop", Location = new Point(256, 174), Size = new Size(60, 24), Name = "btStopCopy", Visible = false };
			cache.Controls.AddRange(new Control[] { btCopyCache, btStopCopy });
			lblCopy = new Label { Location = new Point(14, 204), Size = new Size(530, 34), Name = "lblCopy" };
			cache.Controls.Add(lblCopy);
			Hint(cache, 14, 240, 530, 34).Text = "Pages are kept in this folder (another drive or a network folder will do). A new folder starts empty: copy first to take the stored pages along, then press OK or Apply to use it. The old folder is left as it is.";
			txtCacheDir.TextChanged += delegate { CacheFolderEdited(); };
			btBrowseCache.Click += delegate { BrowseCacheFolder(); };
			btDefaultCache.Click += delegate { txtCacheDir.Text = cacheDefault ?? string.Empty; };
			btCopyCache.Click += delegate { StartCopy(); };
			btStopCopy.Click += delegate { StopCopy(); };
			nudCacheMb.ValueChanged += delegate
			{
				UpdateCacheGb();
				ShowFree();
			};

			// --- bottom area ---
			lblStatus = new Label { Location = new Point(12, 552), Size = new Size(576, 18), Name = "lblStatus", AutoEllipsis = true };
			lblProblems = new Label { Location = new Point(12, 572), Size = new Size(576, 46), Name = "lblProblems" };
			btBrowser = new Button { Text = "Open in browser", Location = new Point(10, 627), Size = new Size(110, 25), Name = "btBrowser" };
			btOk = new Button { Text = "OK", Location = new Point(344, 627), Size = new Size(75, 25), Name = "btOk" };
			btCancel = new Button { Text = "Cancel", Location = new Point(425, 627), Size = new Size(75, 25), DialogResult = DialogResult.Cancel, Name = "btCancel" };
			btApply = new Button { Text = "Apply", Location = new Point(506, 627), Size = new Size(75, 25), Name = "btApply" };
			btBrowser.Click += delegate { OpenInBrowser(); };
			btOk.Click += delegate { Apply(delegate { DialogResult = DialogResult.OK; }); };
			btApply.Click += delegate { Apply(null); };
			Controls.AddRange(new Control[] { tabs, lblStatus, lblProblems, btBrowser, btOk, btCancel, btApply });
			AcceptButton = btOk;
			CancelButton = btCancel;

			foreach (NumericUpDown n in new[] { nudBatch, nudTimeout, nudQuality, nudCacheMb, nudAhead })
			{
				n.ValueChanged += delegate { Edited(); };
			}
		}

		private static GroupBox Group(TabPage page, string title, int x, int y, int w, int h)
		{
			GroupBox box = new GroupBox { Text = title, Location = new Point(x, y), Size = new Size(w, h) };
			page.Controls.Add(box);
			return box;
		}

		private static Label Place(Control parent, string text, int x, int y)
		{
			Label label = new Label { Text = text, Location = new Point(x, y), AutoSize = true };
			parent.Controls.Add(label);
			return label;
		}

		private Label Hint(Control parent, int x, int y, int w, int h)
		{
			Label label = new Label { Location = new Point(x, y), Size = new Size(w, h), ForeColor = grayText };
			parent.Controls.Add(label);
			return label;
		}

		private static ComboBox DropDown(Control parent, int x, int y, int w, string name)
		{
			ComboBox box = new ComboBox { Location = new Point(x, y), Size = new Size(w, 21), DropDownStyle = ComboBoxStyle.DropDownList, Name = name };
			parent.Controls.Add(box);
			return box;
		}

		private static NumericUpDown Number(Control parent, int x, int y, int min, int max, string name)
		{
			NumericUpDown n = new NumericUpDown { Location = new Point(x, y), Size = new Size(72, 20), Minimum = min, Maximum = max, Name = name };
			parent.Controls.Add(n);
			return n;
		}

		// ---- state -> window -------------------------------------------------------------

		private void Edited()
		{
			if (loading)
			{
				return;
			}
			dirty = true;
			UpdateButtons();
		}

		private void SetConnected(bool connected)
		{
			// The settings only appear once the helper answers; until then just the box used to reach it.
			// (Hidden rather than greyed out: ComicRack's dark theme draws disabled labels twice.)
			foreach (TabPage page in tabs.TabPages)
			{
				foreach (Control c in page.Controls)
				{
					Label note = c as Label;
					c.Visible = note != null && offlineNotes.Contains(note) ? !connected : (connected || c == helperGroup);
				}
			}
			if (connected)
			{
				UpdateHints();
			}
			btBrowser.Enabled = connected;
			btStartHelper.Visible = !connected;
			UpdateButtons();
		}

		private void UpdateButtons()
		{
			bool connected = state != null;
			btOk.Enabled = !busy;
			btApply.Enabled = connected && dirty && !busy;
			btCancel.Enabled = true;
		}

		private static string ChoiceValue(ComboBox box)
		{
			HelperChoice choice = box.SelectedItem as HelperChoice;
			return choice == null ? string.Empty : choice.Value;
		}

		private static void Fill(ComboBox box, IEnumerable<HelperChoice> choices, string value)
		{
			box.Items.Clear();
			foreach (HelperChoice c in choices)
			{
				box.Items.Add(c);
			}
			box.SelectedIndex = -1;
			foreach (HelperChoice c in choices)
			{
				if (c.Value == value)
				{
					box.SelectedItem = c;
				}
			}
			if (box.SelectedIndex < 0 && box.Items.Count > 0)
			{
				box.SelectedIndex = 0;
			}
		}

		private void ShowState(HelperState s)
		{
			loading = true;
			state = s;
			List<HelperChoice> none = new List<HelperChoice>();
			Fill(cboOcr, s.Choices.ContainsKey("ocr_engine") ? s.Choices["ocr_engine"] : none, s.GetString("ocr_engine"));
			Fill(cboTranslator, s.Choices.ContainsKey("translator") ? s.Choices["translator"] : none, s.GetString("translator"));
			Fill(cboDirection, s.Choices.ContainsKey("right_to_left") ? s.Choices["right_to_left"] : none, s.GetString("right_to_left"));
			Fill(cboInpaint, s.Choices.ContainsKey("inpainter") ? s.Choices["inpainter"] : none, s.GetString("inpainter"));
			cboInpaint.Enabled = s.Choices.ContainsKey("inpainter");          //an older helper has only the classic fill
			List<HelperChoice> languages = s.Choices.ContainsKey("languages") ? s.Choices["languages"] : none;
			Fill(cboTarget, languages, s.GetString("target_code"));

			cboSource.Items.Clear();
			cboSource.Items.Add(AutoDetect);
			foreach (HelperChoice l in languages)
			{
				cboSource.Items.Add(l.Label);
			}
			string source = s.GetString("source_language");
			cboSource.Text = string.IsNullOrEmpty(source) || source.Equals("auto", StringComparison.OrdinalIgnoreCase) ? AutoDetect : source;

			cboFont.Items.Clear();
			cboFont.Items.Add(DefaultFontText);
			List<HelperChoice> fonts = s.Choices.ContainsKey("fonts") ? s.Choices["fonts"] : none;
			foreach (HelperChoice f in fonts)
			{
				cboFont.Items.Add(f.Label);
			}
			string font = s.GetString("font");
			HelperChoice known = fonts.FirstOrDefault(f => f.Value == font);
			cboFont.Text = string.IsNullOrEmpty(font) ? DefaultFontText : (known != null ? known.Label : font);

			chkUpper.Checked = s.GetBool("uppercase");
			chkMatch.Checked = !s.Config.ContainsKey("match_style") || s.GetBool("match_style");
			chkVertical.Enabled = s.Config.ContainsKey("vertical_chinese");        //helpers before 1.62 only set Chinese in lines
			chkVertical.Checked = !s.Config.ContainsKey("vertical_chinese") || s.GetBool("vertical_chinese");
			chkMinimized.Enabled = s.Config.ContainsKey("start_minimized");      //an older helper does not know the setting
			chkMinimized.Checked = !s.Config.ContainsKey("start_minimized") || s.GetBool("start_minimized");
			chkTray.Enabled = s.Config.ContainsKey("minimize_to_tray");          //helpers before 1.58 have no tray icon
			chkTray.Checked = !s.Config.ContainsKey("minimize_to_tray") || s.GetBool("minimize_to_tray");
			SetNumber(nudBatch, s.GetInt("batch_size", 8));
			SetNumber(nudTimeout, s.GetInt("ollama_timeout", 180));
			SetNumber(nudQuality, s.GetInt("jpeg_quality", 92));
			SetNumber(nudCacheMb, s.GetInt("cache_mb", 2000));
			cacheActive = s.CacheFolder;
			cacheDefault = s.CacheDefaultFolder;
			copiedTo = null;
			string chosenFolder = s.GetString("cache_dir");
			txtCacheDir.Text = string.IsNullOrEmpty(chosenFolder) ? cacheDefault : chosenFolder;
			if (!copyRunning)
			{
				lblCopy.Text = string.Empty;
				if (!HelperKeepsCacheFolder)
				{
					lblCopy.ForeColor = warnText;
					lblCopy.Text = "Choosing the cache folder needs ComicTranslator 1.53 or newer. Restart the helper to get the new version.";
				}
			}
			UpdateCacheGb();
			UpdateCopyButtons();
			freeCheck.Stop();
			freeCheck.Start();
			txtOllamaUrl.Text = s.GetString("ollama_url");

			ApplyModelLists(s.OllamaReachable, s.OllamaModels, s.GetString("vision_model"), s.GetString("translate_model"));
			ShowCache(s.CachePages, s.CacheMegabytes);
			loading = false;
			dirty = false;

			UpdateHints();
			UpdateInpaintHint();
			if (s.InpaintDownloading)
			{
				inpaintPoll.Start();
			}
			ShowProblems(s);
			lblStatus.ForeColor = goodText;
			lblStatus.Text = "Connected to ComicTranslator " + s.Version + " (text detector: " + s.Detector + ")";
			SetConnected(true);
		}

		private static void SetNumber(NumericUpDown n, int value)
		{
			n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, value));
		}

		private void ApplyModelLists(bool reachable, List<OllamaModel> models, string vision, string translate)
		{
			bool wasLoading = loading;
			loading = true;
			cboVision.Items.Clear();
			cboTranslateModel.Items.Clear();
			cboTranslateModel.Items.Add(UseVisionModel);
			foreach (OllamaModel m in models)
			{
				cboVision.Items.Add(m.Name);
				cboTranslateModel.Items.Add(m.Name);
			}
			cboVision.Text = vision;
			cboTranslateModel.Text = string.IsNullOrEmpty(translate) ? UseVisionModel : translate;
			if (!reachable)
			{
				lblOllamaStatus.ForeColor = warnText;
				lblOllamaStatus.Text = "Ollama is not reachable at this address. You can still type a model name.";
			}
			else if (models.Count == 0)
			{
				lblOllamaStatus.ForeColor = warnText;
				lblOllamaStatus.Text = "Ollama is running but has no models yet (for example: ollama pull qwen2.5vl:7b).";
			}
			else
			{
				lblOllamaStatus.ForeColor = goodText;
				lblOllamaStatus.Text = "Ollama is running with " + models.Count + " model" + (models.Count == 1 ? "" : "s") + ": "
					+ string.Join(", ", models.Select(m => m.Name + (m.SizeGb > 0 ? " (" + m.SizeGb.ToString("0.#") + " GB)" : "")));
			}
			loading = wasLoading;
		}

		private void ShowCache(int pages, double megabytes)
		{
			lblCacheInfo.ForeColor = grayText;
			lblCacheInfo.Text = pages + " translated page" + (pages == 1 ? "" : "s") + " stored, using " + megabytes.ToString("0.#") + " MB";
			btClearCache.Enabled = pages > 0 || megabytes > 0;
		}

		/// <summary>What the Text over artwork choice means, and how the AI model is getting on (not downloaded yet, downloading, ready, or why it cannot be used).</summary>
		private void UpdateInpaintHint()
		{
			HelperChoice choice = cboInpaint.SelectedItem as HelperChoice;
			if (!cboInpaint.Enabled || choice == null)
			{
				lblInpaintHint.ForeColor = grayText;
				lblInpaintHint.Text = state != null && !cboInpaint.Enabled ? "This helper is older than 1.59 and only has the classic fill. Update the helper to get the AI model." : string.Empty;
				return;
			}
			string text = choice.Hint;
			lblInpaintHint.ForeColor = grayText;
			if (choice.Value == "lama" && state != null && state.InpaintKnown)
			{
				if (!string.IsNullOrEmpty(state.InpaintError))
				{
					lblInpaintHint.ForeColor = badText;
					text = "The model cannot be used: " + state.InpaintError + " The classic fill is used.";
				}
				else if (state.InpaintDownloading)
				{
					lblInpaintHint.ForeColor = warnText;
					text = "Downloading the model: " + state.InpaintPercent + "%. The classic fill is used until it is ready.";
				}
				else if (!state.InpaintReady)
				{
					text += " It is downloaded when you press Apply or OK.";
				}
				else
				{
					text = "The model is ready" + (string.IsNullOrEmpty(state.InpaintProvider) ? "." : " (running on " + state.InpaintProvider + ").") + " Where the paper around the text is plain, the exact flat fill is still used.";
				}
			}
			lblInpaintHint.Text = text;
		}

		private void inpaintPoll_Tick(object sender, EventArgs e)
		{
			if (inpaintPollBusy || connectedUrl == null)
			{
				return;
			}
			inpaintPollBusy = true;
			HelperState latest = null;
			RunInBackground(delegate { latest = new TranslatorHelperClient(connectedUrl).GetState(); }, delegate (Exception error)
			{
				inpaintPollBusy = false;
				if (latest == null || state == null)
				{
					return;
				}
				state.InpaintKnown = latest.InpaintKnown;
				state.InpaintReady = latest.InpaintReady;
				state.InpaintDownloading = latest.InpaintDownloading;
				state.InpaintPercent = latest.InpaintPercent;
				state.InpaintError = latest.InpaintError;
				state.InpaintProvider = latest.InpaintProvider;
				UpdateInpaintHint();
				if (!latest.InpaintDownloading)
				{
					inpaintPoll.Stop();
				}
			});
		}

		private void UpdateHints()
		{
			HelperChoice ocr = cboOcr.SelectedItem as HelperChoice;
			lblOcrHint.Text = ocr == null ? string.Empty : ocr.Hint;
			bool ollamaOcr = ocr != null && ocr.Value == "ollama";
			cboVision.Enabled = ollamaOcr || ChoiceValue(cboTranslator) == "ollama";
			HelperChoice tr = cboTranslator.SelectedItem as HelperChoice;
			lblTranslatorHint.Text = tr == null ? string.Empty : tr.Hint;
			cboTranslateModel.Enabled = tr != null && tr.Value == "ollama";
		}

		private void ShowProblems(HelperState s)
		{
			StringBuilder text = new StringBuilder();
			foreach (KeyValuePair<string, string> p in s.Problems)
			{
				text.AppendLine("• " + p.Value);
			}
			if (!string.IsNullOrEmpty(s.LastError))
			{
				text.AppendLine("• The last page failed: " + s.LastError);
			}
			lblProblems.ForeColor = warnText;
			lblProblems.Text = text.ToString().TrimEnd();
		}

		// ---- window -> state -------------------------------------------------------------

		private Dictionary<string, object> CollectSettings()
		{
			string translateModel = cboTranslateModel.Text.Trim();
			string source = cboSource.Text.Trim();
			string font = cboFont.Text.Trim();
			HelperChoice knownFont = state == null || !state.Choices.ContainsKey("fonts") ? null : state.Choices["fonts"].FirstOrDefault(f => f.Label == font);
			Dictionary<string, object> collected = new Dictionary<string, object>
			{
				["ocr_engine"] = ChoiceValue(cboOcr),
				["translator"] = ChoiceValue(cboTranslator),
				["ollama_url"] = txtOllamaUrl.Text.Trim(),
				["vision_model"] = cboVision.Text.Trim(),
				["translate_model"] = translateModel == UseVisionModel ? string.Empty : translateModel,
				["source_language"] = source == AutoDetect ? "auto" : source,
				["target_code"] = ChoiceValue(cboTarget),
				["right_to_left"] = ChoiceValue(cboDirection),
				["font"] = font == DefaultFontText ? string.Empty : (knownFont != null ? knownFont.Value : font),
				["uppercase"] = chkUpper.Checked,
				["match_style"] = chkMatch.Checked,
				["batch_size"] = (int)nudBatch.Value,
				["ollama_timeout"] = (int)nudTimeout.Value,
				["jpeg_quality"] = (int)nudQuality.Value,
				["cache_mb"] = (int)nudCacheMb.Value
			};
			if (HelperKeepsCacheFolder)
			{
				collected["cache_dir"] = CacheDirSetting();      //an older helper does not know the setting and would turn all of them down
			}
			if (chkMinimized.Enabled)
			{
				collected["start_minimized"] = chkMinimized.Checked;
			}
			if (chkVertical.Enabled)
			{
				collected["vertical_chinese"] = chkVertical.Checked;
			}
			if (chkTray.Enabled)
			{
				collected["minimize_to_tray"] = chkTray.Checked;
			}
			if (cboInpaint.Enabled && ChoiceValue(cboInpaint).Length > 0)
			{
				collected["inpainter"] = ChoiceValue(cboInpaint);
			}
			return collected;
		}

		// ---- actions (each talks to the helper on a worker thread) ---------------------------

		private void RunInBackground(Action work, Action<Exception> done)
		{
			ThreadPool.QueueUserWorkItem(delegate
			{
				Exception error = null;
				try
				{
					work();
				}
				catch (Exception ex)
				{
					error = ex;
				}
				OnUi(delegate { done(error); });
			});
		}

		private void OnUi(Action action)
		{
			if (IsDisposed || !IsHandleCreated)
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

		private static string Describe(Exception ex)
		{
			WebException web = ex as WebException;
			if (web != null && web.Status == System.Net.WebExceptionStatus.ConnectFailure)
			{
				return "nothing is listening there";
			}
			if (web != null && web.Status == System.Net.WebExceptionStatus.Timeout)
			{
				return "it did not answer in time";
			}
			return ex.Message;
		}

		private void Connect(string url)
		{
			url = (url ?? string.Empty).Trim().TrimEnd('/');
			if (url.Length == 0)
			{
				return;
			}
			lblStatus.ForeColor = grayText;
			lblStatus.Text = "Connecting to " + url + " ...";
			HelperState result = null;
			RunInBackground(delegate { result = new TranslatorHelperClient(url).GetState(); }, delegate (Exception error)
			{
				if (result != null)
				{
					connectedUrl = url;
					txtHelperUrl.Text = url;
					startPoll.Stop();
					ShowState(result);
					return;
				}
				state = null;
				lblStatus.ForeColor = badText;
				lblStatus.Text = "The helper is not running at " + url + " (" + Describe(error) + ").";
				lblProblems.ForeColor = grayText;
				lblProblems.Text = "Start ComicTranslator with its run.bat, or press \"Start helper\". This window fills in once it is reachable.";
				SetConnected(false);
			});
		}

		private void StartHelper()
		{
			string folder = HelperFolder;
			if ((string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, "run.bat"))) && File.Exists(Path.Combine(TranslationServices.InstalledHelperFolder, "run.bat")))
			{
				folder = HelperFolder = TranslationServices.InstalledHelperFolder;      //installed by the setup program
			}
			if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, "run.bat")))
			{
				using (FolderBrowserDialog dlg = new FolderBrowserDialog())
				{
					dlg.Description = "Choose the ComicTranslator folder (the one that contains run.bat).";
					if (dlg.ShowDialog(this) != DialogResult.OK)
					{
						return;
					}
					folder = dlg.SelectedPath;
				}
				if (!File.Exists(Path.Combine(folder, "run.bat")))
				{
					MessageBox.Show(this, "There is no run.bat in that folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
					return;
				}
				HelperFolder = folder;
			}
			try
			{
				Process.Start(new ProcessStartInfo(Path.Combine(folder, "run.bat")) { WorkingDirectory = folder, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "The helper could not be started: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}
			lblStatus.ForeColor = grayText;
			lblStatus.Text = "Starting the helper... (the first start can take a minute)";
			pollUntil = DateTime.UtcNow.AddSeconds(150);
			startPoll.Start();
		}

		private void startPoll_Tick(object sender, EventArgs e)
		{
			if (DateTime.UtcNow > pollUntil)
			{
				startPoll.Stop();
				lblStatus.ForeColor = badText;
				lblStatus.Text = "The helper did not start in time. Look at its console window for the reason.";
				return;
			}
			if (pollInFlight)
			{
				return;
			}
			pollInFlight = true;
			string url = txtHelperUrl.Text.Trim().TrimEnd('/');
			HelperState result = null;
			RunInBackground(delegate { result = new TranslatorHelperClient(url).GetState(); }, delegate
			{
				pollInFlight = false;
				if (result != null && startPoll.Enabled)
				{
					startPoll.Stop();
					connectedUrl = url;
					ShowState(result);
				}
			});
		}

		private void RefreshModels()
		{
			string url = txtOllamaUrl.Text.Trim();
			string vision = cboVision.Text;
			string translate = cboTranslateModel.Text;
			bool reachable = false;
			List<OllamaModel> models = null;
			lblOllamaStatus.ForeColor = grayText;
			lblOllamaStatus.Text = "Asking Ollama...";
			RunInBackground(delegate { new TranslatorHelperClient(connectedUrl).GetModels(url, out reachable, out models); }, delegate (Exception error)
			{
				if (error != null)
				{
					lblOllamaStatus.ForeColor = badText;
					lblOllamaStatus.Text = "Could not ask the helper: " + Describe(error);
					return;
				}
				ApplyModelLists(reachable, models, vision, translate == UseVisionModel ? string.Empty : translate);
			});
		}

		private void TestTranslator()
		{
			Dictionary<string, object> settings = CollectSettings();
			btTest.Enabled = false;
			lblTestResult.ForeColor = grayText;
			lblTestResult.Text = "Translating a sample sentence...";
			TranslatorTestResult result = null;
			RunInBackground(delegate { result = new TranslatorHelperClient(connectedUrl).TestTranslator(settings, null); }, delegate (Exception error)
			{
				btTest.Enabled = true;
				if (error != null)
				{
					lblTestResult.ForeColor = badText;
					lblTestResult.Text = "Test failed: " + Describe(error);
				}
				else if (result.Ok)
				{
					lblTestResult.ForeColor = goodText;
					lblTestResult.Text = "Works (" + result.Seconds.ToString("0.0") + " s with " + result.Model + "):\n“" + result.Input + "” → “" + result.Output + "”";
				}
				else
				{
					lblTestResult.ForeColor = badText;
					lblTestResult.Text = "Failed with " + result.Model + ": " + result.Error;
				}
			});
		}

		private void ClearCache()
		{
			if (MessageBox.Show(this, "Delete all stored translated pages? They are translated again the next time you read them.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
			{
				return;
			}
			btClearCache.Enabled = false;
			HelperState result = null;
			RunInBackground(delegate
			{
				TranslatorHelperClient client = new TranslatorHelperClient(connectedUrl);
				client.ClearCache();
				result = client.GetState();
			}, delegate (Exception error)
			{
				if (error != null)
				{
					btClearCache.Enabled = true;
					MessageBox.Show(this, "The cache could not be cleared: " + Describe(error), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
					return;
				}
				ShowCache(result.CachePages, result.CacheMegabytes);
				SettingsApplied?.Invoke(this, EventArgs.Empty);
			});
		}

		private void Apply(Action onSaved)
		{
			if (state == null || !dirty)
			{
				onSaved?.Invoke();
				return;
			}
			//ComicRack's own setting, kept here; the reader takes it when the helper's settings are saved (SettingsApplied)
			Program.Settings.TranslatorAheadPages = (int)nudAhead.Value;
			if (CacheFolderChangedWithoutCopy() && MessageBox.Show(this, "The pages translated so far stay in " + cacheActive + ".\n\nIn the new folder, every page that is not there is translated again. Press \"Copy the stored pages to this folder\" first to take them along.\n\nUse the new folder without copying?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
			{
				return;
			}
			Dictionary<string, object> settings = CollectSettings();
			busy = true;
			UpdateButtons();
			Cursor = Cursors.WaitCursor;
			HelperState saved = null;
			Dictionary<string, string> rejected = null;
			RunInBackground(delegate
			{
				try
				{
					saved = new TranslatorHelperClient(connectedUrl).Save(settings);
				}
				catch (HelperRejectedException ex)
				{
					rejected = ex.Errors;
				}
			}, delegate (Exception error)
			{
				busy = false;
				Cursor = Cursors.Default;
				if (saved != null)
				{
					ShowState(saved);
					SettingsApplied?.Invoke(this, EventArgs.Empty);
					onSaved?.Invoke();
					return;
				}
				UpdateButtons();
				if (rejected != null)
				{
					StringBuilder text = new StringBuilder("These settings are not valid:\n\n");
					foreach (KeyValuePair<string, string> r in rejected)
					{
						string name;
						text.AppendLine("• " + (SettingNames.TryGetValue(r.Key, out name) ? name : r.Key) + " " + r.Value);
					}
					MessageBox.Show(this, text.ToString(), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
				}
				else
				{
					MessageBox.Show(this, "The settings could not be saved: " + Describe(error), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
				}
			});
		}

		// ---- the cache folder ----------------------------------------------------------------

		private static string NormPath(string path)
		{
			path = (path ?? string.Empty).Trim().Trim('"');
			if (path.Length == 0)
			{
				return string.Empty;
			}
			try
			{
				path = Path.GetFullPath(path);
			}
			catch (Exception)
			{
			}
			return path.TrimEnd('\\', '/').ToLowerInvariant();
		}

		/// <summary>What is saved as the cache folder: nothing when it is the default one beside the helper.</summary>
		private string CacheDirSetting()
		{
			string typed = (txtCacheDir.Text ?? string.Empty).Trim().Trim('"');
			return typed.Length == 0 || NormPath(typed) == NormPath(cacheDefault) ? string.Empty : typed;
		}

		/// <summary>True when the folder to be used differs from the one in use, holds nothing copied from it here, and there are pages to lose.</summary>
		private bool CacheFolderChangedWithoutCopy()
		{
			if (state == null || state.CachePages <= 0 || string.IsNullOrEmpty(cacheActive))
			{
				return false;
			}
			string setting = CacheDirSetting();
			string wanted = NormPath(setting.Length == 0 ? cacheDefault : setting);
			return wanted != NormPath(cacheActive) && wanted != copiedTo;
		}

		private void CacheFolderEdited()
		{
			if (loading)
			{
				return;
			}
			Edited();
			UpdateCopyButtons();
			freeCheck.Stop();
			freeCheck.Start();
		}

		private void UpdateCacheGb()
		{
			double gb = (double)nudCacheMb.Value / 1024.0;
			lblCacheGb.Text = "= " + (gb >= 100 ? gb.ToString("0") : gb.ToString("0.0")) + " GB (the oldest pages go first)";
		}

		/// <summary>Helpers from version 1.53 on report where they keep the pages and accept another folder.</summary>
		private bool HelperKeepsCacheFolder => state != null && !string.IsNullOrEmpty(state.CacheDefaultFolder);

		private void UpdateCopyButtons()
		{
			bool supported = HelperKeepsCacheFolder;
			string typed = NormPath(txtCacheDir.Text);
			btCopyCache.Enabled = supported && !copyRunning && typed.Length > 0 && typed != NormPath(cacheActive);
			btStopCopy.Visible = copyRunning;
			btBrowseCache.Enabled = btDefaultCache.Enabled = supported && !copyRunning;
			txtCacheDir.Enabled = supported;
			txtCacheDir.ReadOnly = copyRunning;
		}

		private static string SizeText(long bytes)
		{
			double gb = bytes / 1073741824.0;
			return gb >= 1000 ? (gb / 1024.0).ToString("0.#") + " TB" : (gb >= 100 ? gb.ToString("0") : gb.ToString("0.#")) + " GB";
		}

		private void freeCheck_Tick(object sender, EventArgs e)
		{
			freeCheck.Stop();
			string typed = (txtCacheDir.Text ?? string.Empty).Trim().Trim('"');
			int token = ++freeToken;
			freeBytes = totalBytes = -1;
			if (typed.Length == 0 || state == null)
			{
				lblFree.Text = string.Empty;
				return;
			}
			lblFree.ForeColor = grayText;
			lblFree.Text = "Looking at the free space there...";
			long free = -1, total = -1;
			RunInBackground(delegate { DiskSpace.TryGet(typed, out free, out total); }, delegate (Exception error)
			{
				if (token != freeToken)
				{
					return;
				}
				freeBytes = free;
				totalBytes = total;
				ShowFree(true);
			});
		}

		private bool freeShown;

		/// <summary>Shows the free space of the chosen folder's drive, and warns when the size limit is more than that. `known`: the numbers were just looked up.</summary>
		private void ShowFree(bool known = false)
		{
			if (!known && !freeShown)
			{
				return;
			}
			freeShown = true;
			if (freeBytes < 0)
			{
				lblFree.ForeColor = warnText;
				lblFree.Text = "That drive or network folder cannot be reached from here.";
				return;
			}
			bool tooBig = (double)nudCacheMb.Value * 1048576.0 > freeBytes;
			lblFree.ForeColor = tooBig ? warnText : grayText;
			lblFree.Text = "Free on that drive: " + SizeText(freeBytes) + " of " + SizeText(totalBytes) + (tooBig ? ". The size limit above is more than that." : ".");
		}

		private void BrowseCacheFolder()
		{
			using (FolderBrowserDialog dlg = new FolderBrowserDialog())
			{
				dlg.Description = "Choose the folder translated pages are kept in (on this computer, another drive or the network).";
				dlg.ShowNewFolderButton = true;
				string start = (txtCacheDir.Text ?? string.Empty).Trim();
				try
				{
					if (start.Length > 0 && !start.StartsWith("\\\\") && Directory.Exists(start))
					{
						dlg.SelectedPath = start;
					}
				}
				catch (Exception)
				{
				}
				if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
				{
					txtCacheDir.Text = dlg.SelectedPath;
				}
			}
		}

		private void StartCopy()
		{
			string to = (txtCacheDir.Text ?? string.Empty).Trim().Trim('"');
			if (to.Length == 0 || state == null || copyRunning)
			{
				return;
			}
			copyRunning = true;
			copiedTo = null;
			lblCopy.ForeColor = grayText;
			lblCopy.Text = "Starting...";
			UpdateCopyButtons();
			CacheCopyStatus first = null;
			Dictionary<string, string> rejected = null;
			RunInBackground(delegate
			{
				try
				{
					first = new TranslatorHelperClient(connectedUrl).StartCacheCopy(to);
				}
				catch (HelperRejectedException ex)
				{
					rejected = ex.Errors;
				}
			}, delegate (Exception error)
			{
				if (first == null)
				{
					copyRunning = false;
					UpdateCopyButtons();
					string why;
					lblCopy.ForeColor = badText;
					lblCopy.Text = rejected != null && rejected.TryGetValue("cache_dir", out why) ? "This folder " + why + "." : "The copy could not be started: " + (error != null ? Describe(error) : "the helper did not accept it") + ".";
					return;
				}
				copyTarget = to;
				copyPoll.Start();
				ShowCopy(first);
			});
		}

		private void copyPoll_Tick(object sender, EventArgs e)
		{
			if (copyPollBusy || connectedUrl == null)
			{
				return;
			}
			copyPollBusy = true;
			CacheCopyStatus status = null;
			RunInBackground(delegate { status = new TranslatorHelperClient(connectedUrl).GetCacheCopy(); }, delegate (Exception error)
			{
				copyPollBusy = false;
				if (status == null)
				{
					lblCopy.ForeColor = warnText;
					lblCopy.Text = "The helper is not answering (" + Describe(error) + "); still trying...";
					return;
				}
				ShowCopy(status);
			});
		}

		private void ShowCopy(CacheCopyStatus s)
		{
			if (!s.Done)
			{
				lblCopy.ForeColor = grayText;
				lblCopy.Text = s.Total == 0 ? "Looking through the stored pages..." : "Copying: " + s.Files.ToString("N0") + " of " + s.Total.ToString("N0") + " files (" + s.Megabytes.ToString("0.#") + " MB copied so far)";
				return;
			}
			copyPoll.Stop();
			copyRunning = false;
			if (!string.IsNullOrEmpty(s.Error))
			{
				lblCopy.ForeColor = badText;
				lblCopy.Text = s.Error;
			}
			else if (s.Cancelled)
			{
				lblCopy.ForeColor = warnText;
				lblCopy.Text = "Stopped after " + s.Files.ToString("N0") + " of " + s.Total.ToString("N0") + " files. Copy again to carry on: what is there already is not copied twice.";
			}
			else
			{
				copiedTo = NormPath(copyTarget);
				lblCopy.ForeColor = goodText;
				lblCopy.Text = "Copied " + s.Copied.ToString("N0") + " files (" + s.Megabytes.ToString("0.#") + " MB) to " + s.To + (s.Skipped > 0 ? "; " + s.Skipped.ToString("N0") + " were there already" : "") + ". Press OK or Apply to use that folder.";
			}
			UpdateCopyButtons();
		}

		private void StopCopy()
		{
			btStopCopy.Enabled = false;
			RunInBackground(delegate { new TranslatorHelperClient(connectedUrl).CancelCacheCopy(); }, delegate (Exception error)
			{
				btStopCopy.Enabled = true;
			});
		}

		/// <summary>The free space of the drive or network share a folder is on (the folder itself need not be there yet).</summary>
		private static class DiskSpace
		{
			[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
			private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

			public static void TryGet(string folder, out long free, out long total)
			{
				free = total = -1;
				try
				{
					string probe = Path.GetFullPath(folder);
					while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
					{
						probe = Path.GetDirectoryName(probe);
					}
					if (!string.IsNullOrEmpty(probe) && GetDiskFreeSpaceEx(probe, out ulong f, out ulong t, out ulong tf))
					{
						free = (long)f;
						total = (long)t;
					}
				}
				catch (Exception)
				{
				}
			}
		}

		private void BrowseFont()
		{
			using (OpenFileDialog dlg = new OpenFileDialog())
			{
				dlg.Title = "Choose a font file";
				dlg.Filter = "Fonts (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc|All files (*.*)|*.*";
				dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
				if (dlg.ShowDialog(this) == DialogResult.OK)
				{
					cboFont.Text = dlg.FileName;
				}
			}
		}

		private void OpenInBrowser()
		{
			try
			{
				Process.Start(ServerUrl);
			}
			catch (Exception)
			{
				MessageBox.Show(this, "Could not open " + ServerUrl, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			}
		}
	}
}
