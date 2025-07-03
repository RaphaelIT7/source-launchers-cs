using MinHook;

using Source;

using System.Runtime.InteropServices;

namespace srcds_cs.Detours;

public unsafe class CSourceAppSystemGroup(void* ptr) : CppClassInterface<CSourceAppSystemGroup.VTable>(ptr)
{
	public struct VTable
	{
		public delegate* unmanaged<void*, bool> Create;
		public delegate* unmanaged<void*, bool> PreInit;
		public delegate* unmanaged<void*, bool> PostInit;
		public delegate* unmanaged<void*, int> Main;
		public delegate* unmanaged<void*, void> PostShutdown;
		public delegate* unmanaged<void*, void> Destroy;
	}
}

// todo: move this stuff to source?
internal unsafe class PlugIntoAppFramework : IImplementsDetours
{
	[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
	delegate int CModAppSystemGroup__Main(void* self);
	static CModAppSystemGroup__Main? CModAppSystemGroup__Main_Original;

	static int CModAppSystemGroup__Main_Detour(void* self) {
		int result = CModAppSystemGroup__Main_Original!(self);

		return result;
	}

	public void SetupWin64(HookEngine engine) {
		CModAppSystemGroup__Main_Original = engine.AddDetour<CModAppSystemGroup__Main>("engine.dll", [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x80, 0xB9, 0xA0, 0x00, 0x00, 0x00, 0x00, 0xBB, 0x03, 0x00], new(CModAppSystemGroup__Main_Detour));
	}
}