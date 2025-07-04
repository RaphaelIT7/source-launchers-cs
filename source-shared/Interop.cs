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
using System.Diagnostics.Metrics;

namespace Source;

// TODO: should this stuff use tier0 allocs

public readonly ref struct AnsiBuffer
{
	private readonly nint ptr;

	public AnsiBuffer(string? text) {
		ptr = Marshal.StringToHGlobalAnsi(text);
	}

	public unsafe AnsiBuffer(void* text) => ptr = (nint)text;
	public unsafe AnsiBuffer(nint text) => ptr = text;
	public unsafe sbyte* AsPointer() => (sbyte*)ptr.ToPointer();
	public unsafe nint AsNativeInt() => ptr;
	public void Dispose() {
		Marshal.FreeHGlobal(ptr);
	}
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
/// If you're trying to create an instance of the class that can be passed to C++, use <see cref="MarshalCpp.New{T}()"/>. Note that if created by C#, it must be manually freed via Dispose() since CppClass interfaces allocate to unmanaged hglobals and interface similarly to how you'd interface with a C++ class.
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
/// Defines a single C++ field via an interface property.
/// <br/>
/// The <paramref name="fieldIndex"/> refers to a 0-based /// index (0, 1, 2, 3, 4, ...) of the field. 
/// <br/>
/// During dynamic assembly, the list of properties is filtered by those that have a CppFieldAttribute, 
/// sorted by <see cref="FieldIndex"/>. Then the properties are checked to ensure no gaps between indices. 
/// <br/>
/// Finally, each property is counted in order, with an internal tracker for the actual field offset 
/// (based on property type). The final allocation size is then pushed into a constant in the type (ie. 
/// <c>public const nuint RESERVED_ALLOCATION_SIZE = FINAL ALLOCATION SIZE</c>).
/// </summary>
/// <param name="fieldIndex"></param>
[AttributeUsage(AttributeTargets.Property)]
public class CppFieldAttribute(int fieldIndex) : Attribute
{
	public int FieldIndex => fieldIndex;
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

	// Interface Type -> Dynamic Type Flags -> Emitted Dynamic Type
	private static Dictionary<Type, Dictionary<DynamicTypeFlags, Type>> intTypeToDynType = [];
	// Interface implementation -> constructor builder. Hacky solution but it will do
	private static Dictionary<Type, ConstructorInfo> constructors = [];
	// The dynamic MSIL assemblers
	private static AssemblyBuilder? DynAssembly;
	private static ModuleBuilder? DynCppInterfaceFactory;

	public static T New<T>() where T : ICppClass {
		// We need to generate the type to know how much space it takes for allocation.
		var generatedType = GetOrCreateDynamicCppType(typeof(T), DynamicTypeFlags.HandleFromCSharpAllocation, null);
		nuint totalSize = (nuint)generatedType.GetField("RESERVED_ALLOCATION_SIZE", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
		void* ptr = Tier0.Plat_Alloc(100);
		return (T)Activator.CreateInstance(generatedType, [(nint)ptr])!;
	}

	/// <summary>
	/// Determines some internal class behavior.
	/// 0 0 0 0 0 0 0 0
	/// ^
	/// ^
	/// ^
	/// ^
	/// ^
	/// ^
	/// ^
	/// ^------------------ If the handle came from C++/unknown territory or if it 100% came from C#
	/// </summary>
	public enum DynamicTypeFlags : byte
	{
		/// <summary>
		/// The ICppClass instance was instantiated via a pointer that came from C++. Regardless
		/// of if it was allocated by C# or not, if it was pulled from C++ or a non-100% C# pointer,
		/// the interface will represent a readonly, unfreeable pointer. In most cases, the ICppClass
		/// instance was likely instantiated via <see cref="MarshalCpp.Cast{T}(nint)"/> or <see cref="MarshalCpp.Cast{T}(void*)"/>.
		/// <br/>
		/// <br/>  - <see cref="IDisposable.Dispose"/> will perform no operation.
		/// <br/>  - <see cref="ICppClass.Pointer"/> is read only.
		/// </summary>
		HandleFromPointer = 0,
		/// <summary>
		/// The ICppClass instance was instantiated via <see cref="MarshalCpp.New{T}"/>. Given this flag,
		/// the class knows it is freeable without as many consequences to invalid operations, and therefore the pointer
		/// can be freed. <b>It is up to your judgement on if the pointer can truly be freed or not without consequences, 
		/// and the pointer will not be freed by deconstructors.</b> This is because the class instance may be used by 
		/// C++ over the total lifetime of the program. To free it, you must use Dispose() manually or use it within a using statement.
		/// <br/>
		/// <br/> - <see cref="IDisposable.Dispose"/> will perform <see cref="Tier0.Plat_Free(void*)"/> on <see cref="ICppClass.Pointer"/>.
		/// <br/> - <see cref="ICppClass.Pointer"/> is read only.
		/// </summary>
		HandleFromCSharpAllocation = 1
	}

	/// <summary>
	/// Determines the ICppClass hashcode. Likely implemented by the dynamic type (ie. the dyntype
	/// should insert this method call into its implementation)
	/// </summary>
	public static int CppClassHashcode(ICppClass self) => HashCode.Combine(self.Pointer);
	/// <summary>
	/// Determines ICppClass equality. Likely implemented by the dynamic type (ie. the dyntype
	/// should insert this method call into its implementation)
	/// </summary>
	public static bool CppClassEquals(ICppClass? a, ICppClass? b) {
		if ((a == null || a.Pointer == 0) && (b == null || b.Pointer == 0)) return true;
		return a.Pointer == b.Pointer;
	}

	// pointerField is so we can reach into _pointer for this
	// pointerProperty is so we can reach into _pointer for non-this but still ICppClass
	// fieldOffset is the offset calculated by the assembler
	// builder is the builder that the dynamic assembler produces

	delegate nuint DynamicCppFieldFactory(
		FieldBuilder pointerField, PropertyInfo pointerProperty, nuint fieldOffset,
		PropertyInfo fieldProperty, ILGenerator getter, ILGenerator setter
	);

	static void PointerMathIL(FieldBuilder pointerField, ILGenerator il, nuint fieldOffset) {
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Ldfld, pointerField);
		if (fieldOffset != 0) {
			il.Emit(OpCodes.Ldc_I8, (long)fieldOffset);
			il.Emit(OpCodes.Conv_I);
			il.Emit(OpCodes.Add);
		}
	}

	/// <summary>
	/// <c>[typeof(<see cref="nint"/>)]</c>
	/// </summary>
	static readonly Type[] ICppClassConstructorTypes = [typeof(nint)];

	static nuint ManagedCppClassInterfaceFactory(
		FieldBuilder pointerField, PropertyInfo pointerProperty, nuint fieldOffset,
		PropertyInfo fieldProperty, ILGenerator getter, ILGenerator setter
	) {
		nuint fieldSize = (nuint)sizeof(nint);

		// Getter
		{
			PointerMathIL(pointerField, getter, fieldOffset);
			getter.Emit(OpCodes.Ldobj, typeof(nint));

			// Instantiate a type of fieldProperty.PropertyType, with a single nint argument (which would be the value loaded by Ldobj)
			Type t = GetOrCreateDynamicCppType(fieldProperty.PropertyType, DynamicTypeFlags.HandleFromPointer, null);
			ConstructorInfo ctor = constructors[t];

			getter.Emit(OpCodes.Newobj, ctor);
			getter.Emit(OpCodes.Ret);
		}

		// Setter
		{
			PointerMathIL(pointerField, setter, fieldOffset);
			setter.Emit(OpCodes.Ldarg_1);
			// We need to take the Pointer property (pointerProperty) from the ICppClass loaded by Ldarg_1,
			// and push that to the stack, to then be stored by Stobj.
			setter.Emit(OpCodes.Callvirt, pointerProperty.GetMethod!);

			setter.Emit(OpCodes.Stobj, typeof(nint));
			setter.Emit(OpCodes.Ret);
		}

		return fieldSize;
	}

	static nuint UnmanagedTypeFieldFactory<T>(
		FieldBuilder pointerField, PropertyInfo pointerProperty, nuint fieldOffset,
		PropertyInfo fieldProperty, ILGenerator getter, ILGenerator setter
	) where T : unmanaged {
		nuint fieldSize = (nuint)sizeof(T);

		// pointerField is an nint representing an unmanaged memory pointer.
		// Getter is defined to read from this pointer, given fieldOffset as an offset to the pointer, and return it as T.
		// Setter is defined vice versa - write T to pointer + offset.
		// PointerMathIL will do (ptr + offset), then Ldobj/Stobj get T from memory/place T into memory respectively

		// Getter
		{
			PointerMathIL(pointerField, getter, fieldOffset);
			getter.Emit(OpCodes.Ldobj, typeof(T));
			getter.Emit(OpCodes.Ret);
		}
		// Setter
		{
			PointerMathIL(pointerField, setter, fieldOffset);
			setter.Emit(OpCodes.Ldarg_1);
			setter.Emit(OpCodes.Stobj, typeof(T));
			setter.Emit(OpCodes.Ret);
		}

		return fieldSize;
	}


	static nuint AnsiBufferFactory(
		FieldBuilder pointerField, PropertyInfo pointerProperty, nuint fieldOffset,
		PropertyInfo fieldProperty, ILGenerator getter, ILGenerator setter
	) {
		nuint fieldSize = (nuint)sizeof(nint);

		// Getter
		{
			PointerMathIL(pointerField, getter, fieldOffset);
			getter.Emit(OpCodes.Ldobj, typeof(nint));
			ConstructorInfo ctor = typeof(AnsiBuffer).GetConstructor(ICppClassConstructorTypes)!;
			getter.Emit(OpCodes.Newobj, ctor);
			getter.Emit(OpCodes.Ret);
		}

		// Setter
		{
			PointerMathIL(pointerField, setter, fieldOffset);
			setter.Emit(OpCodes.Ldarg_1);
			setter.Emit(OpCodes.Callvirt, typeof(AnsiBuffer).GetMethod("AsNativeInt")!);

			setter.Emit(OpCodes.Stobj, typeof(nint));
			setter.Emit(OpCodes.Ret);
		}

		return fieldSize;
	}

	static Dictionary<Type, DynamicCppFieldFactory> Generators = new() {
		{ typeof(bool),     UnmanagedTypeFieldFactory<bool> },

		{ typeof(sbyte),     UnmanagedTypeFieldFactory<sbyte> },
		{ typeof(short),     UnmanagedTypeFieldFactory<short> },
		{ typeof(int),       UnmanagedTypeFieldFactory<int> },
		{ typeof(long),      UnmanagedTypeFieldFactory<long> },

		{ typeof(byte),      UnmanagedTypeFieldFactory<byte> },
		{ typeof(ushort),    UnmanagedTypeFieldFactory<ushort> },
		{ typeof(uint),      UnmanagedTypeFieldFactory<uint> },
		{ typeof(ulong),     UnmanagedTypeFieldFactory<ulong> },

		{ typeof(float),     UnmanagedTypeFieldFactory<float> },
		{ typeof(double),    UnmanagedTypeFieldFactory<double> },

		{ typeof(nint),      UnmanagedTypeFieldFactory<nint> },
		{ typeof(nuint),     UnmanagedTypeFieldFactory<nuint> },

		{ typeof(ICppClass), ManagedCppClassInterfaceFactory },
		{ typeof(AnsiBuffer), AnsiBufferFactory },
	};

	private static bool resolveType(Type t, [NotNullWhen(true)] out DynamicCppFieldFactory? gen) {
		return Generators.TryGetValue(t, out gen);
	}

	public static bool IsValidCppMethod(MethodInfo x) {
		return
			x.GetCustomAttribute<CppMethodFromVTOffsetAttribute>() != null
			|| x.GetCustomAttribute<CppMethodFromSigScanAttribute>() != null;
	}

	public static bool IsValidCppField(PropertyInfo x) {
		return x.GetCustomAttribute<CppFieldAttribute>() != null;
	}

	public static void PreventCppFieldGaps(IEnumerable<PropertyInfo> fields) {
		int lastIndex = -1;
		int index = 0;
		PropertyInfo? lastField = null;
		foreach (PropertyInfo x in fields) {
			var propIndex = x.GetCustomAttribute<CppFieldAttribute>()!.FieldIndex;
			if (propIndex != index)
				throw new IndexOutOfRangeException($"MarshalCpp dynamic factory failed to ensure field order. This matters due to how sizing influences offsets.\n\nThe two offenders were:\n{lastField?.Name ?? "<start>"} [{lastIndex}] -> {x.Name} [{propIndex}]");
			lastIndex = index;
			index = propIndex + 1;
			lastField = x;
		}
	}
	public static int CppFieldIndexSorter(PropertyInfo x) => x.GetCustomAttribute<CppFieldAttribute>()!.FieldIndex;

	private static Type GetOrCreateDynamicCppType(Type interfaceType, DynamicTypeFlags flags, void* ptr) {
		Type? finalType = null;
		if (intTypeToDynType.TryGetValue(interfaceType, out var generatedTypes)) {
			if (generatedTypes.TryGetValue(flags, out finalType))
				return finalType;
		}
		else {
			generatedTypes = [];
			intTypeToDynType[interfaceType] = generatedTypes;
		}

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

		string typeName = $"MarshalCpp_Dynamic{interfaceType.Name}_{flags}";
		TypeBuilder typeBuilder = dynModule.DefineType(typeName, TypeAttributes.Public, null, [interfaceType]);
		intTypeToDynType[interfaceType][flags] = typeBuilder; // TEMPORARILY store this so things don't die

		// This is the pointer field to the C++ class. The Pointer property on an ICppClass is backed by this field.
		// We declare this as soon as possible to use it in IL instructions later on the road
		FieldBuilder pointerField = typeBuilder.DefineField("_pointer", typeof(nint), FieldAttributes.Private);
		// Do the same as above for pointerProperty for the same reasons
		PropertyBuilder pointerProperty = typeBuilder.DefineProperty(
				"Pointer",
				PropertyAttributes.None,
				typeof(nint),
				null
			);

		// Define the constructor, for similar reasons - mostly type referencing dynamic type hell
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

			constructors[typeBuilder] = ctorBuilder;
		}

		// Setup Pointer. Since this comes from C++ land lets not let the user change it without throwing
		{
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

			pointerProperty.SetGetMethod(getterMethod);
			pointerProperty.SetSetMethod(setterMethod);

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
			if (flags.HasFlag(DynamicTypeFlags.HandleFromCSharpAllocation)) {
				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Ldfld, pointerField);

				MethodInfo platFree = typeof(Tier0).GetMethod("Plat_Free", BindingFlags.Public | BindingFlags.Static)!;
				il.Emit(OpCodes.Call, platFree);
			}
			il.Emit(OpCodes.Ret);

			typeBuilder.DefineMethodOverride(disposeMethod, typeof(IDisposable).GetMethod("Dispose")!);
		}

		// All fields the interface type implemented via properties with CppField attributes.
		IEnumerable<PropertyInfo> fields = interfaceType.GetProperties().Where(IsValidCppField).OrderBy(CppFieldIndexSorter);
		PreventCppFieldGaps(fields);
		// All methods the interface implemented via CppMethodFrom* attributes.
		IEnumerable<MethodInfo> methods = interfaceType.GetMethods().Where(IsValidCppMethod);
		// How much space the New<T> factory reserves in unallocated land
		nuint allocation_size = 0;

		foreach (var field in fields) {
			var propertyType = field.PropertyType;
			DynamicCppFieldFactory? generator = null;
			if (!resolveType(propertyType, out generator)) {
				// If ICppClass, use that then
				if (propertyType.IsAssignableTo(typeof(ICppClass)))
					resolveType(typeof(ICppClass), out generator);
			}

			if (generator == null)
				throw new NotImplementedException($"Unable to resolve property '{field.Name}''s type ({propertyType.FullName ?? propertyType.Name}) to a DynamicCppFieldGenerator. This is either invalid/unimplemented behavior.");

			nuint fieldOffset = allocation_size;
			allocation_size += ConstructProperty(typeBuilder, generator, pointerField, pointerProperty, field, fieldOffset);
		}

		FieldBuilder RESERVED_ALLOCATION_SIZE = typeBuilder.DefineField("RESERVED_ALLOCATION_SIZE", typeof(nuint), FieldAttributes.Public | FieldAttributes.Static);

		// define cctor
		{
			ConstructorBuilder cctor = typeBuilder.DefineTypeInitializer();
			ILGenerator il = cctor.GetILGenerator();

			il.Emit(OpCodes.Ldc_I8, (long)allocation_size); 
			il.Emit(OpCodes.Conv_U);
			il.Emit(OpCodes.Stsfld, RESERVED_ALLOCATION_SIZE);
			il.Emit(OpCodes.Ret);
		}

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
			else if (ptr != null) {
				nint vtablePtr = *(nint*)ptr;
				nint* vtable = (nint*)vtablePtr;

				int vt_offset = vtableOffsetN.Value;

				cppMethod = vtable[vt_offset];
			}
			else {
				genInterfaceStub(typeBuilder, method);
				continue;
			}

			// Generate the delegate type
			int typeIndex = 0;
			int pTypeIndex = 0;
			ParameterInfo[] parameters = method.GetParameters();

			// Allocate Type[] with length of Parameters + SelfPtr (if needed) + 1 for return type since
			// this builds the managed delegate type
			Type[] types = new Type[parameters.Length + (vt_useSelfPtr ? 1 : 0) + 1];
			// Some calls don't take in a void*. I believe this is a compiler optimization that discards unused parameters?
			// Entirely guessing but regardless theres not a good enough way to automatically discern it hence this
			// TODO: is this even a thing lol. I think I was just having a bad day with IL gen/etc
			if (vt_useSelfPtr)
				pushArray(typeof(nint), types, ref typeIndex);

			// Figure out how to marshal any other types in the parameter list.
			foreach (ParameterInfo param in parameters)
				pushArray(getMarshalType(param), types, ref typeIndex);

			// The return type is always at the end of the delegate, even if void
			// Since the return of a method is also a ParameterInfo that can be passed into getMarshalType again
			// rather than be reused
			pushArray(getMarshalType(method.ReturnParameter), types, ref typeIndex);

			// Rewrite the dynamic object's method so it implements the interface as expected
			genInterfaceNative(typeBuilder, method, types, cppMethod, pointerField, vt_useSelfPtr);
		}

		finalType = typeBuilder.CreateType();

		constructors.Remove(typeBuilder);
		constructors[finalType] = finalType.GetConstructor(ICppClassConstructorTypes)!;
		intTypeToDynType[interfaceType][flags] = finalType;
		return finalType;
	}

	private static nuint ConstructProperty(
		TypeBuilder typeBuilder, DynamicCppFieldFactory generator,
		FieldBuilder pointerField, PropertyInfo pointerProperty, PropertyInfo interfaceProperty,
		nuint fieldOffset
	) {
		string propName = interfaceProperty.Name;
		Type propType = interfaceProperty.PropertyType;

		MethodInfo getterMethod = interfaceProperty.GetMethod!;
		MethodInfo setterMethod = interfaceProperty.SetMethod!;

		PropertyBuilder concreteProp = typeBuilder.DefineProperty(propName, PropertyAttributes.None, propType, null);
		// Both getter/setter use this bitflag combo
		MethodAttributes propAttrs = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig;

		MethodBuilder getter = typeBuilder.DefineMethod(getterMethod.Name, propAttrs, propType, Type.EmptyTypes);
		ILGenerator getterIL = getter.GetILGenerator();

		MethodBuilder setter = typeBuilder.DefineMethod(setterMethod.Name, propAttrs, null, [propType]);
		ILGenerator setterIL = setter.GetILGenerator();

		// Call the DynamicCppFieldFactory to produce IL.
		nuint fieldSize = generator(pointerField, pointerProperty, fieldOffset, interfaceProperty, getterIL, setterIL);

		concreteProp.SetGetMethod(getter);
		concreteProp.SetSetMethod(setter);

		typeBuilder.DefineMethodOverride(getter, getterMethod);
		typeBuilder.DefineMethodOverride(setter, setterMethod);

		return fieldSize;
	}

	public static T Cast<T>(nint ptr) where T : ICppClass => Cast<T>((void*)ptr);
	public static T Cast<T>(void* ptr) where T : ICppClass {
		var generatedType = GetOrCreateDynamicCppType(typeof(T), DynamicTypeFlags.HandleFromPointer, ptr);
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
				else if (providedManagedType.IsAssignableTo(typeof(ICppClass)) && expectedNativeType == typeof(nint)) {
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
	[DllImport(dllname)] public static extern void* Plat_Alloc(ulong size);
	[DllImport(dllname)] public static extern void* Plat_Realloc(void* ptr, ulong size);
	[DllImport(dllname)] public static extern void Plat_Free(void* ptr);
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