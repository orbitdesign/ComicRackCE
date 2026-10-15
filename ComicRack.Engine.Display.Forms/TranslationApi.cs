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

		public PageAnalysis Analyze(byte[] page)
		{
			return ReadPage(Send("/api/analyze", page, "application/octet-stream", 900000));
		}

		public PageAnalysis Render(byte[] page, IEnumerable<TranslationBox> boxList, IEnumerable<ProtectStroke> protect = null)
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
