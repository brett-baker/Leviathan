// The AssocQueryString lookup is adapted from Files (brett-baker/Files) src/Files.App/Utils/Shell/FileAssociationHelpers.cs,
// Copyright (c) Files Community, MPL-2.0. Generalized from "is it Photos" to "what is the default app".
// See THIRD-PARTY-NOTICES.md.

using Leviathan.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Leviathan.Interop;

/// <summary><see cref="IFileAssociations"/> over <c>AssocQueryString</c>.</summary>
public sealed unsafe class WindowsFileAssociations : IFileAssociations
{
    /// <inheritdoc />
    public DefaultApp? GetDefaultApp(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var dotted = extension.StartsWith('.') ? extension : "." + extension;
        var name = Query(dotted, ASSOCSTR.ASSOCSTR_FRIENDLYAPPNAME);
        var executable = Query(dotted, ASSOCSTR.ASSOCSTR_EXECUTABLE);
        var progId = Query(dotted, ASSOCSTR.ASSOCSTR_PROGID);
        if (name is null && executable is null)
        {
            return null;
        }

        return new DefaultApp(name ?? Path.GetFileNameWithoutExtension(executable) ?? dotted, executable, progId);
    }

    private static string? Query(string extension, ASSOCSTR what)
    {
        fixed (char* association = extension)
        {
            uint length = 0;

            // The first call reports the buffer size needed.
            _ = PInvoke.AssocQueryString(ASSOCF.ASSOCF_INIT_IGNOREUNKNOWN, what, new PCWSTR(association), default, default, &length);
            if (length <= 1)
            {
                return null;
            }

            var buffer = new char[length];
            fixed (char* output = buffer)
            {
                var hr = PInvoke.AssocQueryString(ASSOCF.ASSOCF_INIT_IGNOREUNKNOWN, what, new PCWSTR(association), default, new PWSTR(output), &length);
                if (hr.Failed)
                {
                    return null;
                }

                var value = new string(output);
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
    }
}
