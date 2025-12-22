/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Unity Technologies.
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Linq;

namespace Antigravity.Ide.Editor
{
    public class Assembly
    {
        public string name;
        public string outputPath;
        public string[] sourceFiles;
        public string[] defines;
        public string[] references; // Names of referenced assemblies
        public string[] compiledAssemblyReferences;
        public UnityEditor.Compilation.AssemblyFlags flags;
        public ScriptCompilerOptions compilerOptions;
        public string rootNamespace;

        public Assembly(string name, string outputPath, string[] sourceFiles, string[] defines, string[] references, string[] compiledAssemblyReferences, UnityEditor.Compilation.AssemblyFlags flags, ScriptCompilerOptions compilerOptions, string rootNamespace)
        {
            this.name = name;
            this.outputPath = outputPath;
            this.sourceFiles = sourceFiles;
            this.defines = defines;
            this.references = references;
            this.compiledAssemblyReferences = compiledAssemblyReferences;
            this.flags = flags;
            this.compilerOptions = compilerOptions;
            this.rootNamespace = rootNamespace;
        }
    }

    public class ScriptCompilerOptions
    {
        public string[] ResponseFiles;
        public bool AllowUnsafeCode;
        public UnityEditor.ApiCompatibilityLevel ApiCompatibilityLevel;
        public string languageVersion; // To satisfy CS1061
    }
}
