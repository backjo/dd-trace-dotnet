using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Datadog.Trace.DuckTyping;
using Datadog.Trace.ExtensionMethods;
using Datadog.Trace.Logging;

namespace Datadog.Trace.ClrProfiler.CallTarget
{
    internal class CallTargetInvokerHandler
    {
        private const string BeginMethodName = "OnMethodBegin";
        private const string EndMethodName = "OnMethodEnd";
        private const string EndAsyncMethodName = "OnAsyncMethodEnd";

        private static readonly Vendors.Serilog.ILogger Log = DatadogLogging.GetLogger(typeof(CallTargetInvokerHandler));
        private static readonly MethodInfo UnwrapReturnValueMethodInfo = typeof(CallTargetInvokerHandler).GetMethod(nameof(CallTargetInvokerHandler.UnwrapReturnValue), BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo ConvertTypeMethodInfo = typeof(CallTargetInvokerHandler).GetMethod(nameof(CallTargetInvokerHandler.ConvertType), BindingFlags.NonPublic | BindingFlags.Static);

        internal static DynamicMethod CreateBeginMethodDelegate(Type integrationType, Type targetType, Type[] argumentsTypes)
        {
            Log.Debug($"Creating BeginMethod Dynamic Method for '{integrationType.FullName}' integration. [Target={targetType.FullName}]");
            MethodInfo onMethodBeginMethodInfo = integrationType.GetMethod(BeginMethodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (onMethodBeginMethodInfo is null)
            {
                throw new NullReferenceException($"Couldn't find the method: {BeginMethodName} in type: {integrationType.FullName}");
            }

            if (onMethodBeginMethodInfo.ReturnType != typeof(CallTargetState))
            {
                throw new ArgumentException($"The return type of the method: {BeginMethodName} in type: {integrationType.FullName} is not {nameof(CallTargetState)}");
            }

            Type[] genericArgumentsTypes = onMethodBeginMethodInfo.GetGenericArguments();
            if (genericArgumentsTypes.Length < 1)
            {
                throw new ArgumentException($"The method: {BeginMethodName} in type: {integrationType.FullName} doesn't have the generic type for the instance type.");
            }

            ParameterInfo[] onMethodBeginParameters = onMethodBeginMethodInfo.GetParameters();
            if (onMethodBeginParameters.Length < argumentsTypes.Length)
            {
                throw new ArgumentException($"The method: {BeginMethodName} with {onMethodBeginParameters.Length} paremeters in type: {integrationType.FullName} has less parameters than required.");
            }
            else if (onMethodBeginParameters.Length > argumentsTypes.Length + 1)
            {
                throw new ArgumentException($"The method: {BeginMethodName} with {onMethodBeginParameters.Length} paremeters in type: {integrationType.FullName} has more parameters than required.");
            }
            else if (onMethodBeginParameters.Length != argumentsTypes.Length && onMethodBeginParameters[0].ParameterType != genericArgumentsTypes[0])
            {
                throw new ArgumentException($"The first generic argument for method: {BeginMethodName} in type: {integrationType.FullName} must be the same as the first parameter for the instance value.");
            }

            List<Type> callGenericTypes = new List<Type>();

            bool mustLoadInstance = onMethodBeginParameters.Length != argumentsTypes.Length;
            Type instanceGenericType = genericArgumentsTypes[0];
            Type instanceGenericConstraint = instanceGenericType.GetGenericParameterConstraints().FirstOrDefault();
            Type instanceProxyType = null;
            if (instanceGenericConstraint != null)
            {
                var result = DuckType.GetOrCreateProxyType(instanceGenericConstraint, targetType);
                instanceProxyType = result.ProxyType;
                callGenericTypes.Add(instanceProxyType);
            }
            else
            {
                callGenericTypes.Add(targetType);
            }

            DynamicMethod callMethod = new DynamicMethod(
                     $"{onMethodBeginMethodInfo.DeclaringType.Name}.{onMethodBeginMethodInfo.Name}",
                     typeof(CallTargetState),
                     new Type[] { targetType }.Concat(argumentsTypes),
                     onMethodBeginMethodInfo.Module,
                     true);

            ILGenerator ilWriter = callMethod.GetILGenerator();

            // Load the instance if is needed
            if (mustLoadInstance)
            {
                ilWriter.Emit(OpCodes.Ldarg_0);

                if (instanceGenericConstraint != null)
                {
                    ConstructorInfo ctor = instanceProxyType.GetConstructors()[0];
                    if (targetType.IsValueType && !ctor.GetParameters()[0].ParameterType.IsValueType)
                    {
                        ilWriter.Emit(OpCodes.Box, targetType);
                    }

                    ilWriter.Emit(OpCodes.Newobj, ctor);
                }
            }

            // Load arguments
            for (var i = mustLoadInstance ? 1 : 0; i < onMethodBeginParameters.Length; i++)
            {
                Type sourceParameterType = argumentsTypes[mustLoadInstance ? i - 1 : i];
                Type targetParameterType = onMethodBeginParameters[i].ParameterType;
                Type targetParameterTypeConstraint = null;
                Type parameterProxyType = null;

                if (targetParameterType.IsGenericParameter)
                {
                    targetParameterType = genericArgumentsTypes[targetParameterType.GenericParameterPosition];
                    targetParameterTypeConstraint = targetParameterType.GetGenericParameterConstraints().FirstOrDefault(pType => pType != typeof(IDuckType));
                    if (targetParameterTypeConstraint is null)
                    {
                        callGenericTypes.Add(sourceParameterType);
                    }
                    else
                    {
                        var result = DuckType.GetOrCreateProxyType(targetParameterTypeConstraint, sourceParameterType);
                        parameterProxyType = result.ProxyType;
                        callGenericTypes.Add(parameterProxyType);
                    }
                }
                else if (!targetParameterType.IsAssignableFrom(sourceParameterType) && (!(sourceParameterType.IsEnum && targetParameterType.IsEnum)))
                {
                    throw new InvalidCastException($"The target parameter {targetParameterType} can't be assigned from {sourceParameterType}");
                }

                ILHelpersExtensions.WriteLoadArgument(ilWriter, i, mustLoadInstance);
                if (parameterProxyType != null)
                {
                    ConstructorInfo parameterProxyTypeCtor = parameterProxyType.GetConstructors()[0];

                    if (sourceParameterType.IsValueType && !parameterProxyTypeCtor.GetParameters()[0].ParameterType.IsValueType)
                    {
                        ilWriter.Emit(OpCodes.Box, sourceParameterType);
                    }

                    ilWriter.Emit(OpCodes.Newobj, parameterProxyTypeCtor);
                }
            }

            // Call method
            // Log.Information("Generic Types: " + string.Join(", ", callGenericTypes.Select(t => t.FullName)));
            // Log.Information("Method: " + onMethodBeginMethodInfo);
            onMethodBeginMethodInfo = onMethodBeginMethodInfo.MakeGenericMethod(callGenericTypes.ToArray());
            // Log.Information("Method: " + onMethodBeginMethodInfo);
            ilWriter.EmitCall(OpCodes.Call, onMethodBeginMethodInfo, null);
            ilWriter.Emit(OpCodes.Ret);

            Log.Debug($"Created BeginMethod Dynamic Method for '{integrationType.FullName}' integration. [Target={targetType.FullName}]");
            return callMethod;
        }

        internal static DynamicMethod CreateSlowBeginMethodDelegate(Type integrationType, Type targetType)
        {
            Log.Debug($"Creating SlowBeginMethod Dynamic Method for '{integrationType.FullName}' integration. [Target={targetType.FullName}]");
            MethodInfo onMethodBeginMethodInfo = integrationType.GetMethod(BeginMethodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (onMethodBeginMethodInfo is null)
            {
                throw new NullReferenceException($"Couldn't find the method: {BeginMethodName} in type: {integrationType.FullName}");
            }

            if (onMethodBeginMethodInfo.ReturnType != typeof(CallTargetState))
            {
                throw new ArgumentException($"The return type of the method: {BeginMethodName} in type: {integrationType.FullName} is not {nameof(CallTargetState)}");
            }

            Type[] genericArgumentsTypes = onMethodBeginMethodInfo.GetGenericArguments();
            if (genericArgumentsTypes.Length < 1)
            {
                throw new ArgumentException($"The method: {BeginMethodName} in type: {integrationType.FullName} doesn't have the generic type for the instance type.");
            }

            ParameterInfo[] onMethodBeginParameters = onMethodBeginMethodInfo.GetParameters();

            List<Type> callGenericTypes = new List<Type>();

            bool mustLoadInstance = onMethodBeginParameters[0].ParameterType.IsGenericParameter && onMethodBeginParameters[0].ParameterType.GenericParameterPosition == 0;
            Type instanceGenericType = genericArgumentsTypes[0];
            Type instanceGenericConstraint = instanceGenericType.GetGenericParameterConstraints().FirstOrDefault();
            Type instanceProxyType = null;
            if (instanceGenericConstraint != null)
            {
                var result = DuckType.GetOrCreateProxyType(instanceGenericConstraint, targetType);
                instanceProxyType = result.ProxyType;
                callGenericTypes.Add(instanceProxyType);
            }
            else
            {
                callGenericTypes.Add(targetType);
            }

            DynamicMethod callMethod = new DynamicMethod(
                     $"{onMethodBeginMethodInfo.DeclaringType.Name}.{onMethodBeginMethodInfo.Name}",
                     typeof(CallTargetState),
                     new Type[] { targetType, typeof(object[]) },
                     onMethodBeginMethodInfo.Module,
                     true);

            ILGenerator ilWriter = callMethod.GetILGenerator();

            // Load the instance if is needed
            if (mustLoadInstance)
            {
                ilWriter.Emit(OpCodes.Ldarg_0);

                if (instanceGenericConstraint != null)
                {
                    ConstructorInfo ctor = instanceProxyType.GetConstructors()[0];
                    if (targetType.IsValueType && !ctor.GetParameters()[0].ParameterType.IsValueType)
                    {
                        ilWriter.Emit(OpCodes.Box, targetType);
                    }

                    ilWriter.Emit(OpCodes.Newobj, ctor);
                }
            }

            // Load arguments
            for (var i = mustLoadInstance ? 1 : 0; i < onMethodBeginParameters.Length; i++)
            {
                Type targetParameterType = onMethodBeginParameters[i].ParameterType;
                Type targetParameterTypeConstraint = null;

                if (targetParameterType.IsGenericParameter)
                {
                    targetParameterType = genericArgumentsTypes[targetParameterType.GenericParameterPosition];

                    targetParameterTypeConstraint = targetParameterType.GetGenericParameterConstraints().FirstOrDefault(pType => pType != typeof(IDuckType));
                    if (targetParameterTypeConstraint is null)
                    {
                        callGenericTypes.Add(typeof(object));
                    }
                    else
                    {
                        targetParameterType = targetParameterTypeConstraint;
                        callGenericTypes.Add(targetParameterTypeConstraint);
                    }
                }

                ilWriter.Emit(OpCodes.Ldarg_1);
                WriteIntValue(ilWriter, i - (mustLoadInstance ? 1 : 0));
                ilWriter.Emit(OpCodes.Ldelem_Ref);

                if (targetParameterTypeConstraint != null)
                {
                    ilWriter.EmitCall(OpCodes.Call, ConvertTypeMethodInfo.MakeGenericMethod(targetParameterTypeConstraint), null);
                }

                if (targetParameterType.IsValueType)
                {
                    ilWriter.Emit(OpCodes.Unbox_Any, targetParameterType);
                }
            }

            // Call method
            // Log.Information("Generic Types: " + string.Join(", ", callGenericTypes.Select(t => t.FullName)));
            // Log.Information("Method: " + onMethodBeginMethodInfo);
            onMethodBeginMethodInfo = onMethodBeginMethodInfo.MakeGenericMethod(callGenericTypes.ToArray());
            // Log.Information("Method: " + onMethodBeginMethodInfo);
            ilWriter.EmitCall(OpCodes.Call, onMethodBeginMethodInfo, null);
            ilWriter.Emit(OpCodes.Ret);

            Log.Debug($"Created SlowBeginMethod Dynamic Method for '{integrationType.FullName}' integration. [Target={targetType.FullName}]");
            return callMethod;
        }

        internal static DynamicMethod CreateEndMethodDelegate(Type integrationType, Type targetType)
        {
            Log.Debug($"Creating EndMethod Dynamic Method for '{integrationType.FullName}' integration. [Target={targetType.FullName}]");
            MethodInfo onMethodEndMethodInfo = integrationType.GetMethod(EndMethodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (onMethodEndMethodInfo is null)
            {
                throw new NullReferenceException($"Couldn't find the method: {EndMethodName} in type: {integrationType.FullName}");
            }

            if (onMethodEndMethodInfo.ReturnType != typeof(CallTargetReturn))
            {
                throw new ArgumentException($"The return type of the method: {EndMethodName} in type: {integrationType.FullName} is not {nameof(CallTargetReturn)}");
            }

            Type[] genericArgumentsTypes = onMethodEndMethodInfo.GetGenericArguments();
            if (genericArgumentsTypes.Length != 1)
            {
                throw new ArgumentException($"The method: {EndMethodName} in type: {integrationType.FullName} must have a single generic type for the instance type.");
            }

            ParameterInfo[] onMethodEndParameters = onMethodEndMethodInfo.GetParameters();
            if (onMethodEndParameters.Length < 2)
            {
                throw new ArgumentException($"The method: {EndMethodName} with {onMethodEndParameters.Length} paremeters in type: {integrationType.FullName} has less parameters than required.");
            }
            else if (onMethodEndParameters.Length > 3)
            {
                throw new ArgumentException($"The method: {EndMethodName} with {onMethodEndParameters.Length} paremeters in type: {integrationType.FullName} has more parameters than required.");
            }

            if (onMethodEndParameters[0].ParameterType != typeof(Exception) && onMethodEndParameters[1].ParameterType != typeof(Exception))
            {
                throw new ArgumentException($"The Exception type parameter of the method: {EndMethodName} in type: {integrationType.FullName} is missing.");
            }

            if (onMethodEndParameters[1].ParameterType != typeof(CallTargetState) && onMethodEndParameters[2].ParameterType != typeof(CallTargetState))
            {
                throw new ArgumentException($"The CallTargetState type parameter of the method: {EndMethodName} in type: {integrationType.FullName} is missing.");
            }

            List<Type> callGenericTypes = new List<Type>();

            bool mustLoadInstance = onMethodEndParameters[0].ParameterType.IsGenericParameter;
            Type instanceGenericType = genericArgumentsTypes[0];
            Type instanceGenericConstraint = instanceGenericType.GetGenericParameterConstraints().FirstOrDefault();
            Type instanceProxyType = null;
            if (instanceGenericConstraint != null)
            {
                var result = DuckType.GetOrCreateProxyType(instanceGenericConstraint, targetType);
                instanceProxyType = result.ProxyType;
                callGenericTypes.Add(instanceProxyType);
            }
            else
            {
                callGenericTypes.Add(targetType);
            }

            DynamicMethod callMethod = new DynamicMethod(
                     $"{onMethodEndMethodInfo.DeclaringType.Name}.{onMethodEndMethodInfo.Name}",
                     typeof(CallTargetReturn),
                     new Type[] { targetType, typeof(Exception), typeof(CallTargetState) },
                     onMethodEndMethodInfo.Module,
                     true);

            ILGenerator ilWriter = callMethod.GetILGenerator();

            // Load the instance if is needed
            if (mustLoadInstance)
            {
                ilWriter.Emit(OpCodes.Ldarg_0);

                if (instanceGenericConstraint != null)
                {
                    ConstructorInfo ctor = instanceProxyType.GetConstructors()[0];
                    if (targetType.IsValueType && !ctor.GetParameters()[0].ParameterType.IsValueType)
                    {
                        ilWriter.Emit(OpCodes.Box, targetType);
                    }

                    ilWriter.Emit(OpCodes.Newobj, ctor);
                }
            }

            // Load the exception
            ilWriter.Emit(OpCodes.Ldarg_1);

            // Load the state
            ilWriter.Emit(OpCodes.Ldarg_2);

            // Call Method
            onMethodEndMethodInfo = onMethodEndMethodInfo.MakeGenericMethod(callGenericTypes.ToArray());
            ilWriter.EmitCall(OpCodes.Call, onMethodEndMethodInfo, null);

            ilWriter.Emit(OpCodes.Ret);

            Log.Debug($"Created EndMethod Dynamic Method for '{integrationType.FullName}' integration. [Target={targetType.FullName}]");
            return callMethod;
        }

        internal static DynamicMethod CreateEndMethodDelegate(Type integrationType, Type targetType, Type returnType)
        {
            Log.Debug($"Creating EndMethod Dynamic Method for '{integrationType.FullName}' integration. [Target={targetType.FullName}, ReturnType={returnType.FullName}]");
            MethodInfo onMethodEndMethodInfo = integrationType.GetMethod(EndMethodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (onMethodEndMethodInfo is null)
            {
                throw new NullReferenceException($"Couldn't find the method: {EndMethodName} in type: {integrationType.FullName}");
            }

            if (onMethodEndMethodInfo.ReturnType.GetGenericTypeDefinition() != typeof(CallTargetReturn<>))
            {
                throw new ArgumentException($"The return type of the method: {EndMethodName} in type: {integrationType.FullName} is not {nameof(CallTargetReturn)}");
            }

            Type[] genericArgumentsTypes = onMethodEndMethodInfo.GetGenericArguments();
            if (genericArgumentsTypes.Length < 1 || genericArgumentsTypes.Length > 2)
            {
                throw new ArgumentException($"The method: {EndMethodName} in type: {integrationType.FullName} must have the generic type for the instance type.");
            }

            ParameterInfo[] onMethodEndParameters = onMethodEndMethodInfo.GetParameters();
            if (onMethodEndParameters.Length < 3)
            {
                throw new ArgumentException($"The method: {EndMethodName} with {onMethodEndParameters.Length} paremeters in type: {integrationType.FullName} has less parameters than required.");
            }
            else if (onMethodEndParameters.Length > 4)
            {
                throw new ArgumentException($"The method: {EndMethodName} with {onMethodEndParameters.Length} paremeters in type: {integrationType.FullName} has more parameters than required.");
            }

            if (onMethodEndParameters[1].ParameterType != typeof(Exception) && onMethodEndParameters[2].ParameterType != typeof(Exception))
            {
                throw new ArgumentException($"The Exception type parameter of the method: {EndMethodName} in type: {integrationType.FullName} is missing.");
            }

            if (onMethodEndParameters[2].ParameterType != typeof(CallTargetState) && onMethodEndParameters[3].ParameterType != typeof(CallTargetState))
            {
                throw new ArgumentException($"The CallTargetState type parameter of the method: {EndMethodName} in type: {integrationType.FullName} is missing.");
            }

            List<Type> callGenericTypes = new List<Type>();

            bool mustLoadInstance = onMethodEndParameters[0].ParameterType.IsGenericParameter;
            Type instanceGenericType = genericArgumentsTypes[0];
            Type instanceGenericConstraint = instanceGenericType.GetGenericParameterConstraints().FirstOrDefault();
            Type instanceProxyType = null;
            if (instanceGenericConstraint != null)
            {
                var result = DuckType.GetOrCreateProxyType(instanceGenericConstraint, targetType);
                instanceProxyType = result.ProxyType;
                callGenericTypes.Add(instanceProxyType);
            }
            else
            {
                callGenericTypes.Add(targetType);
            }

            bool isAGenericReturnValue = onMethodEndParameters[1].ParameterType.IsGenericParameter;
            Type returnValueGenericType = null;
            Type returnValueGenericConstraint = null;
            Type returnValueProxyType = null;
            if (isAGenericReturnValue)
            {
                returnValueGenericType = genericArgumentsTypes[1];
                returnValueGenericConstraint = returnValueGenericType.GetGenericParameterConstraints().FirstOrDefault();
                if (returnValueGenericConstraint != null)
                {
                    var result = DuckType.GetOrCreateProxyType(returnValueGenericConstraint, returnType);
                    returnValueProxyType = result.ProxyType;
                    callGenericTypes.Add(returnValueProxyType);
                }
                else
                {
                    callGenericTypes.Add(returnType);
                }
            }
            else if (onMethodEndParameters[1].ParameterType != returnType)
            {
                throw new ArgumentException($"The ReturnValue type parameter of the method: {EndMethodName} in type: {integrationType.FullName} is invalid. [{onMethodEndParameters[1].ParameterType} != {returnType}]");
            }

            DynamicMethod callMethod = new DynamicMethod(
                     $"{onMethodEndMethodInfo.DeclaringType.Name}.{onMethodEndMethodInfo.Name}.{targetType.Name}.{returnType.Name}",
                     typeof(CallTargetReturn<>).MakeGenericType(returnType),
                     new Type[] { targetType, returnType, typeof(Exception), typeof(CallTargetState) },
                     onMethodEndMethodInfo.Module,
                     true);

            ILGenerator ilWriter = callMethod.GetILGenerator();

            // Load the instance if is needed
            if (mustLoadInstance)
            {
                ilWriter.Emit(OpCodes.Ldarg_0);

                if (instanceGenericConstraint != null)
                {
                    ConstructorInfo ctor = instanceProxyType.GetConstructors()[0];
                    if (targetType.IsValueType && !ctor.GetParameters()[0].ParameterType.IsValueType)
                    {
                        ilWriter.Emit(OpCodes.Box, targetType);
                    }

                    ilWriter.Emit(OpCodes.Newobj, ctor);
                }
            }

            // Load the return value
            ilWriter.Emit(OpCodes.Ldarg_1);
            if (returnValueProxyType != null)
            {
                ConstructorInfo ctor = returnValueProxyType.GetConstructors()[0];
                if (returnType.IsValueType && !ctor.GetParameters()[0].ParameterType.IsValueType)
                {
                    ilWriter.Emit(OpCodes.Box, returnType);
                }

                ilWriter.Emit(OpCodes.Newobj, ctor);
            }

            // Load the exception
            ilWriter.Emit(OpCodes.Ldarg_2);

            // Load the state
            ilWriter.Emit(OpCodes.Ldarg_3);

            // Call Method
            onMethodEndMethodInfo = onMethodEndMethodInfo.MakeGenericMethod(callGenericTypes.ToArray());
            ilWriter.EmitCall(OpCodes.Call, onMethodEndMethodInfo, null);

            // Unwrap return value proxy
            if (returnValueProxyType != null)
            {
                MethodInfo unwrapReturnValue = UnwrapReturnValueMethodInfo.MakeGenericMethod(returnValueProxyType, returnType);
                ilWriter.EmitCall(OpCodes.Call, unwrapReturnValue, null);
            }

            ilWriter.Emit(OpCodes.Ret);

            Log.Debug($"Created EndMethod Dynamic Method for '{integrationType.FullName}' integration. [Target={targetType.FullName}, ReturnType={returnType.FullName}]");
            return callMethod;
        }

        private static CallTargetReturn<TTo> UnwrapReturnValue<TFrom, TTo>(CallTargetReturn<TFrom> returnValue)
            where TFrom : IDuckType
        {
            var innerValue = returnValue.GetReturnValue();
            return new CallTargetReturn<TTo>((TTo)innerValue.Instance);
        }

        private static void WriteIntValue(ILGenerator il, int value)
        {
            switch (value)
            {
                case 0:
                    il.Emit(OpCodes.Ldc_I4_0);
                    break;
                case 1:
                    il.Emit(OpCodes.Ldc_I4_1);
                    break;
                case 2:
                    il.Emit(OpCodes.Ldc_I4_2);
                    break;
                case 3:
                    il.Emit(OpCodes.Ldc_I4_3);
                    break;
                case 4:
                    il.Emit(OpCodes.Ldc_I4_4);
                    break;
                case 5:
                    il.Emit(OpCodes.Ldc_I4_5);
                    break;
                case 6:
                    il.Emit(OpCodes.Ldc_I4_6);
                    break;
                case 7:
                    il.Emit(OpCodes.Ldc_I4_7);
                    break;
                case 8:
                    il.Emit(OpCodes.Ldc_I4_8);
                    break;
                default:
                    il.Emit(OpCodes.Ldc_I4, value);
                    break;
            }
        }

        private static T ConvertType<T>(object value)
        {
            var conversionType = typeof(T);
            if (value is null || conversionType == typeof(object))
            {
                return (T)value;
            }

            Type valueType = value.GetType();
            if (valueType == conversionType || conversionType.IsAssignableFrom(valueType))
            {
                return (T)value;
            }

            // Finally we try to duck type
            return DuckType.Create<T>(value);
        }

        internal static class IntegrationOptions<TIntegration, TTarget>
        {
            private static readonly Vendors.Serilog.ILogger Log = DatadogLogging.GetLogger(typeof(IntegrationOptions<TIntegration, TTarget>));

            private static volatile bool _disableIntegration = false;

            internal static bool IsIntegrationEnabled => !_disableIntegration;

            internal static void DisableIntegration() => _disableIntegration = true;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void LogException(Exception exception)
            {
                Log.SafeLogError(exception, exception?.Message, null);
                if (exception is DuckTypeException)
                {
                    Log.Warning($"DuckTypeException has been detected, the integration <{typeof(TIntegration)}, {typeof(TTarget)}> will be disabled.");
                    _disableIntegration = true;
                }
                else if (exception is CallTargetInvokerException)
                {
                    Log.Warning($"CallTargetInvokerException has been detected, the integration <{typeof(TIntegration)}, {typeof(TTarget)}> will be disabled.");
                    _disableIntegration = true;
                }
            }
        }

        internal static class BeginMethodHandler<TIntegration, TTarget>
        {
            private static readonly InvokeDelegate _invokeDelegate;

            static BeginMethodHandler()
            {
                try
                {
                    DynamicMethod dynMethod = CreateBeginMethodDelegate(typeof(TIntegration), typeof(TTarget), Util.ArrayHelper.Empty<Type>());
                    if (dynMethod != null)
                    {
                        _invokeDelegate = (InvokeDelegate)dynMethod.CreateDelegate(typeof(InvokeDelegate));
                    }
                }
                catch (Exception ex)
                {
                    throw new CallTargetInvokerException(ex);
                }
                finally
                {
                    if (_invokeDelegate is null)
                    {
                        _invokeDelegate = instance => CallTargetState.GetDefault();
                    }
                }
            }

            internal delegate CallTargetState InvokeDelegate(TTarget instance);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static CallTargetState Invoke(TTarget instance)
                => _invokeDelegate(instance);
        }

        internal static class BeginMethodHandler<TIntegration, TTarget, TArg1>
        {
            private static readonly InvokeDelegate _invokeDelegate;

            static BeginMethodHandler()
            {
                try
                {
                    DynamicMethod dynMethod = CreateBeginMethodDelegate(typeof(TIntegration), typeof(TTarget), new[] { typeof(TArg1) });
                    if (dynMethod != null)
                    {
                        _invokeDelegate = (InvokeDelegate)dynMethod.CreateDelegate(typeof(InvokeDelegate));
                    }
                }
                catch (Exception ex)
                {
                    throw new CallTargetInvokerException(ex);
                }
                finally
                {
                    if (_invokeDelegate is null)
                    {
                        _invokeDelegate = (instance, arg1) => CallTargetState.GetDefault();
                    }
                }
            }

            internal delegate CallTargetState InvokeDelegate(TTarget instance, TArg1 arg1);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static CallTargetState Invoke(TTarget instance, TArg1 arg1)
                => _invokeDelegate(instance, arg1);
        }

        internal static class BeginMethodHandler<TIntegration, TTarget, TArg1, TArg2>
        {
            private static readonly InvokeDelegate _invokeDelegate;

            static BeginMethodHandler()
            {
                try
                {
                    DynamicMethod dynMethod = CreateBeginMethodDelegate(typeof(TIntegration), typeof(TTarget), new[] { typeof(TArg1), typeof(TArg2) });
                    if (dynMethod != null)
                    {
                        _invokeDelegate = (InvokeDelegate)dynMethod.CreateDelegate(typeof(InvokeDelegate));
                    }
                }
                catch (Exception ex)
                {
                    throw new CallTargetInvokerException(ex);
                }
                finally
                {
                    if (_invokeDelegate is null)
                    {
                        _invokeDelegate = (instance, arg1, arg2) => CallTargetState.GetDefault();
                    }
                }
            }

            internal delegate CallTargetState InvokeDelegate(TTarget instance, TArg1 arg1, TArg2 arg2);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static CallTargetState Invoke(TTarget instance, TArg1 arg1, TArg2 arg2)
                => _invokeDelegate(instance, arg1, arg2);
        }

        internal static class BeginMethodHandler<TIntegration, TTarget, TArg1, TArg2, TArg3>
        {
            private static readonly InvokeDelegate _invokeDelegate;

            static BeginMethodHandler()
            {
                try
                {
                    DynamicMethod dynMethod = CreateBeginMethodDelegate(typeof(TIntegration), typeof(TTarget), new[] { typeof(TArg1), typeof(TArg2), typeof(TArg3) });
                    if (dynMethod != null)
                    {
                        _invokeDelegate = (InvokeDelegate)dynMethod.CreateDelegate(typeof(InvokeDelegate));
                    }
                }
                catch (Exception ex)
                {
                    throw new CallTargetInvokerException(ex);
                }
                finally
                {
                    if (_invokeDelegate is null)
                    {
                        _invokeDelegate = (instance, arg1, arg2, arg3) => CallTargetState.GetDefault();
                    }
                }
            }

            internal delegate CallTargetState InvokeDelegate(TTarget instance, TArg1 arg1, TArg2 arg2, TArg3 arg3);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static CallTargetState Invoke(TTarget instance, TArg1 arg1, TArg2 arg2, TArg3 arg3)
                => _invokeDelegate(instance, arg1, arg2, arg3);
        }

        internal static class BeginMethodHandler<TIntegration, TTarget, TArg1, TArg2, TArg3, TArg4>
        {
            private static readonly InvokeDelegate _invokeDelegate;

            static BeginMethodHandler()
            {
                try
                {
                    DynamicMethod dynMethod = CreateBeginMethodDelegate(typeof(TIntegration), typeof(TTarget), new[] { typeof(TArg1), typeof(TArg2), typeof(TArg3), typeof(TArg4) });
                    if (dynMethod != null)
                    {
                        _invokeDelegate = (InvokeDelegate)dynMethod.CreateDelegate(typeof(InvokeDelegate));
                    }
                }
                catch (Exception ex)
                {
                    throw new CallTargetInvokerException(ex);
                }
                finally
                {
                    if (_invokeDelegate is null)
                    {
                        _invokeDelegate = (instance, arg1, arg2, arg3, arg4) => CallTargetState.GetDefault();
                    }
                }
            }

            internal delegate CallTargetState InvokeDelegate(TTarget instance, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static CallTargetState Invoke(TTarget instance, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4)
                => _invokeDelegate(instance, arg1, arg2, arg3, arg4);
        }

        internal static class BeginMethodHandler<TIntegration, TTarget, TArg1, TArg2, TArg3, TArg4, TArg5>
        {
            private static readonly InvokeDelegate _invokeDelegate;

            static BeginMethodHandler()
            {
                try
                {
                    DynamicMethod dynMethod = CreateBeginMethodDelegate(typeof(TIntegration), typeof(TTarget), new[] { typeof(TArg1), typeof(TArg2), typeof(TArg3), typeof(TArg4), typeof(TArg5) });
                    if (dynMethod != null)
                    {
                        _invokeDelegate = (InvokeDelegate)dynMethod.CreateDelegate(typeof(InvokeDelegate));
                    }
                }
                catch (Exception ex)
                {
                    throw new CallTargetInvokerException(ex);
                }
                finally
                {
                    if (_invokeDelegate is null)
                    {
                        _invokeDelegate = (instance, arg1, arg2, arg3, arg4, arg5) => CallTargetState.GetDefault();
                    }
                }
            }

            internal delegate CallTargetState InvokeDelegate(TTarget instance, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static CallTargetState Invoke(TTarget instance, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5)
                => _invokeDelegate(instance, arg1, arg2, arg3, arg4, arg5);
        }

        internal static class BeginMethodHandler<TIntegration, TTarget, TArg1, TArg2, TArg3, TArg4, TArg5, TArg6>
        {
            private static readonly InvokeDelegate _invokeDelegate;

            static BeginMethodHandler()
            {
                try
                {
                    DynamicMethod dynMethod = CreateBeginMethodDelegate(typeof(TIntegration), typeof(TTarget), new[] { typeof(TArg1), typeof(TArg2), typeof(TArg3), typeof(TArg4), typeof(TArg5), typeof(TArg6) });
                    if (dynMethod != null)
                    {
                        _invokeDelegate = (InvokeDelegate)dynMethod.CreateDelegate(typeof(InvokeDelegate));
                    }
                }
                catch (Exception ex)
                {
                    throw new CallTargetInvokerException(ex);
                }
                finally
                {
                    if (_invokeDelegate is null)
                    {
                        _invokeDelegate = (instance, arg1, arg2, arg3, arg4, arg5, arg6) => CallTargetState.GetDefault();
                    }
                }
            }

            internal delegate CallTargetState InvokeDelegate(TTarget instance, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5, TArg6 arg6);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static CallTargetState Invoke(TTarget instance, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5, TArg6 arg6)
                => _invokeDelegate(instance, arg1, arg2, arg3, arg4, arg5, arg6);
        }

        internal static class BeginMethodSlowHandler<TIntegration, TTarget>
        {
            private static readonly InvokeDelegate _invokeDelegate;

            static BeginMethodSlowHandler()
            {
                try
                {
                    DynamicMethod dynMethod = CreateSlowBeginMethodDelegate(typeof(TIntegration), typeof(TTarget));
                    if (dynMethod != null)
                    {
                        _invokeDelegate = (InvokeDelegate)dynMethod.CreateDelegate(typeof(InvokeDelegate));
                    }
                }
                catch (Exception ex)
                {
                    throw new CallTargetInvokerException(ex);
                }
                finally
                {
                    if (_invokeDelegate is null)
                    {
                        _invokeDelegate = (instance, arguments) => CallTargetState.GetDefault();
                    }
                }
            }

            internal delegate CallTargetState InvokeDelegate(TTarget instance, object[] arguments);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static CallTargetState Invoke(TTarget instance, object[] arguments)
                => _invokeDelegate(instance, arguments);
        }

        internal static class EndMethodHandler<TIntegration, TTarget>
        {
            private static readonly InvokeDelegate _invokeDelegate;

            static EndMethodHandler()
            {
                try
                {
                    DynamicMethod dynMethod = CreateEndMethodDelegate(typeof(TIntegration), typeof(TTarget));
                    if (dynMethod != null)
                    {
                        _invokeDelegate = (InvokeDelegate)dynMethod.CreateDelegate(typeof(InvokeDelegate));
                    }
                }
                catch (Exception ex)
                {
                    throw new CallTargetInvokerException(ex);
                }
                finally
                {
                    if (_invokeDelegate is null)
                    {
                        _invokeDelegate = (instance, exception, state) => CallTargetReturn.GetDefault();
                    }
                }
            }

            internal delegate CallTargetReturn InvokeDelegate(TTarget instance, Exception exception, CallTargetState state);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static CallTargetReturn Invoke(TTarget instance, Exception exception, CallTargetState state)
                => _invokeDelegate(instance, exception, state);
        }

        internal static class EndMethodHandler<TIntegration, TTarget, TReturn>
        {
            private static readonly InvokeDelegate _invokeDelegate;

            static EndMethodHandler()
            {
                try
                {
                    DynamicMethod dynMethod = CreateEndMethodDelegate(typeof(TIntegration), typeof(TTarget), typeof(TReturn));
                    if (dynMethod != null)
                    {
                        _invokeDelegate = (InvokeDelegate)dynMethod.CreateDelegate(typeof(InvokeDelegate));
                    }
                }
                catch (Exception ex)
                {
                    throw new CallTargetInvokerException(ex);
                }
                finally
                {
                    if (_invokeDelegate is null)
                    {
                        _invokeDelegate = (instance, returnValue, exception, state) => new CallTargetReturn<TReturn>(returnValue);
                    }
                }
            }

            internal delegate CallTargetReturn<TReturn> InvokeDelegate(TTarget instance, TReturn returnValue, Exception exception, CallTargetState state);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static CallTargetReturn<TReturn> Invoke(TTarget instance, TReturn returnValue, Exception exception, CallTargetState state)
                => _invokeDelegate(instance, returnValue, exception, state);
        }
    }
}
