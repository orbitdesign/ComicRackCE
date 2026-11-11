using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ComicRackLite
{
	/// <summary>
	/// Puts ComicRack Lite in Explorer's "Open with" list for comic files (this user only, HKEY_CURRENT_USER). Windows does
	/// not let a program make itself the default: the user picks it once in "Open with" with "Always use this app".
	/// </summary>
	internal static class ShellRegistration
	{
		private const string ProgId = "ComicRackLite.Comic";

		private static readonly string[] Extensions = { ".cbz", ".cbr", ".cb7", ".cbt" };

		[DllImport("shell32.dll")]
		private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

		private static string Exe => Assembly.GetExecutingAssembly().Location;

		public static bool Register()
		{
			try
			{
				string command = "\"" + Exe + "\" \"%1\"";
				using (RegistryKey classes = Registry.CurrentUser.CreateSubKey("Software\\Classes"))
				{
					using (RegistryKey prog = classes.CreateSubKey(ProgId))
					{
						prog.SetValue("", "Comic Book");
						prog.SetValue("FriendlyTypeName", "Comic Book");
						using (RegistryKey icon = prog.CreateSubKey("DefaultIcon"))
						{
							icon.SetValue("", "\"" + Exe + "\",0");
						}
						using (RegistryKey open = prog.CreateSubKey("shell\\open\\command"))
						{
							open.SetValue("", command);
						}
					}
					using (RegistryKey app = classes.CreateSubKey("Applications\\ComicRackLite.exe"))
					{
						app.SetValue("FriendlyAppName", Program.Name);
						using (RegistryKey open = app.CreateSubKey("shell\\open\\command"))
						{
							open.SetValue("", command);
						}
						using (RegistryKey types = app.CreateSubKey("SupportedTypes"))
						{
							foreach (string ext in Extensions)
							{
								types.SetValue(ext, "");
							}
						}
					}
					foreach (string ext in Extensions)
					{
						using (RegistryKey with = classes.CreateSubKey(ext + "\\OpenWithProgids"))
						{
							with.SetValue(ProgId, new byte[0], RegistryValueKind.None);
						}
					}
				}
				SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		public static bool Unregister()
		{
			try
			{
				using (RegistryKey classes = Registry.CurrentUser.OpenSubKey("Software\\Classes", writable: true))
				{
					classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
					classes.DeleteSubKeyTree("Applications\\ComicRackLite.exe", throwOnMissingSubKey: false);
					foreach (string ext in Extensions)
					{
						using (RegistryKey with = classes.OpenSubKey(ext + "\\OpenWithProgids", writable: true))
						{
							with?.DeleteValue(ProgId, throwOnMissingValue: false);
						}
					}
				}
				SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>Windows' "How do you want to open this file?" for a book, where "Always use this app" can be ticked.</summary>
		public static void ShowOpenWith(string file)
		{
			Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + file) { UseShellExecute = false });
		}

		public static bool IsDefaultFor(string extension)
		{
			try
			{
				using (RegistryKey choice = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\" + extension + "\\UserChoice"))
				{
					string prog = choice?.GetValue("ProgId") as string;
					return prog != null && (prog == ProgId || prog.IndexOf("ComicRackLite", StringComparison.OrdinalIgnoreCase) >= 0);
				}
			}
			catch (Exception)
			{
				return false;
			}
		}

		public static string FolderOf(string path)
		{
			return Directory.Exists(path) ? path : Path.GetDirectoryName(path);
		}
	}
}
