/*---------------------------------------------------------------------------------------------
 * Copyright (c) Unity Technologies.
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.IO;
using System.Text;
using UnityEditor.Compilation;
using UnityEngine;

namespace Antigravity.Ide.Editor
{
    // Restored to satisfy AntigravityInstallation.cs
    internal class SdkStyleProjectGeneration : ProjectGeneration
    {
        // We set the style name, but we inherit the ProjectText generation from the base class (Legacy)
        // so that references are explicitly hinted and loaded correctly in VS Code.
        internal override string StyleName => "SDK";

        // We do NOT override GetProjectHeader or GetProjectFooter.
        // This ensures it uses the base class's "Legacy" XML format (ToolsVersion="4.0"),
        // which includes hard-coded paths to your DLLs.
    }
}