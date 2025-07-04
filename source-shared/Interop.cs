using System.Linq.Expressions;
using System.Reflection.Emit;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Source.Main;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Source;

public readonly ref struct AnsiBuffer
{
	private readonly nint ptr;
	public AnsiBuffer(string? text) => ptr = Marshal.StringToHGlobalAnsi(text);
	public unsafe AnsiBuffer(void* text) => ptr = (nint)text;
	public unsafe AnsiBuffer(nint text) => ptr = text;
	public unsafe sbyte* AsPointer() => (sbyte*)ptr.ToPointer();
	public void Dispose() => Marshal.FreeHGlobal(ptr);
	public static unsafe implicit operator sbyte*(AnsiBuffer buffer) => buffer.AsPointer();
	public static unsafe implicit operator AnsiBuffer(string text) => new(text);
	public static unsafe string? ToManaged(void* ptr) => Marshal.PtrToStringAnsi(new(ptr));
	public static unsafe string? ToManaged(sbyte* ptr) => Marshal.PtrToStringAnsi(new(ptr));
	public static unsafe string? ToManaged(sbyte* ptr, uint len) => Marshal.PtrToStringAnsi(new(ptr), (int)len);
}


/// <summary>
/// A C++ class or potential-C++ class. 
/// <br/>
/// If allocated by C++ and interop is from unmanaged -> managed, then use <see cref="MarshalCpp.Cast{T}(nint)"/> to cast the C++ reference to a dynamic interface object.
/// <br/>
/// If you're trying to create an instance of the class that can be passed to C++, use <see cref="MarshalCpp.New{T}()"/>. Note that if in C#, T is expected to be a struct and the return value is T*
/// </summary>
public interface ICppClass : IDisposable
{
	/// <summary>
	/// The unmanaged pointer.
	/// </summary>
	public nint Pointer { get; set; }
	/// <summary>
	/// Designates that <see cref="Pointer"/> cannot be changed. This likely means it came from a nint -> interface cast.
	/// </summary>
	public bool ReadOnly { get; }
}

public static class CppClassExts
{
	public static unsafe Span<T> Span<T>(this ICppClass clss, nint offset, int length) where T : unmanaged {
		return new Span<T>((void*)(clss.Pointer + offset), length);
	}
}

/// <summary>
/// Marks where to read the virtual function.
/// </summary>
/// <param name="offset"></param>
[AttributeUsage(AttributeTargets.Method)]
public class CppMethodFromVTOffsetAttribute(int offset) : Attribute
{
	public int Offset => offset;
}

/// <summary>
/// Marks where to read the function from a sigscan
/// </summary>
/// <param name="offset"></param>
[AttributeUsage(AttributeTargets.Method)]
public class CppMethodFromSigScanAttribute(OperatingFlags arch, string dll, string sig) : Attribute
{
	public OperatingFlags Architecture => arch;
	public string DLL => dll;
	byte?[]? sigscan;
	public byte?[] Signature {
		get {
			if (sigscan != null) return sigscan;

			sigscan = Scanning.Parse(sig);

			return sigscan;
		}
	}
}

/// <summary>
/// Marks if the vtable method has a void* self pointer in its C++ signature. If not, it is excluded from the dynamic generation.
/// The default is that the generator generates it. So be specific if this isn't the case.
/// </summary>
/// <param name="has"></param>
[AttributeUsage(AttributeTargets.Method)]
public class CppMethodSelfPtrAttribute(bool has) : Attribute
{
	public bool HasSelfPointer => has;
}


/// <summary>
/// A class to try implementing object-oriented marshalling between C++ and C# where exports aren't available (manual vtable offsets required in interface methods).
/// There may be some future stuff as well to automatically scan for vtables/vtable functions from a scanning address, similar to how <see cref="Scanning.ScanModuleProc32(string, ReadOnlySpan{byte?})"/>
/// would work but for vtables. Regardless, this is infinitely better than anything else I've had so far to accomplish interop.
/// </summary>
public static unsafe class MarshalCpp
{
	static void pushArray<T>(T value, Span<T> values, ref int index) {
		if (index >= values.Length)
			throw new OverflowException($"array overflowed length ({values.Length})");
		values[index++] = value;
	}

	struct ImplFieldNameToDelegate
	{
		public bool IsNativeCall;
		public FieldBuilder Field;
		public nint Delegate;
	}

	private static Dictionary<Type, Type> intTypeToDynType = [];
	private static AssemblyBuilder? DynAssembly;
	private static ModuleBuilder? DynCppInterfaceFactory;

	public static T New<T>() where T : ICppClass {
		void* ptr = (void*)Marshal.AllocHGlobal(100);
		var generatedType = GetOrCreateDynamicCppType(typeof(T), ptr);
		return (T)Activator.CreateInstance(generatedType, [(nint)ptr])!;
	}

	private static Type GetOrCreateDynamicCppType(Type interfaceType, void* ptr) {
		if (intTypeToDynType.TryGetValue(interfaceType, out Type? generatedType))
			return generatedType;
		if (!interfaceType.IsInterface)
			throw new InvalidOperationException($"typeof(T) == {interfaceType.FullName ?? interfaceType.Name} - was not interface. Cast expects an interface, which it then dynamically creates an object implementing the interface, with methods pointing towards C++ vtable pointers");

		if (DynAssembly == null) {
			AssemblyName assemblyName = new("MarshalCpp");
			AssemblyBuilder assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
			DynAssembly = assemblyBuilder;
		}

		AssemblyBuilder dynAssembly = DynAssembly;

		if (DynCppInterfaceFactory == null) {
			var moduleBuilder = dynAssembly.DefineDynamicModule("DynCppInterfaceFactory");
			DynCppInterfaceFactory = moduleBuilder;
		}

		ModuleBuilder dynModule = DynCppInterfaceFactory;

		string typeName = "Dynamic" + interfaceType.Name;
		TypeBuilder typeBuilder = dynModule.DefineType(typeName, TypeAttributes.Public, null, [interfaceType]);

		MethodInfo[] methods = interfaceType.GetMethods();
		FieldBuilder pointerField = typeBuilder.DefineField("_pointer", typeof(nint), FieldAttributes.Private);
		foreach (var method in methods) {
			int? vtableOffsetN = method.GetCustomAttribute<CppMethodFromVTOffsetAttribute>()?.Offset;
			bool vt_useSelfPtr = method.GetCustomAttribute<CppMethodSelfPtrAttribute>()?.HasSelfPointer ?? true;

			nint cppMethod;
			if (vtableOffsetN == null) {
				var sigAttr = method.GetCustomAttributes<CppMethodFromSigScanAttribute>().Where(x => x.Architecture == Program.Architecture).FirstOrDefault();
				if (sigAttr == null) {
					genInterfaceStub(typeBuilder, method);
					continue;
				}

				cppMethod = (int)Scanning.ScanModuleProc32(sigAttr.DLL, sigAttr.Signature);
			}
			else {
				nint vtablePtr = *(nint*)ptr;
				nint* vtable = (nint*)vtablePtr;

				int vt_offset = vtableOffsetN.Value;

				cppMethod = vtable[vt_offset];
			}

			// Generate the delegate type
			int typeIndex = 0;
			int pTypeIndex = 0;
			ParameterInfo[] parameters = method.GetParameters();

			// Allocate Type[] with length of Parameters + SelfPtr (if needed) + 1 for return type since
			// this builds the managed delegate type
			Type[] types = new Type[parameters.Length + (vt_useSelfPtr ? 1 : 0) + 1];
			Type[] justParams = new Type[parameters.Length + (vt_useSelfPtr ? 1 : 0)];
			// Some calls don't take in a void*. I believe this is a compiler optimization that discards unused parameters?
			// Entirely guessing but regardless theres not a good enough way to automatically discern it hence this
			if (vt_useSelfPtr) {
				pushArray(typeof(nint), types, ref typeIndex);
				pushArray(typeof(nint), justParams, ref pTypeIndex);
			}

			// Figure out how to marshal any other types in the parameter list.
			foreach (ParameterInfo param in parameters) {
				pushArray(getMarshalType(param), types, ref typeIndex);
				pushArray(getMarshalType(param), justParams, ref pTypeIndex);
			}

			// The return type is always at the end of the delegate, even if void
			// Since the return of a method is also a ParameterInfo that can be passed into getMarshalType again
			// rather than be reused
			pushArray(getMarshalType(method.ReturnParameter), types, ref typeIndex);

			// Rewrite the dynamic object's method so it implements the interface as expected
			genInterfaceNative(typeBuilder, method, types, cppMethod, pointerField, vt_useSelfPtr);
		}
		// Setup Pointer. Since this comes from C++ land lets not let the user change it without throwing
		{

			PropertyBuilder pointerBuilder = typeBuilder.DefineProperty(
				"Pointer",
				PropertyAttributes.None,
				typeof(nint),
				null
			);

			MethodBuilder getterMethod = typeBuilder.DefineMethod(
				"get_Pointer",
				MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
				typeof(nint),
				Type.EmptyTypes
			);
			ILGenerator getterIL = getterMethod.GetILGenerator();
			getterIL.Emit(OpCodes.Ldarg_0);
			getterIL.Emit(OpCodes.Ldfld, pointerField);
			getterIL.Emit(OpCodes.Ret);

			MethodBuilder setterMethod = typeBuilder.DefineMethod(
				"set_Pointer",
				MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
				null,
				[typeof(nint)]
			);
			ILGenerator setterIL = setterMethod.GetILGenerator();
			setterIL.Emit(OpCodes.Ldstr, $"Setting Pointer is not allowed when coming from a C++ dynamic cast -> interface. To avoid this problem, check {nameof(ICppClass.ReadOnly)} before trying to set the pointer.");
			setterIL.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
			setterIL.Emit(OpCodes.Throw);

			pointerBuilder.SetGetMethod(getterMethod);
			pointerBuilder.SetSetMethod(setterMethod);

			PropertyInfo interfaceProp = typeof(ICppClass).GetProperty("Pointer")!;
			typeBuilder.DefineMethodOverride(getterMethod, interfaceProp.GetMethod!);
			typeBuilder.DefineMethodOverride(setterMethod, interfaceProp.SetMethod!);
		}
		// Set ReadOnly to true
		{
			FieldBuilder readOnlyField = typeBuilder.DefineField("_readonly", typeof(bool), FieldAttributes.Private);
			PropertyBuilder readOnlyBuilder = typeBuilder.DefineProperty(
				"ReadOnly",
				PropertyAttributes.None,
				typeof(bool),
				null
			);

			MethodBuilder readOnlyGetterMethod = typeBuilder.DefineMethod(
				"get_ReadOnly",
				MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
				typeof(bool),
				Type.EmptyTypes
			);
			ILGenerator getterIL = readOnlyGetterMethod.GetILGenerator();
			getterIL.Emit(OpCodes.Ldarg_0);
			getterIL.Emit(OpCodes.Ldfld, readOnlyField);
			getterIL.Emit(OpCodes.Ret);

			readOnlyBuilder.SetGetMethod(readOnlyGetterMethod);
			PropertyInfo interfaceProp = typeof(ICppClass).GetProperty("ReadOnly")!;
			typeBuilder.DefineMethodOverride(readOnlyGetterMethod, interfaceProp.GetMethod!);
		}
		// Cast the typeBuilder dynamic type to nint
		{
			MethodBuilder implicitOp = typeBuilder.DefineMethod(
				"op_Implicit",
				MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
				typeof(nint),         
				new[] { typeBuilder }
			);
			// Mark as 'implicit operator'

			ILGenerator il = implicitOp.GetILGenerator();

			il.Emit(OpCodes.Ldarg_0);                     
			il.Emit(OpCodes.Ldfld, pointerField);      
			il.Emit(OpCodes.Ret);
		}
		// Define the constructor
		{
			ConstructorBuilder ctorBuilder = typeBuilder.DefineConstructor(
				MethodAttributes.Public,
				CallingConventions.Standard,
				[typeof(nint)] // single pointer constructor param
			);

			ILGenerator il = ctorBuilder.GetILGenerator();

			// Call base constructor: base..ctor()
			il.Emit(OpCodes.Ldarg_0); // Load "this"
			ConstructorInfo objectCtor = typeof(object).GetConstructor(Type.EmptyTypes)!;
			il.Emit(OpCodes.Call, objectCtor);

			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldarg_1);
			FieldInfo ptrField = pointerField;
			il.Emit(OpCodes.Stfld, ptrField);

			il.Emit(OpCodes.Ret);
		}

		// Now trick Dispose() by just doing nothing
		// I figured it would be better to encourage using semantics rather than
		// making Dispose() throw an error
		{
			MethodBuilder disposeMethod = typeBuilder.DefineMethod(
				"Dispose",
				MethodAttributes.Public | MethodAttributes.Virtual,
				typeof(void),
				Type.EmptyTypes
			);

			ILGenerator il = disposeMethod.GetILGenerator();
			il.Emit(OpCodes.Ret);

			typeBuilder.DefineMethodOverride(disposeMethod, typeof(IDisposable).GetMethod("Dispose")!);
		}

		generatedType = typeBuilder.CreateType();

		intTypeToDynType[interfaceType] = generatedType;
		return generatedType;
	}

	public static T Cast<T>(nint ptr) where T : ICppClass => Cast<T>((void*)ptr);
	public static T Cast<T>(void* ptr) where T : ICppClass {
		var generatedType = GetOrCreateDynamicCppType(typeof(T), ptr);
		return (T)Activator.CreateInstance(generatedType, [(nint)ptr])!;
	}

	private static Type getMarshalType(ParameterInfo param) {
		if (param.ParameterType.IsAssignableTo(typeof(ICppClass)))
			return typeof(nint);

		return param.ParameterType;
	}
	private static OpCode getNumericConversionOpcode(Type from, Type to) {
		if (to == typeof(byte)) return OpCodes.Conv_U1;
		if (to == typeof(sbyte)) return OpCodes.Conv_I1;
		if (to == typeof(short)) return OpCodes.Conv_I2;
		if (to == typeof(ushort)) return OpCodes.Conv_U2;
		if (to == typeof(int)) return OpCodes.Conv_I4;
		if (to == typeof(uint)) return OpCodes.Conv_U4;
		if (to == typeof(long)) return OpCodes.Conv_I8;
		if (to == typeof(ulong)) return OpCodes.Conv_U8;
		if (to == typeof(float)) return OpCodes.Conv_R4;
		if (to == typeof(double)) return OpCodes.Conv_R8;
		if (to == typeof(IntPtr)) return OpCodes.Conv_I;
		if (to == typeof(UIntPtr)) return OpCodes.Conv_U;

		throw new NotSupportedException($"Cannot implicitly convert from {from} to {to}");
	}


	private static void genInterfaceNative(TypeBuilder typeBuilder, MethodInfo method, Type[] types, nint nativePtr, FieldBuilder _pointer, bool selfPtr) {
		var mparams = Array.ConvertAll(method.GetParameters(), p => p.ParameterType);
		var methodBuilder = typeBuilder.DefineMethod(
				method.Name,
				MethodAttributes.Public | MethodAttributes.Virtual,
				method.ReturnType,
				mparams
			);

		var il = methodBuilder.GetILGenerator();

		if (selfPtr) {
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldfld, _pointer);
		}
		int start = selfPtr ? 1 : 0;
		for (int i = start; i < types.Length - 1; i++) {
			Type expectedNativeType = types[i];
			Type providedManagedType = mparams[i - start];
			il.Emit(OpCodes.Ldarg, i);
			// Check against the index in types
			// if we need to emit an implicit cast?
			// we need to cast what's at Ldarg (given its probably providedManagedType) to expectedNativeType at runtime here
			if (expectedNativeType != providedManagedType) {
				if (expectedNativeType.IsValueType && providedManagedType.IsValueType)
					// Integer or float widening/narrowing
					il.Emit(getNumericConversionOpcode(providedManagedType, expectedNativeType));
				else if (!expectedNativeType.IsValueType && !providedManagedType.IsValueType)
					// Reference type cast
					il.Emit(OpCodes.Castclass, expectedNativeType);
				else if(providedManagedType.IsAssignableTo(typeof(ICppClass)) && expectedNativeType == typeof(nint)) {
					var prop = typeof(ICppClass).GetProperty("Pointer");
					var getter = prop!.GetGetMethod();
					il.Emit(OpCodes.Callvirt, getter);
				}
				else if (expectedNativeType.IsValueType && !providedManagedType.IsValueType)
					// Unbox to value type
					il.Emit(OpCodes.Unbox_Any, expectedNativeType);
				else if (!expectedNativeType.IsValueType && providedManagedType.IsValueType)
					// Box the value type
					il.Emit(OpCodes.Box, providedManagedType);
				else
					throw new InvalidOperationException($"Unsupported cast from {providedManagedType} to {expectedNativeType}");

			}
		}

		il.Emit(OpCodes.Ldc_I8, (long)nativePtr);
		il.Emit(OpCodes.Conv_I);

		il.EmitCalli(OpCodes.Calli, CallingConvention.ThisCall, types[types.Length - 1], types[..(types.Length - 1)]);

		il.Emit(OpCodes.Ret);

		typeBuilder.DefineMethodOverride(methodBuilder, method);
	}

	private static void genInterfaceStub(TypeBuilder typeBuilder, MethodInfo method) {
		var methodBuilder = typeBuilder.DefineMethod(
				method.Name,
				MethodAttributes.Public | MethodAttributes.Virtual,
				method.ReturnType,
				Array.ConvertAll(method.GetParameters(), p => p.ParameterType)
			);

		var il = methodBuilder.GetILGenerator();

		if (method.ReturnType != typeof(void)) {
			if (method.ReturnType.IsValueType) {
				var local = il.DeclareLocal(method.ReturnType);
				il.Emit(OpCodes.Ldloca_S, local);
				il.Emit(OpCodes.Initobj, method.ReturnType);
				il.Emit(OpCodes.Ldloc_0);
			}
			else {
				il.Emit(OpCodes.Ldnull);
			}
		}
		il.Emit(OpCodes.Ret);

		typeBuilder.DefineMethodOverride(methodBuilder, method);
	}
}

// ------------------------------------------------------------------------------- //
// Source-specific Interop
//     This stuff interfaces with AppFramework, etc.
// ------------------------------------------------------------------------------- //

public delegate IntPtr CreateInterfaceFn(string name, ref int code);
public unsafe delegate void WalkInterfaceFn(string name, nint createFn);

public unsafe struct InterfaceReg
{
	public void* m_CreateFn;
	public byte* m_pName;
	public InterfaceReg* m_pNext;
}

/// <summary>
/// Engine-specific interop.
/// </summary>
public static class Engine
{
	public static unsafe T? CreateInterface<T>(string moduleName, string interfaceName, bool inlinedCall = true) where T : ICppClass
		=> CreateInterface(moduleName, interfaceName, out T? obj, inlinedCall) ? obj : default;
	/// <summary>
	/// Create or get an interface from a Source Engine process. <typeparamref name="T"/> must be a <see cref="ICppClass"/> of some kind. It will be
	/// cast by <see cref="MemoryMarshal"/> automatically from the address CreateInterface provides.
	/// </summary>
	public static unsafe bool CreateInterface<T>(
		string moduleName,        // example: engine.dll
		string interfaceName,      // example: VModelInfoServer004, or whatever
		[NotNullWhen(true)] out T? interfaceObj,
		bool isInlinedCall = true
	) where T : ICppClass {
#if _WIN32
		IntPtr module = Win32.GetModuleHandle(moduleName);

		if (module == IntPtr.Zero) {
			Tier0.Plat_MessageBox("CreateInterface issue", $"Failed to get a pointer to the module '{moduleName}'.");
			interfaceObj = default;
			return false;
		}

		IntPtr createInterfacePtr = Win32.GetProcAddress(module, "CreateInterface");
		if (createInterfacePtr == IntPtr.Zero) {
			Tier0.Plat_MessageBox("CreateInterface issue", $"Failed to get a pointer to the CreateInterface procedure.");
			interfaceObj = default;
			return false;
		}

		CreateInterfaceFn createInterface = Marshal.GetDelegateForFunctionPointer<CreateInterfaceFn>(createInterfacePtr);

		int retcode = 0;
		IntPtr interfacePtr = createInterface(interfaceName, ref retcode);
		if (interfacePtr == IntPtr.Zero) {
			Tier0.Plat_MessageBox("CreateInterface issue", $"CreateInterface failed to return an object. Retcode: {retcode}");
			interfaceObj = default;
			return false;
		}

		interfaceObj = MarshalCpp.Cast<T>(interfacePtr);
		return true;
	}
	// Untested rewrite to not use ReadProcessMemory. Probably doesn't work.
	private static unsafe bool __WalkInterfaces(nint createInterfacePtr, WalkInterfaceFn walker) {
		if (Program.IsX64) {
			// TODO: this entire thing is only WinX64 compatible...
			// Retrieves a pointer to x64 instruction mov rbx, QWORD
			// the QWORD decompiles to an if statement, which should be checking if s_pInterfaceRegs is null
			IntPtr sInterfaceRegsInstr = createInterfacePtr + 15;
			// dump out 7 bytes to get the instruction
			Span<byte> instr = new((void*)(Process.GetCurrentProcess().Handle + sInterfaceRegsInstr), 7);
			// sanity check
			Debug.Assert(instr[0] == 0x48 && instr[1] == 0x8B && instr[2] == 0x1D, "unexpected instruction format, expected rip-relative MOV");
			int ripOffset = BitConverter.ToInt32(instr[3..]);
			nint absAddress = sInterfaceRegsInstr + 7 + ripOffset;
			Span<byte> regPtrBytes = new((void*)(Process.GetCurrentProcess().Handle + absAddress), IntPtr.Size);
			IntPtr interfaceRegPtr = BitConverter.ToInt32(regPtrBytes);
			unsafe {
				InterfaceReg* firstReg = (InterfaceReg*)interfaceRegPtr;
				while (firstReg != null) {
					string managedName = Marshal.PtrToStringAnsi((nint)firstReg->m_pName) ?? "";
					walker(managedName, (nint)firstReg->m_CreateFn);
					firstReg = firstReg->m_pNext;
				}
				return true;
			}
		}
		else {
			throw new PlatformNotSupportedException("Cannot fully do this on 32-bit yet.");
		}
	}

	/// <summary>
	/// Makes an attempt to iterate over the interface list of a tier1+ module.
	/// </summary>
	/// <param name="moduleName"></param>
	/// <returns></returns>
	public static bool WalkInterfaces(string moduleName, WalkInterfaceFn walker) {
		IntPtr module = Win32.GetModuleHandle(moduleName);
		if (module == IntPtr.Zero) {
			Tier0.Plat_MessageBox("CreateInterface issue", $"Failed to get a pointer to the module '{moduleName}'."); return false;
		}

		IntPtr createInterfacePtr = Win32.GetProcAddress(module, "CreateInterface");
		if (createInterfacePtr == IntPtr.Zero) {
			Tier0.Plat_MessageBox("CreateInterface issue", $"Failed to get a pointer to the CreateInterface procedure."); return false;
		}

		return __WalkInterfaces(createInterfacePtr, walker);
	}
	/// <summary>
	/// Scans every loaded porcess module for a CreateInterface fn. If it exists, attempts a call to <see cref="WalkInterfaces"/>.
	/// <br></br>
	/// Unlike the by-name module-specific function equiv., this prefixes the module name before calling <see cref="WalkInterfaceFn"/>.
	/// </summary>
	/// <param name="walker"></param>
	/// <returns></returns>
	public static void WalkInterfaces(WalkInterfaceFn walker) {
		foreach (ProcessModule module in Process.GetCurrentProcess().Modules) {
			IntPtr createInterfacePtr = Win32.GetProcAddress(module.BaseAddress, "CreateInterface");
			if (createInterfacePtr == IntPtr.Zero)
				continue;

			// add module name here
			if (!__WalkInterfaces(createInterfacePtr, (name, ptr) => walker($"{module.ModuleName} > {name}", ptr)))
				Console.WriteLine($"ERROR: {module.ModuleName} failed to produce a s_pInterfaceRegs linked list");
		}
	}
#else
#error Please implement the platform.
#endif
}


/// <summary>
/// Tier0 interop. 
/// </summary>
public static unsafe partial class Tier0
{
	private const string dllname = "tier0";
	private const UnmanagedType utf8 = UnmanagedType.LPUTF8Str;

	// todo: figure out how much changes between CStrike-15 style and Obsoletium-style tier0.
	// x86-64 gmod has a lot of cstrike slapped onto it vs main branch it seems
	// also do this for vstdlib detour attributes
	#region Shared Tier0 Exports
	[DllImport(dllname)] public static extern void Plat_MessageBox([MarshalAs(utf8)] string title, [MarshalAs(utf8)] string message);
	#endregion

#if !CSTRIKE_TIER0
	[DllImport(dllname)] public static extern void ConMsg([MarshalAs(utf8)] string msg);
	[DllImport(dllname)] public static extern void Error([MarshalAs(utf8)] string msg);
	[DllImport(dllname)] public static extern void GetCurrentDate(int* pDay, int* pMonth, int* pYear);
	public static void GetCurrentDate(out int pDay, out int pMonth, out int pYear) {
		int day, month, year;
		GetCurrentDate(&day, &month, &year);
		pDay = day;
		pMonth = month;
		pYear = year;
	}
	[DllImport(dllname)] public static extern void Msg([MarshalAs(utf8)] string msg);
	[DllImport(dllname)] public static extern void DevMsg([MarshalAs(utf8)] string msg);
	[DllImport(dllname)] public static extern void ConDMsg([MarshalAs(utf8)] string msg);
	[DllImport(dllname)] public static extern long Plat_CycleTime();
	[DllImport(dllname)] public static extern void Plat_ExitProcess(int nCode);
	[DllImport(dllname)] public static extern void Plat_ExitProcessWithError(int nCode, bool generateMinidump);
	[DllImport(dllname)] public static extern double Plat_FloatTime();
	[DllImport(dllname)] public static extern void Plat_GetModuleFilename(sbyte* pOut, int maxBytes);
	[DllImport(dllname)] public static extern uint Plat_DebugString(string psz);
	[DllImport(dllname)] public static extern uint Plat_MSTime();
	[DllImport(dllname)] public static extern ulong Plat_USTime();
	[DllImport(dllname)] public static extern bool Plat_IsInBenchmarkMode();
	[DllImport(dllname)] public static extern bool Plat_IsInDebugSession();
	[DllImport(dllname)] public static extern bool Plat_IsUserAnAdmin();
	[DllImport(dllname)] public static extern void Plat_SetBenchmarkMode(bool bBenchmark);
	[DllImport(dllname)] public static extern void DoNewAssertDialog([MarshalAs(utf8)] string filename, int line, [MarshalAs(utf8)] string expression);
#endif
}