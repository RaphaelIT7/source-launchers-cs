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
using System.Text;

namespace Source;

// TODO: should this stuff use tier0 allocs

public readonly ref struct AnsiBuffer
{
	public readonly nint Pointer;

	public unsafe AnsiBuffer(string? text) {
		Pointer = (nint)MarshalCpp.StrToPtr(text);
	}

	public unsafe AnsiBuffer(void* text) => Pointer = (nint)text;
	public unsafe AnsiBuffer(nint text) => Pointer = text;
	public unsafe sbyte* AsPointer() => (sbyte*)Pointer.ToPointer();
	public unsafe nint AsNativeInt() => Pointer;
	public unsafe void Dispose() {
		MarshalCpp.Dealloc((void*)Pointer);
	}
	public static unsafe implicit operator sbyte*(AnsiBuffer buffer) => buffer.AsPointer();
	public static unsafe implicit operator string?(AnsiBuffer buffer) => ToManaged(buffer.Pointer);
	public static unsafe implicit operator AnsiBuffer(string text) => new(text);
	public unsafe string? ToManaged() => MarshalCpp.PtrToStr((void*)Pointer);

	public static unsafe string? ToManaged(nint ptr) => Marshal.PtrToStringAnsi(ptr);
	public static unsafe string? ToManaged(void* ptr) => Marshal.PtrToStringAnsi(new(ptr));
	public static unsafe string? ToManaged(sbyte* ptr) => Marshal.PtrToStringAnsi(new(ptr));
	public static unsafe string? ToManaged(sbyte* ptr, uint len) => Marshal.PtrToStringAnsi(new(ptr), (int)len);

	public override string? ToString() {
		return ToManaged();
	}
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
	public static unsafe Span<byte> View<T>(this T clss) where T : ICppClass {
		return new Span<byte>((void*)clss.Pointer, (int)MarshalCpp.SizeOf<T>());
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
public class CppMethodFromSigScanAttribute(OperatingFlags arch, string dll, string sig, [CallerFilePath] string? __from = null, [CallerLineNumber] int __fileNum = 0) : Attribute
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
	public virtual Type? BaseType { get; }
	public int FieldIndex => fieldIndex;
}
/// <summary>
/// (TODO) Defines a single C++ field, starting at the end index of an existing ICppClass interface.
/// </summary>
/// <param name="fieldIndex"></param>
[AttributeUsage(AttributeTargets.Property)]
public class CppFieldAttribute<T>(int fieldIndex) : CppFieldAttribute(fieldIndex) where T : ICppClass
{
	public override Type BaseType => typeof(T);
}
/// <summary>
/// (TODO) Defines the bit-width of a field if necessary. This is used by the dynamic type assembler
/// to determine some boolean flags. How is this going to be done given the current setup? We need some
/// kind of custom accessor for these types, and probably need to start counting bitwise rather than bytewise
/// while assembling the type, then aligning to the nearest class-alignment afterwards...
/// </summary>
/// <param name="bits"></param>
public class CppFieldBitWidthAttribute(int bits) : Attribute {
	public int Bits => bits;
}
/// <summary>
/// Designates that this field is a virtual function table
/// todo: how the hell are we even going to find these...
/// </summary>
public class CppVTableAttribute(int fieldIndex, OperatingFlags arch, string dll, string sig) : Attribute
{
	public int FieldIndex => fieldIndex;
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
/// There may be some future stuff as well to automatically scan for vtables/vtable functions from a scanning address, similar to how <see cref="Scanning.ScanModuleProc(string, ReadOnlySpan{byte?})"/>
/// would work but for vtables. Regardless, this is infinitely better than anything else I've had so far to accomplish interop.
/// </summary>
public static unsafe class MarshalCpp
{
	public static unsafe nuint SizeOf<T>() where T : ICppClass {
		var generatedType = GetOrCreateDynamicCppType(typeof(T), null);
		nuint totalSize = (nuint)generatedType.GetField("RESERVED_ALLOCATION_SIZE", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
		return totalSize;
	}

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
	private static Dictionary<Type, Type> intTypeToDynType = [];
	// Interface implementation -> constructor builder. Hacky solution but it will do
	private static Dictionary<Type, ConstructorInfo> constructors = [];
	// The dynamic MSIL assemblers
	private static AssemblyBuilder? DynAssembly;
	private static ModuleBuilder? DynCppInterfaceFactory;

	/// <summary>
	/// Allocator used by <see cref="MarshalCpp"/> operations
	/// </summary>
	/// <param name="size"></param>
	/// <param name="alignment"></param>
	/// <returns></returns>
	public static unsafe void* Alloc(nuint size) => Tier0.Plat_Alloc(size);
	/// <summary>
	/// Deallocator used by <see cref="MarshalCpp"/> operations
	/// </summary>
	/// <param name="size"></param>
	/// <param name="alignment"></param>
	/// <returns></returns>
	public static unsafe void Dealloc(void* ptr) => Tier0.Plat_Free(ptr);

	/// <summary>
	/// Allocates a string, with ANSI encoding by default (or if <c><paramref name="encoding"/> == null</c>).
	/// It is your responsibility to free it later (or not to)
	/// </summary>
	/// <param name="managed"></param>
	/// <returns></returns>
	public static unsafe void* StrToPtr(ReadOnlySpan<char> managed, Encoding? encoding = null) {
		if (managed == null) return null;

		encoding ??= Encoding.Default;
		nuint strsize = (nuint)encoding.GetByteCount(managed);
		void* strallc = Alloc(strsize + 1);
		encoding.GetBytes(managed, new Span<byte>(strallc, (int)strsize));
		((byte*)strallc)[strsize] = 0; // null terminate the string
		return strallc;
	}

	/// <summary>
	/// Converts a null-terminated string pointer back into a managed string using ANSI by default.
	/// </summary>
	/// <param name="ptr">Pointer to the null-terminated string.</param>
	/// <param name="encoding">Encoding used to interpret the bytes (default is ANSI/Encoding.Default).</param>
	/// <returns>The managed string, or null if <paramref name="ptr"/> is null.</returns>
	public static unsafe string? PtrToStr(void* ptr, Encoding? encoding = null) {
		if (ptr == null) return null;

		encoding ??= Encoding.Default;

		// Find the length of the null-terminated string
		byte* p = (byte*)ptr;
		int length = 0;
		while (p[length] != 0) length++;

		// Decode bytes to string
		return encoding.GetString(p, length);
	}

	public static T New<T>() where T : ICppClass {
		// We need to generate the type to know how much space it takes for allocation.
		var generatedType = GetOrCreateDynamicCppType<T>(null);
		nuint totalSize = MarshalCpp.SizeOf<T>();
		byte* ptr = (byte*)Alloc(totalSize);
		// zero it out
		for (nuint i = 0; i < totalSize; i++) {
			ptr[i] = 0;
		}

		return (T)Activator.CreateInstance(generatedType, [(nint)ptr])!;
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
			Type t = GetOrCreateDynamicCppType(fieldProperty.PropertyType, null);
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
			setter.Emit(OpCodes.Ldfld, typeof(AnsiBuffer).GetField(nameof(AnsiBuffer.Pointer))!);

			setter.Emit(OpCodes.Stobj, typeof(nint));
			setter.Emit(OpCodes.Ret);
		}

		return fieldSize;
	}

	static Dictionary<Type, DynamicCppFieldFactory> Generators = new() {
		{ typeof(bool),       UnmanagedTypeFieldFactory<bool> },

		{ typeof(sbyte),      UnmanagedTypeFieldFactory<sbyte> },
		{ typeof(short),      UnmanagedTypeFieldFactory<short> },
		{ typeof(int),        UnmanagedTypeFieldFactory<int> },
		{ typeof(long),       UnmanagedTypeFieldFactory<long> },

		{ typeof(byte),       UnmanagedTypeFieldFactory<byte> },
		{ typeof(ushort),     UnmanagedTypeFieldFactory<ushort> },
		{ typeof(uint),       UnmanagedTypeFieldFactory<uint> },
		{ typeof(ulong),      UnmanagedTypeFieldFactory<ulong> },

		{ typeof(float),      UnmanagedTypeFieldFactory<float> },
		{ typeof(double),     UnmanagedTypeFieldFactory<double> },

		{ typeof(nint),       UnmanagedTypeFieldFactory<nint> },
		{ typeof(nuint),      UnmanagedTypeFieldFactory<nuint> },

		{ typeof(ICppClass),  ManagedCppClassInterfaceFactory },
		{ typeof(AnsiBuffer), AnsiBufferFactory },
	};
	static Dictionary<Type, nuint> DataSizes = new() {
		{ typeof(bool),       sizeof(bool) },

		{ typeof(sbyte),      sizeof(sbyte) },
		{ typeof(short),      sizeof(short) },
		{ typeof(int),        sizeof(int) },
		{ typeof(long),       sizeof(long) },

		{ typeof(byte),       sizeof(byte) },
		{ typeof(ushort),     sizeof(ushort) },
		{ typeof(uint),       sizeof(uint) },
		{ typeof(ulong),      sizeof(ulong) },

		{ typeof(float),      sizeof(float) },
		{ typeof(double),     sizeof(double) },

		{ typeof(nint),       (nuint)sizeof(nint) },
		{ typeof(nuint),      (nuint)sizeof(nuint) },

		{ typeof(ICppClass),  (nuint)sizeof(nint) },
		{ typeof(AnsiBuffer), (nuint)sizeof(nint) },
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
		return x.GetCustomAttribute<CppFieldAttribute>() != null || x.GetCustomAttribute<CppVTableAttribute>() != null;
	}

	public static int GetPropertyFieldIndex(PropertyInfo x)
		=> x.GetCustomAttribute<CppFieldAttribute>()?.FieldIndex ?? x.GetCustomAttribute<CppVTableAttribute>()!.FieldIndex;

	public static void PreventCppFieldGaps(IEnumerable<PropertyInfo> fields) {
		int lastIndex = -1;
		int index = 0;
		PropertyInfo? lastField = null;
		foreach (PropertyInfo x in fields) {
			var propIndex = GetPropertyFieldIndex(x);
			if (propIndex != index)
				throw new IndexOutOfRangeException($"MarshalCpp dynamic factory failed to ensure field order. This matters due to how sizing influences offsets.\n\nThe two offenders were:\n{lastField?.Name ?? "<start>"} [{lastIndex}] -> {x.Name} [{propIndex}]");
			lastIndex = index;
			index = propIndex + 1;
			lastField = x;
		}
	}
	internal static nuint GetLargestStructSize(IEnumerable<Type> types) {
		nuint s = 0;
		foreach (var type in types) {
			// kind of a sucky solution to this
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
			nuint typeSize = DataSizes.TryGetValue(type, out typeSize) ? typeSize : type.IsAssignableTo(typeof(ICppClass)) ? (nuint)sizeof(nint) : throw new Exception();
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
			if (typeSize > s)
				s = typeSize;
		}
		return s;
	}
	internal static Type GetOrCreateDynamicCppType<T>(void* ptr = null) => GetOrCreateDynamicCppType(typeof(T), ptr);
	internal static Type GetOrCreateDynamicCppType(Type interfaceType, void* ptr) {
		Type? finalType = null;
		if (intTypeToDynType.TryGetValue(interfaceType, out finalType)) {
			return finalType;
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

		string typeName = $"MarshalCpp_Dynamic{interfaceType.Name}";
		TypeBuilder typeBuilder = dynModule.DefineType(typeName, TypeAttributes.Public, null, [interfaceType]);
		intTypeToDynType[interfaceType] = typeBuilder; // TEMPORARILY store this so things don't die

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
				[typeBuilder]
			);
			// Mark as 'implicit operator'

			ILGenerator il = implicitOp.GetILGenerator();

			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldfld, pointerField);
			il.Emit(OpCodes.Ret);
		}
		{
			MethodBuilder disposeMethod = typeBuilder.DefineMethod(
				"Dispose",
				MethodAttributes.Public | MethodAttributes.Virtual,
				typeof(void),
				Type.EmptyTypes
			);

			ILGenerator il = disposeMethod.GetILGenerator();

			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldfld, pointerField);

			MethodInfo platFree = typeof(MarshalCpp).GetMethod(nameof(Dealloc), BindingFlags.Public | BindingFlags.Static)!;
			il.Emit(OpCodes.Call, platFree);

			il.Emit(OpCodes.Ret);

			typeBuilder.DefineMethodOverride(disposeMethod, typeof(IDisposable).GetMethod("Dispose")!);
		}

		// All fields the interface type implemented via properties with CppField attributes.
		IEnumerable<PropertyInfo> fields = interfaceType.GetProperties().Where(IsValidCppField).OrderBy(GetPropertyFieldIndex);
		PreventCppFieldGaps(fields);
		// All methods the interface implemented via CppMethodFrom* attributes.
		IEnumerable<MethodInfo> methods = interfaceType.GetMethods().Where(IsValidCppMethod);
		// How much space the New<T> factory reserves in unallocated land
		nuint allocation_size = 0;
		nuint allocation_alignment = GetLargestStructSize(fields.Select(x => x.PropertyType));

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
			ConstructProperty(typeBuilder, generator, pointerField, pointerProperty, field, fieldOffset);
			allocation_size += allocation_alignment;
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

				cppMethod = (int)Scanning.ScanModuleProc(sigAttr.DLL, sigAttr.Signature);
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
		intTypeToDynType[interfaceType] = finalType;
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
		var generatedType = GetOrCreateDynamicCppType(typeof(T), ptr);
		return (T)Activator.CreateInstance(generatedType, [(nint)ptr])!;
	}

	private static Type getMarshalType(ParameterInfo param) {
		if (param.ParameterType.IsAssignableTo(typeof(ICppClass)))
			return typeof(nint);
		if (param.ParameterType.IsAssignableTo(typeof(AnsiBuffer)))
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

		for (int i = 1; i < types.Length - 1; i++) {
			il.Emit(OpCodes.Ldarg, i);
			Type expectedNativeType = types[i];
			Type providedManagedType = mparams[i - 1];
			// Check against the index in types
			// if we need to emit an implicit cast?
			// we need to cast what's at Ldarg (given its probably providedManagedType) to expectedNativeType at runtime here
			CheckCastEmitIL(il, expectedNativeType, providedManagedType, false);
		}

		il.Emit(OpCodes.Ldc_I8, (long)nativePtr);
		il.Emit(OpCodes.Conv_I);

		var returnType = types[types.Length - 1];
		var parameterTypes = types[..(types.Length - 1)];

		il.EmitCalli(OpCodes.Calli, CallingConvention.ThisCall, returnType, parameterTypes);

		if (returnType != typeof(void)) {
			Type expectedNativeType = types[types.Length - 1];
			Type providedManagedType = method.ReturnType;
			CheckCastEmitIL(il, expectedNativeType, providedManagedType, true);
		}

		il.Emit(OpCodes.Ret);

		typeBuilder.DefineMethodOverride(methodBuilder, method);
	}

	/// <summary>
	/// Performs casting during native interface generation
	/// </summary>
	/// <param name="il">IL generator</param>
	/// <param name="nativeType">The type that we get from native-land</param>
	/// <param name="managedType">The type that we get from managed-land</param>
	/// <param name="returns"></param>
	/// <exception cref="InvalidOperationException"></exception>
	private static void CheckCastEmitIL(ILGenerator il, Type nativeType, Type managedType, bool returns) {
		if (nativeType != managedType) {
			if (managedType.IsAssignableTo(typeof(AnsiBuffer)) && nativeType == typeof(nint)) {
				if (returns) {
					ConstructorInfo ctor = typeof(AnsiBuffer).GetConstructor(ICppClassConstructorTypes)!;
					il.Emit(OpCodes.Newobj, ctor);
				}
				else {
					var field = typeof(AnsiBuffer).GetField(nameof(AnsiBuffer.Pointer))!;
					il.Emit(OpCodes.Ldfld, field);
				}
			}
			else if (nativeType.IsValueType && managedType.IsValueType)
				// Integer or float widening/narrowing
				il.Emit(getNumericConversionOpcode(managedType, nativeType));
			else if (!nativeType.IsValueType && !managedType.IsValueType)
				// Reference type cast
				il.Emit(OpCodes.Castclass, nativeType);
			else if (managedType.IsAssignableTo(typeof(ICppClass)) && nativeType == typeof(nint)) {
				if (returns) {
					var type = GetOrCreateDynamicCppType(managedType, null);
					ConstructorInfo ctor = constructors[type];

					il.Emit(OpCodes.Newobj, ctor);
				}
				else {
					var prop = typeof(ICppClass).GetProperty(nameof(ICppClass.Pointer));
					var getter = prop!.GetGetMethod()!;
					il.Emit(OpCodes.Callvirt, getter);
				}
			}
			else if (nativeType.IsValueType && !managedType.IsValueType)
				// Unbox to value type
				il.Emit(OpCodes.Unbox_Any, nativeType);
			else if (!nativeType.IsValueType && managedType.IsValueType)
				// Box the value type
				il.Emit(OpCodes.Box, managedType);
			else
				throw new InvalidOperationException($"Unsupported cast from {managedType} to {nativeType}");
		}
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