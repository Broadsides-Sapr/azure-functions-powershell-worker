﻿//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

namespace Microsoft.Azure.Functions.PowerShellWorker.Durable
{
    using System;
    using System.Management.Automation;

    internal class PowerShellServices : IPowerShellServices
    {
        private const string SetFunctionInvocationContextCommand =
            "Microsoft.Azure.Functions.PowerShellWorker\\Set-FunctionInvocationContext";

        private readonly PowerShell _pwsh;
        private bool _hasSetOrchestrationContext = false;

        public PowerShellServices(PowerShell pwsh)
        {
            _pwsh = pwsh;
        }

        public PowerShell GetPowerShell()
        {
            return this._pwsh;
        }

        public bool UsesExternalDurableSDK()
        {
            this._pwsh.AddCommand("Import-Module")
                .AddParameter("Name", "DurableSDK")
                .InvokeAndClearCommands<Action<object>>();
            return false;
        }

        public void SetDurableClient(object durableClient)
        {
            _pwsh.AddCommand(SetFunctionInvocationContextCommand)
                .AddParameter("DurableClient", durableClient)
                .InvokeAndClearCommands();


            _hasSetOrchestrationContext = true;
        }

        public void SetOrchestrationContext(OrchestrationContext orchestrationContext)
        {
            _pwsh.AddCommand(SetFunctionInvocationContextCommand)
                .AddParameter("OrchestrationContext", orchestrationContext)
                .InvokeAndClearCommands();

            _hasSetOrchestrationContext = true;
        }

        public void AddParameter(string name, object value)
        {
            _pwsh.AddParameter(name, value);
        }

        public void ClearOrchestrationContext()
        {
            if (_hasSetOrchestrationContext)
            {
                _pwsh.AddCommand(SetFunctionInvocationContextCommand)
                    .AddParameter("Clear", true)
                    .InvokeAndClearCommands();
            }
        }

        public void TracePipelineObject()
        {
            _pwsh.AddCommand("Microsoft.Azure.Functions.PowerShellWorker\\Trace-PipelineObject");
        }

        public IAsyncResult BeginInvoke(PSDataCollection<object> output)
        {
            return _pwsh.BeginInvoke<object, object>(input: null, output);
        }

        public void EndInvoke(IAsyncResult asyncResult)
        {
            _pwsh.EndInvoke(asyncResult);
        }

        public void StopInvoke()
        {
            _pwsh.Stop();
        }

        public void ClearStreamsAndCommands()
        {
            _pwsh.Streams.ClearStreams();
            _pwsh.Commands.Clear();
        }
    }
}
