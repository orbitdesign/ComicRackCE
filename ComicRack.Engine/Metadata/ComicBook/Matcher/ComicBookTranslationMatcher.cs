using System;
using System.ComponentModel;

namespace cYo.Projects.ComicRack.Engine
{
	/// <summary>The page translator's state of the book ("Translated (English)", "Exported (English)", "Partly translated ..."): smart
	/// lists such as "translated but not exported yet". It changes without the book changing, so the list is not cached.</summary>
	[Serializable]
	[Description("Translation")]
	[ComicBookMatcherHint(true)]
	public class ComicBookTranslationMatcher : ComicBookStringMatcher
	{
		protected override string GetValue(ComicBook comicBook)
		{
			return comicBook.TranslationState;
		}
	}
}
