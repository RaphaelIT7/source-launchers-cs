using static Win32;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using MinHook;
using Source.Main;

namespace Source;

public interface IImplementsDetours
{
	public void SetupWin32(HookEngine engine) { DetourManager.NotSupported = true; }
	public void SetupWin64(HookEngine engine) { DetourManager.NotSupported = true; }
	public void SetupLinux32(HookEngine engine) { DetourManager.NotSupported = true; }
	public void SetupLinux64(HookEngine engine) { DetourManager.NotSupported = true; }
}

public unsafe static class Scanning {
	static Dictionary<string, nint> loadedModules = [];

	public static nint GetModuleAddress32(string name) {
		if (!loadedModules.TryGetValue(name, out nint address)) {
			address = LoadLibraryExA(Path.Combine(Main.Program.Bin, name), nint.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
			loadedModules[name] = address;
		}

		return address;
	}
	public static nint GetModuleProc32(string moduleName, nint offset) {
		nint baseAddress = GetModuleAddress32(moduleName);
		return baseAddress + offset;
	}
	public static nint ScanModuleProc32(string moduleName, ReadOnlySpan<byte?> scan) {
		nint baseAddress = GetModuleAddress32(moduleName);
		GetModuleInformation(Process.GetCurrentProcess().Handle, baseAddress, out MODULEINFO modInfo, (uint)Unsafe.SizeOf<MODULEINFO>());
		int scanLength = scan.Length;

		unsafe {
			byte* memory = (byte*)baseAddress;
			for (int i = 0; i < modInfo.SizeOfImage - scanLength; i++) {
				bool matched = true;
				for (int j = 0; j < scanLength; j++) {
					byte? expected = scan[j];
					if (expected.HasValue && memory[i + j] != expected.Value) {
						matched = false;
						break;
					}
				}

				if (matched) {
					Console.Write($"[source / Scanning] Found signature ");
					foreach (var b in scan) {
						if (b == null)
							Console.Write("?? ");
						else
							Console.Write($"{b:X} ");
					}
					Console.WriteLine($"at address +{i:X} in {moduleName}!");
					return baseAddress + i;
				}
			}
			throw new NullReferenceException("Cannot find signature");
		}
	}

	// expects a string of two-character hex codes (or ?? wildcards) separated by spaces until the final hex character
	// will throw up if given anything else
	internal static byte?[] Parse(string sig) {
		byte?[] ret = new byte?[(sig.Length + 1) / 3];
		for (int i = 0; i < sig.Length; i += 3) {
			ReadOnlySpan<char> ch = sig.AsSpan()[i..(i + 2)];
			if (ch[0] == '?' || ch[1] == '?')
				ret[i / 3] = null;
			else
				ret[i / 3] = Convert.FromHexString(ch)[0];
		}
		return ret;
	}
}

public unsafe static class DetourManager
{
	// This stuff is just to catch any errors switching to new architecture/OS in detour-land
	// Defining an empty method is enough for this not to trigger so just do that in that case

	public static bool NotSupported = false;
	public static bool WasSupported() {
		bool notSupported = NotSupported;
		NotSupported = false;
		return !notSupported;
	}
	public static T AddDetour<T>(this HookEngine engine, string module, ReadOnlySpan<byte?> pattern, T del) where T : Delegate {
		T original = engine.CreateHook(Scanning.ScanModuleProc32(module, pattern), del);
		return original;
	}

	static HookEngine? engine;
	static List<IImplementsDetours> implementors = [];
	public static void Bootstrap() {
		engine?.Dispose();
		implementors.Clear();
		engine = new HookEngine();
		Console.WriteLine($"[source / Detours] Bootstrapping detours...");

		var implBaseType = typeof(IImplementsDetours);
		var implTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(asm => asm.GetTypes()).Where(p => implBaseType.IsAssignableFrom(p) && !p.IsInterface);
		foreach (var implType in implTypes) {
			var instance = (IImplementsDetours)Activator.CreateInstance(implType)!;
			implementors.Add(instance);
			switch (Main.Program.Architecture) {
				case ArchitectureOS.Win32: instance.SetupWin32(engine); break;
				case ArchitectureOS.Win64: instance.SetupWin64(engine); break;
				case ArchitectureOS.Linux32: instance.SetupLinux32(engine); break;
				case ArchitectureOS.Linux64: instance.SetupLinux64(engine); break;
			}
			if (!WasSupported()) {
				throw new NotImplementedException($"The detour-class '{implType.FullName ?? implType.Name}' did not implement Setup{Main.Program.Architecture}.");
			}
			else {
				Console.WriteLine($"[source / Detours] Initialized {implType.Name} for {Main.Program.Architecture}");
			}
		}
		engine.EnableHooks();
		Console.WriteLine($"[source / Detours] Detours ready.");
	}
}