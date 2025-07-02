namespace Source.Main;

using System.Runtime.InteropServices;

using Source;

public static partial class Program
{
	[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
	delegate int LauncherMainFunction(IntPtr hInstance, IntPtr hPrevInstance, string lpCmdLine, int nCmdShow);

	static void Main(string[] _) {
		Console.WriteLine(); // top bar gets overridden
		Console.WriteLine($"[launcher-cs / Main] Initializing...");
		var launcher = LoadModule("launcher.dll");
		var launcherMain = GetProcDelegate<LauncherMainFunction>(launcher, "LauncherMain");
		DetourManager.Bootstrap();
		Console.WriteLine($"[launcher-cs / Main] Our work is done - entering LauncherMain");
		Console.WriteLine("");
		launcherMain(Instance, 0, CommandLine, 1);
	}
}