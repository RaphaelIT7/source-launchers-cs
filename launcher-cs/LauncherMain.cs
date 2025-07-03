namespace Source.Main;

using System.Runtime.InteropServices;

using Source;

public static partial class Program
{
	[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
	delegate int LauncherMainFunction(IntPtr hInstance, IntPtr hPrevInstance, string lpCmdLine, int nCmdShow);

	[STAThread]
	static void Main(string[] _) {
		SetBinString(AppContext.BaseDirectory);

		string cd;
		if (IsX64)
			cd = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(AppContext.BaseDirectory)))!;
		else
			cd = Path.GetDirectoryName(Path.GetDirectoryName(AppContext.BaseDirectory))!;

		Directory.SetCurrentDirectory(cd);

		Console.WriteLine($"[launcher-cs / Main] Initializing...");
		var launcher = LoadModule("launcher.dll");
		var launcherMain = GetProcDelegate<LauncherMainFunction>(launcher, "LauncherMain");
		DetourManager.Bootstrap();
		Console.WriteLine($"[launcher-cs / Main] Our work is done - entering LauncherMain");
		Console.WriteLine("");
		launcherMain(Instance, 0, "-steam -game garrysmod " + CommandLine, 1);
	}
}