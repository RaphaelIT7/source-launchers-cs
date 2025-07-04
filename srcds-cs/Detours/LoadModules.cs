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
	[VTMethodOffset(11)][VTMethodSelfPtr(false)] public unsafe void ConsoleOutput(AnsiBuffer txt);
}

public unsafe interface ICvar : ICppClass
{
	// IAppSystem stuff
	[VTMethodOffset(0)][VTMethodSelfPtr(true)] public void Connect(CreateInterfaceFn factory);
	[VTMethodOffset(1)][VTMethodSelfPtr(true)] public void Disconnect();
	[VTMethodOffset(2)][VTMethodSelfPtr(true)] public void* QueryInterface(AnsiBuffer interfaceName);
	[VTMethodOffset(3)][VTMethodSelfPtr(true)] public int Init();
	[VTMethodOffset(4)][VTMethodSelfPtr(true)] public void Shutdown();
													  
	[VTMethodOffset(5)][VTMethodSelfPtr(true)] public int AllocateDLLIdentifier();
	[VTMethodOffset(6)][VTMethodSelfPtr(true)] public void RegisterConCommand(void* pCommandBase);
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
		cvar.RegisterConCommand(null); // lol
	}

	public const string CVAR_INTERFACE_VERSION = "VEngineCvar004";
}