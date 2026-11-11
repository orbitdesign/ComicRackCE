using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("ComicRack Lite")]
[assembly: AssemblyDescription("Fast comic reader that opens books from Explorer and hands them over to ComicRack")]
[assembly: AssemblyCompany("orbitdesign")]
[assembly: AssemblyProduct("ComicRack Community Edition")]
[assembly: AssemblyCopyright("2026 orbitdesign")]
//ComicRack Lite has its own version (Program.Version), raised with each change to it; keep these three in step with it
[assembly: AssemblyVersion("1.5.0.0")]
[assembly: AssemblyFileVersion("1.5.0.0")]
[assembly: AssemblyInformationalVersion("1.5 (ComicRack 1.186)")]

namespace ComicRackLite
{
	internal static class Program
	{
		private static string preloadPath;

		private static Task<PageSource> preload;

		public const string Name = "ComicRack Lite";

		/// <summary>Lite's own version (1.0 came with ComicRack 1.185); every change to Lite raises it and gets a line in Changes.txt.</summary>
		public const string Version = "1.5";

		/// <summary>The ComicRack build Lite came with.</summary>
		public const string ComicRackBuild = "1.186";

		public const string NameAndVersion = Name + " " + Version;

		public static readonly string AppFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

		/// <summary>
		/// Opens the book and reads its first pages while the window is built (WinForms' first use of fonts takes a while
		/// on a system with many fonts installed), so the page is ready when the window is.
		/// </summary>
		private static void StartPreload(string file, int startPage)
		{
			preloadPath = file;
			preload = Task.Run(delegate
			{
				PageSource source = PageSource.Open(file);
				try
				{
					int page = startPage >= 0 ? startPage : PageSource.IsImageName(file) ? source.StartPage : LiteSettings.Load().GetPage(file);
					if (page < 0 || page >= source.Count)
					{
						page = 0;
					}
					if (source.Count > 0)
					{
						source.Read(page);
						if (page + 1 < source.Count)
						{
							source.Read(page + 1);
						}
					}
				}
				catch (Exception)
				{
					//the page shows its error when it is drawn
				}
				return source;
			});
		}

		/// <summary>The book opened ahead for this path (once), or null.</summary>
		public static PageSource TakePreload(string path)
		{
			Task<PageSource> task = preload;
			if (task == null || !string.Equals(path, preloadPath, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			preload = null;
			try
			{
				return task.Result;
			}
			catch (AggregateException ex)
			{
				throw ex.InnerException;
			}
		}

		[STAThread]
		private static int Main(string[] args)
		{
			if (args.Any((string a) => a.Equals("-register", StringComparison.OrdinalIgnoreCase)))
			{
				return ShellRegistration.Register() ? 0 : 1;
			}
			if (args.Any((string a) => a.Equals("-unregister", StringComparison.OrdinalIgnoreCase)))
			{
				return ShellRegistration.Unregister() ? 0 : 1;
			}
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(defaultValue: false);
			Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
			Application.ThreadException += (object s, ThreadExceptionEventArgs e) => MessageBox.Show(e.Exception.Message, Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			//-p N: open at picture N (from 1), as ComicRack's switch; ComicRack's "Open in ComicRack Lite" passes the page shown
			int startPage = 0;
			int p = Array.FindIndex(args, (string a) => a.Equals("-p", StringComparison.OrdinalIgnoreCase));
			if (p >= 0 && p + 1 < args.Length)
			{
				int.TryParse(args[p + 1], out startPage);
			}
			string file = args.Where((string a, int i) => !(p >= 0 && i == p + 1)).FirstOrDefault((string a) => !a.StartsWith("-") && (File.Exists(a) || Directory.Exists(a)));
			if (file != null)
			{
				StartPreload(file, startPage - 1);
			}
			Application.Run(new ViewerForm(file, file != null ? startPage - 1 : -1));
			return 0;
		}
	}
}
