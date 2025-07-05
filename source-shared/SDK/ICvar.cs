using Source.Main;

namespace Source.SDK;

public unsafe interface ICvar : ICppClass
{
#if !CSTRIKE_VSTDLIB
	[CppMethodFromSigScan(OperatingFlags.Win32, "vstdlib", "55 8B EC 51 53 57 8B 7D 08 8B D9 8B CF 8B 07 8B")]
#endif
	public unsafe void RegisterConCommand(ConCommandBase txt);

#if !CSTRIKE_VSTDLIB
	[CppMethodFromSigScan(OperatingFlags.Win32, "vstdlib", "55 8B EC A1 B4 27 ?? ?? 56 8B F1 A8 01 75 2D")]
#endif
	public unsafe void* FindVar(AnsiBuffer var_name);
}

public unsafe interface ConCommandBase : ICppClass
{
	[CppVTable(0, OperatingFlags.Win32, "vstdlib", "")] public nint ConCommandBase_VTable { get; set; }
	[CppField(1)] public ConCommandBase Next { get; set; }
	[CppField(2)] public bool Registered { get; set; }
	[CppField(3)] public AnsiBuffer Name { get; set; }
	[CppField(4)] public AnsiBuffer HelpString { get; set; }
	[CppField(5)] public int Flags { get; set; }
}