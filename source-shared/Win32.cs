﻿using System.Runtime.InteropServices;

public static class Win32
{
	public const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;
	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "LoadLibraryExA")] public static extern IntPtr LoadLibraryExA(string lpLibFileName, IntPtr hFile, uint dwFlags);
	[DllImport("kernel32.dll", SetLastError = true, EntryPoint = "FreeLibrary")] public static extern bool FreeLibrary(IntPtr hModule);
	[DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetProcAddress")] public static extern IntPtr GetProcAddress(IntPtr hModule, string procedureName);
	[DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string? lpModuleName);
}