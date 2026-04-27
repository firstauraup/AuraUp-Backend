using AuraUpBack.Domain.Entities;

namespace AuraUpBack.Domain.Repositories;

public interface IApplicationFormSubmissionRepository
{
    Task AddAsync(ApplicationFormSubmission submission, CancellationToken cancellationToken);
}
