using Sharpy.Models;
using Sharpy.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<CompilerService>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<ExecutorService>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/compile", async (CompileRequest req, CompilerService compiler) =>
{
    var result = await compiler.CompileAsync(req.Files);
    return Results.Ok(result);
});

app.MapPost("/api/execute", (ExecuteRequest req, ExecutorService executor) =>
{
    var result = executor.Execute(req);
    return Results.Ok(result);
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));

app.MapPost("/api/session/reset", (SessionResetRequest req, SessionManager sessionManager) =>
{
    if (!string.IsNullOrEmpty(req.SessionId) && !string.IsNullOrEmpty(req.AssemblyToken) && !string.IsNullOrEmpty(req.ClassName))
    {
        sessionManager.ResetInstance(req.SessionId, req.AssemblyToken, req.ClassName);
    }
    return Results.Ok(new { success = true });
});

app.Run();
