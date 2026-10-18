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

		/// <summary>
		/// Several rectangles that together make the area the translation must stay inside (boxes merged into one, for a
		/// bubble made of two): the text wraps inside the whole shape. Null for an ordinary box.
		/// </summary>
		public List<int[]> Shape { get; set; }

		/// <summary>The form of the box's area: rounded corners, cut corners or a triangle (see TranslationShapes). Needs Rect.</summary>
		public int Form { get; set; }

		/// <summary>Each corner's size in percent: top left, top right, bottom right, bottom left (for a triangle, the first is where its point sits).</summary>
		public int[] Corners { get; set; } = new int[4];

		/// <summary>Rounded corners are circles (a pill, a circle in a square) instead of scaling with the box (an oval).</summary>
		public bool Circular { get; set; }

		public bool HasForm => !TranslationShapes.IsPlain(Form, Corners);

		/// <summary>The corner points of a free polygon (flat x, y list in page pixels); null for other forms.</summary>
		public int[] Points { get; set; }

		/// <summary>Degrees, clockwise: the box's shape turned about its centre (the lettering follows when Rotation matches).</summary>
		public double ShapeAngle { get; set; }

		/// <summary>The box's shape differs from the plain upright rectangle.</summary>
		public bool HasShape => HasForm || Math.Abs(ShapeAngle) >= 0.05;

		/// <summary>
		/// The area the helper lettered this box in when that is not a plain rectangle (a traced bubble, an oval): corner points
		/// as a flat x, y list in page pixels. Only for showing and for starting to edit the shape; never sent back.
		/// </summary>
		public int[] Outline { get; set; }

		/// <summary>"oval" when the helper's area is an upright ellipse, "polygon" for another shape, "" for a rectangle.</summary>
		public string AutoKind { get; set; } = "";

		/// <summary>The id of the merged box this one was folded into; the box is then hidden and left alone.</summary>
		public string MergedInto { get; set; } = "";

		/// <summary>Font size in pixels; 0 lets the helper choose.</summary>
		public double Size { get; set; }

		public string Align { get; set; } = "center";

		/// <summary>Degrees, clockwise.</summary>
		public double Rotation { get; set; }

		/// <summary>Leave the original text as the artist drew it.</summary>
		public bool Skip { get; set; }

		/// <summary>Lettered by the helper's sign rule: line by line over the original lines, each group of lines at its own size.</summary>
		public bool AsSign { get; set; }

		/// <summary>Lettered by the helper's cover rule on any page: a title as large as fits the place of the original.</summary>
		public bool AsCover { get; set; }

		/// <summary>The area the lettering uses on the last render (null until rendered).</summary>
		public int[] Bounds { get; set; }

		/// <summary>The font size the helper chose on the last render.</summary>
		public double AutoSize { get; set; }

		/// <summary>A font file chosen for this box alone ("" lets the helper choose); it comes before the page's and the book's fonts.</summary>
		public string FontChoice { get; set; } = "";

		/// <summary>The font file the helper lettered this box with on the last render (never sent back).</summary>
		public string UsedFont { get; set; } = "";

		/// <summary>That font's family and style as the font names them ("Comic Sans MS", "Bold"); never sent back.</summary>
		public string UsedFamily { get; set; } = "";

		public string UsedStyle { get; set; } = "";

		/// <summary>The helper could not find the chosen font file on the last render and used its usual font.</summary>
		public bool FontChoiceMissing { get; set; }

		public bool HasManualSettings => Rect != null || HasShape || Size > 0 || Rotation != 0 || Skip || AsSign || AsCover || TextEdited || !string.Equals(Align, "center", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(FontChoice);

		/// <summary>True when this box differs from what the helper found, so it is kept with the book.</summary>
		public bool IsEdited => Manual || HasManualSettings || Shape != null || !string.IsNullOrEmpty(MergedInto);

		/// <summary>Where the lettering is: the user's rectangle, else what the helper used, else the original text.</summary>
		public bool IsMergedAway => !string.IsNullOrEmpty(MergedInto);

		/// <summary>Set by the helper when it lettered this box's text together with a neighbour's (same bubble); never sent back.</summary>
		public string AutoMergedInto { get; set; } = "";

		/// <summary>Not shown in the editor: folded into a merged box, by the user or by the helper.</summary>
		public bool IsHidden => IsMergedAway || !string.IsNullOrEmpty(AutoMergedInto);

		public int[] Area
		{
			get
			{
				if (Shape != null && Shape.Count > 0)
				{
					List<int[]> bounds = Shape.Select((int[] r) => TranslationShapes.Bounds(r)).ToList();
					return new[] { bounds.Min((int[] r) => r[0]), bounds.Min((int[] r) => r[1]), bounds.Max((int[] r) => r[2]), bounds.Max((int[] r) => r[3]) };
				}
				return Rect ?? Bounds ?? Src;
			}
		}

		/// <summary>Moves the box (its area and shape) by the given distance in page pixels.</summary>
		public void MoveBy(int dx, int dy)
		{
			if (Shape != null)
			{
				Shape = Shape.Select((int[] r) => { int[] moved = (int[])r.Clone(); moved[0] += dx; moved[1] += dy; moved[2] += dx; moved[3] += dy; return moved; }).ToList();
				return;
			}
			int[] a = (int[])Area.Clone();
			Rect = new[] { a[0] + dx, a[1] + dy, a[2] + dx, a[3] + dy };
			if (Points != null)
			{
				Points = Points.Select((int v, int i) => v + (i % 2 == 0 ? dx : dy)).ToArray();
			}
		}

		public TranslationBox Clone()
		{
			TranslationBox b = (TranslationBox)MemberwiseClone();
			b.Src = (int[])Src.Clone();
			b.Rect = (int[])Rect?.Clone();
			b.Bounds = (int[])Bounds?.Clone();
			b.Corners = (int[])(Corners ?? new int[4]).Clone();
			b.Points = (int[])Points?.Clone();
			b.Outline = (int[])Outline?.Clone();
			b.Shape = Shape?.Select((int[] r) => (int[])r.Clone()).ToList();
			return b;
		}

		/// <summary>Forgets the manual settings (not the text) so the helper letters the box on its own again.</summary>
		public void ResetSettings()
		{
			Rect = null;
			Form = TranslationShapes.Round;
			ShapeAngle = 0;
			Points = null;
			Corners = new int[4];
			Circular = false;
			Size = 0;
			Align = "center";
			Rotation = 0;
			Skip = false;
			AsSign = false;
			AsCover = false;
			FontChoice = "";
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
			if (Math.Abs(ShapeAngle) >= 0.05)
			{
				w.WriteNumber("shape_angle", ShapeAngle);
			}
			if (HasForm)
			{
				w.WriteNumber("kind", Form);
				w.WriteStartArray("corners");
				foreach (int c in Corners)
				{
					w.WriteNumberValue(c);
				}
				w.WriteEndArray();
				w.WriteBoolean("circular", Circular);
				if (Form == TranslationShapes.FreePolygon && Points != null)
				{
					w.WriteStartArray("points");
					foreach (int v in Points)
					{
						w.WriteNumberValue(v);
					}
					w.WriteEndArray();
				}
			}
			if (Shape != null && Shape.Count > 0)
			{
				w.WriteStartArray("shape");
				foreach (int[] r in Shape)
				{
					w.WriteStartArray();
					foreach (int v in r)
					{
						w.WriteNumberValue(v);
					}
					w.WriteEndArray();
				}
				w.WriteEndArray();
			}
			if (IsMergedAway)
			{
				w.WriteString("merged_into", MergedInto);
			}
			w.WriteNumber("size", Size);
			if (!string.IsNullOrEmpty(FontChoice))
			{
				w.WriteString("font_choice", FontChoice);
			}
			w.WriteString("align", Align ?? "center");
			w.WriteNumber("rotation", Rotation);
			w.WriteBoolean("skip", Skip);
			if (AsSign)
			{
				w.WriteBoolean("as_sign", true);
			}
			if (AsCover)
			{
				w.WriteBoolean("as_cover", true);
			}
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
				MergedInto = GetString(e, "merged_into"),
				AutoMergedInto = GetString(e, "auto_merged_into"),
				Size = GetDouble(e, "size"),
				Rotation = GetDouble(e, "rotation"),
				Skip = GetBool(e, "skip"),
				AsSign = GetBool(e, "as_sign"),
				AsCover = GetBool(e, "as_cover"),
				Bounds = GetInts(e, "bounds"),
				AutoSize = GetDouble(e, "auto_size"),
				FontChoice = GetString(e, "font_choice"),
				UsedFont = GetString(e, "font"),
				UsedFamily = GetString(e, "font_family"),
				UsedStyle = GetString(e, "font_style"),
				FontChoiceMissing = GetBool(e, "font_choice_missing")
			};
			b.Form = (int)GetDouble(e, "kind");
			b.ShapeAngle = GetDouble(e, "shape_angle");
			if (e.TryGetProperty("outline", out JsonElement outlineList) && outlineList.ValueKind == JsonValueKind.Array)
			{
				int[] flat = outlineList.EnumerateArray().Where((JsonElement x) => x.ValueKind == JsonValueKind.Number).Select((JsonElement x) => (int)Math.Round(x.GetDouble())).ToArray();
				if (flat.Length >= 6 && flat.Length % 2 == 0)
				{
					b.Outline = flat;
				}
			}
			b.AutoKind = GetString(e, "auto_kind");
			if (e.TryGetProperty("points", out JsonElement pointList) && pointList.ValueKind == JsonValueKind.Array)
			{
				b.Points = pointList.EnumerateArray().Where((JsonElement x) => x.ValueKind == JsonValueKind.Number).Select((JsonElement x) => (int)Math.Round(x.GetDouble())).ToArray();
			}
			b.Circular = GetBool(e, "circular");
			if (e.TryGetProperty("corners", out JsonElement corners) && corners.ValueKind == JsonValueKind.Array)
			{
				int[] cornerValues = corners.EnumerateArray().Where((JsonElement x) => x.ValueKind == JsonValueKind.Number).Select((JsonElement x) => (int)Math.Round(x.GetDouble())).ToArray();
				if (cornerValues.Length == 4)
				{
					b.Corners = cornerValues;
				}
			}
			if (e.TryGetProperty("shape", out JsonElement shape) && shape.ValueKind == JsonValueKind.Array)
			{
				List<int[]> rects = new List<int[]>();
				foreach (JsonElement r in shape.EnumerateArray())
				{
					if (r.ValueKind == JsonValueKind.Array)
					{
						int[] values = r.EnumerateArray().Where((JsonElement x) => x.ValueKind == JsonValueKind.Number).Select((JsonElement x) => (int)Math.Round(x.GetDouble())).ToArray();
						if (values.Length == 4 || values.Length == TranslationShapes.PartLength || values.Length >= TranslationShapes.AnglePart && (values.Length - TranslationShapes.AnglePart) % 2 == 0)
						{
							rects.Add(values);
						}
					}
				}
				b.Shape = rects.Count > 0 ? rects : null;
			}
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

	/// <summary>A font the helper found to be like the lettering of a box.</summary>
	public class FontMatch
	{
		public string Family { get; set; } = "";

		/// <summary>The font's file name, which is how the translator is told to use it.</summary>
		public string File { get; set; } = "";

		/// <summary>"google" (an open font from Google Fonts), "installed" (on this computer), "translator" (in the helper's fonts folder) or "candidates".</summary>
		public string Source { get; set; } = "";

		public string Category { get; set; } = "";

		public string Url { get; set; } = "";

		/// <summary>0 to 100: how near its lettering is.</summary>
		public int Score { get; set; }

		/// <summary>A small picture (PNG) of the box's own text set in this font.</summary>
		public byte[] Preview { get; set; }

		/// <summary>True for a Google font that is also installed on this computer (it was found under both).</summary>
		public bool InstalledCopy { get; set; }
	}

	/// <summary>The fonts that match one kind of lettering among the boxes (capitals, mixed case).</summary>
	/// <summary>A page sent to the font search and how many of its boxes have a given kind of lettering.</summary>
	public class FontPage
	{
		/// <summary>The page's place in the list of pages sent (0 for a single page).</summary>
		public int Index { get; set; }

		public int Boxes { get; set; }
	}

	public class FontGroup
	{
		public string Label { get; set; } = "";

		public int Boxes { get; set; }

		public byte[] Crop { get; set; }

		public List<FontMatch> Matches { get; set; } = new List<FontMatch>();

		/// <summary>The best Google fonts for this kind of lettering besides the ones in Matches (free to install).</summary>
		public List<FontMatch> GoogleMatches { get; set; } = new List<FontMatch>();

		/// <summary>The pages sent whose boxes have this kind of lettering, the one with the most first.</summary>
		public List<FontPage> Pages { get; set; } = new List<FontPage>();

		public List<string> Notes { get; set; } = new List<string>();
	}

	/// <summary>
	/// The fonts chosen for the two kinds of lettering, by font file name: one for boxes whose original is in capital letters, one for
	/// mixed case. Empty means the translator's own choice.
	/// </summary>
	public class StyleFonts
	{
		public string Caps { get; set; } = "";

		public string Mixed { get; set; } = "";

		public bool IsEmpty => string.IsNullOrEmpty(Caps) && string.IsNullOrEmpty(Mixed);

		public bool Same(StyleFonts other)
		{
			return other != null && (Caps ?? "") == (other.Caps ?? "") && (Mixed ?? "") == (other.Mixed ?? "");
		}

		public StyleFonts Clone()
		{
			return new StyleFonts { Caps = Caps ?? "", Mixed = Mixed ?? "" };
		}

		internal void Write(Utf8JsonWriter w, string name)
		{
			w.WriteStartObject(name);
			w.WriteString("caps", Caps ?? "");
			w.WriteString("mixed", Mixed ?? "");
			w.WriteEndObject();
		}

		internal static StyleFonts Read(JsonElement e)
		{
			return new StyleFonts { Caps = TranslationBox.GetString(e, "caps"), Mixed = TranslationBox.GetString(e, "mixed") };
		}
	}

	/// <summary>What the helper's font search is doing now.</summary>
	public class FontProgress
	{
		public bool Busy { get; set; }

		public string Stage { get; set; } = "";

		public int Done { get; set; }

		public int Total { get; set; }

		public double Seconds { get; set; }
	}

	/// <summary>A font file on this computer that the translator can use.</summary>
	public class FontFile
	{
		public string Name { get; set; } = "";

		public string Path { get; set; } = "";

		/// <summary>"installed" (Windows), "translator" (its fonts folder) or "candidates".</summary>
		public string Source { get; set; } = "";
	}

	/// <summary>A place to look for a font that the helper does not search by itself (opened in the user's browser).</summary>
	public class FontLink
	{
		public string Name { get; set; } = "";

		public string About { get; set; } = "";

		public string Url { get; set; } = "";
	}

	public class FontIdentification
	{
		public List<FontMatch> Matches { get; set; } = new List<FontMatch>();

		/// <summary>The best Google fonts besides the ones in Matches, listed on their own (helper 1.38 and later).</summary>
		public List<FontMatch> GoogleMatches { get; set; } = new List<FontMatch>();

		/// <summary>The pages sent whose boxes have the main kind of lettering, the one with the most first (helper 1.40 and later).</summary>
		public List<FontPage> Pages { get; set; } = new List<FontPage>();

		/// <summary>Filled in by ComicRack, not the helper: the reader page of each page sent, in the order sent (FontPage.Index is a
		/// place in this list).</summary>
		public List<int> SamplePages { get; set; } = new List<int>();

		/// <summary>When the boxes have more than one kind of lettering: the matches of each kind (the first is the same as Matches).</summary>
		public List<FontGroup> Groups { get; set; } = new List<FontGroup>();

		public List<FontLink> Links { get; set; } = new List<FontLink>();

		public List<string> Notes { get; set; } = new List<string>();

		public int Compared { get; set; }

		/// <summary>How many of the compared fonts were looked at more closely (0 from an older helper).</summary>
		public int LookedCloser { get; set; }

		/// <summary>How long the helper searched, in seconds (0 from an older helper).</summary>
		public double Seconds { get; set; }

		/// <summary>The lettering as cut from the page (PNG).</summary>
		public byte[] Crop { get; set; }

		/// <summary>Where fonts downloaded from other sites can be dropped to be compared too.</summary>
		public string CandidatesFolder { get; set; } = "";

		/// <summary>The kind of lettering of the main answer ("Capital letters (10 boxes)"), when the helper looked at several boxes.</summary>
		public string Label { get; set; } = "";

		public string Error { get; set; } = "";
	}

	/// <summary>
	/// A stroke of the protect brush: artwork the user painted over so that the helper leaves it as it is (does not wipe it as
	/// text) and does not letter over it. Points are x, y pairs in page pixels; Radius is the brush's radius in page pixels.
	/// </summary>
	public class ProtectStroke
	{
		public int Radius { get; set; } = 14;

		public int[] Points { get; set; } = new int[0];

		public ProtectStroke Clone()
		{
			return new ProtectStroke { Radius = Radius, Points = (int[])Points.Clone() };
		}

		/// <summary>True when the stroke passes within the given distance of a point.</summary>
		public bool Touches(double x, double y, double distance)
		{
			double reach = distance + Radius;
			for (int i = 0; i + 1 < Points.Length; i += 2)
			{
				double dx = Points[i] - x, dy = Points[i + 1] - y;
				if (dx * dx + dy * dy <= reach * reach)
				{
					return true;
				}
			}
			return false;
		}

		internal void Write(Utf8JsonWriter w)
		{
			w.WriteStartArray();
			w.WriteNumberValue(Radius);
			foreach (int v in Points)
			{
				w.WriteNumberValue(v);
			}
			w.WriteEndArray();
		}

		internal static ProtectStroke Read(JsonElement e)
		{
			int[] all = e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where((JsonElement x) => x.ValueKind == JsonValueKind.Number).Select((JsonElement x) => (int)Math.Round(x.GetDouble())).ToArray() : new int[0];
			if (all.Length < 3)
			{
				return null;
			}
			return new ProtectStroke { Radius = Math.Max(1, all[0]), Points = all.Skip(1).Take((all.Length - 1) / 2 * 2).ToArray() };
		}
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
