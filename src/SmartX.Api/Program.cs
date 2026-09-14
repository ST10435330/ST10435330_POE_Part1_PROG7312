using SmartX;
using Microsoft.AspNetCore.Http.Features;
var builder=WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Gateway>();
builder.Services.AddHostedService<Simulator>();
builder.Services.Configure<FormOptions>(o=>o.MultipartBodyLengthLimit=6*1024*1024);
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=6*1024*1024);
var app=builder.Build();
app.Use(async(context,next)=>
{
    context.Response.Headers["X-Content-Type-Options"]="nosniff";
    try { await next(context); }
    catch(GatewayException e) { context.Response.StatusCode=e.Status; await context.Response.WriteAsJsonAsync(new {error=e.Message}); }
    catch(BadHttpRequestException) { context.Response.StatusCode=400; await context.Response.WriteAsJsonAsync(new {error="Invalid request: check JSON types, required fields and request size."}); }
});
app.MapGet("/", (IWebHostEnvironment environment) => Results.File(Path.Combine(environment.WebRootPath, "index.html"), "text/html"));
app.UseStaticFiles();
app.MapGet("/api/health",()=>new {status="ok"});
app.MapGet("/api/deployment",(Gateway g)=>g.Tree);
app.MapPost("/api/deployment/validate",(string[] path,Gateway g)=> { var error=DeploymentValidator.Validate(g.Tree,path); return error is null ? Results.Ok(new {valid=true}) : Results.BadRequest(new {error}); });
app.MapGet("/api/summary",(Gateway g)=>g.Summary());
app.MapGet("/api/alerts",(Gateway g)=>g.Alerts());
app.MapGet("/api/sensors",(Gateway g,string? search,string? category,int skip=0,int take=50)=>g.List(search,category,skip,take));
app.MapGet("/api/sensors/{id:guid}",(Guid id,Gateway g)=>g.Get(id));
app.MapPost("/api/sensors",(SensorRegistration registration,Gateway g)=> { var sensor=g.Register(registration); return Results.Created($"/api/sensors/{sensor.Id}",sensor); });
app.MapGet("/api/sensors/{id:guid}/health",(Guid id,Gateway g)=>g.Health(id));
app.MapPost("/api/telemetry/environmental",(TelemetryPacket<float> packet,Gateway g)=> { g.Ingest(packet); return Results.Accepted(); });
app.MapPost("/api/telemetry/power",(TelemetryPacket<int> packet,Gateway g)=> { g.Ingest(packet); return Results.Accepted(); });
app.MapPost("/api/telemetry/actuator",(TelemetryPacket<bool> packet,Gateway g)=> { g.Ingest(packet); return Results.Accepted(); });
app.MapGet("/api/telemetry/environmental/{id:guid}",(Guid id,Gateway g,int count=120)=>g.FloatHistory(id,count));
app.MapGet("/api/telemetry/power/{id:guid}",(Guid id,Gateway g,int count=120)=>g.IntHistory(id,count));
app.MapGet("/api/telemetry/actuator/{id:guid}",(Guid id,Gateway g,int count=120)=>g.BoolHistory(id,count));
app.MapGet("/api/meters/aggregate",(Guid first,Guid second,Gateway g)=>g.Aggregate(first,second));
app.MapPost("/api/simulator/running",(RunningRequest request,Gateway g)=> {g.SetRunning(request.Running);return Results.Ok();});
app.MapPost("/api/simulator/{id:guid}/pause",(Guid id,PauseRequest request,Gateway g)=>{g.Pause(id,request.Paused);return Results.Ok();});
app.MapPost("/api/simulator/{id:guid}/sample",(Guid id,SampleRequest request,Gateway g)=>{g.Simulate(id,request.Spike);return Results.Ok();});
app.MapPost("/api/simulator/load",(LoadRequest request,Gateway g)=>g.Load(request.Rounds));
app.MapGet("/api/sensors/{id:guid}/attachments",(Guid id,Gateway g)=>g.Files(id));
// Local simulation only: no cookie authentication. Add authentication and CSRF protection before remote deployment.
app.MapPost("/api/sensors/{id:guid}/attachments",async(Guid id,HttpRequest request,Gateway g,CancellationToken ct)=>
{
    if(!request.HasFormContentType) return Results.BadRequest(new {error="Use multipart/form-data with one field named file."});
    var form=await request.ReadFormAsync(ct);
    var file=form.Files.GetFile("file");
    if(file is null || form.Files.Count!=1) return Results.BadRequest(new {error="Select exactly one file."});
    return Results.Ok(await g.Upload(id,file,ct));
});
app.MapGet("/api/sensors/{id:guid}/attachments/{fileId:guid}",(Guid id,Guid fileId,Gateway g)=>
{
    var file=g.Download(id,fileId); return Results.File(file.Path,"application/octet-stream",file.Metadata.Name);
});
app.MapFallback(()=>Results.NotFound(new {error="Route not found."}));
app.Run();
public sealed record RunningRequest(bool Running);
public sealed record PauseRequest(bool Paused);
public sealed record SampleRequest(bool Spike);
public sealed record LoadRequest(int Rounds);
