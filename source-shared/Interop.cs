using System.Linq.Expressions;
using System.Reflection.Emit;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Source;

public readonly ref struct AnsiBuffer
{
	private readonly nint ptr;
	public AnsiBuffer(string? text) => ptr = Marshal.StringToHGlobalAnsi(text);
	public unsafe AnsiBuffer(void* text) => ptr = (nint)text;
	public unsafe sbyte* AsPointer() => (sbyte*)ptr.ToPointer();
	public void Dispose() => Marshal.FreeHGlobal(ptr);
	public static unsafe implicit operator sbyte*(AnsiBuffer buffer) => buffer.AsPointer();
	public static unsafe implicit operator AnsiBuffer(string text) => new(text);
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
	public unsafe CppClassInterface() { }
	public unsafe CppClassInterface(void* ptr) => self = ptr;
	public unsafe CppClassInterface(nint ptr) => self = (void*)ptr;

	public unsafe nint GetPointer() => (nint)self;
	public unsafe void SetPointer(nint pointer) {
		self = (void*)pointer;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public unsafe VTable* Table() {
		void* vtablePtr = *(void**)self; // assuming `ptr` is a pointer to an object with a vtable
		return (VTable*)vtablePtr;
	}
}


// TODO: deprecate everything above.

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


/// <summary>
/// Marks where to read the virtual function.
/// </summary>
/// <param name="offset"></param>
[AttributeUsage(AttributeTargets.Method)]
public class VTMethodOffsetAttribute(int offset) : Attribute
{
	public int Offset => offset;
}

/// <summary>
/// Marks if the vtable method has a void* self pointer in its C++ signature. If not, it is excluded from the dynamic generation.
/// The default is that the generator generates it. So be specific if this isn't the case.
/// </summary>
/// <param name="has"></param>
[AttributeUsage(AttributeTargets.Method)]
public class VTMethodSelfPtrAttribute(bool has) : Attribute
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

	struct ImplFieldNameToDelegate {
		public bool IsNativeCall;
		public FieldBuilder Field;
		public Delegate Delegate;
	}

	private static Dictionary<Type, Type> intTypeToDynType = [];
	private static AssemblyBuilder? DynAssembly;
	private static ModuleBuilder? DynCppInterfaceFactory;
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

		nint vtablePtr = *(nint*)ptr;
		nint* vtable = (nint*)vtablePtr;

		MethodInfo[] methods = interfaceType.GetMethods();
		ImplFieldNameToDelegate[] info = new ImplFieldNameToDelegate[methods.Length];
		int infoIndex = 0;

		foreach (var method in methods) {
			int? vtableOffsetN = method.GetCustomAttribute<VTMethodOffsetAttribute>()?.Offset;
			if (vtableOffsetN == null) {
				genInterfaceStub(typeBuilder, method);
				continue;
			}

			int vt_offset = vtableOffsetN.Value;
			bool vt_useSelfPtr = method.GetCustomAttribute<VTMethodSelfPtrAttribute>()?.HasSelfPointer ?? true;

			nint vt_method = vtable[vt_offset];

			// Generate the delegate type
			int typeIndex = 0;
			int pTypeIndex = 0;
			ParameterInfo[] parameters = method.GetParameters();

			// Allocate Type[] with length of Parameters + SelfPtr (if needed) + 1 for return type since
			// this builds the managed delegate type
			Type[] types = new Type[parameters.Length + (vt_useSelfPtr ? 1 : 0) + 1];
			Type[] justParams = new Type[parameters.Length];
			// Some calls don't take in a void*. I believe this is a compiler optimization that discards unused parameters?
			// Entirely guessing but regardless theres not a good enough way to automatically discern it hence this
			if (vt_useSelfPtr) pushArray(typeof(void*), types, ref typeIndex);

			// Figure out how to marshal any other types in the parameter list.
			foreach (ParameterInfo param in parameters) {
				pushArray(getMarshalType(param), types, ref typeIndex);
				pushArray(getMarshalType(param), justParams, ref pTypeIndex);
			}

			// The return type is always at the end of the delegate, even if void
			// Since the return of a method is also a ParameterInfo that can be passed into getMarshalType again
			// rather than be reused
			pushArray(getMarshalType(method.ReturnParameter), types, ref typeIndex);

			// Build the managed delegate type ...
			Type delegateType = Expression.GetDelegateType(types);
			// ... then cast the function pointer to it ...
			Delegate delegateInstance = Marshal.GetDelegateForFunctionPointer(vt_method, delegateType);
			// ... then rewrite the dynamic object's method so it implements the interface as expected.
			genInterfaceNativePtr(typeBuilder, method, delegateType, delegateInstance, justParams, vt_useSelfPtr, info, ref infoIndex);
		}
		// Setup Pointer. Since this comes from C++ land lets not let the user change it without throwing
		{
			FieldBuilder pointerField = typeBuilder.DefineField("_pointer", typeof(nint), FieldAttributes.Private);

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
		// Define the constructor
		{
			ConstructorBuilder ctorBuilder = typeBuilder.DefineConstructor(
				MethodAttributes.Public,
				CallingConventions.Standard,
				Type.EmptyTypes // no parameters
			);

			ILGenerator il = ctorBuilder.GetILGenerator();

			// Call base constructor: base..ctor()
			il.Emit(OpCodes.Ldarg_0); // Load "this"
			ConstructorInfo objectCtor = typeof(object).GetConstructor(Type.EmptyTypes)!;
			il.Emit(OpCodes.Call, objectCtor);

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
			il.Emit(OpCodes.Ret); // Just return immediately — no-op

			// Implement IDisposable.Dispose
			typeBuilder.DefineMethodOverride(disposeMethod, typeof(IDisposable).GetMethod("Dispose")!);
		}

		generatedType = typeBuilder.CreateType();

		// We need to update the static backing fields from before (info)
		{
			for (int i = 0; i < info.Length; i++) {
				ref ImplFieldNameToDelegate fn2del = ref info[i];
				if (!fn2del.IsNativeCall) continue;

				generatedType.GetField(fn2del.Field.Name, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, fn2del.Delegate);
			}
		}

		intTypeToDynType[interfaceType] = generatedType;
		return generatedType;
	}

	public static T Cast<T>(nint ptr) where T : ICppClass => Cast<T>((void*)ptr);
	public static T Cast<T>(void* ptr) where T : ICppClass {
		var generatedType = GetOrCreateDynamicCppType(typeof(T), ptr);
		return (T)Activator.CreateInstance(generatedType)!;
	}

	private static Type getMarshalType(ParameterInfo param) {
		var type = param.ParameterType;
		var marshalTo = param.GetCustomAttribute<MarshalAsAttribute>()?.Value;

		if (param.ParameterType == typeof(void))
			return typeof(void);

		if (marshalTo == null) {
			return type;
			//throw new NotImplementedException("not providing MarshalAs is not yet implemented");
		}

		switch (marshalTo) {
			case UnmanagedType.LPStr: return typeof(sbyte*);
			default: throw new NotImplementedException($"MarshalAs: no marshalAs case for {marshalTo}.");
		}
	}

	private static void genInterfaceNativePtr(
		TypeBuilder typeBuilder, MethodInfo method, Type delegateType, Delegate delegateInstance, 
		Type[] justParams, bool selfPtrFirst, ImplFieldNameToDelegate[] fields, ref int fieldArPtr) {
		var methodBuilder = typeBuilder.DefineMethod(
				method.Name,
				MethodAttributes.Public | MethodAttributes.Virtual,
				method.ReturnType,
				Array.ConvertAll(method.GetParameters(), p => p.ParameterType)
			);

		var il = methodBuilder.GetILGenerator();

		// Call the delegateInstance
		// if selfPtrFirst then the first argument into delegateInstance must be the 
		// ICppClass's Pointer property.

		var parameters = method.GetParameters();
		var paramTypes = Array.ConvertAll(parameters, p => p.ParameterType);

		var fieldName = $"__impl_{method.Name}";
		var delField = typeBuilder.DefineField(
			fieldName,
			delegateType,
			FieldAttributes.Private | FieldAttributes.Static
		);

		// Push this for later, the static constructor deals with this
		pushArray(new ImplFieldNameToDelegate() {
			IsNativeCall = true,
			Field = delField,
			Delegate = delegateInstance
		}, fields, ref fieldArPtr);

		// 0 will always be delField
		il.Emit(OpCodes.Ldsfld, delField);
		if (selfPtrFirst) {
			// Read our pointer object
			il.Emit(OpCodes.Ldarg_0);
			var pointerProp = typeof(ICppClass).GetProperty("Pointer")!;
			var getter = pointerProp.GetGetMethod()!;
			il.Emit(OpCodes.Callvirt, getter);
		}
		for (int i = 0; i < parameters.Length; i++) {
			il.Emit(OpCodes.Ldarg, i + 1);
		}

		var invokeMethod = delegateType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance)!;
		il.Emit(OpCodes.Callvirt, invokeMethod);
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