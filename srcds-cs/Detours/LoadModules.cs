using MinHook;

using Source;
using Source.Main;
using Source.SDK;

using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace srcds_cs.Detours;

internal unsafe class LoadModules : IImplementsDetours
{
	[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
	delegate bool CSys_LoadModules(void* self, void* pAppSystemGroup);
	static CSys_LoadModules? CSys_LoadModules_Original;
	static bool CSys_LoadModules_Detour(void* self, void* pAppSystemGroup) {
		CSys_LoadModules_Original!(self, pAppSystemGroup);

		CSys sys = MarshalCpp.Cast<CSys>(self);
		sys.ConsoleOutput("Hello from .NET land!");

		ICvar cvar = Source.Engine.CreateInterface<ICvar>("vstdlib", CVAR_INTERFACE_VERSION)!;
		ConCommandBase ccmd = MarshalCpp.New<ConCommandBase>();
		string? test = ccmd.Name;
		ccmd.Name = "csharp_run";
		ccmd.HelpString = "There's no way this works, right?";

		cvar.RegisterConCommand(ccmd);

		return true;
	}

	public void SetupWin32(HookEngine engine) {
		CSys_LoadModules_Original = engine.AddDetour<CSys_LoadModules>("dedicated.dll", [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x60, 0x56, 0x8B], new(CSys_LoadModules_Detour));
	}

	public const string CVAR_INTERFACE_VERSION = "VEngineCvar004";
}