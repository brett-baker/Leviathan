// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0
// Ported from Files (brett-baker/Files) src/Files.App.CsWin32/ComHeapPtr`1.cs and adapted to Leviathan's code style.
// This file remains under MPL-2.0; see THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;

namespace Windows.Win32;

/// <summary>
/// Contains a heap pointer allocated via CoTaskMemAlloc and a set of methods to work with the pointer safely.
/// </summary>
/// <typeparam name="T">The pointed-to unmanaged type.</typeparam>
internal unsafe struct ComHeapPtr<T> : IDisposable
    where T : unmanaged
{
    private T* _ptr;

    /// <summary>Creates a wrapper that frees <paramref name="ptr"/> with CoTaskMemFree when disposed.</summary>
    public ComHeapPtr(T* ptr)
    {
        _ptr = ptr;
    }

    /// <summary>True when no pointer is held.</summary>
    public readonly bool IsNull => _ptr == null;

    /// <summary>Returns the pointer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T* Get() => _ptr;

    /// <summary>Returns the address of the held pointer, for COM out-parameters.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T** GetAddressOf() => (T**)Unsafe.AsPointer(ref Unsafe.AsRef(in this));

    /// <summary>Frees the memory once; later calls do nothing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        var ptr = _ptr;
        if (ptr is not null)
        {
            _ptr = null;
            PInvoke.CoTaskMemFree(ptr);
        }
    }
}
