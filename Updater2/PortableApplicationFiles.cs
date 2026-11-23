using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DS4Updater;

internal static class PortableApplicationFiles
{
    internal const string Assembly = "DS4Windows.dll";
    internal const string RuntimePrefix = "Runtime/";
    private static readonly string[] Dependencies =
        { Assembly, "DS4Windows.deps.json", "DS4Windows.runtimeconfig.json" };

    internal static bool UsesSplitPackage(ISet<string> owned)
    {
        bool split = Dependencies.Any(name => owned.Contains(RuntimePrefix + name));
        if (split && Dependencies.Any(owned.Contains))
            throw new InvalidDataException("The portable package mixes the flat and Runtime application layouts.");
        string prefix = split ? RuntimePrefix : "";
        foreach (string name in Dependencies)
            if (!owned.Contains(prefix + name))
                throw new InvalidDataException("Required package file is missing: " + prefix + name);
        return split;
    }

    internal static string GetInstalledAssemblyRelativePath(string root)
    {
        // Old releases shipped the identity assembly beside the apphost.
        // Transitional builds could have both copies, but ran from Runtime.
        if (!Dependencies.Any(name => File.Exists(Path.Combine(root, RuntimePrefix + name))))
            return Assembly;
        foreach (string name in Dependencies)
        {
            string path = Path.Combine(root, RuntimePrefix + name);
            PortablePackageTransaction.ValidateNoReparse(path);
            if (!File.Exists(path))
                throw new InvalidDataException("The portable Runtime folder is missing " + name + ".");
        }
        return RuntimePrefix + Assembly;
    }
}
