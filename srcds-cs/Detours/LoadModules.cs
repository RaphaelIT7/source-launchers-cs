using MinHook;

using Source;

using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace srcds_cs.Detours;

public interface CSys : ICppClass
{
	[CppMethodFromVTOffset(11)][CppMethodSelfPtr(false)] public unsafe void ConsoleOutput(AnsiBuffer txt);
}

public unsafe interface ICvar : ICppClass
{
	[CppMethodFromSigScan("vstdlib.dll", "55 8B EC 51 53 57 8B 7D 08 8B D9 8B CF 8B 07 8B")]
	[CppMethodSelfPtr(false)]
	public unsafe void RegisterConCommand(ICvar self, ConCommandBase txt);

}

public unsafe interface ConCommandBase : ICppClass
{

}



internal unsafe class LoadModules : IImplementsDetours
{
	[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
	delegate bool CSys_LoadModules(void* self, void* pAppSystemGroup);
	static CSys_LoadModules? CSys_LoadModules_Original;

	static bool CSys_LoadModules_Detour(void* self, void* pAppSystemGroup) {
		CSys_LoadModules_Original!(self, pAppSystemGroup);

		CSys sys = MarshalCpp.Cast<CSys>(self);
		sys.ConsoleOutput("Hello from .NET land!");

		return true;
	}

	public void SetupWin32(HookEngine engine) {
		CSys_LoadModules_Original = engine.AddDetour<CSys_LoadModules>("dedicated.dll", [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x60, 0x56, 0x8B], new(CSys_LoadModules_Detour));
		// Does this work?
		ICvar cvar = Source.Engine.CreateInterface<ICvar>("vstdlib.dll", CVAR_INTERFACE_VERSION)!;
		ConCommandBase cmd = MarshalCpp.New<ConCommandBase>();
		cvar.RegisterConCommand(cvar, cmd);
	}

	public const string CVAR_INTERFACE_VERSION = "VEngineCvar004";
}