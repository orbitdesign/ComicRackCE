using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace cYo.Projects.ComicRack.Engine.Display.Forms
{
	/// <summary>
	/// The page calls of the ComicTranslator helper: read and translate a page, draw it again from
	/// edited boxes, read a rectangle the user drew, and forget stored pages. Every call blocks, so
	/// callers run them on a background thread.
	/// </summary>
	public class TranslationApi
	{
		public string BaseUrl { get; }

		public TranslationApi(string baseUrl)
		{
			BaseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? "http://127.0.0.1:8790" : baseUrl.Trim()).TrimEnd('/');
		}

		/// <summary>How many pages the helper has stored for the language and settings it uses now (helper 1.63 and later).</summary>
		public class BookCacheStatus
		{
			/// <summary>The helper's language, e.g. "Chinese (Simplified)", and its code, e.g. "zh-CN".</summary>
			public string Language = "";

			public string Code = "";

			/// <summary>Pages asked about, read and translated, and with their finished picture stored too.</summary>
			public int Asked;

			public int Read;

			public int Ready;
		}

		/// <summary>Asks the helper how many of these pages (fingerprint and fonts) it has stored for its language now.</summary>
		public BookCacheStatus CacheStatus(IList<KeyValuePair<string, StyleFonts>> pages)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				using (Utf8JsonWriter w = new Utf8JsonWriter(ms))
				{
					w.WriteStartObject();
					w.WriteStartArray("pages");
					foreach (KeyValuePair<string, StyleFonts> page in pages)
					{
						w.WriteStartObject();
						w.WriteString("sha", page.Key);
						if (page.Value != null && !page.Value.IsEmpty)
						{
							w.WriteString("font_caps", page.Value.Caps ?? "");
							w.WriteString("font_mixed", page.Value.Mixed ?? "");
						}
						w.WriteEndObject();
					}
					w.WriteEndArray();
					w.WriteEndObject();
				}
				using (JsonDocument doc = JsonDocument.Parse(Send("/api/cache-status", ms.ToArray(), "application/json", 20000)))
				{
					JsonElement root = doc.RootElement;
					return new BookCacheStatus
					{
						Language = root.TryGetProperty("language", out JsonElement l) && l.ValueKind == JsonValueKind.String ? l.GetString() : "",
						Code = root.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "",
						Asked = root.TryGetProperty("asked", out JsonElement a) && a.TryGetInt32(out int asked) ? asked : 0,
						Read = root.TryGetProperty("read", out JsonElement r) && r.TryGetInt32(out int read) ? read : 0,
						Ready = root.TryGetProperty("ready", out JsonElement d) && d.TryGetInt32(out int ready) ? ready : 0
					};
				}
			}
		}

		public PageAnalysis Analyze(byte[] page, StyleFonts fonts = null, bool cover = false)
		{
			string path = "/api/analyze";
			if (fonts != null && !fonts.IsEmpty)
			{
				//the fonts chosen for this page or book; the helper keeps the picture drawn with them apart from others
				path += "?font_caps=" + Uri.EscapeDataString(fonts.Caps ?? "") + "&font_mixed=" + Uri.EscapeDataString(fonts.Mixed ?? "");
			}
			if (cover)
			{
				//the book's front cover: its title is lettered as large as the original's place allows (helper 1.41)
				path += (path.Contains("?") ? "&" : "?") + "cover=1";
			}
			return ReadPage(Send(path, page, "application/octet-stream", 900000));
		}

		/// <summary>
		/// True when the helper already read and translated this page (given by its fingerprint), so showing it only loads what it
		/// stored. A quick call that answers false when the helper cannot say (an older helper, or not reachable).
		/// </summary>
		public bool IsCached(string sha)
		{
			return IsCached(sha, null, false, out bool _);
		}

		/// <summary>
		/// Whether the helper already read and translated this page, and (helper 1.44 and later) whether its finished picture is stored
		/// for these fonts and cover setting: read but without a picture, the page is drawn again, which takes a while. An older helper
		/// does not say, and the picture is taken as stored.
		/// </summary>
		public bool IsCached(string sha, StyleFonts fonts, bool cover, out bool picture)
		{
			picture = false;
			try
			{
				using (MemoryStream ms = new MemoryStream())
				{
					using (Utf8JsonWriter w = new Utf8JsonWriter(ms))
					{
						w.WriteStartObject();
						w.WriteString("sha", sha);
						if (fonts != null && !fonts.IsEmpty)
						{
							w.WriteString("font_caps", fonts.Caps ?? "");
							w.WriteString("font_mixed", fonts.Mixed ?? "");
						}
						if (cover)
						{
							w.WriteBoolean("cover", true);
						}
						w.WriteEndObject();
					}
					using (JsonDocument doc = JsonDocument.Parse(Send("/api/cached", ms.ToArray(), "application/json", 4000)))
					{
						bool read = TranslationBox.GetBool(doc.RootElement, "cached");
						picture = doc.RootElement.TryGetProperty("picture", out JsonElement p) ? p.ValueKind == JsonValueKind.True : read;
						return read;
					}
				}
			}
			catch (Exception)
			{
				return false;
			}
		}

		public PageAnalysis Render(byte[] page, IEnumerable<TranslationBox> boxList, IEnumerable<ProtectStroke> protect = null, StyleFonts fonts = null, bool cover = false)
		{
			List<TranslationBox> boxes = boxList.ToList();
			using (MemoryStream ms = new MemoryStream())
			{
				using (Utf8JsonWriter w = new Utf8JsonWriter(ms))
				{
					w.WriteStartObject();
					w.WriteString("image", Convert.ToBase64String(page));
					w.WriteStartArray("boxes");
					foreach (TranslationBox box in boxes)
					{
						box.Write(w);
					}
					w.WriteEndArray();
					if (fonts != null && !fonts.IsEmpty)
					{
						w.WriteString("font_caps", fonts.Caps ?? "");
						w.WriteString("font_mixed", fonts.Mixed ?? "");
					}
					if (cover)
					{
						w.WriteBoolean("cover", true);
					}
					List<ProtectStroke> strokes = protect?.Where((ProtectStroke p) => p != null && p.Points.Length >= 2).ToList();
					if (strokes != null && strokes.Count > 0)
					{
						w.WriteStartArray("protect");
						foreach (ProtectStroke stroke in strokes)
						{
							stroke.Write(w);
						}
						w.WriteEndArray();
					}
					w.WriteEndObject();
				}
				PageAnalysis rendered = ReadPage(Send("/api/render", ms.ToArray(), "application/json", 300000));
				//The helper does not echo these two back.
				foreach (TranslationBox sent in boxes.Where((TranslationBox b) => b.TextEdited || b.IsMergedAway))
				{
					TranslationBox got = rendered.Boxes.FirstOrDefault((TranslationBox b) => b.Id == sent.Id);
					if (got != null)
					{
						got.TextEdited = got.TextEdited || sent.TextEdited;
						got.MergedInto = sent.MergedInto;
					}
				}
				return rendered;
			}
		}

		public TranslationRegion ReadRegion(byte[] page, int[] rect)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				using (Utf8JsonWriter w = new Utf8JsonWriter(ms))
				{
					w.WriteStartObject();
					w.WriteString("image", Convert.ToBase64String(page));
					TranslationBox.WriteInts(w, "rect", rect);
					w.WriteEndObject();
				}
				using (JsonDocument doc = JsonDocument.Parse(Send("/api/read-region", ms.ToArray(), "application/json", 600000)))
				{
					return new TranslationRegion
					{
						Text = TranslationBox.GetString(doc.RootElement, "text"),
						Translation = TranslationBox.GetString(doc.RootElement, "translation")
					};
				}
			}
		}

		/// <summary>
		/// Asks the helper which font the lettering inside a rectangle of the page is set in: the closest fonts on this computer and
		/// in Google Fonts, with a picture of the box's text in each. The first search can take several minutes.
		/// </summary>
		public FontIdentification IdentifyFont(byte[] page, int[] rect, string text, bool online)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				using (Utf8JsonWriter w = new Utf8JsonWriter(ms))
				{
					w.WriteStartObject();
					w.WriteString("image", Convert.ToBase64String(page));
					TranslationBox.WriteInts(w, "rect", rect);
					w.WriteString("text", text ?? "");
					w.WriteBoolean("online", online);
					w.WriteEndObject();
				}
				return ReadIdentification(Send("/api/identify-font", ms.ToArray(), "application/json", 1800000));
			}
		}

		/// <summary>Looks for the font of all the given boxes of one page together.</summary>
		public FontIdentification IdentifyPageFont(byte[] page, IEnumerable<int[]> rects, string text, bool online, IEnumerable<string> texts = null)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				using (Utf8JsonWriter w = new Utf8JsonWriter(ms))
				{
					w.WriteStartObject();
					w.WriteString("image", Convert.ToBase64String(page));
					w.WriteStartArray("rects");
					foreach (int[] rect in rects)
					{
						w.WriteStartArray();
						foreach (int v in rect)
						{
							w.WriteNumberValue(v);
						}
						w.WriteEndArray();
					}
					w.WriteEndArray();
					if (texts != null)
					{
						//each box's own text, so that its kind of lettering (capitals or mixed case) is known
						w.WriteStartArray("texts");
						foreach (string t in texts)
						{
							w.WriteStringValue(t ?? "");
						}
						w.WriteEndArray();
					}
					w.WriteString("text", text ?? "");
					w.WriteBoolean("online", online);
					w.WriteEndObject();
				}
				return ReadIdentification(Send("/api/identify-fonts", ms.ToArray(), "application/json", 1800000));
			}
		}

		/// <summary>Looks for the font of a book from a sample of its pages: the helper uses the boxes it stored for each (pages not translated yet are left out).</summary>
		public FontIdentification IdentifyBookFont(IEnumerable<byte[]> pages, bool online)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				using (Utf8JsonWriter w = new Utf8JsonWriter(ms))
				{
					w.WriteStartObject();
					w.WriteStartArray("images");
					foreach (byte[] page in pages)
					{
						w.WriteStringValue(Convert.ToBase64String(page));
					}
					w.WriteEndArray();
					w.WriteBoolean("online", online);
					w.WriteEndObject();
				}
				return ReadIdentification(Send("/api/identify-fonts", ms.ToArray(), "application/json", 1800000));
			}
		}

		/// <summary>What the font search is doing now (a quick call that does not wait for the search).</summary>
		public FontProgress FontSearchProgress()
		{
			using (JsonDocument doc = JsonDocument.Parse(Send("/api/font-progress", Encoding.UTF8.GetBytes("{}"), "application/json", 5000)))
			{
				JsonElement r = doc.RootElement;
				return new FontProgress
				{
					Busy = TranslationBox.GetBool(r, "busy"),
					Stage = TranslationBox.GetString(r, "stage"),
					Done = (int)TranslationBox.GetDouble(r, "done"),
					Total = (int)TranslationBox.GetDouble(r, "total"),
					Seconds = TranslationBox.GetDouble(r, "seconds")
				};
			}
		}

		/// <summary>Stops the font search that is running.</summary>
		public void CancelFontSearch()
		{
			Send("/api/font-cancel", Encoding.UTF8.GetBytes("{}"), "application/json", 5000);
		}

		/// <summary>Every font file on this computer that the translator can use, sorted by name.</summary>
		public List<FontFile> FontsOnPc()
		{
			using (JsonDocument doc = JsonDocument.Parse(Send("/api/fonts-on-pc", Encoding.UTF8.GetBytes("{}"), "application/json", 120000)))
			{
				List<FontFile> list = new List<FontFile>();
				if (doc.RootElement.TryGetProperty("fonts", out JsonElement fonts) && fonts.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement e in fonts.EnumerateArray())
					{
						list.Add(new FontFile { Name = TranslationBox.GetString(e, "name"), Path = TranslationBox.GetString(e, "path"), Source = TranslationBox.GetString(e, "source") });
					}
				}
				return list;
			}
		}

		private static List<FontPage> ReadPages(JsonElement parent)
		{
			List<FontPage> list = new List<FontPage>();
			if (parent.TryGetProperty("pages", out JsonElement pages) && pages.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement e in pages.EnumerateArray())
				{
					if (e.ValueKind == JsonValueKind.Object)
					{
						list.Add(new FontPage { Index = (int)TranslationBox.GetDouble(e, "index"), Boxes = (int)TranslationBox.GetDouble(e, "boxes") });
					}
				}
			}
			return list;
		}

		private static List<FontMatch> ReadMatches(JsonElement parent, string name = "matches")
		{
			List<FontMatch> list = new List<FontMatch>();
			if (parent.TryGetProperty(name, out JsonElement matches) && matches.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement e in matches.EnumerateArray())
				{
					string preview = TranslationBox.GetString(e, "preview");
					list.Add(new FontMatch
					{
						Family = TranslationBox.GetString(e, "family"),
						File = TranslationBox.GetString(e, "file"),
						Source = TranslationBox.GetString(e, "source"),
						Category = TranslationBox.GetString(e, "category"),
						Url = TranslationBox.GetString(e, "url"),
						Score = (int)TranslationBox.GetDouble(e, "score"),
						InstalledCopy = TranslationBox.GetBool(e, "installed_copy"),
						Preview = string.IsNullOrEmpty(preview) ? null : Convert.FromBase64String(preview)
					});
				}
			}
			return list;
		}

		private static FontIdentification ReadIdentification(byte[] json)
		{
			{
				using (JsonDocument doc = JsonDocument.Parse(json))
				{
					JsonElement root = doc.RootElement;
					FontIdentification result = new FontIdentification
					{
						Error = TranslationBox.GetString(root, "error"),
						Compared = (int)TranslationBox.GetDouble(root, "compared"),
						LookedCloser = (int)TranslationBox.GetDouble(root, "looked_closer"),
						Seconds = TranslationBox.GetDouble(root, "seconds"),
						CandidatesFolder = TranslationBox.GetString(root, "candidates_folder"),
						Label = TranslationBox.GetString(root, "label")
					};
					string crop = TranslationBox.GetString(root, "crop");
					if (!string.IsNullOrEmpty(crop))
					{
						result.Crop = Convert.FromBase64String(crop);
					}
					if (root.TryGetProperty("notes", out JsonElement notes) && notes.ValueKind == JsonValueKind.Array)
					{
						result.Notes = notes.EnumerateArray().Select((JsonElement n) => n.GetString() ?? "").ToList();
					}
					result.Matches = ReadMatches(root);
					result.GoogleMatches = ReadMatches(root, "google_matches");
					result.Pages = ReadPages(root);
					if (root.TryGetProperty("groups", out JsonElement groups) && groups.ValueKind == JsonValueKind.Array)
					{
						foreach (JsonElement g in groups.EnumerateArray())
						{
							string gcrop = TranslationBox.GetString(g, "crop");
							FontGroup group = new FontGroup
							{
								Label = TranslationBox.GetString(g, "label"),
								Boxes = (int)TranslationBox.GetDouble(g, "boxes"),
								Crop = string.IsNullOrEmpty(gcrop) ? null : Convert.FromBase64String(gcrop),
								Matches = ReadMatches(g),
								GoogleMatches = ReadMatches(g, "google_matches"),
								Pages = ReadPages(g)
							};
							if (g.TryGetProperty("notes", out JsonElement gnotes) && gnotes.ValueKind == JsonValueKind.Array)
							{
								group.Notes = gnotes.EnumerateArray().Select((JsonElement n) => n.GetString() ?? "").ToList();
							}
							result.Groups.Add(group);
						}
					}
					if (root.TryGetProperty("external", out JsonElement external) && external.ValueKind == JsonValueKind.Array)
					{
						foreach (JsonElement e in external.EnumerateArray())
						{
							result.Links.Add(new FontLink { Name = TranslationBox.GetString(e, "family"), About = TranslationBox.GetString(e, "about"), Url = TranslationBox.GetString(e, "url") });
						}
					}
					return result;
				}
			}
		}

		/// <summary>Has the helper copy a font it found into its fonts folder and, when asked, use it for the lettering. Returns the file name.</summary>
		public string InstallFont(string family, bool use)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				using (Utf8JsonWriter w = new Utf8JsonWriter(ms))
				{
					w.WriteStartObject();
					w.WriteString("family", family);
					w.WriteBoolean("use", use);
					w.WriteEndObject();
				}
				using (JsonDocument doc = JsonDocument.Parse(Send("/api/install-font", ms.ToArray(), "application/json", 300000)))
				{
					return TranslationBox.GetString(doc.RootElement, "file");
				}
			}
		}

		/// <summary>Makes the helper forget what it stored for these pages (their shas), so they are read again.</summary>
		public void Forget(IEnumerable<string> shas)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				using (Utf8JsonWriter w = new Utf8JsonWriter(ms))
				{
					w.WriteStartObject();
					w.WriteStartArray("shas");
					foreach (string sha in shas)
					{
						w.WriteStringValue(sha);
					}
					w.WriteEndArray();
					w.WriteEndObject();
				}
				Send("/api/forget-pages", ms.ToArray(), "application/json", 60000);
			}
		}

		private static PageAnalysis ReadPage(byte[] json)
		{
			using (JsonDocument doc = JsonDocument.Parse(json))
			{
				JsonElement root = doc.RootElement;
				PageAnalysis page = new PageAnalysis
				{
					Sha = TranslationBox.GetString(root, "sha"),
					Width = (int)TranslationBox.GetDouble(root, "width"),
					Height = (int)TranslationBox.GetDouble(root, "height"),
					Changed = TranslationBox.GetBool(root, "changed"),
					Info = TranslationBox.GetString(root, "info"),
					Image = Convert.FromBase64String(TranslationBox.GetString(root, "image"))
				};
				if (root.TryGetProperty("boxes", out JsonElement boxes) && boxes.ValueKind == JsonValueKind.Array)
				{
					page.Boxes = boxes.EnumerateArray().Select(TranslationBox.Read).ToList();
				}
				return page;
			}
		}

		private byte[] Send(string path, byte[] body, string contentType, int timeout)
		{
			try
			{
				HttpWebRequest request = (HttpWebRequest)WebRequest.Create(BaseUrl + path);
				request.Method = "POST";
				request.ContentType = contentType;
				request.Proxy = null;
				request.Timeout = timeout;
				request.ReadWriteTimeout = timeout;
				request.ContentLength = body.Length;
				using (Stream s = request.GetRequestStream())
				{
					s.Write(body, 0, body.Length);
				}
				using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
				{
					return ReadAll(response.GetResponseStream());
				}
			}
			catch (WebException ex)
			{
				if (ex.Response is HttpWebResponse response)
				{
					using (response)
					{
						string problem = null;
						try
						{
							problem = Encoding.UTF8.GetString(ReadAll(response.GetResponseStream())).Trim();
						}
						catch (Exception)
						{
						}
						throw new TranslationApiException(string.IsNullOrEmpty(problem) ? "Translation failed (" + (int)response.StatusCode + ")" : problem, serverDown: false);
					}
				}
				throw new TranslationApiException("Translator not running at " + BaseUrl + " - start ComicTranslator\\run.bat", serverDown: true);
			}
		}

		private static byte[] ReadAll(Stream stream)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				stream.CopyTo(ms);
				return ms.ToArray();
			}
		}
	}
}
