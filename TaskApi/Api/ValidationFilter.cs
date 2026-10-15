using System.Text.Json;
using FluentValidation;

namespace TaskApi.Api;

public sealed class ValidationFilter<T>(IValidator<T> validator, ILogger<ValidationFilter<T>> logger) : IEndpointFilter
    where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<T>().Single();
        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);

        if (result.IsValid)
        {
            return await next(context);
        }

        var errors = result.Errors
            .GroupBy(error => JsonNamingPolicy.CamelCase.ConvertName(error.PropertyName))
            .ToDictionary(group => group.Key, group => group.Select(error => error.ErrorMessage).Distinct().ToArray());

        // Keep request data out of validation logs.
        logger.LogInformation("Request validation failed for {Fields}", errors.Keys);

        return Results.ValidationProblem(errors, extensions: new Dictionary<string, object?>
        {
            ["traceId"] = context.HttpContext.TraceIdentifier
        });
    }
}
