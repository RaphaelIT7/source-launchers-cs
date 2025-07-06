using MinHook;

using Source;
using Source.SDK;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace srcds_cs.Detours;

internal class DetourConClearF : IImplementsDetours
{
	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	delegate void ConClearF();
	static ConClearF? ConClearF_Original;

	static unsafe void csharpRunCallback(void* ccommandOpq) {
		CCommand cmd = MarshalCpp.Cast<CCommand>(ccommandOpq);
		Console.WriteLine($"You typed: {cmd.ArgSBuffer}");
	}

	static unsafe void ConClearF_Detour() {
		ConClearF_Original!();
		Console.Clear();
		Console.CursorTop = 1;

		ICvar cvar = Source.Engine.CreateInterface<ICvar>("vstdlib", LoadModules.CVAR_INTERFACE_VERSION)!;
		ConCommand lua_run = cvar.FindCommand("lua_run");
		ConCommand csharp_run = MarshalCpp.New<ConCommand>();

		csharp_run.Name = "csharp_run";
		csharp_run.HelpString = "There's no way this works, right?"; 
		csharp_run.ConCommandBase_VTable = lua_run.ConCommandBase_VTable; // proof of concept
		csharp_run.Flags = 1 << 2;
		csharp_run.CommandCallback = csharpRunCallback;
		csharp_run.UsingNewCommandCallback = true;

		cvar.RegisterConCommand(csharp_run);
	}

	public void SetupWin32(HookEngine engine) {
		ConClearF_Original = engine.AddDetour<ConClearF>("engine.dll", [0xE8, 0xDB, 0x83, 0x13, 0x00, 0x8B, 0xC8, 0x8B, 0x10, 0xFF, 0x52, 0x4C], new(ConClearF_Detour));
	}
}