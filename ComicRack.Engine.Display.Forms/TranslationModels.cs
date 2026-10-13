using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace cYo.Projects.ComicRack.Engine.Display.Forms
{
	/// <summary>
	/// One piece of text on a translated page: where the original was, what it said, what it was
	/// translated to and the manual settings that replace the automatic lettering. The numbers are
	/// pixels of the original page image. The helper keeps the same fields.
	/// </summary>
	public class TranslationBox
	{
		public string Id { get; set; } = "";

		/// <summary>Where the original text was: left, top, right, bottom.</summary>
		public int[] Src { get; set; } = new int[4];

		public string Text { get; set; } = "";

		public string Translation { get; set; } = "";

		/// <summary>Added by the user rather than found by the helper.</summary>
		public bool Manual { get; set; }

		/// <summary>The translation was typed over by the user.</summary>
		public bool TextEdited { get; set; }

		/// <summary>The area the translation must stay inside, when the user placed it; otherwise null.</summary>
		public int[] Rect { get; set; }

		/// <summary>Font size in pixels; 0 lets the helper choose.</summary>
		public double Size { get; set; }

		public string Align { get; set; } = "center";

		/// <summary>Degrees, clockwise.</summary>
		public double Rotation { get; set; }

		/// <summary>Leave the original text as the artist drew it.</summary>
		public bool Skip { get; set; }

		/// <summary>The area the lettering uses on the last render (null until rendered).</summary>
		public int[] Bounds { get; set; }

		/// <summary>The font size the helper chose on the last render.</summary>
		public double AutoSize { get; set; }

		public bool HasManualSettings => Rect != null || Size > 0 || Rotation != 0 || Skip || TextEdited || !string.Equals(Align, "center", StringComparison.OrdinalIgnoreCase);

		/// <summary>True when this box differs from what the helper found, so it is kept with the book.</summary>
		public bool IsEdited => Manual || HasManualSettings;

		/// <summary>Where the lettering is: the user's rectangle, else what the helper used, else the original text.</summary>
		public int[] Area => Rect ?? Bounds ?? Src;

		public TranslationBox Clone()
		{
			TranslationBox b = (TranslationBox)MemberwiseClone();
			b.Src = (int[])Src.Clone();
			b.Rect = (int[])Rect?.Clone();
			b.Bounds = (int[])Bounds?.Clone();
			return b;
		}

		/// <summary>Forgets the manual settings (not the text) so the helper letters the box on its own again.</summary>
		public void ResetSettings()
		{
			Rect = null;
			Size = 0;
			Align = "center";
			Rotation = 0;
			Skip = false;
		}

		public void Write(Utf8JsonWriter w)
		{
			w.WriteStartObject();
			w.WriteString("id", Id);
			WriteInts(w, "src", Src);
			w.WriteString("text", Text ?? "");
			w.WriteString("translation", Translation ?? "");
			w.WriteBoolean("manual", Manual);
			w.WriteBoolean("text_edited", TextEdited);
			if (Rect != null)
			{
				WriteInts(w, "rect", Rect);
			}
			w.WriteNumber("size", Size);
			w.WriteString("align", Align ?? "center");
			w.WriteNumber("rotation", Rotation);
			w.WriteBoolean("skip", Skip);
			w.WriteEndObject();
		}

		public static TranslationBox Read(JsonElement e)
		{
			TranslationBox b = new TranslationBox
			{
				Id = GetString(e, "id"),
				Src = GetInts(e, "src") ?? new int[4],
				Text = GetString(e, "text"),
				Translation = GetString(e, "translation"),
				Manual = GetBool(e, "manual"),
				TextEdited = GetBool(e, "text_edited"),
				Rect = GetInts(e, "rect"),
				Size = GetDouble(e, "size"),
				Rotation = GetDouble(e, "rotation"),
				Skip = GetBool(e, "skip"),
				Bounds = GetInts(e, "bounds"),
				AutoSize = GetDouble(e, "auto_size")
			};
			string align = GetString(e, "align");
			b.Align = (align == "left" || align == "right") ? align : "center";
			return b;
		}

		internal static void WriteInts(Utf8JsonWriter w, string name, int[] values)
		{
			w.WriteStartArray(name);
			foreach (int v in values)
			{
				w.WriteNumberValue(v);
			}
			w.WriteEndArray();
		}

		internal static string GetString(JsonElement e, string name)
		{
			return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
		}

		internal static bool GetBool(JsonElement e, string name)
		{
			return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
		}

		internal static double GetDouble(JsonElement e, string name)
		{
			return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;
		}

		internal static int[] GetInts(JsonElement e, string name)
		{
			if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Array)
			{
				return null;
			}
			int[] values = v.EnumerateArray().Where((JsonElement x) => x.ValueKind == JsonValueKind.Number).Select((JsonElement x) => (int)Math.Round(x.GetDouble())).ToArray();
			return values.Length == 4 ? values : null;
		}
	}

	/// <summary>What the helper returns for a page: the boxes and the finished picture.</summary>
	public class PageAnalysis
	{
		public string Sha { get; set; }

		public int Width { get; set; }

		public int Height { get; set; }

		/// <summary>False when the page had nothing to translate (the picture is the original).</summary>
		public bool Changed { get; set; }

		public string Info { get; set; }

		public List<TranslationBox> Boxes { get; set; } = new List<TranslationBox>();

		public byte[] Image { get; set; }
	}

	public class TranslationRegion
	{
		public string Text { get; set; }

		public string Translation { get; set; }
	}

	/// <summary>The helper could not do what was asked. ServerDown is true when it was not reachable at all.</summary>
	public class TranslationApiException : Exception
	{
		public bool ServerDown { get; }

		public TranslationApiException(string message, bool serverDown)
			: base(message)
		{
			ServerDown = serverDown;
		}
	}
}
