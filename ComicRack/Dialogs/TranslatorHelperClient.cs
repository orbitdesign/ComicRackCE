using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace cYo.Projects.ComicRack.Viewer.Dialogs
{
	/// <summary>One entry of a list the helper offers (an engine, a language, a font).</summary>
	public class HelperChoice
	{
		public string Value { get; set; }

		public string Label { get; set; }

		public string Hint { get; set; }

		public override string ToString()
		{
			return Label;
		}
	}

	public class OllamaModel
	{
		public string Name { get; set; }

		public double SizeGb { get; set; }
	}

	/// <summary>What the ComicTranslator helper says about itself and its settings.</summary>
	public class HelperState
	{
		public string Version { get; set; }

		public string Detector { get; set; }

		public string LastError { get; set; }

		public Dictionary<string, object> Config { get; } = new Dictionary<string, object>();

		public Dictionary<string, List<HelperChoice>> Choices { get; } = new Dictionary<string, List<HelperChoice>>();

		public bool OllamaReachable { get; set; }

		public List<OllamaModel> OllamaModels { get; } = new List<OllamaModel>();

		/// <summary>Plain-English warnings, keyed by the setting they concern.</summary>
		public Dictionary<string, string> Problems { get; } = new Dictionary<string, string>();

		public int CachePages { get; set; }

		public double CacheMegabytes { get; set; }

		/// <summary>The folder the translated pages are kept in now, and the one used when none is chosen (helper 1.53 and later).</summary>
		public string CacheFolder { get; set; }

		public string CacheDefaultFolder { get; set; }

		public string GetString(string key)
		{
			object value;
			return Config.TryGetValue(key, out value) ? Convert.ToString(value) : string.Empty;
		}

		public int GetInt(string key, int fallback)
		{
			object value;
			return Config.TryGetValue(key, out value) && value is long ? (int)(long)value : fallback;
		}

		public bool GetBool(string key)
		{
			object value;
			return Config.TryGetValue(key, out value) && value is bool && (bool)value;
		}
	}

	/// <summary>How the helper's copy of the stored translated pages to another folder is getting on.</summary>
	public class CacheCopyStatus
	{
		public bool Running { get; set; }

		public bool Done { get; set; }

		public bool Cancelled { get; set; }

		public string Error { get; set; }

		public string To { get; set; }

		/// <summary>Files dealt with so far (copied or already there), of Total.</summary>
		public int Files { get; set; }

		public int Total { get; set; }

		public int Copied { get; set; }

		public int Skipped { get; set; }

		public double Megabytes { get; set; }
	}

	public class TranslatorTestResult
	{
		public bool Ok { get; set; }

		public string Model { get; set; }

		public string Input { get; set; }

		public string Output { get; set; }

		public string Error { get; set; }

		public double Seconds { get; set; }
	}

	/// <summary>The helper refused the request; Errors says which setting was wrong and why.</summary>
	public class HelperRejectedException : Exception
	{
		public Dictionary<string, string> Errors { get; }

		public HelperRejectedException(Dictionary<string, string> errors)
			: base("The helper did not accept these settings.")
		{
			Errors = errors;
		}
	}

	/// <summary>
	/// Talks to the settings API of the ComicTranslator helper (a small local web service).
	/// Every call blocks, so callers run them off the user-interface thread.
	/// </summary>
	public class TranslatorHelperClient
	{
		public string BaseUrl { get; }

		public TranslatorHelperClient(string baseUrl)
		{
			BaseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
		}

		public HelperState GetState()
		{
			return ParseState(Send("GET", "/api/config", null, 5000));
		}

		public void GetModels(string ollamaUrl, out bool reachable, out List<OllamaModel> models)
		{
			using (JsonDocument doc = JsonDocument.Parse(Send("GET", "/api/models?url=" + Uri.EscapeDataString(ollamaUrl ?? string.Empty), null, 10000)))
			{
				reachable = doc.RootElement.GetProperty("reachable").GetBoolean();
				models = ReadModels(doc.RootElement);
			}
		}

		/// <summary>Saves settings. Throws HelperRejectedException when a value is not acceptable.</summary>
		public HelperState Save(IDictionary<string, object> settings)
		{
			return ParseState(Send("POST", "/api/config", ToJson(settings), 15000));
		}

		public TranslatorTestResult TestTranslator(IDictionary<string, object> settings, string text)
		{
			Dictionary<string, object> body = new Dictionary<string, object>
			{
				["config"] = settings,
				["text"] = text ?? string.Empty
			};
			using (JsonDocument doc = JsonDocument.Parse(Send("POST", "/api/test", ToJson(body), 180000)))
			{
				JsonElement r = doc.RootElement;
				return new TranslatorTestResult
				{
					Ok = r.GetProperty("ok").GetBoolean(),
					Model = Str(r, "model"),
					Input = Str(r, "input"),
					Output = Str(r, "output"),
					Error = Str(r, "error"),
					Seconds = r.TryGetProperty("seconds", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0
				};
			}
		}

		public void ClearCache()
		{
			Send("POST", "/api/clear-cache", "{}", 30000);
		}

		/// <summary>Starts copying the stored pages to another folder (in the helper, in the background). Throws HelperRejectedException,
		/// with the reason under "cache_dir", when the folder cannot be used.</summary>
		public CacheCopyStatus StartCacheCopy(string folder)
		{
			return ParseCopy(Send("POST", "/api/cache-copy", ToJson(new Dictionary<string, object> { ["to"] = folder }), 15000));
		}

		public CacheCopyStatus GetCacheCopy()
		{
			return ParseCopy(Send("POST", "/api/cache-copy-status", "{}", 8000));
		}

		public CacheCopyStatus CancelCacheCopy()
		{
			return ParseCopy(Send("POST", "/api/cache-copy-cancel", "{}", 8000));
		}

		private static CacheCopyStatus ParseCopy(string json)
		{
			using (JsonDocument doc = JsonDocument.Parse(json))
			{
				JsonElement r = doc.RootElement;
				return new CacheCopyStatus
				{
					Running = Flag(r, "running"),
					Done = Flag(r, "done"),
					Cancelled = Flag(r, "cancelled"),
					Error = Str(r, "error"),
					To = Str(r, "to"),
					Files = Whole(r, "files"),
					Total = Whole(r, "total"),
					Copied = Whole(r, "copied"),
					Skipped = Whole(r, "skipped"),
					Megabytes = r.TryGetProperty("megabytes", out JsonElement m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() : 0
				};
			}
		}

		private static bool Flag(JsonElement e, string name)
		{
			return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
		}

		private static int Whole(JsonElement e, string name)
		{
			return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
		}

		// ---- plumbing -----------------------------------------------------------------

		private string Send(string method, string path, string body, int timeoutMs)
		{
			HttpWebRequest request = (HttpWebRequest)WebRequest.Create(BaseUrl + path);
			request.Method = method;
			request.Timeout = timeoutMs;
			request.ReadWriteTimeout = timeoutMs;
			request.Proxy = null;
			request.Accept = "application/json";
			if (body != null)
			{
				byte[] bytes = Encoding.UTF8.GetBytes(body);
				request.ContentType = "application/json; charset=utf-8";
				request.ContentLength = bytes.Length;
				using (Stream s = request.GetRequestStream())
				{
					s.Write(bytes, 0, bytes.Length);
				}
			}
			try
			{
				using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
				{
					return ReadAll(response);
				}
			}
			catch (WebException ex) when (ex.Response is HttpWebResponse bad && (int)bad.StatusCode == 400)
			{
				using (bad)
				{
					throw new HelperRejectedException(ParseErrors(ReadAll(bad)));
				}
			}
		}

		private static string ReadAll(HttpWebResponse response)
		{
			using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
			{
				return reader.ReadToEnd();
			}
		}

		private static Dictionary<string, string> ParseErrors(string json)
		{
			Dictionary<string, string> errors = new Dictionary<string, string>();
			try
			{
				using (JsonDocument doc = JsonDocument.Parse(json))
				{
					if (doc.RootElement.TryGetProperty("errors", out JsonElement e) && e.ValueKind == JsonValueKind.Object)
					{
						foreach (JsonProperty p in e.EnumerateObject())
						{
							errors[p.Name] = p.Value.GetString();
						}
					}
				}
			}
			catch (JsonException)
			{
			}
			return errors;
		}

		private static string ToJson(IDictionary<string, object> values)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				using (Utf8JsonWriter w = new Utf8JsonWriter(ms))
				{
					WriteValue(w, values);
				}
				return Encoding.UTF8.GetString(ms.ToArray());
			}
		}

		private static void WriteValue(Utf8JsonWriter w, object value)
		{
			IDictionary<string, object> map = value as IDictionary<string, object>;
			if (map != null)
			{
				w.WriteStartObject();
				foreach (KeyValuePair<string, object> kv in map)
				{
					w.WritePropertyName(kv.Key);
					WriteValue(w, kv.Value);
				}
				w.WriteEndObject();
			}
			else if (value is bool)
			{
				w.WriteBooleanValue((bool)value);
			}
			else if (value is int)
			{
				w.WriteNumberValue((int)value);
			}
			else
			{
				w.WriteStringValue(Convert.ToString(value) ?? string.Empty);
			}
		}

		private static string Str(JsonElement e, string name)
		{
			return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : string.Empty;
		}

		private static List<OllamaModel> ReadModels(JsonElement parent)
		{
			List<OllamaModel> list = new List<OllamaModel>();
			if (parent.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement m in models.EnumerateArray())
				{
					list.Add(new OllamaModel
					{
						Name = Str(m, "name"),
						SizeGb = m.TryGetProperty("size_gb", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0
					});
				}
			}
			return list;
		}

		private static HelperState ParseState(string json)
		{
			HelperState state = new HelperState();
			using (JsonDocument doc = JsonDocument.Parse(json))
			{
				JsonElement root = doc.RootElement;
				state.Version = Str(root, "version");
				state.Detector = Str(root, "detector");
				state.LastError = Str(root, "last_error");
				foreach (JsonProperty p in root.GetProperty("config").EnumerateObject())
				{
					switch (p.Value.ValueKind)
					{
					case JsonValueKind.Number:
						state.Config[p.Name] = p.Value.GetInt64();
						break;
					case JsonValueKind.True:
					case JsonValueKind.False:
						state.Config[p.Name] = p.Value.GetBoolean();
						break;
					default:
						state.Config[p.Name] = p.Value.GetString() ?? string.Empty;
						break;
					}
				}
				foreach (JsonProperty p in root.GetProperty("choices").EnumerateObject())
				{
					List<HelperChoice> list = new List<HelperChoice>();
					foreach (JsonElement c in p.Value.EnumerateArray())
					{
						list.Add(new HelperChoice { Value = Str(c, "value"), Label = Str(c, "label"), Hint = Str(c, "hint") });
					}
					state.Choices[p.Name] = list;
				}
				JsonElement ollama = root.GetProperty("ollama");
				state.OllamaReachable = ollama.GetProperty("reachable").GetBoolean();
				state.OllamaModels.AddRange(ReadModels(ollama));
				foreach (JsonProperty p in root.GetProperty("problems").EnumerateObject())
				{
					state.Problems[p.Name] = p.Value.GetString();
				}
				JsonElement cache = root.GetProperty("cache");
				state.CachePages = cache.GetProperty("pages").GetInt32();
				state.CacheMegabytes = cache.GetProperty("megabytes").GetDouble();
				state.CacheFolder = Str(cache, "folder");
				state.CacheDefaultFolder = Str(cache, "default");
			}
			return state;
		}
	}
}
