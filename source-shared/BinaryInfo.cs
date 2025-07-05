using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Source;

/// <summary>
/// GOALS:
/// - Something that has a better data structure/caches "fast paths" for byte signature scanning
/// - Perhaps plug into a x86-64 disassembler (I found an interesting C# one the other day, may try it out, I believe MinHook uses something from it)
/// - RTTI/VTable information (need to study this more)
/// </summary>
public unsafe class BinaryInfo
{
	void* binaryAddr;
	nuint binarySize;

	public BinaryInfo(string module) {
		throw new NotImplementedException();
	}

	public BinaryInfo(nint address, nuint size) {
		binaryAddr = (void*)address;
		binarySize = size;
	}

	public BinaryInfo(nuint address, nuint size) {
		binaryAddr = (void*)address;
		binarySize = size;
	}

	public Span<byte> AsSpan() => new Span<byte>(binaryAddr, (int)binarySize);
}
