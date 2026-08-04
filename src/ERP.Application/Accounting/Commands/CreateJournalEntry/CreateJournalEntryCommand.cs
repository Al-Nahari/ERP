using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ERP.Domain.Accounting;
using ERP.Domain.Accounting.Services;
using ERP.SharedKernel;
using FluentValidation;
using MediatR;

namespace ERP.Application.Accounting.Commands.CreateJournalEntry;

/// <summary>Single line requested by the caller (manual JE entry screen).</summary>
public record JournalLineDto(Guid AccountId, decimal Debit, decimal Credit, Guid? CostCenterId, string? Description);

/// <summary>
/// CQRS Command: request to create AND immediately post a manual journal entry.
/// Manual entries (as opposed to system-generated ones from Sales/Purchasing/etc.)
/// go through this same command from the Journal Entry screen in the UI.
/// </summary>
public record CreateJournalEntryCommand(
    Guid CompanyId,
    Guid BranchId,
    DateTime PostingDate,
    string Description,
    string CurrencyCode,
    List<JournalLineDto> Lines
) : IRequest<Result<Guid>>;

public class CreateJournalEntryCommandValidator : AbstractValidator<CreateJournalEntryCommand>
{
    public CreateJournalEntryCommandValidator()
    {
        RuleFor(x => x.CompanyId).NotEmpty();
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.PostingDate).NotEmpty();
        RuleFor(x => x.Description).NotEmpty().MaximumLength(500);
        RuleFor(x => x.CurrencyCode).NotEmpty().Length(3);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("A journal entry requires at least two lines.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.AccountId).NotEmpty();
            line.RuleFor(l => l).Must(l => (l.Debit > 0) ^ (l.Credit > 0))
                .WithMessage("Each line must have exactly one of Debit or Credit greater than zero.");
        });
        RuleFor(x => x.Lines).Must(lines =>
        {
            var totalDebit = 0m;
            var totalCredit = 0m;
            foreach (var l in lines) { totalDebit += l.Debit; totalCredit += l.Credit; }
            return totalDebit == totalCredit;
        }).WithMessage("Total debits must equal total credits before submission.");
    }
}

/// <summary>
/// Handler — thin orchestration only. All real business logic (balance
/// enforcement, period checks, account validation) lives in the Domain layer's
/// JournalPostingService and JournalEntry aggregate, per Clean Architecture: the
/// Application layer coordinates, the Domain layer decides.
/// </summary>
public class CreateJournalEntryCommandHandler : IRequestHandler<CreateJournalEntryCommand, Result<Guid>>
{
    private readonly JournalPostingService _postingService;
    private readonly IUnitOfWork _unitOfWork;

    public CreateJournalEntryCommandHandler(JournalPostingService postingService, IUnitOfWork unitOfWork)
    {
        _postingService = postingService;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<Guid>> Handle(CreateJournalEntryCommand request, CancellationToken cancellationToken)
    {
        var lines = request.Lines
            .ConvertAll(l => (l.AccountId, l.Debit, l.Credit, l.CostCenterId))
            .ToArray();

        var result = await _postingService.PostAsync(
            request.CompanyId, request.BranchId, request.PostingDate, request.Description,
            JournalSourceModule.Manual, sourceDocumentNumber: null, sourceDocumentId: null,
            lines, request.CurrencyCode, cancellationToken);

        if (result.IsFailure)
            return Result.Failure<Guid>(result.Errors);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(result.Value.Id);
    }
}
