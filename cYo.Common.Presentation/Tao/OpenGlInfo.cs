using System;
using Tao.OpenGl;

namespace cYo.Common.Presentation.Tao
{
	public static class OpenGlInfo
	{
		private const int GL_MAX_TEXTURE_SIZE = 3379;

		private const int GL_VERSION = 7938;

		private const int GL_EXTENSIONS = 7939;

		private const int FallbackMaxTextureSize = 2048;

		private static int maxTextureSize;

		private static float version;

		private static bool? supportsAnisotropicFilter;

		private static bool? supportsNonPower2Textures;

		private static bool? supportsTextureCompression;

		private static bool? supportsGenerateMipmap;

		public static int MaxTextureSize
		{
			get
			{
				if (maxTextureSize == 0)
				{
					Gl.glGetIntegerv(GL_MAX_TEXTURE_SIZE, out int value);
					//A value of 0 means we asked without a current context, or the driver failed.
					//Don't cache that, so the query is repeated once a context exists.
					if (value <= 0)
						return FallbackMaxTextureSize;
					maxTextureSize = value;
				}
				return maxTextureSize;
			}
		}

		public static float Version
		{
			get
			{
				if (version == 0f)
				{
					try
					{
						string text = Gl.glGetString(GL_VERSION).Trim();
						version = float.Parse(text.Substring(0, 1)) + float.Parse(text.Substring(2, 1)) / 10f;
					}
					catch
					{
						version = 1f;
					}
				}
				return version;
			}
		}

		public static bool SupportsAnisotopricFilter
		{
			get
			{
				if (!supportsAnisotropicFilter.HasValue)
				{
					supportsAnisotropicFilter = IsSupported("texture_filter_anisotropic");
				}
				return supportsAnisotropicFilter.Value;
			}
		}

		public static bool SupportsNonPower2Textures
		{
			get
			{
				if (!supportsNonPower2Textures.HasValue)
				{
					supportsNonPower2Textures = Version >= 2f || IsSupported("texture_non_power_of_two");
				}
				return supportsNonPower2Textures.Value;
			}
		}

		public static bool SupportsTextureCompression
		{
			get
			{
				if (!supportsTextureCompression.HasValue)
				{
					supportsTextureCompression = IsSupported("texture_compression");
				}
				return supportsTextureCompression.Value;
			}
		}

		/// <summary>
		/// True when glGenerateMipmap(EXT) can be used. This replaces the deprecated
		/// GL_GENERATE_MIPMAP (SGIS) texture parameter, which modern drivers only emulate.
		/// </summary>
		public static bool SupportsGenerateMipmap
		{
			get
			{
				if (!supportsGenerateMipmap.HasValue)
				{
					supportsGenerateMipmap = Version >= 3f || IsSupported("framebuffer_object");
				}
				return supportsGenerateMipmap.Value;
			}
		}

		/// <summary>
		/// Clears all cached capabilities. Must be called whenever a new rendering context
		/// becomes current, since the values belong to the context (and possibly to a
		/// different GPU on hybrid graphics machines).
		/// </summary>
		public static void Reset()
		{
			maxTextureSize = 0;
			version = 0f;
			supportsAnisotropicFilter = null;
			supportsNonPower2Textures = null;
			supportsTextureCompression = null;
			supportsGenerateMipmap = null;
		}

		/// <summary>
		/// Checks an extension by its suffix, so the ARB/EXT/vendor prefix does not matter.
		/// The old code asked for names like "ARB_texture_non_power_of_two", which never
		/// matched, because drivers report "GL_ARB_texture_non_power_of_two".
		/// </summary>
		private static bool IsSupported(string suffix)
		{
			try
			{
				string extensions = Gl.glGetString(GL_EXTENSIONS);
				if (string.IsNullOrEmpty(extensions))
					return false;
				foreach (string name in extensions.Split(' '))
				{
					if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
						return true;
				}
				return false;
			}
			catch
			{
				return false;
			}
		}
	}
}
