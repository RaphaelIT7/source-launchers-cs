using MinHook;

using Source;

using System;
using System.Runtime.InteropServices;

namespace srcds_cs.Detours;

internal class DetourConClearF : IImplementsDetours
{
	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	delegate void ConClearF();
	static ConClearF? ConClearF_Original;

	static unsafe void ConClearF_Detour() {
		ConClearF_Original!();
		Console.Clear();
		Console.CursorTop = 1;

		// Does this work?
		var cvar = Source.Engine.CreateInterface<ICvar>("vstdlib.dll", LoadModules.CVAR_INTERFACE_VERSION)!;
		void* test = cvar.FindVar("deathmatch"); // pulling a cvar out of my ass cause I can't find the signature for anything else rn
		Tier0.Msg($"test: {(nint)test:X}\n");
	}

	public void SetupWin32(HookEngine engine) {
		ConClearF_Original = engine.AddDetour<ConClearF>("engine.dll", [0xE8, 0xDB, 0x83, 0x13, 0x00, 0x8B, 0xC8, 0x8B, 0x10, 0xFF, 0x52, 0x4C], new(ConClearF_Detour));
	}
}