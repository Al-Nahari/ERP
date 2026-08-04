using System.Threading.Tasks;
using ERP.Application.Accounting.Commands.CreateJournalEntry;
using ERP.Application.Accounting.Queries.GetTrialBalance;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.WebAPI.Controllers;

/// <summary>
/// Financial Accounting endpoints. Requires the "Accounting.Post" claim/permission
/// (see Security module) — enforced via policy-based authorization, not just
/// [Authorize], since posting journal entries is one of the highest-privilege
/// operations in the entire system.
/// </summary>
[ApiController]
[Route("api/v1/accounting/journal-entries")]
[Authorize]
public class JournalEntriesController : ControllerBase
{
    private readonly IMediator _mediator;

    public JournalEntriesController(IMediator mediator) => _mediator = mediator;

    /// <summary>Creates and posts a manual journal entry. Returns 422 (not 400) when
    /// the failure is a business-rule violation (e.g. unbalanced entry, closed
    /// period) so the frontend can distinguish validation errors from domain
    /// errors and render them appropriately.</summary>
    [HttpPost]
    [Authorize(Policy = "Accounting.Post")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreateJournalEntryCommand command)
    {
        var result = await _mediator.Send(command);
        if (result.IsFailure)
            return UnprocessableEntity(new { errors = result.Errors });

        return CreatedAtAction(nameof(Create), new { id = result.Value }, new { id = result.Value });
    }

    [HttpGet("trial-balance")]
    [Authorize(Policy = "Accounting.ViewReports")]
    public async Task<IActionResult> GetTrialBalance([FromQuery] GetTrialBalanceQuery query)
    {
        var result = await _mediator.Send(query);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Errors);
    }
}
