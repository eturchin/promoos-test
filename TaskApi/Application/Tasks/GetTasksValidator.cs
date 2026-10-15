using FluentValidation;
using TaskApi.Contracts;

namespace TaskApi.Application.Tasks;

public sealed class GetTasksValidator : AbstractValidator<GetTasksRequest>
{
    public GetTasksValidator()
    {
        RuleFor(request => request.Page).InclusiveBetween(1, 1_000_000);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 100);
        RuleFor(request => request.Search).MaximumLength(200);
        RuleFor(request => request.Priority).IsInEnum();
        RuleFor(request => request.SortBy).IsInEnum();
        RuleFor(request => request.SortDirection).IsInEnum();
    }
}
