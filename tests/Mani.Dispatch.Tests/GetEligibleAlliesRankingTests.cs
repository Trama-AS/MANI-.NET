using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Entities;
using ManiDispatch.Domain.Exceptions;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>El listado de elegibles presenta el orden de Rules o falla; no hay orden local (SCRUM-1401, SCRUM-1462).</summary>
public class GetEligibleAlliesRankingTests
{
    private static readonly string[] Razones = { "razón" };

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _requestId = Guid.NewGuid();
    private readonly Mock<IAllyRepository> _repo = new();
    private readonly Mock<ITenantRulesReader> _tenantRules = new();
    private readonly Mock<IRulesClient> _rules = new();
    private readonly GetEligibleAlliesUseCase _useCase;

    public GetEligibleAlliesRankingTests()
    {
        _repo.Setup(r => r.GetSolicitudContextAsync(_requestId, _tenantId)).ReturnsAsync(new SolicitudContext
        {
            Id = _requestId,
            TenantId = _tenantId,
            ZonaId = Guid.NewGuid(),
            CategoriaId = Guid.NewGuid(),
            Estado = "PENDIENTE"
        });
        _tenantRules.Setup(t => t.GetCriterioOrdenListadoAsync(_tenantId, "corr")).ReturnsAsync("CALIFICACION");
        _useCase = new GetEligibleAlliesUseCase(_repo.Object, null, _tenantRules.Object, _rules.Object);
    }

    private List<Ally> SetupAllies(int count)
    {
        var allies = Enumerable.Range(0, count).Select(i => new Ally { Id = Guid.NewGuid(), NombreRazonSocial = $"Aliado {i}" }).ToList();
        _repo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
             .ReturnsAsync((MatchCriteria c) => (allies.Skip((c.Page - 1) * c.PageSize).Take(c.PageSize), allies.Count));
        return allies;
    }

    private Task<Application.DTOs.MatchResponseDto> Execute(int? page = null, int? pageSize = null) =>
        _useCase.ExecuteForRequestAsync(_requestId, _tenantId, Guid.NewGuid(), "cliente", null, page, pageSize, "corr");

    [Fact]
    public async Task OrdersByRulesBeforePaginating_WithGlobalPosition()
    {
        var allies = SetupAllies(60);
        var invertido = Enumerable.Reverse(allies).Select((a, i) => new RankedAlly(a.Id, i + 1, 0.5m, Razones)).ToList();
        _rules.Setup(r => r.RankAlliesAsync("CALIFICACION", It.IsAny<IReadOnlyCollection<Ally>>(), _tenantId, "cliente", "corr"))
              .ReturnsAsync(invertido);

        var result = await Execute(page: 2, pageSize: 10);

        Assert.Equal(60, result.TotalCount);
        Assert.Equal("APLICADO", result.Ranking!.Estado);
        Assert.Equal("CALIFICACION", result.Ranking.Criterio);
        var pagina = result.MatchedAllies.ToList();
        Assert.Equal(10, pagina.Count);
        Assert.Equal(11, pagina[0].Posicion);
        Assert.Equal(allies[49].Id.ToString(), pagina[0].AllyId);
        Assert.Equal(Razones, pagina[0].Razones);
        _rules.Verify(r => r.RankAlliesAsync("CALIFICACION", It.Is<IReadOnlyCollection<Ally>>(c => c.Count == 60),
            _tenantId, "cliente", "corr"), Times.Once);
    }

    [Fact]
    public async Task UsesTheCriterionOfTheTenant()
    {
        var allies = SetupAllies(1);
        _tenantRules.Setup(t => t.GetCriterioOrdenListadoAsync(_tenantId, "corr")).ReturnsAsync("COBERTURA");
        _rules.Setup(r => r.RankAlliesAsync("COBERTURA", It.IsAny<IReadOnlyCollection<Ally>>(), _tenantId, "cliente", "corr"))
              .ReturnsAsync(new[] { new RankedAlly(allies[0].Id, 1, 1m, Array.Empty<string>()) });

        var result = await Execute();

        Assert.Equal("COBERTURA", result.Ranking!.Criterio);
    }

    [Fact]
    public async Task WithoutEligibles_DoesNotCallRules_AndOmitsRanking()
    {
        SetupAllies(0);

        var result = await Execute();

        Assert.Null(result.Ranking);
        Assert.Empty(result.MatchedAllies);
        _rules.VerifyNoOtherCalls();
        _tenantRules.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenRulesFails_PropagatesWithoutLocalOrder()
    {
        SetupAllies(2);
        _rules.Setup(r => r.RankAlliesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Ally>>(), _tenantId, "cliente", "corr"))
              .ThrowsAsync(new RulesServiceUnavailableException("caído"));

        await Assert.ThrowsAsync<RulesServiceUnavailableException>(() => Execute());
    }

    [Fact]
    public async Task WhenTenantCriterionCannotBeRead_DoesNotCallRules()
    {
        SetupAllies(2);
        _tenantRules.Setup(t => t.GetCriterioOrdenListadoAsync(_tenantId, "corr"))
                    .ThrowsAsync(new RulesServiceUnavailableException("sin criterio"));

        await Assert.ThrowsAsync<RulesServiceUnavailableException>(() => Execute());
        _rules.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WithoutRulesConfigured_Fails()
    {
        SetupAllies(1);
        var useCase = new GetEligibleAlliesUseCase(_repo.Object);

        await Assert.ThrowsAsync<RulesServiceUnavailableException>(() =>
            useCase.ExecuteForRequestAsync(_requestId, _tenantId, Guid.NewGuid(), "cliente", null, null, null, "corr"));
    }

    [Fact]
    public async Task WhenRulesLosesOrInventsAnAlly_Fails()
    {
        var allies = SetupAllies(2);
        _rules.Setup(r => r.RankAlliesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Ally>>(), _tenantId, "cliente", "corr"))
              .ReturnsAsync(new[]
              {
                  new RankedAlly(allies[0].Id, 1, null, Array.Empty<string>()),
                  new RankedAlly(Guid.NewGuid(), 2, null, Array.Empty<string>())
              });

        await Assert.ThrowsAsync<RulesServiceUnavailableException>(() => Execute());
    }
}
