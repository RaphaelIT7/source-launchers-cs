using MinHook;

using Source;
using Source.SDK;

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

		ICvar cvar = Source.Engine.CreateInterface<ICvar>("vstdlib", LoadModules.CVAR_INTERFACE_VERSION)!;
		ConCommandBase cmd1 = cvar.FindVar("deathmatch");
		ConCommandBase cmd2 = cvar.FindVar("coop");
		ConCommandBase ccmd = MarshalCpp.New<ConCommandBase>();
		string? test = ccmd.Name;
		ccmd.Name = "csharp_run";
		ccmd.HelpString = "There's no way this works, right?"; // the answer is no!
		ccmd.ConCommandBase_VTable = cmd1.ConCommandBase_VTable; // proof of concept
		ccmd.Flags = 1 << 2;
		cvar.RegisterConCommand(ccmd);
	}

	public void SetupWin32(HookEngine engine) {
		ConClearF_Original = engine.AddDetour<ConClearF>("engine.dll", [0xE8, 0xDB, 0x83, 0x13, 0x00, 0x8B, 0xC8, 0x8B, 0x10, 0xFF, 0x52, 0x4C], new(ConClearF_Detour));
	}
}