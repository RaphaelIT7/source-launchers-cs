using MinHook;

using Source;
using Source.SDK;

using System.Runtime.InteropServices;

namespace launcher_cs.Detours;

internal unsafe class SoundsapeTest : IImplementsDetours
{
	[UnmanagedFunctionPointer(CallingConvention.Winapi)]
	delegate int CModAppSystemGroup__Main(void* self);
	static CModAppSystemGroup__Main? Original;

	static int Detour(void* self) {
		CCommand cmd = MarshalCpp.Cast<CCommand>(self);
		Console.WriteLine("Detour called!");
		int result = Original!(self); // immediate crash???
		return result;
	}

	public void SetupWin64(HookEngine engine) {
		Original = engine.AddDetour<CModAppSystemGroup__Main>("client.dll", "48 83 EC 28 48 8B 0D 2D 35 76 00 48 8D 15 EE 34 76 00 48 3B CA 75 1A F3 0F 10 05 35 35 76 00", new(Detour));
	}
}