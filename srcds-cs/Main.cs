namespace srcds_cs;

using System.Runtime.InteropServices;

using static Win32;

public enum Architecture
{
	X86,
	X64
}

internal class DedicatedMain
{
	[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
	delegate int DedicatedMainFunction(IntPtr hInstance, IntPtr hPrevInstance, string lpCmdLine, int nCmdShow);

	public static string CommandLine;
	public static Architecture Architecture;
	public static bool IsX64 => Architecture == Architecture.X64;
	public static IntPtr Instance;
	public static string Bin;
	public static string GetModulePath(string name) => Path.Combine(Bin, name);

	public static IntPtr LoadWindowsModule(string name) {
		string realPath = GetModulePath(name);
		if (!File.Exists(realPath)) 
			throw new FileNotFoundException($"DLL '{name}' missing? We tried looking here: {realPath}");

		IntPtr dll = LoadLibraryExA(realPath, 0, LOAD_WITH_ALTERED_SEARCH_PATH);
		if (dll == 0)
			throw new FileLoadException($"The DLL '{name}' at {realPath} failed to load. Error code: {Marshal.GetLastWin32Error()}");

		return dll;
	}

	public static IntPtr GetProcAddressWindows(nint module, string name) {
		return Win32.GetProcAddress(module, name);
	}

	public static IntPtr LoadModule(string name) {
		switch (Environment.OSVersion.Platform) {
			case PlatformID.Win32NT: return LoadWindowsModule(name);
			default: throw new PlatformNotSupportedException();
		}
	}

	public static IntPtr GetProcAddress(nint module, string name) {
		switch (Environment.OSVersion.Platform) {
			case PlatformID.Win32NT: return GetProcAddressWindows(module, name);
			default: throw new PlatformNotSupportedException();
		}
	}
	public static T GetProcDelegate<T>(nint module, string name) where T : Delegate {
		IntPtr ptr = GetProcAddress(module, name);
		return Marshal.GetDelegateForFunctionPointer<T>(ptr);
	}

	static DedicatedMain() {
		CommandLine = Environment.CommandLine;
		Architecture = IntPtr.Size == 8
						? Architecture.X64
						: IntPtr.Size == 4
							? Architecture.X86
							: throw new PlatformNotSupportedException("You're not running on a 32-bit or 64-bit machine, somehow, or something went terribly wrong.");
		Instance = GetModuleHandle(null);
		Bin = Path.Combine(AppContext.BaseDirectory, "bin", IsX64 ? "win64" : "");
	}

	static void Main(string[] _) {
		var dedicated = LoadModule("dedicated.dll");
		var dedicatedMain = GetProcDelegate<DedicatedMainFunction>(dedicated, "DedicatedMain");
		DetourManager.Bootstrap();
		dedicatedMain(Instance, 0, CommandLine, 1);
	}
}