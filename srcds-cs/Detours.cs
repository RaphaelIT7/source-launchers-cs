using static srcds_cs.Win32;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MinHook;
using System.Reflection;

namespace srcds_cs;

public interface IImplementsDetours
{
	public void SetupWin32(HookEngine engine) { DetourManager.NotSupported = true; }
	public void SetupWin64(HookEngine engine) { DetourManager.NotSupported = true; }
	public void SetupLinux32(HookEngine engine) { DetourManager.NotSupported = true; }
	public void SetupLinux64(HookEngine engine) { DetourManager.NotSupported = true; }
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

	static Dictionary<string, nint> loadedModules = [];

	public static T AddDetour<T>(this HookEngine engine, string module, ReadOnlySpan<byte?> pattern, T del) where T : Delegate {
		T original = engine.CreateHook(ScanModuleProc32(module, pattern), del);
		return original;
	}

	public static nint GetModuleAddress32(string name) {
		if (!loadedModules.TryGetValue(name, out nint address)) {
			address = LoadLibraryExA(Path.Combine(AppContext.BaseDirectory, "bin", name), IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
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

				if (matched)
					return baseAddress + i;
			}
			throw new NullReferenceException("Cannot find signature");
		}
	}

	static HookEngine? engine;
	static List<IImplementsDetours> implementors = [];
	public static void Bootstrap() {
		engine?.Dispose();
		implementors.Clear();
		engine = new HookEngine();

		var implBaseType = typeof(IImplementsDetours);
		var implTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(asm => asm.GetTypes()).Where(p => implBaseType.IsAssignableFrom(p) && !p.IsInterface);
		foreach (var implType in implTypes) {
			var instance = (IImplementsDetours)Activator.CreateInstance(implType)!;
			implementors.Add(instance);
			switch (DedicatedMain.Architecture) {
				case ArchitectureOS.Win32: instance.SetupWin32(engine); break;
				case ArchitectureOS.Win64: instance.SetupWin64(engine); break;
				case ArchitectureOS.Linux32: instance.SetupLinux32(engine); break;
				case ArchitectureOS.Linux64: instance.SetupLinux64(engine); break;
			}
			if (!WasSupported()) {
				throw new NotImplementedException($"The detour-class '{implType.FullName ?? implType.Name}' did not implement Setup{DedicatedMain.Architecture}.");
			}
		}
		engine.EnableHooks();
	}
}