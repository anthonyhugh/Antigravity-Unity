/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Unity Technologies.
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using UnityEditor.TestTools.TestRunner.Api;

namespace Antigravity.Ide.Editor.Testing
{
    [Serializable]
    internal class TestResultAdaptorContainer
    {
        public TestResultAdaptor[] TestResultAdaptors;
    }

    [Serializable]
    internal class TestResultAdaptor
    {
        public string Name;
        public string FullName;

        public int PassCount;
        public int FailCount;
        public int InconclusiveCount;
        public int SkipCount;

        public string ResultState;
        public string StackTrace;

        public TestStatusAdaptor TestStatus;

        public int Parent;

        public TestResultAdaptor(ITestResultAdaptor testResultAdaptor, int parent)
        {
            Name = testResultAdaptor.Name;
            FullName = testResultAdaptor.FullName;

            PassCount = testResultAdaptor.PassCount;
            FailCount = testResultAdaptor.FailCount;
            InconclusiveCount = testResultAdaptor.InconclusiveCount;
            SkipCount = testResultAdaptor.SkipCount;

            switch (testResultAdaptor.TestStatus)
            {
                case UnityEditor.TestTools.TestRunner.Api.TestStatus.Passed:
                    TestStatus = TestStatusAdaptor.Passed;
                    break;
                case UnityEditor.TestTools.TestRunner.Api.TestStatus.Skipped:
                    TestStatus = TestStatusAdaptor.Skipped;
                    break;
                case UnityEditor.TestTools.TestRunner.Api.TestStatus.Inconclusive:
                    TestStatus = TestStatusAdaptor.Inconclusive;
                    break;
                case UnityEditor.TestTools.TestRunner.Api.TestStatus.Failed:
                    TestStatus = TestStatusAdaptor.Failed;
                    break;
            }

            Parent = parent;
        }
    }
}
