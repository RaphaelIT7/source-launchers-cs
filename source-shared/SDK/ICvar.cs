using Source.Main;

using System.Runtime.InteropServices;

namespace Source.SDK;

public unsafe interface ICvar : ICppClass
{
#if !CSTRIKE_VSTDLIB
	[CppMethodFromSigScan(OperatingFlags.Win32, "vstdlib", "55 8B EC 51 53 57 8B 7D 08 8B D9 8B CF 8B 07 8B")]
	public unsafe void RegisterConCommand(ConCommandBase cmd);

	[CppMethodFromSigScan(OperatingFlags.Win32, "vstdlib", "55 8B EC 8B 01 56 57 FF 50 44 8B F0")]
	public unsafe ConCommandBase FindCommandBase(NativeString var_name);
	[CppMethodFromSigScan(OperatingFlags.Win32, "vstdlib", "55 8B EC 8B 01 56 FF 75 08 FF 50 28 8B F0")]
	public unsafe ConCommand FindCommand(NativeString var_name);
	[CppMethodFromSigScan(OperatingFlags.Win32, "vstdlib", "55 8B EC A1 B4 27 ?? ?? 56 8B F1 A8 01 75 2D")]
	public unsafe ConCommandBase FindVar(NativeString var_name);
#endif
}

public interface ConCommandBase : ICppClass
{
	[CppVTable(0)] public nint ConCommandBase_VTable { get; set; }
	[CppField(1)] public ConCommandBase Next { get; set; }
	[CppField(2)] public bool Registered { get; set; }
	[CppField(3)] public NativeString Name { get; set; }
	[CppField(4)] public NativeString HelpString { get; set; }
	[CppField(5)] public int Flags { get; set; }
}

public interface CCommand : ICppClass {
	public const int COMMAND_MAX_ARGC = 64;
	public const int COMMAND_MAX_LENGTH = 512;
	[CppField(0)] public int ArgCount { get; set; }
	[CppField(1)] public nint ArgV0Size { get; set; }
	[CppField(2, Size: COMMAND_MAX_LENGTH)] public NativeString ArgSBuffer { get; set; }
	[CppField(3, Size: COMMAND_MAX_LENGTH)] public NativeString ArgVBuffer { get; set; }
	[CppField(4, Size: COMMAND_MAX_ARGC)] public NativeArray<NativeString> PPArgV { get; set; }
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public unsafe delegate void FnCommandCallback_t(void* command);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public unsafe delegate void FnCommandCompletionCallback(NativeString partial); // todo

[CppInherit(typeof(ConCommandBase))]
public interface ConCommand : ConCommandBase
{
	[CppField(0)] public FnCommandCallback_t CommandCallback { get; set; }
	[CppField(1)] public FnCommandCompletionCallback CommandCompletionCallback { get; set; }
	[CppField(2), FieldWidth(1)] public bool HasCompletionCallback { get; set; }
	[CppField(3), FieldWidth(1)] public bool UsingNewCommandCallback { get; set; }
	[CppField(4), FieldWidth(1)] public bool UsingCommandCallbackInterface { get; set; }
}