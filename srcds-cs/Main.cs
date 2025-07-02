namespace Source.Main;

using System.Runtime.InteropServices;
using Source;

public static partial class Program
{
	[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
	delegate int DedicatedMainFunction(IntPtr hInstance, IntPtr hPrevInstance, string lpCmdLine, int nCmdShow);

	static void Main(string[] _) {
		Console.WriteLine(); // top bar gets overridden
		Console.WriteLine($"[srcds-cs / Main] Initializing...");
		var dedicated = LoadModule("dedicated.dll");
		var dedicatedMain = GetProcDelegate<DedicatedMainFunction>(dedicated, "DedicatedMain");
		DetourManager.Bootstrap();
		Console.WriteLine($"[srcds-cs / Main] Our work is done - entering DedicatedMain");
		Console.WriteLine("");
		dedicatedMain(Instance, 0, CommandLine, 1);
	}
}