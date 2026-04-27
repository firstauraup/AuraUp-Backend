using AuraUpBack.Application.Abstractions;
using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace AuraUpBack.Application.Commands.SubmitApplicationForm;

public sealed record SubmitApplicationFormCommand(
    string Email,
    string PhoneNumber,
    string FullName,
    string CompanyName,
    string PrimaryNetwork) : ICommand<ApplicationFormSubmissionDto>;

internal sealed class SubmitApplicationFormCommandHandler(
    IApplicationFormSubmissionRepository submissionRepository,
    IEmailNotificationService emailNotificationService,
    ILogger<SubmitApplicationFormCommandHandler> logger)
    : ICommandHandler<SubmitApplicationFormCommand, ApplicationFormSubmissionDto>
{
    public async Task<ApplicationFormSubmissionDto> HandleAsync(SubmitApplicationFormCommand command, CancellationToken cancellationToken)
    {
        var submission = ApplicationFormSubmission.Create(
            command.Email,
            command.PhoneNumber,
            command.FullName,
            command.CompanyName,
            command.PrimaryNetwork,
            DateTime.UtcNow);

        await submissionRepository.AddAsync(submission, cancellationToken);
        try
        {
            await emailNotificationService.SendApplicationFormSubmittedAsync(submission, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Application form notification email failed for submission {SubmissionId}.", submission.Id);
        }

        return new ApplicationFormSubmissionDto(
            submission.Id,
            submission.Email,
            submission.FullName,
            submission.CompanyName,
            submission.PrimaryNetwork,
            submission.CreatedAtUtc);
    }
}
