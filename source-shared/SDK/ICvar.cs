using Source.Main;

namespace Source.SDK;

public unsafe interface ICvar : ICppClass
{
#if !CSTRIKE_VSTDLIB
	[CppMethodFromSigScan(OperatingFlags.Win32, "vstdlib", "55 8B EC 51 53 57 8B 7D 08 8B D9 8B CF 8B 07 8B")]
	public unsafe void RegisterConCommand(ConCommandBase cmd);

	[CppMethodFromSigScan(OperatingFlags.Win32, "vstdlib", "55 8B EC 8B 01 56 57 FF 50 44 8B F0")]
	public unsafe ConCommandBase FindCommandBase(AnsiBuffer var_name);
	[CppMethodFromSigScan(OperatingFlags.Win32, "vstdlib", "55 8B EC A1 B4 27 ?? ?? 56 8B F1 A8 01 75 2D")]
	public unsafe ConCommandBase FindVar(AnsiBuffer var_name);
#endif
}

public interface ConCommandBase : ICppClass
{
	[CppVTable(0, OperatingFlags.Win32, "vstdlib", "")] public nint ConCommandBase_VTable { get; set; }
	[CppField(1)] public ConCommandBase Next { get; set; }
	[CppField(2)] public bool Registered { get; set; }
	[CppField(3)] public AnsiBuffer Name { get; set; }
	[CppField(4)] public AnsiBuffer HelpString { get; set; }
	[CppField(5)] public int Flags { get; set; }
}

public interface ConCommand : ConCommandBase {
	[CppField<ConCommandBase>(0)][CppWidth(1)] public bool HasCompletionCallback
}