using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace srcds_cs;

public readonly ref struct AnsiBuffer
{
	private readonly nint ptr;
	public AnsiBuffer(string? text) => ptr = Marshal.StringToHGlobalAnsi(text);
	public unsafe AnsiBuffer(void* text) => ptr = (nint)text;
	public unsafe sbyte* AsPointer() => (sbyte*)ptr.ToPointer();
	public void Dispose() => Marshal.FreeHGlobal(ptr);
	public static unsafe implicit operator sbyte*(AnsiBuffer buffer) => buffer.AsPointer();
	public static unsafe string? ToManaged(void* ptr) => Marshal.PtrToStringAnsi(new(ptr));
	public static unsafe string? ToManaged(sbyte* ptr) => Marshal.PtrToStringAnsi(new(ptr));
	public static unsafe string? ToManaged(sbyte* ptr, uint len) => Marshal.PtrToStringAnsi(new(ptr), (int)len);
}


public interface IContainsClassPointer
{
	public void SetPointer(IntPtr pointer);
}

public abstract class CppClassInterface<VTable> : IContainsClassPointer where VTable : unmanaged
{
	/// <summary>
	/// The pointer in C++ unmanaged land to the instance of the interface. Use this in combination with <see cref="Table{VTable}"/> calls to call into
	/// unmanaged methods.
	/// </summary>
	internal unsafe void* self;

	public unsafe bool IsValid() => self != null;
	public unsafe CppClassInterface() {  }
	public unsafe CppClassInterface(void* ptr) => self = ptr;
	public unsafe CppClassInterface(nint ptr) => self = (void*)ptr;

	public unsafe void SetPointer(nint pointer) {
		self = (void*)pointer;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public unsafe VTable* Table() {
		void* vtablePtr = *(void**)self; // assuming `ptr` is a pointer to an object with a vtable
		return (VTable*)vtablePtr;
	}
}