using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;

namespace Datadog.Trace.DuckTyping
{
    /// <summary>
    /// Duck Type
    /// </summary>
    public static partial class DuckType
    {
        /// <summary>
        /// Gets the Type.GetTypeFromHandle method info
        /// </summary>
        public static readonly MethodInfo GetTypeFromHandleMethodInfo = typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle));

        /// <summary>
        /// Gets the Enum.ToObject method info
        /// </summary>
        public static readonly MethodInfo EnumToObjectMethodInfo = typeof(Enum).GetMethod(nameof(Enum.ToObject), new[] { typeof(Type), typeof(object) });

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private static readonly object _locker = new object();
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private static readonly ConcurrentDictionary<TypesTuple, Lazy<CreateTypeResult>> DuckTypeCache = new ConcurrentDictionary<TypesTuple, Lazy<CreateTypeResult>>();
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private static readonly PropertyInfo DuckTypeInstancePropertyInfo = typeof(IDuckType).GetProperty(nameof(IDuckType.Instance));
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private static readonly MethodInfo _methodBuilderGetToken = typeof(MethodBuilder).GetMethod("GetToken", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private static readonly ConcurrentDictionary<object, ModuleBuilder> ModuleBuilderCache = new ConcurrentDictionary<object, ModuleBuilder>();

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private static long _typeCount = 0;
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private static long _dynamicMethodCount = 0;

        private static object[] loadContextArgs = new[] { typeof(ModuleBuilderHelper).Assembly.Location };

        internal static ModuleBuilder TargetTypeToModuleBuilder(Type targetType)
        {
#if NETFRAMEWORK
            return ModuleBuilderHelper.GetModuleBuilder();
#else
            // Do runtime check for !.NET Framework

            // Get AssemblyLoadContext
            Type assemblyLoadContextType = Type.GetType("System.Runtime.Loader.AssemblyLoadContext, System.Runtime.Loader", false);
            if (assemblyLoadContextType is null)
            {
                // Default behavior: Return ModuleBuilder from the already loaded Datadog.Trace assembly
                return ModuleBuilderHelper.GetModuleBuilder();
            }

            MethodInfo getLoadContextMethod = assemblyLoadContextType?.GetMethod("GetLoadContext", BindingFlags.Public | BindingFlags.Static);
            MethodInfo getDefaultMethod = assemblyLoadContextType?.GetMethod("get_Default", BindingFlags.Public | BindingFlags.Static);

            // Invoke System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(Assembly)
            object targetAssemblyLoadContext = getLoadContextMethod?.Invoke(null, new[] { targetType.Assembly }) ?? null;

            // Invoke System.Runtime.Loader.AssemblyLoadContext.Default property
            object defaultLoadContext = getDefaultMethod?.Invoke(null, null) ?? null;

            if (targetAssemblyLoadContext is null || targetAssemblyLoadContext == defaultLoadContext)
            {
                // Default behavior: Return ModuleBuilder from the already loaded Datadog.Trace assembly
                return ModuleBuilderHelper.GetModuleBuilder();
            }
            else
            {
                MethodInfo loadMethod = assemblyLoadContextType?.GetMethod("LoadFromAssemblyPath", BindingFlags.Public | BindingFlags.Instance);
                var moduleBuilderHelperAssembly = loadMethod?.Invoke(targetAssemblyLoadContext, loadContextArgs) as Assembly;
                var modulebuilderHelperType = moduleBuilderHelperAssembly?.GetType(typeof(ModuleBuilderHelper).FullName);
                var getModuleBuilderMethod = modulebuilderHelperType?.GetMethod("GetModuleBuilder", BindingFlags.Public | BindingFlags.Static);

                if (getModuleBuilderMethod is null)
                {
                    // Default behavior: Return ModuleBuilder from the already loaded Datadog.Trace assembly
                    return ModuleBuilderHelper.GetModuleBuilder();
                }
                else
                {
                    return (ModuleBuilder)getModuleBuilderMethod.Invoke(null, null);
                }
            }
#endif
        }

        /// <summary>
        /// DynamicMethods delegates cache
        /// </summary>
        /// <typeparam name="TProxyDelegate">Proxy delegate type</typeparam>
        public static class DelegateCache<TProxyDelegate>
            where TProxyDelegate : Delegate
        {
            private static TProxyDelegate _delegate;

            /// <summary>
            /// Get cached delegate from the DynamicMethod
            /// </summary>
            /// <returns>TProxyDelegate instance</returns>
            public static TProxyDelegate GetDelegate()
            {
                return _delegate;
            }

            /// <summary>
            /// Create delegate from a DynamicMethod index
            /// </summary>
            /// <param name="index">Dynamic method index</param>
            internal static void FillDelegate(int index)
            {
                var dynamicMethod = ILHelpersExtensions.GetDynamicMethodForIndex(index);
                var type = typeof(TProxyDelegate);
                _delegate = (TProxyDelegate)dynamicMethod.CreateDelegate(type);
            }
        }
    }
}
