// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0
// Ported from Files (brett-baker/Files) src/Files.App.CsWin32/HRESULT.cs and adapted to Leviathan's code style.
// This file remains under MPL-2.0; see THIRD-PARTY-NOTICES.md.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Windows.Win32.Foundation;

/// <summary>Helpers on the generated <c>HRESULT</c> type.</summary>
[DebuggerDisplay("{" + nameof(Value) + ",h}")]
internal readonly partial struct HRESULT
{
    /// <summary>
    /// Throws when the result is a failure in a Debug build; otherwise returns the value unchanged.
    /// Use it where a failure is a bug during development but must be tolerated in a release build.
    /// </summary>
    public HRESULT ThrowIfFailedOnDebug()
    {
#if DEBUG
        if (Failed)
        {
            Marshal.ThrowExceptionForHR(Value);
        }
#endif
        return this;
    }
}
