using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ComicRackLite
{
	internal enum PageLayout
	{
		Single,
		TwoPages,
		TwoPagesAdaptive
	}

	internal enum FitMode
	{
		FitAll,
		FitWidth,
		FitHeight,
		Original
	}

	internal enum ThemeChoice
	{
		FollowComicRack,
		Light,
		Dark
	}

	/// <summary>
	/// Lite's choices (ComicRackLite.ini) and the page each book was left at (ComicRackLite.books.txt), both in ComicRack's
	/// settings folder (%APPDATA%\cYo\ComicRack Community Edition).
	/// </summary>
	internal sealed class LiteSettings
	{
		public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cYo", "ComicRack Community Edition");

		private static readonly string IniFile = Path.Combine(Folder, "ComicRackLite.ini");

		private static readonly string BooksFile = Path.Combine(Folder, "ComicRackLite.books.txt");

		private const int MaxBooks = 2000;

		public Rectangle Bounds = Rectangle.Empty;

		public bool Maximized;

		public PageLayout Layout = PageLayout.Single;

		public bool RightToLeft;

		public FitMode Fit = FitMode.FitAll;

		public bool OnlyFitOversized;

		public ThemeChoice Theme = ThemeChoice.FollowComicRack;

		public bool ToolbarVisible = true;

		public bool ClickTurnsPages;

		/// <summary>Lay ComicRack's paper texture over the pages (when ComicRack has one).</summary>
		public bool PaperTexture = true;

		/// <summary>The page turn effect (Lite 1.4); none by default.</summary>
		public PageEffect PageEffect = PageEffect.None;

		public string LastFolder = "";

		private Dictionary<string, KeyValuePair<int, long>> books;

		private bool booksChanged;

		public static LiteSettings Load()
		{
			LiteSettings s = new LiteSettings();
			try
			{
				if (File.Exists(IniFile))
				{
					foreach (string line in File.ReadAllLines(IniFile))
					{
						int eq = line.IndexOf('=');
						if (eq > 0)
						{
							s.Set(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
						}
					}
				}
			}
			catch (Exception)
			{
			}
			return s;
		}

		private void Set(string key, string value)
		{
			switch (key)
			{
				case "Bounds":
				{
					int[] n = value.Split(',').Select((string v) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ? x : 0).ToArray();
					if (n.Length == 4 && n[2] > 200 && n[3] > 150)
					{
						Bounds = new Rectangle(n[0], n[1], n[2], n[3]);
					}
					break;
				}
				case "Maximized":
					Maximized = value == "1";
					break;
				case "Layout":
					Enum.TryParse(value, out Layout);
					break;
				case "RightToLeft":
					RightToLeft = value == "1";
					break;
				case "Fit":
					Enum.TryParse(value, out Fit);
					break;
				case "OnlyFitOversized":
					OnlyFitOversized = value == "1";
					break;
				case "Theme":
					Enum.TryParse(value, out Theme);
					break;
				case "Toolbar":
					ToolbarVisible = value != "0";
					break;
				case "PageEffect":
					Enum.TryParse(value, out PageEffect);
					break;
				case "PaperTexture":
					PaperTexture = value != "0";
					break;
				case "ClickTurnsPages":
					ClickTurnsPages = value == "1";
					break;
				case "LastFolder":
					LastFolder = value;
					break;
			}
		}

		public void Save()
		{
			try
			{
				Directory.CreateDirectory(Folder);
				StringBuilder sb = new StringBuilder();
				sb.AppendLine("; ComicRack Lite settings");
				sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Bounds={0},{1},{2},{3}", Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height));
				sb.AppendLine("Maximized=" + (Maximized ? "1" : "0"));
				sb.AppendLine("Layout=" + Layout);
				sb.AppendLine("RightToLeft=" + (RightToLeft ? "1" : "0"));
				sb.AppendLine("Fit=" + Fit);
				sb.AppendLine("OnlyFitOversized=" + (OnlyFitOversized ? "1" : "0"));
				sb.AppendLine("Theme=" + Theme);
				sb.AppendLine("Toolbar=" + (ToolbarVisible ? "1" : "0"));
				sb.AppendLine("ClickTurnsPages=" + (ClickTurnsPages ? "1" : "0"));
				sb.AppendLine("PaperTexture=" + (PaperTexture ? "1" : "0"));
				sb.AppendLine("PageEffect=" + PageEffect);
				sb.AppendLine("LastFolder=" + LastFolder);
				File.WriteAllText(IniFile, sb.ToString());
				SaveBooks();
			}
			catch (Exception)
			{
			}
		}

		/// <summary>Dark when the choice says so; "Follow ComicRack" reads ComicRack's own theme from its Config.xml.</summary>
		public bool IsDark()
		{
			switch (Theme)
			{
				case ThemeChoice.Dark:
					return true;
				case ThemeChoice.Light:
					return false;
			}
			try
			{
				string config = File.ReadAllText(Path.Combine(Folder, "Config.xml"));
				Match theme = Regex.Match(config, "<Theme>\\s*(\\w+)\\s*</Theme>");
				if (theme.Success)
				{
					return theme.Groups[1].Value.Equals("Dark", StringComparison.OrdinalIgnoreCase);
				}
				return Regex.IsMatch(config, "<DarkMode>\\s*true\\s*</DarkMode>", RegexOptions.IgnoreCase);
			}
			catch (Exception)
			{
				return false;
			}
		}

		private void LoadBooks()
		{
			if (books != null)
			{
				return;
			}
			books = new Dictionary<string, KeyValuePair<int, long>>(StringComparer.OrdinalIgnoreCase);
			try
			{
				if (!File.Exists(BooksFile))
				{
					return;
				}
				foreach (string line in File.ReadAllLines(BooksFile))
				{
					string[] parts = line.Split(new[] { '|' }, 3);
					if (parts.Length == 3 && int.TryParse(parts[0], out int page) && long.TryParse(parts[1], out long when))
					{
						books[parts[2]] = new KeyValuePair<int, long>(page, when);
					}
				}
			}
			catch (Exception)
			{
			}
		}

		/// <summary>The page the book was left at, or -1.</summary>
		public int GetPage(string path)
		{
			LoadBooks();
			return books.TryGetValue(path, out KeyValuePair<int, long> v) ? v.Key : -1;
		}

		public void SetPage(string path, int page)
		{
			LoadBooks();
			books[path] = new KeyValuePair<int, long>(page, DateTime.UtcNow.Ticks);
			booksChanged = true;
		}

		private void SaveBooks()
		{
			if (!booksChanged || books == null)
			{
				return;
			}
			//another Lite window may have saved meanwhile: keep the newer entry of each book
			Dictionary<string, KeyValuePair<int, long>> mine = books;
			books = null;
			LoadBooks();
			foreach (KeyValuePair<string, KeyValuePair<int, long>> b in mine)
			{
				if (!books.TryGetValue(b.Key, out KeyValuePair<int, long> other) || other.Value < b.Value.Value)
				{
					books[b.Key] = b.Value;
				}
			}
			IEnumerable<string> lines = books.OrderByDescending((KeyValuePair<string, KeyValuePair<int, long>> b) => b.Value.Value).Take(MaxBooks)
				.Select((KeyValuePair<string, KeyValuePair<int, long>> b) => b.Value.Key + "|" + b.Value.Value + "|" + b.Key);
			File.WriteAllLines(BooksFile, lines);
			booksChanged = false;
		}
	}
}
