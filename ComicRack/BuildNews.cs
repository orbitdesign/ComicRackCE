using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using cYo.Common.Net.News;

namespace cYo.Projects.ComicRack.Viewer
{
	/// <summary>
	/// This build's own changes as a news channel for the News window, read from the Changes.txt next to ComicRack (no download):
	/// every "* 1.N (date) KIND: text" entry of the orbitdesign section is an item, dated, so the window sorts it among Community
	/// Edition's news. An entry without a date takes the date of Changes.txt.
	/// </summary>
	public static class BuildNews
	{
		public const string ChangesLink = "https://github.com/orbitdesign/ComicRackCE/blob/master/ComicRack/Output/Changes.txt";

		//The helper has no page of its own on the web: its items link to this build's changes.
		public const string HelperChangesLink = ChangesLink;

		private static readonly Regex entry = new Regex(@"^\* (?<version>1\.\d+) (\((?<date>\d{4}-\d{2}-\d{2})\) )?(?<kind>[A-Z]+):\s*(?<text>.*)$", RegexOptions.Compiled);

		/// <summary>The channel, or null when Changes.txt cannot be read or has no entries.</summary>
		public static NewsChannel Load(string changesFile)
		{
			string[] lines;
			DateTime fileDate;
			try
			{
				lines = File.ReadAllLines(changesFile, Encoding.UTF8);
				fileDate = File.GetLastWriteTime(changesFile);
			}
			catch (Exception)
			{
				return null;
			}
			NewsChannel channel = new NewsChannel
			{
				Title = "orbitdesign Build changes",
				Description = "The changes of this build of ComicRack, from its Changes.txt",
				Link = ChangesLink
			};
			Dictionary<string, int> perVersion = new Dictionary<string, int>();
			foreach (string raw in lines)
			{
				string line = raw.TrimStart('\ufeff');
				if (line.StartsWith("Community Edition Build"))
				{
					break;          //Community Edition's own history comes after this build's changes; it has its own news
				}
				Match m = entry.Match(line);
				if (!m.Success)
				{
					continue;
				}
				string version = m.Groups["version"].Value;
				perVersion.TryGetValue(version, out int n);
				perVersion[version] = n + 1;
				DateTime date = fileDate;
				if (m.Groups["date"].Success)
				{
					DateTime.TryParseExact(m.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out date);
					//entries of one day keep their order: the later version a little later in the day
					date = date.AddHours(12).AddMinutes(VersionNumber(version) % 600);
				}
				string kind = m.Groups["kind"].Value;
				string text = m.Groups["text"].Value.Trim();
				channel.Items.Add(new NewsChannelItem
				{
					Guid = "orbitdesign-" + version + (n == 0 ? "" : "-" + (n + 1)),
					Title = version + " " + kind + ": " + Headline(text),
					Description = "<pre style='white-space:pre-wrap;width:81ex'>" + WebUtility.HtmlEncode(version + " " + kind + ": " + text) + "</pre>",
					Link = ChangesLink,
					Author = "orbitdesign",
					Category = "orbitdesign Build " + version,
					Published = date
				});
			}
			return channel.Items.Count > 0 ? channel : null;
		}

		private static readonly Regex helperEntry = new Regex(@"^## (?<version>\d+\.\d+) \((?<date>\d{4}-\d{2}-\d{2})\) (?<title>.+)$", RegexOptions.Compiled);

		/// <summary>
		/// The translator helper's changes as a news channel, from the CHANGES.md in its folder ("## 1.70 (2026-10-20) Title" and the
		/// text under it, one entry per version); null when there is no helper or no such file.
		/// </summary>
		public static NewsChannel LoadHelper(string helperFolder)
		{
			string file = string.IsNullOrEmpty(helperFolder) ? null : Path.Combine(helperFolder, "CHANGES.md");
			string[] lines;
			try
			{
				if (file == null || !File.Exists(file))
				{
					return null;
				}
				lines = File.ReadAllLines(file, Encoding.UTF8);
			}
			catch (Exception)
			{
				return null;
			}
			NewsChannel channel = new NewsChannel
			{
				Title = "ComicTranslator helper changes",
				Description = "The changes of the translator helper, from its CHANGES.md",
				Link = HelperChangesLink
			};
			NewsChannelItem item = null;
			StringBuilder text = new StringBuilder();
			void Finish()
			{
				if (item != null)
				{
					item.Description = "<pre style='white-space:pre-wrap;width:81ex'>" + WebUtility.HtmlEncode(item.Title + "\n\n" + text.ToString().Trim()) + "</pre>";
					channel.Items.Add(item);
				}
				text.Clear();
			}
			foreach (string raw in lines)
			{
				Match m = helperEntry.Match(raw.TrimStart('\ufeff'));
				if (m.Success)
				{
					Finish();
					string version = m.Groups["version"].Value;
					DateTime.TryParseExact(m.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime date);
					item = new NewsChannelItem
					{
						Guid = "comictranslator-" + version,
						Title = "Translator helper " + version + ": " + m.Groups["title"].Value.Trim(),
						Link = HelperChangesLink,
						Author = "orbitdesign",
						Category = "ComicTranslator " + version,
						//after this build's own changes of the same day
						Published = date.AddHours(12).AddMinutes(VersionNumber(version) % 600).AddSeconds(30)
					};
				}
				else if (item != null)
				{
					text.AppendLine(raw);
				}
			}
			Finish();
			return channel.Items.Count > 0 ? channel : null;
		}

		/// <summary>The item with the highest version number (the newest change).</summary>
		public static NewsChannelItem Newest(NewsChannel channel)
		{
			NewsChannelItem newest = null;
			foreach (NewsChannelItem item in channel.Items)
			{
				if (newest == null || VersionNumber(item.Category) >= VersionNumber(newest.Category))
				{
					newest = item;
				}
			}
			return newest;
		}

		private static int VersionNumber(string text)
		{
			Match m = Regex.Match(text ?? "", @"1\.(\d+)");
			return m.Success ? int.Parse(m.Groups[1].Value) : 0;
		}

		/// <summary>The entry's first sentence, cut to a list's width.</summary>
		private static string Headline(string text)
		{
			int end = text.IndexOf(". ", StringComparison.Ordinal);
			string first = end > 0 ? text.Substring(0, end) : text.TrimEnd('.');
			return first.Length > 110 ? first.Substring(0, 107).TrimEnd() + "..." : first;
		}
	}
}
