// Review-only shim: lets this source-built copy of Microsoft.IdentityModel.Protocols[.OpenIdConnect] use the
// internals that the official (Microsoft-signed) build reaches through InternalsVisibleTo.
using System.Runtime.CompilerServices;

[assembly: IgnoresAccessChecksTo("Microsoft.IdentityModel.Tokens")]
[assembly: IgnoresAccessChecksTo("Microsoft.IdentityModel.Logging")]
[assembly: IgnoresAccessChecksTo("Microsoft.IdentityModel.JsonWebTokens")]
[assembly: IgnoresAccessChecksTo("System.IdentityModel.Tokens.Jwt")]
[assembly: IgnoresAccessChecksTo("Microsoft.IdentityModel.Abstractions")]

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) { AssemblyName = assemblyName; }
        public string AssemblyName { get; }
    }
}
