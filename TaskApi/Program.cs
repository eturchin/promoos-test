using Serilog;
using TaskApi;
using TaskApi.Api;
using TaskApi.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTaskApi(builder.Configuration);

var app = builder.Build();
app.UseMiddleware<TraceMiddleware>();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSwagger();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Task API v1"));

await app.InitializeDatabaseAsync();

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapTaskEndpoints();
app.Run();

public partial class Program;
