using MinHook;

using Source;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace srcds_cs.Detours;

// todo: move this stuff to source?
using LoggingChannelID_t = int;

public enum LoggingChannelFlags_t
{
	/// <summary>
	/// Indicates that the spew is only relevant to interactive consoles.
	/// </summary>
	ConsoleOnly = 0x00000001,

	/// <summary>
	/// Indicates that spew should not be echoed to any output devices.
	/// A suitable logging listener must be registered which respects this flag 
	/// (e.g. a file logger).
	/// </summary>
	DoNotEcho = 0x00000002,
}

public enum LoggingSeverity_t
{
	/// <summary>
	/// An informative logging message.
	/// </summary>
	Message = 0,

	/// <summary>
	/// A warning, typically non-fatal
	/// </summary>
	Warning = 1,

	/// <summary>
	/// A message caused by an Assert**() operation.
	/// </summary>
	Assert = 2,

	/// <summary>
	/// An error, typically fatal/unrecoverable.
	/// </summary>
	Error = 3,

	/// <summary>
	/// A placeholder level, higher than any legal value.
	/// Not a real severity value!
	/// </summary>
	Highest = 4,
}

public struct Color
{
	public byte R, G, B, A;
}

public struct LoggingContext_t
{
	public LoggingChannelID_t ChannelID;
	public LoggingChannelFlags_t Flags;
	public LoggingSeverity_t Severity;
	public Color Color;
}

// Wow! I hate this!

public unsafe class ManagedLoggingListener : CppClassInterface<ManagedLoggingListener.VTable>
{
	public struct VTable
	{
		public delegate* unmanaged<void*, LoggingContext_t*, sbyte*, void> Log;
	}

	private static VTable _vtable;
	private static delegate* unmanaged<void*, LoggingContext_t*, sbyte*, void> _logPtr;

	private static bool _vtableInitialized = false;

	public ManagedLoggingListener() {
		if (!_vtableInitialized) {
			_logPtr = (delegate* unmanaged<void*, LoggingContext_t*, sbyte*, void>)&LogImpl;
			_vtable.Log = _logPtr;
			_vtableInitialized = true;
		}

		nint vtableMem = Marshal.AllocHGlobal(sizeof(VTable));
		Unsafe.CopyBlockUnaligned((void*)vtableMem, Unsafe.AsPointer(ref _vtable), (uint)sizeof(VTable));

		nint selfMem = Marshal.AllocHGlobal(IntPtr.Size);
		*(nint*)selfMem = vtableMem;

		self = (void*)selfMem;
	}

	[UnmanagedCallersOnly]
	private static void LogImpl(void* @this, LoggingContext_t* ctx, sbyte* message) {
		string managedMessage = Marshal.PtrToStringAnsi((nint)message) ?? "<null>";
		Console.WriteLine($"[Log] {managedMessage}");
	}
}

internal unsafe class PlugIntoSpew : IImplementsDetours
{
	[DllImport("tier0", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
	static extern void LoggingSystem_RegisterLoggingListener(void* listener);
	[DllImport("tier0", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
	static extern void LoggingSystem_SetChannelSpewLevel(LoggingChannelID_t channelID, LoggingSeverity_t minimumSeverity);

	public void SetupWin64(HookEngine engine) {
		for (int i = 0; i < 15; i++) {
			LoggingSystem_SetChannelSpewLevel(i, LoggingSeverity_t.Message);
		}
		// ManagedLoggingListener listener = new ManagedLoggingListener();
		// LoggingSystem_RegisterLoggingListener((void*)listener.GetPointer());
	}
}