using MinHook;

using Source;

using System.Runtime.InteropServices;

namespace srcds_cs.Detours;

// CTextConsoleWin32::Init sig == [0x56, 0x57, 0x8B, 0xF9, 0xFF, 0x15, 0x10, 0x80]

internal unsafe class BetterConsole : IImplementsDetours
{
	[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
	delegate bool CSys_CreateConsoleWindow(void* ptr);
	static CSys_CreateConsoleWindow CSys_CreateConsoleWindow_Original;

	static bool CSys_CreateConsoleWindow_Detour(void* ptr) {
		return true;
	}

	public void SetupWin32(HookEngine engine) {
		CSys_CreateConsoleWindow_Original = engine.AddDetour<CSys_CreateConsoleWindow>("dedicated.dll", [0xFF, 0x15, 0x10, 0x80, null, null, null, 0xC0], new(CSys_CreateConsoleWindow_Detour));
	}
}