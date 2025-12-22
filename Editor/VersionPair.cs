/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Unity Technologies.
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;

namespace Antigravity.Ide.Editor
{
    internal struct VersionPair
    {
        public Version IdeVersion;
        public Version LanguageVersion;

        public VersionPair(int ideMajor, int ideMinor, int langMajor, int langMinor)
        {
            IdeVersion = new Version(ideMajor, ideMinor);
            LanguageVersion = new Version(langMajor, langMinor);
        }
    }
}
