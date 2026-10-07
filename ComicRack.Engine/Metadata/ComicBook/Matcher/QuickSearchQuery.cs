using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace cYo.Projects.ComicRack.Engine
{
	/// <summary>
	/// The parsed form of what was typed into the library's quick search box, and the
	/// text-folding rules used to compare it. Deliberately free of any ComicBook
	/// dependency, so the parsing and matching rules can be reasoned about (and tested)
	/// on their own; ComicBookQuickSearchMatcher adapts it to actual books.
	///
	/// Syntax, all case- and accent-insensitive:
	///   aspic franz          every word must appear somewhere, in any field, not
	///                        necessarily the same one
	///   "big trouble"        a phrase, matched as written within a single field
	///   -hentai              exclude books containing the word
	///   writer:franz         restrict a word to one field (see FieldAliases)
	///   series:"le juge"     a field-restricted phrase
	///   -publisher:glenat    exclude by field
	/// Words are separated by spaces, commas or semicolons, as before. A prefix that is
	/// not a known field name is treated as ordinary text, so titles such as "Re:Zero"
	/// still search the way they look.
	/// </summary>
	public sealed class QuickSearchQuery
	{
		public sealed class Term
		{
			/// <summary>
			/// Canonical field name from FieldAliases, or null for "any field in the
			/// currently selected search scope".
			/// </summary>
			public string Field
			{
				get;
				private set;
			}

			/// <summary>
			/// Already folded (see Fold), so it can be compared directly against folded
			/// field text.
			/// </summary>
			public string Text
			{
				get;
				private set;
			}

			public bool Exclude
			{
				get;
				private set;
			}

			/// <summary>
			/// True when Text is plain ASCII, which lets matching compare it against
			/// plain-ASCII field text without folding that text first - by far the
			/// common case, and the reason this search is not slower than the old one.
			/// </summary>
			public bool IsAscii
			{
				get;
				private set;
			}

			public Term(string field, string text, bool exclude)
			{
				Field = field;
				Text = text;
				Exclude = exclude;
				IsAscii = QuickSearchQuery.IsAscii(text);
			}
		}

		/// <summary>
		/// Alias typed before the colon -> canonical field name. The canonical names are
		/// what ComicBookQuickSearchMatcher knows how to read from a book.
		/// </summary>
		public static readonly IDictionary<string, string> FieldAliases = CreateFieldAliases();

		private readonly List<Term> terms;

		public IList<Term> Terms => terms;

		public bool IsEmpty => terms.Count == 0;

		private QuickSearchQuery(List<Term> terms)
		{
			this.terms = terms;
		}

		private static IDictionary<string, string> CreateFieldAliases()
		{
			Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			Action<string, string[]> add = delegate(string canonical, string[] aliases)
			{
				d[canonical] = canonical;
				foreach (string alias in aliases)
				{
					d[alias] = canonical;
				}
			};
			add("series", new string[0]);
			add("title", new string[0]);
			add("writer", new[] { "author" });
			add("artist", new[] { "artists", "art", "penciller", "pencils", "inker", "colorist", "colourist", "letterer", "cover" });
			add("creator", new[] { "creators", "by" });
			add("editor", new string[0]);
			add("translator", new string[0]);
			add("publisher", new[] { "pub" });
			add("imprint", new string[0]);
			add("genre", new string[0]);
			add("tag", new[] { "tags" });
			add("character", new[] { "characters", "char" });
			add("team", new[] { "teams" });
			add("location", new[] { "locations", "loc" });
			add("arc", new[] { "storyarc" });
			add("group", new[] { "seriesgroup" });
			add("format", new string[0]);
			add("year", new string[0]);
			add("number", new[] { "num", "no" });
			add("volume", new[] { "vol" });
			add("file", new[] { "path", "filename" });
			add("notes", new[] { "note" });
			add("summary", new string[0]);
			add("language", new[] { "lang" });
			add("age", new[] { "agerating", "rating" });
			return d;
		}

		/// <summary>
		/// Parses query into terms. Never throws; anything it cannot make sense of is
		/// treated as plain text, and incomplete pieces left mid-typing (a lone "-", an
		/// empty "writer:", an unclosed quote) are ignored or taken as far as they go
		/// rather than suddenly filtering everything out.
		/// </summary>
		public static QuickSearchQuery Parse(string query)
		{
			List<Term> list = new List<Term>();
			if (string.IsNullOrEmpty(query))
			{
				return new QuickSearchQuery(list);
			}
			int i = 0;
			int n = query.Length;
			while (i < n)
			{
				while (i < n && IsSeparator(query[i]))
				{
					i++;
				}
				if (i >= n)
				{
					break;
				}
				bool exclude = false;
				if (query[i] == '-')
				{
					if (i + 1 >= n || IsSeparator(query[i + 1]))
					{
						//A dash on its own - "spider - man", or "aspic -" while the word
						//to exclude is still being typed - means nothing to search for.
						i++;
						continue;
					}
					exclude = true;
					i++;
				}
				string field = null;
				int colon = FindFieldColon(query, i);
				if (colon > i)
				{
					string candidate = query.Substring(i, colon - i);
					if (FieldAliases.TryGetValue(candidate, out string canonical))
					{
						field = canonical;
						i = colon + 1;
					}
				}
				string text;
				if (i < n && query[i] == '"')
				{
					int close = query.IndexOf('"', i + 1);
					if (close < 0)
					{
						close = n;
					}
					text = query.Substring(i + 1, close - i - 1);
					i = Math.Min(n, close + 1);
				}
				else
				{
					int start = i;
					while (i < n && !IsSeparator(query[i]))
					{
						i++;
					}
					text = query.Substring(start, i - start);
				}
				string folded = Fold(text.Trim());
				if (folded.Length > 0)
				{
					list.Add(new Term(field, folded, exclude));
				}
			}
			return new QuickSearchQuery(list);
		}

		/// <summary>
		/// Position of the ':' ending a "field:" prefix starting at start, or -1. Only a
		/// run of letters directly followed by ':' counts, so "Re:Zero" style titles and
		/// times such as "10:30" are simply passed through as text if the letters are not
		/// a known field (checked by the caller).
		/// </summary>
		private static int FindFieldColon(string s, int start)
		{
			int i = start;
			while (i < s.Length && char.IsLetter(s[i]))
			{
				i++;
			}
			if (i > start && i < s.Length && s[i] == ':')
			{
				return i;
			}
			return -1;
		}

		private static bool IsSeparator(char c)
		{
			if (!char.IsWhiteSpace(c) && c != ',')
			{
				return c == ';';
			}
			return true;
		}

		/// <summary>
		/// True when every character of text is plain ASCII.
		/// </summary>
		public static bool IsAscii(string text)
		{
			for (int i = 0; i < text.Length; i++)
			{
				if (text[i] >= '\u0080')
				{
					return false;
				}
			}
			return true;
		}

		/// <summary>
		/// The comparison form of text: lower-cased, with Latin accents removed (é -> e),
		/// full-width characters made normal width (Ａ -> a), and a few letters that do not
		/// decompose into "letter + accent" spelled out (ß -> ss, æ -> ae, ø -> o...).
		///
		/// Only the combining marks in the Latin/Greek/Cyrillic diacritics block are
		/// removed, and the result is recomposed afterwards: stripping every combining
		/// mark would also strip Japanese dakuten (が would match か) and leave Korean
		/// split into individual jamo, neither of which is what anyone means by "ignore
		/// accents".
		/// </summary>
		public static string Fold(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return string.Empty;
			}
			if (IsAscii(text))
			{
				return text.ToLowerInvariant();
			}
			string s;
			try
			{
				s = text.Normalize(NormalizationForm.FormKD);
			}
			catch (ArgumentException)
			{
				//Malformed UTF-16 (a lone surrogate, say) cannot be normalized; compare it
				//lower-cased as it is rather than failing the whole search over it.
				return text.ToLowerInvariant();
			}
			StringBuilder sb = new StringBuilder(s.Length);
			foreach (char c in s)
			{
				if (c >= '̀' && c <= 'ͯ')
				{
					continue;
				}
				switch (c)
				{
				case 'ß':
					sb.Append("ss");
					break;
				case 'æ':
				case 'Æ':
					sb.Append("ae");
					break;
				case 'œ':
				case 'Œ':
					sb.Append("oe");
					break;
				case 'ø':
				case 'Ø':
					sb.Append('o');
					break;
				case 'ł':
				case 'Ł':
					sb.Append('l');
					break;
				case 'đ':
				case 'Đ':
					sb.Append('d');
					break;
				case 'ı':
					sb.Append('i');
					break;
				default:
					sb.Append(c);
					break;
				}
			}
			try
			{
				return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
			}
			catch (ArgumentException)
			{
				return sb.ToString().ToLowerInvariant();
			}
		}

		/// <summary>
		/// Whether term occurs in one field's raw (unfolded) text. foldNonAscii is how the
		/// caller turns non-ASCII field text into its folded form, so the caller can cache
		/// it; plain-ASCII field text never needs folding, an ordinal case-insensitive
		/// search against the already-folded term gives the same answer.
		/// </summary>
		public static bool Contains(string rawField, Term term, Func<string, string> foldNonAscii)
		{
			if (string.IsNullOrEmpty(rawField))
			{
				return false;
			}
			if (IsAscii(rawField))
			{
				//An ASCII field can only contain an ASCII term. A non-ASCII term (Japanese,
				//say) cannot be found in it, and folding never turns ASCII into non-ASCII.
				if (!term.IsAscii)
				{
					return false;
				}
				return rawField.IndexOf(term.Text, StringComparison.OrdinalIgnoreCase) >= 0;
			}
			string folded = foldNonAscii(rawField);
			return folded.IndexOf(term.Text, StringComparison.Ordinal) >= 0;
		}
	}
}
