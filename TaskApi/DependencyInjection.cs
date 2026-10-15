using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Formatting.Compact;
using TaskApi.Application.Tasks;
using TaskApi.Contracts;
using TaskApi.Infrastructure.Messaging;
using TaskApi.Infrastructure.Persistence;

namespace TaskApi;

public static class DependencyInjection
{
    public static IServiceCollection AddTaskApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(
            new JsonStringEnumConverter(allowIntegerValues: false)));
        // Swashbuckle reads MVC JSON options even when the API uses only Minimal endpoints.
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options => options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(allowIntegerValues: false)));
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddDbContext<TaskDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Tasks")));
        services.AddScoped<TaskService>();
        services.AddScoped<IValidator<CreateTaskRequest>, CreateTaskValidator>();
        services.AddScoped<IValidator<GetTasksRequest>, GetTasksValidator>();

        services.AddOptions<RabbitMqOptions>()
            .BindConfiguration("RabbitMq")
            .Validate(options => Uri.TryCreate(options.Uri, UriKind.Absolute, out var uri)
                && uri.Scheme is "amqp" or "amqps", "RabbitMq:Uri must be an AMQP URI")
            .ValidateOnStart();
        services.AddSingleton<RabbitMqPublisher>();
        services.AddSingleton<ITaskEventPublisher>(provider => provider.GetRequiredService<RabbitMqPublisher>());
        services.AddHostedService(provider => provider.GetRequiredService<RabbitMqPublisher>());
        services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));

        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Task API",
                Version = "v1",
                Description = "Task management API. Pagination is optional: omit page and pageSize to retrieve all tasks."
            });
            options.SupportNonNullableReferenceTypes();
        });

        services.AddSerilog((provider, logger) => logger
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(provider)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "TaskApi")
            .WriteTo.Console(new CompactJsonFormatter()));

        return services;
    }
}
