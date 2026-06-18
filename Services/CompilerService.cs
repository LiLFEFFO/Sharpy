using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sharpy.Models;

namespace Sharpy.Services;

public class CompilerService
{
    private readonly ConcurrentDictionary<string, CompilationResult> _cache = new();
    private static readonly Assembly[] _coreAssemblies =
    [
        typeof(object).Assembly,
        typeof(Console).Assembly,
        typeof(Enumerable).Assembly,
        typeof(System.Text.Json.JsonSerializer).Assembly,
        typeof(System.Linq.Enumerable).Assembly,
        typeof(System.Collections.Generic.List<>).Assembly,
        typeof(System.Collections.IList).Assembly,
        typeof(System.Threading.Tasks.Task).Assembly,
    ];

    private static readonly string[] _implicitUsings =
    [
        "using System;",
        "using System.Collections.Generic;",
        "using System.Linq;",
        "using System.Text;",
        "using System.Threading;",
        "using System.Threading.Tasks;",
        "using System.IO;",
        "using System.Net;",
        "using System.Reflection;",
    ];

    public Task<CompileResponse> CompileAsync(List<CodeFile> files)
    {
        var assemblyToken = Guid.NewGuid().ToString("N");
        var syntaxTrees = new List<SyntaxTree>();
        var rawTrees = new List<(SyntaxTree Tree, string Content)>();
        var anyTopLevel = false;

        foreach (var file in files)
        {
            var tree = CSharpSyntaxTree.ParseText(file.Content,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: file.FileName,
                encoding: Encoding.UTF8);
            var root = tree.GetRoot();
            var hasTopLevel = root.ChildNodes().Any(n =>
                n is Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax);
            if (hasTopLevel) anyTopLevel = true;
            rawTrees.Add((tree, file.Content));
        }

        foreach (var (tree, content) in rawTrees)
        {
            var processed = PreprocessSource(content);
            var newTree = CSharpSyntaxTree.ParseText(processed,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: tree.FilePath,
                encoding: Encoding.UTF8);
            syntaxTrees.Add(newTree);
        }

        var references = BuildReferences();

        var outputKind = anyTopLevel
            ? OutputKind.ConsoleApplication
            : OutputKind.DynamicallyLinkedLibrary;

        var options = new CSharpCompilationOptions(
            outputKind,
            optimizationLevel: OptimizationLevel.Release,
            metadataImportOptions: MetadataImportOptions.All);

        var compilation = CSharpCompilation.Create(
            $"Sharpy_{assemblyToken}",
            syntaxTrees,
            references,
            options);

        using var ms = new MemoryStream();
        using var pdbMs = new MemoryStream();
        var emitResult = compilation.Emit(ms, pdbMs);

        var response = new CompileResponse
        {
            Success = emitResult.Success,
            AssemblyToken = assemblyToken,
            Errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"({d.Location.GetLineSpan().StartLinePosition.Line + 1},{d.Location.GetLineSpan().StartLinePosition.Character + 1}) {d.GetMessage()} [{d.Id}]")
                .ToList()
        };

        if (emitResult.Success)
        {
            ms.Position = 0;
            pdbMs.Position = 0;
            var assemblyBytes = ms.ToArray();
            var pdbBytes = pdbMs.ToArray();

            var result = new CompilationResult
            {
                AssemblyBytes = assemblyBytes,
                PdbBytes = pdbBytes
            };

            response.Classes = AnalyzeAssembly(assemblyBytes, pdbBytes);
            _cache[assemblyToken] = result;

            var ttl = TimeSpan.FromMinutes(30);
            _ = Task.Run(async () =>
            {
                await Task.Delay(ttl);
                _cache.TryRemove(assemblyToken, out _);
            });
        }

        return Task.FromResult(response);
    }

    private static string PreprocessSource(string source)
    {
        var normalized = source.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized.Split('\n');
        var usingLines = new List<string>();
        var otherLines = new List<string>();
        var seenUsings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("using ") && trimmed.EndsWith(";"))
            {
                var key = trimmed.TrimEnd(';').Trim();
                if (key.Contains("Ecma335") || key.Contains("System.Reflection.Metadata.Ecma335"))
                    continue;
                if (seenUsings.Add(key))
                    usingLines.Add(line);
            }
            else
            {
                otherLines.Add(line);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("#pragma warning disable CS0161, CS0162, CS0168, CS0219, CS0414, CS0649, CS1998, CS4014");
        bool hasSystem = seenUsings.Any(u => u.Equals("using System", StringComparison.Ordinal) ||
                                             u.Equals("using System;", StringComparison.Ordinal));
        foreach (var u in _implicitUsings)
        {
            var key = u.TrimEnd(';');
            if (u == "using System;" && hasSystem) continue;
            if (!seenUsings.Contains(key) && !seenUsings.Contains(u))
                sb.AppendLine(u);
        }

        foreach (var u in usingLines)
            sb.AppendLine(u.TrimEnd());

        int braceDepth = 0;
        for (int i = 0; i < otherLines.Count; i++)
        {
            var trimmed = otherLines[i].TrimStart();
            if (braceDepth == 0 && trimmed.StartsWith("int ") && trimmed.Contains("Main("))
            {
                otherLines[i] = otherLines[i].Replace("int ", "void ", StringComparison.Ordinal);
            }
            braceDepth += trimmed.Count(c => c == '{') - trimmed.Count(c => c == '}');
        }

        foreach (var line in otherLines)
            sb.AppendLine(line);

        return sb.ToString();
    }

    private static List<MetadataReference> BuildReferences()
    {
        var references = new List<MetadataReference>();

        foreach (var asm in _coreAssemblies)
        {
            try
            {
                if (asm.Location != null)
                    references.Add(MetadataReference.CreateFromFile(asm.Location));
            }
            catch { }
        }

        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
        {
            var extraAssemblies = new[]
            {
                "System.Runtime.dll", "System.Private.CoreLib.dll", "System.Console.dll",
                "System.Collections.dll", "System.Linq.dll", "System.Text.Json.dll",
                "System.Threading.Tasks.dll", "System.Net.Primitives.dll",
                "System.IO.dll", "System.Text.RegularExpressions.dll",
                "System.ComponentModel.dll", "System.ComponentModel.TypeConverter.dll",
                "System.ObjectModel.dll", "System.Reflection.dll",
                "System.Net.dll", "System.Threading.dll",
            };
            foreach (var name in extraAssemblies)
            {
                var path = Path.Combine(runtimeDir, name);
                if (File.Exists(path) && !references.Any(r =>
                    {
                        try { return r.Display?.Contains(name, StringComparison.OrdinalIgnoreCase) == true; }
                        catch { return false; }
                    }))
                {
                    try { references.Add(MetadataReference.CreateFromFile(path)); }
                    catch { }
                }
            }
        }

        return references;
    }

    public CompilationResult? GetAssembly(string token)
    {
        _cache.TryGetValue(token, out var result);
        return result;
    }

    private static List<ClassInfo> AnalyzeAssembly(byte[] assemblyBytes, byte[] pdbBytes)
    {
        var classes = new List<ClassInfo>();
        System.Reflection.Assembly? assembly = null;

        try
        {
            assembly = System.Reflection.Assembly.Load(assemblyBytes, pdbBytes);
        }
        catch
        {
            try { assembly = System.Reflection.Assembly.Load(assemblyBytes); }
            catch { return classes; }
        }

        if (assembly == null) return classes;

        foreach (var type in assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsGenericType && !t.IsNestedPrivate && !t.Name.StartsWith("<")))
        {
            if (type.Name.Contains('<') || type.Name.Contains('>') || type.Name == "__Program") continue;
            if (type.GetCustomAttributes(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false).Length > 0) continue;

            var ci = new ClassInfo
            {
                Name = type.Name,
                FullName = type.FullName ?? type.Name,
                Namespace = type.Namespace ?? "",
                IsStatic = type.IsAbstract && type.IsSealed,
                IsAbstract = type.IsAbstract && !type.IsSealed,
            };

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                ci.Properties.Add(new PropDef
                {
                    Name = prop.Name,
                    Type = FormatType(prop.PropertyType),
                    CanRead = prop.CanRead,
                    CanWrite = prop.CanWrite
                });
            }

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                var pars = ctor.GetParameters()
                    .Select(p => new ParamInfo
                    {
                        Name = p.Name ?? "param",
                        Type = FormatType(p.ParameterType),
                        TypeFullName = p.ParameterType.FullName ?? p.ParameterType.Name,
                        IsEnum = p.ParameterType.IsEnum,
                        IsCollection = IsCollectionType(p.ParameterType),
                        CollectionElementType = GetCollectionElementType(p.ParameterType),
                        IsGeneric = p.ParameterType.IsGenericType,
                        IsOptional = p.IsOptional,
                        DefaultValue = p.DefaultValue?.ToString()
                    }).ToList();

                ci.Constructors.Add(new CtorInfo { Parameters = pars });
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName) continue;
                if (method.Name.StartsWith("get_") || method.Name.StartsWith("set_")) continue;
                if (method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")) continue;

                var retType = method.ReturnType;
                bool isAsync = false;
                if (retType.IsGenericType && (retType.GetGenericTypeDefinition() == typeof(Task<>) || retType.GetGenericTypeDefinition() == typeof(ValueTask<>)))
                {
                    isAsync = true;
                    retType = retType.GetGenericArguments()[0];
                }
                else if (retType == typeof(Task) || retType == typeof(ValueTask))
                {
                    isAsync = true;
                }

                var mi = new MethodDef
                {
                    Name = method.Name,
                    ReturnType = FormatType(retType),
                    IsStatic = method.IsStatic,
                    IsVoid = retType == typeof(void),
                    IsAsync = isAsync,
                    Parameters = method.GetParameters()
                        .Select(p => new ParamInfo
                        {
                            Name = p.Name ?? "param",
                            Type = FormatType(p.ParameterType),
                            TypeFullName = p.ParameterType.FullName ?? p.ParameterType.Name,
                            IsEnum = p.ParameterType.IsEnum,
                            EnumValues = p.ParameterType.IsEnum ? Enum.GetNames(p.ParameterType).ToList() : null,
                            IsCollection = IsCollectionType(p.ParameterType),
                            CollectionElementType = GetCollectionElementType(p.ParameterType),
                            IsGeneric = p.ParameterType.IsGenericType,
                            IsOptional = p.IsOptional,
                            DefaultValue = p.DefaultValue?.ToString()
                        }).ToList()
                };

                ci.Methods.Add(mi);
            }

            classes.Add(ci);
        }

        return classes;
    }

    private static string FormatType(Type type)
    {
        if (type == typeof(void)) return "void";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(float)) return "float";
        if (type == typeof(double)) return "double";
        if (type == typeof(decimal)) return "decimal";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(string)) return "string";
        if (type == typeof(char)) return "char";
        if (type == typeof(byte)) return "byte";
        if (type == typeof(short)) return "short";
        if (type == typeof(uint)) return "uint";
        if (type == typeof(ulong)) return "ulong";
        if (type == typeof(DateTime)) return "DateTime";
        if (type == typeof(Guid)) return "Guid";
        if (type == typeof(object)) return "object";
        if (type.IsGenericType)
        {
            var name = type.Name.Split('`')[0];
            var args = string.Join(", ", type.GetGenericArguments().Select(FormatType));
            return $"{name}<{args}>";
        }
        if (type.IsArray)
        {
            return $"{FormatType(type.GetElementType()!)}[]";
        }
        return type.Name;
    }

    private static bool IsCollectionType(Type type)
    {
        if (type.IsArray) return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)) return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ICollection<>)) return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IList<>)) return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>)) return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) return true;
        return false;
    }

    private static string? GetCollectionElementType(Type type)
    {
        if (type.IsArray) return FormatType(type.GetElementType()!);
        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            if (typeof(System.Collections.IDictionary).IsAssignableFrom(type) || type.Name.StartsWith("Dictionary"))
                return $"{FormatType(args[0])},{FormatType(args[1])}";
            return FormatType(args[0]);
        }
        return null;
    }
}

public class CompilationResult
{
    public byte[] AssemblyBytes { get; set; } = [];
    public byte[]? PdbBytes { get; set; }
}
