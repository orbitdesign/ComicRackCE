using System;

namespace ComicRackLite
{
	/// <summary>
	/// The page order ComicRack uses: its ExtendedStringComparer (cYo.Common.Text) with IgnoreCase, so numbers sort by
	/// value ("page2" before "page10") and Lite shows the pages in exactly ComicRack's order (page numbers handed over to
	/// ComicRack then point at the same page).
	/// </summary>
	internal static class NaturalCompare
	{
		public static int Compare(string s1, string s2)
		{
			if (string.IsNullOrEmpty(s1))
			{
				return !string.IsNullOrEmpty(s2) ? -1 : 0;
			}
			if (string.IsNullOrEmpty(s2))
			{
				return 1;
			}
			if (s1 == s2)
			{
				return 0;
			}
			int i1 = 0;
			int i2 = 0;
			int length = s1.Length;
			int length2 = s2.Length;
			bool lod1 = char.IsLetterOrDigit(s1[0]);
			bool lod2 = char.IsLetterOrDigit(s2[0]);
			if (lod1 && !lod2)
			{
				return 1;
			}
			if (!lod1 && lod2)
			{
				return -1;
			}
			do
			{
				char c1 = s1[i1];
				char c2 = s2[i2];
				bool d1 = char.IsDigit(c1);
				bool d2 = char.IsDigit(c2);
				if (!d1 && !d2)
				{
					if (c1 != c2)
					{
						bool l1 = char.IsLetter(c1);
						bool l2 = char.IsLetter(c2);
						if (l1 && l2)
						{
							int num = string.Compare(s1, i1, s2, i2, 1, ignoreCase: true);
							if (num != 0)
							{
								return num;
							}
						}
						else
						{
							if (l1 || l2)
							{
								return l1 ? 1 : -1;
							}
							int num2 = string.Compare(s1, i1, s2, i2, 1);
							if (num2 != 0)
							{
								return num2;
							}
						}
					}
				}
				else
				{
					if (!(d1 && d2))
					{
						return d1 ? -1 : 1;
					}
					int num3 = CompareNumbers(s1, length, ref i1, s2, length2, ref i2);
					if (num3 != 0)
					{
						return num3;
					}
				}
				i1++;
				i2++;
				if (i1 >= length)
				{
					return i2 >= length2 ? 0 : -1;
				}
			}
			while (i2 < length2);
			return 1;
		}

		private static int CompareNumbers(string s1, int s1Length, ref int i1, string s2, int s2Length, ref int i2)
		{
			int nz1 = i1;
			int nz2 = i2;
			int end1 = i1;
			int end2 = i2;
			ScanNumber(s1, s1Length, i1, ref nz1, ref end1);
			ScanNumber(s2, s2Length, i2, ref nz2, ref end2);
			int start1 = i1;
			int start2 = i2;
			i1 = end1 - 1;
			i2 = end2 - 1;
			//(the names are swapped as in ComicRack's comparer; the result is the same)
			int sig1 = end2 - nz2;
			int sig2 = end1 - nz1;
			if (sig1 == sig2)
			{
				int c1 = nz1;
				int c2 = nz2;
				while (c1 <= i1)
				{
					int diff = s1[c1] - s2[c2];
					if (diff != 0)
					{
						return diff;
					}
					c1++;
					c2++;
				}
				int total1 = end1 - start1;
				int total2 = end2 - start2;
				if (total1 == total2)
				{
					return 0;
				}
				return total1 > total2 ? -1 : 1;
			}
			return sig1 > sig2 ? -1 : 1;
		}

		private static void ScanNumber(string s, int length, int start, ref int nzStart, ref int end)
		{
			nzStart = start;
			end = start;
			bool leading = true;
			char c = s[end];
			do
			{
				if (leading)
				{
					if (c == '0')
					{
						nzStart++;
					}
					else
					{
						leading = false;
					}
				}
				end++;
				if (end < length)
				{
					c = s[end];
					continue;
				}
				break;
			}
			while (char.IsDigit(c));
		}
	}
}
