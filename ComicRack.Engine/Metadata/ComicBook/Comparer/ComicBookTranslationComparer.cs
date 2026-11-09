using System.Collections.Generic;

namespace cYo.Projects.ComicRack.Engine
{
	public class ComicBookTranslationComparer : Comparer<ComicBook>
	{
		public override int Compare(ComicBook x, ComicBook y)
		{
			return string.Compare(x.TranslationState, y.TranslationState, ignoreCase: true);
		}
	}
}
