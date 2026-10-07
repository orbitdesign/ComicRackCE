using System;
using System.Collections.Generic;
using System.Linq;

namespace cYo.Projects.ComicRack.Engine
{
	/// <summary>
	/// The library quick search box's matcher (see QuickSearchQuery for the syntax).
	/// Replaces the "contains all of" ComicBookAllPropertiesMatcher the box used before,
	/// which needed every word in the same field - "aspic poupee" found nothing when Aspic
	/// was the series and Poupée the title - and compared accents exactly. Smart lists
	/// and their "All" matcher are untouched; this is only ever built in memory for the
	/// search box and never saved.
	/// </summary>
	[Serializable]
	public class ComicBookQuickSearchMatcher : ComicBookMatcher
	{
		/// <summary>
		/// Folded form of each non-ASCII field value seen, shared by every search so the
		/// relatively slow Unicode normalization runs once per distinct string rather than
		/// on every keystroke. Plain-ASCII text - most of a typical library - never enters
		/// it. Cleared outright if it ever grows past MaxFoldCacheEntries, which only a
		/// very large or very heavily edited library would reach.
		/// </summary>
		private static readonly Dictionary<string, string> foldCache = new Dictionary<string, string>(StringComparer.Ordinal);

		private const int MaxFoldCacheEntries = 400000;

		private static readonly Func<string, string> foldNonAscii = FoldCached;

		private string query = string.Empty;

		[NonSerialized]
		private QuickSearchQuery parsed;

		public string Query
		{
			get
			{
				return query;
			}
			set
			{
				query = value ?? string.Empty;
				parsed = null;
			}
		}

		/// <summary>
		/// Which fields a word without a "field:" prefix is looked for in - the same
		/// All/Series/Writer/Artists/... choice as the search box's own drop-down menu.
		/// </summary>
		public ComicBookAllPropertiesMatcher.MatcherOption Option
		{
			get;
			set;
		}

		public QuickSearchQuery ParsedQuery
		{
			get
			{
				if (parsed == null)
				{
					parsed = QuickSearchQuery.Parse(query);
				}
				return parsed;
			}
		}

		/// <summary>
		/// The search box's full filter: the Read/Unread and Comics/Fileless choices built
		/// exactly as before, plus this matcher for the typed text. Returns null when
		/// nothing at all is being filtered.
		/// </summary>
		public static ComicBookMatcher Create(string query, ComicBookAllPropertiesMatcher.MatcherOption option, ComicBookAllPropertiesMatcher.ShowOptionType showOption, ComicBookAllPropertiesMatcher.ShowComicType showComic)
		{
			ComicBookMatcher filters = ComicBookAllPropertiesMatcher.Create(null, ComicBookStringMatcher.OperatorContainsAll, option, showOption, showComic);
			ComicBookQuickSearchMatcher text = new ComicBookQuickSearchMatcher
			{
				Query = query,
				Option = option
			};
			if (text.ParsedQuery.IsEmpty)
			{
				return filters;
			}
			if (filters == null)
			{
				return text;
			}
			ComicBookGroupMatcher group = new ComicBookGroupMatcher
			{
				MatcherMode = MatcherMode.And
			};
			group.Matchers.Add(filters);
			group.Matchers.Add(text);
			return group;
		}

		public override IEnumerable<ComicBook> Match(IEnumerable<ComicBook> items)
		{
			QuickSearchQuery q = ParsedQuery;
			if (q.IsEmpty)
			{
				return items;
			}
			//Buffers belong to this one call rather than to the matcher, so two threads
			//running the same matcher at once cannot trample each other's values.
			List<string> scope = new List<string>();
			List<string> field = new List<string>();
			return items.Where((ComicBook cb) => Matches(cb, q, scope, field));
		}

		private bool Matches(ComicBook comicBook, QuickSearchQuery q, List<string> scope, List<string> field)
		{
			if (comicBook == null)
			{
				return false;
			}
			bool scopeFilled = false;
			foreach (QuickSearchQuery.Term term in q.Terms)
			{
				List<string> values;
				if (term.Field == null)
				{
					//The unprefixed words all share one scope, so it is read from the book
					//once however many words there are.
					if (!scopeFilled)
					{
						scope.Clear();
						scope.AddRange(ComicBookAllPropertiesMatcher.GetOptionValueSet(comicBook, Option));
						scopeFilled = true;
					}
					values = scope;
				}
				else
				{
					field.Clear();
					AddFieldValues(comicBook, term.Field, field);
					values = field;
				}
				bool found = false;
				for (int i = 0; i < values.Count; i++)
				{
					if (QuickSearchQuery.Contains(values[i], term, foldNonAscii))
					{
						found = true;
						break;
					}
				}
				if (found == term.Exclude)
				{
					return false;
				}
			}
			return true;
		}

		/// <summary>
		/// The book's values for one canonical field name from QuickSearchQuery.FieldAliases.
		/// The Shadow* forms are used where they exist, so a book with nothing entered but
		/// a series and number in its file name is still found by series:/number:, the
		/// same as it is shown in the library.
		/// </summary>
		private static void AddFieldValues(ComicBook b, string name, List<string> values)
		{
			switch (name)
			{
			case "series":
				values.Add(b.ShadowSeries);
				values.Add(b.AlternateSeries);
				break;
			case "title":
				values.Add(b.ShadowTitle);
				break;
			case "writer":
				values.Add(b.Writer);
				break;
			case "artist":
				AddArtists(b, values);
				break;
			case "creator":
				values.Add(b.Writer);
				AddArtists(b, values);
				values.Add(b.Editor);
				values.Add(b.Translator);
				break;
			case "editor":
				values.Add(b.Editor);
				break;
			case "translator":
				values.Add(b.Translator);
				break;
			case "publisher":
				values.Add(b.Publisher);
				break;
			case "imprint":
				values.Add(b.Imprint);
				break;
			case "genre":
				values.Add(b.Genre);
				break;
			case "tag":
				values.Add(b.Tags);
				break;
			case "character":
				values.Add(b.Characters);
				values.Add(b.MainCharacterOrTeam);
				break;
			case "team":
				values.Add(b.Teams);
				values.Add(b.MainCharacterOrTeam);
				break;
			case "location":
				values.Add(b.Locations);
				break;
			case "arc":
				values.Add(b.StoryArc);
				break;
			case "group":
				values.Add(b.SeriesGroup);
				break;
			case "format":
				values.Add(b.ShadowFormat);
				break;
			case "year":
				values.Add(b.ShadowYearAsText);
				break;
			case "number":
				values.Add(b.ShadowNumberAsText);
				values.Add(b.AlternateNumberAsText);
				break;
			case "volume":
				values.Add(b.ShadowVolumeAsText);
				break;
			case "file":
				values.Add(b.FilePath);
				break;
			case "notes":
				values.Add(b.Notes);
				break;
			case "summary":
				values.Add(b.Summary);
				break;
			case "language":
				values.Add(b.LanguageAsText);
				values.Add(b.LanguageISO);
				break;
			case "age":
				values.Add(b.AgeRating);
				break;
			}
		}

		private static void AddArtists(ComicBook b, List<string> values)
		{
			values.Add(b.Penciller);
			values.Add(b.Inker);
			values.Add(b.Colorist);
			values.Add(b.Letterer);
			values.Add(b.CoverArtist);
		}

		private static string FoldCached(string raw)
		{
			lock (foldCache)
			{
				if (foldCache.TryGetValue(raw, out string folded))
				{
					return folded;
				}
			}
			string result = QuickSearchQuery.Fold(raw);
			lock (foldCache)
			{
				if (foldCache.Count >= MaxFoldCacheEntries)
				{
					foldCache.Clear();
				}
				foldCache[raw] = result;
			}
			return result;
		}

		public override object Clone()
		{
			return new ComicBookQuickSearchMatcher
			{
				Query = Query,
				Option = Option,
				Not = Not
			};
		}

		public override bool IsSame(ComicBookMatcher cbm)
		{
			ComicBookQuickSearchMatcher other = cbm as ComicBookQuickSearchMatcher;
			if (other != null && base.IsSame(cbm) && other.Option == Option)
			{
				return other.Query == Query;
			}
			return false;
		}
	}
}
