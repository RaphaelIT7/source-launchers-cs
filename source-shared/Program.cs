using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Source.Main;

[Flags]
public enum ArchitectureOS : ushort
{
	x86 = 1 << 0,
	x64 = 1 << 1,

	Windows = 1 << 8,
	OSX = 1 << 9,
	Linux = 1 << 10,

	Win32 = x86 | Windows,
	Win64 = x64 | Win32,
	Linux32 = x86 | Linux,
	Linux64 = x64 | Linux,
}

public static partial class Program
{

	public static string CommandLine;
	public static ArchitectureOS Architecture;
	public static bool IsX64 => (Architecture & ArchitectureOS.x64) == ArchitectureOS.x64;
	public static ArchitectureOS CPU => Architecture & (ArchitectureOS)0x00FF;
	public static ArchitectureOS Platform => Architecture & (ArchitectureOS)0xFF00;
	public static IntPtr Instance;
	public static string Bin;

	public static string GetModulePath(string name) => Path.Combine(Bin, name);

	public static IntPtr LoadModule(string name) {
		switch (Platform) {
			case ArchitectureOS.Windows: {
					string realPath = GetModulePath(name);
					if (!File.Exists(realPath))
						throw new FileNotFoundException($"DLL '{name}' missing? We tried looking here: {realPath}");

					IntPtr dll = Win32.LoadLibraryExA(realPath, 0, Win32.LOAD_WITH_ALTERED_SEARCH_PATH);
					if (dll == 0)
						throw new FileLoadException($"The DLL '{name}' at {realPath} failed to load. Error code: {Marshal.GetLastWin32Error()}");

					return dll;
				}
			default:
				throw new PlatformNotSupportedException();
		}
	}

	public static IntPtr GetProcAddress(nint module, string name) {
		switch (Platform) {
			case ArchitectureOS.Windows:
				return Win32.GetProcAddress(module, name);
			default:
				throw new PlatformNotSupportedException();
		}
	}

	public static T GetProcDelegate<T>(nint module, string name) where T : Delegate {
		IntPtr ptr = GetProcAddress(module, name);
		return Marshal.GetDelegateForFunctionPointer<T>(ptr);
	}

	static Program() {
		CommandLine = Environment.CommandLine;

		Architecture = IntPtr.Size switch {
			8 => ArchitectureOS.x64,
			4 => ArchitectureOS.x86,
			_ => throw new PlatformNotSupportedException("You're not running on a 32-bit or 64-bit machine, somehow, or something went terribly wrong.")
		};

		if (OperatingSystem.IsWindows())
			Architecture |= ArchitectureOS.Windows;
		else if (OperatingSystem.IsLinux())
			Architecture |= ArchitectureOS.Linux;
		else
			throw new PlatformNotSupportedException("You're not running on a Windows or Linux platform.");

		Instance = Win32.GetModuleHandle(null);
		Bin = Path.Combine(AppContext.BaseDirectory, "bin", IsX64 ? "win64" : "");
	}
}
