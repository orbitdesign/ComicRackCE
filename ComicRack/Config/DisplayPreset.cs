using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace cYo.Projects.ComicRack.Viewer.Config
{
	/// <summary>
	/// Every setting in the Book Display Settings dialog, under a name: the reader's page
	/// effects, curl, paper and background, and the library's background, bookshelf and
	/// cover shadow. Textures are kept as references (see DisplayPresetStore), not paths,
	/// so a preset still works after the original picture has been moved or deleted.
	/// Plain public properties only - this is written with XmlSerializer.
	/// </summary>
	[Serializable]
	public class DisplayPreset
	{
		public string Name { get; set; }

		//Reader
		public int PageTransition { get; set; }
		public bool RealisticPages { get; set; }
		public bool PageMargin { get; set; }
		public int PageMarginPercent { get; set; }
		public int BackgroundType { get; set; }
		public string BackgroundColor { get; set; }
		public string BackgroundTexture { get; set; }
		public int BackgroundTextureLayout { get; set; }
		public string PaperTexture { get; set; }
		public int PaperStrength { get; set; }
		public int PaperLayout { get; set; }
		public int CurlAmount { get; set; }
		public int CurlShadowStrength { get; set; }
		public int CurlGrabArea { get; set; }
		public int TurnDuration { get; set; }

		//Library
		public bool LibraryBackgroundEnabled { get; set; }
		public string LibraryBackgroundTexture { get; set; }
		public int LibraryBackgroundLayout { get; set; }
		public bool LibraryShelfEnabled { get; set; }
		public string LibraryShelfTexture { get; set; }
		public int LibraryShelfPosition { get; set; }
		public int LibraryShelfHeight { get; set; }
		public int ShadowDistance { get; set; }
		public int ShadowAngle { get; set; }
		public int ShadowBlur { get; set; }
		public int ShadowTransparency { get; set; }

		/// <summary>
		/// Colour name as the colour picker uses it (like BackgroundColor), not an ARGB
		/// number: the picker only selects the colours in its own list, matched by name.
		/// </summary>
		public string ShadowColor { get; set; }
	}

	/// <summary>
	/// Presets live one-per-file in a "DisplayPresets" folder next to the rest of
	/// ComicRack's settings, and every custom texture a preset uses is copied into its
	/// "Textures" subfolder. Copies are named after a hash of their contents, so the same
	/// picture used by ten presets - or saved again later - is stored once, and nothing
	/// ever has to be deleted: removing a preset leaves its textures alone, which keeps
	/// whatever the library or reader is currently displaying from losing its picture.
	/// </summary>
	public static class DisplayPresetStore
	{
		private const string FolderName = "DisplayPresets";

		private const string TextureFolderName = "Textures";

		private const string Extension = ".preset.xml";

		//A texture reference is a short tag plus a name, so it means the same on another
		//install. "default:" is one of ComicRack's own bundled textures, found again by file
		//name; "store:" is a copy in the Textures folder; "path:" is only a fallback for
		//when copying failed, and points at the original file.
		private const string DefaultPrefix = "default:";

		private const string StorePrefix = "store:";

		private const string PathPrefix = "path:";

		private static readonly XmlSerializer serializer = new XmlSerializer(typeof(DisplayPreset));

		public static string FolderPath => Path.Combine(Program.Paths.ApplicationDataPath, FolderName);

		public static string TextureFolderPath => Path.Combine(FolderPath, TextureFolderName);

		/// <summary>
		/// Names of all saved presets, alphabetically. A file that cannot be read is skipped
		/// rather than failing the list.
		/// </summary>
		public static List<string> GetNames()
		{
			return ReadAll().Select((KeyValuePair<string, DisplayPreset> kvp) => kvp.Value.Name).OrderBy((string n) => n, StringComparer.CurrentCultureIgnoreCase).ToList();
		}

		public static DisplayPreset Load(string name)
		{
			foreach (KeyValuePair<string, DisplayPreset> item in ReadAll())
			{
				if (string.Equals(item.Value.Name, name, StringComparison.CurrentCultureIgnoreCase))
				{
					return item.Value;
				}
			}
			return null;
		}

		/// <summary>
		/// Saves the preset, replacing an existing one of the same name (ignoring case).
		/// </summary>
		public static void Save(DisplayPreset preset)
		{
			if (preset == null || string.IsNullOrWhiteSpace(preset.Name))
			{
				throw new ArgumentException("A preset needs a name.");
			}
			preset.Name = preset.Name.Trim();
			Directory.CreateDirectory(FolderPath);
			string target = FindFile(preset.Name) ?? NewFileName(preset.Name);
			string temp = target + ".tmp";
			XmlWriterSettings settings = new XmlWriterSettings
			{
				Indent = true,
				Encoding = new UTF8Encoding(false)
			};
			using (XmlWriter writer = XmlWriter.Create(temp, settings))
			{
				serializer.Serialize(writer, preset);
			}
			//Written beside the real file first, so a failure part-way never leaves a
			//half-written preset behind.
			File.Copy(temp, target, overwrite: true);
			File.Delete(temp);
		}

		public static bool Delete(string name)
		{
			string file = FindFile(name);
			if (file == null)
			{
				return false;
			}
			File.Delete(file);
			return true;
		}

		/// <summary>
		/// Turns a texture file path as shown in the dialog into what is saved in the
		/// preset. builtIn is ComicRack's own list of bundled textures for that setting
		/// (null for the library's, which only ever uses files the user picked).
		/// </summary>
		public static string ToReference(string path, IEnumerable<string> builtIn)
		{
			if (string.IsNullOrEmpty(path))
			{
				return string.Empty;
			}
			string bundled = builtIn?.FirstOrDefault((string b) => string.Equals(b, path, StringComparison.OrdinalIgnoreCase));
			if (bundled != null)
			{
				return DefaultPrefix + Path.GetFileName(bundled);
			}
			try
			{
				if (!File.Exists(path))
				{
					return string.Empty;
				}
				string full = Path.GetFullPath(path);
				if (string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(TextureFolderPath), StringComparison.OrdinalIgnoreCase))
				{
					return StorePrefix + Path.GetFileName(full);
				}
				return StorePrefix + StoreTexture(full);
			}
			catch (Exception)
			{
				return PathPrefix + path;
			}
		}

		/// <summary>
		/// The reverse: a usable file path for a saved reference, or null when there is
		/// nothing to show (no texture was set, or its file has gone missing).
		/// </summary>
		public static string FromReference(string reference, IEnumerable<string> builtIn)
		{
			if (string.IsNullOrEmpty(reference))
			{
				return null;
			}
			if (reference.StartsWith(DefaultPrefix, StringComparison.Ordinal))
			{
				string name = reference.Substring(DefaultPrefix.Length);
				return builtIn?.FirstOrDefault((string b) => string.Equals(Path.GetFileName(b), name, StringComparison.OrdinalIgnoreCase));
			}
			if (reference.StartsWith(StorePrefix, StringComparison.Ordinal))
			{
				//Only the file name is used, so a hand-edited preset cannot point outside
				//the Textures folder.
				string file = Path.Combine(TextureFolderPath, Path.GetFileName(reference.Substring(StorePrefix.Length)));
				return File.Exists(file) ? file : null;
			}
			if (reference.StartsWith(PathPrefix, StringComparison.Ordinal))
			{
				string file = reference.Substring(PathPrefix.Length);
				return File.Exists(file) ? file : null;
			}
			return null;
		}

		private static string StoreTexture(string fullPath)
		{
			string hash;
			using (SHA1 sha = SHA1.Create())
			using (FileStream stream = File.OpenRead(fullPath))
			{
				hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).Substring(0, 8).ToLowerInvariant();
			}
			string storedName = hash + "-" + SafeFileName(Path.GetFileName(fullPath), 100);
			Directory.CreateDirectory(TextureFolderPath);
			string target = Path.Combine(TextureFolderPath, storedName);
			if (!File.Exists(target))
			{
				File.Copy(fullPath, target);
			}
			return storedName;
		}

		private static List<KeyValuePair<string, DisplayPreset>> ReadAll()
		{
			List<KeyValuePair<string, DisplayPreset>> list = new List<KeyValuePair<string, DisplayPreset>>();
			if (!Directory.Exists(FolderPath))
			{
				return list;
			}
			foreach (string file in Directory.GetFiles(FolderPath, "*" + Extension))
			{
				try
				{
					using (XmlReader reader = XmlReader.Create(file))
					{
						DisplayPreset preset = serializer.Deserialize(reader) as DisplayPreset;
						if (preset != null)
						{
							if (string.IsNullOrWhiteSpace(preset.Name))
							{
								preset.Name = Path.GetFileName(file).Replace(Extension, string.Empty);
							}
							list.Add(new KeyValuePair<string, DisplayPreset>(file, preset));
						}
					}
				}
				catch (Exception)
				{
				}
			}
			return list;
		}

		private static string FindFile(string name)
		{
			foreach (KeyValuePair<string, DisplayPreset> item in ReadAll())
			{
				if (string.Equals(item.Value.Name, name, StringComparison.CurrentCultureIgnoreCase))
				{
					return item.Key;
				}
			}
			return null;
		}

		private static string NewFileName(string name)
		{
			string baseName = SafeFileName(name, 80);
			string file = Path.Combine(FolderPath, baseName + Extension);
			for (int i = 2; File.Exists(file); i++)
			{
				file = Path.Combine(FolderPath, baseName + "-" + i + Extension);
			}
			return file;
		}

		private static string SafeFileName(string text, int maxLength)
		{
			StringBuilder sb = new StringBuilder();
			char[] invalid = Path.GetInvalidFileNameChars();
			foreach (char c in text.Trim())
			{
				sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
			}
			string result = sb.ToString();
			if (result.Length > maxLength)
			{
				result = result.Substring(0, maxLength);
			}
			if (result.Length == 0)
			{
				return "preset";
			}
			//Windows refuses CON, NUL, COM1... as a file name even with an extension after it.
			string stem = result.Split('.')[0].TrimEnd().ToUpperInvariant();
			if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || ((stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem.Length == 4 && char.IsDigit(stem[3])))
			{
				result = "_" + result;
			}
			return result;
		}
	}
}
