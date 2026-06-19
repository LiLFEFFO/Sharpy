using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Sharpy.Models;

namespace Sharpy.Services;

public class ExecutorService
{
    private readonly CompilerService _compiler;
    private readonly SessionManager _sessionManager;

    public ExecutorService(CompilerService compiler, SessionManager sessionManager)
    {
        _compiler = compiler;
        _sessionManager = sessionManager;
    }

    public ExecuteResponse Execute(ExecuteRequest request)
    {
        var sw = Stopwatch.StartNew();
        var result = new ExecuteResponse();

        try
        {
            var compilation = _compiler.GetAssembly(request.AssemblyToken);
            if (compilation == null)
            {
                result.Success = false;
                result.Error = "Assembly not found or expired. Please recompile.";
                return result;
            }

            var sessionId = _sessionManager.GetOrCreateSessionId(request.SessionId);
            result.SessionId = sessionId;

            Assembly? assembly;
            object? instance;

            var cached = _sessionManager.TryGetEntry(sessionId, request.AssemblyToken, request.ClassName, out assembly, out instance);

            if (!cached || request.ResetInstance)
            {
                try
                {
                    assembly = Assembly.Load(compilation.AssemblyBytes, compilation.PdbBytes);
                }
                catch
                {
                    assembly = Assembly.Load(compilation.AssemblyBytes);
                }

                var type = assembly.GetType(request.ClassName);
                if (type == null)
                {
                    type = assembly.GetExportedTypes()
                        .FirstOrDefault(t => t.FullName == request.ClassName || t.Name == request.ClassName);
                }

                if (type == null)
                {
                    result.Success = false;
                    result.Error = $"Class '{request.ClassName}' not found.";
                    return result;
                }

                instance = null;
                if (!type.IsAbstract || type.IsSealed)
                {
                    var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                    if (ctors.Length > 0)
                    {
                        var ctorParams = request.ConstructorArgs
                            .Select((arg, i) =>
                            {
                                var ctor = ctors.FirstOrDefault();
                                if (ctor == null) return arg;
                                var pars = ctor.GetParameters();
                                if (i < pars.Length) return ConvertArg(arg, pars[i].ParameterType);
                                return arg;
                            }).ToArray();

                        instance = Activator.CreateInstance(type, ctorParams);
                    }
                    else
                    {
                        instance = Activator.CreateInstance(type);
                    }
                }

                _sessionManager.SetEntry(sessionId, request.AssemblyToken, request.ClassName, assembly, instance);
                result.InstanceCreated = true;
            }
            else
            {
                result.InstanceCreated = false;
            }

            var typeForMethods = assembly!.GetType(request.ClassName);
            if (typeForMethods == null)
            {
                typeForMethods = assembly.GetExportedTypes()
                    .FirstOrDefault(t => t.FullName == request.ClassName || t.Name == request.ClassName);
            }

            if (typeForMethods == null)
            {
                result.Success = false;
                result.Error = $"Class '{request.ClassName}' not found.";
                return result;
            }

            var methods = typeForMethods.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name == request.MethodName && !m.IsSpecialName)
                .ToArray();

            if (methods.Length == 0)
            {
                result.Success = false;
                result.Error = $"Method '{request.MethodName}' not found.";
                return result;
            }

            object? methodResult = null;
            var exceptions = new List<Exception>();

            foreach (var method in methods.OrderByDescending(m => m.GetParameters().Length))
            {
                try
                {
                    var pars = method.GetParameters();
                    var args = new object?[pars.Length];

                    for (int i = 0; i < pars.Length; i++)
                    {
                        if (i < request.MethodArgs.Count)
                        {
                            args[i] = ConvertArg(request.MethodArgs[i], pars[i].ParameterType);
                        }
                        else if (pars[i].IsOptional)
                        {
                            args[i] = pars[i].DefaultValue;
                        }
                        else
                        {
                            args[i] = GetDefaultValue(pars[i].ParameterType);
                        }
                    }

                    var consoleOut = new StringWriter();
                    var originalOut = Console.Out;
                    Console.SetOut(consoleOut);
                    try
                    {
                        methodResult = method.Invoke(instance, args);
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                    var capturedOutput = consoleOut.ToString();
                    if (!string.IsNullOrEmpty(capturedOutput))
                        result.Output = (result.Output != null ? result.Output + "\n" : "") + capturedOutput.TrimEnd();

                    if (methodResult is Task task)
                    {
                        task.GetAwaiter().GetResult();
                        var taskType = task.GetType();
                        if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
                        {
                            var prop = taskType.GetProperty("Result");
                            methodResult = prop?.GetValue(task);
                        }
                        else
                        {
                            methodResult = null;
                        }
                    }
                    else if (methodResult is ValueTask valueTask)
                    {
                        var resultTask = valueTask.AsTask();
                        resultTask.GetAwaiter().GetResult();
                        var taskType = resultTask.GetType();
                        if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
                        {
                            var prop = taskType.GetProperty("Result");
                            methodResult = prop?.GetValue(resultTask);
                        }
                        else
                        {
                            methodResult = null;
                        }
                    }

                    result.Success = true;
                    result.Result = SerializeResult(methodResult);
                    result.ResultType = methodResult?.GetType().Name ?? "void";
                    break;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }

            if (!result.Success && exceptions.Count > 0)
            {
                var lastEx = exceptions.Last().InnerException ?? exceptions.Last();
                result.Success = false;
                result.Error = lastEx.Message;
                result.StackTrace = lastEx.StackTrace;
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.StackTrace = ex.StackTrace;
        }

        sw.Stop();
        result.ExecutionTimeMs = sw.ElapsedMilliseconds;
        return result;
    }

    private static object? ConvertArg(object? arg, Type targetType)
    {
        if (arg == null) return null;
        if (targetType == typeof(object)) return arg;

        var argType = arg.GetType();
        if (targetType.IsAssignableFrom(argType)) return arg;

        if (arg is JsonElement je)
        {
            return ConvertJsonElement(je, targetType);
        }

        var str = arg.ToString();
        if (str == null) return null;

        try
        {
            if (targetType == typeof(string)) return str;
            if (targetType == typeof(int)) return int.Parse(str);
            if (targetType == typeof(long)) return long.Parse(str);
            if (targetType == typeof(float)) return float.Parse(str);
            if (targetType == typeof(double)) return double.Parse(str);
            if (targetType == typeof(decimal)) return decimal.Parse(str);
            if (targetType == typeof(bool)) return bool.Parse(str);
            if (targetType == typeof(short)) return short.Parse(str);
            if (targetType == typeof(byte)) return byte.Parse(str);
            if (targetType == typeof(uint)) return uint.Parse(str);
            if (targetType == typeof(ulong)) return ulong.Parse(str);
            if (targetType == typeof(char)) return str[0];
            if (targetType == typeof(Guid)) return Guid.Parse(str);
            if (targetType == typeof(DateTime)) return DateTime.Parse(str);
            if (targetType.IsEnum) return Enum.Parse(targetType, str);
        }
        catch
        {
            try { return JsonSerializer.Deserialize(str, targetType, JsonOpts.Options); }
            catch { }
        }

        try { return JsonSerializer.Deserialize(str, targetType, JsonOpts.Options); }
        catch { return arg; }
    }

    private static object? ConvertJsonElement(JsonElement je, Type targetType)
    {
        if (targetType == typeof(string)) return je.GetString();
        if (targetType == typeof(int)) return je.GetInt32();
        if (targetType == typeof(long)) return je.GetInt64();
        if (targetType == typeof(float)) return je.GetSingle();
        if (targetType == typeof(double)) return je.GetDouble();
        if (targetType == typeof(decimal)) return je.GetDecimal();
        if (targetType == typeof(bool)) return je.GetBoolean();
        if (targetType == typeof(short)) return je.GetInt16();
        if (targetType == typeof(byte)) return je.GetByte();
        if (targetType == typeof(uint)) return je.GetUInt32();
        if (targetType == typeof(ulong)) return je.GetUInt64();
        if (targetType == typeof(Guid)) return je.GetGuid();
        if (targetType == typeof(DateTime)) return je.GetDateTime();
        if (targetType.IsEnum) return Enum.Parse(targetType, je.GetString()!);

        if (je.ValueKind == JsonValueKind.Array)
        {
            return ConvertJsonArray(je, targetType);
        }

        if (je.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize(je.GetRawText(), targetType, JsonOpts.Options);
        }

        return null;
    }

    private static object? ConvertJsonArray(JsonElement je, Type targetType)
    {
        if (targetType.IsArray)
        {
            var elemType = targetType.GetElementType()!;
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elemType))!;
            foreach (var item in je.EnumerateArray())
            {
                list.Add(ConvertJsonElement(item, elemType));
            }
            var array = Array.CreateInstance(elemType, list.Count);
            list.CopyTo(array, 0);
            return array;
        }

        if (targetType.IsGenericType)
        {
            var def = targetType.GetGenericTypeDefinition();
            var args = targetType.GetGenericArguments();

            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>) || def == typeof(ICollection<>))
            {
                var listType = typeof(List<>).MakeGenericType(args[0]);
                var list = (IList)Activator.CreateInstance(listType)!;
                foreach (var item in je.EnumerateArray())
                {
                    list.Add(ConvertJsonElement(item, args[0]));
                }
                return list;
            }

            if (def == typeof(HashSet<>))
            {
                var hashType = typeof(HashSet<>).MakeGenericType(args[0]);
                var hash = Activator.CreateInstance(hashType);
                var addMethod = hashType.GetMethod("Add");
                foreach (var item in je.EnumerateArray())
                {
                    addMethod?.Invoke(hash, [ConvertJsonElement(item, args[0])]);
                }
                return hash;
            }

            if (def == typeof(Dictionary<,>) && args[0] == typeof(string))
            {
                return JsonSerializer.Deserialize(je.GetRawText(), targetType, JsonOpts.Options);
            }
        }

        return JsonSerializer.Deserialize(je.GetRawText(), targetType, JsonOpts.Options);
    }

    private static object? GetDefaultValue(Type type)
    {
        if (type.IsValueType) return Activator.CreateInstance(type);
        return null;
    }

    private static string? SerializeResult(object? obj)
    {
        if (obj == null) return null;
        try
        {
            return JsonSerializer.Serialize(obj, JsonOpts.Options);
        }
        catch
        {
            return obj.ToString();
        }
    }
}
