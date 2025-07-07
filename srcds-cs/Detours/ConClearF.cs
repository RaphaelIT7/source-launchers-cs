using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using MinHook;

using Source;
using Source.SDK;

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace srcds_cs.Detours;

internal class DetourConClearF : IImplementsDetours
{
	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	delegate void ConClearF();
	static ConClearF? ConClearF_Original;

	static unsafe void csharpRunCallback(void* ccommandOpq) {
		CCommand cmd = MarshalCpp.Cast<CCommand>(ccommandOpq); // TODO: the delegate ideally should be managed and produce an unmanaged delegate on the fly with correct
		// calling conventions, so this ^^^ cast doesnt have to happen
		var code = cmd.ArgSBuffer[(int)cmd.ArgV0Size..];
		#region C# gen

		var syntaxTree = CSharpSyntaxTree.ParseText($@"
using System;

public class ScriptHost
{{
    public static void Run()
    {{
        {code};
    }}
}}
");
		var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
		var compilation = CSharpCompilation.Create("DynamicAssembly")
			.WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
			.AddReferences(
				MetadataReference.CreateFromFile(typeof(object).Assembly.Location),                      // System.Private.CoreLib.dll
				MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),                    // System.Console.dll
				MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")),     // System.Runtime.dll
				MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "netstandard.dll"))        // netstandard.dll
			)
			.AddSyntaxTrees(syntaxTree);

		using var ms = new System.IO.MemoryStream();
		var result = compilation.Emit(ms);

		if (!result.Success) {
			foreach (var diagnostic in result.Diagnostics)
				Console.WriteLine(diagnostic.ToString());
			return;
		}

		ms.Seek(0, System.IO.SeekOrigin.Begin);
		var assembly = Assembly.Load(ms.ToArray());

		var type = assembly.GetType("ScriptHost");
		var method = type?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
		#endregion
		method?.Invoke(null, null);
	}

	static unsafe void ConClearF_Detour() {
		ConClearF_Original!();
		Console.Clear();
		Console.CursorTop = 1;

		ICvar cvar = Source.Engine.CreateInterface<ICvar>("vstdlib", LoadModules.CVAR_INTERFACE_VERSION)!;
		ConCommand lua_run = cvar.FindCommand("lua_run");
		ConCommand csharp_run = MarshalCpp.New<ConCommand>();

		csharp_run.Name = "csharp_run";
		csharp_run.HelpString = "There's no way this works, right?"; 
		csharp_run.ConCommandBase_VTable = lua_run.ConCommandBase_VTable; // proof of concept
		csharp_run.Flags = 1 << 2;
		csharp_run.CommandCallback = csharpRunCallback;
		csharp_run.UsingNewCommandCallback = true;

		cvar.RegisterConCommand(csharp_run);
	}

	public void SetupWin32(HookEngine engine) {
		ConClearF_Original = engine.AddDetour<ConClearF>("engine.dll", [0xE8, 0xDB, 0x83, 0x13, 0x00, 0x8B, 0xC8, 0x8B, 0x10, 0xFF, 0x52, 0x4C], new(ConClearF_Detour));
	}
}