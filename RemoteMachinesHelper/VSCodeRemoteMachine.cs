// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Community.PowerToys.Run.Plugin.VSCodeWorkspaces.VSCodeHelper;

namespace Community.PowerToys.Run.Plugin.VSCodeWorkspaces.RemoteMachinesHelper
{
    public class VSCodeRemoteMachine : IEquatable<VSCodeRemoteMachine>
    {
        public string Host { get; set; } = string.Empty;

        public string User { get; set; } = string.Empty;

        public string HostName { get; set; } = string.Empty;

        public VSCodeInstance? VSCodeInstance { get; set; }
        public bool Equals(VSCodeRemoteMachine? other)
        {
            if (ReferenceEquals(null, other))
                return false;
            if (ReferenceEquals(this, other))
                return true;
            return Host == other.Host && User == other.User && HostName == other.HostName && Equals(VSCodeInstance, other.VSCodeInstance);
        }
        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj))
                return false;
            if (ReferenceEquals(this, obj))
                return true;
            if (obj?.GetType() != this.GetType())
                return false;
            return Equals((VSCodeRemoteMachine)obj);
        }
        public override int GetHashCode()
        {
            return HashCode.Combine(Host, User, HostName, VSCodeInstance);
        }
    }
}
