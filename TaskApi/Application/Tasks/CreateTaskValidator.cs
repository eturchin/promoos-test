using FluentValidation;
using TaskApi.Contracts;
using TaskApi.Domain;

namespace TaskApi.Application.Tasks;

public sealed class CreateTaskValidator : AbstractValidator<CreateTaskRequest>
{
    public CreateTaskValidator()
    {
        RuleFor(request => request.Title)
            .NotEmpty()
            .MaximumLength(TaskItem.MaxTitleLength);

        RuleFor(request => request.Priority).IsInEnum();
    }
}
