using MinHook;

using System.Runtime.InteropServices;

namespace srcds_cs.Detours;

public unsafe class CSys(void* ptr) : CppClassInterface<CSys.VTable>(ptr)
{
	public void Sleep(int msec) => Table()->Sleep(self, msec);
	public void ConsoleOutput(string @string) {
		using AnsiBuffer str = new(@string);
		Table()->ConsoleOutput(str);
	}
	public struct VTable
	{
		public delegate* unmanaged<void*, void> dtor;

		public delegate* unmanaged<void*, void*, bool> LoadModules;

		public delegate* unmanaged<void*, int, void> Sleep;
		public delegate* unmanaged<void*, sbyte*, bool> GetExecutableName;
		public delegate* unmanaged<void*, int, sbyte*, void> ErrorMessage;

		public delegate* unmanaged<void*, sbyte*, void> WriteStatusText;
		public delegate* unmanaged<void*, int, void> UpdateStatus;

		public delegate* unmanaged<void*, sbyte*, nint> LoadLibrary;
		public delegate* unmanaged<void*, nint, void> FreeLibrary;

		public delegate* unmanaged<void*, bool> CreateConsoleWindow;
		public delegate* unmanaged<void*, void> DestroyConsoleWindow;

		public delegate* unmanaged<sbyte*, void> ConsoleOutput;
	}
}

public unsafe class CDedicatedAppSystemGroup(void* ptr) : CppClassInterface<CDedicatedAppSystemGroup.VTable>(ptr) {
	public struct VTable
	{

	}
}

internal unsafe class LoadModules : IImplementsDetours
{
	[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
	delegate bool CSys_LoadModules(void* self, void* pAppSystemGroup);
	static CSys_LoadModules CSys_LoadModules_Original;

	static bool CSys_LoadModules_Detour(void* self, void* pAppSystemGroup) {
		CSys_LoadModules_Original(self, pAppSystemGroup);

		CSys sys = new(self);
		CDedicatedAppSystemGroup appSystemGroup = new(pAppSystemGroup);

		return true;
	}

	public void SetupWin32(HookEngine engine) {
		CSys_LoadModules_Original = engine.AddDetour<CSys_LoadModules>("dedicated.dll", [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x60, 0x56, 0x8B], new(CSys_LoadModules_Detour));
	}
}