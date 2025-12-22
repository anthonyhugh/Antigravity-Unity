/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Unity Technologies.
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;

namespace Antigravity.Ide.Editor.Testing
{
    [Serializable]
    internal enum TestStatusAdaptor
    {
        Passed,
        Skipped,
        Inconclusive,
        Failed,
    }
}
