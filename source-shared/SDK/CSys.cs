namespace Source.SDK;

public interface CSys : ICppClass
{
	[CppMethodFromVTOffset(11)]
	public unsafe void ConsoleOutput(AnsiBuffer txt);
}
