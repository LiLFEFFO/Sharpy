using System.Text.Json.Serialization;
using System.Text.Json;

namespace Sharpy.Models;

public class CodeFile
{
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public class CompileRequest
{
    public List<CodeFile> Files { get; set; } = new();
}

public class ParamInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string TypeFullName { get; set; } = string.Empty;
    public bool IsEnum { get; set; }
    public List<string>? EnumValues { get; set; }
    public bool IsCollection { get; set; }
    public string? CollectionElementType { get; set; }
    public bool IsGeneric { get; set; }
    public bool IsOptional { get; set; }
    public string? DefaultValue { get; set; }
}

public class CtorInfo
{
    public List<ParamInfo> Parameters { get; set; } = new();
}

public class MethodDef
{
    public string Name { get; set; } = string.Empty;
    public string ReturnType { get; set; } = string.Empty;
    public bool IsStatic { get; set; }
    public bool IsVoid { get; set; }
    public bool IsAsync { get; set; }
    public List<ParamInfo> Parameters { get; set; } = new();
}

public class PropDef
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
}

public class ClassInfo
{
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public bool IsStatic { get; set; }
    public bool IsAbstract { get; set; }
    public List<PropDef> Properties { get; set; } = new();
    public List<CtorInfo> Constructors { get; set; } = new();
    public List<MethodDef> Methods { get; set; } = new();
}

public class CompileResponse
{
    public bool Success { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<ClassInfo> Classes { get; set; } = new();
    public string AssemblyToken { get; set; } = string.Empty;
}

public class ExecuteRequest
{
    public string AssemblyToken { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public List<object?> ConstructorArgs { get; set; } = new();
    public List<object?> MethodArgs { get; set; } = new();
}

public class ExecuteResponse
{
    public bool Success { get; set; }
    public string? Result { get; set; }
    public string? ResultType { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public string? StackTrace { get; set; }
    public long ExecutionTimeMs { get; set; }
}

public static class JsonOpts
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = 32
    };
}
