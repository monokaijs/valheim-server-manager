using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ValheimServerManager.Bootstrap;

internal static class RpcReflectionBridge
{
    private const BindingFlags AllMembers = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void RegisterString(object rpc, string name, Action<object, string> handler) =>
        Register(rpc, name, handler, 1);

    public static void RegisterStrings(object rpc, string name, Action<object, string, string> handler) =>
        Register(rpc, name, handler, 2);

    private static void Register(object rpc, string name, Delegate handler, int argumentCount)
    {
        var openRegister = rpc.GetType().GetMethods(AllMembers)
            .Where(method => method.Name == "Register" && method.IsGenericMethodDefinition)
            .Where(method => method.GetGenericArguments().Length == argumentCount)
            .FirstOrDefault(method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length == 2 && parameters[0].ParameterType == typeof(string);
            }) ?? throw new MissingMethodException(rpc.GetType().FullName, "Register<T>(string, callback)");
        var register = openRegister.MakeGenericMethod(Enumerable.Repeat(typeof(string), argumentCount).ToArray());
        var callbackType = register.GetParameters()[1].ParameterType;
        var invoke = callbackType.GetMethod("Invoke") ?? throw new InvalidOperationException("RPC callback type is not a delegate");
        var callbackParameters = invoke.GetParameters();
        if (callbackParameters.Length != argumentCount + 1 || callbackParameters.Skip(1).Any(parameter => parameter.ParameterType != typeof(string)))
            throw new InvalidOperationException("Valheim string RPC callback signature is unsupported");

        var parameters = callbackParameters.Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();
        var arguments = parameters.Select((parameter, index) => index == 0 ? (Expression)Expression.Convert(parameter, typeof(object)) : parameter).ToArray();
        var body = Expression.Invoke(Expression.Constant(handler), arguments);
        var callback = Expression.Lambda(callbackType, body, parameters).Compile();
        register.Invoke(rpc, new object[] { name, callback });
    }

    public static void InvokeString(object rpc, string name, string payload)
    {
        var invoke = rpc.GetType().GetMethods(AllMembers)
            .FirstOrDefault(method =>
            {
                if (method.Name != "Invoke") return false;
                var parameters = method.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[1].ParameterType == typeof(object[]);
            }) ?? throw new MissingMethodException(rpc.GetType().FullName, "Invoke(string, object[])");
        invoke.Invoke(rpc, new object[] { name, new object[] { payload } });
    }
}
