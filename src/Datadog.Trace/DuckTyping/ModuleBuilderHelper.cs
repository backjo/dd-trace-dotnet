using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;

namespace Datadog.Trace.DuckTyping
{
    internal static class ModuleBuilderHelper
    {
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private static readonly object _locker = new object();
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private static ModuleBuilder _moduleBuilder = null;
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private static AssemblyBuilder _assemblyBuilder = null;

        public static ModuleBuilder GetModuleBuilder()
        {
            lock (_locker)
            {
                // Ensures the module builder
                if (_moduleBuilder is null)
                {
                    var id = Guid.NewGuid().ToString("N");
                    AssemblyName aName = new AssemblyName("DuckTypeAssembly._" + id);
                    _assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(aName, AssemblyBuilderAccess.Run);
                    _moduleBuilder = _assemblyBuilder.DefineDynamicModule("MainModule");
                }

                return _moduleBuilder;
            }
        }
    }
}
