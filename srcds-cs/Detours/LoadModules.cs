using MinHook;

using Source;
using Source.Main;

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
	[CppMethodFromVTOffset(11)]
	[CppMethodSelfPtr(false)] 
	public unsafe void ConsoleOutput(AnsiBuffer txt);
}

public unsafe interface ICvar : ICppClass
{
	[CppMethodFromSigScan(ArchitectureOS.Win32, "vstdlib", "55 8B EC 51 53 57 8B 7D 08 8B D9 8B CF 8B 07 8B")]
	[CppMethodSelfPtr(true)]
	public unsafe void RegisterConCommand(ConCommandBase txt);

	[CppMethodFromSigScan(ArchitectureOS.Win32, "vstdlib", "55 8B EC A1 B4 27 ?? ?? 56 8B F1 A8 01 75 2D")]
	[CppMethodSelfPtr(true)]
	public unsafe void* FindVar(AnsiBuffer var_name);
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

		ICvar cvar = Source.Engine.CreateInterface<ICvar>("vstdlib.dll", CVAR_INTERFACE_VERSION)!;
		void* test = cvar.FindVar("sv_cheats"); // pulling a cvar out of my ass cause I can't find the signature for anything else rn
	}

	public const string CVAR_INTERFACE_VERSION = "VEngineCvar004";
}